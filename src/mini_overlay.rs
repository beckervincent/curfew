//! Mini overlay: small always-on-top display of the remaining time.
//! Its once-per-second timer also drives the countdown, warnings,
//! idle detection and day rollover.

use std::mem::zeroed;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicPtr, Ordering};
use std::sync::Mutex;
use windows::{
    core::w,
    Win32::{
        Foundation::{COLORREF, HWND, LPARAM, LRESULT, RECT, WPARAM},
        Graphics::Gdi::{
            BeginPaint, CreateRoundRectRgn, CreateSolidBrush, DeleteObject, DrawTextW, EndPaint,
            FillRect, InvalidateRect, SelectObject, SetBkMode, SetTextColor, SetWindowRgn,
            DT_CENTER, DT_SINGLELINE, DT_VCENTER, PAINTSTRUCT, TRANSPARENT,
        },
        System::SystemInformation::GetTickCount,
        UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO},
        UI::WindowsAndMessaging::*,
    },
};

use crate::blocking::REMAINING_SECONDS;
use crate::constants::*;
use crate::database;
use crate::dpi::scale;
use crate::ui;

pub static MINI_OVERLAY_HWND: AtomicPtr<std::ffi::c_void> = AtomicPtr::new(std::ptr::null_mut());
pub static MINI_OVERLAY_VISIBLE: AtomicBool = AtomicBool::new(false);

// Pause state.
pub static IS_PAUSED: AtomicBool = AtomicBool::new(false);
pub static CURRENT_PAUSE_DURATION: AtomicI32 = AtomicI32::new(0);
pub static SESSION_ACTIVE_SECONDS: AtomicI32 = AtomicI32::new(0);

// Idle pause is independent from manual pause.
pub static IS_IDLE_PAUSED: AtomicBool = AtomicBool::new(false);

/// Last date the timer ran on, for midnight rollover detection.
static LAST_KNOWN_DATE: Mutex<Option<String>> = Mutex::new(None);

pub const TIMER_MINI_UPDATE: usize = 10;

// Base dimensions at 96 DPI.
const MINI_WIDTH_BASE: i32 = 148;
const MINI_HEIGHT_BASE: i32 = 40;
const MINI_MARGIN_BASE: i32 = 10;

pub unsafe fn create_mini_overlay(hinstance: windows::Win32::Foundation::HMODULE) {
    let class_name = w!("ScreenTimeMiniOverlayClass");

    let mini_width = scale(MINI_WIDTH_BASE);
    let mini_height = scale(MINI_HEIGHT_BASE);
    let mini_margin = scale(MINI_MARGIN_BASE);

    // Top-right corner of the primary monitor.
    let screen_width = GetSystemMetrics(SM_CXSCREEN);
    let x = screen_width - mini_width - mini_margin;
    let y = mini_margin;

    let hwnd = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT,
        class_name,
        w!("Screen Time"),
        WS_POPUP,
        x,
        y,
        mini_width,
        mini_height,
        None,
        None,
        hinstance,
        None,
    )
    .expect("Failed to create mini overlay window");

    SetLayeredWindowAttributes(hwnd, COLORREF(0), 200, LWA_ALPHA)
        .expect("Failed to set layered window attributes");

    // Rounded corners.
    let rgn = CreateRoundRectRgn(0, 0, mini_width, mini_height, scale(12), scale(12));
    SetWindowRgn(hwnd, rgn, true);

    MINI_OVERLAY_HWND.store(hwnd.0, Ordering::SeqCst);
}

pub unsafe fn show_mini_overlay() {
    let hwnd = HWND(MINI_OVERLAY_HWND.load(Ordering::SeqCst));
    if hwnd.0.is_null() {
        return;
    }

    MINI_OVERLAY_VISIBLE.store(true, Ordering::SeqCst);

    let _ = InvalidateRect(hwnd, None, true);
    let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    let _ = SetTimer(hwnd, TIMER_MINI_UPDATE, 1000, None);
}

pub unsafe fn hide_mini_overlay() {
    let hwnd = HWND(MINI_OVERLAY_HWND.load(Ordering::SeqCst));
    if hwnd.0.is_null() {
        return;
    }

    MINI_OVERLAY_VISIBLE.store(false, Ordering::SeqCst);

    let _ = KillTimer(hwnd, TIMER_MINI_UPDATE);
    let _ = ShowWindow(hwnd, SW_HIDE);
}

pub unsafe fn update_mini_overlay() {
    let hwnd = HWND(MINI_OVERLAY_HWND.load(Ordering::SeqCst));
    if !hwnd.0.is_null() {
        let _ = InvalidateRect(hwnd, None, true);
    }
}

/// Format seconds as "1:30:45" or "30:45".
fn format_time_compact(seconds: i32) -> String {
    if seconds < 0 {
        return String::from("--:--");
    }
    let hours = seconds / 3600;
    let minutes = (seconds % 3600) / 60;
    let secs = seconds % 60;
    if hours > 0 {
        format!("{}:{:02}:{:02}", hours, minutes, secs)
    } else {
        format!("{}:{:02}", minutes, secs)
    }
}

