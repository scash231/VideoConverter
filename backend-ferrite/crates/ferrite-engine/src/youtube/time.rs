//! UTC timestamps in the format google-auth writes to token.json
//! (`datetime.isoformat() + "Z"`, microseconds only when non-zero).

use std::time::{Duration, SystemTime, UNIX_EPOCH};

fn days_to_civil(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

fn civil_to_days(y: i64, m: u32, d: u32) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y.rem_euclid(400);
    let m = m as i64;
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

/// `datetime.isoformat() + "Z"` for a UTC instant.
pub fn format_expiry(t: SystemTime) -> String {
    let d = t.duration_since(UNIX_EPOCH).unwrap_or_default();
    let secs = d.as_secs() as i64;
    let micros = d.subsec_micros();
    let (y, m, day) = days_to_civil(secs.div_euclid(86_400));
    let rem = secs.rem_euclid(86_400);
    let (hh, mm, ss) = (rem / 3600, (rem % 3600) / 60, rem % 60);
    if micros == 0 {
        format!("{y:04}-{m:02}-{day:02}T{hh:02}:{mm:02}:{ss:02}Z")
    } else {
        format!("{y:04}-{m:02}-{day:02}T{hh:02}:{mm:02}:{ss:02}.{micros:06}Z")
    }
}

/// google-auth's reader: `strptime(expiry.rstrip("Z").split(".")[0], "%Y-%m-%dT%H:%M:%S")`.
pub fn parse_expiry(s: &str) -> Option<SystemTime> {
    let base = s.trim_end_matches('Z').split('.').next()?;
    let (date, time) = base.split_once('T')?;
    let mut dp = date.split('-');
    let y: i64 = dp.next()?.parse().ok()?;
    let m: u32 = dp.next()?.parse().ok()?;
    let d: u32 = dp.next()?.parse().ok()?;
    let mut tp = time.split(':');
    let hh: u64 = tp.next()?.parse().ok()?;
    let mm: u64 = tp.next()?.parse().ok()?;
    let ss: u64 = tp.next()?.parse().ok()?;
    if !(1..=12).contains(&m) || !(1..=31).contains(&d) || hh > 23 || mm > 59 || ss > 60 {
        return None;
    }
    let days = civil_to_days(y, m, d);
    if days < 0 {
        return None;
    }
    Some(UNIX_EPOCH + Duration::from_secs(days as u64 * 86_400 + hh * 3600 + mm * 60 + ss))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trip_google_auth_format() {
        let t = UNIX_EPOCH + Duration::from_secs(1_791_547_200) + Duration::from_micros(123_456);
        let s = format_expiry(t);
        assert_eq!(s, "2026-10-09T12:00:00.123456Z");
        assert_eq!(parse_expiry(&s).unwrap(), UNIX_EPOCH + Duration::from_secs(1_791_547_200));
        assert_eq!(format_expiry(UNIX_EPOCH + Duration::from_secs(1_791_547_200)), "2026-10-09T12:00:00Z");
        assert_eq!(parse_expiry("2024-02-29T23:59:59Z").map(format_expiry), Some("2024-02-29T23:59:59Z".into()));
        assert!(parse_expiry("garbage").is_none());
    }
}
