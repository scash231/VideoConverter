//! Number formatting the way C# string interpolation does it (`{x:F1}` uses the current culture).

/// The parts of `CultureInfo.CurrentCulture.NumberFormat` that `F` formatting uses.
/// The WPF host sends its own values at start-up, so Ferrite formats exactly like Native did.
#[derive(Debug, Clone)]
pub struct Culture {
    pub name: String,
    pub decimal_separator: String,
    pub negative_sign: String,
    pub positive_infinity: String,
    pub negative_infinity: String,
    pub nan: String,
}

impl Default for Culture {
    fn default() -> Self {
        Culture {
            name: String::new(),
            decimal_separator: ".".into(),
            negative_sign: "-".into(),
            positive_infinity: "Infinity".into(),
            negative_infinity: "-Infinity".into(),
            nan: "NaN".into(),
        }
    }
}

/// `value.ToString("F{decimals}", culture)`.
///
/// .NET Core 3.0+ formats the exact binary value and rounds ties to even, which is what Rust's
/// `{:.N}` does as well (verified against .NET 10: 2.25 → "2.2", 12.5 → "12", 13.5 → "14").
pub fn fixed(value: f64, decimals: usize, culture: &Culture) -> String {
    if value.is_nan() {
        return culture.nan.clone();
    }
    if value.is_infinite() {
        return if value > 0.0 {
            culture.positive_infinity.clone()
        } else {
            culture.negative_infinity.clone()
        };
    }
    let s = format!("{:.*}", decimals, value);
    let (neg, digits) = match s.strip_prefix('-') {
        Some(rest) => (true, rest),
        None => (false, s.as_str()),
    };
    let mut out = String::with_capacity(s.len() + 2);
    if neg {
        out.push_str(&culture.negative_sign);
    }
    match digits.split_once('.') {
        Some((int, frac)) => {
            out.push_str(int);
            out.push_str(&culture.decimal_separator);
            out.push_str(frac);
        }
        None => out.push_str(digits),
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn de() -> Culture {
        Culture {
            name: "de-DE".into(),
            decimal_separator: ",".into(),
            negative_sign: "-".into(),
            positive_infinity: "∞".into(),
            negative_infinity: "-∞".into(),
            nan: "NaN".into(),
        }
    }

    /// Expected values were produced by .NET 10 on a de-DE system.
    #[test]
    fn matches_dotnet_fixed_formatting() {
        let c = de();
        assert_eq!(fixed(45.3, 1, &c), "45,3");
        assert_eq!(fixed(2.25, 1, &c), "2,2");
        assert_eq!(fixed(0.125, 2, &c), "0,12");
        assert_eq!(fixed(12.5, 0, &c), "12");
        assert_eq!(fixed(13.5, 0, &c), "14");
        assert_eq!(fixed(1.005, 2, &c), "1,00");
        assert_eq!(fixed(99.95, 1, &c), "100,0");
        assert_eq!(fixed(100.0, 1, &c), "100,0");
        assert_eq!(fixed(-0.0, 1, &c), "-0,0");
        assert_eq!(fixed(f64::INFINITY, 1, &c), "∞");
    }
}
