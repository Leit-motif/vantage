# Publishing

Vantage is developed in a private repository and published to a public one. The private repo holds
everything, including `.scratch/` — the specifications, tickets and decision record the agent
workflow hands off through. The public repo is the portfolio: the product, its documentation, and
the acceptance evidence, with none of the planning.

## Why two repositories rather than one with an ignore rule

`.scratch/` has to be **committed**. It is the tracker, and the whole argument for using files
rather than an issues API is that a ticket is written atomically, read immediately by the next
agent, and carries git's time and attribution. Untracking it would break the handoff model the
project is built on.

It does not have to be **pushed anywhere public**. But git pushes whole commits — a path cannot be
withheld from a push — so "commit it but don't publish it" necessarily means two histories. The
public one is *produced* from the private one, which is what `tools/Publish/Publish-Public.ps1`
does.

Local-only was considered and rejected: the decision record is the least reproducible thing in the
repository, and keeping it on exactly one disk is not a backup strategy. It lives in a private
remote instead.

## One-time setup

**Order matters.** GitHub keeps a redirect from a renamed repository, and creating a new repo under
the old name is what releases it. Renaming first means the old public URL ends up pointing at the
new public repo, which is what you want.

1. Rename the existing repository to `vantage-internal` and set it **private**. Nothing moves; the
   exposure stops at that moment.
2. **Immediately** repoint the working clone, before the next step:

   ```
   git remote set-url origin https://github.com/Leit-motif/vantage-internal.git
   ```

   This one is not housekeeping. `origin` still reads
   `github.com/Leit-motif/vantage.git`, which GitHub resolves by redirect while no repository holds
   that name. Create the new public repo first and that name resolves to *it* — so the next
   ordinary `git push origin main` would push the entire private history, planning tree included,
   straight to the public repo. Repoint first and the hazard cannot arise.

3. Create a new **public** repository named `vantage`. Leave it empty — no README, no licence, no
   `.gitignore`. The first publish force-pushes a filtered history and an initial commit would only
   be in the way.

4. Confirm the filter is present. It is installed on the author's machine already:

   ```
   git filter-repo --version
   ```

   If it is missing, `pip install git-filter-repo`. Note that `git filter-repo --help` exits 128 on
   a normal Windows git install — it looks for an HTML doc page that was never shipped — so
   `--help` is not a usable test for whether it is there.

## Publishing

```
./tools/Publish/Publish-Public.ps1 -WhatIf   # produce the filtered history, report, do not push
./tools/Publish/Publish-Public.ps1           # and push it
```

Everything happens in a throwaway clone under the temp directory. The script refuses to run against
a dirty tree, and refuses to push if any excluded path survives the filter — a mirror that quietly
published the planning tree would be worse than no mirror.

The filter is deterministic, so a later publish fast-forwards rather than rewriting every SHA. It
drops the commits that touched only `.scratch` and rewrites the ones that touched both it and code.

## What is not filtered

**Commit messages.** A message that describes a ticket is published as written. That is usually
fine and often reads well, but a message is the one place a private detail can still reach the
public repo, so it is worth a thought when writing one.

## Keeping the public repo honest

Two things in the public tree describe the private one, and both should stay true:

- README's *How it was built* section says the specifications live in a separate planning
  repository and are not published. Do not re-add links into `.scratch/` — they will 404 publicly.
  Descriptive mentions of `.scratch` are fine and correct: the **product** reads that layout, and
  that is a feature, not a reference to this repository's own planning.
- `docs/agents/issue-tracker.md` documents where tickets live for an agent working *in the private
  repo*. It is instructions, not a link, and stays accurate.