fn get_time_color(seconds: i32) -> u32 {
    if seconds < 0 {
        COLOR_TEXT_LIGHT
    } else if seconds <= 60 {
        COLOR_ERROR
    } else if seconds <= 300 {
        COLOR_ACCENT
    } else {
        COLOR_TEXT_WHITE
    }
}

// ── Day rollover ─────────────────────────────────────────────────────────────

/// Detect a local-date change. On a new day, reset the timer to the new day's
/// limit and clear the session counter. Returns true when a rollover happened.
/// The first call only records the current date.
pub fn check_day_rollover() -> bool {
    let today = database::get_today_date();
    let mut last = LAST_KNOWN_DATE.lock().unwrap();

    match last.as_deref() {
        Some(prev) if prev == today => false,
        Some(_) => {
            *last = Some(today);
            let limit = (database::get_daily_limit(database::get_current_weekday()) * 60) as i32;
            REMAINING_SECONDS.store(limit, Ordering::SeqCst);
            SESSION_ACTIVE_SECONDS.store(0, Ordering::SeqCst);
            database::save_remaining_time(limit);
            database::save_session_active_time(0);
            true
        }
        None => {
            *last = Some(today);
            false
        }
    }
}

// ── Pause mode ───────────────────────────────────────────────────────────────

pub fn is_paused() -> bool {
    IS_PAUSED.load(Ordering::SeqCst)
}

#[derive(Debug, Clone)]
pub enum PauseBlockedReason {
    Disabled,
    BudgetExhausted,
    CooldownActive { seconds_remaining: i32 },
    MinActiveTimeNotMet { seconds_remaining: i32 },
    TimeTooLow,
}

/// Check whether pausing is allowed right now.
pub fn can_pause() -> Result<(), PauseBlockedReason> {
    // Unpausing is always allowed.
    if IS_PAUSED.load(Ordering::SeqCst) {
        return Ok(());
    }

    if !database::is_pause_enabled() {
        return Err(PauseBlockedReason::Disabled);
    }

    let config = database::get_pause_config();

    if REMAINING_SECONDS.load(Ordering::SeqCst) < 60 {
        return Err(PauseBlockedReason::TimeTooLow);
    }

    let pause_used = database::get_pause_used_today();
    let budget_seconds = (config.daily_budget_minutes * 60) as i32;
    if pause_used >= budget_seconds {
        return Err(PauseBlockedReason::BudgetExhausted);
    }

    let last_pause_end = database::get_last_pause_end();
    let cooldown_seconds = (config.cooldown_minutes * 60) as i64;
    let since_last = database::get_current_timestamp() - last_pause_end;
    if last_pause_end > 0 && since_last < cooldown_seconds {
        return Err(PauseBlockedReason::CooldownActive {
            seconds_remaining: (cooldown_seconds - since_last) as i32,
        });
    }

    let session_active = SESSION_ACTIVE_SECONDS.load(Ordering::SeqCst);
    let min_active_seconds = (config.min_active_time_minutes * 60) as i32;
    if session_active < min_active_seconds {
        return Err(PauseBlockedReason::MinActiveTimeNotMet {
            seconds_remaining: min_active_seconds - session_active,
        });
    }

    Ok(())
}

/// Today's unused pause budget in seconds.
pub fn get_remaining_pause_budget() -> i32 {
    let config = database::get_pause_config();
    let budget_seconds = (config.daily_budget_minutes * 60) as i32;
    (budget_seconds - database::get_pause_used_today()).max(0)
}

/// Longest allowed duration for the current pause.
pub fn get_max_pause_duration() -> i32 {
    let config = database::get_pause_config();
    let max_single = (config.max_duration_minutes * 60) as i32;
    max_single.min(get_remaining_pause_budget())
}

/// Toggle pause; returns true when now paused.
pub fn toggle_pause() -> Result<bool, PauseBlockedReason> {
    if IS_PAUSED.load(Ordering::SeqCst) {
        resume_timer();
        Ok(false)
    } else {
        can_pause()?;
        pause_timer();
        Ok(true)
    }
}

fn pause_timer() {
    CURRENT_PAUSE_DURATION.store(0, Ordering::SeqCst);
    IS_PAUSED.store(true, Ordering::SeqCst);
    unsafe { update_mini_overlay() };
}

fn resume_timer() {
    let pause_duration = CURRENT_PAUSE_DURATION.load(Ordering::SeqCst);

    database::save_pause_used_today(database::get_pause_used_today() + pause_duration);
    database::log_pause_event(pause_duration);
    database::save_last_pause_end(database::get_current_timestamp());

    IS_PAUSED.store(false, Ordering::SeqCst);
    CURRENT_PAUSE_DURATION.store(0, Ordering::SeqCst);
    unsafe { update_mini_overlay() };
}

// ── Idle detection ───────────────────────────────────────────────────────────

/// Seconds since the last keyboard/mouse input.
fn get_idle_seconds() -> u32 {
    unsafe {
        let mut lii: LASTINPUTINFO = zeroed();
        lii.cbSize = std::mem::size_of::<LASTINPUTINFO>() as u32;
        if GetLastInputInfo(&mut lii).as_bool() {
            GetTickCount().wrapping_sub(lii.dwTime) / 1000
        } else {
            0
        }
    }
}

