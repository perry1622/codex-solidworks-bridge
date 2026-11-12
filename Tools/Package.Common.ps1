$ErrorActionPreference = 'Stop'
function Get-ToolkitSha256 {
    param([Parameter(Mandatory)][string]$Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Write-ToolkitJson {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string]$Path)
    $text = ($Value | ConvertTo-Json -Depth 80) + [Environment]::NewLine
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}
function Get-ToolkitFiles {
    param([string]$Root)
    Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($Root.TrimEnd('\').Length + 1).Replace('\', '/')
        $relative -ne 'manifest.json' -and $relative -notmatch '^\.git(?:/|$)' -and $relative -notmatch '^(Bridge/)?(state|models|reports)/' -and $relative -notmatch '\.mcp\.toml$' -and $relative -notmatch '(^|/)(__pycache__|bin|obj)/' -and $relative -notmatch '\.(pyc|zip)$'
    } | Sort-Object FullName
}
