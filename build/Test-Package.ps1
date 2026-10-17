#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('net472', 'net8.0', 'net10.0')][string]$Framework = 'net10.0',
    [string]$PackageDirectory = "$PSScriptRoot/../artifacts/nuget"
)
. "$PSScriptRoot/Use-WorkspaceSdk.ps1"
$version = (Get-Content (Join-Path $nreplRoot 'VERSION') -Raw).Trim()
$clojure = ([xml](Get-Content (Join-Path $nreplRoot 'Directory.Packages.props') -Raw)).Project.ItemGroup.PackageVersion |
    Where-Object Include -eq 'Clojure' | Select-Object -ExpandProperty Version
$fixture = Join-Path $nreplRoot ('artifacts/package-consumer/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
# Override parent build imports: this application has no project/source references.
'<Project />' | Set-Content (Join-Path $fixture 'Directory.Build.props')
'<Project />' | Set-Content (Join-Path $fixture 'Directory.Build.targets')
'<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>' |
    Set-Content (Join-Path $fixture 'Directory.Packages.props')
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>$Framework</TargetFramework>
    <LangVersion>12.0</LangVersion><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects><GenerateBindingRedirectsOutputType>true</GenerateBindingRedirectsOutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="EmbeddedNRepl" Version="$version" />
    <PackageReference Include="Clojure" Version="$clojure" />
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" Condition="'$Framework' == 'net472'" />
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $fixture 'Consumer.csproj')
Copy-Item (Join-Path $nreplRoot 'tests/PackageConsumer/Program.cs') -Destination $fixture
Copy-Item (Join-Path $nreplRoot 'tests/EmbeddedNRepl.TestSupport/WirePeer.cs') -Destination $fixture
Copy-Item (Join-Path $nreplRoot 'tests/EmbeddedNRepl.TestSupport/TestDeadline.cs') -Destination $fixture
$feed = [Security.SecurityElement]::Escape((Resolve-Path -LiteralPath $PackageDirectory).Path)
@"
<configuration><packageSources><clear /><add key="package-under-test" value="$feed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources><packageSourceMapping><packageSource key="package-under-test"><package pattern="EmbeddedNRepl" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping></configuration>
"@ | Set-Content (Join-Path $fixture 'NuGet.Config')
Push-Location $fixture
try {
    # An isolated package cache ensures an older development build cannot mask package defects.
    & $nreplSdk restore Consumer.csproj --configfile NuGet.Config --packages (Join-Path $fixture 'packages')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated package consumer restore failed.' }
    & $nreplSdk build Consumer.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Package consumer build failed.' }
    if ($Framework -eq 'net472') { & "./bin/Release/$Framework/Consumer.exe" }
    elseif ($Framework -eq 'net8.0' -and -not (Test-Path (Join-Path (Split-Path $nreplSdk) 'shared/Microsoft.NETCore.App/8.*'))) {
        & "$env:ProgramFiles/dotnet/dotnet.exe" "./bin/Release/$Framework/Consumer.dll"
    } else { & $nreplSdk "./bin/Release/$Framework/Consumer.dll" }
    if ($LASTEXITCODE -ne 0) { throw 'Package consumer runtime verification failed.' }
} finally { Pop-Location }
Write-Output "PACKAGE_CONSUMER_EVIDENCE=$fixture"
