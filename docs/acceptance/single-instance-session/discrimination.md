# Discrimination: does each test fail when the thing it asserts is broken?

A test pointed at a subject that cannot fail passes exactly as loudly as one that works. Every
behaviour claimed by `.scratch/evidence-integrity/issues/02-single-instance-mutex.md` was therefore
checked by breaking it and requiring the named test to notice.

Each mutation was applied **alone** on `e1eb69b`, the named test run, and the file restored before
the next one. **Eleven mutations, eleven failures, no passes.** The suite is green on the unmutated
commit — `Vantage.Tests` 143/143 and `Vantage.Tests.Shell` 87/87.

Rows 1 to 10 were first run on `3d07aeb` and every one of them was re-run on `e1eb69b`, because
`e1eb69b` changed `TestSession` and `SingleInstanceTests` — the files that produce these
measurements — in response to the cold review. An earlier observation of a mutation whose subject a
later commit edited is a claim about code that is no longer there, so none of the first-pass results
is relied on here. `git diff --name-only e1eb69b..HEAD -- src tests` is empty, so the code these
eleven runs measured is the code at `HEAD`.

| # | What was broken | Test required to fail | Result |
| --- | --- | --- | --- |
| 1 | The guard calls every process the first instance, so nothing ever stands down | `A_second_application_stands_down_instead_of_opening_a_competing_overlay` and `The_session_is_released_when_the_dashboard_exits` | failed, both |
| 2 | The guard calls no process the first instance | all three cross-process tests | failed, all three |
| 3 | `Dispose` returns while still holding the session | `The_session_is_released_when_the_dashboard_exits` | failed |
| 4 | The constructor takes the dashboard's session by default again | `The_session_the_dashboard_claims_is_the_one_named_for_the_dashboard` | failed |
| 5 | The product claims a session other than the one this repository records | `The_session_the_dashboard_claims_is_the_one_named_for_the_dashboard` | failed |
| 6 | An ordinary launch honours `--instance-name` too | `Only_the_probe_may_decide_about_a_session_other_than_the_dashboard_s` | failed |
| 7 | The probe ignores the session it is given and decides about the dashboard's | `Only_the_probe_may_decide_about_a_session_other_than_the_dashboard_s`, and both tests that hold a session | failed, all three |
| 8 | `TestSession` hands out the dashboard's own session | `The_names_this_suite_claims_are_its_own_and_never_repeat` | failed, and `A_session_this_suite_did_not_issue_itself_…` with it |
| 9 | A test constructs the guard without going through `TestSession` | `Nothing_in_this_suite_claims_a_session_except_through_the_name_it_owns` | failed |
| 10 | `TestSession` stops varying the name between claims | `The_names_this_suite_claims_are_its_own_and_never_repeat` | failed |
| 11 | `TestSession.Claim` stops requiring that the name is one it issued | `A_session_this_suite_did_not_issue_itself_cannot_be_claimed_through_it_either` | failed |

Rows 1 and 2 are the pair the ticket's second acceptance cell asks for, and they are recorded as a
pair because the cell's own wording does not survive contact with the subject. A guard that has
*stopped standing down* calls every process the first instance — and
`The_application_starts_normally_when_nothing_else_is_running` asserts exactly that a lone launch is
allowed to start, so it cannot fail on that mutation and should not. Row 2 breaks the decision in the
other direction and all three fail. Between them every one of the three is held, in the direction it
is actually about.

Rows 8 to 11 are the third cell, and rows 9 and 11 are the pair that makes it hold. Row 9 makes the
claim about the *suite* rather than about the three tests someone happened to look at, because it
reads the compiled assembly and fails on a construction of the guard anywhere but `TestSession`.
Row 11 is what the cold review's third finding forced: checking *where* a claim is written is not
checking *what* it claims, and `Claim` would have carried the dashboard's own session as happily as
any other string. It now claims only names it issued itself.

## Three mutations had to be rewritten, and that is recorded rather than quietly corrected

- **Row 3's first attempt did not build.** Inverting `if (_held is null)` to `is not null` makes the
  nullable analysis certain that `_held` is null at the release below it, and this repository builds
  with `TreatWarningsAsErrors`. Replacing the release with a `return` breaks the same behaviour and
  compiles.
- **Rows 5 and 8's first attempts matched nothing.** Both patterns were written with a doubled
  backslash against source that carries one, so `perl` matched nothing, exited 0, and the tests ran
  against unmutated code. They were caught because the harness asserts the file actually changed
  before it believes a result — `git diff --quiet` after applying, reported as
  `MUTATION DID NOT APPLY` rather than as a pass.
