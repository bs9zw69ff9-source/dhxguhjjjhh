using System.Text.Json;
using System.Text.Json.Serialization;
using PavlovBot.Core.Factions;

namespace PavlovBot.Host.Factions;

/// <param name="Set">Null when the file could not be read or parsed at all.</param>
/// <param name="Problems">Everything wrong with it, in terms an operator can act on.</param>
public sealed record FactionsFileResult(FactionSet? Set, IReadOnlyList<string> Problems);

/// <summary>
/// The faction roster as a JSON file, so one binary can run more than one RP.
/// </summary>
/// <remarks>
/// WHY A FILE RATHER THAN ENV VARS. A faction is a name, an ordered ladder, a file per rank,
/// per-rank caps and a set of sub-classes. That is a tree, and flattening a tree into
/// FACTION_1_RANK_3_CAP style keys produces something nobody can read and everybody
/// mistypes. The file sits next to .env and is pointed at by one setting.
///
/// UNSET MEANS THE BUILT-IN SET. An existing deployment configures nothing and gets exactly
/// what it has today, which is the only acceptable default when the point of the change is a
/// SECOND bot rather than a change to the first.
///
/// COMMENTS AND TRAILING COMMAS ARE ALLOWED. This is hand-edited by whoever runs the server,
/// at the point they are adding a rank at 2am, and strict JSON's refusal to explain a
/// trailing comma is a genuinely bad experience for a config file.
/// </remarks>
public static class FactionsFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record RankDto(string? Name, string? File, int? Cap);

    private sealed record FactionDto(
        string? Name,
        string? SpawnFile,
        [property: JsonPropertyName("default")] string? Default,
        IReadOnlyList<RankDto>? Ranks,
        IReadOnlyList<RankDto>? Subclasses);

    private sealed record FileDto(IReadOnlyList<FactionDto>? Factions);

    /// <summary>
    /// Read and validate a faction file. Never throws - a bad file is reported, not raised.
    /// </summary>
    /// <remarks>
    /// The caller is startup, which already reports every configuration problem at once and
    /// exits. An exception here would turn a typo in a roster file into a stack trace, which
    /// is a worse answer to the same question.
    /// </remarks>
    public static FactionsFileResult Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new FactionsFileResult(null, ["No faction file path was given."]);

        if (!File.Exists(path))
        {
            /* NOT SILENTLY THE DEFAULT. A path that was set and does not resolve is a typo,
               and falling back to the built-in factions would start a Fallout bot running the
               police roster - which looks like the file being ignored, because it is. */
            return new FactionsFileResult(null, [$"{path} does not exist."]);
        }

        FileDto? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            // The message carries the line and position, which is the whole value of it.
            return new FactionsFileResult(null, [$"{path} is not valid JSON: {ex.Message}"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FactionsFileResult(null, [$"{path} could not be read: {ex.Message}"]);
        }

        if (parsed?.Factions is not { Count: > 0 } factions)
            return new FactionsFileResult(null, [$"{path} defines no factions."]);

        var problems = new List<string>();
        var definitions = new List<FactionDefinition>();

        foreach (var faction in factions)
        {
            if (string.IsNullOrWhiteSpace(faction.Name))
            {
                problems.Add("A faction has no name.");
                continue;
            }

            var ranks = (faction.Ranks ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.File))
                .ToList();

            if (ranks.Count == 0)
            {
                problems.Add($"{faction.Name} has no usable ranks - each needs a name and a file.");
                continue;
            }

            var order = ranks.Select(r => r.Name!).ToList();

            definitions.Add(new FactionDefinition
            {
                Name = faction.Name.Trim(),
                Order = order,

                // The lowest rank, because that is where a new member starts and it is the
                // answer that needs no configuration to be right.
                Default = string.IsNullOrWhiteSpace(faction.Default) ? order[0] : faction.Default.Trim(),

                /* A LADDERLESS FACTION'S ONE FILE IS ITS SPAWN FILE. Requiring it to be
                   written twice would be a rule to remember for no benefit, and forgetting it
                   produces a faction nobody can spawn as. */
                SpawnFile = string.IsNullOrWhiteSpace(faction.SpawnFile)
                    ? ranks[0].File!.Trim()
                    : faction.SpawnFile.Trim(),

                RankFiles = ranks.ToDictionary(r => r.Name!.Trim(), r => r.File!.Trim(), StringComparer.OrdinalIgnoreCase),

                // Absent or non-positive means uncapped, which is the same thing the built-in
                // set expresses by leaving the rank out of the map entirely.
                RankCaps = ranks.Where(r => r.Cap is > 0)
                    .ToDictionary(r => r.Name!.Trim(), r => r.Cap!.Value, StringComparer.OrdinalIgnoreCase),

                Subclasses = (faction.Subclasses ?? [])
                    .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.File))
                    .ToDictionary(s => s.Name!.Trim(), s => s.File!.Trim(), StringComparer.OrdinalIgnoreCase),
            });
        }

        if (definitions.Count == 0) return new FactionsFileResult(null, problems);

        var set = FactionSet.Of(definitions);
        problems.AddRange(set.Problems());

        return new FactionsFileResult(problems.Count == 0 ? set : null, problems);
    }
}
