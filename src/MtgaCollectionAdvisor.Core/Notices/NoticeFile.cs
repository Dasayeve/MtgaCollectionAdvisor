using System.Text.Json;
using System.Text.RegularExpressions;

namespace MtgaCollectionAdvisor.Core.Notices;

/// <summary>
/// notices.json at the repository root (#96), read from GitHub like creators.json and
/// card-data.json, so a notice needs a commit, not a release. The app reads it leniently (a bad
/// entry is skipped, the rest still show); CI reads it strictly with <see cref="Validate"/>.
/// </summary>
public static partial class NoticeFile
{
    public const string RemoteUrl =
        "https://raw.githubusercontent.com/Dasayeve/MtgaCollectionAdvisor/master/notices.json";

    public const int MaxEntries = 20;
    public const int MaxTitleLength = 80;
    public const int MaxTextLength = 400;

    private static readonly HashSet<string> KnownProperties =
        ["id", "title", "text", "showFrom", "showUntil", "requiresSet"];

    /// <summary>The valid entries, in file order; null when the file isn't an object with a "notices" array.</summary>
    public static IReadOnlyList<Notice>? Parse(string? json)
    {
        var entries = Entries(json);
        if (entries is null) return null;

        var notices = new List<Notice>();
        var ids = new HashSet<string>();
        foreach (var entry in entries.Take(MaxEntries))
        {
            if (Read(entry, problems: null) is { } notice && ids.Add(notice.Id)) notices.Add(notice);
        }
        return notices;
    }

    /// <summary>Every problem in the file, for the CI test; empty when it is valid.</summary>
    public static IReadOnlyList<string> Validate(string json)
    {
        var entries = Entries(json);
        if (entries is null) return ["notices.json must be a JSON object with a \"notices\" array."];

        var problems = new List<string>();
        if (entries.Count > MaxEntries) problems.Add($"At most {MaxEntries} notices are read; the file has {entries.Count}.");

        var ids = new HashSet<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entryProblems = new List<string>();
            var notice = Read(entries[i], entryProblems);
            if (notice is not null && !ids.Add(notice.Id)) entryProblems.Add($"duplicate id \"{notice.Id}\"");
            problems.AddRange(entryProblems.Select(p => $"Notice {i + 1}: {p}."));
        }
        return problems;
    }

    // The entries as JSON elements, cloned so they outlive the document.
    private static List<JsonElement>? Entries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("notices", out var notices)
                   && notices.ValueKind == JsonValueKind.Array
                ? notices.EnumerateArray().Select(e => e.Clone()).ToList()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // One entry, or null when it is unusable. With a problems list, every rule it breaks is added;
    // without one, the first problem is enough to skip it.
    private static Notice? Read(JsonElement entry, List<string>? problems)
    {
        var failed = false;
        void Problem(string text)
        {
            failed = true;
            problems?.Add(text);
        }

        if (entry.ValueKind != JsonValueKind.Object)
        {
            Problem("not a JSON object");
            return null;
        }

        foreach (var property in entry.EnumerateObject().Where(p => !KnownProperties.Contains(p.Name)))
        {
            // Only CI cares: a typo such as "showUtnil" would otherwise silently mean "the default end".
            problems?.Add($"unknown property \"{property.Name}\"");
        }

        var id = Text(entry, "id");
        if (id is null) Problem("no id");
        else if (!IdPattern().IsMatch(id)) Problem($"id \"{id}\" must be 1-60 characters of a-z, 0-9 and -");

        var title = Text(entry, "title");
        if (string.IsNullOrWhiteSpace(title)) Problem("no title");
        else if (title.Length > MaxTitleLength) Problem($"title longer than {MaxTitleLength} characters");

        var text = Text(entry, "text");
        if (string.IsNullOrWhiteSpace(text)) Problem("no text");
        else if (text.Length > MaxTextLength) Problem($"text longer than {MaxTextLength} characters");

        var showFrom = Date(entry, "showFrom", Problem);
        if (showFrom is null && !entry.TryGetProperty("showFrom", out _)) Problem("no showFrom");

        var showUntil = Date(entry, "showUntil", Problem);
        if (showFrom is { } from && showUntil is { } until && until <= from) Problem("showUntil is not after showFrom");

        var set = Text(entry, "requiresSet");
        if (entry.TryGetProperty("requiresSet", out _) && (set is null || !SetPattern().IsMatch(set)))
            Problem("requiresSet must be a lower-case set code, e.g. \"fra\"");

        if (failed) return null;

        return new Notice(id!, title!.Trim(), text!.Trim(), showFrom!.Value, showUntil, set);
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Date(JsonElement entry, string name, Action<string> problem)
    {
        if (!entry.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var at)) return at;
        problem($"{name} is not an ISO 8601 date, e.g. \"2026-10-02T00:00:00Z\"");
        return null;
    }

    [GeneratedRegex("^[a-z0-9-]{1,60}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z0-9]{2,6}$")]
    private static partial Regex SetPattern();
}
