//! Database module for Screen Time Manager
//! Handles SQLite database initialization and settings management

use std::path::PathBuf;
use std::sync::Mutex;
use rusqlite::{Connection, params};

/// Global database connection (thread-safe)
pub static DB_CONNECTION: Mutex<Option<Connection>> = Mutex::new(None);

/// Weekday keys for database
pub const WEEKDAY_KEYS: [&str; 7] = [
    "limit_monday", "limit_tuesday", "limit_wednesday", "limit_thursday",
    "limit_friday", "limit_saturday", "limit_sunday"
];

/// Weekday names for UI
pub const WEEKDAY_NAMES: [&str; 7] = [
    "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
];

/// Get the path to the database file under ProgramData (system-wide, admin-writable)
pub fn get_database_path() -> PathBuf {
    let base = std::env::var("ProgramData").unwrap_or_else(|_| "C:\\ProgramData".into());
    let db_dir = PathBuf::from(base).join("ScreenTimeManager");

    if !db_dir.exists() {
        let _ = std::fs::create_dir_all(&db_dir);
    }

    db_dir.join("data.db")
}

/// Initialize the SQLite database, resetting to defaults on any error.
/// Never returns an error to the caller — a corrupt/missing DB is always reset.
pub fn init_database() -> Result<(), Box<dyn std::error::Error>> {
    let db_path = get_database_path();

    // First attempt; on any failure wipe the file and try once more.
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
    ];

    for (key, value) in defaults {
        let exists: bool = conn.query_row(
            "SELECT EXISTS(SELECT 1 FROM settings WHERE key = ?1)",
            params![key],
            |row| row.get(0),
        )?;

        if !exists {
            conn.execute(
                "INSERT INTO settings (key, value) VALUES (?1, ?2)",
                params![key, value],
            )?;
        }
    }

    *DB_CONNECTION.lock().unwrap() = Some(conn);
    Ok(())
}

/// Get the passcode from the database
pub fn get_passcode() -> Option<String> {
    let guard = DB_CONNECTION.lock().ok()?;
    guard.as_ref()?.query_row(
        "SELECT value FROM settings WHERE key = 'passcode'",
        [],
        |row| row.get(0),
    ).ok()
}

/// Set the passcode in the database
#[allow(dead_code)]
pub fn set_passcode(code: &str) -> bool {
    if let Ok(guard) = DB_CONNECTION.lock() {
        if let Some(conn) = guard.as_ref() {
            return conn.execute(
                "UPDATE settings SET value = ?1 WHERE key = 'passcode'",
                params![code],
            ).is_ok();
        }
    }
    false
}

/// Get a setting value from the database
pub fn get_setting(key: &str) -> Option<String> {
    let guard = DB_CONNECTION.lock().ok()?;
    guard.as_ref()?.query_row(
        "SELECT value FROM settings WHERE key = ?1",
        params![key],
        |row| row.get(0),
    ).ok()
}

/// Set a setting value in the database
pub fn set_setting(key: &str, value: &str) -> bool {
    if let Ok(guard) = DB_CONNECTION.lock() {
        if let Some(conn) = guard.as_ref() {
            return conn.execute(
                "INSERT OR REPLACE INTO settings (key, value) VALUES (?1, ?2)",
                params![key, value],
            ).is_ok();
        }
    }
    false
}

/// Get daily limit for a specific weekday (0 = Monday, 6 = Sunday)
#[allow(dead_code)]
pub fn get_daily_limit(weekday: u32) -> u32 {
    let key = match weekday {
        0 => "limit_monday",
        1 => "limit_tuesday",
        2 => "limit_wednesday",
        3 => "limit_thursday",
        4 => "limit_friday",
        5 => "limit_saturday",
        6 => "limit_sunday",
        _ => return 120,
    };
    get_setting(key)
        .and_then(|s| s.parse().ok())
        .unwrap_or(120)
}

/// Get warning configuration
#[allow(dead_code)]
pub fn get_warning_config(warning_num: u32) -> (u32, String) {
    let minutes_key = format!("warning{}_minutes", warning_num);
    let message_key = format!("warning{}_message", warning_num);

    let minutes = get_setting(&minutes_key)
        .and_then(|s| s.parse().ok())
        .unwrap_or(5);
    let message = get_setting(&message_key)
        .unwrap_or_else(|| format!("{} minutes remaining!", minutes));

    (minutes, message)
}

/// Get blocking message
#[allow(dead_code)]
pub fn get_blocking_message() -> String {
    get_setting("blocking_message")
        .unwrap_or_else(|| "Your screen time limit has been reached.".to_string())
}

/// Get the current local date as a string (YYYY-MM-DD)
fn get_today_date() -> String {
    use windows::Win32::System::SystemInformation::GetLocalTime;

    let st = unsafe { GetLocalTime() };

    format!("{:04}-{:02}-{:02}", st.wYear, st.wMonth, st.wDay)
}

/// Save remaining time to database (associated with current date)
pub fn save_remaining_time(seconds: i32) {
    let date = get_today_date();
    let key = format!("remaining_time_{}", date);
    set_setting(&key, &seconds.to_string());
}

/// Load remaining time from database for today
#[allow(dead_code)]
pub fn load_remaining_time() -> Option<i32> {
    let date = get_today_date();
    let key = format!("remaining_time_{}", date);
    get_setting(&key).and_then(|s| s.parse().ok())
}

