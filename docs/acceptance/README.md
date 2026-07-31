# Runtime acceptance: the read-only scan

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
