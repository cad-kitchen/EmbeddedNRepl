#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory = "$PSScriptRoot/../artifacts/nuget")
. "$PSScriptRoot/Use-WorkspaceSdk.ps1"
Push-Location $nreplRoot
try {
    & $nreplSdk pack src/EmbeddedNRepl/EmbeddedNRepl.csproj -c Release --no-restore --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'NuGet package build failed.' }
    $version = (Get-Content VERSION -Raw).Trim()
    $package = Join-Path $OutputDirectory "EmbeddedNRepl.$version.nupkg"
    if (-not (Test-Path -LiteralPath $package)) { throw 'Expected package was not produced.' }
    Write-Output "NREPL_PACKAGE=$([IO.Path]::GetFullPath($package))"
} finally { Pop-Location }
