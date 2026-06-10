//! Full-screen blocking overlay. Requires the passcode to dismiss and
//! shuts the machine down when the lock-screen countdown expires.

use std::ffi::c_void;
use std::mem::zeroed;
use std::ptr::null_mut;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicIsize, AtomicPtr, Ordering};
use std::sync::Mutex;
use windows::{
    core::{w, PCWSTR},
    Win32::{
        Foundation::{BOOL, COLORREF, HMODULE, HWND, LPARAM, LRESULT, RECT, WPARAM, CloseHandle},
        Graphics::Gdi::{
            BeginPaint, CreatePen, CreateSolidBrush, DeleteObject, EndPaint, EnumDisplayMonitors,
            FillRect, InvalidateRect, LineTo, MoveToEx, RoundRect, SelectObject, SetBkMode,
            SetTextColor, DT_CENTER, DT_SINGLELINE, DT_VCENTER, DT_WORDBREAK, HDC, HFONT,
            HMONITOR, PAINTSTRUCT, PS_SOLID, TRANSPARENT,
        },
        Media::Audio::{PlaySoundW, SND_ALIAS, SND_ASYNC},
        System::LibraryLoader::GetModuleHandleW,
        System::Shutdown::{ExitWindowsEx, EWX_SHUTDOWN, SHUTDOWN_REASON},
        System::Threading::{GetCurrentProcess, OpenProcessToken},
        Security::{
            AdjustTokenPrivileges, LookupPrivilegeValueW, SE_PRIVILEGE_ENABLED,
            TOKEN_ADJUST_PRIVILEGES, TOKEN_PRIVILEGES, TOKEN_QUERY,
        },
        UI::{
            Controls::*,
            Input::KeyboardAndMouse::{
                GetAsyncKeyState, SetFocus, VIRTUAL_KEY, VK_D, VK_ESCAPE, VK_F4, VK_LWIN, VK_M,
                VK_MENU, VK_RETURN, VK_RWIN, VK_TAB,
            },
            WindowsAndMessaging::*,
        },
    },
};

use crate::constants::*;
use crate::database::get_passcode;
use crate::dpi::scale;
use crate::ui;

// ── Global state ─────────────────────────────────────────────────────────────

pub static BLOCKING_HWND: AtomicPtr<c_void> = AtomicPtr::new(null_mut());
pub static BLOCKING_TEXT: Mutex<Option<String>> = Mutex::new(None);
pub static BLOCKING_EDIT_HWND: AtomicPtr<c_void> = AtomicPtr::new(null_mut());
pub static PASSCODE_ERROR: AtomicBool = AtomicBool::new(false);

/// Remaining screen time in seconds (negative = uninitialized).
pub static REMAINING_SECONDS: AtomicI32 = AtomicI32::new(-1);

/// Seconds until automatic shutdown while the lock screen is up (negative = inactive).
pub static SHUTDOWN_COUNTDOWN_SECONDS: AtomicI32 = AtomicI32::new(-1);

/// Low-level keyboard hook handle (null = not installed).
static KEYBOARD_HOOK: AtomicPtr<c_void> = AtomicPtr::new(null_mut());

/// Secondary monitor overlay handles (raw HWND values).
static SECONDARY_OVERLAY_HWNDS: Mutex<Vec<isize>> = Mutex::new(Vec::new());

/// Original window proc of the passcode edit control.
static EDIT_PREV_PROC: AtomicIsize = AtomicIsize::new(0);

pub const TIMER_REASSERT_TOPMOST: usize = 2;
pub const TIMER_COUNTDOWN: usize = 3;

const ID_PASSCODE_EDIT: i32 = 101;
const ID_UNLOCK_BUTTON: i32 = 102;
const ID_EXTEND_15: i32 = 103;
const ID_EXTEND_30: i32 = 104;
const ID_EXTEND_60: i32 = 105;
const ID_SHUTDOWN_BUTTON: i32 = 106;

