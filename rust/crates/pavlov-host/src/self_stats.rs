//! The bot process's own footprint, read from `/proc/self` (the Linux equivalent of the C#
//! `Process.GetCurrentProcess()` figures `/botstats` shows). Every reading is optional: a
//! missing or unreadable `/proc` gives `None`, never a made-up zero.

use std::time::Duration;

/// The kernel's `USER_HZ`: the unit of `/proc/<pid>/stat`'s CPU and start times. It is part of
/// the kernel ABI and 100 on every mainstream architecture; reading it via `sysconf` would need
/// `unsafe`, which this workspace forbids.
const USER_HZ: f64 = 100.0;

/// What the process is using.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct SelfStats {
    pub resident_bytes: Option<u64>,
    pub threads: Option<u64>,
    pub cpu_time: Option<Duration>,
    pub uptime: Option<Duration>,
}

fn status_value(status: &str, key: &str) -> Option<u64> {
    status
        .lines()
        .find_map(|l| l.strip_prefix(key))
        .and_then(|rest| rest.split_whitespace().next())
        .and_then(|v| v.parse().ok())
}

/// The fields of `/proc/<pid>/stat` after the command name, which may itself hold spaces and
/// parentheses: everything after the LAST `)`.
fn stat_fields(stat: &str) -> Option<Vec<&str>> {
    let after = stat.rfind(')').map(|i| &stat[i + 1..])?;
    Some(after.split_whitespace().collect())
}

/// Parse the readings from the three files' contents (pure, so it is tested without `/proc`).
#[must_use]
pub fn parse(status: Option<&str>, stat: Option<&str>, uptime: Option<&str>) -> SelfStats {
    let fields = stat.and_then(stat_fields);
    // After the name: state is field 0, utime 11, stime 12, starttime 19 (fields 3, 14, 15, 22
    // of proc(5)).
    let tick = |i: usize| {
        fields
            .as_ref()
            .and_then(|f| f.get(i))
            .and_then(|v| v.parse::<u64>().ok())
    };
    #[allow(clippy::cast_precision_loss)]
    let cpu_time = match (tick(11), tick(12)) {
        (Some(user), Some(system)) => {
            Some(Duration::from_secs_f64((user + system) as f64 / USER_HZ))
        }
        _ => None,
    };
    let since_boot = uptime
        .and_then(|u| u.split_whitespace().next())
        .and_then(|v| v.parse::<f64>().ok());
    #[allow(clippy::cast_precision_loss)]
    let uptime = match (since_boot, tick(19)) {
        (Some(now), Some(start)) => {
            let seconds = now - start as f64 / USER_HZ;
            (seconds >= 0.0).then(|| Duration::from_secs_f64(seconds))
        }
        _ => None,
    };
    SelfStats {
        resident_bytes: status
            .and_then(|s| status_value(s, "VmRSS:"))
            .map(|kb| kb * 1024),
        threads: status.and_then(|s| status_value(s, "Threads:")),
        cpu_time,
        uptime,
    }
}

/// This process, now.
#[must_use]
pub fn read() -> SelfStats {
    let file = |p: &str| std::fs::read_to_string(p).ok();
    parse(
        file("/proc/self/status").as_deref(),
        file("/proc/self/stat").as_deref(),
        file("/proc/uptime").as_deref(),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_name_with_spaces_and_parentheses_does_not_shift_the_fields() {
        let stat = "123 (bot (a) b) S 1 2 3 4 5 6 7 8 9 10 250 50 0 0 20 0 9 0 1000 0 0";
        let stats = parse(
            Some("VmRSS:\t  2048 kB\nThreads:\t9\n"),
            Some(stat),
            Some("110.00 5.0"),
        );
        assert_eq!(stats.resident_bytes, Some(2048 * 1024));
        assert_eq!(stats.threads, Some(9));
        assert_eq!(stats.cpu_time, Some(Duration::from_secs(3)));
        assert_eq!(stats.uptime, Some(Duration::from_secs(100)));
    }

    #[test]
    fn nothing_readable_is_nothing_known() {
        assert_eq!(parse(None, None, None), SelfStats::default());
    }
}
