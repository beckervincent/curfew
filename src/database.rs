//! SQLite-backed settings and daily state.

use rusqlite::{params, Connection};
use std::path::PathBuf;
use std::sync::Mutex;

pub static DB_CONNECTION: Mutex<Option<Connection>> = Mutex::new(None);

pub const WEEKDAY_KEYS: [&str; 7] = [
    "limit_monday",
    "limit_tuesday",
    "limit_wednesday",
    "limit_thursday",
    "limit_friday",
    "limit_saturday",
    "limit_sunday",
];

pub const WEEKDAY_NAMES: [&str; 7] = [
    "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
];

/// Database path under ProgramData (system-wide, writable per install ACLs).
pub fn get_database_path() -> PathBuf {
    let base = std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".into());
    let db_dir = PathBuf::from(base).join("ScreenTimeManager");
    let _ = std::fs::create_dir_all(&db_dir);
    db_dir.join("data.db")
}

/// Open the database, creating defaults as needed.
/// A corrupt database is deleted and recreated rather than failing.
pub fn init_database() -> Result<(), Box<dyn std::error::Error>> {
    let db_path = get_database_path();

    if try_init_database(&db_path).is_err() {
        let _ = std::fs::remove_file(&db_path);
        try_init_database(&db_path)?;
    }

    Ok(())
}

fn try_init_database(db_path: &std::path::Path) -> Result<(), Box<dyn std::error::Error>> {
    let conn = Connection::open(db_path)?;

    conn.execute(
        "CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )",
        [],
    )?;

    let defaults = [
        ("passcode", "0000"),
        ("limit_monday", "120"),
        ("limit_tuesday", "120"),
        ("limit_wednesday", "120"),
        ("limit_thursday", "120"),
        ("limit_friday", "180"),
        ("limit_saturday", "240"),
        ("limit_sunday", "240"),
        ("warning1_minutes", "10"),
        ("warning1_message", "10 minutes remaining!"),
        ("warning2_minutes", "5"),
        ("warning2_message", "5 minutes remaining!"),
        ("blocking_message", "Your screen time limit has been reached."),
        ("pause_enabled", "1"),
        ("pause_daily_budget", "45"),
        ("pause_max_duration", "20"),
        ("pause_cooldown", "15"),
        ("pause_min_active_time", "10"),
        ("lock_screen_timeout", "600"),
        ("idle_enabled", "1"),
        ("idle_timeout_minutes", "5"),
        ("auto_update_enabled", "1"),
    ];

    for (key, value) in defaults {
        conn.execute(
            "INSERT OR IGNORE INTO settings (key, value) VALUES (?1, ?2)",
            params![key, value],
        )?;
    }

    // Drop per-day rows from previous days so the table doesn't grow forever.
    let today = get_today_date();
    for prefix in ["remaining_time_", "pause_used_", "pause_log_", "session_active_"] {
        let _ = conn.execute(
            "DELETE FROM settings WHERE key LIKE ?1 || '%' AND key != ?1 || ?2",
            params![prefix, today],
        );
    }

    *DB_CONNECTION.lock().unwrap() = Some(conn);
    Ok(())
}

pub fn get_passcode() -> Option<String> {
    get_setting("passcode")
}

pub fn get_setting(key: &str) -> Option<String> {
    let guard = DB_CONNECTION.lock().ok()?;
    guard
        .as_ref()?
        .query_row(
            "SELECT value FROM settings WHERE key = ?1",
            params![key],
            |row| row.get(0),
        )
        .ok()
}

pub fn set_setting(key: &str, value: &str) -> bool {
    if let Ok(guard) = DB_CONNECTION.lock() {
        if let Some(conn) = guard.as_ref() {
            return conn
                .execute(
                    "INSERT OR REPLACE INTO settings (key, value) VALUES (?1, ?2)",
                    params![key, value],
                )
                .is_ok();
        }
    }
    false
}

fn get_setting_parsed<T: std::str::FromStr>(key: &str, default: T) -> T {
    get_setting(key).and_then(|s| s.parse().ok()).unwrap_or(default)
}

/// Daily limit in minutes for a weekday (0 = Monday … 6 = Sunday).
pub fn get_daily_limit(weekday: u32) -> u32 {
    match WEEKDAY_KEYS.get(weekday as usize) {
        Some(key) => get_setting_parsed(key, 120),
        None => 120,
    }
}

/// Warning threshold (minutes) and message for warning 1 or 2.
pub fn get_warning_config(warning_num: u32) -> (u32, String) {
    let minutes = get_setting_parsed(&format!("warning{}_minutes", warning_num), 5);
    let message = get_setting(&format!("warning{}_message", warning_num))
        .unwrap_or_else(|| format!("{} minutes remaining!", minutes));
    (minutes, message)
}

