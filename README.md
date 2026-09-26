# MTGA Deck Advisor

*Find the decks your collection can build — and what the rest will cost.*

MTGA Deck Advisor reads your MTG Arena collection and ranks recent public decks by **how few
wildcards you need to finish them**. You see what you can craft today, what each deck costs
by rarity, and how close you are getting. Standard, Pioneer, Brawl and Standard Brawl, Windows only.

## Install

1. Download `MtgaDeckAdvisor-win-Setup.exe` from the
   [latest release](https://github.com/Dasayeve/MtgaCollectionAdvisor/releases/latest) and run it.
   It installs for your user (no administrator rights, no .NET needed) and adds a desktop and
   Start menu shortcut.
2. **Windows SmartScreen will warn about it**, because the installer isn't code-signed yet.
   Choose **More info → Run anyway**.
3. In MTG Arena, turn on **Options → Account → Detailed Logs (Plugin Support)**, then restart
   the game. Your wildcard totals and the decks saved in Arena come from that log.

To check that the file is the one published here, compare its hash with `SHA256SUMS.txt` in
the same release:

```powershell
Get-FileHash .\MtgaDeckAdvisor-win-Setup.exe -Algorithm SHA256
```

A portable zip is attached to each release too, if you'd rather not install.

## First start

The first start sets everything up by itself, in a couple of minutes. It downloads the card
database and recent Standard decks, then asks you to open MTG Arena and reads your collection
from the running game. You can leave it running; if you close it midway, it carries on from
where it stopped next time.

## What you can do

- **Rank decks by what they cost you.** Each deck shows the wildcards it still needs by
  rarity and how much of it you already own. Filter by format, colours, a wildcard budget
  per rarity, how much you own, cards a deck must or must not include, or only what you
  can craft right now.
- **Keep decks you're working towards.** Pin a deck to track how many wildcards you still
  need since you pinned it.
- **Bring in your own decks.** Paste a list in Arena's export format, or pick from the decks
  saved in your MTG Arena account. They are ranked against your collection like any other.
- **Copy a deck to MTG Arena.** Export any deck to the clipboard in Arena's import format.
- **Watch creators' latest decks.** The Creators tab lists recent videos from MTG Arena
  creators and prices the deck in each one against your collection. The list of creators is
  [`creators.json`](creators.json) and reaches the app without a new version.
- **Take your data with you.** The Export menu saves your collection (a list or JSON with
  wildcards), your own decks, and the decks saved in Arena.

Your collection is read again every time MTG Arena starts. **Fetch decks** adds the newest
public decks, and **Update cards** refreshes the card database after a new set.

## Updates and your data

The app checks for a new version when it starts and downloads it in the background. Click
**Restart to update** in the status bar, or just close the app and it will be on the new
version next time.

Your data lives in `%LOCALAPPDATA%\MtgaCollectionAdvisor\advisor.db` and is kept across
updates and uninstalls. Delete that folder as well to remove everything. The database is
backed up next to itself before an update changes its format.

## Troubleshooting

- **Wildcards show 0 0 0 0, or "Decks saved in Arena" is empty.** Turn on **Detailed Logs
  (Plugin Support)** in MTG Arena (Options → Account), restart the game and log in. The
  collection itself doesn't need it; wildcards and saved decks do.
- **The collection is not found.** Open MTG Arena, log in and wait at the main menu, then
  click **Capture collection**. The first capture takes a minute or two.
- **Windows or an antivirus blocks the app.** The installer isn't signed yet, and the app
  reads another program's memory (see below), which antivirus software looks at closely.
  The code is all here to check, and each release lists the SHA-256 of its files.

## Reporting a problem

Open an issue and attach the app's latest log file. Click **Logs** at the bottom of the app to
open the folder (`%LOCALAPPDATA%\MtgaCollectionAdvisor\logs`); each day has its own file, and a
week is kept. The log holds warnings, errors and the app's version. It contains file paths,
which include your Windows user name, but not your collection or your decks.

## How the collection is read

The app reads the memory of the running `MTGA.exe` process. The current Arena client doesn't
write your collection anywhere else: not to `Player.log` (where older trackers used to find
it), not to a cache file, not to the registry. Wildcard totals and saved decks are still in
`Player.log`, and are read from there.

The read is one-way. The process is opened with `PROCESS_VM_READ`; nothing is written back,
nothing is injected, and no game file is touched. Administrator rights are not needed.

An MTG Arena update can break the read. If it happens, please open an issue; importing a
collection from a file or a pasted list is planned
([#40](https://github.com/Dasayeve/MtgaCollectionAdvisor/issues/40)).

## Limitations

- Windows only.
- Only Standard, Pioneer, Brawl (100 cards) and Standard Brawl (60 cards).
- Many Brawl decks on Archidekt are built for paper and include cards that are not on Arena;
  those are left out, so a Brawl fetch keeps far fewer decks than it reads.
- Archidekt lets anyone file any list under any format, so decks that aren't actually legal
  get filtered out. Expect a chunk of each fetch to disappear.

## Building from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/MtgaCollectionAdvisor.Web
dotnet test
```

It opens in a browser window and uses the same `advisor.db`; there is no database server.
Set `MTGA_ADVISOR_DB_PATH` to use another database file, and `MTGA_ADVISOR_PORT` to run next
to an installed copy (default port 5199).

Releases are built by `.github/workflows/release.yml` when a `v*` tag is pushed: it runs the
tests, publishes a self-contained build, smoke-tests it and packs it with
[Velopack](https://velopack.io). [RELEASING.md](RELEASING.md) has the steps.

## Contributing

Issues and pull requests are welcome, feature requests included. If something is missing or
broken, open an issue.

## Credits

The memory-scanning approach comes from
[MTGA-collection-exporter](https://github.com/NthPhantom10/MTGA-collection-exporter) by
**NthPhantom10**, who worked out how to find the collection in the client's memory. This
project is a C# port of that idea — thank you.

Card data from [Scryfall](https://scryfall.com/docs/api) · decklists from
[Archidekt](https://archidekt.com) · mana symbols from
[mana-font](https://github.com/andrewgioia/mana-font).

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for full licences.

## Licence

[MIT](LICENSE). Unofficial fan project, not affiliated with Wizards of the Coast.
