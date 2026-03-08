//! System tray module for Screen Time Manager
//! Handles the system tray icon and context menu

use std::mem::zeroed;
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

/// Encode a null-terminated string literal as UTF-16. The caller must ensure the
/// input ends with '\0'.
fn encode_static(s: &str) -> Vec<u16> {
    s.encode_utf16().collect()
}

/// Encode a dynamic String as a null-terminated UTF-16 Vec.
fn encode_dynamic(s: String) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}
use crate::constants::*;
use crate::database::{get_blocking_message, get_warning_config, is_pause_enabled};
use crate::dialogs::{show_settings_dialog, show_stats_dialog, verify_passcode_for_quit};
use crate::mini_overlay::{is_paused, is_idle_paused, can_pause, toggle_pause, PauseBlockedReason, get_remaining_pause_budget};
use crate::overlay::{show_overlay, OVERLAY_HWND};
use std::sync::atomic::{AtomicU32, Ordering};

/// Message ID for TaskbarCreated — sent by the shell when the taskbar is (re)created.
/// We use this to re-register the tray icon if Explorer restarts.
static TASKBAR_CREATED_MSG: AtomicU32 = AtomicU32::new(0);

/// Global state for the notification icon data
pub static mut NOTIFY_ICON_DATA: Option<NOTIFYICONDATAW> = None;

