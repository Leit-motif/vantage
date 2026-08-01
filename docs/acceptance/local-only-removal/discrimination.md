# Discrimination checks

The acceptance cell *"no test asserts remote behaviour; every test that survived still asserts
something true"* has an easy half and a hard half. Which tests survived is an inventory, and it is
in the ticket. That each one still asserts something true is not something a passing suite can
show: a test repointed at a subject that cannot fail passes exactly as loudly as one that works.

So every test this change **added or repointed** was checked by breaking the behaviour it claims to
assert and requiring it to fail. Run on `3206168`, each mutation applied alone with `src/` restored
from a copy between runs; `git diff HEAD -- src tests tools` was empty afterwards and the solution
still built.

| # | test(s) | what was broken | result |
| --- | --- | --- | --- |
| 1 | `ConflictInspectionTests` — all four | `RefreshService.FallbackView` returns a fresh view instead of the cached one | **4 failed, 0 passed** |
| 2 | `A_snapshot_stored_before_items_carried_their_conflicts_still_offers_them` | `ProjectProjector.AttachConflictsToItems` returns the view untouched | **1 failed** |
| 3 | `Labels_assignments_and_blockers_each_report_their_own_kind_of_movement` | the label comparison in `RefreshService.DetectSemanticChanges` disabled | **1 failed** |
| 4 | `A_command_that_would_change_something_is_refused_rather_than_run` | the four `gh` reads put back on `ReadOnlyProcessRunner`'s allowlist | **1 failed** |
| 5 | `Every_read_the_dashboard_actually_makes_gets_through` | `log` dropped from the git verb allowlist | **1 failed** |
| 6 | `A_repository_with_a_github_remote_and_a_linked_ticket_starts_no_gh_process`, `A_run_over_a_repository_with_a_github_remote_issues_no_gh_command_at_all` | `GitAdapter.ReadAsync` issues a `gh auth status` | **2 failed** |
| 7 | `One_project_is_never_indexed_by_two_refreshes_at_once` | the per-project semaphore replaced with `Task.Yield()` | **1 failed** |
| 8 | `Registry_intent_recorded_under_a_linked_root_follows_the_project_to_its_resolved_path` | `IndexEntriesByResolvedPath` never matches | **1 failed** |

Twelve failures, no passes. Checked separately, the same way:

| test | what was broken | result |
| --- | --- | --- |
| `A_settings_file_carrying_the_removed_github_keys_loads_clean_and_is_rewritten_without_them` | `GitHubEnrichmentEnabled` reintroduced to `DashboardSettings` | **1 failed** |
| `An_ordinary_refresh_rewrites_a_migrated_settings_file_with_nothing_else_to_save` | the `MarkChanged()` in `SettingsStore.Migrate` disabled | **1 failed** |

Every one passes again once the mutation is reverted.

## What this does not cover

Number 1 is the important one and the reason this file exists. `ConflictInspectionTests` was
repointed from the deleted reconciler to the cache's last-known-good path, and the obvious worry is
that it had become a test of its own fixture — it seeds a view and then reads it back. Breaking
`FallbackView` fails all four, which is the answer: the seeded view reaches the shell through the
product's own path, and if that path stops working the tests say so.

Tests this change did **not** touch are not listed. They assert what they always did, against code
whose behaviour the before-and-after in this directory shows to be unchanged.
