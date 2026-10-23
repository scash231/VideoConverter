//! `Process.Kill(entireProcessTree: true)` on Windows: terminate the process (exit code -1)
//! so it cannot start more children, then find its children (parent id matches and they were
//! started after it) and kill their trees recursively.

use std::collections::HashMap;

use windows_sys::Win32::Foundation::{CloseHandle, FILETIME, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
};
use windows_sys::Win32::System::Threading::{
    GetProcessTimes, OpenProcess, TerminateProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_TERMINATE,
};

struct Handle(HANDLE);

impl Drop for Handle {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: handle opened by OpenProcess.
            unsafe { CloseHandle(self.0) };
        }
    }
}

fn open(pid: u32) -> Option<Handle> {
    // SAFETY: plain FFI call; a null handle means failure.
    let h = unsafe { OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if h.is_null() {
        None
    } else {
        Some(Handle(h))
    }
}

fn start_time(h: &Handle) -> Option<u64> {
    let mut c = FILETIME { dwLowDateTime: 0, dwHighDateTime: 0 };
    let mut e = c;
    let mut k = c;
    let mut u = c;
    // SAFETY: valid handle and out parameters.
    if unsafe { GetProcessTimes(h.0, &mut c, &mut e, &mut k, &mut u) } == 0 {
        return None;
    }
    Some(((c.dwHighDateTime as u64) << 32) | c.dwLowDateTime as u64)
}

/// Snapshot of (pid → parent pid).
fn parent_map() -> HashMap<u32, u32> {
    let mut map = HashMap::new();
    // SAFETY: snapshot handle is closed below.
    let snap = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
    if snap == INVALID_HANDLE_VALUE {
        return map;
    }
    let mut e: PROCESSENTRY32W = unsafe { std::mem::zeroed() };
    e.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
    // SAFETY: valid snapshot handle and entry with dwSize set.
    let mut ok = unsafe { Process32FirstW(snap, &mut e) } != 0;
    while ok {
        map.insert(e.th32ProcessID, e.th32ParentProcessID);
        // SAFETY: as above.
        ok = unsafe { Process32NextW(snap, &mut e) } != 0;
    }
    // SAFETY: handle from CreateToolhelp32Snapshot.
    unsafe { CloseHandle(snap) };
    map
}

/// Kills `pid` and all of its descendants. Errors are ignored, as every Native call site
/// wraps the kill in `try { } catch { }`.
pub fn kill_tree(pid: u32) {
    if let Some(h) = open(pid) {
        kill_handle_tree(pid, &h);
    }
}

fn kill_handle_tree(pid: u32, h: &Handle) {
    // SAFETY: valid handle with PROCESS_TERMINATE access.
    unsafe { TerminateProcess(h.0, u32::MAX) };
    let parent_start = start_time(h);
    let map = parent_map();
    for (&child, &parent) in &map {
        if parent != pid || child == pid {
            continue;
        }
        if let Some(ch) = open(child) {
            let child_start = start_time(&ch);
            let is_child = matches!((parent_start, child_start), (Some(p), Some(c)) if p < c);
            if is_child {
                kill_handle_tree(child, &ch);
            }
        }
    }
}