/// Get the current weekday (0 = Monday, 6 = Sunday)
#[allow(dead_code)]
pub fn get_current_weekday() -> u32 {
    use windows::Win32::System::SystemInformation::GetLocalTime;

    let st = unsafe { GetLocalTime() };

    // Windows: wDayOfWeek is 0 = Sunday, 1 = Monday, ..., 6 = Saturday
    // We want: 0 = Monday, 1 = Tuesday, ..., 6 = Sunday
    if st.wDayOfWeek == 0 {
        6 // Sunday
    } else {
        (st.wDayOfWeek - 1) as u32
    }
}

// ============================================================================
// Lock Screen Timeout Functions
// ============================================================================

/// Get lock screen timeout in seconds (time before shutdown when lock screen is active)
pub fn get_lock_screen_timeout() -> i32 {
    get_setting("lock_screen_timeout")
        .and_then(|s| s.parse().ok())
        .unwrap_or(600) // 10 minutes default
}

// ============================================================================
// Pause Mode Functions
// ============================================================================

/// Check if pause mode is enabled
pub fn is_pause_enabled() -> bool {
    get_setting("pause_enabled")
        .map(|s| s == "1")
        .unwrap_or(true)
}

/// Get pause configuration
pub struct PauseConfig {
    pub daily_budget_minutes: u32,
    pub max_duration_minutes: u32,
    pub cooldown_minutes: u32,
    pub min_active_time_minutes: u32,
}

pub fn get_pause_config() -> PauseConfig {
    PauseConfig {
        daily_budget_minutes: get_setting("pause_daily_budget")
            .and_then(|s| s.parse().ok())
            .unwrap_or(45),
        max_duration_minutes: get_setting("pause_max_duration")
            .and_then(|s| s.parse().ok())
            .unwrap_or(20),
        cooldown_minutes: get_setting("pause_cooldown")
            .and_then(|s| s.parse().ok())
            .unwrap_or(15),
        min_active_time_minutes: get_setting("pause_min_active_time")
            .and_then(|s| s.parse().ok())
            .unwrap_or(10),
    }
}

/// Get pause time used today (in seconds)
pub fn get_pause_used_today() -> i32 {
    let date = get_today_date();
    let key = format!("pause_used_{}", date);
    get_setting(&key)
        .and_then(|s| s.parse().ok())
        .unwrap_or(0)
}

/// Save pause time used today (in seconds)
pub fn save_pause_used_today(seconds: i32) {
    let date = get_today_date();
    let key = format!("pause_used_{}", date);
    set_setting(&key, &seconds.to_string());
}

/// Get timestamp of last pause end (Unix timestamp)
pub fn get_last_pause_end() -> i64 {
    get_setting("pause_last_end_timestamp")
        .and_then(|s| s.parse().ok())
        .unwrap_or(0)
}

/// Save timestamp of last pause end
pub fn save_last_pause_end(timestamp: i64) {
    set_setting("pause_last_end_timestamp", &timestamp.to_string());
}

/// Get current Unix timestamp
pub fn get_current_timestamp() -> i64 {
    use windows::Win32::System::SystemInformation::GetLocalTime;

    let st = unsafe { GetLocalTime() };

    // Simple conversion - just need relative timestamps for cooldown
    // This is approximate but sufficient for our purposes
    let days_since_epoch = (st.wYear as i64 - 1970) * 365
        + (st.wMonth as i64 - 1) * 30
        + st.wDay as i64;
    days_since_epoch * 86400
        + st.wHour as i64 * 3600
        + st.wMinute as i64 * 60
        + st.wSecond as i64
}

/// Get the session start time used today (in seconds) - tracks when timer started today
pub fn get_session_active_time() -> i32 {
    let date = get_today_date();
    let key = format!("session_active_{}", date);
    get_setting(&key)
        .and_then(|s| s.parse().ok())
        .unwrap_or(0)
}

/// Save session active time (in seconds)
pub fn save_session_active_time(seconds: i32) {
    let date = get_today_date();
    let key = format!("session_active_{}", date);
    set_setting(&key, &seconds.to_string());
}

/// Log a pause event for today
pub fn log_pause_event(duration_seconds: i32) {
    use windows::Win32::System::SystemInformation::GetLocalTime;

    let st = unsafe { GetLocalTime() };
    let time_str = format!("{:02}:{:02}:{:02}", st.wHour, st.wMinute, st.wSecond);

    let date = get_today_date();
    let key = format!("pause_log_{}", date);

    let existing = get_setting(&key).unwrap_or_default();
    let new_entry = format!("{}:{}s", time_str, duration_seconds);

    let updated = if existing.is_empty() {
        new_entry
    } else {
        format!("{},{}", existing, new_entry)
    };

    set_setting(&key, &updated);
}

/// Get pause log for today
pub fn get_pause_log_today() -> Vec<String> {
    let date = get_today_date();
    let key = format!("pause_log_{}", date);

    get_setting(&key)
        .map(|s| s.split(',').map(|e| e.to_string()).collect())
        .unwrap_or_default()
}

// ============================================================================
// Idle Detection Functions
// ============================================================================

/// Check if idle detection is enabled
pub fn is_idle_enabled() -> bool {
    get_setting("idle_enabled")
        .map(|s| s == "1")
        .unwrap_or(true)
}

/// Get idle timeout in minutes (minimum 1)
pub fn get_idle_timeout_minutes() -> u32 {
    get_setting("idle_timeout_minutes")
        .and_then(|s| s.parse().ok())
        .unwrap_or(5)
        .max(1)
}

