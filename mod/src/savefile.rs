//! Keeps the mod away from the player's own save: the game's save file name is patched in memory
//! from ER0000.sl2 to ER0000.mec, so the mod plays on a separate save and never touches ER0000.sl2.
//!
//! The game's code and strings are packed on disk, so the name only exists once the game has
//! unpacked itself. We poll the main module until the name shows up, then patch every copy.

use crate::log;
use std::time::Duration;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Memory::{
    VirtualProtect, VirtualQuery, MEMORY_BASIC_INFORMATION, MEM_COMMIT, PAGE_GUARD, PAGE_NOACCESS,
    PAGE_READWRITE,
};

const OLD: &str = "ER0000.sl2";
const NEW: &str = "ER0000.mec";

fn utf16(s: &str) -> Vec<u8> {
    s.encode_utf16().flat_map(|c| c.to_le_bytes()).collect()
}

fn module_range() -> Option<(usize, usize)> {
    unsafe {
        let base = GetModuleHandleW(core::ptr::null()) as usize;
        if base == 0 {
            return None;
        }
        let e_lfanew = *((base + 0x3c) as *const u32) as usize;
        let size_of_image = *((base + e_lfanew + 24 + 56) as *const u32) as usize;
        Some((base, size_of_image))
    }
}

/// Replace every copy of OLD (UTF-16 and ASCII) inside the main module. Returns how many were patched.
fn patch_all() -> usize {
    let Some((base, size)) = module_range() else { return 0 };
    let pats: [(Vec<u8>, Vec<u8>); 2] = [
        (utf16(OLD), utf16(NEW)),
        (OLD.as_bytes().to_vec(), NEW.as_bytes().to_vec()),
    ];
    let mut patched = 0;
    let mut addr = base;
    let end = base + size;
    while addr < end {
        let mut mbi: MEMORY_BASIC_INFORMATION = unsafe { core::mem::zeroed() };
        let got = unsafe { VirtualQuery(addr as *const _, &mut mbi, core::mem::size_of::<MEMORY_BASIC_INFORMATION>()) };
        if got == 0 {
            break;
        }
        let region_start = mbi.BaseAddress as usize;
        let region_end = (region_start + mbi.RegionSize).min(end);
        let readable = mbi.State == MEM_COMMIT && mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD) == 0;
        if readable && region_end > region_start {
            let hay = unsafe { core::slice::from_raw_parts(region_start as *const u8, region_end - region_start) };
            for (old, new) in &pats {
                let mut i = 0;
                while i + old.len() <= hay.len() {
                    if hay[i..i + old.len()] == old[..] {
                        let target = region_start + i;
                        let mut prev = 0u32;
                        unsafe {
                            if VirtualProtect(target as *const _, new.len(), PAGE_READWRITE, &mut prev) != 0 {
                                core::ptr::copy_nonoverlapping(new.as_ptr(), target as *mut u8, new.len());
                                let mut tmp = 0u32;
                                VirtualProtect(target as *const _, new.len(), prev, &mut tmp);
                                patched += 1;
                                log(&format!("savefile: patched copy at {target:#x} ({} bytes)", new.len()));
                            }
                        }
                        i += old.len();
                    } else {
                        i += 1;
                    }
                }
            }
        }
        addr = region_end.max(addr + 1);
    }
    patched
}

pub fn redirect_when_ready() {
    // Poll for up to 90 s: the strings appear once the game has unpacked itself.
    for attempt in 0..180 {
        let n = patch_all();
        if n > 0 {
            log(&format!("savefile: redirect active after attempt {attempt}, {n} copies patched ({OLD} -> {NEW})"));
            // keep watching: a second copy can be created later (e.g. after menus load)
            for _ in 0..60 {
                std::thread::sleep(Duration::from_millis(1000));
                let m = patch_all();
                if m > 0 {
                    log(&format!("savefile: patched {m} late copies"));
                }
            }
            return;
        }
        std::thread::sleep(Duration::from_millis(500));
    }
    log("savefile: NEVER FOUND the save name in the main module. Do not load a character.");
}