struct MonitorInfo {
    rect: RECT,
    is_primary: bool,
}

// ── Shutdown ─────────────────────────────────────────────────────────────────

/// Acquire SeShutdownPrivilege and shut down Windows.
unsafe fn initiate_shutdown() -> bool {
    let mut token_handle = zeroed();
    if OpenProcessToken(
        GetCurrentProcess(),
        TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
        &mut token_handle,
    )
    .is_err()
    {
        return false;
    }

    let mut luid = zeroed();
    if LookupPrivilegeValueW(None, w!("SeShutdownPrivilege"), &mut luid).is_err() {
        let _ = CloseHandle(token_handle);
        return false;
    }

    let tp = TOKEN_PRIVILEGES {
        PrivilegeCount: 1,
        Privileges: [windows::Win32::Security::LUID_AND_ATTRIBUTES {
            Luid: luid,
            Attributes: SE_PRIVILEGE_ENABLED,
        }],
    };

    let adjusted = AdjustTokenPrivileges(token_handle, false, Some(&tp), 0, None, None).is_ok();
    let _ = CloseHandle(token_handle);

    adjusted && ExitWindowsEx(EWX_SHUTDOWN, SHUTDOWN_REASON(0)).is_ok()
}

// ── Keyboard hook ────────────────────────────────────────────────────────────

/// Suppress shortcuts that could escape the lock screen.
unsafe extern "system" fn low_level_keyboard_proc(
    code: i32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if code >= 0 {
        let kbs = &*(lparam.0 as *const KBDLLHOOKSTRUCT);
        let alt_down = (GetAsyncKeyState(VK_MENU.0 as i32) as u16) & 0x8000 != 0;
        let win_down = ((GetAsyncKeyState(VK_LWIN.0 as i32) as u16)
            | (GetAsyncKeyState(VK_RWIN.0 as i32) as u16))
            & 0x8000
            != 0;
        let block = match VIRTUAL_KEY(kbs.vkCode as u16) {
            VK_F4 if alt_down => true,
            VK_TAB if alt_down => true,
            VK_ESCAPE => true,
            VK_LWIN | VK_RWIN => true,
            VK_D if win_down => true,
            VK_M if win_down => true,
            _ => false,
        };
        if block {
            return LRESULT(1);
        }
    }
    CallNextHookEx(None, code, wparam, lparam)
}

// ── Show / hide ──────────────────────────────────────────────────────────────

pub unsafe fn create_blocking_overlay(hinstance: HMODULE) {
    let hwnd = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
        w!("ScreenTimeBlockingClass"),
        w!("Screen Time - Time's Up!"),
        WS_POPUP,
        0,
        0,
        GetSystemMetrics(SM_CXSCREEN),
        GetSystemMetrics(SM_CYSCREEN),
        None,
        None,
        hinstance,
        None,
    )
    .expect("Failed to create blocking overlay");

    BLOCKING_HWND.store(hwnd.0, Ordering::SeqCst);
}

pub unsafe fn show_blocking_overlay(text: &str) {
    let hwnd = HWND(BLOCKING_HWND.load(Ordering::SeqCst));
    if hwnd.0.is_null() {
        return;
    }

    crate::mini_overlay::hide_mini_overlay();

    *BLOCKING_TEXT.lock().unwrap() = Some(text.to_string());
    PASSCODE_ERROR.store(false, Ordering::SeqCst);

    SHUTDOWN_COUNTDOWN_SECONDS.store(crate::database::get_lock_screen_timeout(), Ordering::SeqCst);

    let edit_ptr = BLOCKING_EDIT_HWND.load(Ordering::SeqCst);
    if !edit_ptr.is_null() {
        SetWindowTextW(HWND(edit_ptr), w!("")).ok();
    }

    let _ = InvalidateRect(hwnd, None, false);

    SetWindowPos(
        hwnd,
        HWND_TOPMOST,
        0, 0, 0, 0,
        SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE,
    ).ok();

    let _ = ShowWindow(hwnd, SW_SHOW);
    let _ = SetForegroundWindow(hwnd);

    if !edit_ptr.is_null() {
        let _ = SetFocus(HWND(edit_ptr));
    }

    let _ = PlaySoundW(w!("SystemHand"), None, SND_ALIAS | SND_ASYNC);
    let _ = SetTimer(hwnd, TIMER_REASSERT_TOPMOST, 500, None);
    let _ = SetTimer(hwnd, TIMER_COUNTDOWN, 1000, None);

    if KEYBOARD_HOOK.load(Ordering::SeqCst).is_null() {
        if let Ok(hook) = SetWindowsHookExW(WH_KEYBOARD_LL, Some(low_level_keyboard_proc), None, 0)
        {
            KEYBOARD_HOOK.store(hook.0, Ordering::SeqCst);
        }
    }

    show_secondary_overlays();
}

