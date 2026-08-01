# Vantage

Work spread across a dozen repositories leaves a trail — planning files, commits, branches, issues.
Vantage reads that trail and reconstructs what each project is actually doing, without ever writing
to any of it, and shows you where every conclusion came from.

**Status: personal alpha.** Built for one person's workflow on one machine, and published as a
working reference implementation rather than a product. Windows 11 x64 only.

> Source-available for review and evaluation. **This is not open-source software** — see
> [NOTICE.md](NOTICE.md).

![The expanded layout: five projects with their pipeline progress and next actions on the left, the selected project's provenance, efforts, activity and diagnostics on the right](docs/images/expanded.png)

<sub>Rendered from a synthetic fixture by the application's own <code>--capture</code>
instrumentation. Every project, path and ticket in the frame is invented.</sub>

---

## The problem

If you work across many repositories at once, the state of that work lives in your head. Which
project is blocked and on what. Which one you left mid-thought three days ago. Which ticket is
actually next rather than merely open. The evidence to answer all of it already exists on disk — it
is just scattered across markdown files, Git history, and issue trackers, in shapes that do not
compose.

The usual fix is to adopt a tool that becomes the authority: you move the work into it, and now the
tool's model of your project is the real one, and keeping it true is another job.

Vantage takes the opposite position. It is strictly an observer. It writes nothing, owns nothing,
and holds no state you would miss — delete everything it has ever written and you lose some speed
and some history, and nothing else. If it disagrees with your repository, your repository is right
and Vantage has a bug.

## What it does

It discovers projects beneath the roots you configure, indexes their `.scratch` planning artifacts
and reachable local Git history, and projects that evidence into project states, next actions,
recent activity, diagnostics, and progress across the whole pipeline — planning, research,
grilling, prototypes, implementation, review, and release.

**Vantage is local-only, and not as a default you can switch off.** It reads local files and local
git, and it does not read GitHub. There is no adapter, no setting, and no `gh` process — the
removal and the argument for it are recorded in the planning repository, and the evidence for it is
in [`docs/acceptance/local-only-removal/`](docs/acceptance/local-only-removal).

Git stays, and stays a primitive: it is offline and deterministic, and gives time, attribution and
"what changed when" with no network, no auth and no availability question. A remote service levies
a different tax — auth state, availability, staleness, reconciliation, and an "is this the whole
answer" question on every read — and the ambition here is a context layer several agents can
**write** to, which a file tree serves and an issues API does not.

If enrichment ever comes back it is additive, properly: an unreachable remote must never degrade
the local answer.

## The rule it is built on

Most of the interesting engineering here is one constraint: **an observation and a conclusion are
different kinds of thing, and the difference has to survive all the way to the screen.**

Evidence moves through three stages, and never backwards:

```
Observed          exactly what a source said, as it was written
    |             ObservedArtifact · ObservedCommit
    v
Normalized        the workflow facts those observations imply
    |             WorkflowEffort · WorkflowTicket · WorkflowStatus
    v
Projected         what the dashboard is prepared to tell you
                  project state · next action · progress · conflicts
```

Every fact carries a `Provenance` from the moment it is observed: which adapter saw it, the locator
it was seen at, the raw value as written, and — the part that matters most — **what kind of
timestamp it has**.

That last one is the difference between a dashboard you can trust and one you cannot. A file's
modification time is not evidence that work happened; it is evidence that a file changed. So
timestamps are typed rather than merged into one number:

| `TimestampProvenance` | What it actually means |
| --- | --- |
| `GitCommit` | An author committed at this time |
| `WatcherEvent` | The filesystem told us while we were watching |
| `ObservedChange` | We compared two refreshes and saw real movement — but this is when we *noticed*, not when it happened |
| `FileSystem` | A modification time, and nothing more |

`ObservedChange` exists because the honest answer to "when did this change?" on a first scan is
usually "we don't know — only that it differs from last time." Collapsing that into a real timestamp
would have been easy, and would have quietly made the entire display untrustworthy.

### When observations disagree

Two observations of one subject can disagree, and that is surfaced rather than resolved away. A
project's conflict badge is a control rather than a decoration: it opens that project's
disagreements, each one naming the item, what each side says, which side was kept and why, and
where each side was observed — down to the refresh that produced it.

Identity comes only from explicit links — title similarity is never an identity signal, so two
similarly named pieces of work are never silently merged into one.

Removing GitHub removed the only *producer* of disagreements, not the model: local-versus-remote
was the instance, not the purpose. The producers that replace it — a working tree against the last
commit, a stated `Blocked by:` against the ticket it names, two agents writing one file — are
specified but not yet built. Until they land, the conflict model has no producer.

## What it never does

- Change a workflow file, Git state, or repository configuration.
- Talk to the network. There is no remote adapter to reach one with.
- Execute anything it reads. Monitored content is data; external commands receive structured
  argument lists and never a shell string.
- Send telemetry, analytics, or crash reports anywhere.
- Write its own state into a repository it is watching.

The first and last of those are not claims. They are [measured on every acceptance
run](docs/acceptance/README.md) against real workspaces, by fingerprinting the tree before and
after.

## Discovery

A project is any directory carrying `.git`, `AGENTS.md`, `docs/agents/issue-tracker.md`, or
`.scratch`. Nesting changes nothing on its own — a project inside another project is discovered like
any other.

Vendor, dependency, build and cache trees are the exception. `node_modules`, `obj`, `.venv`,
`packages` and the rest of a configurable list are never walked into uninvited, so if you keep a
genuinely independent project down there, name its full path under **Settings → Projects** to opt it
in.

Registry intent is per project and written the moment it is made. Enabled, hidden or excluded;
pinned; nested opt-in — all survive a restart. Hiding a project keeps its entry rather than
forgetting it, so the choice is not silently undone by the next scan.

Discovery is bounded, and the bounds are configurable and reported: maximum depth, maximum projects,
maximum directories scanned, concurrent external processes, per-process timeout. A scan that hits a
bound says so in diagnostics rather than returning a quietly short answer.

## Architecture

| Project | Owns |
| --- | --- |
| [`src/Vantage.Core`](src/Vantage.Core) | The domain, with no UI and no I/O: observed evidence, normalized workflow facts, derived projections, provenance, the conflict model |
| [`src/Vantage.Infrastructure`](src/Vantage.Infrastructure) | Everything touching the outside world: discovery, markdown parsing, the Git adapter, the SQLite cache, settings, logging, the refresh orchestrator |
| [`src/Vantage.App`](src/Vantage.App) | The WPF overlay, tray, and settings window |
| [`tests/Vantage.Tests`](tests/Vantage.Tests) | The engine's suite, driven through the product's real refresh boundary. Targets `net10.0` |
| [`tests/Vantage.Tests.Shell`](tests/Vantage.Tests.Shell) | The suite that needs Windows to be true: the overlay read back from a real `HWND`, and registry intent through the Settings view model. Targets `net10.0-windows` |
| [`tests/Vantage.Tests.Support`](tests/Vantage.Tests.Support) | The fixtures both suites use, on the portable framework so the portable suite can |
| [`tools/`](tools) | A keystroke witness and a synthetic-workspace generator, both used to produce acceptance evidence |

The dependency direction is the point: `Core` knows nothing about WPF, SQLite or Git, so the
rules about what may be believed are testable without any of them present. The test split is what
holds that to account — `Vantage.Tests` targets `net10.0`, and a `net10.0` project cannot reference
a `net10.0-windows` one, so the engine acquiring a Windows dependency stops being something a reader
has to notice. CI also runs that suite on Linux and macOS, because compiling without a Windows
reference and running without Windows are different claims.

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 11 x64.

```bash
dotnet test Vantage.slnx
```

That is the entire suite, including the shell tests that drive a real window with a real keyboard.
They run on a Windows desktop of their own, so they never appear on your screen or take the keyboard
from what you are doing — you can carry on working through them.

```bash
dotnet run --project src/Vantage.App
```

A fresh install has no roots configured and says so. Add one under **Settings → Projects**.

Shipping configuration — self-contained, single-file, unsigned, untrimmed, no installer and no
auto-update:

```bash
dotnet publish src/Vantage.App -p:PublishProfile=win-x64 -c Release
```

## How it is tested

Tests run through the same refresh boundary the application uses, against real files on disk, so
what passes is the product's behaviour rather than a rehearsal of its internals with everything
interesting replaced by a mock.

The shell tests are the awkward ones. An overlay's job is described entirely in Win32 terms — stay
above other windows, stay out of the taskbar, never take the keyboard, come back when the tray asks
— and a view model that *agreed* to be topmost is not evidence that anything is. So those tests
drive a real window with a real `HWND` and read the answers back from the operating system.

That is also how this suite once opened ten Notepad windows on its author's desktop.
[`docs/testing.md`](docs/testing.md) is the record: what the hazard actually was, why the obvious
guard could not fix it, the measurements that ruled out the preferred solution, what was done
instead, and — stated plainly — **what those tests no longer prove** as a result.

## Runtime acceptance evidence

Some questions cannot be answered by a test suite, because they are about what the built
application does on a real machine. [`docs/acceptance`](docs/acceptance/README.md) holds five
records that answer them, each stamped with the commit it was made at.

| Record | The question it answers |
| --- | --- |
| `read-only-scan.json` | Does a scan of the author's real workspaces change anything? Fingerprinted before and after. |
| `local-only-default.json` | With enrichment off by default, does anything still reach the network? Superseded: enrichment no longer exists to be off. |
| `keystroke-witness.json` | Does running the test suite type into the interactive desktop? Witnessed by a calibrated low-level hook. |
| `running-shell-gaps.json` | What does the running window do when Windows changes underneath it — display scale, light/dark, contrast themes? |
| `visual-acceptance.json` + `frames/` | What does the overlay look like composited over a real desktop, at the default opacity? |

They are as useful for what they refuse to claim as for what they establish. The visual record
measures surface opacity at **79.3%** against a setting of 80% and explains the residual as sRGB
gamma and 8-bit rounding, rather than rounding it into agreement. The running-shell record leaves
its cross-monitor half explicitly unproven for want of a second display.

## Limitations

- **Windows 11 x64 only.** WPF, Win32 interop, a tray icon. There is no cross-platform story.
- **No promise about exclusive-fullscreen applications** or other topmost windows. The overlay stays
  above ordinary and borderless-windowed applications and does not take focus; anything else can and
  will cover it. That boundary was tested rather than assumed.
- **No installer, no code signing, no auto-update.** It publishes as a single unsigned executable.
- **It reads one specific workflow grammar** — the `.scratch` effort layout and the metadata keys
  that go with it. Projects that do not use it are still discovered; there is just less to say about
  them.
- Only 125% display scaling and only the dark palette were photographed for visual acceptance.

## Privacy, and where its own state lives

No telemetry, no analytics, no crash reporting. Nothing leaves the machine, and there is no
adapter that could take it anywhere.

Everything Vantage writes is under `%LOCALAPPDATA%\Vantage`:

- `settings.json` — atomic, schema-versioned configuration
- `cache.db` — a **disposable** SQLite index: 90 days of activity, current ticket snapshots,
  last-known-good project snapshots. Deleting it costs speed and history, nothing else.
- `logs/` — bounded, rolling, local

No Vantage state is ever written into a repository it is watching, and the acceptance run proves
that rather than asserting it.

## Reading the interface

Compact is the everyday state — a narrow column of project cards that sits at the edge of the
screen. Expanding it adds the detail pane in the frame at the top of this page. Both are the same
refresh; the expanded view simply shows the evidence behind the row you selected.

<img src="docs/images/compact.png" width="380" alt="The compact layout: a narrow column of project cards, each with a state glyph, pipeline segments, a percentage and a next action">

State is never carried by colour alone. Every state shows a written label and a Fluent outline glyph
as well as its accent — blue in progress, teal ready, amber blocked, gray idle, green complete,
violet conflicts, red errors.

Progress is equal-weight and spans the whole pipeline. Maps and parent artifacts are containers, not
work units. A ticket counts exactly once, at completion, and its internal stage is shown separately
— so a ticket cannot inflate progress by being large.

Closing hides the window; everything else lives in the tray — show/hide, compact/expand,
click-through, refresh, settings, logs, launch at sign-in, exit. Click-through is off by default and
always recoverable from the tray. The optional global hotkey has no default binding.

Only the background surface carries the opacity setting. Text and controls are composited fully
opaque, so translucency never costs legibility.

## How it was built

Vantage was specified, implemented and reviewed through coding agents, directed by its author. The
specifications and tickets that drove it live in a separate planning repository and are not
published; what *is* published is the part that can be checked — the commit history, and
[`docs/acceptance/`](docs/acceptance), where each runtime claim is paired with the record that
answers it, including the fingerprints proving a run over real workspaces changed nothing.

The corrections are the part worth reading. A commit reclassifying a skipped probe from
*observed* to *unanswered*, and an issue titled *“Shell-seam tests inject keystrokes into the
owner's desktop”*, are both cases of an agent's claim not surviving review.

## Copyright

Copyright © 2026 Amritpal Singh Chana. All rights reserved.

Source-available for review and evaluation only. Not open source. See [NOTICE.md](NOTICE.md).
