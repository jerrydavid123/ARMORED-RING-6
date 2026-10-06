//! Keeps the mod on its own save file. Every native file call that names ER0000.sl2 (or
//! ER0000.sl2.bak) is redirected to ER0000.mec (ER0000.mec.bak) in the same folder, so the
//! player's real save is never opened, written, renamed or deleted by the mod.
//!
//! The hooks sit on ntdll (the lowest user-mode layer), so they catch CreateFileW, CopyFile,
//! MoveFile, ReplaceFile, DeleteFile and direct native calls alike.

use crate::log;
use retour::RawDetour;
use std::collections::HashSet;
use std::ffi::c_void;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Mutex, OnceLock};
use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};

#[repr(C)]
#[derive(Clone, Copy)]
struct UnicodeString {
    length: u16,
    maximum_length: u16,
    buffer: *mut u16,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct ObjectAttributes {
    length: u32,
    root_directory: *mut c_void,
    object_name: *mut UnicodeString,
    attributes: u32,
    security_descriptor: *mut c_void,
    security_qos: *mut c_void,
}

type NtStatus = i32;
type Handle = *mut c_void;

static SEEN: OnceLock<Mutex<HashSet<String>>> = OnceLock::new();

fn us_to_string(us: *const UnicodeString) -> String {
    if us.is_null() {
        return String::new();
    }
    unsafe {
        let u = &*us;
        if u.buffer.is_null() {
            return String::new();
        }
        String::from_utf16_lossy(core::slice::from_raw_parts(u.buffer, (u.length / 2) as usize))
    }
}

fn interesting(lower: &str) -> bool {
    lower.contains("er0000") || lower.contains("\\eldenring\\")
}

fn note(api: &str, s: &str) {
    let lower = s.to_ascii_lowercase();
    if !interesting(&lower) {
        return;
    }
    let seen = SEEN.get_or_init(|| Mutex::new(HashSet::new()));
    let mut g = seen.lock().unwrap();
    if g.len() < 400 && g.insert(format!("{api}|{lower}")) {
        log(&format!("savefile-io: {api} {s}"));
    }
}

/// If `s` names the real save (or its backup), return the redirected path text.
fn map_name(s: &str) -> Option<String> {
    let lower = s.to_ascii_lowercase();
    if lower.ends_with("er0000.sl2") {
        Some(format!("{}ER0000.mec", &s[..s.len() - "ER0000.sl2".len()]))
    } else if lower.ends_with("er0000.sl2.bak") {
        Some(format!("{}ER0000.mec.bak", &s[..s.len() - "ER0000.sl2.bak".len()]))
    } else {
        None
    }
}

/// Holds the memory a redirected OBJECT_ATTRIBUTES points at, for the duration of one call.
struct Redirected {
    _buf: Vec<u16>,
    _us: Box<UnicodeString>,
    oa: Box<ObjectAttributes>,
}

fn redirect_oa(api: &str, oa: *const ObjectAttributes) -> Option<Redirected> {
    if oa.is_null() {
        return None;
    }
    let name = unsafe { us_to_string((*oa).object_name) };
    note(api, &name);
    let new = map_name(&name)?;
    log(&format!("savefile-io: REDIRECT {api}: {name} -> {new}"));
    let mut buf: Vec<u16> = new.encode_utf16().collect();
    let len_bytes = (buf.len() * 2) as u16;
    buf.push(0);
    let mut us = Box::new(UnicodeString { length: len_bytes, maximum_length: len_bytes + 2, buffer: buf.as_mut_ptr() });
    let mut copy = Box::new(unsafe { *oa });
    copy.object_name = &mut *us as *mut UnicodeString;
    Some(Redirected { _buf: buf, _us: us, oa: copy })
}

macro_rules! orig {
    ($slot:ident, $ty:ty) => {{
        let o = $slot.load(Ordering::SeqCst);
        unsafe { core::mem::transmute::<usize, $ty>(o) }
    }};
}

// NtCreateFile
type NtCreateFileFn = unsafe extern "system" fn(*mut Handle, u32, *const ObjectAttributes, *mut c_void, *mut i64, u32, u32, u32, u32, *mut c_void, u32) -> NtStatus;
static ORIG_NTCREATEFILE: AtomicUsize = AtomicUsize::new(0);
unsafe extern "system" fn hk_ntcreatefile(h: *mut Handle, access: u32, oa: *const ObjectAttributes, iosb: *mut c_void, alloc: *mut i64, attrs: u32, share: u32, disp: u32, opts: u32, ea: *mut c_void, ea_len: u32) -> NtStatus {
    let f0 = orig!(ORIG_NTCREATEFILE, NtCreateFileFn);
    match redirect_oa("NtCreateFile", oa) {
        Some(r) => f0(h, access, &*r.oa, iosb, alloc, attrs, share, disp, opts, ea, ea_len),
        None => f0(h, access, oa, iosb, alloc, attrs, share, disp, opts, ea, ea_len),
    }
}

// NtOpenFile
type NtOpenFileFn = unsafe extern "system" fn(*mut Handle, u32, *const ObjectAttributes, *mut c_void, u32, u32) -> NtStatus;
static ORIG_NTOPENFILE: AtomicUsize = AtomicUsize::new(0);
unsafe extern "system" fn hk_ntopenfile(h: *mut Handle, access: u32, oa: *const ObjectAttributes, iosb: *mut c_void, share: u32, opts: u32) -> NtStatus {
    let f0 = orig!(ORIG_NTOPENFILE, NtOpenFileFn);
    match redirect_oa("NtOpenFile", oa) {
        Some(r) => f0(h, access, &*r.oa, iosb, share, opts),
        None => f0(h, access, oa, iosb, share, opts),
    }
}

// NtDeleteFile
type NtDeleteFileFn = unsafe extern "system" fn(*const ObjectAttributes) -> NtStatus;
static ORIG_NTDELETEFILE: AtomicUsize = AtomicUsize::new(0);
unsafe extern "system" fn hk_ntdeletefile(oa: *const ObjectAttributes) -> NtStatus {
    let f0 = orig!(ORIG_NTDELETEFILE, NtDeleteFileFn);
    match redirect_oa("NtDeleteFile", oa) {
        Some(r) => f0(&*r.oa),
        None => f0(oa),
    }
}

// NtQueryAttributesFile / NtQueryFullAttributesFile
type NtQueryAttrFn = unsafe extern "system" fn(*const ObjectAttributes, *mut c_void) -> NtStatus;
static ORIG_NTQUERYATTR: AtomicUsize = AtomicUsize::new(0);
unsafe extern "system" fn hk_ntqueryattr(oa: *const ObjectAttributes, out: *mut c_void) -> NtStatus {
    let f0 = orig!(ORIG_NTQUERYATTR, NtQueryAttrFn);
    match redirect_oa("NtQueryAttributesFile", oa) {
        Some(r) => f0(&*r.oa, out),
        None => f0(oa, out),
    }
}
static ORIG_NTQUERYFULLATTR: AtomicUsize = AtomicUsize::new(0);
unsafe extern "system" fn hk_ntqueryfullattr(oa: *const ObjectAttributes, out: *mut c_void) -> NtStatus {
    let f0 = orig!(ORIG_NTQUERYFULLATTR, NtQueryAttrFn);
    match redirect_oa("NtQueryFullAttributesFile", oa) {
        Some(r) => f0(&*r.oa, out),
        None => f0(oa, out),
    }
}

// NtSetInformationFile: rename (10 = FileRenameInformation, 65 = FileRenameInformationEx)
type NtSetInfoFn = unsafe extern "system" fn(Handle, *mut c_void, *mut c_void, u32, u32) -> NtStatus;
static ORIG_NTSETINFO: AtomicUsize = AtomicUsize::new(0);
unsafe extern "system" fn hk_ntsetinfo(h: Handle, iosb: *mut c_void, info: *mut c_void, len: u32, class: u32) -> NtStatus {
    let f0 = orig!(ORIG_NTSETINFO, NtSetInfoFn);
    if (class == 10 || class == 65) && !info.is_null() && len >= 20 {
        // FILE_RENAME_INFORMATION { u32 flags/ReplaceIfExists (padded to 8), HANDLE root, u32 name_len, WCHAR name[] }
        let name_len = *((info as *const u8).add(16) as *const u32) as usize;
        let name_ptr = (info as *const u8).add(20) as *const u16;
        if name_len > 0 && 20 + name_len <= len as usize {
            let s = String::from_utf16_lossy(core::slice::from_raw_parts(name_ptr, name_len / 2));
            note("NtSetInformationFile.rename", &s);
            if let Some(new) = map_name(&s) {
                log(&format!("savefile-io: REDIRECT rename target {s} -> {new}"));
                let w: Vec<u16> = new.encode_utf16().collect();
                let new_len = w.len() * 2;
                let total = 20 + new_len + 2;
                let mut buf = vec![0u8; total.max(len as usize)];
                core::ptr::copy_nonoverlapping(info as *const u8, buf.as_mut_ptr(), 20);
                *(buf.as_mut_ptr().add(16) as *mut u32) = new_len as u32;
                core::ptr::copy_nonoverlapping(w.as_ptr() as *const u8, buf.as_mut_ptr().add(20), new_len);
                return f0(h, iosb, buf.as_mut_ptr() as *mut c_void, total as u32, class);
            }
        }
    }
    f0(h, iosb, info, len, class)
}

fn install_one(name: &[u8], hook: *const (), slot: &AtomicUsize) -> Result<(), String> {
    unsafe {
        let nt: Vec<u16> = "ntdll.dll\0".encode_utf16().collect();
        let h = GetModuleHandleW(nt.as_ptr());
        let target = GetProcAddress(h, name.as_ptr()).ok_or_else(|| format!("{} not found", String::from_utf8_lossy(&name[..name.len() - 1])))?;
        let det = RawDetour::new(target as *const (), hook).map_err(|e| format!("detour: {e}"))?;
        det.enable().map_err(|e| format!("enable: {e}"))?;
        slot.store(det.trampoline() as *const () as usize, Ordering::SeqCst);
        core::mem::forget(det); // the hook lives as long as the process
        Ok(())
    }
}

pub fn install() -> bool {
    let results = [
        ("NtCreateFile", install_one(b"NtCreateFile\0", hk_ntcreatefile as *const (), &ORIG_NTCREATEFILE)),
        ("NtOpenFile", install_one(b"NtOpenFile\0", hk_ntopenfile as *const (), &ORIG_NTOPENFILE)),
        ("NtDeleteFile", install_one(b"NtDeleteFile\0", hk_ntdeletefile as *const (), &ORIG_NTDELETEFILE)),
        ("NtQueryAttributesFile", install_one(b"NtQueryAttributesFile\0", hk_ntqueryattr as *const (), &ORIG_NTQUERYATTR)),
        ("NtQueryFullAttributesFile", install_one(b"NtQueryFullAttributesFile\0", hk_ntqueryfullattr as *const (), &ORIG_NTQUERYFULLATTR)),
        ("NtSetInformationFile", install_one(b"NtSetInformationFile\0", hk_ntsetinfo as *const (), &ORIG_NTSETINFO)),
    ];
    let mut ok = true;
    for (n, r) in results {
        match r {
            Ok(()) => log(&format!("fileredirect: hooked {n}")),
            Err(e) => {
                ok = false;
                log(&format!("fileredirect: FAILED to hook {n}: {e}"));
            }
        }
    }
    ok
}