pub unsafe fn hide_blocking_overlay() {
    let hwnd = HWND(BLOCKING_HWND.load(Ordering::SeqCst));
    if hwnd.0.is_null() {
        return;
    }

    let _ = KillTimer(hwnd, TIMER_REASSERT_TOPMOST);
    let _ = KillTimer(hwnd, TIMER_COUNTDOWN);
    let _ = ShowWindow(hwnd, SW_HIDE);

    let hook_ptr = KEYBOARD_HOOK.swap(null_mut(), Ordering::SeqCst);
    if !hook_ptr.is_null() {
        let _ = UnhookWindowsHookEx(HHOOK(hook_ptr));
    }

    *BLOCKING_TEXT.lock().unwrap() = None;
    SHUTDOWN_COUNTDOWN_SECONDS.store(-1, Ordering::SeqCst);

    hide_secondary_overlays();

    let remaining = REMAINING_SECONDS.load(Ordering::SeqCst);
    crate::database::save_remaining_time(remaining);

    if remaining > 0 {
        crate::mini_overlay::show_mini_overlay();
    }
}

/// Add minutes to the remaining time.
pub fn extend_time(minutes: i32) {
    let additional = minutes * 60;
    let current = REMAINING_SECONDS.load(Ordering::SeqCst);
    let new_time = if current < 0 { additional } else { current + additional };
    REMAINING_SECONDS.store(new_time, Ordering::SeqCst);
    crate::database::save_remaining_time(new_time);
}

// ── Passcode handling ────────────────────────────────────────────────────────

unsafe fn entered_passcode_matches() -> bool {
    let edit_ptr = BLOCKING_EDIT_HWND.load(Ordering::SeqCst);
    if edit_ptr.is_null() {
        return false;
    }
    let entered = ui::window_text(HWND(edit_ptr));
    matches!(get_passcode(), Some(stored) if entered == stored)
}

/// Show the error state and reset the passcode field.
unsafe fn reject_passcode(hwnd: HWND) {
    PASSCODE_ERROR.store(true, Ordering::SeqCst);
    let _ = InvalidateRect(hwnd, None, false);

    let edit_ptr = BLOCKING_EDIT_HWND.load(Ordering::SeqCst);
    if !edit_ptr.is_null() {
        let edit = HWND(edit_ptr);
        SetWindowTextW(edit, w!("")).ok();
        let _ = SetFocus(edit);
    }
    let _ = PlaySoundW(w!("SystemExclamation"), None, SND_ALIAS | SND_ASYNC);
}

/// Subclass proc for the passcode edit: Enter submits instead of beeping.
unsafe extern "system" fn passcode_edit_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_KEYDOWN if wparam.0 == VK_RETURN.0 as usize => {
            if let Ok(parent) = GetParent(hwnd) {
                SendMessageW(
                    parent,
                    WM_COMMAND,
                    WPARAM(((BN_CLICKED as usize) << 16) | ID_UNLOCK_BUTTON as usize),
                    LPARAM(0),
                );
            }
            LRESULT(0)
        }
        WM_CHAR if wparam.0 == '\r' as usize => LRESULT(0),
        _ => {
            let prev: WNDPROC = std::mem::transmute(EDIT_PREV_PROC.load(Ordering::SeqCst));
            CallWindowProcW(prev, hwnd, msg, wparam, lparam)
        }
    }
}

