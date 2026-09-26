# CLAUDE.md

Working notes for this repository. Constraints here were learned the hard way — each one
cost real debugging time, and none of them are obvious from reading the code.

## Layout

| Project | Contains |
|---|---|
| `MtgaCollectionAdvisor.Core` | All logic: memory scanner, Scryfall import, Archidekt client, wildcard analysis, SQLite storage |
| `MtgaCollectionAdvisor.Web` | Blazor Server UI — the only front-end |
| `MtgaCollectionAdvisor.Core.Tests` | xUnit tests |

`Core` has no reference to `Web`. The UI holds no domain logic.

## Collection capture

**The MTGA client does not write the collection anywhere readable.** Not to `Player.log`,
not to a cache file, not to the registry — verified across a full restart, a login to the
main menu, and opening the in-game Collection screen. Do not spend time looking again.
Reading process memory (`Core/Memory/`) is the only route. Wildcard totals *are* still in
`Player.log` and are read from there.

**The decks saved in Arena *are* in `Player.log`**, unlike the collection. They come in the
`StartHook` login message, which is also the one carrying wildcard totals: `DeckSummaries`
(name, `Format` attribute, `IsNetDeck`) and `DecksInternal` (cards by grpId per section,
including `CommandZone` and `Companions`). About half the list is Wizards' decks: suggested
decks have `IsNetDeck`, and precons have `?=?Loc/...` names. Arena writes the list only at
login, so it is stored (`arena_decks`) rather than re-read. Two traps when turning them into app decks:
Arena names Pioneer **Explorer** (the two are unified; decks may carry either name), and it lists a companion in `Companions` *and* in
`Sideboard`, so read the sideboard only (`ArenaDeckImport`).

**Arena's saved decks call the 100-card Brawl `HistoricBrawl`; their `Brawl` is Standard Brawl**
(60 cards) (#75, #76). Archidekt names them the same old way ("Historic Brawl" is 20, "Brawl" is
13). Legality can't tell a creator's list apart (a 60-card Historic list is all legal in Brawl),
so a list has to fit a format's shape first (`FormatDefinition.FitsShapeOf`). The commander is its own board (`DeckBoard.Commander`):
Copy for Arena must write the `Commander` section, which is the only place Arena's importer
reads a commander from.

**Card legality is `CardInfo.IsLegalIn(format)`, one column per format.** Before Brawl the
calculator picked Standard's column or else Pioneer's, so a new format silently read Pioneer's.
A new format needs a migration for its column and the one-time re-import that fills it
(`NeedsCardDataBackfill`).

