//! Passcode prompt, settings, stats and first-run setup dialogs.

use std::ffi::c_void;
use std::mem::zeroed;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicPtr, Ordering};
use std::sync::Mutex;
use windows::{
    core::{w, PCWSTR},
    Win32::{
        Foundation::{COLORREF, HWND, LPARAM, LRESULT, RECT, WPARAM},
        Graphics::{
            Dwm::{
                DwmSetWindowAttribute, DWMWA_USE_IMMERSIVE_DARK_MODE,
                DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND,
            },
            Gdi::{
                BeginPaint, CreateSolidBrush, DeleteObject, EndPaint, FillRect, InvalidateRect,
                SelectObject, SetBkMode, SetTextColor, DT_CENTER, DT_SINGLELINE, DT_WORDBREAK,
                HDC, HFONT, PAINTSTRUCT, TRANSPARENT,
            },
        },
        System::LibraryLoader::GetModuleHandleW,
        UI::{
            Input::KeyboardAndMouse::{SetFocus, VK_ESCAPE, VK_RETURN},
            WindowsAndMessaging::*,
        },
    },
};

use crate::constants::*;
use crate::database::{get_passcode, get_setting, set_setting, WEEKDAY_KEYS, WEEKDAY_NAMES};
use crate::dpi::scale;
use crate::ui::{self, EditStyle};

// Settings dialog control IDs.
const ID_SETTINGS_BASE: i32 = 2000;
const ID_SETTINGS_SAVE: i32 = 2100;
const ID_SETTINGS_CANCEL: i32 = 2101;
const ID_SETTINGS_UNINSTALL: i32 = 2102;
const ID_CURRENT_PASSCODE: i32 = 2110;
const ID_NEW_PASSCODE: i32 = 2111;
const ID_CONFIRM_PASSCODE: i32 = 2112;

/// Control handles of the settings dialog, stored as raw values so the
/// struct is Send (HWND itself is not).
#[derive(Default)]
struct SettingsHandles {
    daily_limits: [isize; 7],
    warning1_minutes: isize,
    warning1_message: isize,
    warning2_minutes: isize,
    warning2_message: isize,
    blocking_message: isize,
    current_passcode: isize,
    new_passcode: isize,
    confirm_passcode: isize,
    lock_screen_timeout: isize,
    idle_enabled: isize,
    idle_timeout_minutes: isize,
    auto_update: isize,
}

static SETTINGS_HANDLES: Mutex<Option<SettingsHandles>> = Mutex::new(None);
static SETTINGS_DIALOG_OPEN: AtomicBool = AtomicBool::new(false);
static STATS_DIALOG_OPEN: AtomicBool = AtomicBool::new(false);

fn hwnd_of(raw: isize) -> HWND {
    HWND(raw as *mut c_void)
}

// ── Shared dialog plumbing ───────────────────────────────────────────────────

/// Register a dialog class (ignores re-registration), create a centered dark
/// dialog with rounded corners, and pump messages until it is destroyed.
/// `enter_command` optionally maps the Enter key to a WM_COMMAND id.
unsafe fn run_dialog(
    class_name: PCWSTR,
    title: PCWSTR,
    dialog_proc: unsafe extern "system" fn(HWND, u32, WPARAM, LPARAM) -> LRESULT,
    parent: HWND,
    width: i32,
    height: i32,
    enter_command: Option<usize>,
) {
    let hinstance = GetModuleHandleW(None).expect("Failed to get module handle");

    let wnd_class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(dialog_proc),
        hInstance: hinstance.into(),
        lpszClassName: class_name,
        hbrBackground: CreateSolidBrush(COLORREF(DARK_BG)),
        hCursor: LoadCursorW(None, IDC_ARROW).ok().unwrap_or_default(),
        ..zeroed()
    };
    RegisterClassW(&wnd_class);

    let screen_width = GetSystemMetrics(SM_CXSCREEN);
    let screen_height = GetSystemMetrics(SM_CYSCREEN);
    let dialog_width = scale(width);
    let dialog_height = scale(height);

    let dialog_hwnd = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_DLGMODALFRAME,
        class_name,
        title,
        WS_POPUP | WS_CAPTION | WS_SYSMENU,
        (screen_width - dialog_width) / 2,
        (screen_height - dialog_height) / 2,
        dialog_width,
        dialog_height,
        parent,
        HMENU::default(),
        hinstance,
        None,
    );

    if let Ok(dlg) = dialog_hwnd {
        // Native Win11 rounded corners; titlebar follows the app theme.
        let corner = DWMWCP_ROUND;
        let _ = DwmSetWindowAttribute(
            dlg,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            &corner as *const _ as *const c_void,
            4,
        );
        let dark: i32 = crate::theme::current().dark as i32;
        let _ = DwmSetWindowAttribute(
            dlg,
            DWMWA_USE_IMMERSIVE_DARK_MODE,
            &dark as *const i32 as *const c_void,
            4,
        );

        let _ = ShowWindow(dlg, SW_SHOW);
        let _ = SetForegroundWindow(dlg);

        let mut msg: MSG = zeroed();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            if let Some(cmd) = enter_command {
                if msg.message == WM_KEYDOWN && msg.wParam.0 == VK_RETURN.0 as usize {
                    SendMessageW(dlg, WM_COMMAND, WPARAM(cmd), LPARAM(0));
                    continue;
                }
            }
            if !IsDialogMessageW(dlg, &msg).as_bool() {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
    }
}

