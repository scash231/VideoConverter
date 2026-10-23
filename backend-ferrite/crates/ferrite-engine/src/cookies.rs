//! Port of `NativeEngineService.ResolveCookiesFile`: only the cookies file configured in
//! Settings → Advanced, never browser cookie scraping.

use crate::dotnet::fs::file_exists;
use crate::dotnet::text::is_null_or_whitespace;
use crate::settings::AppSettings;

pub fn resolve_cookies_file(settings: &AppSettings) -> Option<String> {
    let custom = settings.cookies_file_path.as_deref();
    if !is_null_or_whitespace(custom) && file_exists(custom.unwrap_or_default()) {
        return custom.map(str::to_owned);
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_existing_configured_files() {
        let mut s = AppSettings::default();
        assert_eq!(resolve_cookies_file(&s), None);
        s.cookies_file_path = Some("   ".into());
        assert_eq!(resolve_cookies_file(&s), None);
        s.cookies_file_path = Some(r"C:\definitely\missing\cookies.txt".into());
        assert_eq!(resolve_cookies_file(&s), None);
        s.cookies_file_path = None;
        assert_eq!(resolve_cookies_file(&s), None);
        let tmp = std::env::temp_dir().join(format!("ferrite_cookies_{}.txt", std::process::id()));
        std::fs::write(&tmp, "# Netscape HTTP Cookie File\n").unwrap();
        s.cookies_file_path = Some(tmp.display().to_string());
        assert_eq!(resolve_cookies_file(&s), Some(tmp.display().to_string()));
        std::fs::remove_file(&tmp).unwrap();
        // A directory is not a cookies file.
        s.cookies_file_path = Some(std::env::temp_dir().display().to_string());
        assert_eq!(resolve_cookies_file(&s), None);
    }
}
