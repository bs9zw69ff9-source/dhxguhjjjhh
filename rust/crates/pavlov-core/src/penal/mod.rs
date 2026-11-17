//! The penal code and the booking maths (C# `Core/Penal/PenalCode.cs`).
//!
//! THE TABLE WAS GENERATED FROM THE C# SOURCE, not retyped: 72 rows of bail figures typed by
//! hand is 72 chances to charge a player the wrong amount. Two independent axes, which PC 707
//! proves are separate: what the SENTENCE is, and how BAIL is decided.
//!
//! `rate` (the `/bail increase|decrease` multiplier) is applied and rounded PER CHARGE, so a
//! booking's bail always equals the sum of the figures shown for its lines.

use std::collections::{BTreeMap, HashMap};
use std::sync::LazyLock;

use crate::text::Currency;
use crate::text::case::fold_case;

/// Severity of a charge.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ChargeClass {
    Infraction,
    Misdemeanor,
    Felony,
    MisdemeanorOrFelony,
}

/// How bail is decided for a charge.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BailRule {
    /// A figure in dollars, scaled by the server's bail rate.
    Fixed,
    /// NOT BAILABLE: the sentence is served, money does not shorten it. This is what "N/A"
    /// means in the sheet's bail column. Reading it as "the officer decides" would let somebody
    /// buy their way out of manslaughter for whatever was typed.
    None,
    /// Set by whatever crime this charge attaches to: see PC 700.
    Associated,
}

/// One charge.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Charge {
    /// e.g. `PC 104`, `VC 604`.
    pub code: &'static str,
    pub name: &'static str,
    pub class: ChargeClass,
    /// 0 for an infraction with no jail, or a ranging sentence.
    pub jail_minutes: i32,
    /// Dollars. `None` whenever `rule` is not [`BailRule::Fixed`].
    pub bail: Option<i32>,
    pub rule: BailRule,
    /// The sentence depends on the associated crime: see PC 707.
    pub sentence_ranges: bool,
}

impl Charge {
    const fn new(
        code: &'static str,
        name: &'static str,
        class: ChargeClass,
        jail_minutes: i32,
        bail: Option<i32>,
        rule: BailRule,
        sentence_ranges: bool,
    ) -> Self {
        Self {
            code,
            name,
            class,
            jail_minutes,
            bail,
            rule,
            sentence_ranges,
        }
    }

    /// The hundreds series: `PC 104` is 100.
    #[must_use]
    pub fn section(&self) -> i32 {
        PenalCode::section_of(self.code)
    }

    /// Bail at `rate`, rounded half away from zero to the dollar. `None` when there is no figure.
    #[must_use]
    pub fn bail_at(&self, rate: f64) -> Option<i32> {
        // A rate is a small admin-set multiplier; the product of a sheet figure and it fits i32.
        #[allow(clippy::cast_possible_truncation)]
        self.bail.map(|b| (f64::from(b) * rate).round() as i32)
    }

    /// False when no amount of money gets this player out early.
    #[must_use]
    pub fn bailable(&self) -> bool {
        self.rule != BailRule::None
    }
}

