//! Shared constants.

/// Custom window message for tray icon events.
pub const WM_TRAYICON: u32 = 0x8001;

// Tray menu item IDs.
pub const IDM_ABOUT: u16 = 1001;
pub const IDM_QUIT: u16 = 1002;
pub const IDM_SHOW_OVERLAY: u16 = 1003;
pub const IDM_SHOW_BLOCKING: u16 = 1004;
pub const IDM_SETTINGS: u16 = 1005;
pub const IDM_TODAYS_STATS: u16 = 1006;
pub const IDM_PAUSE_TOGGLE: u16 = 1007;
pub const IDM_EXTEND_15: u16 = 1008;
pub const IDM_EXTEND_45: u16 = 1009;
pub const IDM_CHECK_UPDATES: u16 = 1010;

/// Single-instance mutex name.
pub const MUTEX_NAME: &str = "Local\\ScreenTimeManager_SingleInstance_7F3A9B2E";

// Colors are COLORREF values (0x00BBGGRR).
pub const COLOR_OVERLAY_BG: u32 = 0x002E1E1E; // dark navy
pub const COLOR_PANEL_BG: u32 = 0x00402828; // navy panel
pub const COLOR_ACCENT: u32 = 0x00756CE0; // soft red
pub const COLOR_TEXT_WHITE: u32 = 0x00FFFFFF;
pub const COLOR_TEXT_LIGHT: u32 = 0x00BBBBBB;
pub const COLOR_TEXT_MUTED: u32 = 0x00888888;
pub const COLOR_ERROR: u32 = 0x004444FF; // red
pub const COLOR_GOOD: u32 = 0x0000AA00; // green
pub const COLOR_SHUTDOWN_WARN: u32 = 0x000000FF; // bright red
pub const COLOR_OVERLAY_BANNER: u32 = 0x00663300; // dark orange

// Dark dialog theme.
pub const DARK_BG: u32 = 0x001E1E2E;
pub const DARK_EDIT_BG: u32 = 0x00282840;

// Mini overlay backgrounds.
pub const MINI_BG: u32 = 0x00222222;
pub const MINI_BG_PAUSED: u32 = 0x00332200; // brown tint
pub const MINI_BG_IDLE: u32 = 0x00333333; // grey
pub const COLOR_PAUSE_TEXT: u32 = 0x0066CCFF; // light blue
