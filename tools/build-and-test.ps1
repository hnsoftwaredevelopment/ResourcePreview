<#
  Builds the solution (Debug) and runs all unit tests, without the Visual Studio IDE.
  Finds MSBuild and vstest.console through vswhere, so it works with any VS 2022 17.14+ or VS 2026 install.

  Example:
    powershell -File tools\build-and-test.ps1
#>
param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
# Prefer a full IDE: Build Tools may be newer but lack the .NET SDK resolver and the VSSDK build tasks.
$ides = 'Microsoft.VisualStudio.Product.Enterprise', 'Microsoft.VisualStudio.Product.Professional', 'Microsoft.VisualStudio.Product.Community'
$vs = & $vswhere -latest -prerelease -products $ides -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vs) { $vs = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath }
if (-not $vs) { throw 'No Visual Studio installation with MSBuild found.' }

$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
& $msbuild (Join-Path $root 'ResourcePreview.sln') -restore "-p:Configuration=$Configuration" -p:DeployExtension=false -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# vstest wants to create a TestResults folder in the working directory, so run it from the test output folder.
$vstest = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe'
$testDll = Join-Path $root "Builds\ResourcePreview.Tests\$Configuration\net48\ResourcePreview.Tests.dll"
Push-Location (Split-Path $testDll)
try { & $vstest $testDll }
finally { Pop-Location }
exit $LASTEXITCODE