/// Every charge, in sheet order.
pub const CHARGES: [Charge; 72] = [
    // ---- Public Order Offenses ----
    Charge::new(
        "PC 100",
        "Disorderly Conduct",
        ChargeClass::Misdemeanor,
        2,
        Some(10),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 101",
        "Disturbing the Peace",
        ChargeClass::Infraction,
        1,
        Some(15),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 102",
        "Public Intoxication",
        ChargeClass::Infraction,
        1,
        Some(15),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 103",
        "Loitering",
        ChargeClass::Infraction,
        1,
        Some(30),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 104",
        "Trespassing",
        ChargeClass::Misdemeanor,
        2,
        Some(50),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 105",
        "Unlawful Assembly",
        ChargeClass::Misdemeanor,
        3,
        Some(50),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 106",
        "Non-Peaceful Riot",
        ChargeClass::Felony,
        7,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 107",
        "Failure to Obey a Lawful Order",
        ChargeClass::Misdemeanor,
        3,
        Some(75),
        BailRule::Fixed,
        false,
    ),
    // ---- Crimes Against Persons ----
    Charge::new(
        "PC 200",
        "Assault",
        ChargeClass::Misdemeanor,
        4,
        Some(75),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 201",
        "Aggravated Assault",
        ChargeClass::Felony,
        5,
        Some(250),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 202",
        "Battery",
        ChargeClass::Misdemeanor,
        4,
        Some(100),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 203",
        "Aggravated Battery",
        ChargeClass::Felony,
        5,
        Some(250),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 204",
        "Menacing",
        ChargeClass::Misdemeanor,
        4,
        Some(150),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 205",
        "Aggravated Menacing",
        ChargeClass::Felony,
        6,
        Some(500),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 206",
        "Brandishing a Weapon",
        ChargeClass::Misdemeanor,
        3,
        Some(150),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 207",
        "Kidnapping",
        ChargeClass::Felony,
        5,
        Some(700),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 208",
        "Fraud",
        ChargeClass::Misdemeanor,
        3,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 209",
        "Attempted Murder",
        ChargeClass::Felony,
        7,
        Some(75),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 210",
        "Homicide",
        ChargeClass::Felony,
        8,
        Some(250),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 211",
        "Mass Homicide",
        ChargeClass::Felony,
        10,
        None,
        BailRule::None,
        false,
    ),
    // ---- Property Crimes ----
    Charge::new(
        "PC 300",
        "Petty Theft (<$500)",
        ChargeClass::Misdemeanor,
        3,
        Some(400),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 301",
        "Grand Larceny (>$500)",
        ChargeClass::Felony,
        5,
        Some(500),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 302",
        "Burglary",
        ChargeClass::Felony,
        6,
        Some(100),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 303",
        "Robbery",
        ChargeClass::Felony,
        5,
        Some(75),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 304",
        "Armed Robbery",
        ChargeClass::Felony,
        7,
        Some(75),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 305",
        "Possession of Stolen Property",
        ChargeClass::Misdemeanor,
        4,
        Some(150),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 306",
        "Criminal Mischief",
        ChargeClass::Misdemeanor,
        3,
        Some(450),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 307",
        "Vandalism",
        ChargeClass::Misdemeanor,
        3,
        Some(300),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 308",
        "Attempted Burglary",
        ChargeClass::Misdemeanor,
        4,
        Some(150),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 309",
        "Extortion",
        ChargeClass::Felony,
        6,
        Some(475),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 310",
        "Grand Theft Auto",
        ChargeClass::Felony,
        7,
        None,
        BailRule::None,
        false,
    ),
    // ---- Weapons Offenses ----
    Charge::new(
        "PC 400",
        "Unlawful Discharge of a Firearm",
        ChargeClass::Misdemeanor,
        4,
        Some(450),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 401",
        "Carrying a Concealed Weapon Without Permit",
        ChargeClass::Misdemeanor,
        5,
        Some(150),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 402",
        "Possession of Illegal Automatic Weapons",
        ChargeClass::Felony,
        6,
        Some(100),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 403",
        "Possession of Explosives",
        ChargeClass::Felony,
        8,
        Some(500),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 404",
        "Illegal Sale of Firearms",
        ChargeClass::Felony,
        6,
        Some(75),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 405",
        "Brandishing of a Firearm",
        ChargeClass::Misdemeanor,
        3,
        None,
        BailRule::None,
        false,
    ),
    // ---- Crimes Against Government ----
    Charge::new(
        "PC 500",
        "Resisting or Evading Arrest",
        ChargeClass::Misdemeanor,
        3,
        Some(300),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 501",
        "Obstruction of Justice",
        ChargeClass::Misdemeanor,
        4,
        Some(250),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 502",
        "Escape from Custody",
        ChargeClass::Felony,
        5,
        Some(75),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 503",
        "False Report",
        ChargeClass::Misdemeanor,
        3,
        Some(10),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 504",
        "Impersonating a Police Officer",
        ChargeClass::Felony,
        6,
        Some(100),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 505",
        "Bribery of a Public Official",
        ChargeClass::Felony,
        5,
        Some(150),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 506",
        "Perjury",
        ChargeClass::Felony,
        6,
        Some(325),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 507",
        "Contempt of Court",
        ChargeClass::Misdemeanor,
        3,
        Some(10),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "PC 508",
        "Malfeasance",
        ChargeClass::Felony,
        5,
        None,
        BailRule::None,
        false,
    ),
    // ---- Vehicle Code ----
    Charge::new(
        "VC 600",
        "Speeding",
        ChargeClass::Infraction,
        0,
        Some(50),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 601",
        "Reckless Driving",
        ChargeClass::Misdemeanor,
        3,
        Some(5),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 602",
        "Driving While Intoxicated",
        ChargeClass::Misdemeanor,
        4,
        Some(350),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 603",
        "Hit and Run",
        ChargeClass::Felony,
        6,
        Some(550),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 604",
        "Failure to Yield",
        ChargeClass::Infraction,
        0,
        Some(75),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 605",
        "Evading and Eluding Police",
        ChargeClass::Felony,
        4,
        Some(200),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 606",
        "Driving Without a License",
        ChargeClass::Misdemeanor,
        2,
        Some(600),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 607",
        "Illegal Parking",
        ChargeClass::Infraction,
        0,
        Some(400),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 608",
        "Vehicular Assault",
        ChargeClass::Felony,
        5,
        Some(400),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 609",
        "Vehicular Manslaughter",
        ChargeClass::Felony,
        8,
        Some(150),
        BailRule::Fixed,
        false,
    ),
    Charge::new(
        "VC 610",
        "Following an Emergency Vehicle",
        ChargeClass::Misdemeanor,
        3,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "VC 611",
        "Failure to Maintain Lane",
        ChargeClass::Infraction,
        0,
        None,
        BailRule::None,
        false,
    ),
    // ---- Organized Crime ----
    Charge::new(
        "PC 700",
        "Illegal Gambling",
        ChargeClass::Misdemeanor,
        3,
        None,
        BailRule::Associated,
        false,
    ),
    Charge::new(
        "PC 701",
        "Bootlegging",
        ChargeClass::Felony,
        5,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 702",
        "Racketeering",
        ChargeClass::Felony,
        7,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 703",
        "Loan Sharking",
        ChargeClass::Felony,
        6,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 704",
        "Smuggling",
        ChargeClass::Felony,
        6,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 705",
        "Operating an Illegal Business",
        ChargeClass::Misdemeanor,
        4,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 706",
        "Criminal Conspiracy",
        ChargeClass::Felony,
        6,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 707",
        "Aiding and Abetting",
        ChargeClass::MisdemeanorOrFelony,
        0,
        None,
        BailRule::None,
        true,
    ),
    // ---- Controlled Substances ----
    Charge::new(
        "PC 801",
        "Possession of Controlled Substance",
        ChargeClass::Misdemeanor,
        3,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 802",
        "Possession of Narcotics with Intent to Distribute",
        ChargeClass::Felony,
        5,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 803",
        "Sale of Illegal Narcotics",
        ChargeClass::Felony,
        6,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 804",
        "Manufacturing Illegal Narcotics",
        ChargeClass::Felony,
        8,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 805",
        "Illegal Distribution of Prescription Drugs",
        ChargeClass::Felony,
        3,
        None,
        BailRule::None,
        false,
    ),
    Charge::new(
        "PC 806",
        "Possession of Drug Paraphernalia",
        ChargeClass::Misdemeanor,
        4,
        None,
        BailRule::None,
        false,
    ),
];

