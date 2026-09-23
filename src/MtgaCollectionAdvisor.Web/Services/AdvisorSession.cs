using MtgaCollectionAdvisor.Core;
using MtgaCollectionAdvisor.Core.Configuration;
using MtgaCollectionAdvisor.Core.Creators;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Memory;
using MtgaCollectionAdvisor.Core.Models;

namespace MtgaCollectionAdvisor.Web.Services;

/// <summary>
/// Holds everything the UI shows and owns the long-running operations (memory scan,
/// deck fetch, card database import). Components subscribe to <see cref="Changed"/>
/// and re-render; nothing in the UI ever blocks on a scan.
/// </summary>
public sealed class AdvisorSession(AppConfig config) : IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private System.Threading.Timer? _mtgaWatchTimer;
    private bool _mtgaWasRunning;
    private AdvisorServices services = null!;

    public event Action? Changed;

    /// <summary>Set when the app could not start (database unreachable); the UI shows it.</summary>
    public string? StartupError { get; private set; }

    public string Status { get; private set; } = "Ready.";
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
            StartupError = $"Could not open the local database: {ex.Message}";
            Status = StartupError;
            Notify();
            return;
        }

        CardsUpdatedAt = await services.CardDatabaseStore.GetLastImportedAsync();
        services.PlayerLogWatcher.InventoryUpdated += OnWildcardsUpdated;
        services.PlayerLogWatcher.Start();

        await ReloadRankingAsync();

        _mtgaWatchTimer = new System.Threading.Timer(_ => _ = AutoScanIfGameStartedAsync(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(20));
    }

    public async Task SetFormatAsync(FormatDefinition format)
    {
        Format = format;
        await ReloadRankingAsync();
    }

    public Task ScanCollectionAsync() => RunAsync("Capturing collection", async report =>
    {
        var result = await services.MemoryCollectionSyncService.SyncAutomaticallyAsync(new Progress<string>(report));
        if (result is null)
        {
            report("Could not find the collection in memory. Open MTG Arena and visit the Collection screen.");
            return;
        }
        report($"Collection captured: {result.DistinctCards} cards ({result.TotalCopies} copies).");
        await ReloadRankingAsync();
    });

    public Task FetchDecksAsync() => RunAsync($"Fetching {Format.DisplayName} decks", async report =>
    {
        var format = Format;
        report($"Querying Archidekt ({format.DisplayName})...");
        var decks = await services.ArchidektClient.FetchTopDecksAsync(format, count: 60);

        var unique = DeckDeduplicator.Deduplicate(decks);
        report($"{decks.Count} decks received, {unique.Count} after removing duplicate lists.");

        await services.CuratedDeckStore.ReplaceAutoFetchedAsync(format, ArchidektClient.SourcePrefix, unique);
        await ReloadRankingAsync();
    });

    public Task RefreshCardDatabaseAsync() => RunAsync("Updating card database", async report =>
    {
        report("Downloading Scryfall bulk data (a few minutes)...");
        await services.CardDatabaseStore.ReplaceAllAsync(services.ScryfallBulkImporter.ImportAsync());
        CardsUpdatedAt = await services.CardDatabaseStore.GetLastImportedAsync();
        report("Card database updated.");
        await ReloadRankingAsync();
    });

    public Task DeleteDeckAsync(string sourceId) => RunAsync("Removing deck", async report =>
    {
        await services.CuratedDeckStore.DeleteDeckAsync(sourceId);
        report("Deck removed.");
        await ReloadRankingAsync();
    });

    public string ExportDeck(CandidateDeck deck) => ArenaDeckListWriter.Write(deck);

    public bool IsPinned(string sourceId) => Pins.ContainsKey(sourceId);

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

    // ---------- SPIKE: creator videos ----------

    public IReadOnlyList<CreatorVideoCard> CreatorVideos { get; private set; } = [];

    /// <summary>
    /// Pulls every curated channel's feed and, where the description gives the deck away,
    /// analyses it exactly like any other deck.
    ///
    /// What a video's deck *is* never changes once published, so that is cached on disk
    /// (<see cref="CreatorVideoCache"/>): Archidekt is asked about a video once, ever, and
    /// the feeds only when the cache is older than <see cref="CreatorVideoCache.MaxAge"/>.
    /// What the deck *costs* depends on the collection, so that is always computed fresh.
    /// </summary>
    public Task LoadCreatorVideosAsync(bool force = false) => RunAsync("Loading creator videos", async report =>
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var cache = await CreatorVideoCache.LoadAsync();
        var fromCache = !force && cache.IsFresh;
        var archidektFetches = 0;
        var failedFeeds = 0;

        if (!fromCache)
        {
            var feeds = await Task.WhenAll(CreatorChannels.All.Select(async c =>
            {
                try { return (Channel: c, Videos: await services.YouTubeFeedClient.FetchAsync(c)); }
                catch { return (Channel: c, Videos: (IReadOnlyList<CreatorVideo>?)null); }
            }));

            // A feed that failed this time is not a creator with no videos: YouTube
            // refuses the odd request, and treating that as empty would wipe the channel
            // from the cache. Keep what we already knew about it instead.
            var failed = feeds.Where(f => f.Videos is null).Select(f => f.Channel.Name).ToHashSet();
            var entries = cache.Videos.Where(v => failed.Contains(v.Video.Creator)).ToList();
            failedFeeds = failed.Count;

            foreach (var video in feeds.SelectMany(f => f.Videos ?? []))
            {
                if (cache.Find(video.VideoId) is { } known)
                {
                    entries.Add(known with { Video = video });
                    continue;
                }

                var source = VideoDeckExtractor.Extract(video.Description);
                var decklist = source.Kind switch
                {
                    DeckSourceKind.InlineList => source.Decklist,
                    DeckSourceKind.Archidekt => await ArchidektListAsync(source.ArchidektId!.Value),
                    _ => null
                };
                if (source.Kind == DeckSourceKind.Archidekt) archidektFetches++;
                entries.Add(new CachedCreatorVideo(video, source with { Decklist = null }, decklist));
            }

            cache = new CreatorVideoCache(DateTimeOffset.UtcNow, entries);
            await cache.SaveAsync();
        }

        var fetchTime = total.Elapsed;
        var analysis = System.Diagnostics.Stopwatch.StartNew();

        CreatorVideos = await AnalyseCreatorVideosAsync(cache.Videos);

        var withDeck = CreatorVideos.Count(c => c.Analysis is not null);
        var origin = fromCache ? "cache" : $"fetch+{archidektFetches}arch, {failedFeeds} feeds failed";
        report($"[{origin} {fetchTime.TotalSeconds:F1}s, analysis {analysis.Elapsed.TotalSeconds:F1}s, " +
               $"total {total.Elapsed.TotalSeconds:F1}s] {CreatorVideos.Count} videos, {withDeck} with deck");
    });

    /// <summary>
    /// One ranking pass per format over every video's deck, rather than one per video:
    /// the ranking shares its card-name lookups across a pass, so each card is resolved
    /// once instead of once per deck it appears in.
    /// </summary>
    private async Task<IReadOnlyList<CreatorVideoCard>> AnalyseCreatorVideosAsync(
        IReadOnlyList<CachedCreatorVideo> videos)
    {
        var drafts = videos
            .Where(v => v.Decklist is not null)
            .Select(v => new CandidateDeck(
                SourceId: $"video:{v.Video.VideoId}",
                Name: v.Video.Title,
                Url: v.Video.Url,
                FormatKey: Format.Key,
                Popularity: 0,
                Cards: ArenaDeckListParser.Parse(v.Decklist!),
                FetchedAt: v.Video.Published))
            .Where(d => d.Cards.Count > 0)
            .ToList();

        // The format is only in a video's title, so keep whichever format the list is
        // (most nearly) legal in. A Historic list is legal in neither and says so.
        var best = new Dictionary<string, (FormatDefinition Format, DeckAnalysisResult Result)>();
        foreach (var format in Formats.All)
        {
            foreach (var result in await services.DeckRankingService.RankAsync(format, drafts, Collection))
            {
                var id = result.Deck.SourceId;
                if (!best.TryGetValue(id, out var current)
                    || result.IllegalInFormat.Count < current.Result.IllegalInFormat.Count)
                {
                    best[id] = (format, result);
                }
            }
        }

        return videos
            .OrderByDescending(v => v.Video.Published)
            .Select(v => best.TryGetValue($"video:{v.Video.VideoId}", out var hit)
                ? new CreatorVideoCard(v.Video, v.Source, v.Decklist, hit.Format, hit.Result)
                : new CreatorVideoCard(v.Video, v.Source, v.Decklist, null, null))
            .ToList();
    }

    private async Task<string?> ArchidektListAsync(int id)
    {
        var deck = await services.ArchidektClient.TryFetchDeckAsync(id, Formats.Standard);
        return deck is null ? null : ArenaDeckListWriter.Write(deck);
    }

    /// <summary>Adds a deck pasted by hand (Arena export format) to the candidate pool.</summary>
    public Task ImportDeckAsync(string name, FormatDefinition format, string decklist, string sourceUrl = "") =>
        RunAsync("Importing deck", async report =>
        {
            var cards = ArenaDeckListParser.Parse(decklist);
            if (cards.Count == 0)
            {
                report("No cards recognised in that text.");
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

            if (format.Key == Format.Key)
            {
                LastImportedSourceId = deck.SourceId;
                report($"Deck \"{name}\" imported ({cards.Count} lines).");
                await ReloadRankingAsync();
            }
            else
            {
                // Saying nothing here would look identical to the import failing: the
                // list on screen is for the other format and does not move.
                report($"Deck \"{name}\" imported into {format.DisplayName} " +
                       $"({cards.Count} lines). Switch the format to see it.");
            }
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
                report("No cards recognised in that text - the deck was left unchanged.");
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
        Pins = await services.PinnedDeckStore.LoadAsync();
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

        if (justStarted && !IsBusy) await ScanCollectionAsync();
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

    private async Task RunAsync(string operation, Func<Action<string>, Task> action)
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
            await action(message =>
            {
                Status = message;
                Notify();
            });
        }
        catch (Exception ex)
        {
            Status = $"{operation} failed: {ex.Message}";
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

    public void Dispose()
    {
        _mtgaWatchTimer?.Dispose();
        _operationGate.Dispose();
    }
}

/// <summary>SPIKE: one video with whatever we managed to learn about its deck.</summary>
public sealed record CreatorVideoCard(
    CreatorVideo Video,
    VideoDeckSource Source,
    string? Decklist,
    FormatDefinition? Format,
    DeckAnalysisResult? Analysis);

/// <summary>SPIKE: what we learned about one video's deck, independent of the collection.</summary>
public sealed record CachedCreatorVideo(CreatorVideo Video, VideoDeckSource Source, string? Decklist);

/// <summary>
/// SPIKE: a JSON file beside the database. Deliberately not a table: it is disposable,
/// and deleting it only costs one slower load.
/// </summary>
public sealed record CreatorVideoCache(DateTimeOffset FetchedAt, IReadOnlyList<CachedCreatorVideo> Videos)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MtgaCollectionAdvisor", "creator-videos.json");

    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public bool IsFresh => Videos.Count > 0 && DateTimeOffset.UtcNow - FetchedAt < MaxAge;

    public CachedCreatorVideo? Find(string videoId) => Videos.FirstOrDefault(v => v.Video.VideoId == videoId);

    public static async Task<CreatorVideoCache> LoadAsync()
    {
        try
        {
            await using var stream = File.OpenRead(FilePath);
            return await System.Text.Json.JsonSerializer.DeserializeAsync<CreatorVideoCache>(stream, Json)
                   ?? Empty;
        }
        catch
        {
            // Missing or unreadable: the only cost is fetching everything again.
            return Empty;
        }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var stream = File.Create(FilePath);
        await System.Text.Json.JsonSerializer.SerializeAsync(stream, this, Json);
    }

    private static CreatorVideoCache Empty => new(DateTimeOffset.MinValue, []);
}
