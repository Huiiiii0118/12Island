# Builds the portable zip from a dotnet publish folder. Dot-source this file, then call New-PortablePackage.
# The zip holds one folder with the published files and 12Island.portable, which lists every file of the package
# (relative paths, one per line). The app treats a folder with that list as a portable copy and replaces only the listed
# files when it updates itself (PortableUpdate in TwelveIsland.Core). The installer is built from the same folder without it.
function New-PortablePackage {
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [Parameter(Mandatory)][string]$Destination,
        [string]$Folder = '12 island'
    )
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $listName = '12Island.portable'
    $root = [IO.Path]::GetFullPath($SourceDir).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName.Substring($root.Length) } |
        Where-Object { $_ -ne $listName } | Sort-Object)
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination }
    $zip = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root $file), "$Folder/$($file.Replace('\', '/'))",
                [IO.Compression.CompressionLevel]::Optimal)
        }
        $writer = [IO.StreamWriter]::new($zip.CreateEntry("$Folder/$listName").Open(), [Text.UTF8Encoding]::new($false))
        try { foreach ($file in $files + $listName) { $writer.WriteLine($file) } }
        finally { $writer.Dispose() }
    }
    finally { $zip.Dispose() }
    Get-Item -LiteralPath $Destination
}
