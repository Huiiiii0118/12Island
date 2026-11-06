# Sets the app's version from the size of a change.
#
# The version the build takes is the name of the newest file in releases/ (see ReleaseVersion.targets), so a change is versioned
# by giving that file a new name: the major number for a change that rewrites much of the app, the minor for one that adds or
# reshapes a feature, the patch for a fix. The size is measured against the last snapshot in .version-state.json: how many source
# files were added, changed or removed, over how many there are.
#
#   .\Version-Bump.ps1            # bump from the last snapshot and record the new one
#   .\Version-Bump.ps1 -Init      # take the current version as the baseline, without bumping
#   .\Version-Bump.ps1 -WhatIf    # say what it would do, change nothing
#
# Run it from the repository root (the folder holding src/ and releases/).

[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Init,
    [string]$Root = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
$statePath = Join-Path $Root '.version-state.json'
$releases = Join-Path $Root 'releases'
if (-not (Test-Path $releases)) { throw "No releases folder under $Root." }

# The newest release notes file is the version: the same order ReleaseVersion.targets uses.
function Get-Version([string]$name) {
    if ($name -notmatch '^(\d+)\.(\d+)\.(\d+)$') { return $null }
    [pscustomobject]@{ Major = [int]$Matches[1]; Minor = [int]$Matches[2]; Patch = [int]$Matches[3]; Text = $name }
}
$current = Get-ChildItem $releases -Filter '*.md' |
    ForEach-Object { Get-Version $_.BaseName } |
    Where-Object { $_ } |
    Sort-Object Major, Minor, Patch |
    Select-Object -Last 1
if (-not $current) { throw "No release notes named <major>.<minor>.<patch>.md in $releases." }

# Every source file that belongs to the app, so a change in the settings counts as much as a change in the panel.
$files = Get-ChildItem (Join-Path $Root 'src') -Recurse -File -Include *.cs, *.xaml, *.json |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$hashes = @{}
# .NET Framework has no Path.GetRelativePath, so the path is made relative through its URI.
$base = [Uri](((Resolve-Path $Root).Path.TrimEnd('\')) + '\')
foreach ($file in $files) {
    $relative = [Uri]::UnescapeDataString($base.MakeRelativeUri([Uri]$file.FullName).ToString())
    $hashes[$relative] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
}

$previous = if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json } else { $null }
$before = @{}
if ($previous -and $previous.files) { $previous.files.PSObject.Properties | ForEach-Object { $before[$_.Name] = $_.Value } }

$changed = 0
foreach ($key in $hashes.Keys) {
    if (-not $before.ContainsKey($key) -or $before[$key] -ne $hashes[$key]) { $changed++ }
}
foreach ($key in $before.Keys) { if (-not $hashes.ContainsKey($key)) { $changed++ } }
$total = [Math]::Max(1, $hashes.Count)
$share = [Math]::Round($changed / $total, 3)

if ($Init) { $next = $current.Text }
elseif ($changed -eq 0) { $next = $current.Text }
elseif ($share -ge 0.4) { $next = "$($current.Major + 1).0.0" }
elseif ($share -ge 0.12) { $next = "$($current.Major).$($current.Minor + 1).0" }
else { $next = "$($current.Major).$($current.Minor).$($current.Patch + 1)" }

Write-Host ("{0} of {1} source files changed ({2:P0}) — version {3} -> {4}" -f $changed, $total, $share, $current.Text, $next)

if ($PSCmdlet.ShouldProcess($next, 'Set the version')) {
    if ($next -ne $current.Text) {
        $from = Join-Path $releases ($current.Text + '.md')
        $to = Join-Path $releases ($next + '.md')
        Move-Item $from $to
        Write-Host "Release notes moved to $to; edit them for this version."
    }
    $state = [ordered]@{ version = $next; saved = (Get-Date).ToString('s'); files = $hashes }
    $state | ConvertTo-Json -Depth 4 | Set-Content $statePath -Encoding utf8
    Write-Host "Snapshot saved to $statePath."
}
