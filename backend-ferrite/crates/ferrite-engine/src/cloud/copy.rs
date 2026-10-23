//! `GetUniqueTargetFilePath`, `CopyFileWithProgressAsync`, `ThrottleProgress` and the
//! progress-reporting request body (`ProgressableStreamContent`).

use std::ffi::{OsStr, OsString};
use std::os::windows::fs::OpenOptionsExt;
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::Arc;

use bytes::Bytes;
use futures_util::Stream;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio_util::sync::CancellationToken;

use crate::dotnet::fs::{delete_file, file_exists};
use crate::dotnet::ioerr::{from_io, NetError};
use crate::dotnet::path::{combine, get_extension, get_file_name_without_extension};
use crate::ncat;

const FILE_SHARE_READ: u32 = 0x1;

/// `GetUniqueTargetFilePath(directory, originalFileName)`.
pub fn unique_target_file_path(directory: &OsStr, file_name: &OsStr) -> OsString {
    let mut target = combine(directory, file_name);
    if !file_exists(&target) {
        return target;
    }
    let stem = get_file_name_without_extension(file_name);
    let ext = get_extension(file_name);
    let mut counter = 1;
    while file_exists(&target) {
        target = combine(directory, ncat!(&stem, format!(" ({counter})"), &ext));
        counter += 1;
    }
    target
}

/// `ThrottleProgress(progress)`: only fires when `(int)pct` changes.
pub fn throttle(progress: Arc<dyn Fn(f64) + Send + Sync>) -> Arc<dyn Fn(f64) + Send + Sync> {
    let last = Arc::new(AtomicI64::new(-1));
    Arc::new(move |pct: f64| {
        let whole = pct as i64;
        if last.swap(whole, Ordering::SeqCst) == whole {
            return;
        }
        progress(pct);
    })
}

/// `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ...)`.
pub async fn open_read(path: &OsStr) -> Result<tokio::fs::File, NetError> {
    let mut o = std::fs::OpenOptions::new();
    o.read(true).share_mode(FILE_SHARE_READ);
    o.open(path).map(tokio::fs::File::from_std).map_err(|e| from_io(&e, Some(path)))
}

/// `new FileInfo(path).Length`.
pub fn file_length(path: &OsStr) -> Result<u64, NetError> {
    std::fs::metadata(path).map(|m| m.len()).map_err(|e| {
        if e.raw_os_error() == Some(3) {
            from_io(&std::io::Error::from_raw_os_error(2), Some(path))
        } else {
            from_io(&e, Some(path))
        }
    })
}

/// `CopyFileWithProgressAsync(source, target, progress, ct)` (1 MiB buffer; a half-written
/// target is deleted on failure).
pub async fn copy_file_with_progress(
    source: &OsStr,
    target: &OsStr,
    progress: Arc<dyn Fn(f64) + Send + Sync>,
    ct: &CancellationToken,
) -> Result<(), NetError> {
    let total = file_length(source)?;
    let report = throttle(progress);
    let result = async {
        let mut src = open_read(source).await?;
        let mut o = std::fs::OpenOptions::new();
        o.write(true).create(true).truncate(true).share_mode(0);
        let mut dst = o.open(target).map(tokio::fs::File::from_std).map_err(|e| from_io(&e, Some(target)))?;
        let mut buf = vec![0u8; 1024 * 1024];
        let mut copied: u64 = 0;
        loop {
            let n = tokio::select! {
                r = src.read(&mut buf) => r.map_err(|e| from_io(&e, Some(source)))?,
                _ = ct.cancelled() => return Err(NetError::canceled()),
            };
            if n == 0 {
                break;
            }
            tokio::select! {
                r = dst.write_all(&buf[..n]) => r.map_err(|e| from_io(&e, Some(target)))?,
                _ = ct.cancelled() => return Err(NetError::canceled()),
            }
            copied += n as u64;
            if total > 0 {
                report((copied as f64 / total as f64 * 100.0).min(100.0));
            }
        }
        dst.flush().await.map_err(|e| from_io(&e, Some(target)))?;
        Ok(())
    }
    .await;
    if result.is_err() && file_exists(target) {
        let _ = delete_file(target);
    }
    result
}

