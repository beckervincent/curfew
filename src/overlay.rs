//! Warning overlay: a click-through banner that auto-hides after a delay.

use std::mem::zeroed;
use std::sync::atomic::{AtomicPtr, Ordering};
use std::sync::Mutex;
use windows::{
    core::w,
    Win32::{
        Foundation::{COLORREF, HWND, LPARAM, LRESULT, RECT, WPARAM},
        Graphics::Gdi::{
            BeginPaint, CreateSolidBrush, DeleteObject, EndPaint, FillRect, GetStockObject,
            InvalidateRect, SelectObject, SetBkMode, SetTextColor, BLACK_BRUSH, DT_CENTER,
            DT_SINGLELINE, DT_VCENTER, HBRUSH, PAINTSTRUCT, TRANSPARENT,
        },
        Media::Audio::{PlaySoundW, SND_ALIAS, SND_ASYNC},
        UI::WindowsAndMessaging::*,
    },
};

use crate::constants::*;
use crate::dpi::scale;
use crate::ui;

pub static OVERLAY_HWND: AtomicPtr<std::ffi::c_void> = AtomicPtr::new(std::ptr::null_mut());
pub static OVERLAY_TEXT: Mutex<Option<String>> = Mutex::new(None);

pub const TIMER_OVERLAY_HIDE: usize = 1;

pub unsafe fn create_overlay_window(hinstance: windows::Win32::Foundation::HMODULE) {
    let overlay_class_name = w!("ScreenTimeOverlayClass");

    let screen_width = GetSystemMetrics(SM_CXSCREEN);
    let screen_height = GetSystemMetrics(SM_CYSCREEN);

    // Full-width banner, vertically centered.
    let overlay_height = scale(120);
    let overlay_y = (screen_height - overlay_height) / 2;

    let overlay_hwnd = CreateWindowExW(
        WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT,
        overlay_class_name,
        w!("Screen Time Overlay"),
        WS_POPUP,
        0,
        overlay_y,
        screen_width,
        overlay_height,
        None,
        None,
        hinstance,
        None,
    )
    .expect("Failed to create overlay window");

    SetLayeredWindowAttributes(overlay_hwnd, COLORREF(0), 230, LWA_ALPHA)
        .expect("Failed to set layered window attributes");

    OVERLAY_HWND.store(overlay_hwnd.0, Ordering::SeqCst);
}

/// Show the banner with `text` for `duration_seconds`.
pub unsafe fn show_overlay(text: &str, duration_seconds: u32) {
    let overlay_hwnd = HWND(OVERLAY_HWND.load(Ordering::SeqCst));
    if overlay_hwnd.0.is_null() {
        return;
    }

    *OVERLAY_TEXT.lock().unwrap() = Some(text.to_string());
    let _ = InvalidateRect(overlay_hwnd, None, true);

    SetWindowPos(
        overlay_hwnd,
        HWND_TOPMOST,
        0, 0, 0, 0,
        SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE,
    ).ok();

    let _ = ShowWindow(overlay_hwnd, SW_SHOWNOACTIVATE);
    let _ = PlaySoundW(w!("SystemExclamation"), None, SND_ALIAS | SND_ASYNC);
    let _ = SetTimer(overlay_hwnd, TIMER_OVERLAY_HIDE, duration_seconds * 1000, None);
}

pub unsafe fn hide_overlay() {
    let overlay_hwnd = HWND(OVERLAY_HWND.load(Ordering::SeqCst));
    if overlay_hwnd.0.is_null() {
        return;
    }

    let _ = KillTimer(overlay_hwnd, TIMER_OVERLAY_HIDE);
    let _ = ShowWindow(overlay_hwnd, SW_HIDE);
    *OVERLAY_TEXT.lock().unwrap() = None;
}

pub unsafe extern "system" fn overlay_window_proc(
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

            let bg_brush = CreateSolidBrush(COLORREF(COLOR_OVERLAY_BANNER));
            FillRect(hdc, &rect, bg_brush);
            let _ = DeleteObject(bg_brush);

            let overlay_text_guard = OVERLAY_TEXT.lock().unwrap();
            if let Some(ref text) = *overlay_text_guard {
                let hfont = ui::font(72, true);
                let old_font = SelectObject(hdc, hfont);
                SetTextColor(hdc, COLORREF(COLOR_TEXT_WHITE));
                SetBkMode(hdc, TRANSPARENT);

                ui::draw_text(hdc, text, &mut rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

                SelectObject(hdc, old_font);
                let _ = DeleteObject(hfont);
            }

            let _ = EndPaint(hwnd, &ps);
            LRESULT(0)
        }
        WM_TIMER => {
            if wparam.0 == TIMER_OVERLAY_HIDE {
                hide_overlay();
            }
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

pub unsafe fn register_overlay_class(hinstance: windows::Win32::Foundation::HMODULE) {
    let overlay_class_name = w!("ScreenTimeOverlayClass");
    let overlay_wnd_class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(overlay_window_proc),
        hInstance: hinstance.into(),
        lpszClassName: overlay_class_name,
        hbrBackground: HBRUSH(GetStockObject(BLACK_BRUSH).0),
        ..zeroed()
    };

    if RegisterClassW(&overlay_wnd_class) == 0 {
        panic!("Failed to register overlay window class");
    }
}
