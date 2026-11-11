$ErrorActionPreference = 'Stop'

function New-SwBridgeClient {
    param([string]$PackageRoot, [switch]$AllowWrite, [int]$SolidWorksPid = 0)
    $stateRoot = Join-Path $PackageRoot ('state\client-' + [Guid]::NewGuid().ToString('N'))
    $outputRoot = Join-Path $PackageRoot 'models'
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $PackageRoot 'SwBridge.exe'
    $info.WorkingDirectory = $PackageRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    # Arguments are passed directly to CreateProcess, not through a shell.
    $info.Arguments = '--attach-only --state-root "' + $stateRoot + '" --output-root "' + $outputRoot + '"'
    if ($AllowWrite) { $info.Arguments += ' --allow-write' }
    if ($SolidWorksPid -gt 0) { $info.Arguments += ' --pid ' + $SolidWorksPid }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    $null = $process.Start()
    $writer = [IO.StreamWriter]::new($process.StandardInput.BaseStream, [Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    return [PSCustomObject]@{ Process=$process; InputWriter=$writer; Counter=0; Transcript=[Collections.Generic.List[object]]::new(); StateRoot=$stateRoot; OutputRoot=$outputRoot; ErrorTask=$process.StandardError.ReadToEndAsync() }
}

function Invoke-SwBridgeRpc {
    param($Client, [string]$Method, $Parameters = @{}, [switch]$Notification)
    $Client.Counter++
    $request = @{ jsonrpc='2.0'; method=$Method; params=$Parameters }
    if (-not $Notification) { $request.id = $Client.Counter }
    $Client.InputWriter.WriteLine(($request | ConvertTo-Json -Depth 80 -Compress))
    if ($Notification) { return }
    $read = $Client.Process.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(35000)) { throw 'Bridge reply timed out. CAD outcome is not inferred from this timeout.' }
    $line = $read.GetAwaiter().GetResult()
    if ($null -eq $line) { throw ('Bridge exited before replying. ' + $Client.ErrorTask.GetAwaiter().GetResult()) }
    $response = $line | ConvertFrom-Json
    $Client.Transcript.Add([PSCustomObject]@{ request=$request; response=$response })
    if ($response.id -ne $Client.Counter) { throw 'MCP reply ID mismatch.' }
    if ($response.error) { throw ('MCP error: ' + $response.error.message) }
    return $response.result
}

function Initialize-SwBridgeClient {
    param($Client)
    $null = Invoke-SwBridgeRpc $Client 'initialize' @{ protocolVersion='2025-06-18'; capabilities=@{}; clientInfo=@{name='local-powerShell-client';version='0.2.0'} }
    Invoke-SwBridgeRpc $Client 'notifications/initialized' @{} -Notification
}

function Invoke-SwBridgeTool {
    param($Client, [string]$Name, $Arguments = @{})
    $response = Invoke-SwBridgeRpc $Client 'tools/call' @{name=$Name;arguments=$Arguments}
    $value = $response.structuredContent
    if ($null -eq $value) { $value = $response.content[0].text | ConvertFrom-Json }
    if ($value.status -eq 'error') { throw ($value.error.code + ': ' + $value.error.message) }
    return $value
}

function Close-SwBridgeClient {
    param($Client)
    if ($null -eq $Client) { return }
    $Client.InputWriter.Close()
    if (-not $Client.Process.WaitForExit(12000)) {
        # Stop only the bridge process owned by this client; never terminate SolidWorks.
        $Client.Process.Kill()
        $Client.Process.WaitForExit()
    }
    $Client.Process.Dispose()
}
