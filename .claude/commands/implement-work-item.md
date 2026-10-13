---
description: Execute the implementation guide on a GitHub issue - code, tests, smoke tests - then post a QA report
argument-hint: <issue-number>
---

# Implement Work Item

Execute the plan already captured in the `<!-- IMPLEMENTATION-GUIDE -->` comment on a GitHub
issue, then post a QA report. You are not designing here — the design exists.

**Issue to implement:** `$ARGUMENTS`

---

## Phase 0 — Load the guide

Resolve `ISSUE_NUMBER` and `REPO_SLUG` from `$ARGUMENTS` (bare number or URL; slug from
`git remote get-url origin`). Then:

```bash
gh issue view ISSUE_NUMBER --repo REPO_SLUG --json number,title,body,labels,comments
```

Find the comment starting with `<!-- IMPLEMENTATION-GUIDE -->`. If there is none, stop and
say to run `/refine-work-item ISSUE_NUMBER` first.

Parse its sections: Refined Scope, Assumptions, Affected Layers, Files to Create / Modify,
Data Contracts, Validation Rules, Business Rules, Implementation Order, Test Cases,
AC → Task Mapping, Prerequisites, Open Questions.

If Prerequisites names an open issue with no `<!-- QA-REPORT -->` comment, warn and wait for
confirmation before continuing.

---

## Phase 0.5 — Branch

Derive `feature/ISSUE_NUMBER-<first four words of the title, lowercased and hyphenated>`.

Create it, or switch to it if it already exists. Never implement on `master`. Confirm with
`git branch --show-current` and tell the user which branch you are on.

---

## Phase 1 — Prepare

Create a task list, one task per step in Implementation Order, and keep it current.

Read `CLAUDE.md` and follow it.

Verify a clean baseline before writing anything:

```bash
dotnet build MtgaCollectionAdvisor.slnx --no-incremental -v minimal
dotnet test src/MtgaCollectionAdvisor.Core.Tests/MtgaCollectionAdvisor.Core.Tests.csproj
```

If the build fails because a running instance locks a DLL, close that copy (by its PID, never
by image name: that would also close the user's window) and retry — that is not a real break.
If the baseline is genuinely broken, stop and report it.

---

## Phase 2 — Implement

Follow Implementation Order exactly, one step at a time.

- Use the exact paths from Files to Create / Modify.
- Read a file before modifying it; change the minimum necessary.
- Implement fully. No `// TODO` stubs.
- Follow the Data Contracts as written. If one is ambiguous, resolve it via `CLAUDE.md` and
  record the decision for the QA report.

Do **not** add packages the guide did not ask for. This repository deliberately has a small
dependency set: `Microsoft.Data.Sqlite` in `Core`, xUnit in the test project. There is no
mocking library and no assertion library — tests use plain `Assert`, matching the existing
test files.

Build after each layer:

```bash
dotnet build MtgaCollectionAdvisor.slnx -v minimal
```

Three failed fix attempts on the same error → stop and ask.

---

## Phase 3 — Tests

Write every test in the guide's Test Cases table into the file(s) listed.

- xUnit, plain `Assert`, no mocking library.
- Prefer pure tests over tests that touch storage. If storage is unavoidable, create a
  throwaway SQLite file and delete it in teardown — call
  `SqliteConnection.ClearAllPools()` first or Windows keeps the file locked.
- No test may depend on MTG Arena running, on the network, or on the user's real database.

```bash
dotnet test src/MtgaCollectionAdvisor.Core.Tests/MtgaCollectionAdvisor.Core.Tests.csproj
```

Three failed attempts on one test → leave it failing, mark it `❌ COULD NOT AUTO-FIX` in the
QA report, and say what you tried. Never delete or skip a failing test.

---

## Phase 4 — Verify the real app

```bash
dotnet build MtgaCollectionAdvisor.slnx -c Release -v minimal
```

Then run the desktop app on a copy of the database in a folder of its own, so the user's own
window and data are left alone (see "Testing" in `CLAUDE.md`), with remote debugging on so it can
be driven:

```bash
WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=9222" MTGA_ADVISOR_DB_PATH="<copy>\advisor.db" \
  src/MtgaCollectionAdvisor.Desktop/bin/Release/net10.0-windows/MtgaDeckAdvisor.exe &
```

Drive the page through the DevTools protocol (`http://127.0.0.1:9222/json`, the page at
`https://0.0.0.1/`). Assert, at minimum:

- The log next to the copy says `Window rendered`: the window drew, Blazor runs.
- The markup this issue adds is present, and its controls respond.
- Features that already worked still render.
- Closing the window stops the app and logs `Stopping: window closed`.

Close that copy by its PID or its window. Killing by image name would also close the user's
window. Native dialogs are reached with UI Automation, never with simulated keystrokes: those
go to whatever window has the focus.

A change to the macOS host (`src/MtgaCollectionAdvisor.Web`) is also checked there:
`MTGA_ADVISOR_PORT=5299` with the database copy, `<!--Blazor:` markers **and**
`/_framework/blazor.web.js` returning 200 (#54). Stop it by the PID listening on 5299.

Click-through paths (typing, clicking, drag) count as verified only if they were actually
driven. Everything else goes under Manual Testing Required with
concrete steps and expected results, never implied as tested.

Anything touching the memory scanner also needs MTG Arena running; if it is closed, say so
rather than reporting the scan as verified.

---

## Phase 5 — Commit and report

Commit the work with a message explaining *why* the change exists, not what changed.

Write the QA report to `qa_report_ISSUE_NUMBER.md` in the repo root (Write tool, absolute
Windows path — never `/tmp/`), post it, delete it:

```bash
gh issue comment ISSUE_NUMBER --repo REPO_SLUG --body-file "qa_report_ISSUE_NUMBER.md"
rm -f qa_report_ISSUE_NUMBER.md
```

Structure (the `<!-- QA-REPORT -->` marker is required — `/complete-work-item` looks for it):

```markdown
<!-- QA-REPORT -->
## 🚀 Implementation Report — #[number]: [title]

### What Was Built
[2–3 sentences: the feature, where the user finds it, the main moving parts.]

### Files Created / Modified
| File | Action | Description |

### Build Status
✅ Release build passed. / ❌ [errors]

### Unit Test Results
| # | Test | Status | Notes |

**Total: X passed, Y failed, Z skipped.**

### App Verification
| # | Check | Expected | Actual | Result |

### ⚠️ Manual Testing Required
[Numbered scenarios with steps and expected results, for anything automation could not
reach — interaction paths, and anything needing MTG Arena running.]

### AC Completion Status
- [x] **AC1**: ...

### Deviations from Implementation Guide
[What differed and why. "None." if nothing did.]

### Open Questions (Unresolved)
### Follow-up Issues to Create
```

Be honest in the report. An unverified path listed under Manual Testing is useful; the same
path implied to be tested is a trap for whoever reads it next.