// ── Window proc ──────────────────────────────────────────────────────────────

#[allow(clippy::too_many_arguments)]
unsafe fn add_button(
    parent: HWND,
    hinstance: HMODULE,
    text: PCWSTR,
    id: i32,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    font: HFONT,
) {
    if let Ok(b) = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        w!("BUTTON"),
        text,
        WS_CHILD | WS_VISIBLE | WINDOW_STYLE(BS_PUSHBUTTON as u32),
        x,
        y,
        width,
        height,
        parent,
        HMENU(id as _),
        hinstance,
        None,
    ) {
        ui::set_font(b, font);
    }
}

pub unsafe extern "system" fn blocking_overlay_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_CREATE => {
            let hinstance = GetModuleHandleW(None).unwrap();
            let screen_width = GetSystemMetrics(SM_CXSCREEN);
            let screen_height = GetSystemMetrics(SM_CYSCREEN);

            let panel_height = scale(580);
            let panel_y = (screen_height - panel_height) / 2;

            let btn_font = ui::font(18, true);

            // Row of extend buttons.
            let extend_btn_width = scale(100);
            let extend_btn_height = scale(40);
            let extend_y = panel_y + scale(200);
            let extend_spacing = scale(20);
            let total_extend_width = extend_btn_width * 3 + extend_spacing * 2;
            let extend_start_x = (screen_width - total_extend_width) / 2;

            for (i, (text, id)) in [
                (w!("+15 min"), ID_EXTEND_15),
                (w!("+30 min"), ID_EXTEND_30),
                (w!("+60 min"), ID_EXTEND_60),
            ]
            .into_iter()
            .enumerate()
            {
                add_button(
                    hwnd,
                    hinstance,
                    text,
                    id,
                    extend_start_x + (extend_btn_width + extend_spacing) * i as i32,
                    extend_y,
                    extend_btn_width,
                    extend_btn_height,
                    btn_font,
                );
            }

            // Passcode field.
            let edit_width = scale(200);
            let edit_height = scale(50);
            let edit_x = (screen_width - edit_width) / 2;
            let edit_y = panel_y + scale(310);

            if let Ok(e) = CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("EDIT"),
                w!(""),
                WS_CHILD | WS_VISIBLE | WS_BORDER
                    | WINDOW_STYLE(ES_CENTER as u32 | ES_PASSWORD as u32 | ES_NUMBER as u32),
                edit_x,
                edit_y,
                edit_width,
                edit_height,
                hwnd,
                HMENU(ID_PASSCODE_EDIT as _),
                hinstance,
                None,
            ) {
                BLOCKING_EDIT_HWND.store(e.0, Ordering::SeqCst);
                SendMessageW(e, EM_SETLIMITTEXT, WPARAM(4), LPARAM(0));
                ui::set_font(e, ui::font(32, true));

                // Subclass so Enter submits the passcode.
                let prev = SetWindowLongPtrW(
                    e,
                    GWLP_WNDPROC,
                    passcode_edit_proc as *const () as isize,
                );
                EDIT_PREV_PROC.store(prev, Ordering::SeqCst);
            }

            // Unlock and shutdown buttons.
            let btn_width = scale(200);
            let btn_height = scale(45);
            let btn_x = (screen_width - btn_width) / 2;
            let btn_y = edit_y + edit_height + scale(15);

            add_button(
                hwnd, hinstance, w!("Unlock"), ID_UNLOCK_BUTTON,
                btn_x, btn_y, btn_width, btn_height, btn_font,
            );
            add_button(
                hwnd, hinstance, w!("Shut Down Computer"), ID_SHUTDOWN_BUTTON,
                btn_x, btn_y + btn_height + scale(15), btn_width, btn_height, btn_font,
            );

            LRESULT(0)
        }
        WM_PAINT => {
            let mut ps: PAINTSTRUCT = zeroed();
            let hdc = BeginPaint(hwnd, &mut ps);

            let mut rect: RECT = zeroed();
            GetClientRect(hwnd, &mut rect).ok();

            let bg_brush = CreateSolidBrush(COLORREF(COLOR_OVERLAY_BG));
            FillRect(hdc, &rect, bg_brush);
            let _ = DeleteObject(bg_brush);

            let screen_width = rect.right;
            let screen_height = rect.bottom;

            // Center panel.
            let panel_width = scale(500);
            let panel_height = scale(580);
            let panel_x = (screen_width - panel_width) / 2;
            let panel_y = (screen_height - panel_height) / 2;

            let panel_brush = CreateSolidBrush(COLORREF(COLOR_PANEL_BG));
            let old_brush = SelectObject(hdc, panel_brush);
            let pen = CreatePen(PS_SOLID, scale(2), COLORREF(COLOR_ACCENT));
            let old_pen = SelectObject(hdc, pen);

            let _ = RoundRect(
                hdc,
                panel_x,
                panel_y,
                panel_x + panel_width,
                panel_y + panel_height,
                scale(20),
                scale(20),
            );

            SelectObject(hdc, old_brush);
            SelectObject(hdc, old_pen);
            let _ = DeleteObject(panel_brush);
            let _ = DeleteObject(pen);

            // Title.
            let title_font = ui::font(42, true);
            let old_font = SelectObject(hdc, title_font);
            SetTextColor(hdc, COLORREF(COLOR_TEXT_WHITE));
            SetBkMode(hdc, TRANSPARENT);

            let mut title_rect = RECT {
                left: panel_x,
                top: panel_y + scale(25),
                right: panel_x + panel_width,
                bottom: panel_y + scale(75),
            };
            ui::draw_text(hdc, "Time's Up!", &mut title_rect, DT_CENTER | DT_SINGLELINE);

            // Shutdown countdown, red when imminent.
            let shutdown_countdown = SHUTDOWN_COUNTDOWN_SECONDS.load(Ordering::SeqCst);
            let time_font = ui::font(36, true);
            SelectObject(hdc, time_font);

            let time_str = if shutdown_countdown >= 0 {
                if shutdown_countdown <= 60 {
                    SetTextColor(hdc, COLORREF(COLOR_SHUTDOWN_WARN));
                    format!("SHUTDOWN IN: {}s", shutdown_countdown)
                } else {
                    SetTextColor(hdc, COLORREF(COLOR_ACCENT));
                    format!("Shutdown in: {}", ui::format_duration(shutdown_countdown))
                }
            } else {
                SetTextColor(hdc, COLORREF(COLOR_ACCENT));
                String::from("Time limit exceeded")
            };
            let mut time_rect = RECT {
                left: panel_x,
                top: panel_y + scale(80),
                right: panel_x + panel_width,
                bottom: panel_y + scale(120),
            };
            ui::draw_text(hdc, &time_str, &mut time_rect, DT_CENTER | DT_SINGLELINE);

            // Configurable message.
            let msg_font = ui::font(20, false);
            SelectObject(hdc, msg_font);
            SetTextColor(hdc, COLORREF(COLOR_TEXT_LIGHT));

            let message = BLOCKING_TEXT
                .lock()
                .unwrap()
                .clone()
                .unwrap_or_else(|| "Screen time limit reached".to_string());
            let mut msg_rect = RECT {
                left: panel_x + scale(30),
                top: panel_y + scale(125),
                right: panel_x + panel_width - scale(30),
                bottom: panel_y + scale(160),
            };
            ui::draw_text(hdc, &message, &mut msg_rect, DT_CENTER | DT_WORDBREAK);

            // Section labels.
            let label_font = ui::font(16, false);
            SelectObject(hdc, label_font);
            SetTextColor(hdc, COLORREF(COLOR_TEXT_LIGHT));

            let mut extend_label_rect = RECT {
                left: panel_x,
                top: panel_y + scale(170),
                right: panel_x + panel_width,
                bottom: panel_y + scale(190),
            };
            ui::draw_text(
                hdc,
                "Extend time (requires passcode):",
                &mut extend_label_rect,
                DT_CENTER | DT_SINGLELINE,
            );

            // Separator between the extend and passcode sections.
            let sep_pen = CreatePen(PS_SOLID, 1, COLORREF(0x00555555));
            let old_sep_pen = SelectObject(hdc, sep_pen);
            let sep_y = panel_y + scale(250);
            let _ = MoveToEx(hdc, panel_x + scale(20), sep_y, None);
            let _ = LineTo(hdc, panel_x + panel_width - scale(20), sep_y);
            SelectObject(hdc, old_sep_pen);
            let _ = DeleteObject(sep_pen);

            let mut passcode_label_rect = RECT {
                left: panel_x,
                top: panel_y + scale(260),
                right: panel_x + panel_width,
                bottom: panel_y + scale(285),
            };
            ui::draw_text(
                hdc,
                "Enter passcode to unlock:",
                &mut passcode_label_rect,
                DT_CENTER | DT_SINGLELINE,
            );

            if PASSCODE_ERROR.load(Ordering::SeqCst) {
                SetTextColor(hdc, COLORREF(COLOR_ERROR));
                let mut error_rect = RECT {
                    left: panel_x,
                    top: panel_y + panel_height - scale(45),
                    right: panel_x + panel_width,
                    bottom: panel_y + panel_height - scale(20),
                };
                ui::draw_text(hdc, "Incorrect passcode!", &mut error_rect, DT_CENTER | DT_SINGLELINE);
            }

            SelectObject(hdc, old_font);
            let _ = DeleteObject(title_font);
            let _ = DeleteObject(time_font);
            let _ = DeleteObject(msg_font);
            let _ = DeleteObject(label_font);

            let _ = EndPaint(hwnd, &ps);
            LRESULT(0)
        }
        WM_COMMAND => {
            let id = (wparam.0 & 0xFFFF) as i32;
            let notification = ((wparam.0 >> 16) & 0xFFFF) as u32;

            if notification == BN_CLICKED {
                match id {
                    ID_UNLOCK_BUTTON => {
                        if entered_passcode_matches() {
                            hide_blocking_overlay();
                        } else {
                            reject_passcode(hwnd);
                        }
                    }
                    ID_EXTEND_15 | ID_EXTEND_30 | ID_EXTEND_60 => {
                        if entered_passcode_matches() {
                            let minutes = match id {
                                ID_EXTEND_15 => 15,
                                ID_EXTEND_30 => 30,
                                _ => 60,
                            };
                            extend_time(minutes);
                            hide_blocking_overlay();
                        } else {
                            reject_passcode(hwnd);
                        }
                    }
                    ID_SHUTDOWN_BUTTON => {
                        let result = MessageBoxW(
                            hwnd,
                            w!("Are you sure you want to shut down the computer?"),
                            w!("Confirm Shutdown"),
                            MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2,
                        );
                        if result == IDYES {
                            initiate_shutdown();
                        }
                    }
                    _ => {}
                }
            }
            LRESULT(0)
        }
        WM_TIMER => {
            match wparam.0 {
                TIMER_REASSERT_TOPMOST => {
                    SetWindowPos(
                        hwnd,
                        HWND_TOPMOST,
                        0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
                    ).ok();
                }
                TIMER_COUNTDOWN => {
                    // A new day restores the allowance — unlock automatically.
                    if crate::mini_overlay::check_day_rollover()
                        && REMAINING_SECONDS.load(Ordering::SeqCst) > 0
                    {
                        hide_blocking_overlay();
                        return LRESULT(0);
                    }

                    let shutdown_remaining = SHUTDOWN_COUNTDOWN_SECONDS.load(Ordering::SeqCst);
                    if shutdown_remaining > 0 {
                        SHUTDOWN_COUNTDOWN_SECONDS.store(shutdown_remaining - 1, Ordering::SeqCst);
                    } else if shutdown_remaining == 0 {
                        initiate_shutdown();
                    }

                    // Redraw without erasing to avoid flicker.
                    let _ = InvalidateRect(hwnd, None, false);
                }
                _ => {}
            }
            LRESULT(0)
        }
        WM_ERASEBKGND => LRESULT(1),
        WM_SYSCOMMAND => {
            // Swallow close/minimize/move/resize/menu.
            let cmd = (wparam.0 & 0xFFF0) as u32;
            if matches!(cmd, SC_CLOSE | SC_MINIMIZE | SC_MOVE | SC_SIZE | SC_KEYMENU) {
                return LRESULT(0);
            }
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
        WM_SYSKEYDOWN => LRESULT(0),
        WM_SHOWWINDOW => {
            // Re-show immediately if something tries to hide us.
            if wparam.0 == 0 {
                let _ = ShowWindow(hwnd, SW_SHOW);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE).ok();
            }
            LRESULT(0)
        }
        WM_WINDOWPOSCHANGING => {
            let pos = &mut *(lparam.0 as *mut WINDOWPOS);
            pos.hwndInsertAfter = HWND_TOPMOST;
            pos.flags |= SWP_NOMOVE | SWP_NOSIZE;
            LRESULT(0)
        }
        WM_CLOSE => LRESULT(0),
        WM_KEYDOWN => {
            if wparam.0 == VK_RETURN.0 as usize {
                if entered_passcode_matches() {
                    hide_blocking_overlay();
                } else {
                    reject_passcode(hwnd);
                }
            }
            LRESULT(0)
        }
        WM_ACTIVATE => {
            let edit_ptr = BLOCKING_EDIT_HWND.load(Ordering::SeqCst);
            if !edit_ptr.is_null() {
                let _ = SetFocus(HWND(edit_ptr));
            }
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

pub unsafe fn register_blocking_class(hinstance: HMODULE) {
    let blocking_wnd_class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(blocking_overlay_proc),
        hInstance: hinstance.into(),
        lpszClassName: w!("ScreenTimeBlockingClass"),
        hbrBackground: CreateSolidBrush(COLORREF(COLOR_OVERLAY_BG)),
        hCursor: LoadCursorW(None, IDC_ARROW).ok().unwrap_or_default(),
        ..zeroed()
    };

    if RegisterClassW(&blocking_wnd_class) == 0 {
        panic!("Failed to register blocking overlay window class");
    }

    let secondary_wnd_class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(secondary_overlay_proc),
        hInstance: hinstance.into(),
        lpszClassName: w!("ScreenTimeSecondaryBlockingClass"),
        hbrBackground: CreateSolidBrush(COLORREF(COLOR_OVERLAY_BG)),
        hCursor: LoadCursorW(None, IDC_ARROW).ok().unwrap_or_default(),
        ..zeroed()
    };

    if RegisterClassW(&secondary_wnd_class) == 0 {
        panic!("Failed to register secondary blocking overlay window class");
    }
}

