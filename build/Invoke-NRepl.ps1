#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$Port,
    [Parameter(Mandatory)][string]$Code,
    [ValidateRange(1, 2147483647)][int]$TimeoutMilliseconds = 10000,
    [string]$ClientScript = $env:EMBEDDED_NREPL_CLIENT,
    [string]$Babashka = $env:EMBEDDED_NREPL_BB
)
$ErrorActionPreference = 'Stop'
if (-not $ClientScript) { $ClientScript = Join-Path $env:USERPROFILE '.local/bin/clj-nrepl-eval' }
if (-not (Test-Path -LiteralPath $ClientScript -PathType Leaf)) { throw "Installed clj-nrepl-eval script not found: $ClientScript" }
if (-not $Babashka) { $Babashka = (Get-Command bb.exe -ErrorAction Stop).Source }
$nreplShim = [IO.Path]::ChangeExtension($Babashka, '.shim')
if (Test-Path -LiteralPath $nreplShim -PathType Leaf) {
    $nreplShimText = Get-Content -LiteralPath $nreplShim -Raw
    if ($nreplShimText -match '(?m)^path\s*=\s*"([^"]+)"') { $Babashka = $Matches[1] }
}
$Babashka = (Resolve-Path -LiteralPath $Babashka).Path
$nreplStart = [Diagnostics.ProcessStartInfo]::new($Babashka)
$nreplStart.UseShellExecute = $false
$nreplStart.CreateNoWindow = $true
$nreplStart.RedirectStandardInput = $true
$nreplStart.RedirectStandardOutput = $true
$nreplStart.RedirectStandardError = $true
$nreplStart.StandardOutputEncoding = [Text.Encoding]::UTF8
$nreplStart.StandardErrorEncoding = [Text.Encoding]::UTF8
foreach ($nreplArgument in @('-f', $ClientScript, '--', '--host', '127.0.0.1', '--port', "$Port", '--timeout', "$TimeoutMilliseconds")) {
    $nreplStart.ArgumentList.Add($nreplArgument)
}
$nreplStart.Environment['PATH'] = (Split-Path -Parent $Babashka) + [IO.Path]::PathSeparator + $env:PATH
$nreplProcess = [Diagnostics.Process]::Start($nreplStart)
$nreplOutput = $nreplProcess.StandardOutput.ReadToEndAsync()
$nreplError = $nreplProcess.StandardError.ReadToEndAsync()
try {
    # Avoid extra shell/shim parents and explicitly encode redirected source as UTF-8.
    $nreplBytes = [Text.Encoding]::UTF8.GetBytes($Code)
    $nreplProcess.StandardInput.BaseStream.Write($nreplBytes, 0, $nreplBytes.Length)
    $nreplProcess.StandardInput.Close()
    $nreplProcess.WaitForExit()
    if ($nreplOutput.Result) { Write-Output $nreplOutput.Result.TrimEnd() }
    if ($nreplError.Result) { [Console]::Error.Write($nreplError.Result) }
    if ($nreplProcess.ExitCode -ne 0) { throw "clj-nrepl-eval exited with code $($nreplProcess.ExitCode)." }
}
finally { $nreplProcess.Dispose() }
