<#
.SYNOPSIS
  Builds a sanitized workspace and a matching dashboard state directory, so the running overlay
  can be photographed without a private project name ever reaching a committed frame.

.DESCRIPTION
  Ticket #6 asks for frames of the real application on the real desktop. The real application
  reads the owner's real workspaces, and a frame of that is a frame of private work. This builds
  an invented workspace with the same shape — projects, efforts, tickets, git history — and a
  state directory pointing at it, which the app opens with:

      MattWorkflowDashboard.exe --state <StateDirectory>

  Nothing here is copied from a live workspace. Every name is made up, and the tickets carry only
  the metadata grammar the dashboard reads.

.PARAMETER Root
  Where the fixture workspace is created. Removed and rebuilt on each run.

  It defaults under the public profile rather than the owner's, because the expanded layout prints
  a project's full path in its detail pane. A fixture under a personal profile would put the
  owner's account name into every wide frame, which is the one thing these frames must not carry.

.PARAMETER StateDirectory
  Where the fixture's own settings, cache, and logs live. Must not be the owner's real state.
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path $env:PUBLIC 'mwd-visual-fixture\workspaces'),
    [string] $StateDirectory = (Join-Path $env:PUBLIC 'mwd-visual-fixture\state')
)

$ErrorActionPreference = 'Stop'

function New-Ticket {
    param(
        [string] $Path,
        [string] $Title,
        [string] $Status,
        [string] $Stage,
        [string] $Type = 'feature',
        [string] $BlockedBy,
        [string] $Labels
    )

    $lines = @("# $Title", '', "Status: $Status", "Type: $Type")
    if ($Stage) { $lines += "Stage: $Stage" }
    if ($BlockedBy) { $lines += "Blocked by: $BlockedBy" }
    if ($Labels) { $lines += "Labels: $Labels" }
    $lines += @('', 'Body text the dashboard treats purely as data.')

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    Set-Content -Path $Path -Value ($lines -join "`n") -Encoding utf8
}

function New-FixtureProject {
    param(
        [string] $Name,
        [string] $Effort,
        [array]  $Tickets
    )

    $project = Join-Path $Root $Name
    New-Item -ItemType Directory -Force -Path $project | Out-Null
    Set-Content -Path (Join-Path $project 'AGENTS.md') -Value "# Agents`n`nInvented fixture project.`n" -Encoding utf8

    $issues = Join-Path $project ".scratch\$Effort\issues"
    New-Item -ItemType Directory -Force -Path $issues | Out-Null

    $index = 1
    foreach ($t in $Tickets) {
        New-Ticket -Path (Join-Path $issues ("{0:D2}-{1}.md" -f $index, ($t.Title -replace '[^a-zA-Z0-9]+', '-').ToLower())) @t
        $index++
    }

    Set-Content -Path (Join-Path $project ".scratch\$Effort\map.md") -Value "# $Effort`n`nInvented effort map.`n" -Encoding utf8

    Push-Location $project
    try {
        git init --initial-branch=main --quiet
        git config user.email 'fixture@example.invalid'
        git config user.name 'Fixture'
        git config commit.gpgsign false
        git add -A
        git commit -m "Fixture: $Name" --quiet
    }
    finally { Pop-Location }
}

# ---------------------------------------------------------------------------

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Root | Out-Null

# `Type` is what drives the pipeline segments — `Stage` is read but does not group them — so the
# types below are chosen to exercise the whole pipeline rather than collapsing into one segment.
New-FixtureProject -Name 'harbour-lantern' -Effort 'lantern-rewrite' -Tickets @(
    @{ Title = 'Chart the signal path';        Status = 'Complete';    Type = 'research';  Stage = 'Research' }
    @{ Title = 'Grill the retention model';    Status = 'Complete';    Type = 'grill';     Stage = 'Grill' }
    @{ Title = 'Prototype the lamp housing';   Status = 'Complete';    Type = 'prototype'; Stage = 'Prototype' }
    @{ Title = 'Build the keeper schedule';    Status = 'In progress'; Type = 'feature';   Stage = 'Build' }
    @{ Title = 'Tune the beam interval';       Status = 'Ready';       Type = 'feature';   Stage = 'Build' }
    @{ Title = 'Review the keeper handbook';   Status = 'Ready';       Type = 'review';    Stage = 'Review' }
    @{ Title = 'Ship the first light';         Status = 'Ready';       Type = 'release';   Stage = 'Release' }
)

