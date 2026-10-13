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
    private static readonly string DesktopProject = File.ReadAllText(Path.Combine(Root, "src", "MtgaCollectionAdvisor.Desktop", "MtgaCollectionAdvisor.Desktop.csproj"));

    /// <summary>The workflow without its comment lines.</summary>
    private static readonly string WorkflowCode = Regex.Replace(Workflow, @"(?m)^\s*#.*$", "");

    /// <summary>The workflow's code with each command on one line: a trailing backslash continues it.</summary>
    private static readonly string WorkflowCommands = Regex.Replace(WorkflowCode, @"\\\r?\n\s*", " ");

    // Velopack installs to %LOCALAPPDATA%\<packId> and deletes that folder on uninstall; with
    // the data folder's name, uninstalling would take the player's collection and decks with it.
    [Fact]
    public void ReleaseWorkflow_PackId_Should_DifferFromDataFolder()
    {
        var packs = Regex.Matches(WorkflowCommands, @"vpk pack\b[^\n]*");

        Assert.True(packs.Count >= 2, "Both release jobs must run vpk pack.");
        Assert.All(packs, pack => Assert.Matches(@"--packId\s+MtgaDeckAdvisor\b", pack.Value));
        Assert.NotEqual(Database.DataFolderName, "MtgaDeckAdvisor");
    }

    // The installer (vpk) and the in-app updater (the Velopack package) move together, in every
    // project that references the package.
    [Fact]
    public void ReleaseWorkflow_VpkVersion_Should_MatchVelopackPackage()
    {
        var vpk = Regex.Match(Workflow, @"VPK_VERSION:\s*(\S+)");
        var packages = Directory.GetFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => Regex.Match(File.ReadAllText(path), @"<PackageReference\s+Include=""Velopack""\s+Version=""([^""]+)"""))
            .Where(match => match.Success)
            .ToList();

        Assert.True(vpk.Success, "The workflow does not set VPK_VERSION.");
        Assert.NotEmpty(packages);
        Assert.All(packages, package => Assert.Equal(vpk.Groups[1].Value, package.Groups[1].Value));
    }

    // Velopack starts the installed app by this name, and an update from the browser-hosted app
    // (MtgaCollectionAdvisor.Web.exe) rewrites the shortcuts to it (#119).
    [Fact]
    public void ReleaseWorkflow_MainExe_Should_MatchDesktopAssembly()
    {
        var assembly = Regex.Match(DesktopProject, @"<AssemblyName>([^<]+)</AssemblyName>");

        Assert.True(assembly.Success, "The Desktop project sets no AssemblyName.");
        Assert.Matches($@"--mainExe\s+{Regex.Escape(assembly.Groups[1].Value)}\.exe\b", WindowsPack());
    }

    // Windows 11 has WebView2; the installer adds it to a Windows 10 machine without it.
    [Fact]
    public void ReleaseWorkflow_Should_BootstrapWebView2()
    {
        Assert.Matches(@"--framework\s+\S*\bwebview2\b", WindowsPack());
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

    // The Windows release is the desktop window, a GUI program with no console. The smoke test
    // runs it and waits for the window to be drawn before anything is packed: v0.1.1 shipped
    // with no button working and no error anywhere (#54).
    [Fact]
    public void ReleaseWorkflow_Should_SmokeTestTheDesktopBeforePacking()
    {
        Assert.Contains("<OutputType>WinExe</OutputType>", DesktopProject);
        Assert.Contains("dotnet publish src/MtgaCollectionAdvisor.Desktop", Workflow);
        Assert.Contains("Window rendered", Workflow);
        Assert.True(Workflow.IndexOf("name: Smoke test", StringComparison.Ordinal) < Workflow.IndexOf("name: Pack", StringComparison.Ordinal),
            "The smoke test must run before the release is packed.");
    }

    private static string WindowsPack()
    {
        var pack = Regex.Matches(WorkflowCommands, @"vpk pack\b[^\n]*").FirstOrDefault(m => m.Value.Contains("win-x64"));
        Assert.True(pack is not null, "release.yml has no Windows vpk pack.");
        return pack.Value;
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
