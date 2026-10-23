//! Port of `FpMethods`, `HwPresets`, `ApplyFingerprintAsync` and `ProbeVideoAsync`.

use std::ffi::{OsStr, OsString};

use tokio_util::sync::CancellationToken;

use crate::download::Phase;
use crate::dotnet::fmt::fixed;
use crate::dotnet::fs::file_exists;
use crate::dotnet::ioerr::{argument, NetError};
use crate::dotnet::json::{JResult, JsonDocument, Kind};
use crate::dotnet::path::{combine, get_directory_name, get_file_name_without_extension};
use crate::dotnet::text::is_blank;
use crate::engine::Engine;
use crate::hwenc::{get_hardware_encoder, HardwareEncoder};
use crate::jobs::{emit, JobContext};
use crate::ncat;
use crate::playlist::parse_double_any_invariant;
use crate::proc::progress::parse_ffmpeg_out_time;
use crate::proc::runner::{run_streaming, run_to_end, ProcSpec};
use crate::util::safe_delete;

/// One entry of `FpMethods`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FpPreset {
    pub scale: f64,
    pub vf_extra: Option<&'static str>,
    pub af_filter: Option<&'static str>,
    pub crf: i32,
    pub preset: &'static str,
    pub copy_audio: bool,
}

pub fn fp_preset(method: &str) -> Option<FpPreset> {
    match method {
        "light" => Some(FpPreset { scale: 1.003, vf_extra: None, af_filter: None, crf: 23, preset: "veryfast", copy_audio: true }),
        "standard" => Some(FpPreset {
            scale: 1.005,
            vf_extra: Some("eq=brightness=0.006:saturation=1.01"),
            af_filter: Some("atempo=1.002"),
            crf: 21,
            preset: "fast",
            copy_audio: false,
        }),
        "strong" => Some(FpPreset {
            scale: 1.010,
            vf_extra: Some("eq=brightness=0.012:saturation=1.02:contrast=1.01,hue=h=1.5"),
            af_filter: Some("atempo=1.005"),
            crf: 19,
            preset: "medium",
            copy_audio: false,
        }),
        _ => None,
    }
}

/// `HwPresets[encoder].GetValueOrDefault(preset, fallback)`.
pub fn hw_preset(encoder: &str, preset: &str) -> &'static str {
    match (encoder, preset) {
        ("h264_nvenc", "veryfast") => "p1",
        ("h264_nvenc", "fast") => "p4",
        ("h264_nvenc", "medium") => "p5",
        ("h264_nvenc", _) => "p4",
        ("h264_qsv", "veryfast") => "veryfast",
        ("h264_qsv", "fast") => "fast",
        ("h264_qsv", "medium") => "medium",
        ("h264_qsv", _) => "fast",
        ("h264_amf", "veryfast") => "speed",
        ("h264_amf", "fast") => "balanced",
        ("h264_amf", "medium") => "quality",
        (_, _) => "balanced",
    }
}

/// `sw`/`sh`: `((int)Math.Round(v * scale) / 2) * 2` with banker's rounding.
pub fn scaled_even(v: i32, scale: f64) -> i32 {
    (((v as f64) * scale).round_ties_even() as i32 / 2) * 2
}

/// The `-vf` filter chain.
pub fn video_filter(w: i32, h: i32, p: &FpPreset) -> String {
    let mut vf = format!("scale={}:{}:flags=fast_bilinear,crop={w}:{h}", scaled_even(w, p.scale), scaled_even(h, p.scale));
    if let Some(extra) = p.vf_extra {
        vf.push(',');
        vf.push_str(extra);
    }
    vf
}

