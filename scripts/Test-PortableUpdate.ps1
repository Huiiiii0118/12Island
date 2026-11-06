# Extracts a portable test copy into a folder whose name has Chinese characters and a space, then updates it through
# AppUpdates twice: first with one of its files held open, so the helper must put every old file back and start the old
# version again, then normally, so the new version replaces the files and starts. A unique AppId, data folder and mutex
# keep the test away from the user's TwelveIsland and settings; nothing is registered, so nothing needs removing afterwards.
# The test cannot cover the administrator prompt for a folder the user cannot write to.
# This file contains Chinese text and must stay UTF-8 with BOM for Windows PowerShell 5.1.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'PortablePackage.ps1')
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ".artifacts/app-update/portable-$testId"))
$folderName = '免安装 TwelveIsland'
$appPath = Join-Path $testRoot $folderName
$exe = Join-Path $appPath 'TwelveIsland.UpdateHarness.exe'
$staleFile = Join-Path $appPath 'only-in-0.1.0.txt'
$userFile = Join-Path $appPath 'user-note.txt'
$result = Join-Path $testRoot 'result.txt'

function Assert($condition, $message) { if (!$condition) { throw "FAIL $message" }; Write-Output "PASS $message" }
function ListedFiles { @(Get-Content -LiteralPath (Join-Path $appPath 'TwelveIsland.portable') -Encoding utf8) }
# Starts the test copy, waits for it to exit and for the copy it leaves running (the helper's restart) to write its result.
function RunUpdate {
    if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.Environment['BRIMDECK_UPDATE_TEST_ROOT'] = $testRoot
    $start.Environment['BRIMDECK_UPDATE_TEST_APPID'] = "{$([Guid]::NewGuid().ToString().ToUpperInvariant())}"
    $start.Environment['BRIMDECK_UPDATE_TEST_MUTEX'] = "TwelveIsland.PortableTest.$testId."
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Test app did not exit after starting the update.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while (!(Test-Path -LiteralPath $result) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (!(Test-Path -LiteralPath $result)) { throw "No result from the restarted app. Inspect $testRoot" }
    Get-Content -Raw -Encoding utf8 -LiteralPath $result
}

Push-Location $projectRoot
try {
    New-Item -ItemType Directory -Force -Path $testRoot, (Join-Path $testRoot 'feed') | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot '.update-test-root') -Value $testId
    foreach ($version in @('0.1.0', '0.1.1')) {
        $publish = Join-Path $testRoot "publish-$version"
        dotnet publish tests/TwelveIsland.UpdateHarness -c Release -r win-x64 --self-contained false -p:Version=$version -o $publish
        if ($LASTEXITCODE -ne 0) { throw 'Test publish failed.' }
        # A file that only the old version ships shows whether the update removes files the new version no longer lists.
        if ($version -eq '0.1.0') { Set-Content -LiteralPath (Join-Path $publish 'only-in-0.1.0.txt') -Value 'old' }
        $zip = Join-Path $testRoot $(if ($version -eq '0.1.0') { 'initial.zip' } else { "feed/TwelveIsland-$version-Portable-x64.zip" })
        New-PortablePackage -SourceDir $publish -Destination $zip -Folder $folderName | Out-Null
    }
    # Portable copies never download the installer, but the manifest always names one.
    $setup = Join-Path $testRoot 'feed/TwelveIsland-0.1.1-Setup.exe'
    Set-Content -LiteralPath $setup -Value 'installer'
    $zip = Get-Item -LiteralPath (Join-Path $testRoot 'feed/TwelveIsland-0.1.1-Portable-x64.zip')
    $manifest = [ordered]@{ version = '0.1.1'; notes = 'Update test'; file = 'TwelveIsland-0.1.1-Setup.exe'; size = (Get-Item -LiteralPath $setup).Length
        sha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        portable = [ordered]@{ file = $zip.Name; size = $zip.Length; sha256 = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }
    [IO.File]::WriteAllText((Join-Path $testRoot 'feed/update.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    Expand-Archive -LiteralPath (Join-Path $testRoot 'initial.zip') -DestinationPath $testRoot
    Set-Content -LiteralPath $userFile -Value 'mine'
    Assert ((Test-Path -LiteralPath $exe) -and (ListedFiles) -contains 'only-in-0.1.0.txt') 'the portable zip holds one folder with the files and their list'

    # Held open without sharing, the old file cannot be moved aside, so the replacement fails part of the way through.
    $lock = [IO.File]::Open($staleFile, 'Open', 'Read', 'None')
    try { $text = RunUpdate }
    finally { $lock.Dispose() }
    if (!$text.StartsWith('KEPT 0.1.0 Ready')) { throw "Expected the old version to start again with the update still ready: $text" }
    Write-Output "PASS $($text.Trim())"
    Assert ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -like '0.1.0*' -and (Test-Path -LiteralPath $staleFile) -and (ListedFiles) -contains 'only-in-0.1.0.txt') `
        'a failed replacement restores the old version and its file list'
    Assert (!(Get-ChildItem -LiteralPath $appPath -Directory -Filter '.brimdeck-update-*')) 'a failed replacement leaves no backup folder'

    $text = RunUpdate
    if (!$text.StartsWith('PASS')) { throw $text }
    Write-Output $text.Trim()
    Assert ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -like '0.1.1*') 'the folder now holds the new version'
    Assert (!(Test-Path -LiteralPath $staleFile) -and !((ListedFiles) -contains 'only-in-0.1.0.txt')) 'files the new version no longer lists are removed'
    Assert ((Get-Content -Raw -LiteralPath $userFile).Trim() -eq 'mine') 'files the package never listed stay'
    Assert (!(Get-ChildItem -LiteralPath $appPath -Directory -Filter '.brimdeck-update-*')) 'no backup folder is left'
    Write-Output "Evidence: $testRoot"
}
finally { Pop-Location }
