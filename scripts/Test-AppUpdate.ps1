# Installs a test copy with the real installer script into a folder whose name has Chinese characters and a space,
# updates it through AppUpdates (the new installer removes the old version first), then uninstalls it twice to cover
# keeping and deleting user data. A unique AppId, name, data folder, mutex and startup value keep the test away from
# the user's TwelveIsland installation and settings.
# This file contains Chinese text and must stay UTF-8 with BOM for Windows PowerShell 5.1.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ".artifacts/app-update/installed-$testId"))
$installRoot = Join-Path $testRoot '安装 目录'
$dataPath = Join-Path $testRoot 'user-data'
$appId = [Guid]::NewGuid().ToString().ToUpperInvariant()
# No spaces: the update manifest accepts only plain installer file names.
$appName = "TwelveIsland-UpdateTest-$($testId.Substring(0, 8))"
# The installer appends the app folder name to the chosen folder; the test passes the parent with a trailing \.
$installPath = Join-Path $installRoot $appName
$mutexPrefix = "TwelveIsland.UpdateTest.$testId."
$runValue = "TwelveIsland.UpdateTest.$testId"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$appId}_is1"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
$staleFile = Join-Path $installPath 'only-in-0.1.0.txt'

function Assert($condition, $message) { if (!$condition) { throw "FAIL $message" }; Write-Output "PASS $message" }
function Install($setup, $log) {
    $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER', "/DIR=`"$installRoot\`"", "/LOG=`"$log`"") -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Install failed with $($process.ExitCode); see $log" }
}
# The uninstaller exits once its copy in TEMP has finished; the loop only guards against a slow file system.
function Uninstall([string[]]$extra) {
    $uninstaller = [regex]::Match((Get-ItemProperty $uninstallKey).UninstallString, '^"([^"]+)"').Groups[1].Value
    Start-Process -FilePath $uninstaller -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES') + $extra) -Wait
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (((Test-Path $uninstallKey) -or (Test-Path -LiteralPath $uninstaller)) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (Test-Path $uninstallKey) { throw 'Uninstall did not finish.' }
}
function InstalledAt { [IO.Path]::GetFullPath((Get-ItemProperty $uninstallKey).InstallLocation).TrimEnd('\') -eq $installPath }
# The test folder sits under the repository. Like the installer, a folder in Program Files or the profile keeps its inherited
# rights; anywhere else, such as D:\TwelveIsland, it must stop inheriting rights such as Authenticated Users' "modify" from the
# drive's root.
$ownRights = !(@($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:USERPROFILE, $env:LOCALAPPDATA) |
    Where-Object { $_ -and "$installPath\".StartsWith("$($_.TrimEnd('\'))\", [StringComparison]::OrdinalIgnoreCase) })
$rightsCase = if ($ownRights) { 'a folder outside Program Files and the profile stops inheriting rights; only this user, SYSTEM and Administrators can change it' }
    else { 'a folder in Program Files or the profile keeps its inherited rights' }
function ExpectedRights {
    $rights = if ($ownRights) { PrivateRights } else { !(Get-Acl -LiteralPath $installPath).AreAccessRulesProtected }
    $rights -and !(Test-Path -LiteralPath (Join-Path $installPath '.brimdeck-write-test'))
}
function PrivateRights {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $full = [int][Security.AccessControl.FileSystemRights]::FullControl
    $folder = Get-Acl -LiteralPath $installPath
    $rules = @($folder.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $program = @((Get-Acl -LiteralPath (Join-Path $installPath 'TwelveIsland.UpdateHarness.exe')).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $folder.AreAccessRulesProtected -and !($rules + $program | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-11' }) `
        -and ($rules | Where-Object { $_.IdentityReference.Value -eq $me -and ([int]$_.FileSystemRights -band $full) -eq $full }) `
        -and !($rules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' -and ([int]$_.FileSystemRights -band [int][Security.AccessControl.FileSystemRights]::Write) })
}

Push-Location $projectRoot
try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot '.update-test-root') -Value $testId
    $iscc = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1')
    $setups = @{}
    foreach ($version in @('0.1.0', '0.1.1')) {
        $publish = Join-Path $testRoot "publish-$version"
        $output = Join-Path $testRoot $(if ($version -eq '0.1.0') { 'initial' } else { 'feed' })
        dotnet publish tests/TwelveIsland.UpdateHarness -c Release -r win-x64 --self-contained false -p:Version=$version -o $publish
        if ($LASTEXITCODE -ne 0) { throw 'Test publish failed.' }
        # A file that only the old version ships shows whether the old version was removed before the update.
        if ($version -eq '0.1.0') { Set-Content -LiteralPath (Join-Path $publish 'only-in-0.1.0.txt') -Value 'old' }
        & $iscc -q -nc "-dAppVersion=$version" "-dSourceDir=$publish" "-o$output" "-dAppId=$appId" "-dAppName=$appName" `
            '-dMainExe=TwelveIsland.UpdateHarness.exe' "-dDataDir=$dataPath" "-dMutexPrefix=$mutexPrefix" "-dRunValue=$runValue" `
            "-dAppUserModelId=TwelveIsland.UpdateTest.$testId" installer/TwelveIsland.iss
        if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
        $setups[$version] = Join-Path $output "$appName-$version-Setup.exe"
    }
    $next = Get-Item -LiteralPath $setups['0.1.1']
    $manifest = [ordered]@{ version = '0.1.1'; notes = 'Update test'; file = $next.Name; size = $next.Length
        sha256 = (Get-FileHash -LiteralPath $next.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    [IO.File]::WriteAllText((Join-Path $testRoot 'feed/update.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    Install $setups['0.1.0'] (Join-Path $testRoot 'install-0.1.0.log')
    $entry = Get-ItemProperty $uninstallKey
    Assert ($entry.DisplayName -eq $appName -and $entry.DisplayVersion -eq '0.1.0') 'installer registers the app for Settings > Apps and Control Panel'
    Assert (InstalledAt) 'a folder with Chinese characters, a space and a trailing \ gets the app folder name appended'
    Assert ($entry.UninstallString -eq "`"$installPath\unins000.exe`" /SILENT /UI") 'Settings > Apps runs the uninstaller with its own confirmation dialog'
    Assert ((Test-Path -LiteralPath $desktopShortcut) -and (Test-Path -LiteralPath $startMenuShortcut)) 'a first install creates the desktop and Start menu shortcuts'
    Assert (ExpectedRights) $rightsCase

    # The user deletes both shortcuts after installing.
    [IO.File]::Delete($startMenuShortcut)
    [IO.File]::Delete($desktopShortcut)
    # A fresh account, such as a CI runner, may have no Run key yet; the app creates it the same way.
    if (!(Test-Path $runKey)) { New-Item -Path $runKey | Out-Null }
    New-ItemProperty -Path $runKey -Name $runValue -Value "`"$installPath\TwelveIsland.UpdateHarness.exe`"" -Force | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $installPath 'TwelveIsland.UpdateHarness.exe'))
    $start.UseShellExecute = $false
    $start.Environment['BRIMDECK_UPDATE_TEST_ROOT'] = $testRoot
    $start.Environment['BRIMDECK_UPDATE_TEST_APPID'] = "{$appId}"
    $start.Environment['BRIMDECK_UPDATE_TEST_MUTEX'] = $mutexPrefix
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Test app did not exit after starting the installer.' }
    $result = Join-Path $testRoot 'result.txt'
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while (!(Test-Path -LiteralPath $result) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if (!(Test-Path -LiteralPath $result)) { throw "No result from the restarted app. Inspect $testRoot" }
    $text = Get-Content -Raw -LiteralPath $result
    if (!$text.StartsWith('PASS')) { throw $text }
    Write-Output $text.Trim()
    $entry = Get-ItemProperty $uninstallKey
    Assert ($entry.DisplayVersion -eq '0.1.1' -and (InstalledAt)) 'update keeps a single registration in the same folder'
    Assert ($entry.UninstallString -eq "`"$installPath\unins000.exe`" /SILENT /UI" -and !(Test-Path -LiteralPath "$installPath\unins001.exe")) 'the new uninstaller replaces the old one'
    Assert (!(Test-Path -LiteralPath $staleFile)) 'update removes the old version before installing the new one'
    Assert ((Test-Path -LiteralPath $startMenuShortcut) -and !(Test-Path -LiteralPath $desktopShortcut)) 'update restores the Start menu shortcut and keeps a deleted desktop shortcut deleted'
    Assert ($null -ne (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue)) 'update keeps the startup entry'
    Assert (ExpectedRights) "after an in-app update without administrator rights, $rightsCase"

    Uninstall @()
    Assert (!(Test-Path -LiteralPath (Join-Path $installPath 'TwelveIsland.UpdateHarness.exe'))) 'uninstall removes the program files'
    Assert (!(Test-Path -LiteralPath $startMenuShortcut)) 'uninstall removes the shortcut'
    Assert (Test-Path -LiteralPath (Join-Path $dataPath 'settings.json')) 'a silent uninstall keeps settings and data'
    Assert ($null -eq (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue)) 'uninstall removes the startup entry that pointed to the program'

    Install $setups['0.1.1'] (Join-Path $testRoot 'install-again.log')
    Assert ((Get-ItemProperty $uninstallKey).DisplayVersion -eq '0.1.1') 'reinstall after uninstall succeeds'
    Uninstall @('/DELETEUSERDATA')
    Assert (!(Test-Path -LiteralPath $dataPath)) 'uninstall with /DELETEUSERDATA deletes settings and data'
    Write-Output "Evidence: $testRoot"
}
finally {
    if (Test-Path $uninstallKey) {
        Write-Warning 'Removing the leftover test installation.'
        try { Uninstall @() } catch { Write-Warning $_ }
    }
    Remove-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue
    Pop-Location
}
