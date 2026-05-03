#requires -Version 7.0
<#
.SYNOPSIS
    Interactive end-to-end release helper for SC-DataRunner.

.DESCRIPTION
    Drives the full release flow with friendly prompts:

      Mode A — "Publish via CI" (default, recommended):
        1) Bump <Version> in Directory.Build.props
        2) Commit + push the bump on the current branch
        3) Create the matching git tag (v<version>) and push it
        4) The tag push triggers .github/workflows/release.yml on GitHub,
           which builds, packs and publishes the Velopack release.

      Mode B — "Local build only":
        Builds Setup.exe and the .nupkg locally under .\releases\, without
        touching git or GitHub. Used to sanity-check a release before
        going public.

      Mode C — "Local build + manual upload to GitHub":
        Same as B, plus `vpk upload github` from your machine. Requires
        $env:GH_TOKEN to be set to a PAT with contents:write.

    All destructive steps (commit/push/tag) ask for confirmation before
    running. You can abort at any time with Ctrl+C.

.PARAMETER NonInteractive
    Skip all prompts and pick safe defaults (useful in CI). The bump is
    "patch" and the mode is "publish via CI".

.PARAMETER Bump
    Pre-select the version bump kind: patch | minor | major | none.
    "none" keeps the current <Version> as-is (useful to re-tag a fixed build).

.PARAMETER Mode
    Pre-select the mode: ci | local | localUpload.

.EXAMPLE
    pwsh ./scripts/release.ps1
        # fully interactive — ask everything

.EXAMPLE
    pwsh ./scripts/release.ps1 -Bump minor -Mode ci -NonInteractive
        # bump minor, push tag, exit
#>
[CmdletBinding()]
param(
    [ValidateSet('patch','minor','major','none')]
    [string]$Bump,

    [ValidateSet('ci','local','localUpload')]
    [string]$Mode,

    [switch]$NonInteractive,

    [string]$Runtime = 'win-x64',
    [string]$RepoUrl = 'https://github.com/Olrik-WP/SC-DataRunnerNet',
    [string]$PackId = 'SC-DataRunner',
    [string]$ReleaseChannel = 'win',
    [string]$ProjectPath = 'src/DataRunner.App.Wpf/DataRunner.App.Wpf.csproj',
    [string]$MainExe = 'DataRunner.App.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

# ---------- pretty printing helpers -----------------------------------
function H1($t) { Write-Host ""; Write-Host ("=" * 60) -ForegroundColor DarkGray; Write-Host " $t" -ForegroundColor Cyan; Write-Host ("=" * 60) -ForegroundColor DarkGray }
function Step($t)  { Write-Host "==>" -ForegroundColor Cyan -NoNewline; Write-Host " $t" }
function Info($t)  { Write-Host "    $t" -ForegroundColor Gray }
function Warn($t)  { Write-Host "!! $t" -ForegroundColor Yellow }
function Fail($t)  { Write-Host "XX $t" -ForegroundColor Red; exit 1 }

function Ask($question, [string]$default) {
    if ($NonInteractive) { return $default }
    $hint = if ($default) { " [$default]" } else { "" }
    $ans = Read-Host "$question$hint"
    if ([string]::IsNullOrWhiteSpace($ans)) { return $default }
    return $ans.Trim()
}

function AskYesNo($question, [bool]$default = $false) {
    if ($NonInteractive) { return $default }
    $suffix = if ($default) { '[Y/n]' } else { '[y/N]' }
    while ($true) {
        $a = (Read-Host "$question $suffix").Trim().ToLowerInvariant()
        if ($a -eq '')                            { return $default }
        if ($a -in @('y','yes','o','oui'))        { return $true }
        if ($a -in @('n','no','non'))             { return $false }
        Warn "Please answer y or n."
    }
}

# ---------- 1. Inspect current state ----------------------------------
H1 'SC-DataRunner — Release helper'

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $propsPath)) { Fail "Directory.Build.props not found at $propsPath." }

[xml]$props = Get-Content $propsPath
$currentVersion = ($props.Project.PropertyGroup.Version | Select-Object -First 1).Trim()
if (-not $currentVersion) { Fail "No <Version> tag in Directory.Build.props." }

