using MtgaCollectionAdvisor.Core.Analysis;
using MtgaCollectionAdvisor.Core.Arena;
using MtgaCollectionAdvisor.Core.Cards;
using MtgaCollectionAdvisor.Core.Configuration;
using MtgaCollectionAdvisor.Core.Creators;
using MtgaCollectionAdvisor.Core.Decks;
using MtgaCollectionAdvisor.Core.Export;
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
    public ArenaDeckStore ArenaDeckStore { get; }
    public DataExportService DataExportService { get; }
    public DeckRankingService DeckRankingService { get; }
    public PlayerLogWatcher PlayerLogWatcher { get; }
    public HttpClient ScryfallHttpClient { get; }
    public ScryfallBulkImporter ScryfallBulkImporter { get; }
    public HttpClient ArchidektHttpClient { get; }
    public ArchidektClient ArchidektClient { get; }
    public ArchidektDeckSync ArchidektDeckSync { get; }
    public HttpClient YouTubeHttpClient { get; }
    public CreatorVideoStore CreatorVideoStore { get; }
    public CreatorVideoService CreatorVideoService { get; }
    public HttpClient CreatorRosterHttpClient { get; }
    public CreatorRosterService CreatorRosterService { get; }

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
        ArenaDeckStore = new ArenaDeckStore(Database);
        DataExportService = new DataExportService(CollectionStore, CardDatabaseStore, CuratedDeckStore, ArenaDeckStore);
        DeckRankingService = new DeckRankingService(new WildcardCalculator(CardDatabaseStore));
        PlayerLogWatcher = new PlayerLogWatcher(config.PlayerLogPathOverride);

        ScryfallHttpClient = ScryfallBulkImporter.CreateHttpClient();
        ScryfallBulkImporter = new ScryfallBulkImporter(ScryfallHttpClient);

        ArchidektHttpClient = ArchidektClient.CreateHttpClient();
        ArchidektClient = new ArchidektClient(ArchidektHttpClient);
        ArchidektDeckSync = new ArchidektDeckSync(ArchidektClient, CuratedDeckStore, PinnedDeckStore);

        YouTubeHttpClient = YouTubeFeedClient.CreateHttpClient();
        CreatorVideoStore = new CreatorVideoStore(Database);
        CreatorVideoService = new CreatorVideoService(
            new YouTubeFeedClient(YouTubeHttpClient), ArchidektClient, CreatorVideoStore, DeckRankingService);
        CreatorRosterHttpClient = CreatorRosterService.CreateHttpClient();
        CreatorRosterService = new CreatorRosterService(CreatorRosterHttpClient, new CreatorRosterStore(Database));
    }

    public static async Task<AdvisorServices> CreateAsync(AppConfig config, CancellationToken ct = default)
    {
        var services = new AdvisorServices(config);
        await SchemaMigrator.MigrateAsync(services.Database, ct);
        return services;
    }

    public ValueTask DisposeAsync()
    {
        PlayerLogWatcher.Dispose();
        ScryfallHttpClient.Dispose();
        ArchidektHttpClient.Dispose();
        YouTubeHttpClient.Dispose();
        CreatorRosterHttpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
