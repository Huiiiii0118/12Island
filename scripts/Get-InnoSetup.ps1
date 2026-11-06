# Restores the Inno Setup compiler pinned in installer/InnoSetup.csproj and returns the path of ISCC.exe.
$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'installer/InnoSetup.csproj'
dotnet restore $project | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup restore failed.' }
$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.ItemGroup.PackageDownload.Version.Trim('[', ']')
$packages = ((dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
$iscc = Join-Path $packages "tools.innosetup/$version/tools/ISCC.exe"
if (!(Test-Path -LiteralPath $iscc)) { throw "ISCC.exe not found at $iscc" }
$iscc