fn check_idle_state() {
    if !database::is_idle_enabled() {
        IS_IDLE_PAUSED.store(false, Ordering::SeqCst);
        return;
    }

    let idle = get_idle_seconds() >= database::get_idle_timeout_minutes() * 60;
    let currently_idle_paused = IS_IDLE_PAUSED.load(Ordering::SeqCst);

    if idle {
        if !currently_idle_paused && !IS_PAUSED.load(Ordering::SeqCst) {
            IS_IDLE_PAUSED.store(true, Ordering::SeqCst);
        }
    } else if currently_idle_paused {
        IS_IDLE_PAUSED.store(false, Ordering::SeqCst);
    }
}

pub fn is_idle_paused() -> bool {
    IS_IDLE_PAUSED.load(Ordering::SeqCst)
}

// ── Window proc ──────────────────────────────────────────────────────────────

/// One countdown tick: decrement, persist, fire warnings and the block screen.
unsafe fn countdown_tick() {
    let current = REMAINING_SECONDS.load(Ordering::SeqCst);
    if current <= 0 {
        return;
    }

    let new_time = current - 1;
    REMAINING_SECONDS.store(new_time, Ordering::SeqCst);
    SESSION_ACTIVE_SECONDS.fetch_add(1, Ordering::SeqCst);

    // Persist every 30 seconds.
    if new_time % 30 == 0 {
        database::save_remaining_time(new_time);
        database::save_session_active_time(SESSION_ACTIVE_SECONDS.load(Ordering::SeqCst));
    }

    for warning in 1..=2 {
        let (warn_mins, warn_msg) = database::get_warning_config(warning);
        if new_time == (warn_mins * 60) as i32 {
            crate::overlay::show_overlay(&warn_msg, 10);
        }
    }

    if new_time == 0 {
        crate::blocking::show_blocking_overlay(&database::get_blocking_message());
    }
}

pub unsafe extern "system" fn mini_overlay_proc(
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

            let paused = IS_PAUSED.load(Ordering::SeqCst);
            let idle_paused = IS_IDLE_PAUSED.load(Ordering::SeqCst);

            let bg_color = if paused {
                MINI_BG_PAUSED
            } else if idle_paused {
                MINI_BG_IDLE
            } else {
                MINI_BG
            };
            let bg_brush = CreateSolidBrush(COLORREF(bg_color));
            FillRect(hdc, &rect, bg_brush);
            let _ = DeleteObject(bg_brush);

            let remaining = REMAINING_SECONDS.load(Ordering::SeqCst);

            let (display_text, color) = if paused {
                let pause_remaining =
                    get_max_pause_duration() - CURRENT_PAUSE_DURATION.load(Ordering::SeqCst);
                (format!("II {}", format_time_compact(pause_remaining)), COLOR_PAUSE_TEXT)
            } else if idle_paused {
                (format!("ZZ {}", format_time_compact(remaining)), COLOR_TEXT_MUTED)
            } else {
                (format_time_compact(remaining), get_time_color(remaining))
            };

            // Small padding so glyphs don't clip at high DPI.
            let pad = scale(2);
            let mut text_rect = RECT {
                left: rect.left + pad,
                top: rect.top + pad,
                right: rect.right - pad,
                bottom: rect.bottom - pad,
            };
            let hfont = ui::font_face(24, true, w!("Consolas"));

            let old_font = SelectObject(hdc, hfont);
            SetTextColor(hdc, COLORREF(color));
            SetBkMode(hdc, TRANSPARENT);

            let mut wide_text: Vec<u16> = display_text.encode_utf16().collect();
            DrawTextW(
                hdc,
                &mut wide_text,
                &mut text_rect,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE,
            );

            SelectObject(hdc, old_font);
            let _ = DeleteObject(hfont);

            let _ = EndPaint(hwnd, &ps);
            LRESULT(0)
        }
        WM_TIMER => {
            if wparam.0 == TIMER_MINI_UPDATE {
                check_day_rollover();

                if IS_PAUSED.load(Ordering::SeqCst) {
                    // Count the pause instead of the screen time.
                    let duration = CURRENT_PAUSE_DURATION.fetch_add(1, Ordering::SeqCst) + 1;
                    if duration >= get_max_pause_duration() {
                        resume_timer();
                    }
                } else if !IS_IDLE_PAUSED.load(Ordering::SeqCst) {
                    countdown_tick();
                }

                check_idle_state();
                let _ = InvalidateRect(hwnd, None, true);
            }
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

pub unsafe fn register_mini_overlay_class(hinstance: windows::Win32::Foundation::HMODULE) {
    let class_name = w!("ScreenTimeMiniOverlayClass");
    let wnd_class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(mini_overlay_proc),
        hInstance: hinstance.into(),
        lpszClassName: class_name,
        hbrBackground: CreateSolidBrush(COLORREF(MINI_BG)),
        ..zeroed()
    };

    if RegisterClassW(&wnd_class) == 0 {
        panic!("Failed to register mini overlay window class");
    }
}
