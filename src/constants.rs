//! Constants module for Screen Time Manager
//! Contains all shared constants used across the application

// Custom message ID for tray icon events
pub const WM_TRAYICON: u32 = 0x8001;

// Menu item IDs
pub const IDM_ABOUT: u16 = 1001;
pub const IDM_QUIT: u16 = 1002;
pub const IDM_SHOW_OVERLAY: u16 = 1003;
pub const IDM_SHOW_BLOCKING: u16 = 1004;
pub const IDM_SETTINGS: u16 = 1005;
pub const IDM_TODAYS_STATS: u16 = 1006;
pub const IDM_PAUSE_TOGGLE: u16 = 1007;
pub const IDM_EXTEND_15: u16 = 1008;
pub const IDM_EXTEND_45: u16 = 1009;

// Mutex name for single instance
pub const MUTEX_NAME: &str = "Local\\ScreenTimeManager_SingleInstance_7F3A9B2E";

// Colors (COLORREF / BGR format: 0x00BBGGRR)
pub const COLOR_OVERLAY_BG: u32 = 0x002E1E1E;      // Deep navy (RGB 30,30,46)
pub const COLOR_PANEL_BG: u32 = 0x00402828;        // Medium navy panel (RGB 40,40,64)
pub const COLOR_ACCENT: u32 = 0x00756CE0;          // Soft red accent (RGB 224,108,117)
pub const COLOR_TEXT_WHITE: u32 = 0x00FFFFFF;
pub const COLOR_TEXT_LIGHT: u32 = 0x00BBBBBB;
pub const COLOR_ERROR: u32 = 0x004444FF;           // Red (BGR)
pub const COLOR_SHUTDOWN_WARN: u32 = 0x000000FF;   // Bright red for imminent shutdown (BGR)
pub const COLOR_OVERLAY_BANNER: u32 = 0x00663300;  // Dark orange-navy banner background (BGR)
