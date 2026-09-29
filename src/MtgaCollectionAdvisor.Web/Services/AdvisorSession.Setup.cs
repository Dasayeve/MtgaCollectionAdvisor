using MtgaCollectionAdvisor.Core.Memory;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Onboarding;

namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>What the setup screen shows.</summary>
public sealed record SetupProgress(
    IReadOnlyList<SetupStep> Steps,
    IReadOnlyDictionary<SetupStep, SetupStepState> States,
    SetupStep? Current,
    string Message,
    bool MtgaRunning,
    bool Capturing,
    bool CollectionNeedsCards);

/// <summary>
/// The first-run setup (#56): when the card database or the Standard decks are missing, the
/// window shows a setup screen that fetches them, then the creator videos, then asks for MTG
/// Arena and captures the collection, one step at a time. Which steps and what comes next is
/// <see cref="SetupPlan"/>; this runs them.
/// </summary>
public sealed partial class AdvisorSession
{
    // Once Arena is seen running, give the client time to reach its main menu before the
    // one automatic capture: a scan at process start finds nothing.
    private static readonly TimeSpan AutoCaptureDelay = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _setupGate = new(1, 1);
    private IReadOnlyList<SetupStep> _setupSteps = [];
    private readonly Dictionary<SetupStep, SetupStepState> _setupStates = [];
    private string _setupMessage = "";
    private System.Threading.Timer? _setupArenaPoll;
    private DateTimeOffset? _setupArenaSeenAt;
    private bool _setupAutoCaptureTried;
    private bool _setupMtgaRunning;
    private bool _setupCapturing;

    // The capture matches memory against the card database; with that step skipped there is
    // nothing to match, so the Arena step says so instead of offering a capture that fails.
    private bool _setupNeedsCards;

    /// <summary>Non-null while the setup screen replaces the page.</summary>
    public SetupProgress? Setup { get; private set; }

    public static string SetupStepLabel(SetupStep step) => step switch
    {
        SetupStep.CardDatabase => "Card database",
        SetupStep.Decks => "Standard decks",
        SetupStep.CreatorVideos => "Creator videos",
        SetupStep.Collection => "Your collection",
        _ => step.ToString(),
    };

    private async Task StartSetupIfNeededAsync()
    {
        var facts = await ReadSetupFactsAsync();
        if (!SetupPlan.IsNeeded(facts)) return;

        // Marks the setup unfinished until FinishSetupAsync, so closing the window midway
        // resumes it on the next start instead of dropping the steps still to come.
        File.WriteAllText(SetupMarkerPath, DateTimeOffset.UtcNow.ToString("O"));

        _setupSteps = SetupPlan.StepsFor(facts);
        foreach (var step in _setupSteps) _setupStates[step] = SetupStepState.Pending;
        _setupMessage = "Getting started…";
        PublishSetup();

        _ = Task.Run(RunSetupAsync);
    }

    private async Task<SetupFacts> ReadSetupFactsAsync()
    {
        var standardDecks = await services.CuratedDeckStore.LoadAsync(Formats.Standard);
        var creatorVideos = CreatorVideosEnabled && (await services.CreatorVideoStore.LoadAsync()).Videos.Count > 0;
        var collection = await services.CollectionStore.LoadAsync();

        return new SetupFacts(
            InProgress: File.Exists(SetupMarkerPath),
            HasCards: await services.CardDatabaseStore.GetLastImportedAsync() is not null,
            HasStandardDecks: standardDecks.Any(d => !d.IsUserDeck),
            CreatorVideosEnabled: CreatorVideosEnabled,
            HasCreatorVideos: creatorVideos,
            HasCollection: collection.OwnedByGrpId.Count > 0);
    }

    /// <summary>Works through the steps until one fails, the collection step is reached, or all are done.</summary>
    private async Task RunSetupAsync()
    {
        if (!await _setupGate.WaitAsync(0)) return;
        try
        {
            while (SetupPlan.Next(_setupStates, _setupSteps) is { } step)
            {
                if (_setupStates[step] == SetupStepState.Failed) return;   // waits for Retry or Skip

                if (step == SetupStep.Collection)
                {
                    BeginCollectionStep();
                    return;                                               // the poll and the buttons take it from here
                }

                await RunSetupStepAsync(step);
            }

            await FinishSetupAsync();
        }
        finally
        {
            _setupGate.Release();
        }
    }