New-FixtureProject -Name 'copper-kettle' -Effort 'kettle-intake' -Tickets @(
    @{ Title = 'Describe the intake shape';    Status = 'Ready';       Type = 'research';  Stage = 'Research' }
    @{ Title = 'Name the pouring states';      Status = 'Ready';       Type = 'prototype'; Stage = 'Prototype' }
    @{ Title = 'Fit the pouring spout';        Status = 'Ready';       Type = 'feature';   Stage = 'Build' }
)

New-FixtureProject -Name 'paper-anemone' -Effort 'anemone-drift' -Tickets @(
    @{ Title = 'Settle the drift vocabulary';  Status = 'Complete';    Type = 'research';  Stage = 'Research' }
    @{ Title = 'Model the tidal window';       Status = 'Blocked';     Type = 'prototype'; Stage = 'Prototype'; BlockedBy = 'Settle the drift vocabulary'; Labels = 'needs-decision' }
    @{ Title = 'Wire the drift telemetry';     Status = 'Blocked';     Type = 'feature';   Stage = 'Build';     BlockedBy = 'Model the tidal window' }
    @{ Title = 'Audit the drift readings';     Status = 'Blocked';     Type = 'review';    Stage = 'Review';    BlockedBy = 'Wire the drift telemetry' }
)

New-FixtureProject -Name 'salt-marsh-atlas' -Effort 'atlas-first-pass' -Tickets @(
    @{ Title = 'Survey the marsh sections';    Status = 'Complete';    Type = 'research';  Stage = 'Research' }
    @{ Title = 'Draw the section plates';      Status = 'Complete';    Type = 'prototype'; Stage = 'Prototype' }
    @{ Title = 'Bind the first pass';          Status = 'Complete';    Type = 'feature';   Stage = 'Build' }
    @{ Title = 'Release the first pass';       Status = 'Complete';    Type = 'release';   Stage = 'Release' }
)

New-FixtureProject -Name 'tin-whistle-registry' -Effort 'whistle-catalogue' -Tickets @(
    @{ Title = 'Collect the whistle makers';   Status = 'Idle';        Type = 'research';  Stage = 'Research' }
    @{ Title = 'Catalogue the bore sizes';     Status = 'Idle';        Type = 'feature';   Stage = 'Build' }
)

# The state directory the app is pointed at. Roots names only the fixture tree, and GitHub
# enrichment is off so nothing reaches the network while frames are being taken.
New-Item -ItemType Directory -Force -Path $StateDirectory | Out-Null
$settings = [ordered]@{
    SchemaVersion           = 1
    Roots                   = @($Root)
    Projects                = @()
    GitHubEnrichmentEnabled = $false
    LaunchAtSignIn          = $false
    Ui                      = [ordered]@{
        Theme                 = 'Dark'
        SurfaceOpacityPercent = 80
        StartCompact          = $true
        ClickThrough          = $false
        EdgeSnap              = $false
        ReducedMotion         = $false
        Geometry              = [ordered]@{
            Left          = $null
            Top           = $null
            CompactWidth  = 380
            ExpandedWidth = 720
            Height        = 520
        }
    }
}
$settings | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $StateDirectory 'settings.json') -Encoding utf8

Write-Host "Fixture workspace : $Root"
Write-Host "Fixture state     : $StateDirectory"
Write-Host "Run               : MattWorkflowDashboard.exe --state `"$StateDirectory`""
