namespace MtgaCollectionAdvisor.Core.Storage;

/// <summary>
/// The schema, one step at a time. The database's <c>PRAGMA user_version</c> is the last step
/// it has; <see cref="SchemaMigrator"/> applies the rest. A released migration is never edited:
/// a change to the schema is a new migration at the end (ReleasedMigrations_Should_BeUnchanged).
/// </summary>
public static class Migrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        // The schema as it stood before versioning, still IF NOT EXISTS: on a new file it creates
        // everything, on a database from before versioning (user_version 0) it only stamps it.
        new(1, "Baseline", """
                CREATE TABLE IF NOT EXISTS collection_cards (
                    grp_id     INTEGER PRIMARY KEY,
                    quantity   INTEGER NOT NULL,
                    synced_at  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS wildcard_inventory (
                    id         INTEGER PRIMARY KEY CHECK (id = 1),
                    commons    INTEGER NOT NULL,
                    uncommons  INTEGER NOT NULL,
                    rares      INTEGER NOT NULL,
                    mythics    INTEGER NOT NULL,
                    synced_at  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS cards (
                    grp_id           INTEGER PRIMARY KEY,
                    name             TEXT NOT NULL,
                    set_code         TEXT NOT NULL,
                    mana_cost        TEXT NOT NULL,
                    colors           TEXT NOT NULL,
                    rarity           TEXT NOT NULL,
                    standard_legal   INTEGER NOT NULL,
                    pioneer_legal    INTEGER NOT NULL,
                    updated_at       TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_cards_name ON cards (name COLLATE NOCASE);

                CREATE TABLE IF NOT EXISTS card_import_state (
                    id             INTEGER PRIMARY KEY CHECK (id = 1),
                    last_imported  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS decks (
                    source_id    TEXT PRIMARY KEY,
                    format_key   TEXT NOT NULL,
                    name         TEXT NOT NULL,
                    url          TEXT NOT NULL,
                    popularity   INTEGER NOT NULL,
                    fetched_at   TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_decks_format ON decks (format_key);

                -- No foreign key to decks on purpose: a pin has to outlive the deck row being
                -- deleted and reinserted by a source refresh, which is the whole point of pinning.
                CREATE TABLE IF NOT EXISTS pinned_decks (
                    source_id             TEXT PRIMARY KEY,
                    format_key            TEXT NOT NULL,
                    pinned_at             TEXT NOT NULL,
                    wildcards_when_pinned INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS deck_cards (
                    source_id  TEXT NOT NULL REFERENCES decks (source_id) ON DELETE CASCADE,
                    card_name  TEXT NOT NULL,
                    board      TEXT NOT NULL,
                    quantity   INTEGER NOT NULL,
                    PRIMARY KEY (source_id, card_name, board)
                );

                -- The version of each deck a source listed that was read in detail, kept or not, so
                -- a fetch only asks for what is new or changed (ArchidektDeckSync). kept = 0 marks a
                -- deck that was read and rejected; it is not asked for again until it changes.
                CREATE TABLE IF NOT EXISTS source_deck_versions (
                    source_id          TEXT PRIMARY KEY,
                    format_key         TEXT NOT NULL,
                    source_updated_at  TEXT NOT NULL,
                    kept               INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS deck_sync_state (
                    format_key    TEXT PRIMARY KEY,
                    last_sync_at  TEXT,
                    full_walk_at  TEXT
                );

                -- The decks saved in MTG Arena as it last logged them at login, kept so they can be
                -- exported with Arena closed or after Player.log has rotated. Replaced whole on
                -- each capture.
                CREATE TABLE IF NOT EXISTS arena_decks (
                    deck_id      TEXT PRIMARY KEY,
                    name         TEXT NOT NULL,
                    format       TEXT NOT NULL,
                    wizards      INTEGER NOT NULL,
                    cards_json   TEXT NOT NULL,
                    captured_at  TEXT NOT NULL
                );

                -- Creator videos: what each video's description told us, so neither its feed nor
                -- Archidekt is asked again. The description itself is not kept.
                CREATE TABLE IF NOT EXISTS creator_videos (
                    video_id       TEXT PRIMARY KEY,
                    creator        TEXT NOT NULL,
                    title          TEXT NOT NULL,
                    published_at   TEXT NOT NULL,
                    source_kind    TEXT NOT NULL,
                    decklist       TEXT,
                    archidekt_id   INTEGER,
                    external_site  TEXT,
                    external_url   TEXT,
                    language       TEXT NOT NULL DEFAULT 'en'
                );

                -- Per channel, so each feed keeps its own schedule and backoff (CreatorFeedSchedule).
                CREATE TABLE IF NOT EXISTS creator_feeds (
                    creator               TEXT PRIMARY KEY,
                    last_success_at       TEXT,
                    last_attempt_at       TEXT,
                    consecutive_failures  INTEGER NOT NULL
                );
                """),

        // Left behind by an early build of the Creators tab; nothing reads it.
        new(2, "Drop creator_feed_state", "DROP TABLE IF EXISTS creator_feed_state;"),

        // Scryfall image URLs for the hover preview (#59). cards is a cache table: existing rows
        // get NULL until the next Update cards fills them.
        new(3, "Card image URLs", """
            ALTER TABLE cards ADD COLUMN image_url TEXT;
            ALTER TABLE cards ADD COLUMN back_image_url TEXT;
            """),

        // Whether a card is a non-basic land, for pricing a deck without them (#61). NULL until
        // the automatic re-import fills it (CardDatabaseStore.NeedsCardDataBackfill).
        new(4, "Non-basic land flag", "ALTER TABLE cards ADD COLUMN is_nonbasic_land INTEGER;"),

        // The last good creators.json read from the repository (#64), and when it was last asked
        // for. A cache: one row, and losing it only means the compiled list until the next check.
        new(5, "Creator roster", """
            CREATE TABLE creator_roster (
                id               INTEGER PRIMARY KEY CHECK (id = 1),
                json             TEXT,
                fetched_at       TEXT,
                last_attempt_at  TEXT
            );
            """),

        // Whether a card is legal in Brawl (#76). NULL until the automatic re-import fills it
        // (CardDatabaseStore.NeedsCardDataBackfill), like the columns of migrations 3 and 4.
        new(6, "Brawl legality", "ALTER TABLE cards ADD COLUMN brawl_legal INTEGER;"),

        // Whether a card is legal in Standard Brawl (#76). Filled the same way as brawl_legal.
        new(7, "Standard Brawl legality", "ALTER TABLE cards ADD COLUMN standard_brawl_legal INTEGER;"),

        // The card database refreshing itself on a new set (#89): which Scryfall file the cards
        // came from (NULL until the next import; unknown counts as older than any flag), and the
        // times that keep every check within its ceiling across restarts. Both are cache.
        new(8, "Card refresh", """
            ALTER TABLE card_import_state ADD COLUMN source_updated_at TEXT;
            CREATE TABLE card_refresh (
                id                   INTEGER PRIMARY KEY CHECK (id = 1),
                flag_json            TEXT,
                flag_read_at         TEXT,
                scryfall_checked_at  TEXT,
                scryfall_file_at     TEXT,
                auto_import_at       TEXT
            );
            """),
    ];

    public static int Latest => All[^1].Version;
}
