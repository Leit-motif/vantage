# Discrimination: does each test fail when the thing it asserts is broken?

A test pointed at a subject that cannot fail passes exactly as loudly as one that works. Every
behaviour claimed by `.scratch/local-only/issues/03-conflicts-between-any-two-observations.md` was
therefore checked by breaking it and requiring the named test to notice.

Each mutation was applied **alone** on `2263d5a`, the named test run, and `src/` and `tests/`
restored from that commit before the next one. **Fifteen mutations, fifteen failures, no passes.**
The suite is green on the unmutated commit — `Vantage.Tests` 142/142 and `Vantage.Tests.Shell`
82/82.

| # | What was broken | Test required to fail | Result |
| --- | --- | --- | --- |
| 1 | The committed status is compared against itself, so producer one cannot see a status that differs | `A_ticket_edited_but_not_committed_disagrees_with_what_the_last_commit_recorded` | failed |
| 2 | Producer one reports the title on every modified file, differing or not | `An_uncommitted_edit_that_changes_no_stated_fact_reports_nothing` | failed |
| 3 | The working-tree comparison is never made at all | `A_ticket_edited_but_not_committed_disagrees_with_what_the_last_commit_recorded` | failed |
| 4 | Producer two stops requiring that every named edge is finished | `A_ticket_blocked_by_work_that_is_not_finished_reports_nothing` | failed |
| 5 | Producer two's guard is inverted, so it skips the tickets that say blocked | `A_ticket_that_still_says_blocked_over_finished_work_disagrees_with_what_it_names` | failed |
| 6 | Producer three stops requiring an open box | `Unticked_boxes_that_are_not_acceptance_report_nothing` | failed |
| 7 | Producer three's guard is inverted, so it skips finished tickets | `A_ticket_that_calls_itself_finished_over_unticked_boxes_disagrees_with_its_own_evidence` | failed |
| 8 | A disagreement stops travelling with the item it is about | `A_ticket_edited_but_not_committed_disagrees_with_what_the_last_commit_recorded` | failed |
| 9 | The cache keeps a projection written under the old side names | `A_projection_stored_under_the_old_side_names_is_dropped_rather_than_read_back_blank` | failed |
| 10 | `ObservedValue` reacquires a member called `RemoteValue` | `No_type_or_member_of_the_conflict_model_names_a_local_a_remote_or_github` | failed |
| 11 | The shell labels the two sides `local` and `remote` again instead of reading each side's provenance | `The_conflict_shown_on_an_item_names_both_values_the_resolution_and_each_side_s_provenance` | failed |
| 12 | The acceptance tally goes back to counting every checkbox in the document | `Unticked_boxes_that_are_not_acceptance_report_nothing` | failed |
| 13 | Several satisfied edges collapse into one row attributed to the first | `Each_satisfied_edge_is_reported_against_the_file_that_states_it` | failed |
| 14 | A `git status` that fails on its own is swallowed instead of reported | `A_working_tree_git_could_not_report_is_named_rather_than_passed_over` | failed |
| 15 | The committed blob's size is never checked before it is read | `A_committed_copy_too_large_to_read_is_skipped_and_reported` | failed |

Rows 2, 4, 6 and 12 are the ones worth naming: they are the *silence* checks. A producer that fires
on everything satisfies every positive test in the file, and only these can tell the difference
between a conflict model and a second diagnostics list.

Row 10 is the acceptance cell "no type or field in the conflict model names a remote, a local, or
GitHub" made into something a build can check rather than something a reader concluded.

Rows 12 to 15 hold the four behaviours added in response to the cold review, so a fix made for a
review finding is measured the same way as the original work.

## Two mutations that had to be rewritten

Both are recorded because a mutation that fails to break anything is the same mistake as a test that
fails to check anything, and neither is visible from a green run.

**Constant conditions do not compile here.** Three mutations were first written as `if (false)` or an
early `return null`. This build treats warnings as errors, so the unreachable code that follows
fails the build and no test can run. They were rewritten to mutate a value rather than a constant —
comparing a status against itself, inverting a guard — which is the stronger form anyway: the code
still runs, it just runs wrong.

**Row 4's first attempt was equivalent, and passed.** It replaced producer two's `!resolution.CanMove`
with `!resolution.HasInvalidBlocker`. On the fixture in question the blocker is open but valid, so
both expressions are true and the mutant behaves exactly like the original — the test passed, and
the run was right to. Removing the clause outright is the mutation that actually weakens the guard,
and the test fails on it. The lesson is the obvious one: a passing mutation means either the test is
weak or the mutation is, and the second has to be ruled out first.

The scripts that applied the mutations are not kept: they are one `perl -0pi` expression each, and
the table above names what every one of them changed.
