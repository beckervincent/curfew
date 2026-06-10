//! System tray icon and context menu.

use std::mem::zeroed;
use std::sync::atomic::{AtomicU32, Ordering};
use windows::{
    core::{w, PCWSTR},
    Win32::{
        Foundation::{HWND, LPARAM, LRESULT, WPARAM},
        System::LibraryLoader::GetModuleHandleW,
        UI::{
            Shell::{
                Shell_NotifyIconW, NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE,
                NOTIFYICONDATAW,
            },
            WindowsAndMessaging::*,
        },
    },
};

use crate::blocking::{extend_time, hide_blocking_overlay, show_blocking_overlay, BLOCKING_HWND};
use crate::constants::*;
use crate::database::{get_blocking_message, is_pause_enabled};
use crate::dialogs::{show_settings_dialog, show_stats_dialog, verify_passcode};
use crate::mini_overlay::{
    can_pause, get_remaining_pause_budget, is_idle_paused, is_paused, toggle_pause,
    PauseBlockedReason,
};
use crate::overlay::{show_overlay, OVERLAY_HWND};
use crate::ui::to_wide;

/// "TaskbarCreated" message ID, used to re-add the icon when Explorer restarts.
static TASKBAR_CREATED_MSG: AtomicU32 = AtomicU32::new(0);

unsafe fn build_notify_icon_data(hwnd: HWND) -> NOTIFYICONDATAW {
    let hinstance = GetModuleHandleW(None).expect("Failed to get module handle");

    // MAKEINTRESOURCE(1): first icon resource in the exe, fall back to the stock icon.
    #[allow(clippy::manual_dangling_ptr)]
    let hicon = LoadIconW(hinstance, PCWSTR(1usize as *const u16))
        .or_else(|_| LoadIconW(None, IDI_APPLICATION))
        .expect("Failed to load icon");

    let mut tip_buffer: [u16; 128] = [0; 128];
    for (i, c) in "Screen Time Manager".encode_utf16().enumerate().take(127) {
        tip_buffer[i] = c;
    }

    let mut nid: NOTIFYICONDATAW = zeroed();
    nid.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
    nid.hWnd = hwnd;
    nid.uID = 1;
    nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
    nid.uCallbackMessage = WM_TRAYICON;
    nid.hIcon = hicon;
    nid.szTip = tip_buffer;
    nid
}

pub unsafe fn add_tray_icon(hwnd: HWND) {
    let msg_id = RegisterWindowMessageW(w!("TaskbarCreated"));
    TASKBAR_CREATED_MSG.store(msg_id, Ordering::SeqCst);

    let nid = build_notify_icon_data(hwnd);

    // The taskbar may not be ready right after login — retry for ~5s.
    for attempt in 1..=10u32 {
        if Shell_NotifyIconW(NIM_ADD, &nid).as_bool() {
            return;
        }
        if attempt == 10 {
            eprintln!("Failed to add tray icon after {} attempts", attempt);
            return;
        }
        std::thread::sleep(std::time::Duration::from_millis(500));
    }
}

pub unsafe fn remove_tray_icon(hwnd: HWND) {
    let nid = build_notify_icon_data(hwnd);
    let _ = Shell_NotifyIconW(NIM_DELETE, &nid);
}

/// Label and enabled state for the pause menu entry.
fn pause_menu_item() -> (String, bool) {
    if is_paused() {
        return ("Resume Timer".into(), true);
    }
    if is_idle_paused() {
        return ("Pause (Idle paused)".into(), false);
    }
    if !is_pause_enabled() {
        return ("Pause (Disabled)".into(), false);
    }

    match can_pause() {
        Ok(()) => {
            let budget_mins = get_remaining_pause_budget() / 60;
            (format!("Pause Timer ({}m left)", budget_mins), true)
        }
        Err(PauseBlockedReason::BudgetExhausted) => ("Pause (Budget used)".into(), false),
        Err(PauseBlockedReason::CooldownActive { seconds_remaining }) => {
            (format!("Pause ({}m cooldown)", (seconds_remaining + 59) / 60), false)
        }
        Err(PauseBlockedReason::MinActiveTimeNotMet { seconds_remaining }) => {
            (format!("Pause (wait {}m)", (seconds_remaining + 59) / 60), false)
        }
        Err(PauseBlockedReason::TimeTooLow) => ("Pause (Time too low)".into(), false),
        Err(PauseBlockedReason::Disabled) => ("Pause (Disabled)".into(), false),
    }
}