- **Row 8's second attempt did not build.** Returning the dashboard's session directly left
  `TestSession`'s counter field unread, and an unused field is an error here. The third attempt keeps
  the counter in the expression and still returns the dashboard's session.

The rule all three give is the one this repository already recorded against the conflict producers:
**a passing mutation is a claim about the mutation before it is a claim about the test**, and a
mutation that silently failed to apply reports exactly what a well-tested behaviour reports.

## The runs behind the first acceptance cell

Two dashboards held the session over the course of this work, and the cell is closed on the second.

The first was this branch's own build launched with `--state <scratch>`, so that it claimed the
real, session-wide `Local\Vantage.SingleInstance` while writing its settings, cache and logs
somewhere disposable. The cold review's first finding was that this supports an inference about the
collision rather than an observation of the artifact the cell names, and it was right: the cell says
*installed*. So the gate was re-run against the installed copy at
`%LOCALAPPDATA%\Programs\Vantage\Vantage.exe` — product version `1.0.0+b7863d26`, a different binary
from this branch — and that is what the cell now rests on. The scratch-build runs are kept below
because the reproduction was made against them.

**The defect, reproduced first.** On `d14fb0d`, whose `src/` and `tests/` are identical to the review
baseline `ba5e495`, with the scratch-state build running:

```
Failed The_application_starts_normally_when_nothing_else_is_running
  Expected:<0>. Actual:<2>. With no dashboard running, the application must take the session for itself.
Failed A_second_application_stands_down_instead_of_opening_a_competing_overlay
Failed The_session_is_released_when_the_dashboard_exits
```

Stopping the dashboard and re-running the same three, unchanged: 3 passed, 0 failed. Both halves of
the report hold.

**After the change, against the installed dashboard.** On `e1eb69b`, the full solution — both
projects, nothing filtered — run six times while the installed copy held the session:

| run | `Vantage.Tests` | `Vantage.Tests.Shell` |
| --- | --- | --- |
| 1 | 143/143 | 85/87 — `Folding_away_from_full_screen_…` and `Leaving_full_screen_returns_to_the_edge_…` |
| 2 | 143/143 | 86/87 — `Folding_away_from_full_screen_…` |
| 3 | 143/143 | **87/87** |
| 4 | 143/143 | **87/87** |
| 5 | 143/143 | **87/87** |
| 6 | 143/143 | **87/87** |

Four consecutive clean runs of the whole solution with the installed dashboard up, which is the cell
as written. The three failures in runs 1 and 2 are all `RunningShellTests` geometry, all in the
pre-existing flake family measured below, and none is a single-instance test — those were 8 of 8 in
every run.

**The contrast.** The same full solution on the baseline `ba5e495`, with the same installed
dashboard still running, in the same session: 143/143 and 79/82, failing exactly the three tests
this ticket names. So what changed the outcome is this branch and not the machine.

**Nothing in the suite took the dashboard's session.** The installed dashboard was still running and
still holding `Local\Vantage.SingleInstance` after all seven full runs above, on the process id it
started with. It was started for these runs and stopped afterwards; no Vantage process was running
before or after.

**The earlier runs, against the scratch-state build.** On `3d07aeb`, three full runs: 143/143 and
86/86 twice, and once 85/86 on `Folding_away_from_full_screen_lands_small_against_the_edge_it_was_against`.
The baseline contrast under that dashboard was the same 79/82 on the same three tests. Superseded by
the installed-dashboard runs above rather than relied on.

## A pre-existing flake, which is not this ticket's and is not fixed here

`RunningShellTests`'s geometry tests fail intermittently, and runs 1 and 2 above are instances. It is
unrelated to the dashboard running and unrelated to this change:

| | `Collapsing_at_the_bottom_of_the_screen_keeps_the_ribbon_at_the_bottom`, alone, ten runs |
| --- | --- |
| `3d07aeb`, nothing running | 8 passed, 2 failed |
| baseline `ba5e495`, nothing running | 9 passed, 1 failed |

It fails on the assertion that a folded ribbon keeps its bottom edge, and on the matching one for
unfolding, by the same distance in opposite directions. Three tests in the family have now been seen
to do it: `Collapsing_at_the_bottom_of_the_screen_keeps_the_ribbon_at_the_bottom`,
`Folding_away_from_full_screen_lands_small_against_the_edge_it_was_against` and
`Leaving_full_screen_returns_to_the_edge_the_window_was_against`. The cold review's own full-suite
run hit the first of them independently.

Reported separately rather than absorbed here: this ticket is about a name collision, and a suite
that flakes for a second reason is a second ticket's subject.