/// Section number to title.
pub const SECTION_TITLES: [(i32, &str); 8] = [
    (100, "Public Order Offenses"),
    (200, "Crimes Against Persons"),
    (300, "Property Crimes"),
    (400, "Weapons Offenses"),
    (500, "Crimes Against Government"),
    (600, "Vehicle Code"),
    (700, "Organized Crime"),
    (800, "Controlled Substances"),
];

static BY_CODE: LazyLock<HashMap<String, &'static Charge>> =
    LazyLock::new(|| CHARGES.iter().map(|c| (fold_case(c.code), c)).collect());

/// A booking: the charges that matched, and the sentence and bail they add up to.
#[derive(Debug, Clone, PartialEq)]
pub struct Booking {
    pub charges: Vec<&'static Charge>,
    pub jail_minutes: i32,
    pub bail: i32,
    pub bailable: bool,
    pub associated_bail: bool,
    pub ranges: bool,
    pub capped: bool,
    pub cap_minutes: Option<i32>,
}

impl Booking {
    /// `35 min`, `15 min (capped)`, `... + ranges with the associated charge`, or `No jail time`.
    #[must_use]
    pub fn sentence_label(&self) -> String {
        let mut parts = Vec::with_capacity(2);
        if self.jail_minutes > 0 {
            parts.push(format!(
                "{} min{}",
                self.jail_minutes,
                if self.capped { " (capped)" } else { "" }
            ));
        }
        if self.ranges {
            parts.push("ranges with the associated charge".to_owned());
        }
        if parts.is_empty() {
            "No jail time".to_owned()
        } else {
            parts.join(" + ")
        }
    }

