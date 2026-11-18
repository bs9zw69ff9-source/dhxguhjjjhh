/// How a server writes an amount of money: `$1,250`.
///
/// THE ONE MONEY FORMATTER (C# `Core/Text/Currency.cs`). The separator is always a comma,
/// whatever the locale of the machine running the bot, because a figure on a receipt must
/// not change shape between hosts.
///
/// Dollars only, as in the C#: the caps currency went with the fallout theme (2026-11-17).
/// The type stays general so a currency is still data, not a code path.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Currency {
    /// Written before the figure, e.g. `$`.
    pub prefix: String,
    /// Written after the figure.
    pub suffix: String,
}

impl Currency {
    /// Dollars: `$1,250`.
    #[must_use]
    pub fn dollars() -> Self {
        Self {
            prefix: "$".to_owned(),
            suffix: String::new(),
        }
    }

    /// `prefix + amount with thousands separators + suffix`, matching .NET's
    /// `ToString("N0", InvariantCulture)`: `-1,250` for a negative amount.
    #[must_use]
    pub fn format(&self, amount: i64) -> String {
        format!(
            "{}{}{}",
            self.prefix,
            super::number::group_thousands(amount),
            self.suffix
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dollars_group_with_commas_in_every_locale() {
        let d = Currency::dollars();
        assert_eq!(d.format(0), "$0");
        assert_eq!(d.format(999), "$999");
        assert_eq!(d.format(1_250), "$1,250");
        assert_eq!(d.format(1_234_567), "$1,234,567");
        assert_eq!(d.format(-1_250), "$-1,250");
        assert_eq!(d.format(i64::MIN), "$-9,223,372,036,854,775,808");
    }
}
