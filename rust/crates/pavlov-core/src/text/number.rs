//! Numbers as the C# printed them with invariant culture.

/// `ToString("N0")`: `1234567` as `1,234,567`, `-1250` as `-1,250`.
#[must_use]
pub fn group_thousands(amount: i64) -> String {
    let digits = amount.unsigned_abs().to_string();
    let mut out = String::with_capacity(digits.len() + digits.len() / 3 + 1);
    if amount < 0 {
        out.push('-');
    }
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i) % 3 == 0 {
            out.push(',');
        }
        out.push(c);
    }
    out
}

/// `ToString("F{decimals}")`: fixed decimals, midpoints rounded AWAY FROM ZERO as .NET formats
/// them. Rust's `{:.1}` rounds half to even, so `2.25` would read `2.2` where the C# wrote `2.3`.
#[must_use]
pub fn fixed(value: f64, decimals: u32) -> String {
    let scale = 10_f64.powi(i32::try_from(decimals).unwrap_or(0));
    let rounded = (value * scale).round() / scale;
    let width = decimals as usize;
    format!("{rounded:.width$}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fixed_rounds_midpoints_away_from_zero_like_dotnet() {
        assert_eq!(fixed(2.25, 1), "2.3");
        assert_eq!(fixed(2.5, 0), "3");
        assert_eq!(fixed(-2.5, 0), "-3");
        assert_eq!(fixed(8.0, 1), "8.0");
        assert_eq!(fixed(1.0 / 3.0, 1), "0.3");
        assert_eq!(group_thousands(18_000), "18,000");
    }
}
