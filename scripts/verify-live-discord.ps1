param([string]$Report, [string]$Output)
$ErrorActionPreference = 'Stop'
$settingsFile = Join-Path $env:LOCALAPPDATA 'NowPlaying\settings.json'
$settings = Get-Content -LiteralPath $settingsFile -Encoding UTF8 -Raw | ConvertFrom-Json
$diagnostic = Get-Content -LiteralPath $Report -Encoding UTF8 -Raw | ConvertFrom-Json
if (-not $settings.Enabled) { throw 'Publishing is disabled; live verification skipped.' }
if (-not $diagnostic.activity) { throw 'No playing media for live verification.' }
$script:rpcPipe = $null
function Write-Frame([int]$Opcode, [byte[]]$Payload) {
    $header = [BitConverter]::GetBytes($Opcode) + [BitConverter]::GetBytes([int]$Payload.Length)
    $script:rpcPipe.Write($header,0,8)
    $script:rpcPipe.Write($Payload,0,$Payload.Length)
    $script:rpcPipe.Flush()
}
function Read-Exact([int]$Size) {
    $buffer = New-Object byte[] $Size
    $offset = 0
    while ($offset -lt $Size) {
        $read = $script:rpcPipe.ReadAsync($buffer,$offset,($Size-$offset))
        if (-not $read.Wait(5000)) { throw 'Discord RPC read timed out.' }
        if ($read.Result -eq 0) { throw 'Discord RPC closed the pipe.' }
        $offset += $read.Result
    }
    return ,$buffer
}
function Read-Message {
    while ($true) {
        $header = Read-Exact 8
        $opcode = [BitConverter]::ToInt32($header,0)
        $length = [BitConverter]::ToInt32($header,4)
        if ($length -lt 0 -or $length -gt 1048576) { throw 'Invalid frame size.' }
        $payload = Read-Exact $length
        if ($opcode -eq 3) { Write-Frame 4 $payload; continue }
        $message = [Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
        if ($opcode -eq 2 -or $message.evt -eq 'ERROR') { throw ('Discord RPC error: ' + ($message | ConvertTo-Json -Compress -Depth 8)) }
        if ($opcode -eq 1) { return $message }
    }
}
try {
    foreach ($index in 0..9) {
        $candidate = New-Object IO.Pipes.NamedPipeClientStream '.',("discord-ipc-$index"),([IO.Pipes.PipeDirection]::InOut),([IO.Pipes.PipeOptions]::Asynchronous)
        try { $candidate.Connect(100); $script:rpcPipe = $candidate; break }
        catch { $candidate.Dispose() }
    }
    if (-not $script:rpcPipe) { throw 'Discord is not running.' }
    $handshake = @{ v = 1; client_id = [string]$settings.ApplicationId } | ConvertTo-Json -Compress
    Write-Frame 0 ([Text.Encoding]::UTF8.GetBytes($handshake))
    $ready = Read-Message
    if ($ready.evt -ne 'READY') { throw 'Discord did not acknowledge handshake.' }
    $nonce = [Guid]::NewGuid().ToString('N')
    $activity = $diagnostic.activity
    $payload = @{ cmd = 'SET_ACTIVITY'; args = @{ pid = $PID; activity = $activity }; nonce = $nonce } | ConvertTo-Json -Depth 8 -Compress
    Write-Frame 1 ([Text.Encoding]::UTF8.GetBytes($payload))
    $artworkTimer = [Diagnostics.Stopwatch]::StartNew()
    $ack = Read-Message
    if ($ack.nonce -ne $nonce) { throw 'Unexpected Discord RPC acknowledgment.' }
    if ($ack.data.name -ne $activity.name -or $ack.data.type -ne $activity.type) { throw 'Discord did not apply the requested activity name and type.' }
    $image = [string]$activity.assets.large_image
    $publicArtwork = $image -like 'https://i.ytimg.com/*' -or $image -match '^https://[^/]+\.mzstatic\.com/'
    if ($publicArtwork) {
        $expected = [Uri]$activity.assets.large_image
        $actual = [string]$ack.data.assets.large_image
        if (-not $actual.Contains($expected.Host + $expected.AbsolutePath)) { throw 'Discord did not resolve the requested artwork.' }
    }
    $artworkTimer.Stop()
    $clearNonce = [Guid]::NewGuid().ToString('N')
    $clearPayload = @{ cmd = 'SET_ACTIVITY'; args = @{ pid = $PID; activity = $null }; nonce = $clearNonce } | ConvertTo-Json -Depth 4 -Compress
    Write-Frame 1 ([Text.Encoding]::UTF8.GetBytes($clearPayload))
    $clearAck = Read-Message
    if ($clearAck.nonce -ne $clearNonce) { throw 'Discord did not acknowledge activity removal.' }
    $result = @{ success = $true; acknowledged = $true; requestedName = $activity.name; requestedType = $activity.type; artworkVerified = $publicArtwork; artworkAcknowledgedMilliseconds = $artworkTimer.Elapsed.TotalMilliseconds; thumbnailVerified = ($image -like 'https://i.ytimg.com/*'); clearAcknowledged = $true }
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding UTF8
    Write-Output 'Discord confirmed activity, thumbnail when available, and activity removal.'
}
finally { if ($script:rpcPipe) { $script:rpcPipe.Dispose() } }
