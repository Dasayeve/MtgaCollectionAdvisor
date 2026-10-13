# Releasing

A new version exists only when a tag named `v<version>` is pushed. Pushes and merges to
`master` run CI (build and tests) and nothing else; they never publish anything.

## Versions

Versions follow [SemVer](https://semver.org): `MAJOR.MINOR.PATCH`.

- **Patch** (`0.1.0` → `0.1.1`): fixes only, such as the memory scanner after an MTG Arena update.
- **Minor** (`0.1.1` → `0.2.0`): new features.
- While the version starts with `0.`, anything may still change. `1.0.0` is the point where
  the app is considered stable.

A published version number is never reused. If something goes wrong, fix it and release the
next patch.

## Before tagging

1. Everything for the release is merged into `master`, and CI on `master` is green.
2. **If this version changes the database schema** (a new migration in
   `Storage/Migrations.cs`), `Core.Tests/Fixtures/schema-v{N}.sql` exists for the schema it
   ships and is listed in the schema tests. See "Database schema" in `CLAUDE.md`.
3. The app has been used on this commit (`publish-local.ps1`) and the change works in the
   real window.
4. **Smoke test what will ship.** CI's own check only opens the window and closes it.
   - **Publish:** publish `master` the way `release.yml` does (`src/MtgaCollectionAdvisor.Desktop`,
     self-contained single file, `-p:Version=X.Y.Z`).
   - **Run on an upgrade:** start `MtgaDeckAdvisor.exe` with `MTGA_ADVISOR_DB_PATH` pointing at a
     copy of a database from the previous release (an `advisor.db.backup-v{N}` works), in a folder
     of its own. Check that it migrates and logs the new version.
   - **Use it:** drive what the release changed, plus Decks, My decks, a deck, Creators and the
     exports. To drive it from a script, start it with
     `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222` (see `CLAUDE.md`, Testing).
   - **Close it:** close the window and check that the log records the stop.

## Tagging

From an up-to-date `master`:

```bash
git switch master
git pull
git tag -a v0.2.0 -m "MTGA Deck Advisor 0.2.0"
git push origin v0.2.0
```

## What happens next

The **Release** workflow (`.github/workflows/release.yml`) runs on the tag, in about three minutes:

1. It runs the tests. If any fails, nothing is published.
2. It publishes the desktop app (`MtgaCollectionAdvisor.Desktop`), self-contained win-x64,
   stamped with the tag's version, and smoke-tests it: the window must draw and close.
3. It packs it with Velopack (`MtgaDeckAdvisor-win-Setup.exe`, a portable zip, and full and
   delta update packages). The installer adds WebView2 on a machine without it.
4. It creates the GitHub Release "MTGA Deck Advisor 0.2.0" with those files, `SHA256SUMS.txt`,
   and notes generated from the merged pull requests.

Follow it under **Actions → Release**, then check the release page: the files are attached
and the notes end with the SHA-256 block.

Installed copies find the new version the next time they start, download it in the
background, and apply it on **Restart to update** or when the app closes. That works only
while the repository is public, because a private repository's releases need a login to
download.

## When a release fails

- **The workflow failed before "Upload release":** nothing was published. Fix the cause on
  `master`, then tag the **next** patch version. The failed tag can be deleted
  (`git push origin :v0.2.0`, then `git tag -d v0.2.0`). The "Protect release tags" ruleset
  refuses that to everyone but an admin, who bypasses it.
- **The release is published but broken:** do not delete it, because installed copies may
  already have updated to it. Fix on `master` and release the next patch, which they will
  update to.

## Testing a change to the release workflow

A pull request that changes `release.yml` runs the workflow as a **dry run**. It builds and
packs a `0.0.<run>-dryrun` version, keeps the result as a workflow artifact
(`release-dry-run`), and publishes nothing.

## Testing install and update locally

- **Without publishing:** pack under another `packId` and point `MTGA_ADVISOR_UPDATE_SOURCE` at
  the local `Releases` folder.
- **The real update loop:** install the previous release next to a test database. Run its
  `Setup.exe --silent` with `MTGA_ADVISOR_DB_PATH` set (and `MTGA_ADVISOR_PORT`, for a release
  before the desktop window), then start `%LOCALAPPDATA%\MtgaDeckAdvisor\current\*.exe` the same
  way. The app keeps them across Velopack's restart. Uninstall with `Update.exe --uninstall --silent`.
- **Never point an older release at the real database:** it refuses a newer schema.
- **The desktop shortcut:** installing a release replaces the `MTGA Deck Advisor` shortcut that
  `publish-local.ps1` makes. Run `publish-local.ps1` again afterwards. Uninstalling removes only
  shortcuts into the install folder.
