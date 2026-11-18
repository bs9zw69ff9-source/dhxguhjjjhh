//! Outbound HTTP behind a trait (the C# `HttpClient` and its `HttpMessageHandler` seam), so
//! the master list, the VPN detectors and the webhooks are tested against canned answers.
//!
//! BOUNDED: every request carries its own timeout, and a response body is read up to a cap and
//! then refused, so a misbehaving endpoint can neither hang a caller nor exhaust memory.
//! Header values are never printed: they carry API keys.

use std::fmt;
use std::time::Duration;

use futures_util::future::BoxFuture;

/// The default response-body cap: far above any real answer, far below harm.
pub const DEFAULT_MAX_BODY: usize = 16 * 1024 * 1024;

/// The methods the bot uses.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Method {
    Get,
    Post,
    Patch,
    Delete,
}

/// One request.
#[derive(Clone)]
pub struct HttpRequest {
    pub method: Method,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<String>,
    pub timeout: Duration,
    pub max_body: usize,
}

impl fmt::Debug for HttpRequest {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        // Header NAMES only, and no query string: both can carry a key.
        let names: Vec<&str> = self.headers.iter().map(|(k, _)| k.as_str()).collect();
        f.debug_struct("HttpRequest")
            .field("method", &self.method)
            .field("url", &self.url.split('?').next().unwrap_or_default())
            .field("headers", &names)
            .field("body_len", &self.body.as_ref().map(String::len))
            .field("timeout", &self.timeout)
            .finish_non_exhaustive()
    }
}

impl HttpRequest {
    #[must_use]
    pub fn new(method: Method, url: impl Into<String>, timeout: Duration) -> Self {
        Self {
            method,
            url: url.into(),
            headers: Vec::new(),
            body: None,
            timeout,
            max_body: DEFAULT_MAX_BODY,
        }
    }

    #[must_use]
    pub fn get(url: impl Into<String>, timeout: Duration) -> Self {
        Self::new(Method::Get, url, timeout)
    }

    /// A JSON POST.
    #[must_use]
    pub fn post_json(url: impl Into<String>, body: impl Into<String>, timeout: Duration) -> Self {
        let mut request = Self::new(Method::Post, url, timeout);
        request.body = Some(body.into());
        request.header("Content-Type", "application/json")
    }

    #[must_use]
    pub fn header(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.headers.push((name.into(), value.into()));
        self
    }
}

/// The answer: any status, the raw body (an image is not text), and its declared type.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpResponse {
    pub status: u16,
    pub content_type: Option<String>,
    pub body: Vec<u8>,
}

impl HttpResponse {
    /// A text answer with no declared type.
    #[must_use]
    pub fn new(status: u16, body: impl Into<String>) -> Self {
        Self {
            status,
            content_type: None,
            body: body.into().into_bytes(),
        }
    }

    /// The body as text; invalid UTF-8 is replaced, never an error.
    #[must_use]
    pub fn text(&self) -> std::borrow::Cow<'_, str> {
        String::from_utf8_lossy(&self.body)
    }

    #[must_use]
    pub fn is_success(&self) -> bool {
        (200..300).contains(&self.status)
    }
}

/// Why no answer arrived.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HttpError {
    Timeout(Duration),
    /// DNS, TLS, connection refused, a reset.
    Transport(String),
    /// The body passed [`HttpRequest::max_body`].
    TooLarge(usize),
}

impl fmt::Display for HttpError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Timeout(after) => write!(f, "no answer within {}s", after.as_secs()),
            Self::Transport(why) => f.write_str(why),
            Self::TooLarge(cap) => write!(f, "the answer was larger than {cap} bytes"),
        }
    }
}

impl std::error::Error for HttpError {}

/// Sends requests.
pub trait HttpClient: Send + Sync {
    fn send(&self, request: HttpRequest) -> BoxFuture<'_, Result<HttpResponse, HttpError>>;
}

/// The real client: rustls, the system proxy settings, one connection pool.
#[derive(Debug, Clone)]
pub struct ReqwestClient {
    client: reqwest::Client,
}

impl ReqwestClient {
    /// # Errors
    /// When the TLS backend cannot be initialised.
    pub fn new(user_agent: &str) -> Result<Self, HttpError> {
        reqwest::Client::builder()
            .user_agent(user_agent)
            .connect_timeout(Duration::from_secs(10))
            .build()
            .map(|client| Self { client })
            .map_err(|e| HttpError::Transport(e.to_string()))
    }

    async fn execute(&self, request: HttpRequest) -> Result<HttpResponse, HttpError> {
        let method = match request.method {
            Method::Get => reqwest::Method::GET,
            Method::Post => reqwest::Method::POST,
            Method::Patch => reqwest::Method::PATCH,
            Method::Delete => reqwest::Method::DELETE,
        };
        let mut builder = self.client.request(method, &request.url);
        for (name, value) in &request.headers {
            builder = builder.header(name, value);
        }
        if let Some(body) = request.body {
            builder = builder.body(body);
        }
        let mut response = builder.send().await.map_err(describe)?;
        let status = response.status().as_u16();
        let content_type = response
            .headers()
            .get(reqwest::header::CONTENT_TYPE)
            .and_then(|v| v.to_str().ok())
            .map(str::to_owned);
        let mut body = Vec::new();
        while let Some(chunk) = response.chunk().await.map_err(describe)? {
            if body.len() + chunk.len() > request.max_body {
                return Err(HttpError::TooLarge(request.max_body));
            }
            body.extend_from_slice(&chunk);
        }
        Ok(HttpResponse {
            status,
            content_type,
            body,
        })
    }
}

/// A transport error, without the URL (which may carry a key in its query).
fn describe(error: reqwest::Error) -> HttpError {
    HttpError::Transport(error.without_url().to_string())
}

impl HttpClient for ReqwestClient {
    fn send(&self, request: HttpRequest) -> BoxFuture<'_, Result<HttpResponse, HttpError>> {
        Box::pin(async move {
            let timeout = request.timeout;
            tokio::time::timeout(timeout, self.execute(request))
                .await
                .unwrap_or(Err(HttpError::Timeout(timeout)))
        })
    }
}
