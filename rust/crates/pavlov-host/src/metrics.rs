//! In-process metrics, rendered as Prometheus text (C# `Observability/MetricsRegistry.cs`).
//!
//! BOUNDED: at most [`MAX_SERIES`] series in total; a new series past that is dropped and
//! counted, never allowed to grow memory without limit. Labels must be bounded by the caller
//! (see `RconClient::metric_verb`). Recording never fails the caller.

use std::collections::BTreeMap;
use std::future::Future;
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::Instant;

/// The most series the registry will hold.
pub const MAX_SERIES: usize = 5000;

/// Histogram buckets in milliseconds.
pub const DEFAULT_BUCKETS: [f64; 11] = [
    5.0, 10.0, 25.0, 50.0, 100.0, 250.0, 500.0, 1000.0, 2500.0, 5000.0, 10000.0,
];

/// A series' labels, compared and rendered in key order.
#[derive(Debug, Clone, Default, PartialEq, Eq, Hash)]
pub struct MetricLabels(Vec<(String, String)>);

impl MetricLabels {
    /// No labels.
    #[must_use]
    pub fn none() -> Self {
        Self::default()
    }

    #[must_use]
    pub fn of(key: &str, value: &str) -> Self {
        Self(vec![(key.to_owned(), value.to_owned())])
    }

    #[must_use]
    pub fn of2(k1: &str, v1: &str, k2: &str, v2: &str) -> Self {
        Self(vec![
            (k1.to_owned(), v1.to_owned()),
            (k2.to_owned(), v2.to_owned()),
        ])
    }

    /// These labels and one more.
    #[must_use]
    pub fn with(&self, key: &str, value: &str) -> Self {
        let mut next = self.0.clone();
        next.push((key.to_owned(), value.to_owned()));
        Self(next)
    }

    fn sorted(&self) -> Vec<&(String, String)> {
        let mut pairs: Vec<&(String, String)> = self.0.iter().collect();
        pairs.sort_by(|a, b| a.0.cmp(&b.0));
        pairs
    }

    /// `k=v,k=v`, the identity of a series.
    #[must_use]
    pub fn key(&self) -> String {
        self.sorted()
            .iter()
            .map(|(k, v)| format!("{k}={v}"))
            .collect::<Vec<_>>()
            .join(",")
    }

    /// `{k="v",k="v"}`, or nothing for no labels.
    #[must_use]
    pub fn render(&self) -> String {
        let pairs = self.sorted();
        if pairs.is_empty() {
            return String::new();
        }
        let escaped: Vec<String> = pairs
            .iter()
            .map(|(k, v)| {
                format!(
                    "{k}=\"{}\"",
                    v.replace('\\', "\\\\")
                        .replace('\n', "\\n")
                        .replace('"', "\\\"")
                )
            })
            .collect();
        format!("{{{}}}", escaped.join(","))
    }
}

/// The three metric kinds.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MetricType {
    Counter,
    Gauge,
    Histogram,
}

impl MetricType {
    const fn name(self) -> &'static str {
        match self {
            Self::Counter => "counter",
            Self::Gauge => "gauge",
            Self::Histogram => "histogram",
        }
    }
}

#[derive(Debug)]
struct Series {
    labels: MetricLabels,
    value: f64,
    buckets: Vec<f64>,
    counts: Vec<u64>,
    sum: f64,
    count: u64,
}

#[derive(Debug)]
struct Metric {
    kind: MetricType,
    help: String,
    series: BTreeMap<String, Series>,
}

#[derive(Debug, Default)]
struct Inner {
    metrics: BTreeMap<String, Metric>,
    series: usize,
    dropped: usize,
}

/// Counts for `/diagnose`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MetricsStats {
    pub metrics: usize,
    pub series: usize,
    pub dropped: usize,
}

/// The registry. One per process.
#[derive(Debug)]
pub struct MetricsRegistry {
    prefix: String,
    inner: Mutex<Inner>,
}

impl Default for MetricsRegistry {
    fn default() -> Self {
        Self::new("bot")
    }
}

/// Prometheus numbers: shortest round-trip form, `+Inf`/`-Inf`/`NaN` spelled as the format wants.
fn number(value: f64) -> String {
    if value.is_nan() {
        "NaN".into()
    } else if value.is_infinite() {
        if value > 0.0 {
            "+Inf".into()
        } else {
            "-Inf".into()
        }
    } else {
        format!("{value}")
    }
}

