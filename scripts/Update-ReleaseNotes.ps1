[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-(alpha|beta)\.\d+)?$')][string]$Version,
    [string]$ReleaseNotes
)
# Replaces the notes of a published release without rebuilding it: the GitHub release text, the notes in update.json
# that the app shows, and the update.json line of SHA256SUMS.txt. The installer and its checksum stay as published.
# Requires the GitHub CLI (gh), signed in with permission to edit releases of this repository.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'ReleaseNotes.ps1')
Push-Location $projectRoot
$work = Join-Path ([IO.Path]::GetTempPath()) "TwelveIsland-notes-$([Guid]::NewGuid().ToString('N'))"
try {
    if (!$ReleaseNotes) { $ReleaseNotes = "releases/$Version.md" }
    $notesPath = (Resolve-Path -LiteralPath $ReleaseNotes).Path
    $notes = Read-ReleaseNotes $notesPath
    $tag = "v$Version"
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    gh release download $tag --pattern update.json --pattern SHA256SUMS.txt --dir $work
    if ($LASTEXITCODE -ne 0) { throw "Could not download the assets of $tag." }

    $manifestPath = Join-Path $work 'update.json'
    $manifest = Get-Content -Raw -Encoding utf8 -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.version -ne $Version) { throw "update.json of $tag names version $($manifest.version)." }
    $manifest.notes = $notes
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

    $sumsPath = Join-Path $work 'SHA256SUMS.txt'
    $hash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums = Get-Content -Encoding utf8 -LiteralPath $sumsPath | Where-Object { $_ -and $_ -notmatch '\s+update\.json$' }
    [IO.File]::WriteAllLines($sumsPath, [string[]](@($sums) + "$hash  update.json"), [Text.UTF8Encoding]::new($false))

    gh release edit $tag --notes-file $notesPath
    if ($LASTEXITCODE -ne 0) { throw "Could not edit the notes of $tag." }
    gh release upload $tag $manifestPath $sumsPath --clobber
    if ($LASTEXITCODE -ne 0) { throw "Could not upload the updated assets of $tag." }
    # The beta channel holds a copy of the newest beta's update.json; it gets the new notes while it still names this version.
    if ($Version -match '-beta\.') {
        $channel = Join-Path $work 'beta-channel'
        gh release download beta-channel --pattern update.json --dir $channel
        if ($LASTEXITCODE -eq 0 -and (Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $channel 'update.json') | ConvertFrom-Json).version -eq $Version) {
            gh release upload beta-channel $manifestPath --clobber
            if ($LASTEXITCODE -ne 0) { throw 'Could not update the beta channel.' }
        }
    }
    Write-Output "Updated the notes of $tag."
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
