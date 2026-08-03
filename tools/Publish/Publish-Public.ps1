<#
.SYNOPSIS
    Publishes this repository to its public mirror with the planning tree removed.

.DESCRIPTION
    The working repository is private and holds everything, including `.scratch/` — the
    specifications, tickets and decision record the agent workflow hands off through. The public
    repository is the portfolio, and carries the product and its evidence but none of the planning.

    They are two histories, not one history pushed twice. Git pushes whole commits, so a path
    cannot be withheld from a push; the public history has to be *produced* from the private one.
    That is what this does.

    Nothing here touches the working repository. Every step happens in a throwaway clone under the
    system temp directory, so a failed publish cannot leave the private repo rewritten.

.PARAMETER PublicRemote
    The public repository's push URL.

.PARAMETER Branch
    The branch to publish. Defaults to main.

.PARAMETER ExcludePath
    Paths to strip from the published history. Defaults to `.scratch`.

.PARAMETER WhatIf
    Do everything except the final push, and report what would be published.

.EXAMPLE
    ./tools/Publish/Publish-Public.ps1 -WhatIf
    ./tools/Publish/Publish-Public.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $PublicRemote = 'https://github.com/Leit-motif/vantage.git',
    [string] $Branch = 'main',
    [string[]] $ExcludePath = @('.scratch'),
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

function Fail($message) { Write-Error $message; exit 1 }

# git-filter-repo is what makes the published history granular rather than a squashed snapshot:
# it drops the commits that only touched the planning tree and rewrites the ones that touched both,
# deterministically — so a later publish fast-forwards rather than churning every SHA.
# Probed with --version, deliberately. `git filter-repo --help` exits 128 on a normal Windows git
# install because it goes looking for an HTML doc page that was never shipped — which reads as
# "not installed" and is not.
& git filter-repo --version 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Fail @"
git-filter-repo is not installed, and it is what produces the published history.

    pip install git-filter-repo

Without it the only alternative is a squashed snapshot, which cannot be turned back into a
granular history later. Install it rather than reaching for that.
"@
}

$source = & git rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0) { Fail 'Not inside a git repository.' }

$head = (& git rev-parse $Branch).Trim()
Write-Host "source      $source"
Write-Host "branch      $Branch at $($head.Substring(0,7))"
Write-Host "public      $PublicRemote"
Write-Host "excluding   $($ExcludePath -join ', ')"

# Refuse to publish a branch that still has uncommitted work behind it: the mirror would claim a
# state that never existed as a commit.
$dirty = & git status --porcelain
if ($dirty -and -not $Force) {
    Fail "The working tree is dirty. Commit or stash first, or pass -Force if you are certain."
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "vantage-publish-$([System.Guid]::NewGuid().ToString('n').Substring(0,8))"

try {
    Write-Host "`n--- cloning into a throwaway staging clone ---"
    # A real clone, not a worktree: filter-repo rewrites history, and it must never be pointed at
    # the repository actually being worked in.
    & git clone --no-local --single-branch --branch $Branch "$source" "$staging" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'Clone failed.' }

    Push-Location $staging
    try {
        $before = (& git rev-list --count HEAD).Trim()

        Write-Host "--- stripping $($ExcludePath -join ', ') from the history ---"
        $filterArgs = @()
        foreach ($path in $ExcludePath) { $filterArgs += @('--path', $path) }
        $filterArgs += @('--invert-paths', '--force')

        & git filter-repo @filterArgs
        if ($LASTEXITCODE -ne 0) { Fail 'filter-repo failed; nothing was published.' }

        $after = (& git rev-list --count HEAD).Trim()
        Write-Host "commits     $before -> $after ($([int]$before - [int]$after) dropped as planning-only)"

        # The whole point of the exercise. If this finds anything, do not push.
        foreach ($path in $ExcludePath) {
            $leaked = & git log --all --oneline -- $path
            if ($leaked) { Fail "'$path' still appears in the filtered history. Nothing was published." }
        }
        Write-Host "verified    no excluded path survives anywhere in the filtered history"

        if ($PSCmdlet.ShouldProcess($PublicRemote, "push $Branch")) {
            & git remote add public $PublicRemote
            & git push --force public "HEAD:refs/heads/$Branch"
            if ($LASTEXITCODE -ne 0) { Fail 'Push failed.' }
            Write-Host "`npublished   $Branch -> $PublicRemote"
        }
        else {
            Write-Host "`n-WhatIf: not pushed. The filtered history is at $staging"
            $staging = $null   # keep it for inspection
        }
    }
    finally { Pop-Location }
}
finally {
    if ($staging -and (Test-Path $staging)) {
        Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
    }
}
