//! DPI-aware UI scaling.

use std::sync::atomic::{AtomicU32, Ordering};
use windows::Win32::UI::HiDpi::GetDpiForSystem;

/// Cached system DPI (0 = not yet initialized).
static CACHED_DPI: AtomicU32 = AtomicU32::new(0);

/// 96 DPI = 100% scaling.
const STANDARD_DPI: u32 = 96;

/// Cache the system DPI. Call once at startup, after setting DPI awareness.
pub fn init_dpi() {
    let dpi = unsafe { GetDpiForSystem() };
    CACHED_DPI.store(dpi, Ordering::SeqCst);
}

pub fn get_dpi() -> u32 {
    match CACHED_DPI.load(Ordering::SeqCst) {
        0 => {
            let dpi = unsafe { GetDpiForSystem() };
            CACHED_DPI.store(dpi, Ordering::SeqCst);
            dpi
        }
        cached => cached,
    }
}

/// Scale a 96-DPI value to the current DPI (integer math, rounded).
pub fn scale(value: i32) -> i32 {
    let dpi = get_dpi();
    ((value as i64 * dpi as i64 + STANDARD_DPI as i64 / 2) / STANDARD_DPI as i64) as i32
}
