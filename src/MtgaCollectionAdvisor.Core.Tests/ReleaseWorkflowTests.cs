using System.Text.RegularExpressions;
using MtgaCollectionAdvisor.Core.Storage;
using Xunit;

namespace MtgaCollectionAdvisor.Core.Tests;

/// <summary>
/// Guards on the release workflow that no build would catch: a mistake in any of them only
/// shows once a release is in players' hands.
/// </summary>
public sealed class ReleaseWorkflowTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string Workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));

    /// <summary>The workflow without its comment lines.</summary>
    private static readonly string WorkflowCode = Regex.Replace(Workflow, @"(?m)^\s*#.*$", "");

    // Velopack installs to %LOCALAPPDATA%\<packId> and deletes that folder on uninstall; with
    // the data folder's name, uninstalling would take the player's collection and decks with it.
    [Fact]
    public void ReleaseWorkflow_PackId_Should_DifferFromDataFolder()
    {
        var packs = Regex.Matches(WorkflowCode, @"vpk pack\b[^\n]*(?:\\\r?\n[^\n]*)*");

        Assert.True(packs.Count >= 2, "Both release jobs must run vpk pack.");
        Assert.All(packs, pack => Assert.Matches(@"--packId\s+MtgaDeckAdvisor\b", pack.Value));
        Assert.NotEqual(Database.DataFolderName, "MtgaDeckAdvisor");
    }

    [Fact]
    public void ReleaseWorkflow_VpkVersion_Should_MatchVelopackPackage()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src", "MtgaCollectionAdvisor.Web", "MtgaCollectionAdvisor.Web.csproj"));
        var package = Regex.Match(project, @"<PackageReference\s+Include=""Velopack""\s+Version=""([^""]+)""");
        var vpk = Regex.Match(Workflow, @"VPK_VERSION:\s*(\S+)");

        Assert.True(package.Success, "The Web project has no Velopack package reference.");
        Assert.True(vpk.Success, "The workflow does not set VPK_VERSION.");
        Assert.Equal(package.Groups[1].Value, vpk.Groups[1].Value);
    }

    // A pull request runs this workflow as a dry run; nothing may leave it but an artifact.
    [Fact]
    public void ReleaseWorkflow_Should_OnlyUploadFromTags()
    {
        var steps = Regex.Split(WorkflowCode, @"\r?\n\s*- (?=name:|uses:|run:)");
        var publishing = steps.Where(s => Regex.IsMatch(s, @"vpk (upload|download) github|gh release")).ToList();

        Assert.NotEmpty(publishing);
        Assert.All(publishing, step => Assert.Matches(@"if:.*startsWith\(github\.ref, 'refs/tags/v'\)", step));
    }

    // Unsigned, so an artifact only; the change that signs it rewrites this guard.
    [Fact]
    public void ReleaseWorkflow_MacosJob_Should_PublishNothing()
    {
        var start = WorkflowCode.IndexOf("\n  release-macos:", StringComparison.Ordinal);
        Assert.True(start >= 0, "release.yml has no release-macos job.");
        var next = Regex.Match(WorkflowCode[(start + 1)..], @"\n  [\w-]+:\s*\n");
        var job = next.Success ? WorkflowCode.Substring(start + 1, next.Index) : WorkflowCode[(start + 1)..];

        Assert.Contains("--runtime osx-arm64", job);
        Assert.Contains("actions/upload-artifact", job);
        Assert.DoesNotMatch(@"vpk (upload|download)\b", job);
        Assert.DoesNotMatch(@"\bgh release\b", job);
        Assert.DoesNotContain("GITHUB_TOKEN", job);
    }

    // The console is hidden by a flag the release passes, never by WinExe: a WinExe build
    // leaves out blazor.web.js and ships with no button working (#54). The smoke test is what
    // catches that before anything is packed.
    [Fact]
    public void ReleaseWorkflow_Should_HideConsoleAndSmokeTestBeforePacking()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src", "MtgaCollectionAdvisor.Web", "MtgaCollectionAdvisor.Web.csproj"));

        Assert.DoesNotContain("<OutputType>WinExe", project);
        Assert.Contains("-p:WindowsAppNoConsole=true", Workflow);
        Assert.Contains("/_framework/blazor.web.js", Workflow);
        Assert.True(Workflow.IndexOf("name: Smoke test", StringComparison.Ordinal) < Workflow.IndexOf("name: Pack", StringComparison.Ordinal),
            "The smoke test must run before the release is packed.");
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MtgaCollectionAdvisor.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}