/// Fill the background and draw centered title, subtitle and optional error line.
unsafe fn paint_dialog_header(hwnd: HWND, title: &str, subtitle: &str, error: Option<&str>) {
    let theme = crate::theme::current();
    let mut ps: PAINTSTRUCT = zeroed();
    let hdc = BeginPaint(hwnd, &mut ps);

    let mut rect: RECT = zeroed();
    GetClientRect(hwnd, &mut rect).ok();

    let bg_brush = CreateSolidBrush(COLORREF(theme.bg));
    FillRect(hdc, &rect, bg_brush);
    let _ = DeleteObject(bg_brush);

    let title_font = ui::font(22, true);
    let old_font = SelectObject(hdc, title_font);
    SetTextColor(hdc, COLORREF(theme.text));
    SetBkMode(hdc, TRANSPARENT);

    let mut title_rect = RECT { left: 0, top: scale(25), right: rect.right, bottom: scale(55) };
    ui::draw_text(hdc, title, &mut title_rect, DT_CENTER | DT_SINGLELINE);

    let sub_font = ui::font(14, false);
    SelectObject(hdc, sub_font);
    SetTextColor(hdc, COLORREF(theme.text_secondary));

    let mut sub_rect = RECT { left: 0, top: scale(55), right: rect.right, bottom: scale(80) };
    ui::draw_text(hdc, subtitle, &mut sub_rect, DT_CENTER | DT_SINGLELINE);

    if let Some(error) = error {
        SetTextColor(hdc, COLORREF(COLOR_ERROR));
        let mut err_rect = RECT { left: 0, top: scale(150), right: rect.right, bottom: scale(170) };
        ui::draw_text(hdc, error, &mut err_rect, DT_CENTER | DT_SINGLELINE);
    }

    SelectObject(hdc, old_font);
    let _ = DeleteObject(title_font);
    let _ = DeleteObject(sub_font);

    let _ = EndPaint(hwnd, &ps);
}

// ── Passcode prompt ──────────────────────────────────────────────────────────

// Prompt state: 0 = open, 1 = accepted, 2 = cancelled.
static PROMPT_RESULT: AtomicI32 = AtomicI32::new(0);
static PROMPT_EDIT: AtomicPtr<c_void> = AtomicPtr::new(std::ptr::null_mut());
static PROMPT_ERROR: AtomicBool = AtomicBool::new(false);

const ID_PROMPT_OK: usize = 1;
const ID_PROMPT_CANCEL: usize = 2;

/// Ask for the passcode before a protected action. Returns true when the
/// correct code was entered (or none is configured).
pub unsafe fn verify_passcode(parent_hwnd: HWND) -> bool {
    if get_passcode().is_none() {
        return true;
    }

    PROMPT_RESULT.store(0, Ordering::SeqCst);
    PROMPT_ERROR.store(false, Ordering::SeqCst);

    unsafe extern "system" fn prompt_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        match msg {
            WM_CREATE => {
                let edit_font = ui::font(28, true);
                let edit = ui::create_edit(
                    hwnd, 101, 100, 95, 150, 45, edit_font, EditStyle::passcode(), "",
                );
                PROMPT_EDIT.store(edit.0, Ordering::SeqCst);
                let _ = SetFocus(edit);

                let btn_font = ui::font(16, false);
                ui::create_button(hwnd, ID_PROMPT_OK as i32, "OK", 70, 200, 100, 40, btn_font);
                ui::create_button(hwnd, ID_PROMPT_CANCEL as i32, "Cancel", 180, 200, 100, 40, btn_font);
                LRESULT(0)
            }
            WM_PAINT => {
                let error = PROMPT_ERROR
                    .load(Ordering::SeqCst)
                    .then_some("Incorrect passcode");
                paint_dialog_header(hwnd, "Enter Passcode", "Enter 4-digit code to continue", error);
                LRESULT(0)
            }
            WM_COMMAND => {
                match wparam.0 & 0xFFFF {
                    ID_PROMPT_OK => {
                        let edit = HWND(PROMPT_EDIT.load(Ordering::SeqCst));
                        let entered = ui::window_text(edit);
                        if get_passcode().map(|stored| stored == entered).unwrap_or(true) {
                            PROMPT_RESULT.store(1, Ordering::SeqCst);
                            DestroyWindow(hwnd).ok();
                        } else {
                            PROMPT_ERROR.store(true, Ordering::SeqCst);
                            let _ = InvalidateRect(hwnd, None, true);
                            SetWindowTextW(edit, w!("")).ok();
                            let _ = SetFocus(edit);
                        }
                    }
                    ID_PROMPT_CANCEL => {
                        PROMPT_RESULT.store(2, Ordering::SeqCst);
                        DestroyWindow(hwnd).ok();
                    }
                    _ => {}
                }
                LRESULT(0)
            }
            WM_KEYDOWN => {
                if wparam.0 == VK_RETURN.0 as usize {
                    SendMessageW(hwnd, WM_COMMAND, WPARAM(ID_PROMPT_OK), LPARAM(0));
                } else if wparam.0 == VK_ESCAPE.0 as usize {
                    PROMPT_RESULT.store(2, Ordering::SeqCst);
                    DestroyWindow(hwnd).ok();
                }
                LRESULT(0)
            }
            WM_CTLCOLOREDIT | WM_CTLCOLORSTATIC => ui::ctl_color(wparam),
            WM_CLOSE => {
                PROMPT_RESULT.store(2, Ordering::SeqCst);
                DestroyWindow(hwnd).ok();
                LRESULT(0)
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        }
    }

    run_dialog(
        w!("ScreenTimePasscodeDialogNice"),
        w!(""),
        prompt_proc,
        parent_hwnd,
        350,
        300,
        Some(ID_PROMPT_OK),
    );

    PROMPT_RESULT.load(Ordering::SeqCst) == 1
}

