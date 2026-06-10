//! Shared Win32 UI helpers: UTF-16 strings, fonts, controls, drawing.

use std::sync::atomic::{AtomicIsize, Ordering};
use windows::{
    core::{w, PCWSTR},
    Win32::{
        Foundation::{COLORREF, HWND, LPARAM, LRESULT, RECT, WPARAM},
        Graphics::Gdi::{
            CreateFontW, CreateSolidBrush, DrawTextW, SetBkColor, SetTextColor, DRAW_TEXT_FORMAT,
            FW_BOLD, FW_NORMAL, HBRUSH, HDC, HFONT,
        },
        UI::Controls::EM_SETLIMITTEXT,
        UI::WindowsAndMessaging::{
            CreateWindowExW, SendMessageW, GetWindowTextW, SetWindowTextW, BS_AUTOCHECKBOX,
            BS_PUSHBUTTON, ES_CENTER, ES_NUMBER, ES_PASSWORD, HMENU, WINDOW_EX_STYLE,
            WINDOW_STYLE, WM_SETFONT, WS_BORDER, WS_CHILD, WS_TABSTOP, WS_VISIBLE,
        },
    },
};

use crate::constants::{COLOR_TEXT_LIGHT, DARK_EDIT_BG};
use crate::dpi::scale;

/// Encode text as a null-terminated UTF-16 buffer.
/// The buffer must stay alive for as long as Win32 reads the pointer.
pub fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Create a DPI-scaled "Segoe UI" font.
pub unsafe fn font(height: i32, bold: bool) -> HFONT {
    font_face(height, bold, w!("Segoe UI"))
}

/// Create a DPI-scaled font with a specific face.
pub unsafe fn font_face(height: i32, bold: bool, face: PCWSTR) -> HFONT {
    let weight = if bold { FW_BOLD.0 } else { FW_NORMAL.0 } as i32;
    CreateFontW(scale(height), 0, 0, 0, weight, 0, 0, 0, 0, 0, 0, 0, 0, face)
}

/// Assign a font to a control.
pub unsafe fn set_font(hwnd: HWND, font: HFONT) {
    SendMessageW(hwnd, WM_SETFONT, WPARAM(font.0 as usize), LPARAM(1));
}

/// Draw text into a rect.
pub unsafe fn draw_text(hdc: HDC, text: &str, rect: &mut RECT, flags: DRAW_TEXT_FORMAT) {
    let mut wide: Vec<u16> = text.encode_utf16().collect();
    DrawTextW(hdc, &mut wide, rect, flags);
}

/// Read the full text of a control (up to 255 chars).
pub unsafe fn window_text(hwnd: HWND) -> String {
    let mut buffer = [0u16; 256];
    let len = GetWindowTextW(hwnd, &mut buffer);
    String::from_utf16_lossy(&buffer[..len as usize])
}

/// Set a control's text from a Rust string.
pub unsafe fn set_window_text(hwnd: HWND, text: &str) {
    let wide = to_wide(text);
    SetWindowTextW(hwnd, PCWSTR(wide.as_ptr())).ok();
}

/// Create a STATIC label.
pub unsafe fn create_label(
    parent: HWND,
    text: &str,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    label_font: HFONT,
) {
    let wide = to_wide(text);
    if let Ok(h) = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        w!("STATIC"),
        PCWSTR(wide.as_ptr()),
        WS_CHILD | WS_VISIBLE,
        scale(x),
        scale(y),
        scale(width),
        scale(height),
        parent,
        HMENU::default(),
        None,
        None,
    ) {
        set_font(h, label_font);
    }
}

/// Style options for `create_edit`.
pub struct EditStyle {
    pub numeric: bool,
    pub password: bool,
    pub centered: bool,
    pub max_len: usize,
}

impl EditStyle {
    pub fn text() -> Self {
        Self { numeric: false, password: false, centered: false, max_len: 255 }
    }
    pub fn number(max_len: usize) -> Self {
        Self { numeric: true, password: false, centered: true, max_len }
    }
    pub fn passcode() -> Self {
        Self { numeric: true, password: true, centered: true, max_len: 4 }
    }
}

