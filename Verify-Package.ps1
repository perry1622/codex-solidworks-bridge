[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Tools\Package.Common.ps1')
$root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$manifest = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json
$errors = [Collections.Generic.List[string]]::new()
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { $errors.Add('Invalid package path: ' + $relative); continue }
    if (-not $expected.Add($relative)) { $errors.Add('Duplicate package path: ' + $relative) }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $errors.Add('Missing file: ' + $relative); continue }
    if ((Get-Item -LiteralPath $path).Length -ne $entry.size_bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { $errors.Add('File changed: ' + $relative) }
}
foreach ($file in @(Get-ToolkitFiles $root)) {
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
    if (-not $expected.Contains($relative)) { $errors.Add('Unlisted package file: ' + $relative) }
}
[ordered]@{status=$(if ($errors.Count -eq 0) {'passed'} else {'failed'}); files_checked=$expected.Count; errors=$errors.ToArray(); runtime_or_cad_test_performed=$false} | ConvertTo-Json -Depth 20
if ($errors.Count -gt 0) { exit 1 }