// ── Settings dialog ──────────────────────────────────────────────────────────

/// Save a numeric edit's value, clamped; invalid input keeps the stored value.
unsafe fn save_clamped(ctl: isize, key: &str, min: u32, max: u32) {
    if ctl == 0 {
        return;
    }
    if let Ok(v) = ui::window_text(hwnd_of(ctl)).trim().parse::<u32>() {
        set_setting(key, &v.clamp(min, max).to_string());
    }
}

/// Save a text edit's value; blank input keeps the stored value.
unsafe fn save_text(ctl: isize, key: &str) {
    if ctl == 0 {
        return;
    }
    let v = ui::window_text(hwnd_of(ctl));
    let trimmed = v.trim();
    if !trimmed.is_empty() {
        set_setting(key, trimmed);
    }
}

/// Validate and persist all settings. Returns false to keep the dialog open.
unsafe fn save_settings(hwnd: HWND) -> bool {
    let guard = SETTINGS_HANDLES.lock().unwrap();
    let Some(h) = guard.as_ref() else {
        return true;
    };

    // Passcode change (only when a new code was typed).
    let new_pass = ui::window_text(hwnd_of(h.new_passcode));
    let confirm_pass = ui::window_text(hwnd_of(h.confirm_passcode));
    if !new_pass.is_empty() || !confirm_pass.is_empty() {
        let current_pass = ui::window_text(hwnd_of(h.current_passcode));
        let stored = get_passcode().unwrap_or_else(|| "0000".to_string());
        if current_pass != stored {
            MessageBoxW(hwnd, w!("Current passcode is incorrect!"), w!("Error"), MB_OK | MB_ICONERROR);
            return false;
        }
        if new_pass.len() != 4 || new_pass.chars().any(|c| !c.is_ascii_digit()) {
            MessageBoxW(hwnd, w!("New passcode must be exactly 4 digits!"), w!("Error"), MB_OK | MB_ICONERROR);
            return false;
        }
        if new_pass != confirm_pass {
            MessageBoxW(hwnd, w!("New passcode and confirmation do not match!"), w!("Error"), MB_OK | MB_ICONERROR);
            return false;
        }
        set_setting("passcode", &new_pass);
    }

    for (i, &ctl) in h.daily_limits.iter().enumerate() {
        save_clamped(ctl, WEEKDAY_KEYS[i], 0, 1440);
    }

    save_clamped(h.warning1_minutes, "warning1_minutes", 0, 600);
    save_text(h.warning1_message, "warning1_message");
    save_clamped(h.warning2_minutes, "warning2_minutes", 0, 600);
    save_text(h.warning2_message, "warning2_message");
    save_text(h.blocking_message, "blocking_message");

    // Lock screen timeout is edited in minutes, stored in seconds.
    if h.lock_screen_timeout != 0 {
        if let Ok(minutes) = ui::window_text(hwnd_of(h.lock_screen_timeout)).trim().parse::<u32>() {
            let seconds = minutes.clamp(1, 720) * 60;
            set_setting("lock_screen_timeout", &seconds.to_string());
        }
    }

    if h.idle_enabled != 0 {
        let checked = SendMessageW(hwnd_of(h.idle_enabled), BM_GETCHECK, WPARAM(0), LPARAM(0));
        set_setting("idle_enabled", if checked.0 == 1 { "1" } else { "0" });
    }
    save_clamped(h.idle_timeout_minutes, "idle_timeout_minutes", 1, 600);

    if h.auto_update != 0 {
        let checked = SendMessageW(hwnd_of(h.auto_update), BM_GETCHECK, WPARAM(0), LPARAM(0));
        set_setting("auto_update_enabled", if checked.0 == 1 { "1" } else { "0" });
    }

    true
}