/// A request body stream over a file that reports upload progress per buffer, throttled to
/// whole percents (`ProgressableStreamContent`). `prefix`/`suffix` frame multipart bodies.
pub fn progress_body_stream(
    file: tokio::fs::File,
    total: u64,
    buffer: usize,
    prefix: Bytes,
    suffix: Bytes,
    progress: Arc<dyn Fn(f64) + Send + Sync>,
) -> impl Stream<Item = Result<Bytes, std::io::Error>> + Send + 'static {
    let report = throttle(progress);
    enum State {
        Prefix,
        File,
        Suffix,
        Done,
    }
    futures_util::stream::unfold(
        (State::Prefix, file, 0u64, prefix, suffix),
        move |(state, mut file, mut sent, prefix, suffix)| {
            let report = report.clone();
            async move {
                let mut state = state;
                loop {
                    match state {
                        State::Prefix => {
                            if !prefix.is_empty() {
                                let p = prefix.clone();
                                return Some((Ok(p), (State::File, file, sent, Bytes::new(), suffix)));
                            }
                            state = State::File;
                        }
                        State::File => {
                            let mut buf = vec![0u8; buffer];
                            match file.read(&mut buf).await {
                                Ok(0) => state = State::Suffix,
                                Ok(n) => {
                                    buf.truncate(n);
                                    sent += n as u64;
                                    if total > 0 {
                                        report((sent as f64 / total as f64 * 100.0).min(100.0));
                                    }
                                    return Some((Ok(Bytes::from(buf)), (State::File, file, sent, prefix, suffix)));
                                }
                                Err(e) => return Some((Err(e), (State::Done, file, sent, prefix, suffix))),
                            }
                        }
                        State::Suffix => {
                            if !suffix.is_empty() {
                                let s = suffix.clone();
                                return Some((Ok(s), (State::Done, file, sent, prefix, Bytes::new())));
                            }
                            state = State::Done;
                        }
                        State::Done => return None,
                    }
                }
            }
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    #[test]
    fn unique_names_like_native() {
        let d = std::env::temp_dir().join(format!("ferrite_unique_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        let name = OsStr::new("clip.mp4");
        assert_eq!(unique_target_file_path(d.as_os_str(), name), d.join("clip.mp4").into_os_string());
        std::fs::write(d.join("clip.mp4"), b"").unwrap();
        std::fs::write(d.join("clip (1).mp4"), b"").unwrap();
        assert_eq!(unique_target_file_path(d.as_os_str(), name), d.join("clip (2).mp4").into_os_string());
        std::fs::remove_dir_all(&d).unwrap();
    }

    #[test]
    fn throttle_only_on_whole_percent_changes() {
        let seen = Arc::new(Mutex::new(Vec::new()));
        let s2 = seen.clone();
        let t = throttle(Arc::new(move |p| s2.lock().unwrap().push(p)));
        for p in [0.1, 0.5, 1.0, 1.9, 2.0, 99.99, 100.0] {
            t(p);
        }
        assert_eq!(*seen.lock().unwrap(), vec![0.1, 1.0, 2.0, 99.99, 100.0]);
    }

    #[tokio::test]
    async fn copies_with_progress_and_cleans_up_on_cancel() {
        let d = std::env::temp_dir().join(format!("ferrite_copy_{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        let src = d.join("a.bin");
        std::fs::write(&src, vec![7u8; 3 * 1024 * 1024 + 10]).unwrap();
        let dst = d.join("b.bin");
        let seen = Arc::new(Mutex::new(Vec::new()));
        let s2 = seen.clone();
        copy_file_with_progress(src.as_os_str(), dst.as_os_str(), Arc::new(move |p| s2.lock().unwrap().push(p)), &CancellationToken::new()).await.unwrap();
        assert_eq!(std::fs::read(&dst).unwrap().len(), 3 * 1024 * 1024 + 10);
        assert_eq!(*seen.lock().unwrap().last().unwrap(), 100.0);
        let ct = CancellationToken::new();
        ct.cancel();
        let dst2 = d.join("c.bin");
        let e = copy_file_with_progress(src.as_os_str(), dst2.as_os_str(), Arc::new(|_| {}), &ct).await.unwrap_err();
        assert!(e.is_canceled());
        assert!(!dst2.exists());
        std::fs::remove_dir_all(&d).unwrap();
    }
}
