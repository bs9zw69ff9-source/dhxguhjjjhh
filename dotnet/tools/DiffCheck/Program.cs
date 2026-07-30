// Differential harness: replay the JS reference cases through the C# scorer and compare.
// Passing my own xUnit tests only proves the port is self-consistent; this proves it
// agrees with the implementation that is actually running in production.
using System.Text.Json;
using PavlovBot.Core.Evasion;

var casesPath = args[0];
var jsPath = args[1];

using var casesDoc = JsonDocument.Parse(File.ReadAllText(casesPath));
using var jsDoc = JsonDocument.Parse(File.ReadAllText(jsPath));
var jsResults = jsDoc.RootElement.EnumerateArray().ToArray();

static string? Str(JsonElement e, string k) =>
    e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
static bool? Tri(JsonElement e, string k) =>
    e.TryGetProperty(k, out var v)
        ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
        : null;
static string[] Arr(JsonElement e, string k) =>
    e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];

int failures = 0, i = 0;
foreach (var c in casesDoc.RootElement.EnumerateArray())
{
    var j = c.GetProperty("join");
    var join = new JoinContext
    {
        Name = Str(j, "name"), EosId = Str(j, "eosId"), Ip = Str(j, "ip"),
        Asn = Str(j, "asn"), Provider = Str(j, "provider"),
        Vpn = Tri(j, "vpn"), Hosting = Tri(j, "hosting"), AltNames = Arr(j, "altNames"),
    };
    var bans = c.GetProperty("bans").EnumerateArray().Select(b =>
    {
        BanNetwork? net = null;
        if (b.TryGetProperty("network", out var n) && n.ValueKind == JsonValueKind.Object)
            net = new BanNetwork
            {
                EosIds = Arr(n, "eosIds"), Ips = Arr(n, "ips"), Names = Arr(n, "names"),
                Asn = Str(n, "asn"), Organization = Str(n, "organization"), Provider = Str(n, "provider"),
                Vpn = Tri(n, "vpn"), Hosting = Tri(n, "hosting"),
            };
        return new BanRecord { PlayerId = b.GetProperty("playerId").GetString()!, Network = net };
    }).ToList();

    var cs = EvasionScorer.Find(join, bans);
    var js = jsResults[i++];
    var name = js.GetProperty("n").GetString();

    var (jsEv, jsCert, jsScore, jsMatches) = (
        js.GetProperty("evasion").GetBoolean(), js.GetProperty("certain").GetBoolean(),
        js.GetProperty("score").GetInt32(), js.GetProperty("matches").GetInt32());

    var ok = cs.IsEvasion == jsEv && cs.IsCertain == jsCert && cs.Score == jsScore && cs.Matches.Count == jsMatches;
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "match  " : "DIFFER ")} {name,-24} " +
        $"js(ev={jsEv},cert={jsCert},score={jsScore},m={jsMatches})  " +
        $"cs(ev={cs.IsEvasion},cert={cs.IsCertain},score={cs.Score},m={cs.Matches.Count})");
}
Console.WriteLine(failures == 0
    ? $"\nALL {i} CASES AGREE - the port is behaviourally identical on this set"
    : $"\n{failures}/{i} DIVERGED");
return failures == 0 ? 0 : 1;
