//! Ferrite: a 1:1 port of VideoConverter's Native C# media engine
//! (`VideoConverterUI/Services/NativeEngineService.cs` and the helpers it uses).
//!
//! Every module names the C# member it ports. Behaviour that depends on .NET or Win32
//! semantics (number formatting, JSON access, path handling, IO error texts, command-line
//! quoting, process start/kill) is reproduced in [`dotnet`], so the rest of the code reads
//! like the original.
#![cfg(windows)]

pub mod dotnet;

pub mod host;
pub mod models;
pub mod settings;

pub mod proc;

pub mod binaries;
pub mod cookies;
pub mod locate;
pub mod util;

pub mod download;
pub mod metadata;
pub mod playlist;
pub mod ytmusic;

pub mod fingerprint;
pub mod hwenc;
pub mod subtitles;

pub mod cloud;
pub mod youtube;

pub mod engine;
pub mod jobs;
pub mod pipeline;

pub use engine::{CallError, Engine, JobRequest};
pub use host::{Host, HostInfo};
