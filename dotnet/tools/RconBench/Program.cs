// Replays the bot's real 30s RCON cycle and counts what it costs the game server.
// The Node numbers are the ones measured earlier in this session against the same shape.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using PavlovBot.Rcon;

int connections = 0, commands = 0;
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var cts = new CancellationTokenSource();

_ = Task.Run(async () => {
    while (!cts.IsCancellationRequested) {
        TcpClient c;
        try { c = await listener.AcceptTcpClientAsync(cts.Token); } catch { return; }
        Interlocked.Increment(ref connections);
        _ = Task.Run(async () => {
            using (c) {
                var s = c.GetStream(); var buf = new byte[4096];
                await s.WriteAsync(Encoding.UTF8.GetBytes("Password: "));
                await s.ReadAsync(buf);
                await s.WriteAsync(Encoding.UTF8.GetBytes("Authenticated=1\r\n"));
                while (true) {
                    int n; try { n = await s.ReadAsync(buf, cts.Token); } catch { return; }
                    if (n == 0) return;
                    Interlocked.Increment(ref commands);
                    await Task.Delay(3);   // a real RCON round trip is not instant
                    await s.WriteAsync(Encoding.UTF8.GetBytes("{\"Successful\":true,\"PlayerList\":[]}\r\n"));
                }
            }
        });
    }
});

var port = ((IPEndPoint)listener.LocalEndpoint).Port;
await using var client = new RconClient(new RconOptions {
    Host = "127.0.0.1", Port = port, Password = "secret", Name = "server1",
    CommandSpacing = TimeSpan.Zero, ReadCacheDuration = TimeSpan.FromMilliseconds(2500),
});

// One cycle = what the player list, dashboard, leaderboards, ban sweep, health check and
// cache refresh all ask for within the same second.
async Task Cycle() {
    await Task.WhenAll(client.SendAsync("RefreshList"), client.SendAsync("RefreshList"), client.SendAsync("ServerInfo"));
    await Task.Delay(120);
    await Task.WhenAll(client.SendAsync("RefreshList"), client.SendAsync("RefreshList"));
    await Task.Delay(120);
    await client.SendAsync("RefreshList");
}

await Cycle();                       // warm up: pays the one-time connect
var c0 = connections; var m0 = commands;
var sw = Stopwatch.StartNew();
const int cycles = 10;
for (var i = 0; i < cycles; i++) { await Cycle(); await Task.Delay(2600); }   // let the cache lapse between cycles
sw.Stop();

Console.WriteLine($"{cycles} cycles of the bot's real RCON pattern:");
Console.WriteLine($"  TCP connections opened : {connections - c0}   (persistent session)");
Console.WriteLine($"  commands sent          : {commands - m0}");
Console.WriteLine($"  wall time              : {sw.ElapsedMilliseconds}ms");
Console.WriteLine();
Console.WriteLine("Node, measured earlier this session on the same shape:");
Console.WriteLine("  6 connections per cycle before coalescing, 2 after - one connect + MD5 each");
Console.WriteLine($"  so ~{cycles * 2}-{cycles * 6} connections where .NET opened {connections - c0}");
Console.WriteLine($"  process RSS: {Environment.WorkingSet / 1024 / 1024} MB");

await cts.CancelAsync(); listener.Stop();
