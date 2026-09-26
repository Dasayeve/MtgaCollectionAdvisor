-- A database as release v0.2.0 leaves it: schema version 5 (Migrations 1 to 5), with the
-- same rows as schema-v0.sql and schema-v2.sql, so all fixtures can be checked by the same
-- assertions, plus the columns and table the later migrations added.

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
    updated_at       TEXT NOT NULL,
    image_url        TEXT,
    back_image_url   TEXT,
    is_nonbasic_land INTEGER
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


-- The last good creators.json read from the repository (#64).
CREATE TABLE creator_roster (
    id               INTEGER PRIMARY KEY CHECK (id = 1),
    json             TEXT,
    fetched_at       TEXT,
    last_attempt_at  TEXT
);

INSERT INTO collection_cards VALUES (90001, 4, '2026-09-20T12:00:00Z');
INSERT INTO collection_cards VALUES (90002, 2, '2026-09-20T12:00:00Z');
INSERT INTO collection_cards VALUES (90003, 1, '2026-09-20T12:00:00Z');

INSERT INTO wildcard_inventory VALUES (1, 31, 22, 7, 3, '2026-09-20T12:00:00Z');

INSERT INTO cards VALUES (90001, 'Lightning Strike', 'FDN', '{1}{R}', 'R', 'common', 1, 1, '2026-09-19T00:00:00Z', 'https://cards.scryfall.io/normal/front/a/b/ab.jpg', NULL, 0);
INSERT INTO cards VALUES (90002, 'Sheoldred, the Apocalypse', 'DMU', '{2}{B}{B}', 'B', 'mythic', 0, 1, '2026-09-19T00:00:00Z', 'https://cards.scryfall.io/normal/front/c/d/cd.jpg', NULL, 0);
INSERT INTO card_import_state VALUES (1, '2026-09-19T00:00:00Z');

INSERT INTO decks VALUES ('archidekt:123456', 'standard', 'Mono Red Aggro', 'https://archidekt.com/decks/123456', 870, '2026-09-21T08:00:00Z');
INSERT INTO deck_cards VALUES ('archidekt:123456', 'Lightning Strike', 'main', 4);
INSERT INTO decks VALUES ('manual:my-rakdos', 'pioneer', 'My Rakdos Midrange', '', 0, '2026-09-22T19:30:00Z');
INSERT INTO deck_cards VALUES ('manual:my-rakdos', 'Sheoldred, the Apocalypse', 'main', 3);
INSERT INTO deck_cards VALUES ('manual:my-rakdos', 'Lightning Strike', 'sideboard', 2);

INSERT INTO pinned_decks VALUES ('archidekt:123456', 'standard', '2026-09-21T09:00:00Z', 5);

INSERT INTO source_deck_versions VALUES ('archidekt:123456', 'standard', '2026-09-20T22:00:00Z', 1);
INSERT INTO deck_sync_state VALUES ('standard', '2026-09-21T08:00:00Z', NULL);

INSERT INTO arena_decks VALUES ('a1b2c3', 'Izzet Prowess', 'Standard', 0, '{"MainDeck":[{"cardId":90001,"quantity":4}]}', '2026-09-23T18:00:00Z');
INSERT INTO arena_decks VALUES ('d4e5f6', '?=?Loc/Decks/Precon/Red', 'Explorer', 1, '{"MainDeck":[]}', '2026-09-23T18:00:00Z');

INSERT INTO creator_videos VALUES ('vid00000001', 'Some Creator', 'Best deck this week', '2026-09-18T15:00:00Z', 'archidekt', NULL, 123456, NULL, NULL, 'pt');
INSERT INTO creator_feeds VALUES ('Some Creator', '2026-09-23T10:00:00Z', '2026-09-23T10:00:00Z', 0);
INSERT INTO creator_roster VALUES (1, '{ "channels": [ { "name": "Some Creator", "channelId": "UCKivtYJCyZn-uaTr5uAozAg", "language": "pt" } ] }', '2026-09-25T20:00:00Z', '2026-09-25T20:00:00Z');

PRAGMA user_version = 5;
