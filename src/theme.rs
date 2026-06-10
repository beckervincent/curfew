//! Light/dark theme support. Dialogs follow the Windows app theme setting;
//! the lock screen and overlays stay dark on purpose.

use std::ffi::c_void;
use windows::{
    core::w,
    Win32::System::Registry::{RegGetValueW, HKEY_CURRENT_USER, RRF_RT_REG_DWORD},
};

use crate::constants::*;

#[derive(Clone, Copy)]
pub struct Theme {
    pub dark: bool,
    pub bg: u32,
    pub edit_bg: u32,
    pub text: u32,
    pub text_secondary: u32,
    pub text_muted: u32,
}

const DARK: Theme = Theme {
    dark: true,
    bg: DARK_BG,
    edit_bg: DARK_EDIT_BG,
    text: COLOR_TEXT_WHITE,
    text_secondary: COLOR_TEXT_LIGHT,
    text_muted: COLOR_TEXT_MUTED,
};

const LIGHT: Theme = Theme {
    dark: false,
    bg: 0x00F3F3F3,
    edit_bg: 0x00FFFFFF,
    text: 0x00262626,
    text_secondary: 0x00505050,
    text_muted: 0x00909090,
};

/// True when Windows apps are set to dark mode (the default when unreadable).
pub fn is_dark_mode() -> bool {
    let mut data: u32 = 0;
    let mut size: u32 = std::mem::size_of::<u32>() as u32;
    let result = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            w!(r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"),
            w!("AppsUseLightTheme"),
            RRF_RT_REG_DWORD,
            None,
            Some(&mut data as *mut u32 as *mut c_void),
            Some(&mut size),
        )
    };
    if result.is_ok() {
        data == 0
    } else {
        true
    }
}

/// Palette matching the current Windows app theme.
pub fn current() -> Theme {
    if is_dark_mode() {
        DARK
    } else {
        LIGHT
    }
}
