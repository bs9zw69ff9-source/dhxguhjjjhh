//! The risk score (C# `Core/Intelligence/RiskModels.cs`, `RiskScorer.cs`).
//!
//! A SCORE THAT BANS THE WRONG PERSON IS WORSE THAN NO SCORE. Signals combine by NOISY-OR, so
//! weak ones saturate well below a single strong one; one signal per kind counts, so one fact
//! seen three times is one fact; and nothing here can recommend a ban, because the
//! recommendation type has no value that means one.

/// What was seen. The weight IS the variant, so the two cannot drift apart.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, serde::Serialize)]
pub enum RiskSignalKind {
    BannedAccount = 100,
    SharedAddressWithBanned = 60,
    EvasionMatch = 55,
    NameMatchesBanned = 40,
    Flagged = 35,
    PriorBan = 25,
    Anonymising = 15,
    ActiveWarnings = 10,
    BrandNewAccount = 5,
}

impl RiskSignalKind {
    /// Every kind, strongest first.
    pub const ALL: [Self; 9] = [
        Self::BannedAccount,
        Self::SharedAddressWithBanned,
        Self::EvasionMatch,
        Self::NameMatchesBanned,
        Self::Flagged,
        Self::PriorBan,
        Self::Anonymising,
        Self::ActiveWarnings,
        Self::BrandNewAccount,
    ];

    /// The weight.
    #[must_use]
    pub fn weight(self) -> i32 {
        self as i32
    }
}

/// One thing seen about a player.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct RiskSignal {
    pub kind: RiskSignalKind,
    pub summary: String,
    pub evidence: Option<String>,
    /// Derived from network data, so withheld from staff without network access.
    pub sensitive: bool,
}

impl RiskSignal {
    #[must_use]
    pub fn new(
        kind: RiskSignalKind,
        summary: impl Into<String>,
        evidence: Option<String>,
        sensitive: bool,
    ) -> Self {
        Self {
            kind,
            summary: summary.into(),
            evidence,
            sensitive,
        }
    }

    #[must_use]
    pub fn weight(&self) -> i32 {
        self.kind.weight()
    }
}

/// How much the signals can be relied on.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, serde::Serialize)]
pub enum RiskConfidence {
    Low,
    Medium,
    High,
}

/// What staff should do. NO VALUE MEANS BAN, by design.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, serde::Serialize)]
pub enum RiskRecommendation {
    None,
    Monitor,
    ReviewManually,
    Investigate,
}

impl RiskRecommendation {
    /// Every value, in order.
    pub const ALL: [Self; 4] = [
        Self::None,
        Self::Monitor,
        Self::ReviewManually,
        Self::Investigate,
    ];
}

/// The score as a band.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, serde::Serialize)]
pub enum RiskBand {
    None,
    Low,
    Medium,
    High,
    Critical,
}

/// The assessment.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize)]
pub struct RiskAssessment {
    pub score: i32,
    pub confidence: RiskConfidence,
    /// Every signal, strongest first, repeats included: staff need to see there were three.
    pub signals: Vec<RiskSignal>,
    pub assessment: String,
    pub recommendation: RiskRecommendation,
}

impl RiskAssessment {
    /// Nothing on record.
    #[must_use]
    pub fn clean() -> Self {
        Self {
            score: 0,
            confidence: RiskConfidence::Low,
            signals: Vec::new(),
            assessment: "Nothing on record.".to_owned(),
            recommendation: RiskRecommendation::None,
        }
    }

    #[must_use]
    pub fn band(&self) -> RiskBand {
        RiskScorer::band_of(self.score)
    }
}

/// Namespace for the scorer, mirroring the C# static class.
#[derive(Debug)]
pub struct RiskScorer;

impl RiskScorer {
    /// A weight at or above this is a strong signal.
    pub const STRONG_SIGNAL: i32 = 40;
    /// A weight at or above this is decisive on its own.
    pub const DECISIVE_SIGNAL: i32 = 60;

