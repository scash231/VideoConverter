//! Exceptions as Native sees them. The UI only ever shows `Exception.Message` (and catches
//! `OperationCanceledException` separately), so an error is its kind plus the exact message
//! .NET would have produced.

use std::ffi::{OsStr, OsString};
use std::fmt;

use super::win::format_message;
use crate::ncat;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NetErrorKind {
    /// `OperationCanceledException` / `TaskCanceledException`.
    Canceled,
    FileNotFound,
    DirectoryNotFound,
    UnauthorizedAccess,
    PathTooLong,
    Io,
    Argument,
    InvalidOperation,
    Win32,
    Timeout,
    HttpRequest,
    Json,
    Generic,
}

#[derive(Debug, Clone)]
pub struct NetError {
    pub kind: NetErrorKind,
    pub message: OsString,
}

impl NetError {
    pub fn new(kind: NetErrorKind, message: impl AsRef<OsStr>) -> Self {
        NetError { kind, message: message.as_ref().to_owned() }
    }

    /// `new Exception(message)`.
    pub fn generic(message: impl AsRef<OsStr>) -> Self {
        Self::new(NetErrorKind::Generic, message)
    }

    /// `new OperationCanceledException()` ("The operation was canceled.").
    pub fn canceled() -> Self {
        Self::new(NetErrorKind::Canceled, "The operation was canceled.")
    }

    pub fn is_canceled(&self) -> bool {
        self.kind == NetErrorKind::Canceled
    }
}

impl fmt::Display for NetError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message.to_string_lossy())
    }
}

impl std::error::Error for NetError {}

pub const ERROR_FILE_NOT_FOUND: u32 = 2;
pub const ERROR_PATH_NOT_FOUND: u32 = 3;
pub const ERROR_ACCESS_DENIED: u32 = 5;
pub const ERROR_SHARING_VIOLATION: u32 = 32;
pub const ERROR_FILE_EXISTS: u32 = 80;
pub const ERROR_INVALID_PARAMETER: u32 = 87;
pub const ERROR_FILENAME_EXCED_RANGE: u32 = 206;
pub const ERROR_ALREADY_EXISTS: u32 = 183;
pub const ERROR_OPERATION_ABORTED: u32 = 995;
pub const ERROR_DIR_NOT_EMPTY: u32 = 145;
pub const ERROR_NO_MORE_FILES: u32 = 18;
pub const ERROR_INVALID_DRIVE: u32 = 15;

/// `Win32Marshal.GetExceptionForWin32Error(errorCode, path)`.
pub fn from_win32(code: u32, path: Option<&OsStr>) -> NetError {
    let p = path.filter(|p| !p.is_empty());
    match code {
        ERROR_FILE_NOT_FOUND => match p {
            Some(p) => NetError::new(NetErrorKind::FileNotFound, ncat!("Could not find file '", p, "'.")),
            None => NetError::new(NetErrorKind::FileNotFound, "Unable to find the specified file."),
        },
        ERROR_PATH_NOT_FOUND => match p {
            Some(p) => NetError::new(NetErrorKind::DirectoryNotFound, ncat!("Could not find a part of the path '", p, "'.")),
            None => NetError::new(NetErrorKind::DirectoryNotFound, "Could not find a part of the path."),
        },
        ERROR_ACCESS_DENIED => match p {
            Some(p) => NetError::new(NetErrorKind::UnauthorizedAccess, ncat!("Access to the path '", p, "' is denied.")),
            None => NetError::new(NetErrorKind::UnauthorizedAccess, "Access to the path is denied."),
        },
        ERROR_ALREADY_EXISTS if p.is_some() => NetError::new(
            NetErrorKind::Io,
            ncat!("Cannot create '", p.unwrap(), "' because a file or directory with the same name already exists."),
        ),
        ERROR_FILENAME_EXCED_RANGE => match p {
            Some(p) => NetError::new(
                NetErrorKind::PathTooLong,
                ncat!("The path '", p, "' is too long, or a component of the specified path is too long."),
            ),
            None => NetError::new(NetErrorKind::PathTooLong, "The specified file name or path is too long, or a component of the specified path is too long."),
        },
        ERROR_SHARING_VIOLATION => match p {
            Some(p) => NetError::new(
                NetErrorKind::Io,
                ncat!("The process cannot access the file '", p, "' because it is being used by another process."),
            ),
            None => NetError::new(NetErrorKind::Io, "The process cannot access the file because it is being used by another process."),
        },
        ERROR_FILE_EXISTS if p.is_some() => {
            NetError::new(NetErrorKind::Io, ncat!("The file '", p.unwrap(), "' already exists."))
        }
        ERROR_OPERATION_ABORTED => NetError::canceled(),
        _ => {
            let mut msg = OsString::from(format_message(code));
            if let Some(p) = p {
                msg.push(" : '");
                msg.push(p);
                msg.push("'");
            }
            NetError::new(NetErrorKind::Io, msg)
        }
    }
}

/// Maps a `std::io::Error` that carries a Win32 error code like .NET would.
pub fn from_io(e: &std::io::Error, path: Option<&OsStr>) -> NetError {
    match e.raw_os_error() {
        Some(code) => from_win32(code as u32, path),
        None => NetError::new(NetErrorKind::Io, e.to_string()),
    }
}

/// `ArgumentException(message, paramName)`'s `Message`.
pub fn argument(message: &str, param: &str) -> NetError {
    NetError::new(NetErrorKind::Argument, format!("{message} (Parameter '{param}')"))
}

/// `Win32Exception` thrown by `Process.Start`.
pub fn process_start_failed(file_name: &OsStr, working_directory: &OsStr, code: u32) -> NetError {
    NetError::new(
        NetErrorKind::Win32,
        ncat!(
            "An error occurred trying to start process '",
            file_name,
            "' with working directory '",
            working_directory,
            "'. ",
            format_message(code)
        ),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Texts verified against .NET 10.
    #[test]
    fn io_messages_match_dotnet() {
        assert_eq!(
            from_win32(ERROR_PATH_NOT_FOUND, Some(OsStr::new(r"C:\nope_dir_xyz\a.txt"))).message,
            OsString::from(r"Could not find a part of the path 'C:\nope_dir_xyz\a.txt'.")
        );
        assert_eq!(
            from_win32(ERROR_FILE_NOT_FOUND, Some(OsStr::new(r"C:\nope_file_xyz.txt"))).message,
            OsString::from(r"Could not find file 'C:\nope_file_xyz.txt'.")
        );
        assert_eq!(
            from_win32(ERROR_ACCESS_DENIED, Some(OsStr::new(r"C:\x"))).message,
            OsString::from(r"Access to the path 'C:\x' is denied.")
        );
        assert_eq!(argument("The path is empty.", "path").message, OsString::from("The path is empty. (Parameter 'path')"));
    }
}
