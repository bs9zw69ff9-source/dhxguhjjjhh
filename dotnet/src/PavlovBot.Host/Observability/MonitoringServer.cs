using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Host.Configuration;

namespace PavlovBot.Host.Observability;

/// <summary>
/// <c>/metrics</c>, <c>/health</c>, <c>/healthz</c> and <c>/ready</c> over plain HTTP.
/// </summary>
/// <remarks>
/// <see cref="HttpListener"/> rather than Kestrel, deliberately. Four endpoints that return
/// text do not justify pulling the ASP.NET Core framework reference into a process whose
/// whole reason for existing is a smaller footprint.
///
/// Security posture, conservative on purpose:
///   - LOOPBACK BY DEFAULT. These endpoints expose service names, error messages and
///     internal state.
///   - Optional bearer token for when it must be exposed.
///   - Disabled unless METRICS_PORT is set. No port, no listener, no attack surface.
///   - Unknown paths get a bare 404 with no body - nothing to enumerate.
///   - GET and HEAD only. Nothing here mutates state.
/// </remarks>
public sealed class MonitoringServer : IHostedService, IDisposable
{
    private readonly MonitoringOptions _options;
    private readonly MetricsRegistry _metrics;
    private readonly HealthRegistry _health;
    private readonly ILogger<MonitoringServer> _logger;
    private readonly Func<bool> _readiness;

    private HttpListener? _listener;
    private Task? _loop;
    private CancellationTokenSource? _stopping;

    public MonitoringServer(
        MonitoringOptions options,
        MetricsRegistry metrics,
        HealthRegistry health,
        ILogger<MonitoringServer> logger,
        Func<bool>? readiness = null)
    {
        _options = options;
        _metrics = metrics;
        _health = health;
        _logger = logger;
        _readiness = readiness ?? (static () => true);
    }

    public bool Listening => _listener?.IsListening ?? false;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("METRICS_PORT not set - monitoring endpoint disabled");
            return Task.CompletedTask;
        }

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{_options.Host}:{_options.Port}/");
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            /* A metrics port that will not bind must not stop the bot. The whole point of
               the endpoint is to observe a running bot; refusing to run without it inverts
               that. Log loudly, continue. */
            _logger.LogWarning(ex, "Monitoring endpoint failed to bind {Host}:{Port} - continuing without it",
                _options.Host, _options.Port);
            _listener = null;
            return Task.CompletedTask;
        }

        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token), CancellationToken.None);

        _logger.LogInformation("Metrics on http://{Host}:{Port}/metrics, health on /health{Auth}",
            _options.Host, _options.Port, _options.Token is null ? "" : " (token required)");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || _listener is not { IsListening: true })
            {
                return;   // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monitoring accept failed");
                continue;
            }

            // One slow /health must not queue behind it every other scrape.
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(context, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Monitoring request failed"); }
                finally { try { context.Response.Close(); } catch (Exception) { } }
            }, CancellationToken.None);
        }
    }

    /// <summary>Routing and authorisation, split out so tests exercise it without a socket.</summary>
    internal async Task<MonitoringResponse> RouteAsync(string method, string rawUrl, string? authorization, CancellationToken ct)
    {
        if (method is not ("GET" or "HEAD"))
            return new MonitoringResponse(405, "method not allowed", "text/plain; charset=utf-8");

        var path = rawUrl.Split('?')[0];

        if (_options.Token is not null)
        {
            // Liveness stays OPEN. An orchestrator has to be able to restart a bot whose
            // configuration is broken, and that includes a wrong metrics token.
            if (path != "/healthz" && !string.Equals(authorization, $"Bearer {_options.Token}", StringComparison.Ordinal))
                return new MonitoringResponse(401, "unauthorized", "text/plain; charset=utf-8");
        }

        switch (path)
        {
            case "/metrics":
                return new MonitoringResponse(200, _metrics.Render(), "text/plain; version=0.0.4; charset=utf-8");

            case "/healthz":
                return new MonitoringResponse(200, "ok", "text/plain; charset=utf-8");

            case "/ready":
            {
                var ready = _readiness();
                return new MonitoringResponse(ready ? 200 : 503,
                    $"{{\"ready\":{(ready ? "true" : "false")}}}", "application/json");
            }

            case "/health":
            {
                var report = await _health.ReportAsync(ct).ConfigureAwait(false);
                /* 503 for unhealthy ONLY. "Degraded" must not take a bot out of a load
                   balancer or trigger a restart loop because one optional detector is down. */
                return new MonitoringResponse(
                    report.Status == HealthStatus.Unhealthy ? 503 : 200,
                    Serialize(report), "application/json");
            }

            default:
                return new MonitoringResponse(404, "", "text/plain; charset=utf-8");
        }
    }

    private static string Serialize(HealthReport report)
    {
        // Hand-built rather than reflection-serialised: it keeps the wire shape identical
        // to the Node bot's /health, which existing dashboards already parse, and it keeps
        // the serialiser out of a path that has to work when the process is unhealthy.
        var json = new StringBuilder();
        json.Append("{\n  \"status\": \"").Append(Lower(report.Status)).Append("\",\n");
        json.Append("  \"checkedAt\": \"").Append(report.CheckedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append("\",\n");
        json.Append("  \"components\": {\n");

        var first = true;
        foreach (var (name, component) in report.Components.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (!first) json.Append(",\n");
            first = false;
            json.Append("    ").Append(JsonEncode(name)).Append(": {")
                .Append("\"status\": \"").Append(Lower(component.Status)).Append("\", ")
                .Append("\"detail\": ").Append(component.Detail is null ? "null" : JsonEncode(component.Detail)).Append(", ")
                .Append("\"critical\": ").Append(component.Critical ? "true" : "false").Append(", ")
                .Append("\"durationMs\": ").Append(component.DurationMs.ToString("0.###", CultureInfo.InvariantCulture))
                .Append('}');
        }
        json.Append("\n  }\n}");
        return json.ToString();
    }

    private static string Lower(HealthStatus status) => status.ToString().ToLowerInvariant();

    private static string JsonEncode(string value) => JsonSerializer.Serialize(value);

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = await RouteAsync(
            context.Request.HttpMethod,
            context.Request.RawUrl ?? "/",
            context.Request.Headers["Authorization"],
            ct).ConfigureAwait(false);

        var body = Encoding.UTF8.GetBytes(response.Body);
        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = body.Length;

        if (context.Request.HttpMethod != "HEAD")
            await context.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is not null) await _stopping.CancelAsync().ConfigureAwait(false);
        try { _listener?.Stop(); } catch (Exception) { }
        if (_loop is not null)
        {
            // Bounded: a stuck accept must not hold up shutdown of everything else.
            await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
        }
        _listener?.Close();
        _listener = null;
    }

    public void Dispose()
    {
        _stopping?.Dispose();
        _listener?.Close();
    }
}

public readonly record struct MonitoringResponse(int StatusCode, string Body, string ContentType);