pub unsafe fn show_settings_dialog(parent_hwnd: HWND) {
    if SETTINGS_DIALOG_OPEN.swap(true, Ordering::SeqCst) {
        return;
    }

    unsafe extern "system" fn settings_dialog_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        match msg {
            WM_CREATE => {
                let label_font = ui::font(16, false);
                let title_font = ui::font(18, true);
                let edit_font = ui::font(16, false);

                let mut h = SettingsHandles::default();
                let mut y = 10;

                // Daily limits, two columns per row.
                ui::create_label(hwnd, "Daily Time Limits (minutes)", 15, y, 350, 20, title_font);
                y += 22;

                for row in 0..4 {
                    for col in 0..2 {
                        let i = row * 2 + col;
                        if i >= 7 {
                            break;
                        }
                        let (label_x, edit_x) = if col == 0 { (25, 120) } else { (210, 305) };
                        ui::create_label(hwnd, &format!("{}:", WEEKDAY_NAMES[i]), label_x, y + 2, 90, 20, label_font);
                        let value = get_setting(WEEKDAY_KEYS[i]).unwrap_or_else(|| "120".to_string());
                        let edit = ui::create_edit(
                            hwnd, ID_SETTINGS_BASE + i as i32, edit_x, y, 60, 22,
                            edit_font, EditStyle::number(4), &value,
                        );
                        h.daily_limits[i] = edit.0 as isize;
                    }
                    y += 24;
                }

                // Warnings.
                for (n, title, min_key, msg_key, min_default, msg_default, ids) in [
                    (1, "First Warning", "warning1_minutes", "warning1_message", "10", "10 minutes remaining!", (20, 21)),
                    (2, "Second Warning", "warning2_minutes", "warning2_message", "5", "5 minutes remaining!", (30, 31)),
                ] {
                    y += if n == 1 { 4 } else { 0 };
                    ui::create_label(hwnd, title, 15, y, 350, 20, title_font);
                    y += 20;

                    ui::create_label(hwnd, "Minutes before:", 25, y + 2, 100, 20, label_font);
                    let minutes_value = get_setting(min_key).unwrap_or_else(|| min_default.to_string());
                    let minutes_edit = ui::create_edit(
                        hwnd, ID_SETTINGS_BASE + ids.0, 130, y, 50, 22,
                        edit_font, EditStyle::number(3), &minutes_value,
                    );
                    y += 24;

                    ui::create_label(hwnd, "Message:", 25, y + 2, 60, 20, label_font);
                    let msg_value = get_setting(msg_key).unwrap_or_else(|| msg_default.to_string());
                    let msg_edit = ui::create_edit(
                        hwnd, ID_SETTINGS_BASE + ids.1, 90, y, 275, 22,
                        edit_font, EditStyle::text(), &msg_value,
                    );
                    y += 24;

                    if n == 1 {
                        h.warning1_minutes = minutes_edit.0 as isize;
                        h.warning1_message = msg_edit.0 as isize;
                    } else {
                        h.warning2_minutes = minutes_edit.0 as isize;
                        h.warning2_message = msg_edit.0 as isize;
                    }
                }

                // Blocking message.
                ui::create_label(hwnd, "Blocking Screen Message", 15, y, 350, 20, title_font);
                y += 20;
                let block_value = get_setting("blocking_message")
                    .unwrap_or_else(|| "Your screen time limit has been reached.".to_string());
                let block_edit = ui::create_edit(
                    hwnd, ID_SETTINGS_BASE + 40, 25, y, 340, 22,
                    edit_font, EditStyle::text(), &block_value,
                );
                h.blocking_message = block_edit.0 as isize;
                y += 24;

                // Passcode change.
                ui::create_label(hwnd, "Change Passcode (leave blank to keep)", 15, y, 360, 20, title_font);
                y += 20;

                ui::create_label(hwnd, "Current:", 25, y + 2, 55, 20, label_font);
                let curr = ui::create_edit(hwnd, ID_CURRENT_PASSCODE, 80, y, 60, 22, edit_font, EditStyle::passcode(), "");
                h.current_passcode = curr.0 as isize;

                ui::create_label(hwnd, "New:", 155, y + 2, 35, 20, label_font);
                let newp = ui::create_edit(hwnd, ID_NEW_PASSCODE, 190, y, 60, 22, edit_font, EditStyle::passcode(), "");
                h.new_passcode = newp.0 as isize;

                ui::create_label(hwnd, "Confirm:", 265, y + 2, 50, 20, label_font);
                let conf = ui::create_edit(hwnd, ID_CONFIRM_PASSCODE, 315, y, 60, 22, edit_font, EditStyle::passcode(), "");
                h.confirm_passcode = conf.0 as isize;
                y += 24;

                // Lock screen.
                ui::create_label(hwnd, "Lock Screen", 15, y, 360, 20, title_font);
                y += 20;
                ui::create_label(hwnd, "Shutdown timeout (min):", 25, y + 2, 150, 20, label_font);
                let timeout_mins = (crate::database::get_lock_screen_timeout() / 60).to_string();
                let lock_edit = ui::create_edit(
                    hwnd, ID_SETTINGS_BASE + 50, 180, y, 60, 22,
                    edit_font, EditStyle::number(4), &timeout_mins,
                );
                h.lock_screen_timeout = lock_edit.0 as isize;
                y += 24;

                // Idle detection.
                ui::create_label(hwnd, "Idle Detection", 15, y, 360, 20, title_font);
                y += 20;
                let idle_chk = ui::create_checkbox(hwnd, "Auto-pause when idle", 25, y, 200, 20, label_font);
                if crate::database::is_idle_enabled() {
                    SendMessageW(idle_chk, BM_SETCHECK, WPARAM(1), LPARAM(0));
                }
                h.idle_enabled = idle_chk.0 as isize;
                y += 22;

                ui::create_label(hwnd, "Idle timeout (min):", 25, y + 2, 150, 20, label_font);
                let idle_value = crate::database::get_idle_timeout_minutes().to_string();
                let idle_edit = ui::create_edit(
                    hwnd, ID_SETTINGS_BASE + 51, 180, y, 50, 22,
                    edit_font, EditStyle::number(3), &idle_value,
                );
                h.idle_timeout_minutes = idle_edit.0 as isize;
                y += 26;

                // Updates.
                ui::create_label(hwnd, "Updates", 15, y, 360, 20, title_font);
                y += 20;
                let upd_chk = ui::create_checkbox(hwnd, "Install updates automatically", 25, y, 250, 20, label_font);
                if crate::database::get_setting("auto_update_enabled").map(|s| s == "1").unwrap_or(true) {
                    SendMessageW(upd_chk, BM_SETCHECK, WPARAM(1), LPARAM(0));
                }
                h.auto_update = upd_chk.0 as isize;
                y += 28;

                // Buttons.
                let btn_font = ui::font(16, false);
                ui::create_button(hwnd, ID_SETTINGS_SAVE, "Save", 100, y, 90, 30, btn_font);
                ui::create_button(hwnd, ID_SETTINGS_CANCEL, "Cancel", 200, y, 90, 30, btn_font);
                ui::create_button(hwnd, ID_SETTINGS_UNINSTALL, "Uninstall Application...", 95, y + 40, 210, 28, btn_font);

                *SETTINGS_HANDLES.lock().unwrap() = Some(h);
                LRESULT(0)
            }
            WM_ERASEBKGND => {
                let hdc = HDC(wparam.0 as _);
                let mut rect: RECT = zeroed();
                GetClientRect(hwnd, &mut rect).ok();
                let brush = CreateSolidBrush(COLORREF(crate::theme::current().bg));
                FillRect(hdc, &rect, brush);
                let _ = DeleteObject(brush);
                LRESULT(1)
            }
            WM_COMMAND => {
                match (wparam.0 & 0xFFFF) as i32 {
                    ID_SETTINGS_SAVE => {
                        if save_settings(hwnd) {
                            MessageBoxW(hwnd, w!("Settings saved successfully!"), w!("Settings"), MB_OK | MB_ICONINFORMATION);
                            DestroyWindow(hwnd).ok();
                        }
                    }
                    ID_SETTINGS_CANCEL => {
                        DestroyWindow(hwnd).ok();
                    }
                    ID_SETTINGS_UNINSTALL => {
                        let result = MessageBoxW(
                            hwnd,
                            w!("This will uninstall Screen Time Manager.\n\nContinue?"),
                            w!("Uninstall"),
                            MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2,
                        );
                        if result == IDYES {
                            let uninstaller = std::env::current_exe()
                                .ok()
                                .and_then(|p| p.parent().map(|d| d.join("unins000.exe")));
                            match uninstaller {
                                Some(path) if path.exists() => {
                                    let _ = std::process::Command::new(&path).spawn();
                                    DestroyWindow(hwnd).ok();
                                }
                                _ => {
                                    MessageBoxW(
                                        hwnd,
                                        w!("Uninstaller not found. Use Add/Remove Programs in Windows Settings."),
                                        w!("Uninstall"),
                                        MB_OK | MB_ICONWARNING,
                                    );
                                }
                            }
                        }
                    }
                    _ => {}
                }
                LRESULT(0)
            }
            WM_CTLCOLOREDIT | WM_CTLCOLORSTATIC => ui::ctl_color(wparam),
            WM_CLOSE => {
                DestroyWindow(hwnd).ok();
                LRESULT(0)
            }
            WM_DESTROY => {
                *SETTINGS_HANDLES.lock().unwrap() = None;
                PostQuitMessage(0);
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        }
    }

    run_dialog(
        w!("ScreenTimeSettingsDialog"),
        w!("Screen Time Settings"),
        settings_dialog_proc,
        parent_hwnd,
        400,
        720,
        None,
    );

    SETTINGS_DIALOG_OPEN.store(false, Ordering::SeqCst);
}

