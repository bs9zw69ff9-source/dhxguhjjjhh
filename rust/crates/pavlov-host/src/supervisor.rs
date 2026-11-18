//! What keeps the bot running, so a command that needs a restart can ask for one (C#
//! `Servers/BotSupervisor.cs`).

use pavlov_core::servers::units::is_plausible_unit_name;

/// The process manager the bot runs under.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SupervisorKind {
    None,
    Pm2,
    Systemd,
}

/// The supervisor and the name it knows the bot by.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BotSupervisor {
    pub kind: SupervisorKind,
    pub name: String,
}

impl BotSupervisor {
    /// No process manager.
    #[must_use]
    pub fn unmanaged() -> Self {
        Self {
            kind: SupervisorKind::None,
            name: String::new(),
        }
    }

    #[must_use]
    pub fn describe(&self) -> String {
        match self.kind {
            SupervisorKind::Pm2 => format!("pm2 app `{}`", self.name),
            SupervisorKind::Systemd => format!("systemd unit `{}`", self.name),
            SupervisorKind::None => "no process manager".into(),
        }
    }

    /// The command that restarts the bot, or `None` when nothing supervises it. `--no-block`,
    /// so systemd does not wait on the process it is about to stop.
    #[must_use]
    pub fn restart_command(&self) -> Option<(String, Vec<String>)> {
        match self.kind {
            SupervisorKind::Pm2 => Some(("pm2".into(), vec!["restart".into(), self.name.clone()])),
            SupervisorKind::Systemd => Some((
                "systemctl".into(),
                vec!["restart".into(), "--no-block".into(), self.name.clone()],
            )),
            SupervisorKind::None => None,
        }
    }

    /// This process's supervisor, from its environment and `/proc/self/cgroup`.
    #[must_use]
    pub fn current() -> Self {
        let cgroup = std::fs::read_to_string("/proc/self/cgroup").ok();
        Self::detect(|k| std::env::var(k).ok(), cgroup.as_deref())
    }

    /// The decision, from injected sources. pm2 sets `pm_id`; systemd sets `INVOCATION_ID`, and
    /// the unit is `PAVLOV_SYSTEMD_UNIT` or the `.service` in the cgroup path.
    #[must_use]
    pub fn detect(env: impl Fn(&str) -> Option<String>, cgroup: Option<&str>) -> Self {
        if env("pm_id").is_some_and(|v| !v.is_empty()) {
            let app = env("PAVLOV_PM2_APP")
                .or_else(|| env("name"))
                .unwrap_or_else(|| "pavlov-bot-cs".into());
            return Self {
                kind: SupervisorKind::Pm2,
                name: app,
            };
        }
        if env("INVOCATION_ID").is_none_or(|v| v.is_empty()) {
            return Self::unmanaged();
        }
        let unit = env("PAVLOV_SYSTEMD_UNIT").or_else(|| unit_from_cgroup(cgroup));
        match unit {
            Some(unit) if is_plausible_unit_name(&unit) => match unit.strip_suffix(".service") {
                Some(name) => Self {
                    kind: SupervisorKind::Systemd,
                    name: name.to_owned(),
                },
                None => Self::unmanaged(),
            },
            _ => Self::unmanaged(),
        }
    }
}

/// The `.service` a systemd cgroup line names (cgroup v2 `0::/...` or v1 `name=systemd`).
#[must_use]
pub fn unit_from_cgroup(cgroup: Option<&str>) -> Option<String> {
    cgroup?
        .split('\n')
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .find_map(|line| {
            let mut parts = line.splitn(3, ':');
            let (_, controller, path) = (parts.next()?, parts.next()?, parts.next()?);
            if !(controller.is_empty() || controller == "name=systemd") {
                return None;
            }
            let last = path.rsplit('/').next().unwrap_or(path);
            last.ends_with(".service").then(|| last.to_owned())
        })
}