/// Add the system tray icon
pub unsafe fn add_tray_icon(hwnd: HWND) {
    // Register the TaskbarCreated message once so we can re-add the icon if Explorer restarts.
    let msg_id = RegisterWindowMessageW(w!("TaskbarCreated"));
    TASKBAR_CREATED_MSG.store(msg_id, Ordering::SeqCst);

    let hinstance = GetModuleHandleW(None).expect("Failed to get module handle");

    // MAKEINTRESOURCE(1) — load the first icon resource embedded in the exe
    #[allow(clippy::manual_dangling_ptr)]
    let hicon = LoadIconW(hinstance, PCWSTR(1usize as *const u16))
        .or_else(|_| LoadIconW(None, IDI_APPLICATION))
        .expect("Failed to load icon");

    let tooltip = "Screen Time Manager";
    let mut tip_buffer: [u16; 128] = [0; 128];
    for (i, c) in tooltip.encode_utf16().enumerate() {
        if i >= 127 { break; }
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

    // Retry NIM_ADD — the taskbar may not be ready immediately at login
    let mut attempts = 0u32;
    loop {
        if Shell_NotifyIconW(NIM_ADD, &nid).as_bool() {
            break;
        }
        attempts += 1;
        if attempts >= 10 {
            // Give up after 10 attempts (~5 s); TaskbarCreated will retry later
            eprintln!("Failed to add tray icon after {} attempts", attempts);
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(500));
    }

    NOTIFY_ICON_DATA = Some(nid);
}

/// Remove the system tray icon
pub unsafe fn remove_tray_icon() {
    if let Some(ref nid) = NOTIFY_ICON_DATA {
        let _ = Shell_NotifyIconW(NIM_DELETE, nid);
        NOTIFY_ICON_DATA = None;
    }
}

/// Show the context menu when right-clicking the tray icon
pub unsafe fn show_context_menu(hwnd: HWND) {
    let hmenu = CreatePopupMenu().expect("Failed to create popup menu");

    // Determine pause menu item text and state
    let paused = is_paused();
    let pause_enabled = is_pause_enabled();

    // Build pause label and flags. Dynamic strings are kept alive in `_dyn_wide`
    // until show_context_menu_with_pause returns (TrackPopupMenu is synchronous).
    let mut _dyn_wide: Option<Vec<u16>> = None;

    let (pause_ptr, pause_flags) = if paused {
        (PCWSTR(encode_static("Resume Timer\0").as_ptr()), MF_BYPOSITION | MF_STRING)
    } else if is_idle_paused() {
        (PCWSTR(encode_static("Pause (Idle paused)\0").as_ptr()), MF_BYPOSITION | MF_STRING | MF_GRAYED)
    } else if !pause_enabled {
        (PCWSTR(encode_static("Pause (Disabled)\0").as_ptr()), MF_BYPOSITION | MF_STRING | MF_GRAYED)
    } else {
        match can_pause() {
            Ok(()) => {
                let budget_mins = get_remaining_pause_budget() / 60;
                let wide = encode_dynamic(format!("Pause Timer ({}m left)", budget_mins));
                let ptr = PCWSTR(wide.as_ptr());
                _dyn_wide = Some(wide);
                (ptr, MF_BYPOSITION | MF_STRING)
            }
            Err(PauseBlockedReason::BudgetExhausted) => {
                (PCWSTR(encode_static("Pause (Budget used)\0").as_ptr()), MF_BYPOSITION | MF_STRING | MF_GRAYED)
            }
            Err(PauseBlockedReason::CooldownActive { seconds_remaining }) => {
                let mins = (seconds_remaining + 59) / 60;
                let wide = encode_dynamic(format!("Pause ({}m cooldown)", mins));
                let ptr = PCWSTR(wide.as_ptr());
                _dyn_wide = Some(wide);
                (ptr, MF_BYPOSITION | MF_STRING | MF_GRAYED)
            }
            Err(PauseBlockedReason::MinActiveTimeNotMet { seconds_remaining }) => {
                let mins = (seconds_remaining + 59) / 60;
                let wide = encode_dynamic(format!("Pause (wait {}m)", mins));
                let ptr = PCWSTR(wide.as_ptr());
                _dyn_wide = Some(wide);
                (ptr, MF_BYPOSITION | MF_STRING | MF_GRAYED)
            }
            Err(PauseBlockedReason::TimeTooLow) => {
                (PCWSTR(encode_static("Pause (Time too low)\0").as_ptr()), MF_BYPOSITION | MF_STRING | MF_GRAYED)
            }
            Err(PauseBlockedReason::Disabled) => {
                (PCWSTR(encode_static("Pause (Disabled)\0").as_ptr()), MF_BYPOSITION | MF_STRING | MF_GRAYED)
            }
        }
    };

    show_context_menu_with_pause(hwnd, hmenu, pause_ptr, pause_flags);
}

/// Helper to show context menu with pause item
unsafe fn show_context_menu_with_pause(hwnd: HWND, hmenu: HMENU, pause_text: PCWSTR, pause_flags: MENU_ITEM_FLAGS) {
    InsertMenuW(hmenu, 0, MF_BYPOSITION | MF_STRING, IDM_TODAYS_STATS as usize, w!("Today's Stats..."))
        .expect("Failed to insert menu item");
    InsertMenuW(hmenu, 1, MF_BYPOSITION | MF_STRING, IDM_SETTINGS as usize, w!("Settings..."))
        .expect("Failed to insert menu item");
    InsertMenuW(hmenu, 2, MF_BYPOSITION | MF_SEPARATOR, 0, PCWSTR::null())
        .expect("Failed to insert separator");
    InsertMenuW(hmenu, 3, MF_BYPOSITION | MF_STRING, IDM_EXTEND_15 as usize, w!("Extend +15 min"))
        .expect("Failed to insert menu item");
    InsertMenuW(hmenu, 4, MF_BYPOSITION | MF_STRING, IDM_EXTEND_45 as usize, w!("Extend +45 min"))
        .expect("Failed to insert menu item");
    InsertMenuW(hmenu, 5, MF_BYPOSITION | MF_SEPARATOR, 0, PCWSTR::null())
        .expect("Failed to insert separator");

    // Pause menu item with dynamic text
    InsertMenuW(hmenu, 6, pause_flags, IDM_PAUSE_TOGGLE as usize, pause_text)
        .expect("Failed to insert pause menu item");

    let mut idx = 7;

    // Show idle status if idle-paused
    if is_idle_paused() {
        InsertMenuW(hmenu, idx, MF_BYPOSITION | MF_STRING | MF_GRAYED, 0, w!("Idle: Paused"))
            .expect("Failed to insert idle status");
        idx += 1;
    }

    InsertMenuW(hmenu, idx, MF_BYPOSITION | MF_SEPARATOR, 0, PCWSTR::null())
        .expect("Failed to insert separator");
    idx += 1;
    InsertMenuW(hmenu, idx, MF_BYPOSITION | MF_STRING, IDM_SHOW_OVERLAY as usize, w!("Show Warning (5s)"))
        .expect("Failed to insert menu item");
    idx += 1;
    InsertMenuW(hmenu, idx, MF_BYPOSITION | MF_STRING, IDM_SHOW_BLOCKING as usize, w!("Show Blocking Overlay"))
        .expect("Failed to insert menu item");
    idx += 1;
    InsertMenuW(hmenu, idx, MF_BYPOSITION | MF_SEPARATOR, 0, PCWSTR::null())
        .expect("Failed to insert separator");
    idx += 1;
    InsertMenuW(hmenu, idx, MF_BYPOSITION | MF_STRING, IDM_ABOUT as usize, w!("About"))
        .expect("Failed to insert menu item");
    idx += 1;
    InsertMenuW(hmenu, idx, MF_BYPOSITION | MF_STRING, IDM_QUIT as usize, w!("Quit"))
        .expect("Failed to insert menu item");

    let mut point = zeroed();
    GetCursorPos(&mut point).expect("Failed to get cursor position");

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

    DestroyMenu(hmenu).ok();
}

/// Main window procedure for handling tray events
pub unsafe extern "system" fn window_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_TRAYICON => {
            let event = lparam.0 as u32;
            match event {
                WM_RBUTTONUP | WM_LBUTTONUP => {
                    show_context_menu(hwnd);
                }
                _ => {}
            }
            LRESULT(0)
        }
        WM_COMMAND => {
            let menu_id = (wparam.0 & 0xFFFF) as u16;
            match menu_id {
                IDM_PAUSE_TOGGLE => {
                    // Toggle pause state (no passcode required - it's a child feature)
                    match toggle_pause() {
                        Ok(_is_now_paused) => {
                            // Success - UI will update automatically
                        }
                        Err(_reason) => {
                            // Should not happen since menu item should be grayed out
                            // But just in case, do nothing
                        }
                    }
                }
                IDM_SHOW_OVERLAY => {
                    let (minutes, message) = get_warning_config(1);
                    show_overlay(&message, minutes);
                }
                IDM_SHOW_BLOCKING => {
                    let message = get_blocking_message();
                    show_blocking_overlay(&message);
                }
                IDM_TODAYS_STATS => {
                    if verify_passcode_for_quit(hwnd) {
                        show_stats_dialog(hwnd);
                    }
                }
                IDM_SETTINGS => {
                    if verify_passcode_for_quit(hwnd) {
                        show_settings_dialog(hwnd);
                    }
                }
                IDM_EXTEND_15 => {
                    if verify_passcode_for_quit(hwnd) {
                        extend_time(15);
                    }
                }
                IDM_EXTEND_45 => {
                    if verify_passcode_for_quit(hwnd) {
                        extend_time(45);
                    }
                }
                IDM_ABOUT => {
                    MessageBoxW(
                        hwnd,
                        w!("Screen Time Manager v0.1.0\n\nA parental control application for managing screen time."),
                        w!("About"),
                        MB_OK | MB_ICONINFORMATION,
                    );
                }
                IDM_QUIT => {
                    if verify_passcode_for_quit(hwnd) {
                        DestroyWindow(hwnd).ok();
                    }
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
            remove_tray_icon();
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => {
            // Re-add the tray icon when Explorer/taskbar restarts
            let taskbar_msg = TASKBAR_CREATED_MSG.load(Ordering::SeqCst);
            if taskbar_msg != 0 && msg == taskbar_msg {
                // Remove stale entry first, then re-add
                if let Some(ref nid) = NOTIFY_ICON_DATA {
                    let _ = Shell_NotifyIconW(NIM_DELETE, nid);
                    NOTIFY_ICON_DATA = None;
                }
                add_tray_icon(hwnd);
                return LRESULT(0);
            }
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
    }
}
