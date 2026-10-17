[CmdletBinding()]
param(
    [ValidateSet('Unit', 'Transport', 'Independent', 'ClojureClr', 'Client', 'All')]
    [string]$Layer = 'Independent',
    [ValidateSet('net472', 'net8.0', 'net10.0')]
    [string[]]$Framework = @('net472', 'net8.0', 'net10.0'),
    [string]$Filter,
    [string]$RunName = 'tests',
    [string]$Net8SdkRoot,
    [switch]$Coverage,
    [string]$CoverageSettings = "$PSScriptRoot/coverage.runsettings",
    [switch]$NoBuild
)
. "$PSScriptRoot/Use-WorkspaceSdk.ps1"
if ($Coverage) { $CoverageSettings = (Resolve-Path -LiteralPath $CoverageSettings).Path }
$nreplMap = @{
    Unit = @('EmbeddedNRepl.UnitTests')
    Transport = @('EmbeddedNRepl.TransportTests')
    Independent = @('EmbeddedNRepl.UnitTests', 'EmbeddedNRepl.TransportTests')
    ClojureClr = @('EmbeddedNRepl.ClojureClr.Tests')
    Client = @('EmbeddedNRepl.ClientTests')
    All = @('EmbeddedNRepl.UnitTests', 'EmbeddedNRepl.TransportTests', 'EmbeddedNRepl.ClojureClr.Tests', 'EmbeddedNRepl.ClientTests')
}
if ($RunName -notmatch '^[A-Za-z0-9_.-]+$') { throw 'RunName must be a simple file name.' }
$nreplRun = Join-Path $nreplRoot ("artifacts/tests/" + $RunName + "-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $nreplRun -Force | Out-Null
$nreplResults = @()
$nreplFailed = $false
foreach ($nreplProjectName in $nreplMap[$Layer]) {
    $nreplProject = Join-Path $nreplRoot "tests/$nreplProjectName/$nreplProjectName.csproj"
    foreach ($nreplTarget in $Framework) {
        $nreplPrefix = "$nreplProjectName-$nreplTarget"
        $nreplBuildLog = Join-Path $nreplRun "$nreplPrefix.build.log"
        $nreplRow = [pscustomobject]@{
            project = $nreplProjectName; target = $nreplTarget; status = 'unverified'
            stage = 'build'; exitCode = $null; total = $null; passed = $null; failed = $null
            notExecuted = $null; environment = @(); trx = $null; error = $null
            buildLog = $(if ($NoBuild) { $null } else { $nreplBuildLog })
            coverage = $null; publishLog = $null
        }
        try {
            if (-not $NoBuild) {
                & $nreplSdk build $nreplProject --framework $nreplTarget -c Release --no-restore --verbosity quiet -warnaserror 2>&1 |
                    Tee-Object -FilePath $nreplBuildLog
                $nreplRow.exitCode = $LASTEXITCODE
                if ($nreplRow.exitCode -ne 0) {
                    $nreplRow.status = 'build-failed'
                    $nreplRow.error = 'Build failed before behavioral tests could run.'
                    continue
                }
            }
            $nreplRow.stage = 'test'
            $nreplBin = Join-Path $nreplRoot "tests/$nreplProjectName/bin/Release/$nreplTarget"
            $nreplResultDirectory = $nreplRun
            if ($Coverage) {
                # Instrument a published copy; never modify DLLs in normal build/CAD outputs.
                $nreplRow.stage = 'publish'
                $nreplOriginalBin = $nreplBin
                $nreplBin = Join-Path $nreplRun "published/$nreplPrefix"
                $nreplRow.publishLog = Join-Path $nreplRun "$nreplPrefix.publish.log"
                & $nreplSdk publish $nreplProject --framework $nreplTarget -c Release --no-build --no-restore --output $nreplBin --verbosity quiet 2>&1 |
                    Tee-Object -FilePath $nreplRow.publishLog
                $nreplRow.exitCode = $LASTEXITCODE
                if ($nreplRow.exitCode -ne 0) {
                    $nreplRow.status = 'publish-failed'
                    $nreplRow.error = 'Publishing the isolated coverage copy failed.'
                    continue
                }
                # Publish omits private testhost dependencies from the deps manifest.
                # Retain the working build's complete runtime graph beside the collector.
                Get-ChildItem -LiteralPath $nreplOriginalBin | Copy-Item -Destination $nreplBin -Recurse -Force
                $nreplResultDirectory = Join-Path $nreplRun $nreplPrefix
                $nreplRow.stage = 'test'
            }
            $nreplAssembly = Join-Path $nreplBin "$nreplProjectName.dll"
            $nreplTrx = Join-Path $nreplResultDirectory "$nreplPrefix.trx"
            $nreplArgs = @($nreplAssembly, '/Platform:x64', "/TestAdapterPath:$nreplBin",
                "/ResultsDirectory:$nreplResultDirectory", "/Logger:trx;LogFileName=$nreplPrefix.trx", '/Logger:console;verbosity=normal')
            if ($Filter) { $nreplArgs += "/TestCaseFilter:$Filter" }
            if ($Coverage) { $nreplArgs += @('/Collect:XPlat Code Coverage', "/Settings:$CoverageSettings") }
            if ($nreplTarget -eq 'net8.0') {
                # Use the installed .NET 8 host, not SDK 10's private runtime directory.
                $nreplNet8Root = $Net8SdkRoot
                if (-not $nreplNet8Root) {
                    $nreplNet8Root = Split-Path -Parent $nreplSdk
                    if (-not (Test-Path -LiteralPath (Join-Path $nreplNet8Root 'sdk/8.0.420/vstest.console.dll'))) {
                        $nreplNet8Root = Join-Path $env:ProgramFiles 'dotnet'
                    }
                }
                $nreplNet8Host = Join-Path $nreplNet8Root 'dotnet.exe'
                $nreplVstest = Join-Path $nreplNet8Root 'sdk/8.0.420/vstest.console.dll'
                & $nreplNet8Host exec $nreplVstest @nreplArgs 2>&1 | Tee-Object -FilePath (Join-Path $nreplRun "$nreplPrefix.log")
            } else {
                & $nreplSdk vstest @nreplArgs 2>&1 | Tee-Object -FilePath (Join-Path $nreplRun "$nreplPrefix.log")
            }
            $nreplExit = $LASTEXITCODE
            $nreplStatus = 'unverified'
            $nreplCounters = $null
            if (Test-Path -LiteralPath $nreplTrx) {
                [xml]$nreplXml = Get-Content -LiteralPath $nreplTrx -Raw
                $nreplCounters = $nreplXml.TestRun.ResultSummary.Counters
                if ($nreplExit -eq 0 -and [int]$nreplCounters.total -gt 0 -and
                    [int]$nreplCounters.passed -eq [int]$nreplCounters.total) {
                    $nreplStatus = 'passed'
                } elseif ([int]$nreplCounters.failed -gt 0) { $nreplStatus = 'failed' }
            }
            $nreplRuntimeLines = @(Select-String -LiteralPath (Join-Path $nreplRun "$nreplPrefix.log") -Pattern 'ACTUAL_RUNTIME=|PROCESS_ARCHITECTURE=' | ForEach-Object { $_.Line.Trim() })
            $nreplRow.status = $nreplStatus
            $nreplRow.exitCode = $nreplExit
            $nreplRow.total = $nreplCounters.total
            $nreplRow.passed = $nreplCounters.passed
            $nreplRow.failed = $nreplCounters.failed
            $nreplRow.notExecuted = $nreplCounters.notExecuted
            $nreplRow.environment = $nreplRuntimeLines
            $nreplRow.trx = $nreplTrx
            if ($Coverage) {
                $nreplRow.stage = 'coverage'
                # TRX also copies attachments into a deeper machine-specific directory.
                # Use the collector's original report, not the duplicate TRX attachment.
                $nreplReports = @(Get-ChildItem -LiteralPath $nreplResultDirectory -Recurse -Depth 1 -File -Filter coverage.cobertura.xml)
                if ($nreplReports.Count -ne 1) {
                    $nreplRow.status = 'coverage-failed'
                    $nreplRow.error = 'Expected exactly one coverage report for this test run.'
                } else {
                    [xml]$nreplCoverage = Get-Content -LiteralPath $nreplReports[0].FullName -Raw
                    if ($nreplCoverage.DocumentElement.Name -ne 'coverage' -or [int]$nreplCoverage.coverage.'lines-valid' -le 0) {
                        $nreplRow.status = 'coverage-failed'
                        $nreplRow.error = 'Coverage report contains no instrumented production lines.'
                    } else { $nreplRow.coverage = $nreplReports[0].FullName }
                }
            }
        } catch {
            $nreplRow.status = switch ($nreplRow.stage) {
                'build' { 'build-failed' }
                'publish' { 'publish-failed' }
                'coverage' { 'coverage-failed' }
                default { 'runner-failed' }
            }
            $nreplRow.exitCode = -1
            $nreplRow.error = $_.Exception.Message
            Write-Output "$nreplProjectName/$nreplTarget : $($nreplRow.error)"
        } finally {
            if ($nreplRow.status -ne 'passed') { $nreplFailed = $true }
            $nreplResults += $nreplRow
            ConvertTo-Json -InputObject @($nreplResults) -Depth 6 |
                Set-Content -LiteralPath (Join-Path $nreplRun 'results.json') -Encoding utf8
        }
    }
}
Write-Output "EVIDENCE_DIRECTORY=$nreplRun"
if ($nreplFailed) { exit 1 }