    /// Assess a set of signals.
    #[must_use]
    pub fn assess(signals: impl IntoIterator<Item = RiskSignal>) -> RiskAssessment {
        let mut ordered: Vec<RiskSignal> = signals.into_iter().collect();
        // Strongest first, stable, as LINQ's OrderByDescending.
        ordered.sort_by_key(|s| std::cmp::Reverse(s.weight()));
        if ordered.is_empty() {
            return RiskAssessment::clean();
        }

        // ONE PER KIND, strongest first: the repeats are dropped from the arithmetic only.
        let mut counted: Vec<&RiskSignal> = Vec::new();
        for signal in &ordered {
            if !counted.iter().any(|c| c.kind == signal.kind) {
                counted.push(signal);
            }
        }

        let score = Self::combine(counted.iter().map(|s| s.weight()));
        let confidence = Self::confidence_of(counted.iter().map(|s| s.weight()));
        let assessment = Self::describe(counted.first().map(|s| s.kind), score);
        RiskAssessment {
            score,
            confidence,
            assessment,
            recommendation: Self::recommendation_for(score, confidence),
            signals: ordered,
        }
    }

    /// Noisy-OR: each weight closes its fraction of what is left. Rounded half away from zero,
    /// so two signals worth 50 are 75, not 74.
    #[must_use]
    pub fn combine(weights: impl IntoIterator<Item = i32>) -> i32 {
        let remaining: f64 = weights
            .into_iter()
            .map(|w| 1.0 - f64::from(w.clamp(0, 100)) / 100.0)
            .product();
        // Bounded to 0..=100 by construction.
        #[allow(clippy::cast_possible_truncation)]
        let score = ((1.0 - remaining) * 100.0).round() as i32;
        score
    }

    /// High with any decisive signal or two strong ones; Medium with one strong or three of
    /// any; Low otherwise.
    #[must_use]
    pub fn confidence_of(weights: impl IntoIterator<Item = i32>) -> RiskConfidence {
        let weights: Vec<i32> = weights.into_iter().collect();
        let strong = weights
            .iter()
            .filter(|w| **w >= Self::STRONG_SIGNAL)
            .count();
        if weights.iter().any(|w| *w >= Self::DECISIVE_SIGNAL) || strong >= 2 {
            RiskConfidence::High
        } else if strong == 1 || weights.len() >= 3 {
            RiskConfidence::Medium
        } else {
            RiskConfidence::Low
        }
    }

    /// The band a score falls in.
    #[must_use]
    pub fn band_of(score: i32) -> RiskBand {
        match score {
            i32::MIN..=9 => RiskBand::None,
            10..=29 => RiskBand::Low,
            30..=54 => RiskBand::Medium,
            55..=79 => RiskBand::High,
            _ => RiskBand::Critical,
        }
    }

    /// The sentence, driven by the STRONGEST SIGNAL rather than the score: two players on 82
    /// can be there for completely different reasons.
    #[must_use]
    pub fn describe(top: Option<RiskSignalKind>, score: i32) -> String {
        let Some(top) = top else {
            return "Nothing on record.".to_owned();
        };
        match top {
            RiskSignalKind::BannedAccount => "This account is banned. Not a prediction - it is on the ban list now.",
            RiskSignalKind::SharedAddressWithBanned | RiskSignalKind::EvasionMatch => {
                "Consistent with ban evasion. Addresses are shared by households, phone tethering and student halls, so this is a reason to look rather than a finding."
            }
            RiskSignalKind::NameMatchesBanned => "A name here matches a banned player. Names are not identifiers and can be reused by anybody, so on its own this proves nothing.",
            RiskSignalKind::Flagged => "Carrying a standing ban-evasion flag. Check whether the flag should still exist.",
            RiskSignalKind::PriorBan => "Has prior moderation history. That is a record, not a prediction about now.",
            RiskSignalKind::Anonymising => "Connecting through a VPN, proxy or hosting network. Common enough among ordinary players that it means little by itself.",
            RiskSignalKind::ActiveWarnings | RiskSignalKind::BrandNewAccount => {
                if score >= 30 {
                    "Several weak indicators and nothing decisive. Worth an eye, not an action."
                } else {
                    "Nothing that stands out."
                }
            }
        }
        .to_owned()
    }

    /// The recommendation. LOW CONFIDENCE CAPS IT at Monitor however high the score, so the
    /// weakest data in the system can never drive the strongest action.
    #[must_use]
    pub fn recommendation_for(score: i32, confidence: RiskConfidence) -> RiskRecommendation {
        if score < 10 {
            return RiskRecommendation::None;
        }
        if confidence == RiskConfidence::Low {
            return RiskRecommendation::Monitor;
        }
        match Self::band_of(score) {
            RiskBand::Critical if confidence == RiskConfidence::High => {
                RiskRecommendation::Investigate
            }
            RiskBand::Critical | RiskBand::High => RiskRecommendation::ReviewManually,
            _ => RiskRecommendation::Monitor,
        }
    }
}
