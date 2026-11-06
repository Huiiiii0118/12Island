param(
    # The branch a pull request merges into, as in GITHUB_BASE_REF: dev, main or release/<version>.
    [Parameter(Mandatory)][string]$TargetBranch
)
# A settings migration step runs only for files saved by an earlier version than its label (see SettingsMigrations.cs),
# so a step added to a branch is labelled with the version that branch builds: the newest releases/<version>.md, as in
# src/12Island/ReleaseVersion.targets. On dev that version must still be in development. Once release/<version> exists
# or a beta or release of it is tagged, dev's work ships in a later version, and a step labelled with the earlier one
# would never run for the users who installed it. Release notes files are added by the maintainer, never for a step.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$migrations = 'src/12Island.Core/SettingsMigrations.cs'

function Get-StepVersions([string]$Text) {
    $steps = [regex]::Match($Text, '(?s)\bSteps\s*=\s*\[(.*?)\];')
    if (!$steps.Success) { throw "The Steps list was not found in $migrations." }
    [regex]::Matches($steps.Groups[1].Value, 'new Version\((\d+),\s*(\d+),\s*(\d+)\)') | ForEach-Object { '{0}.{1}.{2}' -f $_.Groups[1].Value, $_.Groups[2].Value, $_.Groups[3].Value }
}

# Ordered by major, minor, patch, then alpha < beta < release, then the prerelease number; other file names are ignored.
function Get-BuildVersion {
    $versions = foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'releases') -Filter *.md) {
        if ($file.BaseName -match '^(\d+)\.(\d+)\.(\d+)(?:-(alpha|beta)\.(\d+))?$') {
            [pscustomobject]@{
                Name = $file.BaseName; Core = '{0}.{1}.{2}' -f $Matches[1], $Matches[2], $Matches[3]; Label = $Matches[4]
                Major = [long]$Matches[1]; Minor = [long]$Matches[2]; Patch = [long]$Matches[3]
                Stage = if (!$Matches[4]) { 2 } elseif ($Matches[4] -eq 'beta') { 1 } else { 0 }
                Number = if ($Matches[5]) { [long]$Matches[5] } else { 0 }
            }
        }
    }
    if (!$versions) { throw 'No release notes named <major>.<minor>.<patch>[-alpha.N|-beta.N].md were found in releases/.' }
    $versions | Sort-Object Major, Minor, Patch, Stage, Number | Select-Object -Last 1
}

git -C $root rev-parse --quiet --verify "refs/remotes/origin/$TargetBranch" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "origin/$TargetBranch is missing. Fetch it before running this check." }
$baseText = (git -C $root show "origin/${TargetBranch}:$migrations") -join "`n"
if ($LASTEXITCODE -ne 0) { throw "$migrations could not be read from origin/$TargetBranch." }
$base = @(Get-StepVersions $baseText)
$added = @(Get-StepVersions (Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $root $migrations)) | Where-Object { $_ -notin $base } | Select-Object -Unique)
if ($added.Count -eq 0) { 'No settings migration step was added.'; return }

$build = Get-BuildVersion
if ($TargetBranch -eq 'dev') {
    $core = $build.Core
    $reasons = @()
    if ($build.Label -ne 'alpha') { $reasons += "releases/$($build.Name).md is not an alpha" }
    if (git -C $root branch -r --list "origin/release/$core") { $reasons += "release/$core exists" }
    $tags = @(git -C $root tag --list "v$core" "v$core-beta.*")
    if ($tags.Count -gt 0) { $reasons += "$($tags -join ', ') is tagged" }
    if ($reasons.Count -gt 0) {
        throw "Version $core is already in testing or released ($($reasons -join '; ')), so dev builds a later version. " +
            "The maintainer adds releases/<next version>-alpha.1.md to dev first; then label the new migration step with that version."
    }
}
foreach ($step in $added) {
    if ($step -ne $build.Core) {
        throw "The new migration step $step must be labelled $($build.Core), the version $TargetBranch builds (releases/$($build.Name).md). " +
            'Do not add a release notes file for it; the maintainer adds those.'
    }
}
"New migration step $($added -join ', ') matches the version $TargetBranch builds."
