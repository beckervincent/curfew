//! Service mode for Screen Time Manager
//! Runs as SYSTEM via NSSM, monitors user sessions, and spawns the tray app
//! in each interactive user session.

use std::mem::zeroed;
use windows::{
    core::{w, PCWSTR, PWSTR},
    Win32::{
        Foundation::{CloseHandle, BOOL, HANDLE, HWND, LPARAM, LRESULT, WPARAM},
        System::{
            LibraryLoader::{GetModuleFileNameW, GetModuleHandleW},
            RemoteDesktop::{
                WTSActive, WTSEnumerateSessionsW, WTSFreeMemory, WTSQueryUserToken,
                WTSRegisterSessionNotification, WTSUnRegisterSessionNotification,
            },
            Threading::{
                CreateProcessAsUserW, PROCESS_INFORMATION, STARTUPINFOW,
                CREATE_UNICODE_ENVIRONMENT,
            },
            Environment::{CreateEnvironmentBlock, DestroyEnvironmentBlock},
        },
        UI::WindowsAndMessaging::*,
    },
};

const NOTIFY_FOR_ALL_SESSIONS: u32 = 1;
const WM_WTSSESSION_CHANGE: u32 = 0x02B1;
const WTS_CONSOLE_CONNECT: usize = 0x1;
const WTS_SESSION_LOGON:   usize = 0x5;
const WTS_SESSION_UNLOCK:  usize = 0x8;

/// Timer ID used to retry spawning after boot (in case the shell wasn't ready yet)
const TIMER_BOOT_RETRY: usize = 1;

pub unsafe fn run_service_mode() {
    let hinstance = GetModuleHandleW(None).expect("Failed to get module handle");

    let class_name = w!("ScreenTimeServiceClass");
    let wnd_class = WNDCLASSW {
        lpfnWndProc: Some(service_window_proc),
        hInstance: hinstance.into(),
        lpszClassName: class_name,
        ..zeroed()
    };
    RegisterClassW(&wnd_class);

    let hwnd = CreateWindowExW(
        Default::default(),
        class_name,
        w!("Screen Time Service"),
        WS_POPUP,
        0, 0, 0, 0,
        None, None, hinstance, None,
    ).expect("Failed to create service window");

    // Register for WTS session change notifications
    if let Err(e) = WTSRegisterSessionNotification(hwnd, NOTIFY_FOR_ALL_SESSIONS) {
        eprintln!("WTSRegisterSessionNotification failed: {:?}", e);
    }

    // Spawn for any users already logged in when the service starts.
    // Do it immediately, then schedule a retry in 15 s in case the shell
    // wasn't fully initialised yet (common on fast-boot / auto-login).
    spawn_for_existing_sessions();
    SetTimer(hwnd, TIMER_BOOT_RETRY, 15_000, None);

    // Message loop
    let mut msg: MSG = zeroed();
    while GetMessageW(&mut msg, None, 0, 0).as_bool() {
        let _ = TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    let _ = WTSUnRegisterSessionNotification(hwnd);
}

/// Enumerate active sessions and spawn the tray app in each one.
unsafe fn spawn_for_existing_sessions() {
    let mut session_info = std::ptr::null_mut();
    let mut count: u32 = 0;

    if WTSEnumerateSessionsW(None, 0, 1, &mut session_info, &mut count).is_ok() {
        let sessions = std::slice::from_raw_parts(session_info, count as usize);
        for session in sessions {
            // Session 0 is the service/console session — skip it
            if session.State == WTSActive && session.SessionId != 0 {
                spawn_in_session(session.SessionId);
            }
        }
        WTSFreeMemory(session_info as *mut _);
    }
}

/// Spawn the tray app inside a specific user session.
unsafe fn spawn_in_session(session_id: u32) {
    let mut token = HANDLE::default();
    if WTSQueryUserToken(session_id, &mut token).is_err() {
        eprintln!("WTSQueryUserToken failed for session {}", session_id);
        return;
    }

    // Build an environment block for this user
    let mut env_block: *mut std::ffi::c_void = std::ptr::null_mut();
    let _ = CreateEnvironmentBlock(&mut env_block, token, BOOL::from(false));

    // Get own executable path
    let mut exe_path = [0u16; 32768];
    let len = GetModuleFileNameW(None, &mut exe_path);
    if len == 0 {
        if !env_block.is_null() { let _ = DestroyEnvironmentBlock(env_block); }
        let _ = CloseHandle(token);
        return;
    }

    // Desktop must be set so the process appears on the user's interactive desktop
    let mut desktop: Vec<u16> = "winsta0\\default\0".encode_utf16().collect();

    let mut si: STARTUPINFOW = zeroed();
    si.cb = std::mem::size_of::<STARTUPINFOW>() as u32;
    si.lpDesktop = PWSTR(desktop.as_mut_ptr());

    let mut pi: PROCESS_INFORMATION = zeroed();

    // Command line must be a mutable buffer; no extra args — normal tray mode
    let mut cmd_line: Vec<u16> = (String::from("\"")
        + &String::from_utf16_lossy(&exe_path[..len as usize])
        + "\"")
        .encode_utf16()
        .chain(std::iter::once(0u16))
        .collect();

    let env_param = if env_block.is_null() { None } else { Some(env_block as *const std::ffi::c_void) };

    let _ = CreateProcessAsUserW(
        token,
        PCWSTR(exe_path.as_ptr()),
        PWSTR(cmd_line.as_mut_ptr()),
        None,
        None,
        BOOL::from(false),
        CREATE_UNICODE_ENVIRONMENT,
        env_param,
        None,
        &si,
        &mut pi,
    );

    if !pi.hProcess.is_invalid() { let _ = CloseHandle(pi.hProcess); }
    if !pi.hThread.is_invalid()  { let _ = CloseHandle(pi.hThread);  }
    if !env_block.is_null()      { let _ = DestroyEnvironmentBlock(env_block); }
    let _ = CloseHandle(token);
}

unsafe extern "system" fn service_window_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_WTSSESSION_CHANGE => {
            match wparam.0 {
                // Logon / console connect / unlock — all mean a user desktop is now active
                WTS_SESSION_LOGON | WTS_CONSOLE_CONNECT | WTS_SESSION_UNLOCK => {
                    let session_id = lparam.0 as u32;
                    if session_id != 0 {
                        spawn_in_session(session_id);
                    }
                }
                _ => {}
            }
        }
        WM_TIMER if wparam.0 == TIMER_BOOT_RETRY => {
            // One-shot retry to catch sessions that weren't ready at service start
            KillTimer(hwnd, TIMER_BOOT_RETRY).ok();
            spawn_for_existing_sessions();
        }
        _ => {}
    }
    DefWindowProcW(hwnd, msg, wparam, lparam)
}
