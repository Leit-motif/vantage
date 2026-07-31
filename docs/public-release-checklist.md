# Public release checklist

The audit performed before making this repository public, what it found, and what it could not
decide on the owner's behalf. Recorded so the decision is reviewable rather than remembered.

Audited at `cc15781` on 2026-07-31, against 69 commits across all branches.

---

## Verdict

**Ready with caveats.** No credential, token, key, or third-party private data is present in the
working tree or in any commit. Two items are accepted risks the owner has to sign off rather than
defects, and one gate — reading the issue and pull-request threads — must be done before visibility
is flipped, because publishing a repository publishes its tracker, not just its code.

---

## Checks run

| Check | Command | Result |
| --- | --- | --- |
| Build | `dotnet build Vantage.slnx -c Release` | 0 errors, 0 warnings, with `TreatWarningsAsErrors` on |
| Tests | `dotnet test Vantage.slnx -c Release --no-build` | 229 passed, 0 failed, 0 skipped |
| Shipping publish | `dotnet publish src/Vantage.App -p:PublishProfile=win-x64 -c Release` | produced `Vantage.exe`, 76.8 MB |
| Credential scan, all history | `git grep -E '<token patterns>' $(git rev-list --all)` | no match |
| Personal identifiers, all history | `git grep -E 'C:\\Users\\…' $(git rev-list --all)` | one finding, below |
| Contact details, all history | phone and consumer-mail patterns | no match in file content |
| Tracked build output | `git ls-files \| grep -E '\.(dll\|exe\|pdb\|trx)$\|/bin/\|/obj/'` | none |
| Ignore rules vs tracked files | `git ls-files -i -c --exclude-standard` | none caught |
| Large tracked files | `git ls-files \| xargs ls -l`, >500 KB | two, below |
| README links | every local link resolved against the tree | all resolve |
| Working tree | `git status --short` | clean |

**No dedicated secret scanner was run.** `gitleaks`, `trufflehog`, `git-secrets` and
`detect-secrets` are not installed on this machine, and GitHub's own secret scanning is disabled
for this repository. What ran was a pattern scan over every commit for GitHub tokens, OpenAI and
AWS keys, Slack tokens, PEM private-key headers, and assigned `password` / `api_key` / `secret` /
`Bearer` literals. That is weaker than a real scanner and should be treated as such.

---

## Findings

### 1. Windows account name in git history · low · accepted

`C:\Users\Example\Workspaces` was the shipped default discovery root. It is fixed at `HEAD` — the
default is now empty — but it remains in **60 of 69 commits**, in
`src/…/Settings/DashboardSettings.cs` and `tests/…/RefreshSeam/ReadOnlyAcceptanceTests.cs`.

Deleting the current file does not remove it from history. Removing it would require rewriting
every commit that touches those paths, which in practice means all of them.

**Recommendation: do not rewrite.** The exposure is a pseudonymous local account handle on one
machine, and the repository already names its author openly in `NOTICE.md`. The cost is
disproportionate and specific to this repository: five acceptance records are stamped with the
commit they were made at, and all five stamps currently resolve —

| Record | Stamp | Resolves to |
| --- | --- | --- |
| `keystroke-witness.json` | `d12a93b` | *retire the idle-desktop split and record what the shell seam proves* |
| `local-only-default.json` | `920a732` | *report the skipped offline gh probe as unanswered, not observed* |
| `read-only-scan.json` | `2cbd3f2` | *close the second audit round on execution and measurement* |
| `running-shell-gaps.json` | `a6ac282` | *instrument the running shell so a live Windows change can be read back* |
| `visual-acceptance.json` | `c7d885d` | *prove the running-shell gaps that needed the owner at the machine* |

A history rewrite changes every SHA and orphans all five. It would trade a pseudonymous username
for the evidence chain that is the most valuable thing in the repository.

### 2. Author email on every commit · low · owner's decision

All 69 commits are authored by `Amrit Chana <172229106+Leit-motif@users.noreply.github.com>`. Making the repository public
makes that address publicly visible and scrapeable on every commit.

This is ordinary — a great many public repositories expose their authors' addresses — and the
address is not a secret. But it is a choice, not an oversight, and it should be made deliberately.

Options, in increasing cost:

1. **Accept it.** Nothing to do.
2. **Change it going forward.** Set `git config user.email` to a
   `@users.noreply.github.com` address, and enable *Block command line pushes that expose my email*
   in GitHub email settings. Historical commits keep the real address.
3. **Rewrite history.** Same objection as finding 1: it orphans every acceptance stamp. Not
   recommended.

### 3. Design mockups are committed and unreferenced · informational

`docs/design/workflow-dashboard-concept-*.png` are 3.2 MB of pre-implementation concept art. They
are the two largest tracked files and nothing links to them.

They are not a risk. The only hazard is that a reader mistakes a mockup for the product. Either
reference them somewhere that frames them as concepts — the intended agentic-engineering case study
is the natural home, since concept-versus-shipped is part of that story — or remove them. Leaving
them unreferenced is the one option with no upside.

### 4. No `.gitattributes` · informational

Every commit produces `LF will be replaced by CRLF` warnings. Nothing is broken, and the software
is Windows-only, so the practical impact is nil. `* text=auto` would silence it if it becomes
irritating.

---

## Cleared

- **No credentials, tokens, keys, or connection strings** in the working tree or in any of the 69
  commits.
- **No `.env`, database, log, or settings file has ever been tracked.** Verified against added-file
  history, not just the current tree.
- **Acceptance evidence is sanitized by construction.** Project identities are hashed
  (`"ProjectId": "2b2d4e7da617"`); no repository name, remote, or personal path appears in any
  record. The one path that does appear is `C:\Users\Public\mwd-visual-fixture`, which is under the
  public profile deliberately — the expanded layout prints a project path, so a fixture under a
  personal profile would have put the account name into every wide frame.
- **No content from monitored private repositories** appears anywhere. The visual fixture is
  invented; the acceptance records report counts and hashes rather than names.
- **The shipped defaults no longer reference the author's machine**, and a test now asserts that a
  fresh `DashboardSettings` names no directory at all.
- **Ignore rules cover** the cache database, logs, capture output, test results, dotenv files, and
  local agent scratch, and no tracked file is caught by any of them.

---

## Before flipping visibility

- [ ] **Read all 11 issues and every pull-request review thread.** This is the real gate. Making a
      repository public publishes its tracker, and those threads were largely agent-written against
      a live workspace — they are the most likely place for a private project name, a local path, or
      an unrelated client's details to be sitting. The code has been audited; the threads have not.
- [ ] Decide finding 2 (author email).
- [ ] Decide finding 3 (design mockups: frame them or remove them).
- [ ] Confirm `NOTICE.md` reads the way the owner wants it to read. It is a statement of terms
      written in plain language, not legal advice, and it has not been reviewed by anyone qualified.
- [ ] After flipping: enable **secret scanning** and **push protection** in repository settings.
      Both are free on public repositories and neither is on today.
- [ ] After flipping: confirm the two `github.com/Leit-motif/vantage/issues/…` links in the README
      resolve, and that the old repository slug still redirects.
