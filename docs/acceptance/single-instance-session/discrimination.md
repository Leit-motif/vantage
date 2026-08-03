# Discrimination: does each test fail when the thing it asserts is broken?

A test pointed at a subject that cannot fail passes exactly as loudly as one that works. Every
behaviour claimed by `.scratch/evidence-integrity/issues/02-single-instance-mutex.md` was therefore
checked by breaking it and requiring the named test to notice.

Each mutation was applied **alone** on `3d07aeb`, `SingleInstanceTests` run, and the file restored
from that commit before the next one. **Ten mutations, ten failures, no passes.** The suite is green
on the unmutated commit — `Vantage.Tests` 143/143 and `Vantage.Tests.Shell` 86/86.

| # | What was broken | Test required to fail | Result |
| --- | --- | --- | --- |
| 1 | The guard calls every process the first instance, so nothing ever stands down | `A_second_application_stands_down_instead_of_opening_a_competing_overlay` and `The_session_is_released_when_the_dashboard_exits` | failed, both |
| 2 | The guard calls no process the first instance | all three cross-process tests | failed, all three |
| 3 | `Dispose` returns while still holding the session | `The_session_is_released_when_the_dashboard_exits` | failed |
| 4 | The constructor takes the dashboard's session by default again | `The_session_the_dashboard_claims_is_the_one_named_for_the_dashboard` | failed |
| 5 | The product claims a session other than the one this repository records | `The_session_the_dashboard_claims_is_the_one_named_for_the_dashboard` | failed |
| 6 | An ordinary launch honours `--instance-name` too | `Only_the_probe_may_decide_about_a_session_other_than_the_dashboard_s` | failed |
| 7 | The probe ignores the session it is given and decides about the dashboard's | `Only_the_probe_may_decide_about_a_session_other_than_the_dashboard_s`, and both tests that hold a session | failed, all three |
| 8 | `TestSession` hands out the dashboard's own session | `The_names_this_suite_claims_are_its_own_and_never_repeat` | failed |
| 9 | A test constructs the guard without going through `TestSession` | `Nothing_in_this_suite_claims_a_session_except_through_the_name_it_owns` | failed |
| 10 | `TestSession` stops varying the name between claims | `The_names_this_suite_claims_are_its_own_and_never_repeat` | failed |

Rows 1 and 2 are the pair the ticket's second acceptance cell asks for, and they are recorded as a
pair because the cell's own wording does not survive contact with the subject. A guard that has
*stopped standing down* calls every process the first instance — and
`The_application_starts_normally_when_nothing_else_is_running` asserts exactly that a lone launch is
allowed to start, so it cannot fail on that mutation and should not. Row 2 breaks the decision in the
other direction and all three fail. Between them every one of the three is held, in the direction it
is actually about.

Rows 8, 9 and 10 are the third cell. Row 9 is the load-bearing one: it is what makes the claim about
the *suite* rather than about the three tests someone happened to look at, because it reads the
compiled assembly and fails on a construction of the guard anywhere but `TestSession`.

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

The dashboard holding the session throughout was this branch's own build, launched with
`--state <scratch>` so that it claims the real, session-wide `Local\Vantage.SingleInstance` while
writing its settings, cache and logs somewhere disposable. The owner chose this over their installed
copy. It is the same executable and the same name, which is all the collision depends on.

**The defect, reproduced first.** On `d14fb0d`, whose `src/` and `tests/` are identical to the review
baseline `ba5e495`, with that dashboard running:

```
Failed The_application_starts_normally_when_nothing_else_is_running
  Expected:<0>. Actual:<2>. With no dashboard running, the application must take the session for itself.
Failed A_second_application_stands_down_instead_of_opening_a_competing_overlay
Failed The_session_is_released_when_the_dashboard_exits
```

Stopping the dashboard and re-running the same three, unchanged: 3 passed, 0 failed. Both halves of
the report hold.

**After the change.** On `3d07aeb`, the full solution — both projects, nothing filtered — run three
times with the dashboard up:

| run | `Vantage.Tests` | `Vantage.Tests.Shell` |
| --- | --- | --- |
| 1 | 143/143 | 86/86 |
| 2 | 143/143 | 85/86 — `Folding_away_from_full_screen_lands_small_against_the_edge_it_was_against` |
| 3 | 143/143 | 86/86 |

**The contrast.** The same full solution on the baseline `ba5e495`, with the same dashboard still
running, in the same session: 143/143 and 79/82, failing exactly the three tests above. So what
changed the outcome is this branch and not the machine.

**Nothing in the suite took the dashboard's session.** The holder process was still running and
still holding `Local\Vantage.SingleInstance` after all four full runs above, on the process id it
started with.

## A pre-existing flake, which is not this ticket's and is not fixed here

`RunningShellTests`'s geometry tests fail intermittently, and run 2 above is one instance. It is
unrelated to the dashboard running and unrelated to this change:

| | `Collapsing_at_the_bottom_of_the_screen_keeps_the_ribbon_at_the_bottom`, alone, ten runs |
| --- | --- |
| `3d07aeb`, nothing running | 8 passed, 2 failed |
| baseline `ba5e495`, nothing running | 9 passed, 1 failed |

It fails on the assertion that a folded ribbon keeps its bottom edge, and on the matching one for
unfolding, by the same distance in opposite directions. Two tests in the family have been seen to do
it. Reported separately rather than absorbed here: this ticket is about a name collision, and a
suite that flakes for a second reason is a second ticket's subject.