pub fn get_blocking_message() -> String {
    get_setting("blocking_message")
        .unwrap_or_else(|| "Your screen time limit has been reached.".to_string())
}

/// Current local date as "YYYY-MM-DD".
pub fn get_today_date() -> String {
    use windows::Win32::System::SystemInformation::GetLocalTime;
    let st = unsafe { GetLocalTime() };
    format!("{:04}-{:02}-{:02}", st.wYear, st.wMonth, st.wDay)
}

pub fn save_remaining_time(seconds: i32) {
    set_setting(&format!("remaining_time_{}", get_today_date()), &seconds.to_string());
}

pub fn load_remaining_time() -> Option<i32> {
    get_setting(&format!("remaining_time_{}", get_today_date())).and_then(|s| s.parse().ok())
}

/// Current weekday (0 = Monday … 6 = Sunday).
pub fn get_current_weekday() -> u32 {
    use windows::Win32::System::SystemInformation::GetLocalTime;
    let st = unsafe { GetLocalTime() };
    // Windows uses 0 = Sunday.
    if st.wDayOfWeek == 0 {
        6
    } else {
        (st.wDayOfWeek - 1) as u32
    }
}

/// Seconds the lock screen waits before shutting the machine down.
pub fn get_lock_screen_timeout() -> i32 {
    get_setting_parsed("lock_screen_timeout", 600)
}

// ── Pause mode ───────────────────────────────────────────────────────────────

pub fn is_pause_enabled() -> bool {
    get_setting("pause_enabled").map(|s| s == "1").unwrap_or(true)
}

pub struct PauseConfig {
    pub daily_budget_minutes: u32,
    pub max_duration_minutes: u32,
    pub cooldown_minutes: u32,
    pub min_active_time_minutes: u32,
}

pub fn get_pause_config() -> PauseConfig {
    PauseConfig {
        daily_budget_minutes: get_setting_parsed("pause_daily_budget", 45),
        max_duration_minutes: get_setting_parsed("pause_max_duration", 20),
        cooldown_minutes: get_setting_parsed("pause_cooldown", 15),
        min_active_time_minutes: get_setting_parsed("pause_min_active_time", 10),
    }
}

/// Pause time used today, in seconds.
pub fn get_pause_used_today() -> i32 {
    get_setting_parsed(&format!("pause_used_{}", get_today_date()), 0)
}

pub fn save_pause_used_today(seconds: i32) {
    set_setting(&format!("pause_used_{}", get_today_date()), &seconds.to_string());
}

/// Unix timestamp of the last pause end (0 = never).
pub fn get_last_pause_end() -> i64 {
    get_setting_parsed("pause_last_end_timestamp", 0)
}

pub fn save_last_pause_end(timestamp: i64) {
    set_setting("pause_last_end_timestamp", &timestamp.to_string());
}

pub fn get_current_timestamp() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// Active (non-paused) seconds accumulated today.
pub fn get_session_active_time() -> i32 {
    get_setting_parsed(&format!("session_active_{}", get_today_date()), 0)
}

pub fn save_session_active_time(seconds: i32) {
    set_setting(&format!("session_active_{}", get_today_date()), &seconds.to_string());
}

/// Append a "HH:MM:SS:<duration>s" entry to today's pause log.
pub fn log_pause_event(duration_seconds: i32) {
    use windows::Win32::System::SystemInformation::GetLocalTime;

    let st = unsafe { GetLocalTime() };
    let key = format!("pause_log_{}", get_today_date());
    let entry = format!(
        "{:02}:{:02}:{:02}:{}s",
        st.wHour, st.wMinute, st.wSecond, duration_seconds
    );

    let updated = match get_setting(&key) {
        Some(existing) if !existing.is_empty() => format!("{},{}", existing, entry),
        _ => entry,
    };
    set_setting(&key, &updated);
}

pub fn get_pause_log_today() -> Vec<String> {
    get_setting(&format!("pause_log_{}", get_today_date()))
        .map(|s| s.split(',').map(|e| e.to_string()).collect())
        .unwrap_or_default()
}

// ── Idle detection ───────────────────────────────────────────────────────────

pub fn is_idle_enabled() -> bool {
    get_setting("idle_enabled").map(|s| s == "1").unwrap_or(true)
}

/// Idle timeout in minutes (minimum 1).
pub fn get_idle_timeout_minutes() -> u32 {
    get_setting_parsed("idle_timeout_minutes", 5).max(1)
}
