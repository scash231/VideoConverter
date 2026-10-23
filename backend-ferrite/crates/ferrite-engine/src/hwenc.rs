//! Port of `MediaTools.GetHardwareEncoderAsync` / `CanEncodeAsync`: the first H.264 hardware
//! encoder (NVENC → QSV → AMF) that really encodes a test frame. Cached per process.

use std::ffi::{OsStr, OsString};

use tokio::sync::OnceCell;
use tokio_util::sync::CancellationToken;

use crate::host::HostInfo;
use crate::locate::find_ffmpeg;
use crate::proc::env::EnvMode;
use crate::proc::runner::{run_to_end, ProcSpec};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HardwareEncoder {
    pub encoder: &'static str,
    pub hw_accel: &'static str,
}

pub const CANDIDATES: [HardwareEncoder; 3] = [
    HardwareEncoder { encoder: "h264_nvenc", hw_accel: "cuda" },
    HardwareEncoder { encoder: "h264_qsv", hw_accel: "qsv" },
    HardwareEncoder { encoder: "h264_amf", hw_accel: "d3d11va" },
];

static DETECTED: OnceCell<Option<HardwareEncoder>> = OnceCell::const_new();

pub fn probe_args(encoder: &str) -> String {
    format!("-hide_banner -loglevel error -f lavfi -i color=c=black:s=256x256:d=0.1 -frames:v 1 -c:v {encoder} -f null -")
}

async fn can_encode(ffmpeg: &OsStr, encoder: &str, info: &HostInfo) -> bool {
    let spec = ProcSpec::raw(ffmpeg, probe_args(encoder), EnvMode::Inherit);
    matches!(run_to_end(&spec, info, &CancellationToken::new(), Some(15_000)).await, Ok(r) if r.exit_code == 0)
}

/// `GetHardwareEncoderAsync(ffmpegPath)`.
pub async fn get_hardware_encoder(ffmpeg_path: Option<OsString>, info: &HostInfo) -> Option<HardwareEncoder> {
    *DETECTED
        .get_or_init(|| async {
            let ffmpeg = ffmpeg_path.unwrap_or_else(|| find_ffmpeg(info));
            for c in CANDIDATES {
                if can_encode(&ffmpeg, c.encoder, info).await {
                    return Some(c);
                }
            }
            None
        })
        .await
}