$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
$dirty = $null -ne (& git status --porcelain)
$lastTag = (& git tag --sort=-v:refname | Select-Object -First 1)
if (-not $lastTag) { $lastTag = '(none)' }
$ahead = (& git rev-list "@{u}..HEAD" --count 2>$null)
if (-not $ahead) { $ahead = '0 (no upstream)' }

Step "Current state"
Info "Version (Directory.Build.props): $currentVersion"
Info "Git branch:                       $branch"
Info "Last tag:                         $lastTag"
Info "Working tree:                     $(if ($dirty) { 'DIRTY (uncommitted changes)' } else { 'clean' })"
Info "Commits ahead of origin/${branch}: $ahead"

if ($branch -ne 'main' -and $branch -ne 'master') {
    Warn "You are on '$branch' (not main/master). Releases SHOULD usually be cut from main."
    if (-not (AskYesNo "Continue anyway?")) { exit 0 }
}

# ---------- 2. Pick mode ---------------------------------------------
if (-not $Mode) {
    H1 'Mode'
    Write-Host "  1) Publish via CI         — bump version, commit, push tag (triggers GitHub Actions, which builds & releases publicly)"
    Write-Host "  2) Local build only       — produce Setup.exe under ./releases/, no git, no GitHub"
    Write-Host "  3) Local + manual upload  — local build + vpk upload to GitHub Releases (requires \$env:GH_TOKEN)"
    Write-Host ""
    $sel = Ask 'Pick a mode (1/2/3)' '1'
    $Mode = switch ($sel) { '1' { 'ci' }; '2' { 'local' }; '3' { 'localUpload' }; default { 'ci' } }
}
Step "Mode: $Mode"

# ---------- 3. Pick / compute version --------------------------------
function Get-NextVersion([string]$v, [string]$kind) {
    if (-not ($v -match '^(\d+)\.(\d+)\.(\d+)(?:-(.+))?$')) {
        throw "Current <Version> is not strict SemVer: $v"
    }
    $maj = [int]$matches[1]; $min = [int]$matches[2]; $pat = [int]$matches[3]
    switch ($kind) {
        'major' { return "$($maj+1).0.0" }
        'minor' { return "$maj.$($min+1).0" }
        'patch' { return "$maj.$min.$($pat+1)" }
        'none'  { return $v }
        default { throw "Unknown bump kind: $kind" }
    }
}

if (-not $Bump) {
    H1 'New version'
    Write-Host "  Current: $currentVersion"
    Write-Host "  1) patch  -> $(Get-NextVersion $currentVersion 'patch')   (bug fix)"
    Write-Host "  2) minor  -> $(Get-NextVersion $currentVersion 'minor')   (new feature, backward compatible)"
    Write-Host "  3) major  -> $(Get-NextVersion $currentVersion 'major')   (breaking change)"
    Write-Host "  4) none   -> $currentVersion         (re-release the same version — risky)"
    Write-Host "  5) custom -> type any SemVer manually"
    Write-Host ""
    $sel = Ask 'Pick a bump kind (1-5)' '1'
    $Bump = switch ($sel) {
        '1' { 'patch' }; '2' { 'minor' }; '3' { 'major' }; '4' { 'none' }
        '5' {
            $custom = Ask 'Enter the new version (SemVer X.Y.Z[-suffix])'
            if (-not ($custom -match '^\d+\.\d+\.\d+(?:-[\w\.\-]+)?$')) {
                Fail "Invalid SemVer: $custom"
            }
            $script:newVersionOverride = $custom
            'custom'
        }
        default { 'patch' }
    }
}

if ($Bump -eq 'custom') {
    $newVersion = $script:newVersionOverride
} else {
    $newVersion = Get-NextVersion $currentVersion $Bump
}

Step "New version: $newVersion"

# ---------- 4. Pre-flight checks for the chosen mode ------------------
$tagName = "v$newVersion"

