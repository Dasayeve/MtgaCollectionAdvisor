using MtgaCollectionAdvisor.Core;
using MtgaCollectionAdvisor.Core.Arena;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Configuration;
using MtgaCollectionAdvisor.Core.Creators;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Export;
using MtgaCollectionAdvisor.Core.Hosting;
using MtgaCollectionAdvisor.Core.Memory;
using MtgaCollectionAdvisor.Core.Models;
using MtgaCollectionAdvisor.Core.Storage;

namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>
/// Holds everything the UI shows and owns the long-running operations (memory scan,
/// deck fetch, card database import). Components subscribe to <see cref="Changed"/>
/// and re-render; nothing in the UI ever blocks on a scan.
/// </summary>
public sealed partial class AdvisorSession(AppConfig config, ILogger<AdvisorSession> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private System.Threading.Timer? _mtgaWatchTimer;
    private System.Threading.Timer? _cardRefreshTimer;
    private int _cardRefreshRunning;
    private bool _mtgaWasRunning;
    private AdvisorServices services = null!;

    public event Action? Changed;

    /// <summary>Set when the app could not start (database unreachable); the UI shows it.</summary>
    public string? StartupError { get; private set; }

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            StatusLevel = StatusLevel.Info; // any new message replaces a warning
        }
    }

    private string _status = "Ready.";

    /// <summary>Whether the status bar shows something the player should act on.</summary>
    public StatusLevel StatusLevel { get; private set; }

    public bool IsBusy { get; private set; }
    public string? BusyOperation { get; private set; }

    public CollectionSnapshot Collection { get; private set; } = CollectionSnapshot.Empty;
    public IReadOnlyList<DeckAnalysisResult> Decks { get; private set; } = [];
    public IReadOnlyDictionary<string, PinnedDeck> Pins { get; private set; } = new Dictionary<string, PinnedDeck>();
    public FormatDefinition Format { get; private set; } = Formats.Standard;
    public DateTimeOffset? CardsUpdatedAt { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            services = await AdvisorServices.CreateAsync(config);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not open the local database");
            StartupError = $"Could not open the local database: {ex.Message}";
            Status = StartupError;
            StatusLevel = StatusLevel.Warning;
            Notify();
            return;
        }

        CardsUpdatedAt = await services.CardDatabaseStore.GetLastImportedAsync();
        services.PlayerLogWatcher.InventoryUpdated += OnWildcardsUpdated;
        services.PlayerLogWatcher.DetailedLogsReported += OnDetailedLogsReported;
        services.PlayerLogWatcher.WatchError += ex => log.LogWarning(ex, "Reading Player.log failed");
        services.PlayerLogWatcher.ArenaDecksUpdated += OnArenaDecksUpdated;
        ArenaDecksCapturedAt = (await services.ArenaDeckStore.LoadAsync())?.CapturedAt;
        services.PlayerLogWatcher.Start();

        await ReloadRankingAsync();
        await StartSetupIfNeededAsync();
        await BackfillCardImagesIfNeededAsync();

        _mtgaWatchTimer = new System.Threading.Timer(_ => _ = AutoScanIfGameStartedAsync(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(20));

        // A new set's cards (#89). The tick only compares stored times; the network is used when
        // CardRefreshSchedule allows it, at most every 6 hours per call. A minute after start, so
        // the start-up work (and a card backfill, if one runs) goes first.
        _cardRefreshTimer = new System.Threading.Timer(_ => _ = RefreshCardsForNewSetIfNeededAsync(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
    }

    /// <summary>The user's own decks per format key, so an empty User decks tab can say where the others are.</summary>
    public IReadOnlyDictionary<string, int> UserDeckCounts { get; private set; } = new Dictionary<string, int>();

    public async Task SetFormatAsync(FormatDefinition format)
    {
        Format = format;
        await ReloadRankingAsync();
    }

    public Task ScanCollectionAsync() => RunAsync("Capturing collection", report => CaptureAsync(report));

    public Task FetchDecksAsync() => RunAsync($"Fetching {Format.DisplayName} decks", report => FetchDecksAsync(Format, report));

    public Task RefreshCardDatabaseAsync() => RunAsync("Updating card database", ImportCardsAsync);

    /// <summary>
    /// A card database from before a later migration's card columns (image URLs, #59; the
    /// non-basic land flag, #61) is missing that data; re-import it once, in the background,
    /// rather than leave the player to find Update cards. The setup screen imports cards
    /// itself, so it is left alone.
    /// </summary>
    private async Task BackfillCardImagesIfNeededAsync()
    {
        if (Setup is not null) return;

        if (!CardDatabaseStore.NeedsCardDataBackfill(await services.CardDatabaseStore.CountCardDataAsync())) return;

        _ = Task.Run(() => RunAsync("Updating card data", ImportCardsAsync));
    }

    /// <summary>
    /// Re-imports the card database once when the maintainer's card-data.json says a new set has
    /// landed and Scryfall has it (#89). In the background, like the backfill above: nothing to
    /// click, and the status bar says what is happening. Skipped during the first-run setup and
    /// while another operation runs; a skipped tick records nothing, so a later one goes ahead.
    /// </summary>
    private async Task RefreshCardsForNewSetIfNeededAsync()
    {
        if (Setup is not null || IsBusy) return;
        if (Interlocked.Exchange(ref _cardRefreshRunning, 1) == 1) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var check = await services.CardRefreshService.CheckAsync(now);
            if (check.ReadFailure is { } reason) log.LogWarning("Card refresh check learnt nothing new: {Reason}", reason);
            if (!check.ShouldImport || IsBusy) return;

            // Recorded first, so an import that fails is tried again only in the next window.
            await services.CardRefreshService.RecordAutoImportAsync(now);
            await RunAsync("Updating cards for a new set", ImportCardsAsync);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Card refresh check failed");
        }
        finally
        {
            Volatile.Write(ref _cardRefreshRunning, 0);
        }
    }

    // The bodies of the operations above, shared with the first-run setup, which needs to
    // know whether each one worked rather than only what it said in the status bar.

    /// <summary>False when the collection was not found in MTG Arena's memory.</summary>
    private async Task<bool> CaptureAsync(StatusReport report)
    {
        var result = await services.MemoryCollectionSyncService.SyncAutomaticallyAsync(new Progress<string>(message => report(message)));
        if (result is null)
        {
            report("Could not find the collection in memory. Open MTG Arena and visit the Collection screen.", StatusLevel.Warning);
            return false;
        }
        report($"Collection captured: {result.DistinctCards} cards ({result.TotalCopies} copies).");
        await ReloadRankingAsync();
        return true;
    }

    private async Task<DeckSyncReport> FetchDecksAsync(FormatDefinition format, StatusReport report)
    {
        var result = await services.ArchidektDeckSync.SyncAsync(format, new ImmediateProgress(message => report(message)));
        if (result.StoppedBecause is not null)
        {
            log.LogWarning("Archidekt fetch for {Format} stopped early: {Reason} ({Cause})",
                format.DisplayName, result.StoppedBecause, result.StopCause ?? "unknown cause");
        }
        await ReloadRankingAsync();
        report(result.Describe());
        return result;
    }

    private async Task ImportCardsAsync(StatusReport report)
    {
        report("Downloading Scryfall bulk data (a few minutes)...");
        // Records which Scryfall file the cards came from: every import, manual or not, is what
        // clears a pending card-data flag (#89).
        var file = await services.ScryfallBulkImporter.GetDefaultCardsAsync();
        await services.CardDatabaseStore.ReplaceAllAsync(services.ScryfallBulkImporter.ImportAsync(file), file.UpdatedAt);
        CardsUpdatedAt = await services.CardDatabaseStore.GetLastImportedAsync();
        report("Card database updated.");
        await ReloadRankingAsync();
    }

    public Task DeleteDeckAsync(string sourceId) => RunAsync("Removing deck", async report =>
    {
        await services.CuratedDeckStore.DeleteDeckAsync(sourceId);
        report("Deck removed.");
        await ReloadRankingAsync();
    });

    public string ExportDeck(CandidateDeck deck) => ArenaDeckListWriter.Write(deck);

    /// <summary>Whether the deck view offers the Liga Magic quote (#62): Windows region Brazil only.</summary>
    public bool LigaMagicAvailable { get; } =
        LigaMagicList.IsAvailable(WindowsRegion.HomeLocation(), WindowsRegion.RegionalFormat());

    /// <summary>
    /// Opens Liga Magic's list page in the player's default browser, where they are logged in:
    /// its search field only appears then. Not the app's window, which has no login.
    /// </summary>
    public bool OpenLigaMagic()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LigaMagicList.PageUrl) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Opening Liga Magic in the default browser failed");
            return false;
        }
    }

    /// <summary>Writes the user's data out for the download endpoints; null before the app has started.</summary>
    public DataExportService? DataExport => services?.DataExportService;

    public bool IsPinned(string sourceId) => Pins.ContainsKey(sourceId);

    /// <summary>
    /// Why a card name can go unrecognised (#87), for every place that says so. The app can't
    /// tell these apart, so it names them all - and a new set's cards are the common case.
    /// </summary>
    public const string UnrecognisedHint =
        "Not in the card database: a card from a set that isn't on Arena yet, a name in another " +
        "language, or a typo. Update cards (under More) picks up new sets once they reach Arena.";

    /// <summary>
    /// The wildcards the deck open in the list needs, so the top bar can show which of the
    /// player's totals fall short of it. Null when no deck is open.
    /// </summary>
    public WildcardNeed? OpenDeckNeed { get; private set; }

    /// <summary>
    /// Its own event, not Changed: the deck list resets to page 1 on Changed, so opening a
    /// deck on page 3 would send the list back to page 1.
    /// </summary>
    public event Action? OpenDeckChanged;

    /// <summary>Only raises OpenDeckChanged on a change: the deck list calls this on every render.</summary>
    public void SetOpenDeck(WildcardNeed? need)
    {
        if (OpenDeckNeed == need) return;
        OpenDeckNeed = need;
        OpenDeckChanged?.Invoke();
    }

    /// <summary>
    /// Pins or unpins without refetching anything - the ranking in memory is unchanged,
    /// only which decks are marked.
    /// </summary>
    public async Task TogglePinAsync(DeckAnalysisResult deck)
    {
        if (services is null) return;

        if (IsPinned(deck.Deck.SourceId))
        {
            await services.PinnedDeckStore.UnpinAsync(deck.Deck.SourceId);
        }
        else
        {
            await services.PinnedDeckStore.PinAsync(deck.Deck.SourceId, deck.Deck.FormatKey, deck.Needed.Total);
        }

        Pins = await services.PinnedDeckStore.LoadAsync();
        Notify();
    }

    /// <summary>Card-name autocomplete for the card filters.</summary>
    public Task<IReadOnlyList<string>> SearchCardNamesAsync(string term) =>
        services is null
            ? Task.FromResult<IReadOnlyList<string>>([])
            : services.CardDatabaseStore.SearchNamesAsync(term);

    /// <summary>
    /// The deck imported most recently into the current format, so the UI can take the
    /// user to where it landed rather than leaving them on a list that did not move.
    /// Read once, via <see cref="ConsumeLastImport"/>.
    /// </summary>
    public string? LastImportedSourceId { get; private set; }

    /// <summary>Returns the last import and forgets it, so it is acted on exactly once.</summary>
    public string? ConsumeLastImport()
    {
        var sourceId = LastImportedSourceId;
        LastImportedSourceId = null;
        return sourceId;
    }

    /// <summary>
    /// What a decklist would cost, without storing it - so the user can decide whether a
    /// deck is worth keeping before they commit to it.
    ///
    /// Deliberately outside RunAsync: this is a read that runs while the user types, and
    /// taking the operation gate would flicker the status line on every change and could
    /// block a real operation. Nothing here reaches the store.
    /// </summary>
    public async Task<DeckAnalysisResult?> PreviewDeckAsync(FormatDefinition format, string decklist)
    {
        if (services is null) return null;

        var cards = ArenaDeckListParser.Parse(decklist);
        if (cards.Count == 0) return null;

        var draft = new CandidateDeck(
            SourceId: "preview:unsaved",
            Name: "",
            Url: "",
            FormatKey: format.Key,
            Popularity: 0,
            Cards: cards,
            FetchedAt: DateTimeOffset.UtcNow);

        try
        {
            var ranked = await services.DeckRankingService.RankAsync(format, [draft], Collection);
            return ranked.Count > 0 ? ranked[0] : null;
        }
        catch
        {
            // A preview that fails must never stop the user saving.
            return null;
        }
    }

    // ---------- Creator videos (feature-flagged) ----------

    public bool CreatorVideosEnabled => config.CreatorVideosEnabled;

    public IReadOnlyList<CreatorVideoCard> CreatorVideos { get; private set; } = [];

    /// <summary>The curated channels (#64): creators.json from the repository, else the last copy, else the compiled list.</summary>
    public IReadOnlyList<CreatorChannel> CreatorChannelList { get; private set; } = CreatorChannels.All;

    /// <summary>What the cache knows, kept so a collection change can re-price without the network.</summary>
    private IReadOnlyList<CreatorVideo> _creatorVideoSources = [];

    /// <summary>
    /// Loads creator videos - from the cache while it is fresh, from the feeds otherwise or
    /// when <paramref name="force"/> is set - and prices them against the collection. Does
    /// nothing at all when the feature is off: no feed is ever fetched.
    /// </summary>
    public Task LoadCreatorVideosAsync(bool force = false)
    {
        if (!CreatorVideosEnabled) return Task.CompletedTask;

        return RunAsync("Loading creator videos", report => LoadCreatorsAsync(force, report));
    }

    private async Task LoadCreatorsAsync(bool force, StatusReport report)
    {
        var roster = await services.CreatorRosterService.LoadAsync();
        CreatorChannelList = roster.Channels;
        if (roster.ReadFailure is not null)
        {
            log.LogWarning("creators.json not read ({Reason}); using the {Count} creators already known",
                roster.ReadFailure, roster.Channels.Count);
        }

        var loaded = await services.CreatorVideoService.LoadAsync(CreatorChannelList, force);
        foreach (var failure in loaded.FeedFailures)
        {
            log.LogWarning("Creator feed {Creator} unavailable: {Reason} ({Failures} in a row)",
                failure.Creator, failure.Reason, failure.ConsecutiveFailures);
        }
        _creatorVideoSources = loaded.Videos;
        CreatorVideos = await services.CreatorVideoService.PriceAsync(loaded.Videos, Collection);

        var withDeck = CreatorVideos.Count(c => c.Analysis is not null);
        var unavailable = loaded.FailedFeeds > 0
            ? $" ({loaded.FailedFeeds} creator feed{(loaded.FailedFeeds == 1 ? "" : "s")} unavailable, showing what was cached)"
            : "";
        report($"{CreatorVideos.Count} creator videos, {withDeck} with a deck priced against your collection.{unavailable}");
    }

    /// <summary>Costs follow the collection; what a video's deck is does not, so no fetch here.</summary>
    private async Task RepriceCreatorVideosAsync()
    {
        if (!CreatorVideosEnabled || _creatorVideoSources.Count == 0) return;
        CreatorVideos = await services.CreatorVideoService.PriceAsync(_creatorVideoSources, Collection);
    }

    /// <summary>
    /// Adds a deck pasted by hand (Arena export format) to the candidate pool.
    /// <paramref name="sourceUrl"/> is where it came from - a creator video, when imported
    /// from the Creators tab - and becomes the deck's source link.
    /// </summary>
    public Task ImportDeckAsync(string name, FormatDefinition format, string decklist, string sourceUrl = "") =>
        RunAsync("Importing deck", async report =>
        {
            var cards = ArenaDeckListParser.Parse(decklist);
            if (cards.Count == 0)
            {
                report("No cards recognised in that text.", StatusLevel.Warning);
                return;
            }

            var deck = new CandidateDeck(
                SourceId: $"{CandidateDeck.ManualSourcePrefix}{Guid.NewGuid()}",
                Name: name,
                Url: sourceUrl,
                FormatKey: format.Key,
                Popularity: 0,
                Cards: cards,
                FetchedAt: DateTimeOffset.UtcNow);

            await services.CuratedDeckStore.AddDeckAsync(deck);

            // A deck imported into another format goes where it lives: staying on this format's
            // list would look exactly like the import having failed.
            Format = format;
            LastImportedSourceId = deck.SourceId;
            report($"Deck \"{name}\" imported into {format.DisplayName} ({cards.Count} lines).");
            await ReloadRankingAsync();
        });

    /// <summary>
    /// Saves an edit to a deck the user owns, keeping its source id so the pin on it -
    /// and the deck's creation date - survive. Re-importing would mint a new id and lose
    /// both.
    /// </summary>
    public Task UpdateDeckAsync(CandidateDeck existing, string name, FormatDefinition format, string decklist) =>
        RunAsync("Saving deck", async report =>
        {
            var cards = ArenaDeckListParser.Parse(decklist);
            if (cards.Count == 0)
            {
                // Abort before writing: a typo must not empty a deck the user already has.
                report("No cards recognised in that text - the deck was left unchanged.", StatusLevel.Warning);
                return;
            }

            var updated = existing with
            {
                Name = name,
                FormatKey = format.Key,
                Cards = cards
            };

            await services.CuratedDeckStore.UpdateUserDeckAsync(updated);

            var wasPinned = IsPinned(existing.SourceId);
            LastImportedSourceId = updated.SourceId;
            Format = format;
            await ReloadRankingAsync();

            if (wasPinned)
            {
                // The baseline measured progress towards a list that no longer exists.
                // Rebasing is the honest option, and saying so is the rest of it.
                var need = Decks.FirstOrDefault(d => d.Deck.SourceId == updated.SourceId)?.Needed.Total ?? 0;
                await services.PinnedDeckStore.RebaseAsync(updated.SourceId, need);
                Pins = await services.PinnedDeckStore.LoadAsync();
                report($"Deck \"{name}\" updated ({cards.Count} lines). Pin progress now measures from this list.");
            }
            else
            {
                report($"Deck \"{name}\" updated ({cards.Count} lines).");
            }
        });

    private async Task ReloadRankingAsync()
    {
        Collection = await services.CollectionStore.LoadAsync();
        await RepriceCreatorVideosAsync();
        Pins = await services.PinnedDeckStore.LoadAsync();
        UserDeckCounts = await services.CuratedDeckStore.CountUserDecksAsync();
        var stored = await services.CuratedDeckStore.LoadAsync(Format);

        if (stored.Count == 0)
        {
            Decks = [];
            Notify();
            return;
        }

        // Every ranked deck, nothing dropped. Deciding which of them a given list shows is
        // DeckFilter's job now (DeckFilterCriteria.IncludeUnplayable): filtering here cost
        // an imported deck its only route into the UI, after import had already reported
        // success, and nothing told the user why.
        Decks = await services.DeckRankingService.RankAsync(Format, stored, Collection);
        Notify();
    }

    private async Task AutoScanIfGameStartedAsync()
    {
        var running = MemoryCollectionSyncService.IsMtgaRunning();
        var justStarted = running && !_mtgaWasRunning;
        _mtgaWasRunning = running;

        // The setup screen runs its own MTG Arena step; the page it would scan for isn't shown.
        if (Setup is not null) return;

        if (justStarted && !IsBusy) await ScanCollectionAsync();
    }

    /// <summary>
    /// When the app last read Arena's saved decks from Player.log - not when the player logged
    /// in, which may have been hours before. Null if never.
    /// </summary>
    public DateTimeOffset? ArenaDecksCapturedAt { get; private set; }

    /// <summary>The player's own Arena decks for the Import deck dialog, marked when already in the app.</summary>
    public async Task<IReadOnlyList<ArenaDeckChoice>> GetArenaDeckChoicesAsync()
    {
        if (services is null || await services.ArenaDeckStore.LoadAsync() is not { } snapshot) return [];

        var names = await services.CardDatabaseStore.GetNamesAsync();
        return ArenaDeckImport.Choices(snapshot.Decks, names, await LoadUserDeckIdsAsync());
    }

    /// <summary>
    /// Brings the picked Arena decks in as user decks. One already in the app is updated in
    /// place, keeping its id and so its pin; the rest are added. Arena itself is never touched.
    /// </summary>
    public Task ImportArenaDecksAsync(IReadOnlyCollection<string> arenaDeckIds) =>
        RunAsync("Importing Arena decks", async report =>
        {
            if (await services.ArenaDeckStore.LoadAsync() is not { } snapshot) return;

            var names = await services.CardDatabaseStore.GetNamesAsync();
            var inApp = await LoadUserDeckIdsAsync();
            var now = DateTimeOffset.UtcNow;
            int added = 0, updated = 0;
            string? lastInCurrentFormat = null;
            CandidateDeck? lastImported = null;

            foreach (var arenaDeck in snapshot.Decks.Where(d => arenaDeckIds.Contains(d.Id) && !d.IsWizardsDeck))
            {
                if (ArenaDeckImport.ToCandidateDeck(arenaDeck, names, now) is not { } deck || deck.Cards.Count == 0) continue;

                if (inApp.Contains(deck.SourceId))
                {
                    await services.CuratedDeckStore.UpdateUserDeckAsync(deck);
                    updated++;
                }
                else
                {
                    await services.CuratedDeckStore.AddDeckAsync(deck);
                    added++;
                }

                if (deck.FormatKey == Format.Key) lastInCurrentFormat = deck.SourceId;
                lastImported = deck;
            }

            // None in the format on screen: go to the imported decks' format, or "Find them under
            // User decks" would point at a tab that doesn't have them.
            if (lastInCurrentFormat is null && lastImported is not null)
            {
                Format = Formats.All.First(f => f.Key == lastImported.FormatKey);
                lastInCurrentFormat = lastImported.SourceId;
            }

            LastImportedSourceId = lastInCurrentFormat;
            await ReloadRankingAsync();

            var parts = new List<string>();
            if (added > 0) parts.Add($"{added} added");
            if (updated > 0) parts.Add($"{updated} updated");
            report(parts.Count == 0
                ? "No Arena decks imported."
                : $"Arena decks: {string.Join(", ", parts)}. Find them under My decks.");
        });

    private async Task<IReadOnlySet<string>> LoadUserDeckIdsAsync()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var format in Formats.All)
        {
            foreach (var deck in await services.CuratedDeckStore.LoadAsync(format))
            {
                if (deck.IsUserDeck) ids.Add(deck.SourceId);
            }
        }
        return ids;
    }

    private void OnArenaDecksUpdated(IReadOnlyList<ArenaDeck> decks)
    {
        _ = Task.Run(async () =>
        {
            var capturedAt = DateTimeOffset.UtcNow;
            await services.ArenaDeckStore.ReplaceAsync(decks, capturedAt);
            ArenaDecksCapturedAt = capturedAt;
            Notify();
        });
    }

    /// <summary>What Player.log last said about MTG Arena's Detailed Logs option (#57).</summary>
    public DetailedLogs DetailedLogs { get; private set; }

    /// <summary>
    /// Why the wildcards are unknown and what to do about it; null once they are read. The
    /// top bar, the craftable column and the craftable filters show it instead of zeros.
    /// </summary>
    public string? WildcardHint => WildcardStatus.Hint(
        Collection.Wildcards is not null, DetailedLogs, services?.PlayerLogWatcher.LogExists == true);

    private void OnDetailedLogsReported(bool enabled)
    {
        DetailedLogs = enabled ? DetailedLogs.Enabled : DetailedLogs.Disabled;
        Notify();
    }

    private void OnWildcardsUpdated(WildcardInventory inventory)
    {
        _ = Task.Run(async () =>
        {
            await services.CollectionStore.SaveWildcardsAsync(inventory);
            Collection = Collection with { Wildcards = inventory };
            Notify();
        });
    }

    private async Task RunAsync(string operation, Func<StatusReport, Task> action)
    {
        if (!await _operationGate.WaitAsync(0))
        {
            Status = $"Please wait: {BusyOperation} in progress.";
            Notify();
            return;
        }

        IsBusy = true;
        BusyOperation = operation;
        Status = $"{operation}...";
        Notify();

        try
        {
            await action((message, level) =>
            {
                Status = message;
                StatusLevel = level;
                Notify();
            });
        }
        catch (Exception ex)
        {
            // Shown in the status bar, and kept in the log file (#52) for when a player reports it.
            log.LogError(ex, "{Operation} failed", operation);
            Status = $"{operation} failed: {ex.Message}";
            StatusLevel = StatusLevel.Warning;
        }
        finally
        {
            IsBusy = false;
            BusyOperation = null;
            _operationGate.Release();
            Notify();
        }
    }

    private void Notify() => Changed?.Invoke();

    /// <summary>The folder with the app's log files (#52), next to the database.</summary>
    public string LogFolder => LogFiles.FolderFor(services?.Database.FilePath
        ?? Database.CreateDefault(config.DatabasePathOverride).FilePath);

    /// <summary>Opens the log folder in Explorer; the app runs locally, as the player.</summary>
    public void OpenLogFolder()
    {
        var folder = LogFolder;
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Reports on the calling thread. <see cref="Progress{T}"/> posts each report for later,
    /// so a late one can land after the final summary and overwrite it in the status bar.
    /// </summary>
    private sealed class ImmediateProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>
    /// Runs when the app stops - including when its window has been gone for the grace
    /// period (<see cref="StopWhenNoWindowService"/>) - so the MTG Arena watcher and the
    /// Player.log poller end with it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_mtgaWatchTimer is not null) await _mtgaWatchTimer.DisposeAsync();
        if (_cardRefreshTimer is not null) await _cardRefreshTimer.DisposeAsync();
        if (_setupArenaPoll is not null) await _setupArenaPoll.DisposeAsync();
        if (services is not null) await services.DisposeAsync();
        _operationGate.Dispose();
    }
}
