//! CLI installer / uninstaller for headless / remote deployments.
//! Usage:
//!   screen-time-manager.exe --install [--dir <path>] [--nssm <path>]
//!   screen-time-manager.exe --uninstall [--dir <path>] [--nssm <path>]

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::SystemTime;

// ── logging ──────────────────────────────────────────────────────────────────

fn ts() -> String {
    // Simple wall-clock timestamp using seconds since UNIX epoch
    let secs = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let (h, m, s) = (secs / 3600 % 24, secs / 60 % 60, secs % 60);
    format!("{:02}:{:02}:{:02}", h, m, s)
}

fn log_line(tag: &str, msg: &str) {
    println!("[{}] [{}] {}", ts(), tag, msg);
}

macro_rules! info  { ($($a:tt)*) => { log_line("INFO ", &format!($($a)*)) } }
macro_rules! ok    { ($($a:tt)*) => { log_line(" OK  ", &format!($($a)*)) } }
macro_rules! warn  { ($($a:tt)*) => { log_line("WARN ", &format!($($a)*)) } }
macro_rules! error { ($($a:tt)*) => { log_line("ERROR", &format!($($a)*)) } }

// ── helpers ───────────────────────────────────────────────────────────────────

fn nssm_run(nssm: &Path, args: &[&str]) -> bool {
    match Command::new(nssm).args(args).output() {
        Ok(out) => {
            let stdout = String::from_utf8_lossy(&out.stdout);
            let stderr = String::from_utf8_lossy(&out.stderr);
            if !stdout.trim().is_empty() { info!("{}", stdout.trim()); }
            if !stderr.trim().is_empty() { warn!("{}", stderr.trim()); }
            out.status.success()
        }
        Err(e) => {
            error!("nssm {:?} failed: {}", args, e);
            false
        }
    }
}

fn set_acl(dir: &Path, reset_only: bool) {
    // Restore inherited ACLs (for cleanup) or lock down (for install)
    let script = if reset_only {
        format!(
            "$p = '{}'; $a = Get-Acl $p; $a.SetAccessRuleProtection($false, $true); Set-Acl $p $a",
            dir.display()
        )
    } else {
        format!(
            r#"$p = '{dir}';
$a = Get-Acl $p;
$a.SetAccessRuleProtection($true, $false);
$a.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Administrators','FullControl','ContainerInherit,ObjectInherit','None','Allow')));
$a.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM','FullControl','ContainerInherit,ObjectInherit','None','Allow')));
$a.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Users','ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')));
Set-Acl $p $a"#,
            dir = dir.display()
        )
    };

    match Command::new("powershell")
        .args(["-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", &script])
        .output()
    {
        Ok(out) if out.status.success() => ok!("ACL set on {}", dir.display()),
        Ok(out) => warn!("ACL on {}: {}", dir.display(), String::from_utf8_lossy(&out.stderr).trim()),
        Err(e)  => warn!("ACL on {}: {}", dir.display(), e),
    }
}

fn lock_db_dir(db_dir: &Path) {
    let script = format!(
        r#"$p = '{db}';
New-Item -ItemType Directory -Path $p -Force | Out-Null;
$a = Get-Acl $p;
$a.SetAccessRuleProtection($true, $false);
$a.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Administrators','FullControl','ContainerInherit,ObjectInherit','None','Allow')));
$a.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM','FullControl','ContainerInherit,ObjectInherit','None','Allow')));
Set-Acl $p $a"#,
        db = db_dir.display()
    );
    match Command::new("powershell")
        .args(["-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", &script])
        .output()
    {
        Ok(out) if out.status.success() => ok!("DB dir locked: {}", db_dir.display()),
        Ok(out) => warn!("DB dir ACL: {}", String::from_utf8_lossy(&out.stderr).trim()),
        Err(e)  => warn!("DB dir ACL: {}", e),
    }
}

// ── arg parsing ───────────────────────────────────────────────────────────────

fn arg_value<'a>(args: &'a [String], flag: &str) -> Option<&'a str> {
    args.windows(2)
        .find(|w| w[0] == flag)
        .map(|w| w[1].as_str())
}

fn default_install_dir() -> PathBuf {
    let pf = std::env::var("ProgramFiles").unwrap_or_else(|_| "C:\\Program Files".into());
    PathBuf::from(pf).join("ScreenTimeManager")
}

fn default_nssm(install_dir: &Path) -> PathBuf {
    // Prefer nssm.exe next to this exe (same dir as the current process)
    if let Ok(mut p) = std::env::current_exe() {
        p.pop();
        let candidate = p.join("nssm.exe");
        if candidate.exists() { return candidate; }
    }
    install_dir.join("nssm.exe")
}

// ── public API ────────────────────────────────────────────────────────────────