if ($Mode -eq 'ci') {
    if ($dirty) {
        # Strict mode for the CI flow: the user's expected workflow is to
        # commit + push their code via Cursor BEFORE running this script.
        # Releasing on top of a dirty tree would silently leave their work
        # out of the tagged commit and ship a half-baked binary. So we bail
        # with a clear message instead of trying to be clever.
        Warn 'Working tree is DIRTY (uncommitted changes detected):'
        & git status --short
        Write-Host ''
        Fail "Please commit and push your changes first (use the git panel in Cursor), then re-run this script. The script's job is to bump the version, tag, and trigger CI — your feature commits stay your responsibility."
    }
    $existingTag = & git tag --list $tagName
    if ($existingTag) {
        Fail "Tag $tagName already exists locally. Aborting (delete it with 'git tag -d $tagName' if you really want to re-tag)."
    }
    if ($ahead -ne '0' -and $ahead -ne '0 (no upstream)') {
        Warn "You have $ahead local commit(s) not yet on origin/$branch. The script will push them along with the version bump."
        if (-not (AskYesNo 'Continue?' $true)) { exit 0 }
    }
}

if ($Mode -eq 'localUpload' -and -not $env:GH_TOKEN) {
    Fail '$env:GH_TOKEN is required for "Local + manual upload" mode. Set a GitHub PAT with contents:write and retry.'
}

# ---------- 5. Show the plan and confirm -----------------------------
H1 'Plan'
switch ($Mode) {
    'ci' {
        Write-Host "  [x] Update <Version> in Directory.Build.props ($currentVersion -> $newVersion)"
        if ($Bump -ne 'none') {
            Write-Host "  [x] git add Directory.Build.props"
            Write-Host "  [x] git commit -m 'Release v$newVersion'   (only the version bump)"
        }
        Write-Host "  [x] git push origin $branch"
        Write-Host "  [x] git tag $tagName"
        Write-Host "  [x] git push origin $tagName    ← TRIGGERS GitHub Actions"
        Write-Host ""
        Write-Host "  After the tag push, GitHub Actions runs the YAML workflow and:" -ForegroundColor DarkGray
        Write-Host "    - dotnet publish self-contained ($Runtime)   (build happens THERE, not on your machine)" -ForegroundColor DarkGray
        Write-Host "    - vpk download (previous release, for delta)" -ForegroundColor DarkGray
        Write-Host "    - vpk pack v$newVersion" -ForegroundColor DarkGray
        Write-Host "    - vpk upload github (creates the public release)" -ForegroundColor DarkGray
    }
    'local' {
        Write-Host "  [x] Update <Version> in Directory.Build.props ($currentVersion -> $newVersion)"
        Write-Host "  [x] dotnet publish $ProjectPath ($Runtime, self-contained)"
        Write-Host "  [x] vpk pack v$newVersion (output: ./releases/)"
        Write-Host "  [ ] git operations: NONE"
        Write-Host "  [ ] GitHub upload:  NONE"
    }
    'localUpload' {
        Write-Host "  [x] Update <Version> in Directory.Build.props ($currentVersion -> $newVersion)"
        Write-Host "  [x] dotnet publish $ProjectPath ($Runtime, self-contained)"
        Write-Host "  [x] vpk pack v$newVersion (output: ./releases/)"
        Write-Host "  [x] vpk upload github (with `$env:GH_TOKEN)"
        Write-Host "  [ ] git commit/tag: NONE — you'll need to do that yourself if you want the source tag too"
    }
}
Write-Host ""
if (-not (AskYesNo 'Proceed?' $true)) { Step 'Cancelled.'; exit 0 }

# ---------- 6. Apply version bump ------------------------------------
if ($newVersion -ne $currentVersion) {
    Step "Updating Directory.Build.props"
    # XML round-trip would re-format the whole file; do a targeted regex replace.
    $raw = Get-Content $propsPath -Raw
    $patched = [regex]::Replace($raw,
        '(<Version>)[^<]+(</Version>)',
        "`${1}$newVersion`${2}",
        [System.Text.RegularExpressions.RegexOptions]::Multiline)
    Set-Content -Path $propsPath -Value $patched -Encoding UTF8 -NoNewline
    Info "Set <Version>$newVersion</Version>"
}