**Wildcard totals and saved decks are only in `Player.log` with MTG Arena's "Detailed Logs
(Plugin Support)" on** (Options → Account). The log says which with a plain line near the top
of each session, `DETAILED LOGS: ENABLED` or `DISABLED` (`DetailedLogsLine`). Wildcards never
read are **unknown (`null`), never zero** (#57): zero made every deck look uncraftable.

Two traps in the scanner, both fixed and both easy to reintroduce:

- **Chunked reads must overlap.** A collection table straddling a chunk boundary gets split,
  and only its larger half survives.
- **The client keeps partial views of the collection in memory** (a filtered page, a
  format-restricted pool) that score as well as the real table. The collection is the
  *maximal* such table, so any block essentially contained in a larger one is a view of it.

A real collection has thousands of entries, ~98% known Arena ids, a spread of 1–4 copies,
and cannot average more than 4 copies per card. A block where every quantity is exactly 1
is a UI list, not a collection.

## Blazor

**Interactive components need `@rendermode="InteractiveServer"`** on `<Routes />` and
`<HeadOutlet />` in `App.razor`. Without it the app renders as static HTML: the page looks
perfect, no button responds, and *no error appears anywhere* — not in the browser console,
not in the server log. If nothing is clickable, check this first.

The taskbar icon of the Chromium `--app` window comes from the page's favicon and web app
manifest, not from the executable's embedded icon.

**Closing the window stops the app, 45 s later** (#34). `WindowPresence` counts window
*connections*, not circuits: Blazor keeps a closed window's circuit for about 3 minutes in
case it reconnects, so `OnCircuitClosedAsync` fires far too late. Nothing stops until a
first window has connected, so a `--no-browser` run that nobody opens stays up. A second
launch finds the running instance through `/instance` and opens a window on it. An
instance killed mid-shutdown can still hold port 5199 and lock `bin/` DLLs, so check
`tasklist` before blaming a port conflict or a broken build.

**Test on another port while the user has the app open:** set `MTGA_ADVISOR_PORT` (e.g.
5299) for the test run. On 5199, a copy started for testing is where the user's desktop
shortcut opens its window, and stopping it breaks their session mid-use with nothing
saying why. Both copies share `advisor.db` unless `MTGA_ADVISOR_DB_PATH` points the test
copy at another file, such as a copy of the user's database; without it, a test writes the
user's data.

**A browser-automation tab is hidden, and Edge freezes hidden tabs** (#56). After a minute
idle, scripts and screenshots in it time out ("renderer frozen"), its circuit drops, and 45 s
later the app stops as if its window had closed. That is not an app bug. Keep driving the tab,
or check the outcome server-side (`curl` the page, look at the database).

**The first-run setup (#56) resumes through a marker file**, `advisor.db.setup`, next to the
database. It is created when the setup starts and deleted when it finishes, so a setup closed
midway comes back even though the cards and decks it stored no longer call for it. Delete the
marker to drop an unfinished setup; delete the database (or point `MTGA_ADVISOR_DB_PATH` at a
new file) to see the setup again.

**Verify with `dotnet run`, not by launching the `.exe` in `bin/`.** Run that way, outside
a publish, the app is in Production and serves no `wwwroot`: every page arrives with no CSS,
and no error.

**A `string` component parameter needs `@` to take a field** (#62). `Copied="_copied"` works
because a `bool` parameter's value is read as C#, but `QuoteStatus="_quoteStatus"` passes the
text "_quoteStatus": the button showed the field's name, with no warning. Write
`QuoteStatus="@_quoteStatus"`.

**Windows has two region settings** (#62): the home location ("Country or region",
`GetUserDefaultGeoName`) and the regional format, the only one `RegionInfo.CurrentRegion`
follows. They often differ; `WindowsRegion` reads both.

**Report progress synchronously when a final status follows.** `Progress<T>` posts each
report to run later, so the last "Reading deck 150…" can land after the summary line and
overwrite it in the status bar. `AdvisorSession` has an `ImmediateProgress` for this.

**File downloads are plain `GET` endpoints linked with `<a download>`** (see `/export/*`
in `Program.cs`). Blazor leaves an anchor with a `download` attribute to the browser; the
Chromium `--app` window saves it to Downloads. No JS interop, no blob.

## Testing

Put logic where it can be tested without a UI or a database. Deck-list filtering lives in
`Core/Analysis/DeckFilter.cs` rather than in the Razor component for exactly this reason —
the component just builds a `DeckFilterCriteria` and calls it.

Tests use plain xUnit `Assert`; there is no mocking library, because nothing here needs one.
Tests that genuinely need storage create a throwaway SQLite file and delete it in teardown
with `TestDatabaseFiles.Delete` (see `CardNameSearchTests`): a pooled connection keeps the
file locked on Windows, so its pool is cleared first. **Never `SqliteConnection.ClearAllPools()`**
(#41): xUnit runs test classes in parallel, and it closes the connections other classes are
still using. They fail at random with `ObjectDisposedException: SQLitePCL.sqlite3`, rarely on
an idle machine and often under load. To check a fix for this kind of failure, run a few
`dotnet test` in parallel, several times.

## Database schema

**The schema is versioned: `PRAGMA user_version` is the last migration a database has run**
(#47). `SchemaMigrator` runs `Storage/Migrations.cs` at startup, backs the file up first
(`advisor.db.backup-v{N}`, three kept), commits each migration whole, and refuses a
database newer than the build without touching it. To change the schema, **add a migration
at the end, never edit one**: users' databases have already run it, and a test pins each
migration's hash. Editing `CREATE TABLE IF NOT EXISTS` in place does nothing to an
existing database, which is why this exists.

When a version is released, add `Core.Tests/Fixtures/schema-v{N}.sql` (that version's
schema plus a row of each kind of user data) and list it in the fresh-versus-upgraded test.

**A migration adds columns; it does not fill them.** When the data comes from an import
(Scryfall, Archidekt), an upgraded player gets the new feature empty and nothing says why. #59
shipped image URLs that way until the app learned to re-import once by itself when the column
is empty everywhere (`CardDatabaseStore.NeedsImageBackfill`). Plan that trigger with the
migration.

`decks` and `deck_cards` are **not** cache: they hold the user's own decks (`manual:` ids)
next to fetched ones (`archidekt:`). A migration may rebuild the cache tables (`cards`,
fetched decks' sync state, creator videos) but must carry user rows across.

## Releases

**A release is a pushed `v*` tag**; `.github/workflows/release.yml` tests, publishes, packs
with Velopack and uploads. On a PR that touches the workflow it is a dry run that publishes
nothing. Add the schema fixture for the release (see above).

**The Velopack `packId` must never be `MtgaCollectionAdvisor`.** Velopack installs to
`%LOCALAPPDATA%\<packId>` and deletes that folder on uninstall; that name is the data
folder, so uninstalling would delete the player's collection and decks. It is
`MtgaDeckAdvisor`, and `ReleaseWorkflowTests` holds it there. The same test keeps `vpk` in
the workflow at the `Velopack` package's version: move both together.

**`VelopackApp.Build().Run()` stays the first statement of `Program.cs`**: the installer
runs the exe with hook arguments and expects it to exit at once. `vpk pack` warns that it
"does not look like your application's entry point": the top-level statements compile to an
async `Main`, and the call sits in its state machine. It still runs first; install, update
and uninstall were verified with it there. The published build pins
its content root to the exe's folder, because the updater's restart does not set a working
directory and the page would otherwise arrive with no CSS.

**Never make the Web project `WinExe`.** Blazor's framework files (`blazor.web.js`) are
only added to `Exe` projects (`Microsoft.AspNetCore.App.Internal.Assets.targets`), so a
`WinExe` build loads, looks right, and no button works, with no error anywhere. v0.1.1
shipped like that (#54). The release hides the console instead with
`-p:WindowsAppNoConsole=true`, which sets the SDK's own GUI app-host flag; `dotnet run` and
`publish-local.ps1` keep their console. The flag is cached in `obj`: after a local publish
with it, delete `bin/Release` and `obj/Release` before building without it. The release
workflow smoke-tests the published app (`blazor.web.js` must return 200) before packing.
Nothing the app logs is visible without a console; the log file is the place to look (#52).

**The log file is `logs\advisor-YYYY-MM-DD.log` next to the database** (#52): warnings, errors,
and three startup lines (version, content root, database). A week is kept, and 5 MB per day at
most. A test run with `MTGA_ADVISOR_DB_PATH` writes its own logs beside that file, never into
the player's. Errors the app shows in the status bar or a setup step are logged with their
exception; keep it that way when adding operations, and never log the collection or decks.
Failures the app handles by itself (a creator feed, a stopped Archidekt fetch, creators.json)
never reach `RunAsync`'s catch, so Core returns why and `AdvisorSession` logs it (#74);
`FailureText` turns the exception into "HTTP 429 TooManyRequests", "timed out" and the like.

Installing a release on the dev machine replaces the `MTGA Deck Advisor` desktop shortcut
that `publish-local.ps1` makes; run `publish-local.ps1` again afterwards. Uninstalling removes
only shortcuts that point into the install folder, so a restored one survives it. To try the
update loop locally, pack under another `packId`, and point `MTGA_ADVISOR_UPDATE_SOURCE` at the
local `Releases` folder.

**To test the real update loop, install the previous release next to a test database** (verified
for v0.1.2 → v0.2.0): run its `Setup.exe --silent` with `MTGA_ADVISOR_DB_PATH` and
`MTGA_ADVISOR_PORT` set, and start `%LOCALAPPDATA%\MtgaDeckAdvisor\current\*.exe` the same way.
The app inherits both, across Velopack's restart too. Never point an older release at the real
database: it refuses a newer schema. Uninstall with `Update.exe --uninstall --silent`.

## External data

**Escape `LIKE` wildcards when the search term comes from the user.** An unescaped `%`
turns a prefix search into a full-table match.

**But `ESCAPE` switches off SQLite's `LIKE` index optimisation**, so an escaped `LIKE` scans
the whole table: results stay correct, only ~200x slower, with no error. For a prefix
match on an indexed column, write a range instead (`name >= $p AND name < $p || U+10FFFF`,
see `CardDatabaseStore`): it uses the index and has no wildcards to escape. Check any new
lookup with `EXPLAIN QUERY PLAN`. `SCAN` means it will not scale.

**Deck sources send explicit nulls where a list is expected.** `System.Text.Json` writes
those over property initializers, so `= []` on a DTO property does not protect you —
coalesce at the point of use. Archidekt does this for `categories` on untagged cards.

**AetherHub and Moxfield (and MTGGoldfish) refuse automated reads** behind Cloudflare. Do not
try to get past it: open their links for the user and let them paste the export instead.
Archidekt's API is the readable deck source.

**Archidekt's search ignores `pageSize`** (always 60 per page) and stops at 1000 results.
`orderBy=-viewCount` is all-time: its top pages are years-old, rotated decks and never
change. The fetch walks `orderBy=-updatedAt` instead (#36). Standard gets roughly 150
updated decks a *day*, so a walk reaches only a few days back, whatever window the code
sets. Keep to `ArchidektSyncPlanner`'s limits (a deck read every 300 ms, 150 per fetch, 5 min
between fetches of a format); they are what keeps the app polite. A failed read, a 429
included, stops the fetch and keeps what was read.

**YouTube's public channel feeds fail at random (404/500), and throttle a machine that asks
too often** — during #32, bulk probing got every feed refused for hours, for the app too.
Treat a failed feed as "no news", never "no videos", and keep to `CreatorFeedSchedule`. Do
not bulk-probe feeds while testing.

**The creators list is `creators.json` at the repository root** (#64), read by every copy from
GitHub's raw URL at most once a day, with the last good copy stored (`creator_roster`) and
`CreatorChannels.All` as the fallback. Add or remove a creator by editing that file, no release
needed; `CreatorRosterTests` fails CI when an entry would be dropped. The compiled list only
catches up at a release. While the repository is private the raw URL returns 404 and every copy
uses the compiled list. Only the maintainer curates it: no UI adds channels.

**Card images come from Scryfall's image CDN, by URLs stored at import** (#59). The bulk
file already carries `image_uris`; a double-faced card has none at the top level and one per
face instead. `*.scryfall.io` has no rate limit, while `api.scryfall.com` does (10/s), so never
build image URLs through the API per card. Scryfall's rules: show the whole card, scaled
proportionally, never cropped, filtered or covered (the artist and copyright lines stay).

**Land kinds come from the front face's type line, by whole-word supertype** (#61). Basic
means the "Basic" supertype on a Land: matching the text "Basic Land" missed "Basic Snow Land",
and snow-covered basics were priced as commons. A non-basic land is a Land without it; spells
with a land on their back are not lands for this. `ScryfallCard.IsBasicLandType` and
`IsNonBasicLandType` are the only place this is decided.

Deck sites let anyone file any list under any format, so fetched decks must be checked for
format legality rather than trusted. Scryfall's bulk data lists a few `arena_id` values more
than once, so dedupe before inserting against a primary key.
