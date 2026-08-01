# Evidence for `01-remove-the-github-adapter`

Two things the ticket's acceptance table cites, kept here because neither belongs in the test
suite: one is a measurement against a build that no longer exists on this branch, and the other is
a record of deliberately broken code.

## `ProjectionDump.cs` and the three dumps

`ProjectionDump.cs` is a **one-off measurement, not a test**, which is why it is here rather than
under `tests/`. It answers the acceptance cell *"the dashboard reports the same local evidence it
does today for a project with no remote"* by comparison rather than by proxy.

The method: drop this identical file into a worktree at the review baseline `22094b9` and into the
branch head, run it in both, diff the output.

```bash
git worktree add <somewhere>/baseline 22094b9
cp docs/acceptance/local-only-removal/ProjectionDump.cs <each>/tests/Vantage.Tests/RefreshSeam/
PROJECTION_DUMP=<somewhere>/dump-baseline.txt dotnet test tests/Vantage.Tests/Vantage.Tests.csproj \
  --filter "FullyQualifiedName~ProjectionDump"      # in the baseline worktree
PROJECTION_DUMP=<somewhere>/dump-head.txt dotnet test tests/Vantage.Tests/Vantage.Tests.csproj \
  --filter "FullyQualifiedName~ProjectionDump"      # in this one
diff dump-baseline.txt dump-head.txt
```

It compiles unchanged against both builds because it touches only fields both have. The
remote-only fields are absent from the dump deliberately — they are the thing being removed — and
every local conclusion is in it: per project the state and its reason, progress, pipeline, next
action and why, staleness, git availability, activity kinds and diagnostics; per ticket the status,
kind, actionability, blocked state, resolved blockers, link, source and provenance kind.

| file | what it is |
| --- | --- |
| `dump-baseline.txt` | the projection at `22094b9`, before any of this |
| `dump-head.txt` | the projection at the branch head — **byte-identical to the baseline** |
| `dump-mutated.txt` | the branch head with `ProjectProjector.CollectActivity` deliberately broken |

`dump-mutated.txt` is why the identical result means something. Without it, a dump that captured
too little would compare equal for the wrong reason. The mutation drops the local-commit loop from
the one method in the projector this change actually edited, and the comparison catches it:

```
-  activityKinds LocalCommit
+  activityKinds
```

The fixture has no remote anywhere in it, so the baseline build issues no `gh` call while producing
its half — the comparison is entirely offline, and nothing in it depends on the owner's account or
their live settings.

## `discrimination.md`

Every test this change added or repointed, and the deliberate break that makes each one fail. A
repointed test that still passes proves nothing on its own: it might have been repointed at
something that cannot fail. This records what was broken, and what happened when it was.
