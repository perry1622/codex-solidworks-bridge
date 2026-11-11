[CmdletBinding()]
param([string]$PackageRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Package.Common.ps1')
$root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
$packageInfo = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
$entries = @(foreach ($file in @(Get-ToolkitFiles $root)) {
    [ordered]@{path=$file.FullName.Substring($root.Length + 1).Replace('\', '/'); size_bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest = [ordered]@{
    package=$packageInfo.name; release=$packageInfo.version
    generated_utc=[DateTime]::UtcNow.ToString('o')
    checksum_purpose='Accidental-change detection; checksums alone do not authenticate the publisher.'
    files=$entries
}
Write-ToolkitJson $manifest (Join-Path $root 'manifest.json')
Write-Output (Join-Path $root 'manifest.json')
