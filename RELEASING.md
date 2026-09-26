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
2. It publishes a self-contained win-x64 build stamped with the tag's version.
3. It packs it with Velopack (`MtgaDeckAdvisor-win-Setup.exe`, a portable zip, and full and
   delta update packages).
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

To try installing and updating locally without publishing, see "Releases" in `CLAUDE.md`.
Don't install a real release on the development machine: it replaces the `MTGA Deck Advisor`
desktop shortcut that `publish-local.ps1` makes.