pub fn run_install(args: &[String]) {
    allocate_console();

    let install_dir = arg_value(args, "--dir")
        .map(PathBuf::from)
        .unwrap_or_else(default_install_dir);

    let nssm_src = arg_value(args, "--nssm")
        .map(PathBuf::from)
        .unwrap_or_else(|| default_nssm(&install_dir));

    let svc = "ScreenTimeManager";

    println!();
    info!("=== Screen Time Manager CLI Installer ===");
    info!("Install dir : {}", install_dir.display());
    info!("NSSM source : {}", nssm_src.display());
    println!();

    // 1. Create install directory
    info!("Creating install directory...");
    if let Err(e) = fs::create_dir_all(&install_dir) {
        error!("Cannot create {}: {}", install_dir.display(), e);
        std::process::exit(1);
    }
    ok!("{}", install_dir.display());

    // 2. Copy this executable
    let exe_dest = install_dir.join("screen-time-manager.exe");
    info!("Copying executable...");
    let exe_src = std::env::current_exe().expect("cannot resolve own path");
    if let Err(e) = fs::copy(&exe_src, &exe_dest) {
        error!("Copy exe: {}", e);
        std::process::exit(1);
    }
    ok!("{} -> {}", exe_src.display(), exe_dest.display());

    // 3. Copy nssm.exe
    let nssm_dest = install_dir.join("nssm.exe");
    if nssm_src != nssm_dest {
        info!("Copying nssm.exe...");
        if !nssm_src.exists() {
            error!("nssm.exe not found at {}. Provide --nssm <path>", nssm_src.display());
            std::process::exit(1);
        }
        if let Err(e) = fs::copy(&nssm_src, &nssm_dest) {
            error!("Copy nssm: {}", e);
            std::process::exit(1);
        }
        ok!("{} -> {}", nssm_src.display(), nssm_dest.display());
    }

    // 4. Remove any existing service
    info!("Removing existing service (if any)...");
    nssm_run(&nssm_dest, &["stop",   svc]);
    std::thread::sleep(std::time::Duration::from_secs(2));
    nssm_run(&nssm_dest, &["remove", svc, "confirm"]);
    std::thread::sleep(std::time::Duration::from_secs(1));

    // 5. Install service
    info!("Installing service via NSSM...");
    let exe_dest_str = exe_dest.to_string_lossy().into_owned();
    if !nssm_run(&nssm_dest, &["install", svc, &exe_dest_str, "--service"]) {
        error!("NSSM install failed");
        std::process::exit(1);
    }
    let dir_str  = install_dir.to_string_lossy().into_owned();
    let logs_str = install_dir.join("logs").to_string_lossy().into_owned();
    nssm_run(&nssm_dest, &["set", svc, "AppDirectory",    &dir_str]);
    nssm_run(&nssm_dest, &["set", svc, "ObjectName",      "LocalSystem"]);
    nssm_run(&nssm_dest, &["set", svc, "DisplayName",     "Screen Time Manager"]);
    nssm_run(&nssm_dest, &["set", svc, "Description",     "Screen Time Manager - Manages daily computer time limits"]);
    nssm_run(&nssm_dest, &["set", svc, "Start",           "SERVICE_AUTO_START"]);
    ok!("Service registered");

    // 6. Log rotation
    info!("Configuring log rotation...");
    let _ = fs::create_dir_all(install_dir.join("logs"));
    let stdout_log = format!("{}\\stdout.log", logs_str);
    let stderr_log = format!("{}\\stderr.log", logs_str);
    nssm_run(&nssm_dest, &["set", svc, "AppStdout",        &stdout_log]);
    nssm_run(&nssm_dest, &["set", svc, "AppStderr",        &stderr_log]);
    nssm_run(&nssm_dest, &["set", svc, "AppRotateFiles",   "1"]);
    nssm_run(&nssm_dest, &["set", svc, "AppRotateSeconds", "86400"]);
    ok!("Logs -> {}", logs_str);

    // 7. Lock down directories
    info!("Applying directory permissions...");
    set_acl(&install_dir, false);
    let db_dir_base = std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".into());
    let db_dir = PathBuf::from(db_dir_base).join("ScreenTimeManager");
    lock_db_dir(&db_dir);

    // 8. Start service
    info!("Starting service...");
    if nssm_run(&nssm_dest, &["start", svc]) {
        ok!("Service started");
    } else {
        warn!("Service may need to be started manually");
    }

    println!();
    ok!("=== Installation complete ===");
    println!("  Install dir : {}", install_dir.display());
    println!("  Database    : {}", db_dir.display());
    println!();
}

pub fn run_uninstall(args: &[String]) {
    allocate_console();

    let install_dir = arg_value(args, "--dir")
        .map(PathBuf::from)
        .unwrap_or_else(default_install_dir);

    let nssm = arg_value(args, "--nssm")
        .map(PathBuf::from)
        .unwrap_or_else(|| install_dir.join("nssm.exe"));

    let svc = "ScreenTimeManager";

    println!();
    info!("=== Screen Time Manager CLI Uninstaller ===");
    info!("Install dir : {}", install_dir.display());
    println!();

    // 1. Stop + remove service via NSSM
    info!("Stopping service...");
    nssm_run(&nssm, &["stop", svc]);
    std::thread::sleep(std::time::Duration::from_secs(2));
    info!("Removing service...");
    nssm_run(&nssm, &["remove", svc, "confirm"]);
    std::thread::sleep(std::time::Duration::from_secs(1));

    // 2. Kill any remaining processes
    info!("Killing remaining processes...");
    let _ = Command::new("taskkill")
        .args(["/f", "/im", "screen-time-manager.exe"])
        .output();
    ok!("Done");

    // 3. Reset ACLs so files can be deleted
    info!("Resetting directory permissions...");
    if install_dir.exists() { set_acl(&install_dir, true); }
    let db_dir_base = std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".into());
    let db_dir = PathBuf::from(&db_dir_base).join("ScreenTimeManager");
    if db_dir.exists() { set_acl(&db_dir, true); }

    // 4. Remove install directory
    info!("Removing install directory...");
    if install_dir.exists() {
        match fs::remove_dir_all(&install_dir) {
            Ok(_)  => ok!("{} removed", install_dir.display()),
            Err(e) => warn!("Could not remove {}: {}", install_dir.display(), e),
        }
    }

    println!();
    ok!("=== Uninstall complete ===");
    println!("  Note: database in {} was not deleted.", db_dir.display());
    println!("  Delete it manually if you want to remove all data.");
    println!();
}

// ── console allocation (needed because windows_subsystem = "windows") ─────────

fn allocate_console() {
    #[cfg(target_os = "windows")]
    unsafe {
        windows::Win32::System::Console::AllocConsole().ok();
    }
}