impl MetricsRegistry {
    #[must_use]
    pub fn new(prefix: &str) -> Self {
        Self {
            prefix: prefix.to_owned(),
            inner: Mutex::new(Inner::default()),
        }
    }

    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn with_series(
        &self,
        name: &str,
        kind: MetricType,
        help: Option<&str>,
        labels: &MetricLabels,
        buckets: Option<&[f64]>,
        update: impl FnOnce(&mut Series),
    ) {
        let mut inner = self.lock();
        let Inner {
            metrics,
            series: total,
            dropped,
        } = &mut *inner;
        let metric = metrics.entry(name.to_owned()).or_insert_with(|| Metric {
            kind,
            help: help.unwrap_or(name).to_owned(),
            series: BTreeMap::new(),
        });
        // First caller with real help text wins; later ones do not clobber it with the name.
        if let Some(help) = help
            && metric.help == name
        {
            help.clone_into(&mut metric.help);
        }
        let key = labels.key();
        if !metric.series.contains_key(&key) {
            if *total >= MAX_SERIES {
                *dropped += 1;
                return;
            }
            let buckets = buckets.map(<[f64]>::to_vec).unwrap_or_default();
            metric.series.insert(
                key.clone(),
                Series {
                    labels: labels.clone(),
                    value: 0.0,
                    counts: vec![0; buckets.len()],
                    buckets,
                    sum: 0.0,
                    count: 0,
                },
            );
            *total += 1;
        }
        if let Some(series) = metric.series.get_mut(&key) {
            update(series);
        }
    }

    pub fn increment(&self, name: &str, labels: &MetricLabels, by: f64, help: Option<&str>) {
        self.with_series(name, MetricType::Counter, help, labels, None, |s| {
            s.value += by
        });
    }

    pub fn gauge(&self, name: &str, value: f64, labels: &MetricLabels, help: Option<&str>) {
        self.with_series(name, MetricType::Gauge, help, labels, None, |s| {
            s.value = value
        });
    }

    pub fn observe(&self, name: &str, value: f64, labels: &MetricLabels, help: Option<&str>) {
        self.observe_with(name, value, labels, help, &DEFAULT_BUCKETS);
    }

    /// [`observe`](Self::observe) into these buckets (fixed by the series' first observation).
    pub fn observe_with(
        &self,
        name: &str,
        value: f64,
        labels: &MetricLabels,
        help: Option<&str>,
        buckets: &[f64],
    ) {
        self.with_series(
            name,
            MetricType::Histogram,
            help,
            labels,
            Some(buckets),
            |s| {
                s.sum += value;
                s.count += 1;
                // Cumulative, per Prometheus: le="10" counts everything <= 10.
                for (bucket, count) in s.buckets.iter().zip(s.counts.iter_mut()) {
                    if value <= *bucket {
                        *count += 1;
                    }
                }
            },
        );
    }

    /// Time an operation in milliseconds, labelled `outcome=success|failure`.
    ///
    /// # Errors
    /// Whatever the operation returns.
    pub async fn time<T, E, F>(
        &self,
        name: &str,
        labels: &MetricLabels,
        help: Option<&str>,
        operation: F,
    ) -> Result<T, E>
    where
        F: Future<Output = Result<T, E>>,
    {
        let started = Instant::now();
        let result = operation.await;
        let outcome = if result.is_ok() { "success" } else { "failure" };
        self.observe(
            name,
            started.elapsed().as_secs_f64() * 1000.0,
            &labels.with("outcome", outcome),
            help,
        );
        result
    }

    /// The Prometheus text exposition, metrics in name order.
    #[must_use]
    pub fn render(&self) -> String {
        let inner = self.lock();
        let mut out = String::new();
        for (name, metric) in &inner.metrics {
            let full = format!("{}_{name}", self.prefix);
            out.push_str(&format!("# HELP {full} {}\n", metric.help));
            out.push_str(&format!("# TYPE {full} {}\n", metric.kind.name()));
            for series in metric.series.values() {
                if metric.kind == MetricType::Histogram {
                    for (bucket, count) in series.buckets.iter().zip(&series.counts) {
                        out.push_str(&format!(
                            "{full}_bucket{} {count}\n",
                            series.labels.with("le", &number(*bucket)).render()
                        ));
                    }
                    out.push_str(&format!(
                        "{full}_bucket{} {}\n",
                        series.labels.with("le", "+Inf").render(),
                        series.count
                    ));
                    out.push_str(&format!(
                        "{full}_sum{} {}\n",
                        series.labels.render(),
                        number(series.sum)
                    ));
                    out.push_str(&format!(
                        "{full}_count{} {}\n",
                        series.labels.render(),
                        series.count
                    ));
                } else {
                    out.push_str(&format!(
                        "{full}{} {}\n",
                        series.labels.render(),
                        number(series.value)
                    ));
                }
            }
        }
        out
    }

    /// How much the registry holds, and how many new series were refused.
    #[must_use]
    pub fn stats(&self) -> MetricsStats {
        let inner = self.lock();
        MetricsStats {
            metrics: inner.metrics.len(),
            series: inner.series,
            dropped: inner.dropped,
        }
    }

    /// Forget everything.
    pub fn reset(&self) {
        *self.lock() = Inner::default();
    }
}