pub unsafe fn show_context_menu(hwnd: HWND) {
    let hmenu = match CreatePopupMenu() {
        Ok(m) => m,
        Err(_) => return,
    };

    let (pause_label, pause_enabled) = pause_menu_item();
    let pause_flags = if pause_enabled {
        MF_BYPOSITION | MF_STRING
    } else {
        MF_BYPOSITION | MF_STRING | MF_GRAYED
    };

    // Owned buffers must outlive TrackPopupMenu.
    let pause_wide = to_wide(&pause_label);

    let mut idx = 0u32;
    let mut insert = |flags: MENU_ITEM_FLAGS, id: u16, text: PCWSTR| {
        let _ = InsertMenuW(hmenu, idx, flags, id as usize, text);
        idx += 1;
    };
    let item = MF_BYPOSITION | MF_STRING;
    let sep = MF_BYPOSITION | MF_SEPARATOR;

    insert(item, IDM_TODAYS_STATS, w!("Today's Stats..."));
    insert(item, IDM_SETTINGS, w!("Settings..."));
    insert(sep, 0, PCWSTR::null());
    insert(item, IDM_EXTEND_15, w!("Extend +15 min"));
    insert(item, IDM_EXTEND_45, w!("Extend +45 min"));
    insert(sep, 0, PCWSTR::null());
    insert(pause_flags, IDM_PAUSE_TOGGLE, PCWSTR(pause_wide.as_ptr()));
    if is_idle_paused() {
        insert(item | MF_GRAYED, 0, w!("Idle: Paused"));
    }
    insert(sep, 0, PCWSTR::null());
    insert(item, IDM_SHOW_OVERLAY, w!("Show Warning (5s)"));
    insert(item, IDM_SHOW_BLOCKING, w!("Show Blocking Overlay"));
    insert(sep, 0, PCWSTR::null());
    insert(item, IDM_CHECK_UPDATES, w!("Check for Updates"));
    insert(item, IDM_ABOUT, w!("About"));
    insert(item, IDM_QUIT, w!("Quit"));

    let mut point = zeroed();
    if GetCursorPos(&mut point).is_ok() {
        let _ = SetForegroundWindow(hwnd);
        let _ = TrackPopupMenu(
            hmenu,
            TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN,
            point.x,
            point.y,
            0,
            hwnd,
            None,
        );
    }

    DestroyMenu(hmenu).ok();
}

unsafe fn show_about(hwnd: HWND) {
    let text = to_wide(&format!(
        "Screen Time Manager v{}\n\nA parental control application for managing screen time.",
        env!("CARGO_PKG_VERSION")
    ));
    MessageBoxW(hwnd, PCWSTR(text.as_ptr()), w!("About"), MB_OK | MB_ICONINFORMATION);
}

pub unsafe extern "system" fn window_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_TRAYICON => {
            if matches!(lparam.0 as u32, WM_RBUTTONUP | WM_LBUTTONUP) {
                show_context_menu(hwnd);
            }
            LRESULT(0)
        }
        WM_COMMAND => {
            let menu_id = (wparam.0 & 0xFFFF) as u16;
            match menu_id {
                IDM_PAUSE_TOGGLE => {
                    // No passcode needed: pausing only ever costs the child time.
                    let _ = toggle_pause();
                }
                IDM_SHOW_OVERLAY => {
                    let (_, message) = crate::database::get_warning_config(1);
                    show_overlay(&message, 5);
                }
                IDM_SHOW_BLOCKING => {
                    show_blocking_overlay(&get_blocking_message());
                }
                IDM_TODAYS_STATS => {
                    if verify_passcode(hwnd) {
                        show_stats_dialog(hwnd);
                    }
                }
                IDM_SETTINGS => {
                    if verify_passcode(hwnd) {
                        show_settings_dialog(hwnd);
                    }
                }
                IDM_EXTEND_15 => {
                    if verify_passcode(hwnd) {
                        extend_time(15);
                    }
                }
                IDM_EXTEND_45 => {
                    if verify_passcode(hwnd) {
                        extend_time(45);
                    }
                }
                IDM_CHECK_UPDATES => {
                    // Network check runs off the UI thread; result shows as a message box.
                    std::thread::spawn(|| {
                        let text = match crate::updater::check_for_update() {
                            Ok(Some(tag)) => format!(
                                "Version {} is available.\n\nIt will be installed automatically in the background.",
                                tag.trim_start_matches('v')
                            ),
                            Ok(None) => format!(
                                "Curfew {} is up to date.",
                                crate::updater::CURRENT_VERSION
                            ),
                            Err(()) => "Could not check for updates.\nPlease check your internet connection.".to_string(),
                        };
                        let wide = to_wide(&text);
                        unsafe {
                            MessageBoxW(
                                HWND::default(),
                                PCWSTR(wide.as_ptr()),
                                w!("Curfew Updates"),
                                MB_OK | MB_ICONINFORMATION,
                            );
                        }
                    });
                }
                IDM_ABOUT => show_about(hwnd),
                IDM_QUIT if verify_passcode(hwnd) => {
                    DestroyWindow(hwnd).ok();
                }
                _ => {}
            }
            LRESULT(0)
        }
        WM_DESTROY => {
            let overlay_hwnd = HWND(OVERLAY_HWND.load(Ordering::SeqCst));
            if !overlay_hwnd.0.is_null() {
                DestroyWindow(overlay_hwnd).ok();
            }
            let blocking_hwnd = HWND(BLOCKING_HWND.load(Ordering::SeqCst));
            if !blocking_hwnd.0.is_null() {
                hide_blocking_overlay();
                DestroyWindow(blocking_hwnd).ok();
            }
            remove_tray_icon(hwnd);
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => {
            // Explorer restarted: the taskbar is new, re-add the icon.
            let taskbar_msg = TASKBAR_CREATED_MSG.load(Ordering::SeqCst);
            if taskbar_msg != 0 && msg == taskbar_msg {
                remove_tray_icon(hwnd);
                add_tray_icon(hwnd);
                return LRESULT(0);
            }
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
    }
}
