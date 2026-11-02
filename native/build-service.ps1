$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw 'Windows .NET Framework compiler not found.' }
$shared = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'shared') -Filter '*.cs' | ForEach-Object { $_.FullName })
$service = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'service') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $csc /nologo /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.ServiceProcess.dll "/out:$projectRoot\bin\finley-tunnel-service.exe" $shared $service
if ($LASTEXITCODE -ne 0) { throw 'Could not build the tunnel service.' }
& $csc /nologo /target:exe /platform:x64 /reference:System.Web.Extensions.dll "/out:$projectRoot\bin\finley-service-client.exe" $shared "$PSScriptRoot\client\Client.cs"
if ($LASTEXITCODE -ne 0) { throw 'Could not build the service client.' }
& "$PSScriptRoot\build-helper.ps1"
& $csc /nologo /target:exe /platform:x64 /reference:System.Web.Extensions.dll /reference:System.ServiceProcess.dll /main:ServiceHarness "/out:$projectRoot\tests\fixtures\service-harness.exe" $shared $service "$projectRoot\tests\fixtures\ServiceHarness.cs"
if ($LASTEXITCODE -ne 0) { throw 'Could not build the service test harness.' }
