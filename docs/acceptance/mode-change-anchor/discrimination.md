# Discrimination and rates: the mode change that moved on a later callback

Evidence for `.scratch/evidence-integrity/issues/04-ribbon-anchor-race.md`.

## What the deterministic evidence is, and what the rates are for

The claim this ticket rests on is not "the failures stopped". A rate going to zero is what a widened
race looks like as well as what a closed one looks like, and this suite had already produced one
false all-clear from exactly that (see *What the diagnosis cost*, below).

The load-bearing evidence is that the property is now held by a test that cannot flake:
`Folding_the_ribbon_moves_the_window_in_the_same_step_that_resizes_it` reads the window **inside the
dispatcher operation that changes it**, where nothing the window queued for later can run. It fails
5 of 5 runs with the move deferred and passes 5 of 5 with it not deferred. The rates below corroborate
that; they do not carry it.

## Discrimination

Each mutation applied **alone** on `c5c3041`, `RunningShellTests` run, the file restored from that
commit before the next. **Four mutations, four failures, no passes.** The suite is green on the
unmutated commit — `Vantage.Tests` 143/143 and `Vantage.Tests.Shell` 88/88.

| # | What was broken | Tests required to fail | Result |
| --- | --- | --- | --- |
| A | The move is deferred to a later dispatcher callback again | `Folding_the_ribbon_moves_the_window_in_the_same_step_that_resizes_it` | failed |
| B | The window stops holding its bottom edge | `Collapsing_at_the_bottom_…` and `Folding_the_ribbon_moves_…` | failed, both |
| C | The window stops holding its right edge | `Changing_the_view_grows_away_…`, `Folding_away_from_full_screen_…`, `Leaving_full_screen_…` | failed, all three |
| D | The anchor is read after the window has changed shape rather than before | `Folding_away_from_full_screen_…` and `Leaving_full_screen_…` | failed, both |

Row A is the ticket's second acceptance cell, and it is the one that matters: it restores exactly the
behaviour this change removes, and the new test alone notices. Rows B to D are there because a test
that only catches the deferral would not notice the anchor being wrong in every other way.

## Rates, before and after

One test, run alone, which is the configuration the fault shows up in.

| | runs | failed |
| --- | --- | --- |
| **Before** — `3d07aeb` | 10 | 2 |
| **Before** — `ba5e495` | 10 | 1 |
| **Before** — `f9734fd` | 20 | 1 |
| **Before, pooled** | **40** | **4** |
| **After** — `c5c3041` | **40** | **0** |

The before-rate is low and unstable — 2 in 10, then 1 in 10, then 1 in 20 — which is worth stating
plainly, because it is why a handful of green runs would have proved nothing. Forty after-runs
against a pooled 10% is the sample that makes zero mean something, and it is still only corroboration
of the deterministic result above.

Two further before-observations, both from the previous ticket rather than manufactured here: on
`e1eb69b`, six full-solution runs with the installed dashboard produced three geometry failures
across two of them; and the cold review of that ticket hit `Collapsing_at_the_bottom_…` on its own
independent full-suite run.

After, at the whole-solution level, on the final code (`c5c3041`; `git diff --name-only c5c3041..HEAD
-- src tests` is empty): **twelve full-solution runs, and the four geometry tests passed in all
twelve.** Eleven of the twelve were clean outright at 143/143 and 88/88.

The twelfth failed one test — `Persisting_registry_intent_writes_only_under_the_dashboard_s_own_app_data`,
in the portable suite. It is not this change and cannot be: `Vantage.Tests` targets `net10.0` and so
cannot reference the `net10.0-windows` project this change edits, which is the property
`docs/testing.md` says the split exists to give. It did not recur in 12 runs of that test alone, 6
runs of the whole portable suite, or the 6 further full-solution runs after it, so no rate is claimed
for it beyond "seen once, under the full-solution run, where both assemblies run at once". Reported
separately rather than absorbed here.

An earlier six consecutive clean full-solution runs were taken on `a4ac182`, before the layout pass
was removed. They are superseded by the twelve above and not relied on — the cold review was right
that runs on the previous implementation cannot close a cell about this one.

## Three tests that ran, and one filter that measures nothing

Running the three affected geometry tests **together** produced 0 failures in 10 runs on the *before*
commit. That is not evidence the fault was absent; it is evidence that filter does not exercise it —
more work in the process changes the scheduling that the race turns on. Recorded because a
before-measurement that cannot show the fault would have made any after-measurement look like a fix.

Everything in the table above therefore uses the single-test configuration on both sides.

## What the diagnosis cost, and what it got wrong on the way

Three wrong turns, recorded rather than tidied away, because each one produced a result that looked
like an answer.

- **The first hypothesis was wrong, and the instrumentation said so.** The guess was that the anchor
  was computed against a stale height. Logging `Reanchor`'s inputs showed a *failing* run computing
  exactly the same correct values as every passing one, which ruled the arithmetic out and pointed at
  the window being read between the resize and the move.
- **Instrumentation heavy enough to log to a file made the failure vanish** — 0 in 16 with no fix at
  all. That is a genuine result about the mechanism, and it is also exactly what a fix looks like from
  the outside. Any rate measured with instrumentation in place was discarded.
- **A test that drained the dispatcher to `Render` priority did not discriminate**: it passed 5 of 5
  against the unfixed code, because the dispatcher usually runs the queued move before another thread
  can look. That is what forced the final design — ask inside the operation, where nothing queued can
  run.

And one process failure worth recording. A discrimination run reported the fix failing 5 of 5 on its
own test. The cause was that `git checkout --` had reverted the fix, which was **not yet committed**,
so both halves of that comparison measured the same unfixed code. It cost a round and it was the
recorded hazard for this exact command. The fix was committed before any mutation ran afterwards, and
`UpdateLayout` was measured only after that.

## One line that was written and then removed

The first fix forced a layout pass before anchoring, on the assumption the ribbon's content-driven
height would not be known otherwise. Mutating it away left every geometry test green, so it was
measured properly — ten runs of four geometry tests without it, zero failures — and removed rather
than kept as something no test held.

What that leaves is one assumption in the changed line: the sizes set above are already applied by
the time the anchor runs, the ribbon's own included. It is not taken on trust. The regression test
requires the fold to have happened in the same read that checks the edge, so if it ever stops being
true, a test fails rather than the owner seeing the jump.
