using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Configuration;
using MtgaCollectionAdvisor.Core.Creators;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Logs;
using MtgaCollectionAdvisor.Core.Memory;
using MtgaCollectionAdvisor.Core.Storage;

namespace MtgaCollectionAdvisor.Core;

/// <summary>
/// Manual composition root - this app is small enough that a full DI container would
/// be pure ceremony, so callers just new this up once.
/// </summary>
public sealed class AdvisorServices : IAsyncDisposable
{
    public Database Database { get; }
    public CollectionStore CollectionStore { get; }
    public CardDatabaseStore CardDatabaseStore { get; }
    public CollectionImportService CollectionImportService { get; }
    public CollectionExporterJsonImporter CollectionExporterJsonImporter { get; }
    public MemoryCollectionSyncService MemoryCollectionSyncService { get; }
    public CuratedDeckStore CuratedDeckStore { get; }
    public PinnedDeckStore PinnedDeckStore { get; }
    public CollectionBrowserQuery CollectionBrowserQuery { get; }
    public DeckRankingService DeckRankingService { get; }
    public PlayerLogWatcher PlayerLogWatcher { get; }
    public HttpClient ScryfallHttpClient { get; }
    public ScryfallBulkImporter ScryfallBulkImporter { get; }
    public HttpClient ArchidektHttpClient { get; }
    public ArchidektClient ArchidektClient { get; }

    private AdvisorServices(AppConfig config)
    {
        Database = Database.CreateDefault(config.DatabasePathOverride);
        CollectionStore = new CollectionStore(Database);
        CardDatabaseStore = new CardDatabaseStore(Database);
        CollectionImportService = new CollectionImportService(CardDatabaseStore, CollectionStore);
        CollectionExporterJsonImporter = new CollectionExporterJsonImporter(CollectionStore);
        MemoryCollectionSyncService = new MemoryCollectionSyncService(CardDatabaseStore, CollectionStore);
        CuratedDeckStore = new CuratedDeckStore(Database);
        PinnedDeckStore = new PinnedDeckStore(Database);
        CollectionBrowserQuery = new CollectionBrowserQuery(Database);
        DeckRankingService = new DeckRankingService(new WildcardCalculator(CardDatabaseStore));
        PlayerLogWatcher = new PlayerLogWatcher(config.PlayerLogPathOverride);

        ScryfallHttpClient = ScryfallBulkImporter.CreateHttpClient();
        ScryfallBulkImporter = new ScryfallBulkImporter(ScryfallHttpClient);

        ArchidektHttpClient = ArchidektClient.CreateHttpClient();
        ArchidektClient = new ArchidektClient(ArchidektHttpClient);

        YouTubeHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        YouTubeFeedClient = new YouTubeFeedClient(YouTubeHttpClient);
    }

    public HttpClient YouTubeHttpClient { get; }
    public YouTubeFeedClient YouTubeFeedClient { get; }

    public static async Task<AdvisorServices> CreateAsync(AppConfig config, CancellationToken ct = default)
    {
        var services = new AdvisorServices(config);
        await SchemaInitializer.EnsureCreatedAsync(services.Database, ct);
        return services;
    }

    public ValueTask DisposeAsync()
    {
        PlayerLogWatcher.Dispose();
        ScryfallHttpClient.Dispose();
        ArchidektHttpClient.Dispose();
        YouTubeHttpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
