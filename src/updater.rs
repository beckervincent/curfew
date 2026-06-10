//! Automatic updates. Checks the GitHub releases feed, downloads the
//! installer and applies it silently via a scheduled task — the task runs
//! detached from the service, so the update survives the service being
//! stopped mid-install.

use std::ffi::c_void;
use std::path::PathBuf;
use windows::{
    core::{w, PCWSTR},
    Win32::Networking::WinHttp::*,
};

use crate::ui::to_wide;

const API_HOST: &str = "api.github.com";
const RELEASE_PATH: &str = "/repos/beckervincent/curfew/releases/latest";
const TASK_NAME: &str = "CurfewAutoUpdate";

pub const CURRENT_VERSION: &str = env!("CARGO_PKG_VERSION");

/// RAII wrapper for WinHTTP handles.
struct HInternet(*mut c_void);

impl Drop for HInternet {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                let _ = WinHttpCloseHandle(self.0);
            }
        }
    }
}

fn parse_version(v: &str) -> Option<(u32, u32, u32)> {
    let mut parts = v.trim().trim_start_matches('v').split('.');
    Some((
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
        parts.next()?.parse().ok()?,
    ))
}

/// Plain HTTPS GET. Returns the response body.
fn http_get(host: &str, path: &str) -> Result<Vec<u8>, ()> {
    unsafe {
        let session = HInternet(WinHttpOpen(
            w!("curfew-updater"),
            WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            PCWSTR::null(),
            PCWSTR::null(),
            0,
        ));
        if session.0.is_null() {
            return Err(());
        }

        let host_w = to_wide(host);
        let conn = HInternet(WinHttpConnect(
            session.0,
            PCWSTR(host_w.as_ptr()),
            INTERNET_DEFAULT_HTTPS_PORT,
            0,
        ));
        if conn.0.is_null() {
            return Err(());
        }

        let path_w = to_wide(path);
        let request = HInternet(WinHttpOpenRequest(
            conn.0,
            w!("GET"),
            PCWSTR(path_w.as_ptr()),
            PCWSTR::null(),
            PCWSTR::null(),
            std::ptr::null_mut(),
            WINHTTP_FLAG_SECURE,
        ));
        if request.0.is_null() {
            return Err(());
        }

        // GitHub requires a User-Agent header.
        let headers: Vec<u16> = "User-Agent: curfew-updater\r\nAccept: application/vnd.github+json"
            .encode_utf16()
            .collect();

        WinHttpSendRequest(request.0, Some(&headers), None, 0, 0, 0).map_err(|_| ())?;
        WinHttpReceiveResponse(request.0, std::ptr::null_mut()).map_err(|_| ())?;

        let mut body = Vec::new();
        loop {
            let mut available: u32 = 0;
            if WinHttpQueryDataAvailable(request.0, &mut available).is_err() || available == 0 {
                break;
            }
            let mut chunk = vec![0u8; available as usize];
            let mut read: u32 = 0;
            if WinHttpReadData(
                request.0,
                chunk.as_mut_ptr() as *mut c_void,
                available,
                &mut read,
            )
            .is_err()
                || read == 0
            {
                break;
            }
            chunk.truncate(read as usize);
            body.extend_from_slice(&chunk);
        }

        Ok(body)
    }
}

/// Extract a string value from a flat JSON document (no escape handling —
/// tags and asset URLs never contain escapes).
fn extract_json_string(json: &str, key: &str, from: usize) -> Option<(String, usize)> {
    let pat = format!("\"{}\"", key);
    let key_pos = json[from..].find(&pat)? + from + pat.len();
    let rest = &json[key_pos..];
    let colon = rest.find(':')?;
    let rest = rest[colon + 1..].trim_start();
    let rest = rest.strip_prefix('"')?;
    let end = rest.find('"')?;
    let abs_end = key_pos + colon + 1 + (json[key_pos + colon + 1..].len() - rest.len()) + end;
    Some((rest[..end].to_string(), abs_end))
}

/// Latest release: (version tag, installer download URL).
fn fetch_latest_release() -> Result<(String, String), ()> {
    let body = http_get(API_HOST, RELEASE_PATH)?;
    let json = String::from_utf8_lossy(&body);

    let (tag, _) = extract_json_string(&json, "tag_name", 0).ok_or(())?;

    let mut pos = 0;
    while let Some((url, next)) = extract_json_string(&json, "browser_download_url", pos) {
        if url.ends_with(".exe") && url.contains("curfew-setup") {
            return Ok((tag, url));
        }
        pos = next;
    }
    Err(())
}

/// Check whether a newer release exists. Returns its version tag if so.
pub fn check_for_update() -> Result<Option<String>, ()> {
    let (tag, _) = fetch_latest_release()?;
    let latest = parse_version(&tag).ok_or(())?;
    let current = parse_version(CURRENT_VERSION).ok_or(())?;
    Ok((latest > current).then_some(tag))
}

fn update_download_dir() -> PathBuf {
    let base = std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".into());
    PathBuf::from(base).join("ScreenTimeManager").join("update")
}

/// Download the installer and schedule a silent install. Runs in the
/// SYSTEM service, so the scheduled task needs no UAC prompt.
fn download_and_install(url: &str) -> Result<(), ()> {
    use std::os::windows::process::CommandExt;
    use std::process::Command;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;

    let rest = url.strip_prefix("https://").ok_or(())?;
    let (host, path) = rest.split_once('/').ok_or(())?;
    let body = http_get(host, &format!("/{}", path))?;
    // Sanity check: a real installer is megabytes, an error page is not.
    if body.len() < 500_000 {
        return Err(());
    }

    let dir = update_download_dir();
    std::fs::create_dir_all(&dir).map_err(|_| ())?;
    let installer = dir.join("curfew-update.exe");
    std::fs::write(&installer, &body).map_err(|_| ())?;

    let action = format!(
        "\"{}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
        installer.display()
    );
    let created = Command::new("schtasks")
        .args(["/create", "/tn", TASK_NAME, "/tr", &action, "/sc", "once",
               "/st", "00:00", "/ru", "SYSTEM", "/f"])
        .creation_flags(CREATE_NO_WINDOW)
        .status()
        .map_err(|_| ())?;
    if !created.success() {
        return Err(());
    }

    let _ = Command::new("schtasks")
        .args(["/run", "/tn", TASK_NAME])
        .creation_flags(CREATE_NO_WINDOW)
        .status();

    Ok(())
}

fn is_auto_update_enabled() -> bool {
    crate::database::get_setting("auto_update_enabled")
        .map(|s| s == "1")
        .unwrap_or(true)
}

/// Full service-side cycle: check, download, schedule install.
/// Quiet on every failure — it simply retries on the next timer tick.
pub fn run_auto_update_cycle() {
    let _ = crate::database::init_database();
    if !is_auto_update_enabled() {
        return;
    }

    if let Ok(Some(_tag)) = check_for_update() {
        if let Ok((_, url)) = fetch_latest_release() {
            let _ = download_and_install(&url);
        }
    }
}