// ── Secondary monitors ───────────────────────────────────────────────────────

/// Plain "Screen Locked" cover for non-primary monitors.
pub unsafe extern "system" fn secondary_overlay_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_PAINT => {
            let mut ps: PAINTSTRUCT = zeroed();
            let hdc = BeginPaint(hwnd, &mut ps);

            let mut rect: RECT = zeroed();
            GetClientRect(hwnd, &mut rect).ok();

            let bg_brush = CreateSolidBrush(COLORREF(COLOR_OVERLAY_BG));
            FillRect(hdc, &rect, bg_brush);
            let _ = DeleteObject(bg_brush);

            let font = ui::font(48, true);
            let old_font = SelectObject(hdc, font);
            SetTextColor(hdc, COLORREF(COLOR_TEXT_LIGHT));
            SetBkMode(hdc, TRANSPARENT);

            ui::draw_text(hdc, "Screen Locked", &mut rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

            SelectObject(hdc, old_font);
            let _ = DeleteObject(font);

            let _ = EndPaint(hwnd, &ps);
            LRESULT(0)
        }
        WM_ERASEBKGND => LRESULT(1),
        WM_SYSCOMMAND => {
            let cmd = (wparam.0 & 0xFFF0) as u32;
            if matches!(cmd, SC_CLOSE | SC_MINIMIZE | SC_MOVE | SC_SIZE | SC_KEYMENU) {
                return LRESULT(0);
            }
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
        WM_SYSKEYDOWN => LRESULT(0),
        WM_SHOWWINDOW => {
            if wparam.0 == 0 {
                let _ = ShowWindow(hwnd, SW_SHOW);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE).ok();
            }
            LRESULT(0)
        }
        WM_WINDOWPOSCHANGING => {
            let pos = &mut *(lparam.0 as *mut WINDOWPOS);
            pos.hwndInsertAfter = HWND_TOPMOST;
            pos.flags |= SWP_NOMOVE | SWP_NOSIZE;
            LRESULT(0)
        }
        WM_CLOSE => LRESULT(0),
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

unsafe extern "system" fn monitor_enum_callback(
    _hmonitor: HMONITOR,
    _hdc: HDC,
    lprect: *mut RECT,
    lparam: LPARAM,
) -> BOOL {
    let monitors = &mut *(lparam.0 as *mut Vec<MonitorInfo>);

    if let Some(rect) = lprect.as_ref() {
        monitors.push(MonitorInfo {
            rect: *rect,
            // The primary monitor has its origin at (0,0).
            is_primary: rect.left == 0 && rect.top == 0,
        });
    }

    BOOL::from(true)
}

unsafe fn enumerate_monitors() -> Vec<MonitorInfo> {
    let mut monitors: Vec<MonitorInfo> = Vec::new();
    let _ = EnumDisplayMonitors(
        None,
        None,
        Some(monitor_enum_callback),
        LPARAM(&mut monitors as *mut Vec<MonitorInfo> as isize),
    );
    monitors
}

/// Create a cover window for every non-primary monitor.
pub unsafe fn create_secondary_overlays(hinstance: HMODULE) {
    let mut secondary_hwnds = SECONDARY_OVERLAY_HWNDS.lock().unwrap();
    secondary_hwnds.clear();

    for monitor in enumerate_monitors() {
        if monitor.is_primary {
            continue;
        }

        let hwnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            w!("ScreenTimeSecondaryBlockingClass"),
            w!("Screen Time - Locked"),
            WS_POPUP,
            monitor.rect.left,
            monitor.rect.top,
            monitor.rect.right - monitor.rect.left,
            monitor.rect.bottom - monitor.rect.top,
            None,
            None,
            hinstance,
            None,
        );

        if let Ok(h) = hwnd {
            secondary_hwnds.push(h.0 as isize);
        }
    }
}

unsafe fn show_secondary_overlays() {
    for &hwnd_ptr in SECONDARY_OVERLAY_HWNDS.lock().unwrap().iter() {
        let hwnd = HWND(hwnd_ptr as *mut c_void);
        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            0, 0, 0, 0,
            SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE,
        ).ok();
        let _ = ShowWindow(hwnd, SW_SHOW);
    }
}

unsafe fn hide_secondary_overlays() {
    for &hwnd_ptr in SECONDARY_OVERLAY_HWNDS.lock().unwrap().iter() {
        let _ = ShowWindow(HWND(hwnd_ptr as *mut c_void), SW_HIDE);
    }
}
