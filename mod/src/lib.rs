//! Tarnished in a Mech: native part of the mod, loaded by ModEngine2.

mod fileredirect;
mod frames_gen;
mod mech;
mod savefile;

use std::io::Write;

/// Where the logs and self-test flag files live. During development a folder next to the mod (labmod/logs) wins when it
/// exists: %LOCALAPPDATA% is virtualized for packaged apps (the Claude desktop app's shell sees a private copy), so a
/// log written by the game started from the user's own launcher was invisible to the tools. Otherwise %LOCALAPPDATA%.
pub fn log_dir() -> std::path::PathBuf {
    let dev = std::path::PathBuf::from("C:/Users/ddean/modtools/labmod/logs");
    if dev.is_dir() {
        return dev;
    }
    std::path::PathBuf::from(std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".into())).join("TarnishedMech").join("logs")
}

pub fn log(msg: &str) {
    let dir = log_dir();
    let _ = std::fs::create_dir_all(&dir);
    if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(dir.join("latest.log")) {
        let _ = writeln!(f, "{msg}");
    }
}

#[no_mangle]
pub extern "system" fn DllMain(
    _h: *mut core::ffi::c_void,
    reason: u32,
    _r: *mut core::ffi::c_void,
) -> i32 {
    if reason == 1 {
        std::panic::set_hook(Box::new(|info| log(&format!("PANIC: {info}"))));
        log("tarnished_mech: DLL attached");
        std::thread::spawn(|| {
            if fileredirect::install() {
                log("fileredirect: all hooks installed");
            } else {
                log("fileredirect: HOOKS INCOMPLETE - do not load a character");
            }
            std::thread::spawn(|| { let _ = std::panic::catch_unwind(mech::start); });
            savefile::redirect_when_ready();
        });
    }
    1
}