// ── Stats dialog ─────────────────────────────────────────────────────────────

pub unsafe fn show_stats_dialog(parent_hwnd: HWND) {
    if STATS_DIALOG_OPEN.swap(true, Ordering::SeqCst) {
        return;
    }

    unsafe extern "system" fn stats_dialog_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        use crate::blocking::REMAINING_SECONDS;
        use crate::database::{
            get_current_weekday, get_daily_limit, get_pause_config, get_pause_log_today,
            get_pause_used_today, is_pause_enabled, save_remaining_time,
        };
        use crate::mini_overlay::update_mini_overlay;

        const ID_RESET_TIMER: i32 = 3001;
        const ID_CLOSE: i32 = 3002;

        /// Draw one "Label:  value" row, returns the next y position.
        #[allow(clippy::too_many_arguments)]
        unsafe fn stat_row(
            hdc: HDC,
            label_font: HFONT,
            value_font: HFONT,
            label: &str,
            value: &str,
            value_color: u32,
            y: i32,
            right: i32,
        ) -> i32 {
            let left_margin = scale(25);
            let value_x = scale(160);

            SelectObject(hdc, label_font);
            SetTextColor(hdc, COLORREF(crate::theme::current().text_secondary));
            let mut label_rect = RECT { left: left_margin, top: y, right: value_x, bottom: y + scale(22) };
            ui::draw_text(hdc, label, &mut label_rect, DT_SINGLELINE);

            SelectObject(hdc, value_font);
            SetTextColor(hdc, COLORREF(value_color));
            let mut value_rect = RECT { left: value_x, top: y, right: right - scale(15), bottom: y + scale(22) };
            ui::draw_text(hdc, value, &mut value_rect, DT_SINGLELINE);

            y + scale(24)
        }

        match msg {
            WM_CREATE => {
                let btn_font = ui::font(16, false);
                ui::create_button(hwnd, ID_RESET_TIMER, "Reset Timer", 50, 310, 120, 35, btn_font);
                ui::create_button(hwnd, ID_CLOSE, "Close", 190, 310, 100, 35, btn_font);
                LRESULT(0)
            }
            WM_PAINT => {
                let mut ps: PAINTSTRUCT = zeroed();
                let hdc = BeginPaint(hwnd, &mut ps);

                let theme = crate::theme::current();
                let mut rect: RECT = zeroed();
                GetClientRect(hwnd, &mut rect).ok();

                let bg_brush = CreateSolidBrush(COLORREF(theme.bg));
                FillRect(hdc, &rect, bg_brush);
                let _ = DeleteObject(bg_brush);

                let weekday = get_current_weekday();
                let daily_limit_minutes = get_daily_limit(weekday);
                let daily_limit_seconds = (daily_limit_minutes * 60) as i32;
                let remaining_seconds = REMAINING_SECONDS.load(Ordering::SeqCst);
                let used_seconds = if remaining_seconds >= 0 {
                    (daily_limit_seconds - remaining_seconds).max(0)
                } else {
                    0
                };

                let pause_enabled = is_pause_enabled();
                let pause_config = get_pause_config();
                let pause_used_seconds = get_pause_used_today();
                let pause_budget_seconds = (pause_config.daily_budget_minutes * 60) as i32;
                let pause_remaining_seconds = (pause_budget_seconds - pause_used_seconds).max(0);
                let pause_log = get_pause_log_today();

                let title_font = ui::font(22, true);
                let section_font = ui::font(16, true);
                let label_font = ui::font(15, false);
                let value_font = ui::font(16, true);
                let small_font = ui::font(13, false);

                let old_font = SelectObject(hdc, title_font);
                SetTextColor(hdc, COLORREF(theme.text));
                SetBkMode(hdc, TRANSPARENT);

                let mut title_rect = RECT { left: 0, top: scale(15), right: rect.right, bottom: scale(42) };
                ui::draw_text(hdc, "Today's Statistics", &mut title_rect, DT_CENTER | DT_SINGLELINE);

                fn time_color(seconds: i32) -> u32 {
                    if seconds <= 60 {
                        COLOR_ERROR
                    } else if seconds <= 300 {
                        COLOR_ACCENT
                    } else {
                        COLOR_GOOD
                    }
                }

                let weekday_name = WEEKDAY_NAMES.get(weekday as usize).unwrap_or(&"Unknown");

                let mut y = scale(50);
                y = stat_row(hdc, label_font, value_font, "Day:", weekday_name, theme.text, y, rect.right);
                y = stat_row(hdc, label_font, value_font, "Daily Limit:", &format!("{} min", daily_limit_minutes), theme.text, y, rect.right);
                y = stat_row(hdc, label_font, value_font, "Time Used:", &ui::format_duration(used_seconds), theme.text, y, rect.right);
                y = stat_row(hdc, label_font, value_font, "Time Remaining:", &ui::format_duration(remaining_seconds), time_color(remaining_seconds), y, rect.right);
                y += scale(8);

                // Pause section.
                SelectObject(hdc, section_font);
                SetTextColor(hdc, COLORREF(theme.text));
                let mut section_rect = RECT { left: scale(25), top: y, right: rect.right - scale(15), bottom: y + scale(20) };
                ui::draw_text(hdc, "Pause Mode", &mut section_rect, DT_SINGLELINE);
                y += scale(22);

                if pause_enabled {
                    let pause_used_str = format!(
                        "{} / {} min",
                        pause_used_seconds / 60,
                        pause_config.daily_budget_minutes
                    );
                    y = stat_row(hdc, label_font, value_font, "Pause Used:", &pause_used_str, theme.text, y, rect.right);
                    y = stat_row(
                        hdc, label_font, value_font, "Pause Remaining:",
                        &ui::format_duration(pause_remaining_seconds),
                        time_color(pause_remaining_seconds), y, rect.right,
                    );
                    y = stat_row(hdc, label_font, value_font, "Pauses Today:", &pause_log.len().to_string(), theme.text, y, rect.right);

                    if !pause_log.is_empty() {
                        SelectObject(hdc, small_font);
                        SetTextColor(hdc, COLORREF(theme.text_muted));
                        let log_str = format!("Log: {}", pause_log.join(", "));
                        let mut log_rect = RECT { left: scale(25), top: y, right: rect.right - scale(15), bottom: y + scale(18) };
                        ui::draw_text(hdc, &log_str, &mut log_rect, DT_SINGLELINE);
                    }
                } else {
                    SelectObject(hdc, label_font);
                    SetTextColor(hdc, COLORREF(theme.text_muted));
                    let mut disabled_rect = RECT { left: scale(25), top: y, right: rect.right - scale(15), bottom: y + scale(22) };
                    ui::draw_text(hdc, "Pause feature is disabled", &mut disabled_rect, DT_SINGLELINE);
                }

                SelectObject(hdc, old_font);
                let _ = DeleteObject(title_font);
                let _ = DeleteObject(section_font);
                let _ = DeleteObject(label_font);
                let _ = DeleteObject(value_font);
                let _ = DeleteObject(small_font);

                let _ = EndPaint(hwnd, &ps);
                LRESULT(0)
            }
            WM_COMMAND => {
                let id = (wparam.0 & 0xFFFF) as i32;
                if id == ID_RESET_TIMER {
                    let daily_limit_seconds =
                        (get_daily_limit(get_current_weekday()) * 60) as i32;
                    REMAINING_SECONDS.store(daily_limit_seconds, Ordering::SeqCst);
                    save_remaining_time(daily_limit_seconds);
                    update_mini_overlay();

                    MessageBoxW(hwnd, w!("Timer has been reset to the daily limit."), w!("Timer Reset"), MB_OK | MB_ICONINFORMATION);
                    let _ = InvalidateRect(hwnd, None, true);
                } else if id == ID_CLOSE {
                    DestroyWindow(hwnd).ok();
                }
                LRESULT(0)
            }
            WM_CTLCOLOREDIT | WM_CTLCOLORSTATIC => ui::ctl_color(wparam),
            WM_CLOSE => {
                DestroyWindow(hwnd).ok();
                LRESULT(0)
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        }
    }

    run_dialog(
        w!("ScreenTimeStatsDialog"),
        w!("Today's Stats"),
        stats_dialog_proc,
        parent_hwnd,
        340,
        390,
        None,
    );

    STATS_DIALOG_OPEN.store(false, Ordering::SeqCst);
}