/// Create an EDIT control, set its font, limit and initial value.
#[allow(clippy::too_many_arguments)]
pub unsafe fn create_edit(
    parent: HWND,
    id: i32,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    edit_font: HFONT,
    style: EditStyle,
    initial: &str,
) -> HWND {
    let mut win_style = WS_CHILD | WS_VISIBLE | WS_BORDER | WS_TABSTOP;
    if style.numeric {
        win_style |= WINDOW_STYLE(ES_NUMBER as u32);
    }
    if style.password {
        win_style |= WINDOW_STYLE(ES_PASSWORD as u32);
    }
    if style.centered {
        win_style |= WINDOW_STYLE(ES_CENTER as u32);
    }

    match CreateWindowExW(
        WINDOW_EX_STYLE(0x200), // WS_EX_CLIENTEDGE
        w!("EDIT"),
        w!(""),
        win_style,
        scale(x),
        scale(y),
        scale(width),
        scale(height),
        parent,
        HMENU(id as _),
        None,
        None,
    ) {
        Ok(h) => {
            set_font(h, edit_font);
            SendMessageW(h, EM_SETLIMITTEXT, WPARAM(style.max_len), LPARAM(0));
            if !initial.is_empty() {
                set_window_text(h, initial);
            }
            h
        }
        Err(_) => HWND::default(),
    }
}

/// Create a push button.
#[allow(clippy::too_many_arguments)]
pub unsafe fn create_button(
    parent: HWND,
    id: i32,
    text: &str,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    btn_font: HFONT,
) {
    let wide = to_wide(text);
    if let Ok(h) = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        w!("BUTTON"),
        PCWSTR(wide.as_ptr()),
        WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_PUSHBUTTON as u32),
        scale(x),
        scale(y),
        scale(width),
        scale(height),
        parent,
        HMENU(id as _),
        None,
        None,
    ) {
        set_font(h, btn_font);
    }
}

/// Create a checkbox; returns the handle (default on failure).
pub unsafe fn create_checkbox(
    parent: HWND,
    text: &str,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    chk_font: HFONT,
) -> HWND {
    let wide = to_wide(text);
    match CreateWindowExW(
        WINDOW_EX_STYLE(0),
        w!("BUTTON"),
        PCWSTR(wide.as_ptr()),
        WS_CHILD | WS_VISIBLE | WINDOW_STYLE(BS_AUTOCHECKBOX as u32),
        scale(x),
        scale(y),
        scale(width),
        scale(height),
        parent,
        HMENU::default(),
        None,
        None,
    ) {
        Ok(h) => {
            set_font(h, chk_font);
            h
        }
        Err(_) => HWND::default(),
    }
}

/// Cached brush for dark edit/static backgrounds.
/// WM_CTLCOLOR* fires on every redraw; creating a brush each time leaks GDI handles.
static CTL_BG_BRUSH: AtomicIsize = AtomicIsize::new(0);

/// Shared WM_CTLCOLOREDIT / WM_CTLCOLORSTATIC handler for dark dialogs.
pub unsafe fn ctl_color_dark(wparam: WPARAM) -> LRESULT {
    let hdc = HDC(wparam.0 as _);
    SetTextColor(hdc, COLORREF(COLOR_TEXT_LIGHT));
    SetBkColor(hdc, COLORREF(DARK_EDIT_BG));

    let mut brush = CTL_BG_BRUSH.load(Ordering::Relaxed);
    if brush == 0 {
        brush = CreateSolidBrush(COLORREF(DARK_EDIT_BG)).0 as isize;
        CTL_BG_BRUSH.store(brush, Ordering::Relaxed);
    }
    LRESULT(HBRUSH(brush as _).0 as isize)
}

/// Format seconds as "1h 30m 45s" (or "--" when negative).
pub fn format_duration(seconds: i32) -> String {
    if seconds < 0 {
        return String::from("--");
    }
    let hours = seconds / 3600;
    let minutes = (seconds % 3600) / 60;
    let secs = seconds % 60;
    if hours > 0 {
        format!("{}h {}m {}s", hours, minutes, secs)
    } else if minutes > 0 {
        format!("{}m {}s", minutes, secs)
    } else {
        format!("{}s", secs)
    }
}
