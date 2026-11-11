[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$archive = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArchivePath)
if (Test-Path -LiteralPath $archive) { throw 'Archive already exists; use a new filename.' }
$verification = & (Join-Path $root 'Verify-Package.ps1') | ConvertFrom-Json
if ($verification.status -ne 'passed') { throw 'Package verification failed.' }
$manifest = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.FileStream]::new($archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $prefix = (Split-Path -Leaf $root) + '/'
        foreach ($relative in @($manifest.files | ForEach-Object path) + @('manifest.json')) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $root $relative), ($prefix + $relative), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Write-Output $archive