/// The ffmpeg argument list of `EncodeAsync(hw)`.
pub fn encode_args(input: &OsStr, output: &OsStr, vf: &str, p: &FpPreset, hw: Option<HardwareEncoder>) -> Vec<OsString> {
    let mut a: Vec<OsString> = vec!["-y".into(), "-hwaccel".into(), hw.map_or("d3d11va", |h| h.hw_accel).into()];
    a.push("-i".into());
    a.push(input.to_owned());
    a.push("-vf".into());
    a.push(vf.into());
    if let Some(af) = p.af_filter {
        a.push("-af".into());
        a.push(af.into());
    }
    let crf = p.crf.to_string();
    let push = |a: &mut Vec<OsString>, xs: &[&str]| a.extend(xs.iter().map(OsString::from));
    match hw.map(|h| h.encoder) {
        Some("h264_nvenc") => push(
            &mut a,
            &["-c:v", "h264_nvenc", "-rc", "vbr", "-cq", &crf, "-preset", hw_preset("h264_nvenc", p.preset), "-bf", "2", "-b_ref_mode", "middle", "-rc-lookahead", "20"],
        ),
        Some("h264_qsv") => push(&mut a, &["-c:v", "h264_qsv", "-global_quality", &crf, "-preset", hw_preset("h264_qsv", p.preset)]),
        Some("h264_amf") => push(&mut a, &["-c:v", "h264_amf", "-qp_i", &crf, "-qp_p", &crf, "-quality", hw_preset("h264_amf", p.preset)]),
        _ => push(&mut a, &["-c:v", "libx264", "-crf", &crf, "-preset", p.preset, "-pix_fmt", "yuv420p"]),
    }
    if p.copy_audio {
        push(&mut a, &["-c:a", "copy"]);
    } else {
        push(&mut a, &["-c:a", "aac", "-b:a", "128k"]);
    }
    push(&mut a, &["-threads", "0", "-map_metadata", "-1", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", "-loglevel", "error"]);
    a.push(output.to_owned());
    a
}

/// `ProbeVideoAsync(path, ct)` → (width, height, duration).
pub async fn probe_video(engine: &Engine, path: &OsStr, ct: &CancellationToken) -> Result<(Option<i32>, Option<i32>, f64), NetError> {
    let spec = ProcSpec::raw(
        engine.bins().ffprobe(),
        ncat!("-v quiet -print_format json -select_streams v:0 -show_streams -show_format \"", path, "\""),
        engine.configured_env(),
    );
    match run_to_end(&spec, engine.info(), ct, Some(60_000)).await {
        Ok(r) => {
            if is_blank(&r.stdout) {
                return Ok((None, None, 0.0));
            }
            Ok(parse_probe(&r.stdout).unwrap_or((None, None, 0.0)))
        }
        Err(e) if e.is_canceled() && ct.is_cancelled() => Err(e),
        Err(_) => Ok((None, None, 0.0)),
    }
}

/// The JSON part of `ProbeVideoAsync` (`Err` = exception → `(null, null, 0)`).
pub fn parse_probe(json: &str) -> JResult<(Option<i32>, Option<i32>, f64)> {
    let doc = JsonDocument::parse(json)?;
    let root = doc.root();
    let mut duration = 0.0;
    if let Some(fmt) = root.try_get_property("format")? {
        if let Some(d) = fmt.try_get_property("duration")? {
            match d.kind() {
                Kind::String => duration = d.get_string()?.and_then(|s| parse_double_any_invariant(&s)).unwrap_or(0.0),
                Kind::Number => duration = d.try_get_double()?.unwrap_or(0.0),
                _ => {}
            }
        }
    }
    if let Some(streams) = root.try_get_property("streams")? {
        for stream in streams.array()? {
            if let Some(ct) = stream.try_get_property("codec_type")? {
                if ct.get_string()?.as_deref() == Some("video") {
                    let w = match stream.try_get_property("width")? {
                        Some(w) => Some(w.get_int32()?),
                        None => None,
                    };
                    let h = match stream.try_get_property("height")? {
                        Some(h) => Some(h.get_int32()?),
                        None => None,
                    };
                    return Ok((w, h, duration));
                }
            }
        }
    }
    Ok((None, None, duration))
}

#[allow(clippy::too_many_arguments)]
async fn encode(
    engine: &Engine,
    ctx: &JobContext,
    input: &OsStr,
    output: &OsStr,
    vf: &str,
    preset: &FpPreset,
    duration: f64,
    hw: Option<HardwareEncoder>,
    phase: &Phase,
    ct: &CancellationToken,
) -> Result<i32, NetError> {
    let spec = ProcSpec::list(engine.bins().ffmpeg(), encode_args(input, output, vf, preset, hw), engine.configured_env());
    run_streaming(
        &spec,
        engine.info(),
        ct,
        &ctx.running,
        |line| {
            if let Some(frac) = parse_ffmpeg_out_time(&line, duration) {
                let msg = format!("{}Encoding video: {}%", phase.prefix(), fixed(frac * 100.0, 0, engine.culture()));
                emit(engine, ctx, "processing", 50.0 + frac * 50.0, msg);
            }
        },
        |_| {},
    )
    .await
}

/// `ApplyFingerprintAsync(ctx, inputFile, method, useGpu, phasePrefix, ct)`.
pub async fn apply_fingerprint(
    engine: &Engine,
    ctx: &JobContext,
    input: &OsStr,
    method: Option<&str>,
    use_gpu: bool,
    phase: &Phase,
    ct: &CancellationToken,
) -> Result<OsString, NetError> {
    let dir = get_directory_name(input).unwrap_or_default();
    let mut name = get_file_name_without_extension(input);
    name.push("_fp.mp4");
    let out = combine(&dir, &name);

    if method == Some("metadata") {
        let spec = ProcSpec::raw(
            engine.bins().ffmpeg(),
            ncat!("-y -hide_banner -loglevel error -i \"", input, "\" -c copy -map_metadata -1 -movflags +faststart \"", &out, "\""),
            engine.configured_env(),
        );
        let r = run_to_end(&spec, engine.info(), ct, None).await?;
        if r.exit_code == 0 && file_exists(&out) {
            return Ok(out);
        }
        safe_delete(Some(&out)).await;
        return Ok(input.to_owned());
    }

    let Some(method) = method else {
        return Err(argument("Value cannot be null.", "key"));
    };
    let preset = fp_preset(method).or_else(|| fp_preset("standard")).expect("standard preset exists");

    let (w, h, duration) = probe_video(engine, input, ct).await?;
    let (Some(w), Some(h)) = (w, h) else { return Ok(input.to_owned()) };
    if w == 0 || h == 0 {
        return Ok(input.to_owned());
    }
    let vf = video_filter(w, h, &preset);

    let hw = if use_gpu { get_hardware_encoder(engine.bins().ffmpeg_path, engine.info()).await } else { None };
    let mut exit_code = encode(engine, ctx, input, &out, &vf, &preset, duration, hw, phase, ct).await?;
    if exit_code != 0 && hw.is_some() && !ct.is_cancelled() {
        safe_delete(Some(&out)).await;
        emit(engine, ctx, "processing", 50.0, format!("{}GPU encoder failed, retrying on CPU...", phase.prefix()));
        exit_code = encode(engine, ctx, input, &out, &vf, &preset, duration, None, phase, ct).await?;
    }
    if exit_code != 0 || ct.is_cancelled() {
        safe_delete(Some(&out)).await;
        return Ok(input.to_owned());
    }
    Ok(if file_exists(&out) { out } else { input.to_owned() })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scale_math_uses_bankers_rounding() {
        assert_eq!(scaled_even(1080, 1.005), 1084); // 1085.4 -> 1085 -> 1084
        assert_eq!(scaled_even(1920, 1.005), 1930); // 1929.6 -> 1930
        assert_eq!(scaled_even(5, 1.5), 8); // 7.5 -> 8 (ties to even)
        assert_eq!(scaled_even(3, 1.5), 4); // 4.5 -> 4 (ties to even)
    }

    #[test]
    fn filters_and_args_match_native() {
        let p = fp_preset("standard").unwrap();
        assert_eq!(video_filter(1080, 1920, &p), "scale=1084:1930:flags=fast_bilinear,crop=1080:1920,eq=brightness=0.006:saturation=1.01");
        let args = encode_args(OsStr::new(r"C:\a b.mp4"), OsStr::new(r"C:\a b_fp.mp4"), "VF", &p, None);
        let s: Vec<String> = args.iter().map(|a| a.to_string_lossy().into_owned()).collect();
        assert_eq!(
            s.join(" "),
            r"-y -hwaccel d3d11va -i C:\a b.mp4 -vf VF -af atempo=1.002 -c:v libx264 -crf 21 -preset fast -pix_fmt yuv420p -c:a aac -b:a 128k -threads 0 -map_metadata -1 -movflags +faststart -progress pipe:1 -nostats -loglevel error C:\a b_fp.mp4"
        );
        let nv = encode_args(OsStr::new("i"), OsStr::new("o"), "VF", &fp_preset("light").unwrap(), Some(crate::hwenc::CANDIDATES[0]));
        let s: Vec<String> = nv.iter().map(|a| a.to_string_lossy().into_owned()).collect();
        assert_eq!(
            s.join(" "),
            "-y -hwaccel cuda -i i -vf VF -c:v h264_nvenc -rc vbr -cq 23 -preset p1 -bf 2 -b_ref_mode middle -rc-lookahead 20 -c:a copy -threads 0 -map_metadata -1 -movflags +faststart -progress pipe:1 -nostats -loglevel error o"
        );
        assert_eq!(hw_preset("h264_amf", "medium"), "quality");
        assert!(fp_preset("metadata").is_none());
    }

    #[test]
    fn probe_parsing() {
        let j = r#"{"streams":[{"codec_type":"video","width":1080,"height":1920}],"format":{"duration":"12.500000"}}"#;
        assert_eq!(parse_probe(j).unwrap(), (Some(1080), Some(1920), 12.5));
        let j = r#"{"streams":[{"codec_type":"audio"}],"format":{"duration":3}}"#;
        assert_eq!(parse_probe(j).unwrap(), (None, None, 3.0));
        assert!(parse_probe(r#"{"streams":[{"codec_type":"video","width":"1080"}]}"#).is_err());
    }
}
