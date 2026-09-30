using MtgaCollectionAdvisor.Core.Notices;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>#96: notices.json, read leniently by the app and strictly by CI.</summary>
public sealed class NoticeFileTests
{
    private const string Example = """
        {
          "notices": [
            {
              "id": "reality-fracture-cards",
              "title": "Reality Fracture is here",
              "text": "The new set's cards are in your card database.",
              "showFrom": "2026-10-02T00:00:00Z",
              "showUntil": "2026-10-20T00:00:00Z",
              "requiresSet": "fra"
            }
          ]
        }
        """;

    [Fact]
    public void Parse_reads_every_field()
    {
        var notice = Assert.Single(NoticeFile.Parse(Example)!);

        Assert.Equal(new Notice(
            "reality-fracture-cards", "Reality Fracture is here", "The new set's cards are in your card database.",
            new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
            "fra"), notice);
    }

    [Fact]
    public void Parse_skips_invalid_entries_and_keeps_the_rest()
    {
        var json = $$"""
            { "notices": [
              { "id": "Bad Id", "title": "t", "text": "x", "showFrom": "2026-10-02T00:00:00Z" },
              { "id": "no-date", "title": "t", "text": "x", "showFrom": "next week" },
              { "id": "too-long", "title": "{{new string('a', 81)}}", "text": "x", "showFrom": "2026-10-02T00:00:00Z" },
              { "id": "no-from", "title": "t", "text": "x" },
              { "id": "good", "title": "t", "text": "x", "showFrom": "2026-10-02T00:00:00Z", "link": "ignored at runtime" },
              { "id": "good", "title": "twice", "text": "x", "showFrom": "2026-10-02T00:00:00Z" }
            ] }
            """;

        var notices = NoticeFile.Parse(json)!;

        Assert.Equal(["good"], notices.Select(n => n.Id));
        Assert.Equal("t", notices[0].Title);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "notices": {} }""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_is_null_for_a_file_that_is_not_a_notice_list(string? json)
    {
        Assert.Null(NoticeFile.Parse(json));
    }

    [Fact]
    public void Parse_of_an_empty_list_is_empty()
    {
        Assert.Empty(NoticeFile.Parse("""{ "notices": [] }""")!);
        Assert.Empty(NoticeFile.Validate("""{ "notices": [] }"""));
    }

    [Fact]
    public void Validate_reports_each_problem()
    {
        var json = """
            { "notices": [
              { "title": "t", "text": "x", "showFrom": "2026-10-02T00:00:00Z" },
              { "id": "dup", "title": "t", "text": "x", "showFrom": "2026-10-02T00:00:00Z" },
              { "id": "dup", "title": "t", "text": "x", "showFrom": "2026-10-02T00:00:00Z" },
              { "id": "no-from", "title": "t", "text": "x" },
              { "id": "bad-date", "title": "t", "text": "x", "showFrom": "soon" },
              { "id": "backwards", "title": "t", "text": "x", "showFrom": "2026-10-02T00:00:00Z", "showUntil": "2026-10-01T00:00:00Z" },
              { "id": "bad-set", "title": "t", "text": "x", "showFrom": "2026-10-02T00:00:00Z", "requiresSet": "FRA!" },
              { "id": "typo", "title": "t", "text": "x", "showFrom": "2026-10-02T00:00:00Z", "showUtnil": "2026-10-09T00:00:00Z" },
              { "id": "old-link", "title": "t", "text": "x", "showFrom": "2026-10-02T00:00:00Z", "link": { "label": "a", "url": "https://x" } },
              { "id": "no-text", "title": "t", "showFrom": "2026-10-02T00:00:00Z" }
            ] }
            """;

        var problems = NoticeFile.Validate(json);

        Assert.Contains("Notice 1: no id.", problems);
        Assert.Contains("Notice 3: duplicate id \"dup\".", problems);
        Assert.Contains("Notice 4: no showFrom.", problems);
        Assert.Contains(problems, p => p.StartsWith("Notice 5: showFrom is not an ISO 8601 date", StringComparison.Ordinal));
        Assert.Contains("Notice 6: showUntil is not after showFrom.", problems);
        Assert.Contains(problems, p => p.StartsWith("Notice 7: requiresSet", StringComparison.Ordinal));
        Assert.Contains("Notice 8: unknown property \"showUtnil\".", problems);
        Assert.Contains("Notice 9: unknown property \"link\".", problems);
        Assert.Contains("Notice 10: no text.", problems);
        Assert.Equal(9, problems.Count);
    }

    [Fact]
    public void Validate_rejects_a_file_that_is_not_a_notice_list()
    {
        Assert.Single(NoticeFile.Validate("""{ "notice": [] }"""));
    }

    [Fact]
    public void The_repository_notices_json_is_valid()
    {
        // The maintainer edits this file by hand (#96): a mistake must fail CI, not show nothing.
        var json = File.ReadAllText(Path.Combine(RepositoryRoot(), "notices.json"));

        Assert.Empty(NoticeFile.Validate(json));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MtgaCollectionAdvisor.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}
