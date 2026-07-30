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

// ---- roster write guard ----
if (args.Length >= 4 && args[2] == "--roster")
{
    using var rc = JsonDocument.Parse(File.ReadAllText(args[3]));
    using var rj = JsonDocument.Parse(File.ReadAllText(args[4]));
    var jsRoster = rj.RootElement.EnumerateArray().ToArray();
    int rf = 0, ri = 0;
    Console.WriteLine();
    foreach (var c in rc.RootElement.EnumerateArray())
    {
        var current = c.GetProperty("current").EnumerateArray().Select(x => x.GetString()!).ToList();
        var proposed = c.GetProperty("proposed").EnumerateArray().Select(x => x.GetString()!).ToList();
        var verdict = PavlovBot.Core.Data.RosterWriteGuard.Evaluate(current, proposed);
        var js = jsRoster[ri++];
        var jsAllowed = js.GetProperty("allowed").GetBoolean();
        var ok = verdict.IsAllowed == jsAllowed;
        if (!ok) rf++;
        Console.WriteLine($"{(ok ? "match  " : "DIFFER ")} {js.GetProperty("n").GetString(),-28} js(allowed={jsAllowed})  cs(allowed={verdict.IsAllowed}, dropped={verdict.Dropped})");
    }
    Console.WriteLine(rf == 0
        ? $"\nALL {ri} ROSTER CASES AGREE"
        : $"\n{rf}/{ri} ROSTER CASES DIVERGED");
    if (rf > 0) return 1;
}


// ---- factions: registry data + rank-change arithmetic ----
if (args.Length >= 7 && args[5] == "--factions")
{
    using var fj = JsonDocument.Parse(File.ReadAllText(args[6]));
    var registry = fj.RootElement.GetProperty("registry");
    int ff = 0, fi = 0;
    Console.WriteLine();

    foreach (var jsFaction in registry.EnumerateObject())
    {
        var def = PavlovBot.Core.Factions.FactionRegistry.Get(jsFaction.Name);
        fi++;
        if (def is null) { Console.WriteLine($"DIFFER {jsFaction.Name,-10} missing from the C# registry"); ff++; continue; }

        var jsOrder = jsFaction.Value.GetProperty("order").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var jsDefault = jsFaction.Value.GetProperty("default").GetString();
        var orderOk = jsOrder.SequenceEqual(def.Order);
        var defaultOk = jsDefault == def.Default;

        var capsOk = true;
        foreach (var cap in jsFaction.Value.GetProperty("rankCaps").EnumerateObject())
            if (def.CapFor(cap.Name) != cap.Value.GetInt32()) capsOk = false;
        foreach (var rank in def.Order)
        {
            var jsHasCap = jsFaction.Value.GetProperty("rankCaps").TryGetProperty(rank, out _);
            var csCapped = def.CapFor(rank) != int.MaxValue;
            if (jsHasCap != csCapped) capsOk = false;
        }

        var filesOk = true;
        foreach (var f in jsFaction.Value.GetProperty("rankFiles").EnumerateObject())
            if (!def.RankFiles.TryGetValue(f.Name, out var v) || v != f.Value.GetString()) filesOk = false;

        var subsOk = jsFaction.Value.GetProperty("subclasses").EnumerateObject().Count() == def.Subclasses.Count;
        foreach (var sc in jsFaction.Value.GetProperty("subclasses").EnumerateObject())
            if (!def.Subclasses.TryGetValue(sc.Name, out var v) || v != sc.Value.GetString()) subsOk = false;

        var ok = orderOk && defaultOk && capsOk && filesOk && subsOk;
        if (!ok) ff++;
        Console.WriteLine($"{(ok ? "match  " : "DIFFER ")} {jsFaction.Name,-10} order={orderOk} default={defaultOk} caps={capsOk} files={filesOk} subclasses={subsOk}");
    }

    int sf = 0, si = 0;
    foreach (var sc in fj.RootElement.GetProperty("scenarios").EnumerateArray())
    {
        si++;
        var def = PavlovBot.Core.Factions.FactionRegistry.Get(sc.GetProperty("faction").GetString());
        var cur = sc.GetProperty("current").ValueKind == JsonValueKind.Null ? null : sc.GetProperty("current").GetString();
        var d = PavlovBot.Core.Factions.MembershipRules.ChangeRank(def, cur, sc.GetProperty("dir").GetInt32(), _ => 0);
        var jsOutcome = sc.GetProperty("outcome").GetString();
        var jsRank = sc.TryGetProperty("rank", out var r) ? r.GetString() : null;
        if (d.Outcome.ToString() != jsOutcome || d.Rank != (jsRank ?? d.Rank)) sf++;
    }
    Console.WriteLine($"\nrank-change arithmetic: {si - sf}/{si} scenarios agree");
    Console.WriteLine(ff == 0 && sf == 0
        ? $"ALL {fi} FACTIONS AND {si} SCENARIOS AGREE"
        : $"{ff} faction(s) and {sf} scenario(s) DIVERGED");
    if (ff > 0 || sf > 0) return 1;
}


// ---- penal code: booking maths and labels ----
if (args.Length >= 9 && args[7] == "--penal")
{
    using var pj = JsonDocument.Parse(File.ReadAllText(args[8]));
    int pf = 0, pi = 0;
    var firstFailures = new List<string>();
    foreach (var sc in pj.RootElement.EnumerateArray())
    {
        pi++;
        var codes = sc.GetProperty("codes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var rate = sc.GetProperty("rate").GetDouble();
        var b = PavlovBot.Core.Penal.PenalCode.Book(codes, rate);

        var ok = b.JailMinutes == sc.GetProperty("minutes").GetInt32()
              && b.Bail == sc.GetProperty("bail").GetInt32()
              && b.Execution == sc.GetProperty("execution").GetBoolean()
              && b.Variable == sc.GetProperty("variable").GetBoolean()
              && b.SentenceLabel() == sc.GetProperty("sentence").GetString()
              && b.BailLabel() == sc.GetProperty("bailLabel").GetString();
        if (!ok)
        {
            pf++;
            if (firstFailures.Count < 5)
                firstFailures.Add($"  [{string.Join(",", codes)}] rate={rate}  " +
                    $"js(min={sc.GetProperty("minutes").GetInt32()},bail={sc.GetProperty("bail").GetInt32()},\"{sc.GetProperty("bailLabel").GetString()}\")  " +
                    $"cs(min={b.JailMinutes},bail={b.Bail},\"{b.BailLabel()}\")");
        }
    }
    Console.WriteLine();
    foreach (var f in firstFailures) Console.WriteLine(f);
    Console.WriteLine(pf == 0
        ? $"ALL {pi} PENAL SCENARIOS AGREE (every charge at 5 rates, plus 12 combinations)"
        : $"{pf}/{pi} PENAL SCENARIOS DIVERGED");
    if (pf > 0) return 1;
}

return failures == 0 ? 0 : 1;
