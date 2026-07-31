# Public release checklist

The audit performed before making this repository public: what was checked, what it found, what was
decided, and what is left. Recorded so the decision is reviewable rather than remembered.

Audited on 2026-07-31 at `2e46811`, against 71 commits across all branches, and against all 11
issues and 3 pull requests — 45 thread bodies, comments and reviews, roughly 185,000 characters.

---

## Verdict

**Ready with caveats.** No credential, token, key, or third-party private data is present in the
working tree, in any commit, or in any tracker thread. No name of a private project the dashboard
monitors appears anywhere.

What remains are decisions, not defects. One — the author's email on historical commits — is
resolved by [the planned history rewrite](#the-planned-history-rewrite), which must happen before
publication. The rest are accepted risks recorded below.

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

### 1. Windows account name in git history · low · accepted, deliberately not purged

The shipped default discovery roots were two absolute paths on the author's machine, one of them
under `C:\Users\Example`. Both are fixed at `HEAD` — the default is now empty — but they remain in
**62 of 71 commits**, in
`src/…/Settings/DashboardSettings.cs` and `tests/…/RefreshSeam/ReadOnlyAcceptanceTests.cs`, and in
the bodies of issues #1 and #10.

The planned rewrite will not remove them, on purpose. Rewriting file *content* would leave the
history incoherent: the commit *"fix: take the author's machine out of the shipped defaults"* would
be fixing a value that appears nowhere in the repository it supposedly fixed. That is a worse
artifact than the disclosure, which is a pseudonymous local account handle on one machine, in a
repository that names its author openly in `NOTICE.md`.

Rewriting commit *metadata* and deleting *unreferenced binaries* leave every claim the history makes
intact. Editing what the historical source said does not. Only the first two are in scope.

### 2. Author email on historical commits · low · resolved by the rewrite

70 of 71 commits are authored by `Amrit Chana <172229106+Leit-motif@users.noreply.github.com>`. Publishing makes that
address visible and scrapeable on every one of them.

Three parts, two already done:

- **Future commits** — this repository's local `user.email` is now
  `172229106+Leit-motif@users.noreply.github.com`. Setting it `--global` is the owner's call.
- **Enforcement** — GitHub **Settings → Emails**, *Keep my email addresses private* and *Block
  command line pushes that expose my email*. The second makes GitHub reject any push carrying the
  real address, which is the durable fix. **Still to do.**
- **Historical commits** — only a rewrite. See below.

### 3. Pre-implementation concept art · resolved

`docs/design/*.png` was 3.2 MB of mockups that nothing referenced, showing a dashboard that was
imagined rather than built. Removed at `HEAD`; the blobs come out of history in the rewrite.

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
| Windows paths | the same account name, in #1 and #10 — see finding 1 |

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

## The planned history rewrite

Two operations, both metadata or unreferenced data, neither touching what the source said:

1. Rewrite author and committer email on all commits to
   `172229106+Leit-motif@users.noreply.github.com`.
2. Purge `docs/design/*.png` blobs from every commit.

**Sequence matters.** Do it last, on one branch, after everything has landed:

1. Finish the remaining prep work.
2. Merge all branches into `main` via pull request; delete the merged branches.
3. Rewrite `main` alone with `git filter-repo`.
4. Remap the five acceptance stamps using filter-repo's old→new commit map, in a follow-up commit
   that records the mapping. The stamps are the reason the rewrite is done once and late.
5. Force-push `main`; prune stale worktrees.
6. Flip visibility.

**What it costs.** Every SHA changes. The two concept-art commits disappear entirely — they added
nothing else. Merged PR #12's page will reference commits that no longer exist, and any SHA quoted
in an issue comment becomes a dead link. All cosmetic, and all confined to a repository nobody has
cloned — which is exactly why this is the last cheap moment to do it.

---

## Before flipping visibility

- [x] Audit the working tree and full history for secrets and personal data
- [x] Audit all issue and pull-request threads
- [x] Remove the author's machine from shipped defaults
- [x] Remove unreferenced concept art
- [x] State the copyright posture (`NOTICE.md`)
- [x] Set this repository's commit email to the GitHub noreply address
- [ ] Enable *Block command line pushes that expose my email* in GitHub email settings
- [ ] Land all branches into `main`
- [ ] Run the history rewrite and re-stamp the acceptance records
- [ ] Confirm `NOTICE.md` reads the way the owner wants. It is a plain-language statement of terms,
      not legal advice, and nobody qualified has reviewed it.
- [ ] Flip visibility
- [ ] Enable **secret scanning** and **push protection** — free on public repositories, off today
- [ ] Confirm the README's issue links resolve and the old repository slug still redirects