    private async Task RunSetupStepAsync(SetupStep step)
    {
        _setupStates[step] = SetupStepState.Running;
        _setupMessage = step switch
        {
            SetupStep.CardDatabase => "Downloading the card list from Scryfall…",
            SetupStep.Decks => "Searching Archidekt for recent Standard decks…",
            _ => "Loading creator videos…",
        };
        PublishSetup();

        // The setup shows a step's trouble by its state (Failed), not by the message's level.
        void Report(string message, StatusLevel level)
        {
            _setupMessage = message;
            PublishSetup();
        }

        try
        {
            switch (step)
            {
                case SetupStep.CardDatabase:
                    await ImportCardsAsync(Report);
                    break;
                case SetupStep.Decks:
                    var result = await FetchDecksAsync(Formats.Standard, Report);
                    // Nothing stored means nothing to rank: a cooldown or an unreachable Archidekt.
                    if (!(await ReadSetupFactsAsync()).HasStandardDecks)
                    {
                        Fail(step, result.DescribeEmptyPool());
                        return;
                    }
                    break;
                case SetupStep.CreatorVideos:
                    // A failed feed is "no news" and never fails the step; only an exception does.
                    await LoadCreatorsAsync(force: false, Report);
                    break;
            }

            _setupStates[step] = SetupStepState.Done;
            PublishSetup();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Setup step {Step} failed", step);
            Fail(step, $"{SetupStepLabel(step)} failed: {ex.Message}");
        }
    }

    private void Fail(SetupStep step, string reason)
    {
        _setupStates[step] = SetupStepState.Failed;
        _setupMessage = reason;
        PublishSetup();
    }

    public async Task RetrySetupStepAsync()
    {
        if (Setup?.Current is not { } step || _setupStates[step] != SetupStepState.Failed) return;

        _setupStates[step] = SetupStepState.Pending;
        await RunSetupAsync();
    }

    public async Task SkipSetupStepAsync()
    {
        if (Setup?.Current is not { } step) return;

        var skippable = _setupStates[step] == SetupStepState.Failed
                        || (step == SetupStep.Collection && !_setupCapturing);
        if (!skippable) return;

        _setupStates[step] = SetupStepState.Skipped;
        StopArenaPoll();
        await RunSetupAsync();
    }

    // ---------- The MTG Arena step ----------

    private void BeginCollectionStep()
    {
        _setupNeedsCards = CardsUpdatedAt is null;
        _setupStates[SetupStep.Collection] = SetupStepState.Running;
        _setupMessage = "";
        PublishSetup();

        _setupArenaPoll ??= new System.Threading.Timer(_ => _ = PollArenaAsync(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private async Task PollArenaAsync()
    {
        var running = MemoryCollectionSyncService.IsMtgaRunning();
        if (running != _setupMtgaRunning)
        {
            _setupMtgaRunning = running;
            _setupArenaSeenAt = running ? DateTimeOffset.UtcNow : null;
            PublishSetup();
        }

        if (running && !_setupAutoCaptureTried && DateTimeOffset.UtcNow - _setupArenaSeenAt >= AutoCaptureDelay)
        {
            _setupAutoCaptureTried = true;
            await CaptureDuringSetupAsync();
        }
    }

    /// <summary>The Collection step's button, and its one automatic attempt.</summary>
    public async Task CaptureDuringSetupAsync()
    {
        if (Setup?.Current != SetupStep.Collection || !_setupMtgaRunning || _setupCapturing || _setupNeedsCards) return;

        _setupCapturing = true;
        _setupAutoCaptureTried = true;
        _setupMessage = "Reading your collection from MTG Arena (a minute or two the first time)…";
        PublishSetup();

        bool found;
        try
        {
            found = await CaptureAsync((message, _) =>
            {
                _setupMessage = message;
                PublishSetup();
            });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Setup collection capture failed");
            found = false;
            _setupMessage = $"Capturing the collection failed: {ex.Message}";
        }
        finally
        {
            _setupCapturing = false;
        }

        if (!found)
        {
            // Not a failure of the setup: the player may simply not have logged in yet.
            if (!_setupMessage.StartsWith("Capturing the collection failed", StringComparison.Ordinal))
                _setupMessage = "Your collection wasn't found yet. Log in to MTG Arena, wait at the main menu, then click Capture collection.";
            PublishSetup();
            return;
        }

        _setupStates[SetupStep.Collection] = SetupStepState.Done;
        StopArenaPoll();
        await RunSetupAsync();
    }

    private void StopArenaPoll()
    {
        _setupArenaPoll?.Dispose();
        _setupArenaPoll = null;
    }

    /// <summary>Next to the database, so a test copy (MTGA_ADVISOR_DB_PATH) has its own.</summary>
    private string SetupMarkerPath => services.Database.FilePath + ".setup";

    private async Task FinishSetupAsync()
    {
        StopArenaPoll();
        File.Delete(SetupMarkerPath);
        Setup = null;
        await ReloadRankingAsync();
        Status = "Setup complete. Your decks are ranked against your collection.";
        Notify();
    }

    private void PublishSetup()
    {
        Setup = new SetupProgress(
            _setupSteps,
            new Dictionary<SetupStep, SetupStepState>(_setupStates),
            SetupPlan.Next(_setupStates, _setupSteps),
            _setupMessage,
            _setupMtgaRunning,
            _setupCapturing,
            _setupNeedsCards);
        Notify();
    }
}
