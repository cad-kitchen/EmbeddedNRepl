[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Word', 'Excel')][string[]]$Application = @('Word', 'Excel'),
    [Parameter(Mandatory)][ValidateSet('x86', 'x64')][string]$OfficeBitness,
    [string]$AssemblyPath = (Join-Path $PSScriptRoot 'bin/Release/net472/EmbeddedNRepl.OfficeAddIn.dll'),
    [switch]$Unregister,
    [switch]$LoadAtStartup
)
$ErrorActionPreference = 'Stop'
$assemblyFile = [IO.Path]::GetFullPath($AssemblyPath)
if (!$Unregister) {
    if (!(Test-Path -LiteralPath $assemblyFile)) { throw "Build the sample first: $assemblyFile" }
    $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($assemblyFile).FullName
}
$view = if ($OfficeBitness -eq 'x64') { [Microsoft.Win32.RegistryView]::Registry64 } else { [Microsoft.Win32.RegistryView]::Registry32 }
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
$ids = @{ Word = '{AFB33272-53B4-44A6-A267-A7D028F64510}'; Excel = '{74205B9F-B51A-4C59-9799-EF621DD61852}' }
function Set-Values([string]$Path, [hashtable]$Values) {
    $key = $registry.CreateSubKey($Path)
    try {
        foreach ($name in $Values.Keys) { $key.SetValue($name, $Values[$name]) }
    } finally { $key.Dispose() }
}
try {
    foreach ($product in $Application) {
        $processName = if ($product -eq 'Word') { 'WINWORD' } else { 'EXCEL' }
        if (Get-Process -Name $processName -ErrorAction SilentlyContinue) {
            throw "Close $product before changing its sample registration."
        }
        $progId = "EmbeddedNRepl.${product}AddIn"
        $className = "EmbeddedNRepl.OfficeAddIn.${product}AddIn"
        $classKey = "Software\Classes\CLSID\$($ids[$product])"
        $progIdKey = "Software\Classes\$progId"
        $officeKey = "Software\Microsoft\Office\$product\Addins\$progId"
        $existing = $registry.OpenSubKey("$classKey\InprocServer32")
        try {
            if ($existing -and ($existing.GetValue('Class') -ne $className -or
                $existing.GetValue('CodeBase') -ne ([Uri]$assemblyFile).AbsoluteUri)) {
                throw 'This sample is registered from another location. Unregister that copy first.'
            }
        } finally { if ($existing) { $existing.Dispose() } }
        if (!$PSCmdlet.ShouldProcess("HKCU $product sample ($OfficeBitness)", $(if ($Unregister) { 'Unregister' } else { 'Register' }))) { continue }
        if ($Unregister) {
            foreach ($path in @($officeKey, $progIdKey, $classKey)) { $registry.DeleteSubKeyTree($path, $false) }
        } else {
            Set-Values $classKey @{ '' = $progId }
            Set-Values "$classKey\ProgId" @{ '' = $progId }
            Set-Values "$classKey\InprocServer32" @{
                '' = 'mscoree.dll'; ThreadingModel = 'Both'; Class = $className
                Assembly = $assemblyName; RuntimeVersion = 'v4.0.30319'; CodeBase = ([Uri]$assemblyFile).AbsoluteUri
            }
            Set-Values $progIdKey @{ '' = "EmbeddedNRepl $product sample" }
            Set-Values "$progIdKey\CLSID" @{ '' = $ids[$product] }
            $loadBehavior = if ($LoadAtStartup) { 3 } else { 8 }
            Set-Values $officeKey @{
                FriendlyName = "EmbeddedNRepl $product sample"
                Description = 'Local developer nREPL inside Office'
                LoadBehavior = $loadBehavior
            }
        }
        Write-Output "${product}: $(if ($Unregister) { 'unregistered' } else { 'registered' }) for the current user ($OfficeBitness)."
    }
} finally { $registry.Dispose() }
