# Runtime acceptance

Five records, each answering a question the fixtures cannot: whether the dashboard stays read-only
against the owner's real workspaces, whether that stays true with GitHub enrichment off by default,
whether the test suite stays off the owner's keyboard, what the running shell does when Windows
itself moves underneath it, and what the overlay actually looks like on a real desktop. All are made
on the live machine, and all record their evidence here.

---

# The keystroke witness

`keystroke-witness.json` is the evidence that running the tests does not type into the owner's
session. It answers the failure this suite actually had: on 2026-07-30 shell tests delivering
keystrokes with `keybd_event` — which go to whatever window holds the foreground, not to any
particular window — opened ten Notepad windows on the owner's live desktop.

The tests no longer inject anything, and the windows they drive are on a Windows desktop of their
own. `docs/testing.md` explains how, and what it costs. This is the part that does not take that on
trust.

## Reproducing it

```bash
dotnet run -c Release --project tools/KeystrokeWitness -- docs/acceptance/keystroke-witness.json --stamp $(git rev-parse HEAD) dotnet test tests/MattWorkflowDashboard.Tests --configuration Release --no-build
```

**Run it while using the machine.** A clean result from an idle desktop is the weakest version of
this evidence: the hazard it exists to detect only appears when there is a foreground window to
steal, and somebody there to steal it from.

Exit codes follow the read-only scan's: `0` clean; `3` synthetic keystrokes reached the interactive
desktop — a safety failure; `4` the instrument could not be trusted, kept apart for the same reason
as below.

## What it does

It installs a low-level keyboard hook, which sees everything arriving on the interactive desktop,
and then runs the command it was given while watching.

Before watching anything, it injects one `VK_F24` into the owner's own session and requires the
hook to see it. This is the whole difference between evidence and an argument from silence: a hook
that was never installed, or was dropped for not answering its message queue, reports exactly the
same clean run as a suite that behaved perfectly. A run whose control was not observed is reported
`void` rather than clean, and exits `4`.

It records the virtual key of every **synthetic** event it sees. Real keystrokes — the owner
actually typing, which is the condition this is supposed to run under — are counted and discarded
without being recorded. An instrument that logged them would be a keylogger, and could not
honestly be run during normal work.

## The run recorded here

Against `d12a93b`, watching a full Release run of all 224 tests for 49 seconds while the machine
was in use:

| | |
| --- | --- |
| Control observed | yes — the hook was demonstrably listening |
| Synthetic keystrokes on the interactive desktop | **0** |
| Real key events seen and discarded | 1 — the machine was genuinely in use |
| Tests | 224 passed, 0 failed |

What is deliberately *not* reproduced here is the same measurement against `4a44566`, the commit
before the fix. It would be the sharpest possible demonstration that the instrument discriminates,
and running it means running the old suite, which is the hazard itself. The control keystroke
already shows the hook detects an injected key on this desktop, which is the only mechanism it has
to detect.

---

# The read-only scan

`read-only-scan.json` is the evidence from running the built application over the owner's real
configured roots. It is the answer to the question the fixtures cannot answer: the dashboard is
read-only against *sanitized* trees in every test, but the workspaces it exists to observe are
real, private, and messier than any fixture.

## Reproducing it

```bash
dotnet run -c Release --project src/MattWorkflowDashboard.App -- --acceptance "$env:TEMP/mwd-scan" --stamp $(git rev-parse HEAD)
```

**The output directory must be outside every configured root**, and the run refuses to start
otherwise. Its own cache and report are files, and files written under a monitored root are changes
to a workspace the run is supposed to be observing — worse, changes present in *both* fingerprints,
so the comparison would report that nothing moved. This repository is itself beneath a configured
root, so `./scan` is exactly the wrong answer.

Development-only instrumentation, reached only by that switch. It builds no UI. It reads the real
`settings.json` for the roots and registry intent, but everything it writes — cache, settings,
the `gh` configuration used for the offline pass — lives under the output directory, so an
acceptance run leaves the owner's dashboard state as untouched as it leaves their workspaces.

Exit codes: `0` clean and complete; `3` something monitored changed or a command was refused —
a safety failure; `4` nothing changed but something could not be read. The last is kept apart on
purpose. A private or deleted repository is an ordinary fact of a real workspace, not a safety
failure, but it is also not evidence that anything is unchanged — folding the two together would
either make a clean run impossible or let the least observant run look the strongest.

When a run does report a change, the report names the project only as a salted identifier. The real
paths for the affected projects alone are written beside it as `affected-projects.local.txt`, which
is local and must never be committed — a sanitized report that cannot tell you *whose* workspace
moved is unusable exactly when it matters.

## What it does

1. Fingerprints every discovered project: workflow file names and contents, `HEAD`, working-tree
   status, local config, refs, and the GitHub issues and labels of the repository the project is
   **confirmed** to be associated with — never the remote as it currently reads, because a changed
   remote is waiting on the owner and must not be queried before they confirm it.