// ── First-run setup wizard ───────────────────────────────────────────────────

static SETUP_PIN_EDIT: AtomicPtr<c_void> = AtomicPtr::new(std::ptr::null_mut());
static SETUP_CONFIRM_EDIT: AtomicPtr<c_void> = AtomicPtr::new(std::ptr::null_mut());
static SETUP_ERROR: AtomicBool = AtomicBool::new(false);
static SETUP_COMPLETE: AtomicBool = AtomicBool::new(false);

const ID_SETUP_CONTINUE: usize = 1;
const ID_SETUP_PIN: i32 = 2;
const ID_SETUP_CONFIRM: i32 = 3;

/// First-run wizard launched by the installer (`--setup`): create the PIN,
/// then open the settings dialog.
pub unsafe fn show_setup_wizard() {
    SETUP_PIN_EDIT.store(std::ptr::null_mut(), Ordering::SeqCst);
    SETUP_CONFIRM_EDIT.store(std::ptr::null_mut(), Ordering::SeqCst);
    SETUP_ERROR.store(false, Ordering::SeqCst);
    SETUP_COMPLETE.store(false, Ordering::SeqCst);

    unsafe extern "system" fn setup_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        match msg {
            WM_CREATE => {
                let edit_font = ui::font(28, true);
                let btn_font = ui::font(16, false);

                let pin = ui::create_edit(hwnd, ID_SETUP_PIN, 100, 120, 200, 45, edit_font, EditStyle::passcode(), "");
                SETUP_PIN_EDIT.store(pin.0, Ordering::SeqCst);
                let _ = SetFocus(pin);

                let confirm = ui::create_edit(hwnd, ID_SETUP_CONFIRM, 100, 220, 200, 45, edit_font, EditStyle::passcode(), "");
                SETUP_CONFIRM_EDIT.store(confirm.0, Ordering::SeqCst);

                ui::create_button(hwnd, ID_SETUP_CONTINUE as i32, "Continue", 125, 300, 150, 36, btn_font);
                LRESULT(0)
            }
            WM_PAINT => {
                let mut ps: PAINTSTRUCT = zeroed();
                let hdc = BeginPaint(hwnd, &mut ps);
                let mut rect: RECT = zeroed();
                GetClientRect(hwnd, &mut rect).ok();

                let theme = crate::theme::current();
                let bg = CreateSolidBrush(COLORREF(theme.bg));
                FillRect(hdc, &rect, bg);
                let _ = DeleteObject(bg);

                SetBkMode(hdc, TRANSPARENT);

                let title_font = ui::font(22, true);
                let label_font = ui::font(14, false);
                let old_font = SelectObject(hdc, title_font);

                SetTextColor(hdc, COLORREF(theme.text));
                let mut r = RECT { left: 0, top: scale(20), right: rect.right, bottom: scale(55) };
                ui::draw_text(hdc, "Set Up Curfew", &mut r, DT_CENTER | DT_SINGLELINE);

                SelectObject(hdc, label_font);
                SetTextColor(hdc, COLORREF(theme.text_secondary));
                let mut r2 = RECT { left: scale(20), top: scale(58), right: rect.right - scale(20), bottom: scale(95) };
                ui::draw_text(
                    hdc,
                    "Create a 4-digit administrator PIN to protect your settings and time limits.",
                    &mut r2,
                    DT_CENTER | DT_WORDBREAK,
                );

                SetTextColor(hdc, COLORREF(theme.text));
                let mut r3 = RECT { left: 0, top: scale(98), right: rect.right, bottom: scale(116) };
                ui::draw_text(hdc, "Enter PIN:", &mut r3, DT_CENTER | DT_SINGLELINE);

                let mut r4 = RECT { left: 0, top: scale(198), right: rect.right, bottom: scale(216) };
                ui::draw_text(hdc, "Confirm PIN:", &mut r4, DT_CENTER | DT_SINGLELINE);

                if SETUP_ERROR.load(Ordering::SeqCst) {
                    let err_font = ui::font(14, true);
                    SelectObject(hdc, err_font);
                    SetTextColor(hdc, COLORREF(COLOR_ERROR));
                    let mut r5 = RECT { left: 0, top: scale(270), right: rect.right, bottom: scale(292) };
                    ui::draw_text(hdc, "PINs must be 4 digits and match.", &mut r5, DT_CENTER | DT_SINGLELINE);
                    let _ = DeleteObject(err_font);
                }

                SelectObject(hdc, old_font);
                let _ = DeleteObject(title_font);
                let _ = DeleteObject(label_font);
                let _ = EndPaint(hwnd, &ps);
                LRESULT(0)
            }
            WM_COMMAND => {
                if wparam.0 & 0xFFFF == ID_SETUP_CONTINUE {
                    let read_pin = |ptr: &AtomicPtr<c_void>| {
                        let h = HWND(ptr.load(Ordering::SeqCst));
                        if h.0.is_null() {
                            return None;
                        }
                        let text = ui::window_text(h);
                        (text.len() == 4).then_some(text)
                    };

                    match (read_pin(&SETUP_PIN_EDIT), read_pin(&SETUP_CONFIRM_EDIT)) {
                        (Some(p), Some(c)) if p == c => {
                            crate::database::set_setting("passcode", &p);
                            SETUP_COMPLETE.store(true, Ordering::SeqCst);
                            DestroyWindow(hwnd).ok();
                        }
                        _ => {
                            SETUP_ERROR.store(true, Ordering::SeqCst);
                            let _ = InvalidateRect(hwnd, None, true);
                            let pin = HWND(SETUP_PIN_EDIT.load(Ordering::SeqCst));
                            let confirm = HWND(SETUP_CONFIRM_EDIT.load(Ordering::SeqCst));
                            if !pin.0.is_null() {
                                SetWindowTextW(pin, w!("")).ok();
                            }
                            if !confirm.0.is_null() {
                                SetWindowTextW(confirm, w!("")).ok();
                            }
                            if !pin.0.is_null() {
                                let _ = SetFocus(pin);
                            }
                        }
                    }
                }
                LRESULT(0)
            }
            WM_KEYDOWN => {
                if wparam.0 == VK_RETURN.0 as usize {
                    SendMessageW(hwnd, WM_COMMAND, WPARAM(ID_SETUP_CONTINUE), LPARAM(0));
                }
                LRESULT(0)
            }
            WM_CTLCOLOREDIT | WM_CTLCOLORSTATIC => ui::ctl_color(wparam),
            WM_CLOSE => {
                DestroyWindow(hwnd).ok();
                LRESULT(0)
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wparam, lparam),
        }
    }

    run_dialog(
        w!("ScreenTimeSetupWizard"),
        w!("Screen Time Manager Setup"),
        setup_proc,
        HWND::default(),
        400,
        360,
        Some(ID_SETUP_CONTINUE),
    );

    if SETUP_COMPLETE.load(Ordering::SeqCst) {
        show_settings_dialog(HWND::default());
    }
}
