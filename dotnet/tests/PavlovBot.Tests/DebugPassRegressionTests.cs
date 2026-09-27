using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>Regressions for the bugs found in the full debug pass.</summary>
public class DebugPassRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-debugpass-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    // ---- RCON: a login cut short is not a wrong password ----

    [Fact]
    public async Task AServerThatHangsUpDuringLoginIsRetriedNotReportedAsABadPassword()
    {
        // A restarting server accepts the connection and closes it before the verdict.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connections = 0;

        using var stop = new CancellationTokenSource();
        var serve = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) { return; }

                Interlocked.Increment(ref connections);
                using (client)
                {
                    var stream = client.GetStream();
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("Password: "));
                    _ = await stream.ReadAtLeastAsync(new byte[32], 32, throwOnEndOfStream: false);   // the MD5 hex
                }
            }
        });

        await using var rcon = new RconClient(new RconOptions
        {
            Host = "127.0.0.1", Port = port, Password = "x", Name = "test",
            CommandTimeout = TimeSpan.FromSeconds(2), MaxAttempts = 2, ReadCacheDuration = TimeSpan.Zero,
        });

        var failure = await Assert.ThrowsAsync<RconException>(() => rcon.SendAsync("ServerInfo"));

        Assert.IsNotType<RconAuthException>(failure);
        Assert.True(Volatile.Read(ref connections) >= 2, "a transport failure is retried; a bad password is not");

        await stop.CancelAsync();
        listener.Stop();
        await serve;
    }

    // ---- provision/delete: one at a time, never on a stale layout ----

    private static ProvisionOutcome Outcome(params (string Name, ProvisionStatus Status)[] steps) =>
        new([.. steps.Select(s => new ProvisionStep(s.Name, s.Status, ""))], RestartQueued: false);

    [Fact]
    public void ASecondLayoutChangeWaitsForTheFirst()
    {
        var gate = new ServerLayoutGate();

        Assert.Null(gate.TryEnter("provisioning a server"));
        Assert.Contains("provisioning a server", gate.TryEnter("deleting server 3"), StringComparison.Ordinal);
    }

    [Fact]
    public void AfterEnvChangesNothingElseRunsUntilARestart()
    {
        // The stale-layout case: a second provision before the restart rebuilt over the first.
        var gate = new ServerLayoutGate();
        gate.TryEnter("provisioning a server");
        gate.Exit(Outcome(("Pre-flight checks", ProvisionStatus.Ok), ("Wire into the bot (.env)", ProvisionStatus.Ok)));

        Assert.Contains("restart", gate.TryEnter("provisioning a server"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeletingTheLastServerDoesNotLockOutTheNextProvision()
    {
        // No restart follows (the bot cannot start with no servers), so latching would be a dead end.
        var gate = new ServerLayoutGate();
        gate.TryEnter("deleting server 1");
        gate.Exit(Outcome(("Unwire from the bot (.env)", ProvisionStatus.Ok)), restartExpected: false);

        Assert.Null(gate.TryEnter("provisioning a server"));
    }

    [Fact]
    public void ARunThatFailedBeforeEnvLeavesTheGateOpen()
    {
        var gate = new ServerLayoutGate();
        gate.TryEnter("provisioning a server");
        gate.Exit(Outcome(("SteamCMD install", ProvisionStatus.Failed), ("Wire into the bot (.env)", ProvisionStatus.Skipped)));

        Assert.Null(gate.TryEnter("provisioning a server"));
    }

    [Fact]
    public void ARunThatThrewIsTreatedAsAChange()
    {
        var gate = new ServerLayoutGate();
        gate.TryEnter("deleting server 2");
        gate.Exit(null);

        Assert.NotNull(gate.TryEnter("deleting server 1"));
    }

    [Fact]
    public void ARefusedRunReleasesTheGate()
    {
        var gate = new ServerLayoutGate();
        gate.TryEnter("deleting server 2");
        gate.Abandon();

        Assert.Null(gate.TryEnter("deleting server 2"));
    }

    // ---- the JSON export keeps the last good copy ----

    [Fact]
    public void ADamagedDatasetDoesNotOverwriteItsLastGoodExport()
    {
        using var backend = new SqliteKeyValueBackend(Path.Combine(_directory, "bot.db"), NullLogger.Instance);
        var output = Path.Combine(_directory, "export");

        backend.Write("tempbans", """[{"playerId":"alice"}]""");
        backend.ExportToJson(output);

        backend.Write("tempbans", """[{"playerId":"al""");   // truncated
        backend.ExportToJson(output);

        Assert.Contains("alice", File.ReadAllText(Path.Combine(output, "tempbans.json")), StringComparison.Ordinal);
        Assert.Equal("""[{"playerId":"al""", File.ReadAllText(Path.Combine(output, "tempbans.json.corrupt")));
    }

    [Fact]
    public void ADatasetTheStoreCannotReadIsExportedAsideEvenWhenItIsValidJson()
    {
        using var backend = new SqliteKeyValueBackend(Path.Combine(_directory, "bot.db"), NullLogger.Instance);
        var output = Path.Combine(_directory, "export");

        backend.Write("roles", """{"adminRole":1}""");
        backend.ExportToJson(output);
        backend.Write("roles", "\"the wrong shape\"");
        backend.ExportToJson(output, ["roles"]);

        Assert.Contains("adminRole", File.ReadAllText(Path.Combine(output, "roles.json")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "roles.json.corrupt")));
    }
}
