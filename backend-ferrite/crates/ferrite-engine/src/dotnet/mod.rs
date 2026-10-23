//! Reproductions of the .NET runtime and Win32 behaviour the Native engine relies on.
//!
//! Native runs inside .NET, so many observable details (texts, rounding, which files match a
//! wildcard, how a command line is quoted) come from the runtime rather than from the engine
//! code itself. Each function here documents the .NET member it mirrors.

pub mod cmdline;
pub mod fmt;
pub mod fs;
pub mod http;
pub mod ioerr;
pub mod json;
pub mod netstr;
pub mod path;
pub mod text;
pub mod win;
