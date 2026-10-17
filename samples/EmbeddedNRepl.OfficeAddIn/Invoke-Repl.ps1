[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port,
    [Parameter(Mandatory)][string]$Code,
    [ValidateRange(1, 300)][int]$TimeoutSeconds = 30
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$client = New-Object Net.Sockets.TcpClient
$deadline = [Diagnostics.Stopwatch]::StartNew()
function Read-Byte {
    $remaining = $TimeoutSeconds * 1000 - [int]$deadline.ElapsedMilliseconds
    if ($remaining -le 0) { throw 'Timed out waiting for nREPL.' }
    $stream.ReadTimeout = $remaining
    $value = $stream.ReadByte()
    if ($value -lt 0) { throw 'nREPL closed the connection before the response completed.' }
    return $value
}
function Read-Bencode([int]$First = -1, [int]$Depth = 0) {
    if ($Depth -gt 32) { throw 'Response nesting is too deep.' }
    if ($First -lt 0) { $First = Read-Byte }
    if ($First -eq 100) {
        $map = @{}
        while (($next = Read-Byte) -ne 101) {
            $key = Read-Bencode $next ($Depth + 1)
            $map[$key] = Read-Bencode -Depth ($Depth + 1)
        }
        return $map
    }
    if ($First -eq 108) {
        $items = New-Object 'Collections.Generic.List[object]'
        while (($next = Read-Byte) -ne 101) { $items.Add((Read-Bencode $next ($Depth + 1))) }
        return ,$items.ToArray()
    }
    $integer = $First -eq 105
    $text = if ($integer) { '' } else { [string][char]$First }
    $end = if ($integer) { 101 } else { 58 }
    while (($next = Read-Byte) -ne $end) {
        $text += [char]$next
        if ($text.Length -gt 20) { throw 'Invalid bencode number.' }
    }
    if ($integer) { return [long]$text }
    $length = [int]$text
    if ($length -lt 0 -or $length -gt 1048576) { throw 'Response string is too large.' }
    $bytes = New-Object byte[] $length
    for ($index = 0; $index -lt $length; $index++) { $bytes[$index] = Read-Byte }
    return $utf8.GetString($bytes)
}
try {
    $connect = $client.ConnectAsync('127.0.0.1', $Port)
    if (!$connect.Wait($TimeoutSeconds * 1000)) { throw 'Timed out connecting to nREPL.' }
    $stream = $client.GetStream()
    $stream.WriteTimeout = $TimeoutSeconds * 1000
    $encoded = $utf8.GetBytes('d4:code' + $utf8.GetByteCount($Code) + ':' + $Code + '2:id6:sample2:op4:evale')
    $stream.Write($encoded, 0, $encoded.Length)
    do {
        $response = Read-Bencode
        if ($response.ContainsKey('out')) { Write-Host -NoNewline $response.out }
        if ($response.ContainsKey('err')) { Write-Host -NoNewline $response.err }
        if ($response.ContainsKey('value')) { Write-Output $response.value }
        if ($response.status -contains 'error' -or $response.status -contains 'interrupted') {
            throw "nREPL evaluation failed: $($response.status -join ', ')"
        }
    } while ($response.status -notcontains 'done')
} finally { $client.Dispose() }