# ---------- 7a. CI mode: git commit + push + tag --------------------
if ($Mode -eq 'ci') {
    Step 'Staging Directory.Build.props'
    & git add $propsPath | Out-Null

    $hasStagedChanges = $null -ne (& git diff --cached --name-only)
    if ($hasStagedChanges) {
        Step "Committing 'Release v$newVersion'"
        & git commit -m "Release v$newVersion"
        if ($LASTEXITCODE -ne 0) { Fail 'git commit failed.' }
    } else {
        Info 'Nothing to commit (no changes detected). Skipping commit.'
    }

    Step "Pushing branch $branch"
    & git push origin $branch
    if ($LASTEXITCODE -ne 0) { Fail "git push origin $branch failed." }

    Step "Creating tag $tagName"
    & git tag -a $tagName -m "Release $tagName"
    if ($LASTEXITCODE -ne 0) { Fail "git tag failed." }

    Step "Pushing tag $tagName  (this triggers GitHub Actions)"
    & git push origin $tagName
    if ($LASTEXITCODE -ne 0) { Fail "git push tag failed." }

    H1 'Done'
    Write-Host "  GitHub Actions is now building $tagName." -ForegroundColor Green
    Write-Host "  Follow it here:" -ForegroundColor Green
    Write-Host "    $RepoUrl/actions" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Once it finishes (~3-5 min), the release will appear at:" -ForegroundColor Green
    Write-Host "    $RepoUrl/releases/tag/$tagName" -ForegroundColor Cyan
    exit 0
}

# ---------- 7b. Local modes: publish + pack [+ upload] --------------
$publishDir  = Join-Path $repoRoot 'publish'
$releasesDir = Join-Path $repoRoot 'releases'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir | Out-Null
New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

Step "Publishing $ProjectPath ($Runtime, self-contained)"
dotnet publish $ProjectPath -c Release -r $Runtime --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed (exit $LASTEXITCODE)." }

Step 'Ensuring vpk CLI is installed'
$vpkInstalled = (dotnet tool list -g | Select-String -Pattern '^\s*vpk\s' -Quiet)
if (-not $vpkInstalled) {
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { Fail 'Failed to install vpk.' }
}

# `dotnet tool install -g` writes into ~/.dotnet/tools, but on a brand-new
# install Windows does NOT refresh PATH for the running process. Without
# this fix, the very first run of the script (right after installing vpk)
# fails with "vpk is not recognized" — the user has to re-launch.
# Append the tools dir to the session PATH so subsequent calls resolve.
$dotnetTools = Join-Path $env:USERPROFILE '.dotnet\tools'
if ((Test-Path $dotnetTools) -and (";$env:PATH;" -notlike "*;$dotnetTools;*")) {
    $env:PATH = "$env:PATH;$dotnetTools"
    Info "Added '$dotnetTools' to PATH for this session."
}

# Sanity-check that vpk is now on PATH; if not, point the user at the
# remediation rather than failing two steps later with a confusing error.
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Fail "vpk was installed but is still not on PATH. Open a NEW terminal and re-run the script (or manually run: `$env:PATH += ';$dotnetTools')."
}

Step 'Downloading previous release for delta packing (best-effort)'
$dlArgs = @('download','github','--repoUrl',$RepoUrl,'-o',$releasesDir,'--channel',$ReleaseChannel)
if ($env:GH_TOKEN) { $dlArgs += @('--token',$env:GH_TOKEN) }
& vpk @dlArgs
if ($LASTEXITCODE -ne 0) {
    Warn "vpk download returned $LASTEXITCODE. Likely no prior release exists yet — first release will be a full package."
    $LASTEXITCODE = 0
}

Step "Packing v$newVersion"
& vpk pack `
    --packId $PackId `
    --packVersion $newVersion `
    --packDir $publishDir `
    --mainExe $MainExe `
    --channel $ReleaseChannel `
    --outputDir $releasesDir
if ($LASTEXITCODE -ne 0) { Fail 'vpk pack failed.' }

if ($Mode -eq 'localUpload') {
    Step 'Uploading to GitHub Releases'
    & vpk upload github `
        --repoUrl $RepoUrl `
        --token $env:GH_TOKEN `
        --releaseName "SC DataRunner v$newVersion" `
        --tag $tagName `
        --channel $ReleaseChannel `
        --publish
    if ($LASTEXITCODE -ne 0) { Fail 'vpk upload failed.' }
}

H1 'Done'
Write-Host "  Local artifacts:  $releasesDir" -ForegroundColor Green
if ($Mode -eq 'localUpload') {
    Write-Host "  GitHub release:   $RepoUrl/releases/tag/$tagName" -ForegroundColor Cyan
}
