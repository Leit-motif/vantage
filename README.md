# Workflow dashboard

A personal, **read-only** Windows 11 x64 dashboard for development work spread across local
workflow artifacts, Git repositories, and explicitly linked GitHub issues. It observes and
explains those sources; it never becomes another workflow authority.

Specified by [issue #1](https://github.com/Leit-motif/matt-workflow-dashboard-widget/issues/1).

## What it does

It discovers projects beneath configured roots, indexes `.scratch` planning artifacts and
reachable local Git history, enriches explicitly linked items through your already-authenticated
`gh` session, and projects that evidence into traceable project states, next actions, recent
activity, conflicts, diagnostics, and full-pipeline progress.

Every displayed conclusion carries provenance: which source observed it, the raw value as
written, what kind of timestamp it has, and which refresh produced it.

### Discovery, nesting, and remotes

A project is any directory carrying `.git`, `AGENTS.md`, `docs/agents/issue-tracker.md`, or
`.scratch`. Nesting alone changes nothing: a project inside another project is discovered like any
other. Vendor, dependency, tool, build, and cache locations — `node_modules`, `obj`, `.venv`,
`packages`, and the rest of the configurable list — are the exception. Those trees are never walked
into uninvited, so name the full path of an intentionally independent project down there under
**Settings → Projects** to opt it in.

Registry intent is per project and is written the moment it is made: enabled, hidden, or excluded,
pinned, and nested opt-in all survive a restart, and hiding a project keeps its entry rather than
forgetting it.

A GitHub origin is an association on the local path, never the identity. The first remote seen is
recorded; if the remote later changes, the new one is held as *pending* and reported, and the
dashboard keeps using the confirmed association until you confirm the relink in Settings — which
adopts the pending origin you were shown, not whatever the remote happens to read by then.

## What it never does

- Change a workflow file, Git state, a GitHub issue or label, or repository configuration.
- Enumerate your GitHub account. Only repositories associated with a local project are queried.
- Execute anything it reads. Monitored content is data; external commands receive structured
  argument lists and never a shell string.
- Send telemetry, analytics, or crash reports anywhere.

## Layout

| Project | Role |
| --- | --- |
| `src/MattWorkflowDashboard.Core` | UI-free domain: observed evidence, normalized workflow facts, derived projections |
| `src/MattWorkflowDashboard.Infrastructure` | Discovery, parsing, Git and `gh` adapters, SQLite cache, settings, logging, the refresh orchestrator |
| `src/MattWorkflowDashboard.App` | The WPF overlay, tray, and settings window |
| `tests/MattWorkflowDashboard.Tests` | Tests, driven through the product's refresh boundary |

## Build and run

```bash
dotnet test MattWorkflowDashboard.slnx
```

That is the whole suite, including the shell tests that drive a real window with a real keyboard.
They run on a Windows desktop of their own, so they neither appear on your screen nor take the
keyboard from what you are doing, and you can carry on working through them. See
[docs/testing.md](docs/testing.md) for what those tests prove and what they deliberately do not.

```bash
dotnet run --project src/MattWorkflowDashboard.App
```

Shipping configuration — self-contained, single-file, unsigned, untrimmed, no installer and no
auto-update:

```bash
dotnet publish src/MattWorkflowDashboard.App -p:PublishProfile=win-x64 -c Release
```

## Where its own state lives

Everything the dashboard writes is under `%LOCALAPPDATA%\MattWorkflowDashboard`:

- `settings.json` — atomic, schema-versioned configuration
- `cache.db` — a **disposable** SQLite index (90 days of activity, current ticket snapshots,
  last-known-good project snapshots). Deleting it loses nothing but speed and history.
- `logs/` — bounded rolling local logs

No dashboard state is ever written into a monitored repository.

## Reading the interface

State is never carried by colour alone: each state shows a written label and a Fluent outline
glyph as well as its accent — blue for in progress, teal for ready, amber for blocked, gray for
idle, green for completion, violet for conflicts, red for errors.

Progress is equal-weight and spans the whole pipeline: planning, research, grilling, prototypes,
implementation, review, and release. Maps and parent artifacts are containers, not work units.
A ticket counts exactly once, at completion; its internal stage is shown separately.

### Following a conflict to its evidence

A project's conflict badge is a control rather than a decoration: it selects that project, opens
the expanded view, and lists its disagreements. Each one names the item it is about, what the local
side says, what the remote side says, which side was kept and why, and where each side was
observed — source, locator, what kind of timestamp it has, and the refresh that produced it. Every
affected item carries the same control, which narrows the list to that item alone; **Show all**
widens it again.

### On "80% transparency"

The specification's target is stated as *80% transparency*. Opacity and transparency are routinely
inverted between APIs, so Settings expresses the value as **opacity**, where 100 is fully solid,
and shows both readings next to the slider (`80% opaque · 20% see-through`). The default is 80%
opacity. Only the background surface carries it — text and controls are composited fully opaque
so translucency never costs legibility.

## Overlay behaviour, honestly

The window stays above ordinary and borderless-windowed applications and does not take focus.
It makes **no** promise about exclusive-fullscreen applications or other topmost windows; those
can and will cover it.

Closing hides the window. Everything else lives in the tray: show/hide, compact/expand,
click-through, refresh, settings, logs, launch at sign-in, and exit. Click-through is off by
default and always recoverable from the tray, and the optional global hotkey has no default
binding.

## Runtime evidence

`--capture <directory>` is development-only instrumentation: it waits for the first refresh, then
renders the built application in compact, expanded, and narrow layouts to PNG. It renders the
window's own visual tree, so it proves the shell's layout, hierarchy, and colours without
capturing whatever is on the desktop behind the glass.

Proving translucency *over* a specific desktop application still requires a screen capture of the
running overlay on a real desktop; that frame is the owner's to take.

`--acceptance <directory>` is the other half: it runs the built application over the real
configured roots without any UI, and writes sanitized evidence that nothing it observed was
changed — along with how long the scan took, how quickly it stopped when asked, what it does
without a `gh` session, and which repositories it queried. See
[docs/acceptance](docs/acceptance/README.md).
