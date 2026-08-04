# Public release checklist

The audit performed before making this work public: what was checked, what it found, what was
decided, and what is left. Recorded so the decision is reviewable rather than remembered.

**Closed on 2026-08-03.** Every box below is ticked. The plan it was written against was replaced a
day after it was written — see [how this was superseded](#how-this-was-superseded-2026-08-01) before
acting on anything here.

Audited on 2026-07-31 at `2e46811`, against 71 commits across all branches, and against all 11
issues and 3 pull requests — 45 thread bodies, comments and reviews, roughly 185,000 characters.

---

## Verdict

**Ready with caveats.** No credential, token, key, or third-party private data is present in the
working tree, in any commit, or in any tracker thread. No name of a private project the dashboard
monitors appears anywhere.

What remains are decisions, not defects. One — the author's email on historical commits — is
resolved by [the history rewrite](#the-history-rewrite-as-performed), which has been done. The rest are accepted risks recorded below.

---

## Checks run

| Check | Command | Result |
| --- | --- | --- |
| Build | `dotnet build Vantage.slnx -c Release` | 0 errors, 0 warnings, `TreatWarningsAsErrors` on |
| Tests | `dotnet test Vantage.slnx -c Release --no-build` | 229 passed, 0 failed, 0 skipped |
| Shipping publish | `dotnet publish src/Vantage.App -p:PublishProfile=win-x64 -c Release` | produced `Vantage.exe`, 76.8 MB |
| Credential scan, all history | `git grep -E '<token patterns>' $(git rev-list --all)` | no match |
| Personal identifiers, all history | `git grep -E 'C:\\Users\\…' $(git rev-list --all)` | finding 1 |
| Contact details, all history | phone and consumer-mail patterns | no match in file content |
| Tracker threads | every issue and PR body, comment and review | finding 4 |
| Tracked build output | `git ls-files \| grep -E '\.(dll\|exe\|pdb\|trx)$\|/bin/\|/obj/'` | none |
| Ignore rules vs tracked files | `git ls-files -i -c --exclude-standard` | none caught |
| Large tracked files | `git ls-files \| xargs ls -l`, >500 KB | none remaining |
| README links | every local link resolved against the tree | all resolve |
| Working tree | `git status --short` | clean |

**No dedicated secret scanner was run.** `gitleaks`, `trufflehog`, `git-secrets` and
`detect-secrets` are not installed on this machine, and GitHub's own secret scanning is disabled for
this repository. What ran was a pattern scan over every commit for GitHub tokens, OpenAI and AWS
keys, Slack tokens, PEM private-key headers, and assigned `password` / `api_key` / `secret` /
`Bearer` literals. That is weaker than a real scanner and should be read as such. Enable GitHub's
own scanning once the repository is public, where it is free.

---

## Findings

### 1. Windows account name in git history · low · resolved by the rewrite

The shipped default discovery roots were two absolute paths on the author's machine. Both were
fixed at `HEAD` before the rewrite — the default is now empty — but they remained in **62 of 71**
commits, in `src/…/Settings/DashboardSettings.cs` and
`tests/…/RefreshSeam/ReadOnlyAcceptanceTests.cs`, and in the bodies of issues #1 and #10.

The tracker bodies were edited. The history was handled by the rewrite's text-replacement pass,
which substitutes placeholder paths in file content *and* in commit messages — see
[the rewrite section](#the-history-rewrite-as-performed) for why including commit messages is what
made this safe to do at all.

### 2. Author email on historical commits · low · resolved by the rewrite

70 of 71 commits carried the author's personal address. Publishing would have made it visible and
scrapeable on every one of them. The address is deliberately not reproduced here — a document that
records the removal should not be the thing that republishes it.

Three parts, two already done:

- **Future commits** — this repository's local `user.email` is now
  `172229106+Leit-motif@users.noreply.github.com`. Setting it `--global` is the owner's call.
- **Enforcement** — GitHub **Settings → Emails**, *Keep my email addresses private* and *Block
  command line pushes that expose my email*. The second makes GitHub reject any push carrying the
  real address, which is the durable fix. **Still to do.**
- **Historical commits** — only a rewrite. See below.

### 3. Pre-implementation concept art · resolved

`docs/design/*.png` was 3.2 MB of mockups that nothing referenced, showing a dashboard that was
imagined rather than built. Removed at `HEAD`, and the blobs purged from history by the rewrite —
which is most of why the pack went from 3.78 MiB to 730 KiB.

### 4. Tracker threads · low · audited, publishable

Publishing a repository publishes its tracker, and these threads were largely agent-written against
a live workspace, which made them the most likely place for a leak. They are clean.

| Looked for | Found |
| --- | --- |
| Tokens, keys, credentials | none |
| Email addresses | none |
| Unix home paths | none |
| Links to other repositories | one, to this repository under its old name |
| **Names of private projects the dashboard monitors** | **none** |
| Windows paths | the same account name, in #1 and #10 — edited out, see finding 1 |

One comment on #5 described an unrelated hobby project of the owner's by name while establishing
that a tree change during a live run came from outside the dashboard. It never named the project
itself, so this was presentation rather than privacy — but the repository is public-facing, so the
reference was removed and the same passage in `docs/acceptance/README.md` was reworded. The
evidence is unchanged: what mattered was that the writer was external and the file types were ones
the dashboard has no code path to touch.

Note what editing a thread does and does not achieve. GitHub keeps an edit history that anyone with
read access can open, which on a public repository is everyone. Editing removes text from the
default view — which is what almost every reader sees — but leaves the original retrievable by
anyone who clicks through. Only deleting a comment and reposting a clean one removes it outright,
and an issue body cannot be deleted without deleting the issue.

### 5. No `.gitattributes` · informational

Every commit produces `LF will be replaced by CRLF` warnings. Nothing is broken and the software is
Windows-only, so the practical impact is nil. `* text=auto` would silence it.

---

## Cleared

- **No credentials, tokens, keys, or connection strings** in the working tree, in any commit, or in
  any tracker thread.
- **No `.env`, database, log, or settings file has ever been tracked.** Verified against added-file
  history, not just the current tree.
- **Acceptance evidence is sanitized by construction.** Project identities are hashed
  (`"ProjectId": "2b2d4e7da617"`); no repository name, remote, or personal path appears in any
  record. The one path that does appear is `C:\Users\Public\mwd-visual-fixture`, under the public
  profile deliberately — the expanded layout prints a project path, so a fixture under a personal
  profile would have put the account name into every wide frame.
- **No content from monitored private repositories** appears anywhere. The visual fixture is
  invented; the acceptance records report counts and hashes rather than names.
- **The shipped defaults no longer reference the author's machine**, and a test asserts that a fresh
  `DashboardSettings` names no directory at all.
- **Ignore rules cover** the cache database, logs, capture output, test results, dotenv files, and
  local agent scratch, and no tracked file is caught by any of them.

---

## The history rewrite, as performed

Run with `git filter-repo` on a fresh clone, so no existing worktree was disturbed. Three
operations, none of which changes a claim the history makes:

1. **Author and committer email** on every commit rewritten to
   `172229106+Leit-motif@users.noreply.github.com`, via `--mailmap`.
2. **`docs/design/*.png` purged** from every commit. All three commits that only touched those
   files became empty and were dropped: the two that added the art, and the one that removed it.
3. **Text replaced** in both file content and commit messages — the author's account name and
   second discovery root became placeholder paths, the personal address became the noreply one, and
   the references to an unrelated hobby project were reworded.

The third was initially ruled out, on the grounds that purging source content would leave the commit
that fixed the shipped defaults fixing a value present nowhere. Applying the same substitutions to
commit messages removes that objection: the history now reads coherently throughout, with a
placeholder where a machine-specific path used to be.

### The acceptance stamps survived

This is the reason the rewrite was done once and late rather than early or repeatedly. Each
acceptance record is stamped with the commit it was made at, and a rewrite changes every SHA.
Remapped through filter-repo's `commit-map`, each one still points at the same commit it always did:

| Record | Was | Now | Commit |
| --- | --- | --- | --- |
| `keystroke-witness.json` | `d12a93b0d` | `0c00d95c3` | test: retire the idle-desktop split and record what the shell seam proves |
| `local-only-default.json` | `920a732` | `81de45d` | fix: report the skipped offline gh probe as unanswered, not observed |
| `read-only-scan.json` | `2cbd3f271` | `d1ebdeaa1` | test: close the second audit round on execution and measurement |
| `running-shell-gaps.json` | `a6ac2827a` | `7b04175a9` | test: instrument the running shell so a live Windows change can be read back |
| `visual-acceptance.json` | `c7d885d2d` | `65b83c1c7` | test: prove the running-shell gaps that needed the owner at the machine |
### Result

| | Before | After |
| --- | --- | --- |
| Commits | 77 | 74 |
| Pack size | 3.78 MiB | 730 KiB |
| Distinct author addresses | 2 | 1, the noreply address |
| Sensitive strings in blobs or messages | several | none |

**What it cost.** Every SHA changed. Merged PR #12's page references commits that no longer exist,
and any SHA quoted in an issue comment is now a dead link. Both are cosmetic, and both were confined
to a repository nobody had cloned — which is exactly why this was the last cheap moment to do it.

---

## How this was superseded, 2026-08-01

**This checklist was written for a plan that was replaced the next day.** It assumes one repository
whose visibility gets flipped from private to public. What actually happened is the two-repository
split in `.scratch/publication/spec.md`: development stays in the private `vantage-internal`, and
the public `vantage` is *produced* from it by `tools/Publish/Publish-Public.ps1`, with the planning
tree filtered out of the published history entirely.

So "flip visibility" was never performed and never will be — there is no single repository to flip.
Everything the checklist audited still holds, because the same commits reach the public repo; what
changed is the mechanism, and that the private repo keeps `.scratch` rather than the public one
inheriting it.

Read the audit above as the record of what was checked before anything went public. Read
`docs/publishing.md` for how publishing works now.

## Closing out

- [x] Audit the working tree and full history for secrets and personal data
- [x] Audit all issue and pull-request threads
- [x] Remove the author's machine from shipped defaults
- [x] Remove unreferenced concept art
- [x] State the copyright posture (`LICENSE`)
- [x] Set this repository's commit email to the GitHub noreply address
- [x] Enable *Block command line pushes that expose my email* in GitHub email settings —
      confirmed set by the owner, 2026-08-03
- [x] Land all branches into `main`
- [x] Run the history rewrite and re-stamp the acceptance records
- [x] Confirm `LICENSE` reads the way the owner wants. Reworded on 2026-08-03 into external-facing
      language: it had described the work as published "for portfolio review, technical evaluation,
      and educational inspection", which framed it as a submission rather than as software with
      terms. The terms are unchanged. It remains a plain-language statement, not legal advice, and
      nobody qualified has reviewed it — that caveat stands.
- [x] ~~Flip visibility~~ — superseded by the two-repository split; see above
- [x] Enable **secret scanning** and **push protection** on the public repository — confirmed on by
      the owner, 2026-08-03. The line above once read "off today"; that was true of the *private*
      repository before the split, and it misled a reader on 2026-08-03 into asking the owner to
      enable something already enabled.
- [x] Confirm the README's issue links resolve and the old repository slug still redirects. The
      README carries no GitHub links at all — the tracker moved into `.scratch/`, which is filtered
      out of the public history, so there is nothing left to redirect.
