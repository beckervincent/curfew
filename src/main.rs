//! Screen Time Manager — a Windows tray app that enforces daily screen time
//! limits with warnings, a passcode-protected lock screen and pause support.

#![windows_subsystem = "windows"]

mod blocking;
mod cli_install;
mod constants;
mod database;
mod dialogs;
mod dpi;
mod mini_overlay;
mod overlay;
mod service;
mod theme;
mod tray;
mod ui;
mod updater;

use std::mem::zeroed;
use std::sync::atomic::Ordering;
use windows::{
    core::{w, PCWSTR},
    Win32::{
        Foundation::{GetLastError, CloseHandle, BOOL, ERROR_ALREADY_EXISTS},
        System::{LibraryLoader::GetModuleHandleW, Threading::CreateMutexW},
        UI::HiDpi::{SetProcessDpiAwareness, PROCESS_PER_MONITOR_DPI_AWARE},
        UI::WindowsAndMessaging::*,
    },
};

use blocking::{
    create_blocking_overlay, create_secondary_overlays, register_blocking_class, REMAINING_SECONDS,
};
use constants::MUTEX_NAME;
use database::{get_current_weekday, get_daily_limit, init_database, load_remaining_time};
use mini_overlay::{create_mini_overlay, register_mini_overlay_class, show_mini_overlay};
use overlay::{create_overlay_window, register_overlay_class};
use tray::{add_tray_icon, window_proc};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let has_flag = |flag: &str| args.iter().any(|a| a == flag);

    if has_flag("--install") {
        cli_install::run_install(&args);
        return;
    }
    if has_flag("--uninstall") {
        cli_install::run_uninstall(&args);
        return;
    }
    if has_flag("--service") {
        unsafe { service::run_service_mode() };
        return;
    }

    // --setup: first-run wizard (PIN + settings), launched by the installer.
    // --settings: settings dialog only, used by the Start Menu shortcut.
    if has_flag("--setup") || has_flag("--settings") {
        unsafe {
            let _ = SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
            dpi::init_dpi();
            if init_database().is_ok() {
                if has_flag("--setup") {
                    dialogs::show_setup_wizard();
                } else {
                    dialogs::show_settings_dialog(windows::Win32::Foundation::HWND::default());
                }
            }
        }
        return;
    }

    unsafe {
        let _ = SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
        dpi::init_dpi();

        // Exit silently if another instance is running (the service retries spawning).
        if !ensure_single_instance() {
            return;
        }

        // Never fatal: a corrupt database is reset to defaults.
        let _ = init_database();

        let hinstance = GetModuleHandleW(None).expect("Failed to get module handle");

        let class_name = w!("ScreenTimeManagerClass");
        let wnd_class = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(window_proc),
            hInstance: hinstance.into(),
            lpszClassName: class_name,
            ..zeroed()
        };

        if RegisterClassW(&wnd_class) == 0 {
            panic!("Failed to register window class");
        }

        register_overlay_class(hinstance);
        register_blocking_class(hinstance);
        register_mini_overlay_class(hinstance);

        // Hidden window that owns the tray icon and receives its messages.
        let hwnd = CreateWindowExW(
            Default::default(),
            class_name,
            w!("Screen Time Manager"),
            WS_POPUP,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            None,
            None,
            hinstance,
            None,
        )
        .expect("Failed to create window");

        create_overlay_window(hinstance);
        create_blocking_overlay(hinstance);
        create_secondary_overlays(hinstance);
        create_mini_overlay(hinstance);

        // Remaining time: today's saved value, or the daily limit.
        let remaining = load_remaining_time()
            .unwrap_or_else(|| (get_daily_limit(get_current_weekday()) * 60) as i32);
        REMAINING_SECONDS.store(remaining, Ordering::SeqCst);

        mini_overlay::SESSION_ACTIVE_SECONDS
            .store(database::get_session_active_time(), Ordering::SeqCst);

        // Record today's date so midnight rollover can be detected later.
        mini_overlay::check_day_rollover();

        show_mini_overlay();

        if remaining <= 0 {
            blocking::show_blocking_overlay(&database::get_blocking_message());
        }

        add_tray_icon(hwnd);

        let mut msg: MSG = zeroed();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

/// Returns false when another instance already holds the mutex.
unsafe fn ensure_single_instance() -> bool {
    let mutex_name = ui::to_wide(MUTEX_NAME);
    match CreateMutexW(None, BOOL::from(true), PCWSTR(mutex_name.as_ptr())) {
        Ok(h) => {
            if GetLastError() == ERROR_ALREADY_EXISTS {
                let _ = CloseHandle(h);
                false
            } else {
                true
            }
        }
        Err(_) => false,
    }
}
