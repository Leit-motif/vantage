# Discrimination: does each test fail when the thing it asserts is broken?

A test pointed at a subject that cannot fail passes exactly as loudly as one that works. Every
behaviour claimed by `.scratch/local-only/issues/03-conflicts-between-any-two-observations.md` was
therefore checked by breaking it and requiring the named test to notice.

Each mutation was applied **alone** on `1c24f98`, the named test run, and `src/` and `tests/`
restored from that commit before the next one. **Eleven mutations, eleven failures, no passes.** The
suite is green on the unmutated commit — `Vantage.Tests` 139/139 and `Vantage.Tests.Shell` 82/82.

Three of the eleven were first written as constant conditions (`if (false)`, an early `return
null`). This build treats warnings as errors, so unreachable code fails to compile and no test could
run; those three were rewritten to mutate a value rather than a constant, which is the stronger form
anyway — the code still runs, it just runs wrong.

| # | What was broken | Test required to fail | Result |
| --- | --- | --- | --- |
| 1 | The committed status is compared against itself, so producer one cannot see a status that differs | `A_ticket_edited_but_not_committed_disagrees_with_what_the_last_commit_recorded` | failed |
| 2 | Producer one reports the title on every modified file, differing or not | `An_uncommitted_edit_that_changes_no_stated_fact_reports_nothing` | failed |
| 3 | The working-tree comparison is never made at all | `A_ticket_edited_but_not_committed_disagrees_with_what_the_last_commit_recorded` | failed |
| 4 | Producer two stops requiring that every named edge is finished | `A_ticket_blocked_by_work_that_is_not_finished_reports_nothing` | failed |
| 5 | Producer two's guard is inverted, so it skips the tickets that say blocked | `A_ticket_that_still_says_blocked_over_finished_work_disagrees_with_what_it_names` | failed |
| 6 | Producer three stops requiring an open box | `Unticked_boxes_on_work_that_does_not_claim_to_be_finished_reports_nothing` | failed |
| 7 | Producer three's guard is inverted, so it skips finished tickets | `A_ticket_that_calls_itself_finished_over_unticked_boxes_disagrees_with_its_own_evidence` | failed |
| 8 | A disagreement stops travelling with the item it is about | `A_ticket_edited_but_not_committed_disagrees_with_what_the_last_commit_recorded` | failed |
| 9 | The cache keeps a projection written under the old side names | `A_projection_stored_under_the_old_side_names_is_dropped_rather_than_read_back_blank` | failed |
| 10 | `ObservedValue` reacquires a member called `RemoteValue` | `No_type_or_member_of_the_conflict_model_names_a_local_a_remote_or_github` | failed |
| 11 | The shell labels the two sides `local` and `remote` again instead of reading each side's provenance | `The_conflict_shown_on_an_item_names_both_values_the_resolution_and_each_side_s_provenance` | failed |

Rows 2, 4 and 6 are the ones worth naming: they are the *silence* checks. A producer that fires on
everything satisfies every positive test in the file, and only these three can tell the difference
between a conflict model and a second diagnostics list.

Row 10 is the acceptance cell "no type or field in the conflict model names a remote, a local, or
GitHub" made into something a build can check rather than something a reader concluded.

The scripts that applied the mutations are not kept: they are three lines of `perl -0pi` each, and
the table above names what every one of them changed.
