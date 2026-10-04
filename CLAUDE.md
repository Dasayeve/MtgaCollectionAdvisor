# CLAUDE.md

Constraints that aren't obvious from the code. Most of them fail silently.

## Layout

| Project | Contains |
|---|---|
| `MtgaCollectionAdvisor.Core` | All logic: memory scanner, Scryfall import, Archidekt client, wildcard analysis, SQLite storage |
| `MtgaCollectionAdvisor.Web` | Blazor Server UI, the only front-end |
| `MtgaCollectionAdvisor.Core.Tests` | xUnit tests |

`Core` has no reference to `Web`. The UI holds no domain logic.

## MTG Arena data

**The collection is only in process memory** (`Core/Memory/`). The client writes it nowhere
readable: not `Player.log`, not a cache file, not the registry. That was checked thoroughly; don't
look again.

**Wildcard totals and saved decks are in `Player.log`, only with "Detailed Logs (Plugin Support)" on**
(Options → Account). Each session logs `DETAILED LOGS: ENABLED` or `DISABLED` near the top
(`DetailedLogsLine`). Wildcards never read are **unknown (`null`), never zero** (#57): zero made every
deck look uncraftable.

**Saved decks come only in the login message** (`StartHook`), so they are stored (`arena_decks`),
not re-read. Turning them into app decks (`ArenaDeckImport`) has three traps:
- Arena calls Pioneer **Explorer**.
- A companion is listed in `Companions` *and* `Sideboard`: read the sideboard only.
- Arena's `HistoricBrawl` is the 100-card Brawl; its `Brawl` is Standard Brawl (60 cards). Archidekt
  names them the same way.

Legality can't tell the Brawls apart, so a list must fit a format's shape first
(`FormatDefinition.FitsShapeOf`). The commander is its own board (`DeckBoard.Commander`), and Copy for
Arena must write a `Commander` section: Arena's importer reads a commander from nowhere else.

Two scanner traps, both easy to reintroduce:
- **Chunked reads must overlap**, or a table straddling a boundary is split and only its larger half
  survives.
- **The client keeps partial views of the collection** (a filtered page, a format pool) that score as
  well as the real table. The collection is the *maximal* table: a block contained in a larger one is
  a view. A real collection has thousands of entries, ~98% known ids, 1–4 copies (never averaging
  over 4). A block of all 1s is a UI list.

**On macOS, memory is read through Mach** (`ProcessMemoryReader`). `task_for_pid` needs the
`com.apple.security.cs.debugger` entitlement (an ad-hoc signature is enough; the Web csproj re-signs
after Build and Publish) *and* the user in the `_developer` group. Mach error 5 with a signed app is
usually the group. A Mach read fails whole on one unreadable page, so `ReadPartial` retries page by page.

**MTG Arena's card database lends ids Scryfall doesn't have yet** (#101):
`MTGA_Data\Downloads\Raw\Raw_CardDatabase_<hash>.mtga`, plain SQLite, no legality or images.
- **Matching.** `CardSourceMerge` matches an id-less Scryfall print by set, collector number *and*
  name. A card Scryfall lacks gets, per format, the legality most of its set's id-less Scryfall prints
  have. Old cards under codes Scryfall never gave Arena ids (TMP, PZA) must not come out
  Standard-legal.
- **Reading the file.** Open it `Mode=ReadOnly;Pooling=False`, so no handle blocks Arena's update.
  Names carry markup (`<nobr>`, `///`, an Alchemy sprite that becomes `A-`; `ArenaCardText`).
  Alchemy cards are not flagged `IsRebalanced`. A failed read never fails an import.
- **Re-imports.** A new file hash with unknown ids triggers one import. Scryfall publishing the ids
  later triggers nothing: set `refreshCardsAfter` then.

## Card data

**Legality is `CardInfo.IsLegalIn(format)`, one column per format.** A new format needs a migration for
its column and the re-import that fills it.

**Mana value is `cards.mana_value` (Scryfall's `cmc`), never parsed from `mana_cost`** (#110). The
stored cost of a split card and of an adventure is `A // B` either way, but one sums its halves and the
other counts the main card. Arena-only cards use `ArenaCardText.ManaValue`; Arena's cost already holds
an adventure's main card alone.

**Land kinds come from the front face's type line, by whole-word supertype** (#61). "Basic Land" as
text missed "Basic Snow Land". A spell with a land on its back is not a land.
`ScryfallCard.IsBasicLandType`/`IsNonBasicLandType` are the only place this is decided.

**Scryfall's bulk data repeats a few `arena_id`s**: dedupe before inserting.

**Fetched decks must be checked for legality**: deck sites let anyone file any list under any format.

**Which sets sell packs on Arena is a fixed list** (`PackSets`, #84), from Wizards'
[drop-rates page](https://magic.wizards.com/en/mtgarena/drop-rates). Add a new set's code when it
reaches Arena. Scryfall can't say: `booster` is empty for a new set, and `set_type` matches Jumpstart
and other sets with no packs.

**Card images come from Scryfall's CDN, by URLs stored at import** (#59). `*.scryfall.io` has no rate
limit; `api.scryfall.com` does (10/s), so never build image URLs through the API. A double-faced card
has `image_uris` per face, none at the top. Scryfall's rules: the whole card, scaled proportionally,
never cropped, filtered or covered. **One deliberate exception** (#111, the maintainer's call): the
visual deck view stacks cards like MTG Arena, each whole on hover, with the count on the art. Don't
spread it: no dimming or filters anywhere, nothing drawn on a card elsewhere.

## Network and external sources

**Every network call gets a ceiling per install before it gets code** (#89). Know how often it runs in
the worst case (restarts, retries), hold it across restarts by storing the times, and treat a failure
as "no news", never with a retry loop. A rate-limited app looks broken and says nothing. The schedule
lives in Core with a test (`CardRefreshSchedule`, `CreatorFeedSchedule`); a large download sits behind
a small check.

**Archidekt is the readable deck source.** AetherHub, Moxfield and MTGGoldfish refuse automated reads
(Cloudflare): don't get past it, open their links and let the player paste the export. Archidekt's
search ignores `pageSize` (60 per page), stops at 1000 results, and `-viewCount` is all-time and
stale, so the fetch walks `-updatedAt` (#36). Keep to `ArchidektSyncPlanner`'s limits. A failed read,
429 included, stops the fetch and keeps what was read.

**Deck sources send explicit nulls where a list is expected.** `System.Text.Json` writes them over
`= []` initializers, so coalesce at the point of use.

**YouTube feeds fail at random (404/500) and throttle a machine that asks too often.** A failed feed is
"no news", never "no videos". Keep to `CreatorFeedSchedule`, and don't bulk-probe feeds while testing:
it got every feed refused for hours.

**Files at the repository root that every installed copy reads from GitHub, no release needed:**
- `creators.json` (#64): the curated creator roster. At most daily, last good copy stored,
  `CreatorChannels.All` as fallback. `CreatorRosterTests` fails CI if an entry would be dropped.
  Only the maintainer curates it: no UI adds channels.
- `notices.json` (#96): notices to players. At most every 6 h (`NoticeSchedule`). Fields: `id`,
  `title`, `text`, `showFrom`, and optionally `requiresSet` and `showUntil`. A notice lasts 30 days, or
  until a newer one starts. **Never reuse an `id`**: a dismissed one never shows again.
  `NoticeFileTests` fails CI on a bad entry. Test with `MTGA_ADVISOR_NOTICES_URL`.
- `card-data.json` (#89): set `refreshCardsAfter` (ISO 8601 UTC) when a new set reaches Arena. Each
  copy re-imports once Scryfall has a file past that time. Scryfall's `updated_at` changes twice a day
  (prices), so it says nothing about new cards. `card_import_state.source_updated_at` prevents a
  second import. Test with `MTGA_ADVISOR_CARD_DATA_URL`.

## SQL

**Escape `LIKE` wildcards from user input, but `ESCAPE` disables SQLite's `LIKE` index**, about 200x
slower with no error. For a prefix match on an indexed column, use a range
(`name >= $p AND name < $p || U+10FFFF`, see `CardDatabaseStore`). Check new lookups with
`EXPLAIN QUERY PLAN`: `SCAN` won't scale.

## Database schema

**`PRAGMA user_version` is the last migration run** (#47). `SchemaMigrator` runs `Storage/Migrations.cs`
at startup. It backs up first (`advisor.db.backup-v{N}`, three kept), commits each migration whole, and
refuses a newer database. **Add a migration at the end, never edit one**: a test pins each hash.

**A migration adds columns; it doesn't fill them.** Data from an import needs the one-time re-import
trigger (`CardDatabaseStore.NeedsCardDataBackfill`), planned with the migration. Otherwise an upgraded
player gets the feature empty, with no explanation.

**`decks` and `deck_cards` are not cache**: they hold the player's own decks (`manual:`) next to fetched
ones (`archidekt:`). A migration may rebuild cache tables but must carry user rows across.

Each release that changes the schema adds `Core.Tests/Fixtures/schema-v{N}.sql`, listed in both schema
tests (see `RELEASING.md`).

## Blazor

**Interactive components need `@rendermode="InteractiveServer"`** on `<Routes />` and `<HeadOutlet />` in
`App.razor`. Without it the page looks perfect and nothing is clickable, with no error anywhere. Check
this first.

**Never make the Web project `WinExe`**: Blazor's `blazor.web.js` is only added to `Exe` projects, so
nothing is clickable, with no error (v0.1.1, #54). The release hides the console with
`-p:WindowsAppNoConsole=true`. That flag is cached in `obj`: after a publish with it, delete
`bin/Release` and `obj/Release`.

**Verify with `dotnet run`, not the `.exe` in `bin/`**: that runs as Production, serves no `wwwroot`,
and every page has no CSS.

**Razor prints code as text, with no warning:**
- A `string` parameter needs `@` to take a field: `QuoteStatus="@_quoteStatus"`, not
  `QuoteStatus="_quoteStatus"` (#62).
- `Deck@SortArrow(x)` after a word reads as an e-mail address: write `Deck@(SortArrow(x))` (#85).
- Attribute text is escaped (`&#10;` shows literally): put the string in a C# field.

**Don't raise `AdvisorSession.Changed` for view state** (#85): `Decks.razor` resets to page 1 on every
`Changed`. Shared view state gets its own event (`OpenDeckChanged`) or none (`DeckView`).

**Colours are tokens in `app.css`, defined for both themes** (#85). Colour carries information only
(rarity, mana, owned/missing, good/bad, pinned, commander), and every text colour passes WCAG AA in
both themes. A new colour is a token with a dark value and a light value, the light one written in
**both** light blocks (Windows setting and player's pick); never a hex in a rule.

**A popover inside a dialog is clipped** (#84): `.modal-panel` scrolls. Previews there are
`position: fixed`, placed by the script in `App.razor`; a new one needs the same.

**Report progress synchronously when a final status follows**: `Progress<T>` posts later, so the last
report can overwrite the summary. Use `AdvisorSession.ImmediateProgress`.

**File downloads are plain `GET` endpoints with `<a download>`** (`/export/*` in `Program.cs`). No JS
interop.

## App lifetime

**Closing the window stops the app 45 s later; a lost connection doesn't** (#34, #99). Browsers freeze
hidden pages (a window behind Arena in full screen counts), which drops the connection. So pages
report `visible|hidden|closed` by beacon (`POST /window/{id}/...`, in `App.razor`), and
`WindowPresence` decides:
- all windows `closed`: stop after 45 s;
- quiet after `hidden`: asleep, wait 12 h;
- quiet while visible, or never reported: stop after 30 min.

It counts *connections*, not circuits (`OnCircuitClosedAsync` means nothing here). Nothing stops before
a first window connects. Every stop is logged with its reason.

**`VelopackApp.Build().Run()` stays the first statement of `Program.cs`**: the installer runs the exe
with hook arguments and expects it to exit at once. `vpk pack`'s "entry point" warning is expected.
The published build pins its content root to the exe's folder, because the updater's restart sets no
working directory.

**The Velopack `packId` is `MtgaDeckAdvisor`, never `MtgaCollectionAdvisor`**: uninstall deletes
`%LOCALAPPDATA%\<packId>`, which would be the player's data folder. `ReleaseWorkflowTests` holds it, and
keeps `vpk` at the `Velopack` package's version: move both together.

**The log is `logs\advisor-YYYY-MM-DD.log` next to the database** (#52), the only place to look in the
console-less release. Errors shown to the player are logged with their exception; never log the
collection or decks. Failures the app absorbs (a feed, a stopped fetch) never reach `RunAsync`'s catch,
so Core returns why and `AdvisorSession` logs it (#74, `FailureText`).

## Testing

**Logic goes where it can be tested without a UI or database**: e.g. `DeckFilter`, `DeckListOrder`. The
component builds the input and calls it.

Tests use plain xUnit `Assert`, no mocking library. Storage tests use a throwaway SQLite file, deleted
with `TestDatabaseFiles.Delete`. **Never `SqliteConnection.ClearAllPools()`** (#41): test classes run in
parallel, and it breaks theirs at random (`ObjectDisposedException`). To check a fix, run several
`dotnet test` in parallel, a few times.

**Test the running app on another port, with a copy of the database:** `MTGA_ADVISOR_PORT=5299` and
`MTGA_ADVISOR_DB_PATH`. On 5199 a test copy hijacks the player's desktop shortcut, and without the DB
path it writes their data. An instance killed mid-shutdown can hold 5199 and lock `bin/`: check
`tasklist` before blaming the build.

**A browser-automation tab is hidden, and the browser freezes it** after about a minute idle
("renderer frozen"). That isn't an app bug. Keep driving it, or check server-side (`curl`, the
database).

**The first-run setup resumes through `advisor.db.setup`** next to the database. Delete it to drop an
unfinished setup; use a new `MTGA_ADVISOR_DB_PATH` to see the setup again.

## GitHub

**Never put a Claude session link (`claude.ai/code/session_...`) on GitHub** (commits, PRs, comments,
issues, release notes): only the maintainer can open it. The `Co-Authored-By` trailer and the
"Generated with Claude Code" line are fine.

Releases: see `RELEASING.md`. macOS is packed (`release-macos`, unsigned artifact) but not published
until there is a Developer ID and notarization.
