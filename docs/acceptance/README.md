# Runtime acceptance: the read-only scan

`read-only-scan.json` is the evidence from running the built application over the owner's real
configured roots. It is the answer to the question the fixtures cannot answer: the dashboard is
read-only against *sanitized* trees in every test, but the workspaces it exists to observe are
real, private, and messier than any fixture.

## Reproducing it

```bash
dotnet run -c Release --project src/MattWorkflowDashboard.App -- --acceptance ./scan --stamp $(git rev-parse HEAD)
```

Development-only instrumentation, reached only by that switch. It builds no UI. It reads the real
`settings.json` for the roots and registry intent, but everything it writes — cache, settings,
the `gh` configuration used for the offline pass — lives under the output directory, so an
acceptance run leaves the owner's dashboard state as untouched as it leaves their workspaces.

Exit code `0` means nothing monitored changed and nothing was refused; `3` means something did.

## What it does

1. Fingerprints every discovered project: workflow file names and contents, `HEAD`, working-tree
   status, local config, refs, and the repository's GitHub issues and labels.
2. Refreshes cold, then warm.
3. Starts a third refresh, waits until twenty external commands are in flight — past discovery,
   with several projects being indexed at once — then cancels it and times how long it keeps going.
4. Refreshes again with the real `gh` pointed at an empty configuration directory and every
   inherited token removed from the child's environment, so the tool itself decides it has no
   session.
5. Fingerprints everything a second time and reports the difference.

Throughout, every external command passes a boundary that refuses anything outside the four `gh`
reads and the handful of `git` reads the dashboard actually makes. `gh repo list` and `gh api` are
refused even though both only read, because each is a way to enumerate an account.

## What is in the file, and what is not

Counts, timings, bounds, diagnostic codes, and digests. Nothing else. Projects and repositories
appear only as identifiers salted per run, and the salt is never recorded — so an identifier
cannot be turned back into the name of a private project, and two runs cannot be joined against
each other. When nothing changed, as here, no identifier appears at all.

The instrument itself is covered by `ReadOnlyAcceptanceTests`: the boundary is shown to refuse
writes *and* to pass the reads the adapters really issue, and the before/after comparison is shown
to notice an edited file and a renamed one. A boundary that refused everything, or a comparison
that noticed nothing, would produce the same clean report.
