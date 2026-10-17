[CmdletBinding()]
param(
    [ValidateRange(0, 65535)][int]$Port = 7888,
    [ValidateSet('net472', 'net8.0', 'net10.0')][string]$Framework = 'net10.0',
    [switch]$NoBuild
)
. "$PSScriptRoot/Use-WorkspaceSdk.ps1"
$nreplConsoleProject = Join-Path $nreplRoot 'samples/EmbeddedNRepl.ConsoleHost/EmbeddedNRepl.ConsoleHost.csproj'
if (-not $NoBuild) {
    & $nreplSdk build $nreplConsoleProject -c Release -f $Framework --no-restore --verbosity quiet -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Console host build failed. Restore the pinned solution dependencies first.' }
}
& $nreplSdk run --project $nreplConsoleProject -c Release -f $Framework --no-build -- --port $Port
if ($LASTEXITCODE -ne 0) { throw "Console host exited with code $LASTEXITCODE." }
