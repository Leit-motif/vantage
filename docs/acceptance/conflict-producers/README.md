# The conflict producers, on a real tracker

Ticket: `.scratch/local-only/issues/03-conflicts-between-any-two-observations.md`.
Everything here was observed on **`1c24f98`** (`test: hold the cost of the rename on an old cache,
and claim the ticket`).

The fixtures in `ConflictProducerTests` show that each producer fires on a case built for it and
stays silent on the near-miss beside it. They cannot answer the question this ticket actually
worries about, which is not whether a disagreement is found but **how many are found on a real
tracker** — a badge that is always lit is a badge nobody reads.

So the product's own refresh boundary was run over a real repository: this one, 20 tickets across
six efforts, with its real git history.

## Reproducing it

`LiveConflictDump.cs` is a one-off measurement rather than a permanent test, kept here for the same
reason `local-only-removal/ProjectionDump.cs` is. Copy it into `tests/Vantage.Tests/RefreshSeam/`,
run it, and delete the copy:

```bash
LIVE_ROOT=<directory holding the repository> LIVE_DUMP=<output file> dotnet test tests/Vantage.Tests/Vantage.Tests.csproj --filter "FullyQualifiedName~LiveConflictDump"
```

It writes nothing inside the repository it observes: settings and cache go to a temporary directory
of their own.

## What it found, with the tree clean

`live-clean.txt`. **Four conflicts across 20 tickets, beside 7 diagnostics.**

Every one of them is producer three, and every one is real: four tickets that call themselves
`resolved` while their own acceptance lists still hold unticked boxes.

```
CONFLICT WorkflowStatus on dashboard-fast-follow/01-minimal-ribbon
  working tree · 01-minimal-ribbon.md#L3 :: resolved
  working tree · 01-minimal-ribbon.md#checklist :: 5 of 5 checklist item(s) still unticked
```

That is the number the ticket's "do not let the count become noise" warning asks for: conflicts are
rarer here than diagnostics, they land on four items rather than on everything, and each names a
ticket whose status the owner would want to look at. The two readings are of the same file and are
still told apart, because each side is named by where in that file it was read.

Producers one and two are silent on a clean checkout of this tracker, which is the correct answer:
nothing is uncommitted, and no ticket here states `blocked`.

## What it found with one uncommitted edit

`live-uncommitted.txt`. The same run, with a single real ticket edited on disk and not committed —
`local-only/04` moved from `Status: needs-triage` / `Blocked by: 03` to `Status: blocked` /
`Blocked by: 01`, where `01` is resolved. The file was restored from git afterwards and the tree
verified clean.

**Seven conflicts.** The four above, unchanged, plus all three of the remaining producers on the
one ticket that was touched:

```
CONFLICT WorkflowStatus on local-only/04-a-project-is-its-worktrees
  working tree · 04-a-project-is-its-worktrees.md :: blocked
  last commit · 04-a-project-is-its-worktrees.md@HEAD :: needs-triage

CONFLICT Blockers on local-only/04-a-project-is-its-worktrees
  working tree · 04-a-project-is-its-worktrees.md :: 01
  last commit · 04-a-project-is-its-worktrees.md@HEAD :: 03

CONFLICT Blockers on local-only/04-a-project-is-its-worktrees
  working tree · 04-a-project-is-its-worktrees.md#L3 :: blocked, waiting on 01
  working tree · 01-remove-the-github-adapter.md :: 'Remove the GitHub adapter…' is resolved
```

The first two are producer one: what this checkout says now against what every other checkout still
reads. The third is producer two: the ticket says it is waiting, and the work it names is finished.

The difference between the two runs is the measurement. Nothing else in the two dumps moved except
the one diagnostic that went with the edit — `needs-triage` is not a status the dashboard
recognizes and `blocked` is, so `TICKET_AMBIGUOUS_STATUS` stopped being raised for that file while
the edit was in place.

## What this does not show

The disagreement between two agents writing one ticket file. That is producer five in the ticket's
list, deliberately left to its own work, because detecting it after the fact is not the same as
preventing the loss.
