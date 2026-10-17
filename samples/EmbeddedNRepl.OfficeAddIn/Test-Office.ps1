[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Word', 'Excel')][string]$Application)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this check in Windows PowerShell 5.1 with -STA.' }
$processName = if ($Application -eq 'Word') { 'WINWORD' } else { 'EXCEL' }
if (Get-Process -Name $processName -ErrorAction SilentlyContinue) { throw "Close $Application before this disposable-document check." }
$app = $null
$addIns = $null
$addIn = $null
$portFile = $null
$created = $null
$verifiedApplication = $false
$registration = $null
$registrationPath = "HKCU:\Software\Microsoft\Office\$Application\Addins\EmbeddedNRepl.${Application}AddIn"
if (!('OfficeTestWindow' -as [type])) {
    Add-Type 'using System; using System.Runtime.InteropServices; public static class OfficeTestWindow { [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process); }'
}
function Assert-Eval([string]$Code, [string]$Expected = 'true') {
    $actual = @(& "$PSScriptRoot/Invoke-Repl.ps1" -Port $port -Code $Code) -join "`n"
    if ($actual -ne $Expected) { throw "Expected $Expected; got $actual for $Code" }
}
try {
    $registration = Get-ItemProperty -LiteralPath $registrationPath
    # Office remembers manual disconnection. Enable startup for this run and restore it below.
    Set-ItemProperty -LiteralPath $registrationPath -Name LoadBehavior -Value 3
    $exe = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$processName.exe").'(default)'
    $argument = if ($Application -eq 'Word') { '/n' } else { '/x' }
    # Normal Office startup exercises the real add-in loader; automation startup may omit add-ins.
    $created = Start-Process -FilePath $exe -ArgumentList $argument -WindowStyle Hidden -PassThru
    $stateDirectory = Join-Path $env:LOCALAPPDATA 'EmbeddedNRepl/Office'
    $portFile = Join-Path $stateDirectory ($Application.ToLowerInvariant() + '-' + $created.Id + '.port')
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (!(Test-Path -LiteralPath $portFile) -and [DateTime]::UtcNow -lt $deadline) {
        if ($created.HasExited) { throw 'Office exited before starting nREPL. Inspect the sample log.' }
        Start-Sleep -Milliseconds 100
    }
    if (!(Test-Path -LiteralPath $portFile)) { throw "No endpoint appeared. Inspect the log beside $portFile" }
    $port = [int](Get-Content -LiteralPath $portFile -Raw)
    $ns = $Application.ToLowerInvariant()
    Assert-Eval "(= (.Id (System.Diagnostics.Process/GetCurrentProcess)) $($created.Id))"
    Assert-Eval ('(and (= (.OwnerThreadId {0}/host) (.ManagedThreadId System.Threading.Thread/CurrentThread)) (= "STA" (str (.GetApartmentState System.Threading.Thread/CurrentThread))))' -f $ns)
    if ($Application -eq 'Word') {
        Assert-Eval '(do (word/new-document!) (word/append! "Hello from EmbeddedNRepl\r\n") (.Contains (word/text) "Hello from EmbeddedNRepl"))'
        Assert-Eval '(= :blocked @(future (try (word/text) (catch System.Exception e :blocked))))'
    } else {
        Assert-Eval '(do (excel/new-workbook!) (excel/set-value! "A1" 20.0) (excel/set-value! "A2" 22.0) (excel/set-formula! "A3" "=SUM(A1:A2)") (= 42.0 (excel/value "A3")))'
        Assert-Eval '(= :blocked @(future (try (excel/value "A1") (catch System.Exception e :blocked))))'
        Assert-Eval '(try (excel/value "A1:A2") false (catch System.Exception e true))'
    }
    # A document must exist before Office publishes its automation object in the ROT.
    $app = [Runtime.InteropServices.Marshal]::GetActiveObject("$Application.Application")
    $owner = [uint32]0
    if ($Application -eq 'Word') {
        $window = $app.ActiveWindow
        try { $handle = $window.Hwnd } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($window) }
    } else { $handle = $app.Hwnd }
    [void][OfficeTestWindow]::GetWindowThreadProcessId([IntPtr]$handle, [ref]$owner)
    if ($owner -ne $created.Id) {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app)
        $app = $null
        throw 'The automation object belongs to another Office instance.'
    }
    $verifiedApplication = $true
    $addIns = $app.COMAddIns
    $addInId = [object]"EmbeddedNRepl.${Application}AddIn"
    $addIn = $addIns.Item([ref]$addInId)
    $addIn.Connect = $false
    if (Test-Path -LiteralPath $portFile) { throw 'The endpoint file survived add-in disconnection.' }
    $probe = New-Object Net.Sockets.TcpClient
    try {
        try { $probe.Connect('127.0.0.1', $port) } catch [Net.Sockets.SocketException] { }
        if ($probe.Connected) { throw 'The endpoint is still listening after disconnection.' }
    } finally { $probe.Dispose() }
    $addIn.Connect = $true
    $port = [int](Get-Content -LiteralPath $portFile -Raw)
    Assert-Eval '(= 5 (+ 2 3))'
    if ($Application -eq 'Word') { Assert-Eval '(.Contains (word/text) "Hello from EmbeddedNRepl")' }
    else { Assert-Eval '(= 42.0 (excel/value "A3"))' }
    Write-Output "$Application passed: in-process eval, STA owner, document changes, background guard, disconnect and reconnect."
} finally {
    try {
        try {
            if ($addIn) { try { $addIn.Connect = $false } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($addIn) } }
        } finally {
            try {
                if ($addIns) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($addIns) }
                if ($app) {
                    try {
                        if ($verifiedApplication) {
                            if ($Application -eq 'Word') { $save = [object]0; $app.Quit([ref]$save) }
                            else { $app.DisplayAlerts = $false; $app.Quit() }
                        }
                    } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app) }
                }
            } finally {
                if ($created -and !$verifiedApplication -and !$created.HasExited) {
                    # Only the exact process started above; never kill an existing Office process.
                    $created.Kill()
                    if ($portFile -and (Test-Path -LiteralPath $portFile)) { Remove-Item -LiteralPath $portFile }
                }
                if ($created -and !$created.HasExited -and !$created.WaitForExit(10000)) {
                    Write-Warning "The disposable Office process $($created.Id) has not exited yet."
                }
            }
        }
    } finally {
        if ($registration) { Set-ItemProperty -LiteralPath $registrationPath -Name LoadBehavior -Value $registration.LoadBehavior }
    }
}