    /// The bail line. NOT BAILABLE WINS OUTRIGHT: showing a price the player cannot actually
    /// pay sends them to the station with the money for nothing.
    #[must_use]
    pub fn bail_label(&self, currency: &Currency) -> String {
        if !self.bailable {
            return "No bail - the sentence must be served".to_owned();
        }
        let amount = currency.format(i64::from(self.bail));
        if !self.associated_bail {
            return amount;
        }
        if self.bail > 0 {
            format!("{amount} + based on the associated charge")
        } else {
            "Based on the associated charge".to_owned()
        }
    }
}

/// Namespace for the code, mirroring the C# static class.
#[derive(Debug)]
pub struct PenalCode;

impl PenalCode {
    /// The hundreds series of a code from its ASCII digits: `PC 104` is 100, no digits is 0.
    #[must_use]
    pub fn section_of(code: &str) -> i32 {
        let digits: String = code.chars().filter(char::is_ascii_digit).collect();
        // Overlong digit runs are not codes; they read as section 0 rather than overflowing.
        digits
            .parse::<i64>()
            .ok()
            .and_then(|n| i32::try_from(n / 100 * 100).ok())
            .unwrap_or(0)
    }

    /// The charge with this code, ignoring case.
    #[must_use]
    pub fn get(code: Option<&str>) -> Option<&'static Charge> {
        code.and_then(|c| BY_CODE.get(&fold_case(c)).copied())
    }

    /// Charges grouped by section, sections ascending, sheet order within each.
    #[must_use]
    pub fn sections() -> BTreeMap<i32, Vec<&'static Charge>> {
        let mut sections: BTreeMap<i32, Vec<&'static Charge>> = BTreeMap::new();
        for charge in &CHARGES {
            sections.entry(charge.section()).or_default().push(charge);
        }
        sections
    }

    /// `(number, title, count)` per section, ascending. An untitled section reads `Section N`.
    #[must_use]
    pub fn section_list() -> Vec<(i32, String, usize)> {
        Self::sections()
            .into_iter()
            .map(|(number, charges)| {
                let title = SECTION_TITLES
                    .iter()
                    .find(|(n, _)| *n == number)
                    .map_or_else(|| format!("Section {number}"), |(_, t)| (*t).to_owned());
                (number, title, charges.len())
            })
            .collect()
    }

    /// Book a player on `codes`. An unknown code is SKIPPED, never guessed at. A positive
    /// `cap_minutes` caps the sentence, and the booking says so: a receipt reading "35 min"
    /// for a 15 minute stay lies, and the officer needs to know the cap decided it.
    #[must_use]
    pub fn book<'a>(
        codes: impl IntoIterator<Item = &'a str>,
        rate: f64,
        cap_minutes: i32,
    ) -> Booking {
        let mut booking = Booking {
            charges: Vec::new(),
            jail_minutes: 0,
            bail: 0,
            bailable: true,
            associated_bail: false,
            ranges: false,
            capped: false,
            cap_minutes: None,
        };
        for code in codes {
            let Some(charge) = Self::get(Some(code)) else {
                continue;
            };
            booking.charges.push(charge);
            booking.jail_minutes = booking.jail_minutes.saturating_add(charge.jail_minutes);
            if let Some(b) = charge.bail_at(rate) {
                booking.bail = booking.bail.saturating_add(b);
            }
            match charge.rule {
                BailRule::None => booking.bailable = false,
                BailRule::Associated => booking.associated_bail = true,
                BailRule::Fixed => {}
            }
            if charge.sentence_ranges {
                booking.ranges = true;
            }
        }
        if cap_minutes > 0 && booking.jail_minutes > cap_minutes {
            booking.jail_minutes = cap_minutes;
            booking.capped = true;
            booking.cap_minutes = Some(cap_minutes);
        }
        booking
    }
}
