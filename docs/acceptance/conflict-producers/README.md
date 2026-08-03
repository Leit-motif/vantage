# The conflict producers, live

Ticket: `.scratch/local-only/issues/03-conflicts-between-any-two-observations.md`.
Everything here was observed on **`a64b4cb`** (`fix: reach the conflicts region in a captured frame,
and stop it collapsing`).

The fixtures in `ConflictProducerTests` show that each producer fires on a case built for it and
stays silent on the near-miss beside it. They cannot answer the two questions this record exists
for: **how many disagreements appear on real trackers**, since a badge that is always lit is a badge
nobody reads, and **what the owner actually sees**, since a view model that holds two sides is not
evidence that a window shows them.

## What it reports on real trackers

Three runs of the product's own refresh boundary, each writing only counts and — for this
repository alone — the disagreements themselves. Absolute paths are never written: this directory is
published, and the owner's project names are theirs.

| | projects | reporting nothing | conflicts |
| --- | --- | --- | --- |
| the owner's first root | 7 | 6 | 4 |
| the owner's second root | 13 | 11 | 2 |
| **both roots** | **20** | **17** | **6** |
| this repository, clean | 1 | 0 | 4 |
| this repository, one ticket edited | 1 | 0 | 7 |

`live-real-roots.txt` holds the first two. **Seventeen of twenty real projects report nothing at
all**, which is the silence half of the acceptance cell measured on real data rather than on a
fixture. The six that remain are on three projects.

`live-clean.txt` is this repository with the tree clean: 21 tickets, 8 diagnostics, **4 conflicts**,
all of them producer three, and all of them real — `dashboard-fast-follow` 01/02/03 and
`evidence-integrity` 01 each say `resolved` above unticked acceptance boxes.

```
CONFLICT WorkflowStatus on dashboard-fast-follow/01-minimal-ribbon
  working tree · 01-minimal-ribbon.md#L3 :: resolved
  working tree · 01-minimal-ribbon.md#acceptance :: 5 of 5 acceptance item(s) still unticked
```

Both sides of that were read from one file, and they are still told apart, because a side is named
by its own provenance down to the anchor it was read at.

`live-uncommitted.txt` is the same run with one real ticket edited on disk and not committed —
`local-only/04` moved from `Status: needs-triage` / `Blocked by: 03` to `Status: blocked` /
`Blocked by: 01`, where `01` is resolved. The file was restored from git afterwards and the tree
verified clean. **Seven conflicts**: the four above unchanged, plus producers one and two on the one
ticket that was touched.

```
CONFLICT WorkflowStatus on local-only/04-a-project-is-its-worktrees
  working tree · 04-a-project-is-its-worktrees.md :: blocked
  last commit · 04-a-project-is-its-worktrees.md@HEAD :: needs-triage

CONFLICT Blockers on local-only/04-a-project-is-its-worktrees
  working tree · 04-a-project-is-its-worktrees.md#L3 :: blocked, waiting on 01
  working tree · 01-remove-the-github-adapter.md#L3 :: 'Remove the GitHub adapter…' is resolved
```

The difference between the two runs is the measurement. The only other thing that moved is the one
diagnostic that should have: `needs-triage` is not a status the dashboard recognizes and `blocked`
is, so `TICKET_AMBIGUOUS_STATUS` stopped being raised for that file while the edit was in place.

### The reading that was rejected, and what it cost

The cold review held that producer 2 should be implemented as its ticket line literally reads: a
`Blocked by:` naming something that does not exist, and one naming something already resolved,
without requiring the ticket to say it is blocked. That reading was built as a throwaway and
measured over the same two real roots before being discarded:

| | conflicts over 20 real projects | projects reporting nothing |
| --- | --- | --- |
| as shipped | **6** | **17** |
| the literal reading | **97** | **6** |

Roughly 74 of the 97 are dangling edges, each of which the dashboard already reports as its own
`BLOCKER_MISSING` diagnostic. A badge lit on 14 of 20 projects, most of it duplicating a diagnostic,
is the failure the parent ticket's *Watch for* section names. The narrow reading ships; the
disagreement and these numbers are recorded in the ticket rather than settled quietly.

## What the owner sees

![The conflicts region of the running dashboard, reached by invoking a project's conflict badge: two disagreements on one project, each with both sides labelled by where they were read — working tree and last commit for one, and two anchors inside a single file for the other — followed by the resolution and the full provenance trail for each side](conflicts.png)

`conflicts.png` is the built application on a real desktop, from `--capture`, over the sanitized
workspace `tools/VisualFixture/New-VisualFixture.ps1` builds. The fixture is invented and lives
under the public profile, so no private name or path is in the frame.

It is taken by executing the aggregate badge's **own command**, not by arranging the pane for the
photograph. That command selects the project, opens the expanded view and asks the conflicts region
to be brought into view, so the frame is the result of the navigation the acceptance cell is about.

In it: producer one, `working tree · 03-mount-the-outer-planets.md` saying `In progress` against
`last commit · 03-mount-the-outer-planets.md@HEAD` saying `Ready`; and producer three, one file's
`#L3` against its own `#acceptance`. Neither side is labelled `local` or `remote` — the names come
from the provenance — and each carries its full trail beneath it. The badge on the row reads `3`,
the third being producer two, below the fold of a 520-unit window.

**Photographing it is what found a defect.** A side's label is now as long as a file name, and in
the two-column layout the auto-sized label took the width and left the value one character wide. No
test caught it: they assert that the text is present and reachable, which it was. The layout is a
label above its value now, and the first frame — the broken one — is why.

## What this does not show

The disagreement between two agents writing one ticket file, which is producer five in the ticket's
list and deliberately left to its own work. And a stated status against what a commit already claims
to have done, which is `05-a-status-against-what-a-commit-claims.md`.
