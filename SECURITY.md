# Security policy

## Reporting a vulnerability

Please report security problems privately, not in a public issue: use
**[Report a vulnerability](https://github.com/Dasayeve/MtgaCollectionAdvisor/security/advisories/new)**
on this repository's Security tab. Include what you found, how to reproduce it, and which version
you ran (shown at the bottom of the app).

This is a one-person project, maintained in spare time. You'll get an answer as soon as possible,
and a fix is released as a new version, which installed copies download by themselves.

## Supported versions

Only the latest release. Installed copies update themselves, so a fix reaches players without them
doing anything.

## What the app does, for anyone assessing it

- It reads the memory of the running `MTGA.exe` with `PROCESS_VM_READ` only. It never writes to
  the game's memory, injects code, or touches game files, and it needs no administrator rights.
- Its interface is a local web server on `localhost` (port 5199), opened in a browser window. It
  does not listen on other network interfaces.
- It connects to Scryfall (card data), Archidekt (decks), YouTube's public channel feeds, and
  GitHub (updates and the creators list). It sends nothing about the player to any of them.
- The player's data stays in `%LOCALAPPDATA%\MtgaCollectionAdvisor`.