2. Refreshes cold, then warm.
3. Starts a third refresh, waits until twenty external commands have been submitted — past
   discovery, with several projects being indexed — then cancels it and times how long it keeps
   going. It records both the number submitted and the number of child processes actually alive at
   that moment; only the second says anything about interrupting work in progress.
4. Refreshes again with the real `gh` pointed at an empty configuration directory and every
   inherited token removed from the child's environment, so the tool itself decides it has no
   session.
5. Fingerprints everything a second time and reports the difference — **and separately reports every
   source it could not read**. Two absent digests compare equal, so without that a source never
   observed would look exactly like one observed twice and found identical, and the report would be
   at its most reassuring when it had seen the least.

Throughout, every external command passes a boundary that refuses anything outside the four `gh`
reads and the handful of `git` reads the dashboard actually makes. `gh repo list` and `gh api` are
refused even though both only read, because each is a way to enumerate an account. The check is not
by verb alone: `git log --output=<path>` reads by verb and writes a file, and `-c` injects
configuration that can name a program to run, so options like those are refused wherever they appear.

Separately, every child process runs with `core.fsmonitor` and `diff.external` forced off through
the configuration environment. Both name a program that git runs, and both are set by the
repository being observed — monitored content is data, and a repository's own configuration is
monitored content.

## What is in the file, and what is not

Counts, timings, bounds, diagnostic codes, and digests. Nothing else. Projects and repositories
appear only as identifiers salted per run, and the salt is never recorded — so an identifier
cannot be turned back into the name of a private project, and two runs cannot be joined against
each other. When nothing changed, as here, no identifier appears at all.

The instrument itself is covered by `ReadOnlyAcceptanceTests`: the boundary is shown to refuse
writes *and* to pass the reads the adapters really issue, and the before/after comparison is shown
to notice an edited file and a renamed one. A boundary that refused everything, or a comparison
that noticed nothing, would produce the same clean report.

It has also discriminated on live data. An earlier run of this same instrument reported one
project's working tree as changed; the named project turned out to be one another agent on the machine
was writing to at that moment, in file types the dashboard has no code path to touch. A comparison
that had simply always been green would not have caught that, and would not have been worth
believing when it was.

---

# The local-only default

`local-only-default.json` is the evidence for #9: with `GitHubEnrichmentEnabled` at its shipped
default of `false`, a live run over the owner's real roots issues no `gh` command at all — not
merely that a `gh` call's result goes unused.

## Reproducing it

Requires a settings.json without an explicit `GitHubEnrichmentEnabled` override (a fresh install,
or the key temporarily removed from the owner's own file for the duration of the run — restored
immediately after):

```bash
dotnet run -c Release --project src/MattWorkflowDashboard.App -- --acceptance "$env:TEMP/mwd-local-only" --stamp $(git rev-parse HEAD)
```

Same instrument as the read-only scan above, including its own offline pass — which probes a real
`gh auth status` to characterize behaviour with no session. That probe is itself gated on
`GitHubEnrichmentEnabled` now: with enrichment off there is no session to probe for, so the one gh
call an earlier version of the instrument still made on a local-only run is gone too. See
`OfflinePassAsync` in `ReadOnlyAcceptanceRun.cs`.

## The run recorded here

Against `710d9da`, over the same 53 projects as the read-only scan:

| | |
| --- | --- |
| `CommandsIssued` | `git log`, `git rev-parse`, `git config --list`, `git show-ref`, `git remote get-url`, `git status` — no `gh` entry |
| Associations compared without querying GitHub | 33 of 33, 0 disagreements |
| Monitored state changes | 0 |
| Exit code | `4` — a pre-existing fingerprint gap (one file past the size bound in an unrelated project), not a safety failure |

---

# The running-shell gaps

`running-shell-gaps.json` is the evidence for the five items ticket #4 proved only in part and
handed on as #7. Every one of them needed something a test process cannot supply: hardware this
machine does not have, the owner's live Windows settings changed mid-run, or the shipped
executable started the way Windows would start it. None was left open because it was hard to write.

## The instrument

`--shell-journal <file>` records what the running window *is*, appending a line on every appearance
change, every visibility change, and on a five-second tick. It exists because the two claims these
items make cannot be photographed. A frame cannot show that the process never restarted, and a view
model that agreed to restyle is not evidence that anything did. Every line therefore carries the
process id and its start time beside the resolved palette, the effective opacity, the window's rect,
its scale, and the monitor it sits on — so a run that quietly restarted cannot read as a run that
restyled, and a restore can be checked in physical pixels rather than in whichever units each end
happened to speak.

It writes only when the switch names a file, and it records the shell's own state. No workspace
content, no project or repository names.

```bash
dotnet run -c Release --project src/MattWorkflowDashboard.App -- --shell-journal "$env:TEMP/mwd-shell.jsonl"
```

The items below are then driven by hand — the owner changing Windows, the agent reading the journal
back. That is the point of the ticket: what remains here is exactly what no test process could do
for itself.

## What was proven

**Geometry survives a scale change.** Parked at logical `(912, 516)` on a 125% display — physical
`(1140, 645)` — the dashboard was closed, the display changed to 150%, and reopened at logical
`(760, 430)`, which is physical `(1140, 645)`. Reversing the change reproduced `(912, 516)` and the
same physical point a third time. The position was predicted before each measurement rather than
read off afterwards.

**Windows appearance reaches the running window.** With the System theme selected, applying a
contrast theme moved the palette to `HighContrast.xaml` and `SurfaceColor` to `#FF202020` — exactly
the `systemWindowColor` of the moment, which is the one line in `ThemeManager` that makes the
system palette win over a colour a brush cannot carry. With the opacity setting held at 80%, the
effective opacity went to `1` for the duration and returned to `0.8` on the way out, so it is the
code forcing the surface opaque and not the slider. Switching Windows between light and dark moved
`AppsUseLightTheme` and the running window followed in both directions. All of it on one process id
across an unbroken uptime.

**The notification area.** The icon is present, its tooltip carrying live status truncated at the
63 characters the shell allows. A real double-click on it took the window from hidden to visible on
the process that was already running.

**An ordinary second launch.** Started with no arguments — the full startup path, not
`--single-instance-probe` — the second process exited `0` and left the first untouched.

**The production Run key.** The tray's own command wrote the real value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; a different, later process read it back and
showed the command checked. Launched verbatim through `CreateProcessW` with a null
`lpApplicationName`, which is how Windows starts a Run entry, it produced a working dashboard. The
same command removed the value, leaving the owner's other 23 entries as they were.

## What is deliberately not claimed

**The cross-monitor half of the geometry item.** This machine has one display. Changing its scale
exercises `Reinterpret`, which is the arithmetic that item is mostly about, and exercises nothing at
all about arriving on a *different* monitor. `MonitorDeviceName` is still persisted and still unused
by the restore path, and the case that would settle whether it should be needs the second display
this run did not have. Recorded as undecided rather than decided on reasoning alone.

**A real sign-in.** Everything above about the Run key is about the value and what launching it
does. Whether the dashboard appears after an actual sign-out and sign-in was not tested, because
the owner declined to spend the cycle. The distinction is kept because the weaker claim is easy to
read as the stronger one.

**The light/dark flip on the first attempt.** Worth recording because the instrument is what caught
it. The first pass looked like a pass: the window went light, then dark. The journal showed
`themeSetting` moving `System → Light → Dark` while `windowsAppsUseLightTheme` never left `0` — the
dashboard's own theme selector had been changed, and Windows had not moved at all. On the `Dark`
setting the subscription under test is ignored by design, so the run proved the opposite of what it
appeared to. The same pass also had the opacity slider at 100%, which would have made "high contrast
forces the surface opaque" true of the setting rather than of the code. Both were re-run from a
known state.

## Settings changed, and put back

Windows app light/dark mode; a Windows contrast theme; the display scale on `\.\DISPLAY1`; the
dashboard's Run value; and — from the mis-aimed first attempt — the dashboard's own theme and
surface opacity. Every one was restored, and the file records each with its restoration.

---

# The runtime frames

`visual-acceptance.json` and `frames/` are the evidence for ticket #6: the built overlay
photographed on the real desktop at the confirmed default of 80% opacity, rather than inferred
from a mockup or from the window's own visual tree.

The distinction matters more than it sounds. `--capture` renders the window's visual tree, which
cannot show what sits behind a translucent surface, because what sits behind it is not part of the
window. These frames are cut out of the composited screen instead, so the surface is photographed
doing the one thing the ticket is about.

## Reproducing it

```bash
pwsh tools/VisualFixture/New-VisualFixture.ps1
dotnet run -c Release --project src/MattWorkflowDashboard.App -- --state "$env:PUBLIC/mwd-visual-fixture/state"
pwsh tools/VisualFixture/Save-Frame.ps1 -Rect 725,113,775,950 -Path frame.png
```

The fixture is invented and lives under the **public** profile, not the owner's. The expanded
layout prints a project's full path in its detail pane, so a fixture under a personal profile would
put the account name into every wide frame — which is the one thing these frames must not carry.
`--state` keeps the run's settings, cache and logs out of the owner's own dashboard state.

## What the frames show

Compact, expanded, and narrow, over an ordinary titled window and over a borderless-windowed one.
The target's window style is recorded beside each frame rather than argued from the picture:
`WS_CAPTION` set at `0x96CF0000` for the ordinary case, cleared at `0x96080000` with `WS_POPUP` set
and the whole display covered for the borderless one.

The opacity is measured rather than asserted. Comparing the same target content under the base
surface against the same content beside it gives **79.3%** against a setting of 80%, the residual
being sRGB gamma and 8-bit rounding. A frame that merely came from a run configured at 80% would
prove the configuration; this proves the pixels.

## What is deliberately not claimed

Exclusive-fullscreen applications and other topmost windows — the overlay makes no promise about
either, and neither was tested. Only 125% scaling and only the Dark palette were photographed.

Every frame also carries a `Diagnostics 5` badge, because the fixture uses the status `Idle`, which
the parser does not recognise. That is the fixture's doing rather than the product's, and the frames
were approved with it visible rather than quietly re-shot.
