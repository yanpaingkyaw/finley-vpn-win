$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw 'Windows .NET Framework C# compiler not found.' }
& $csc /nologo /target:exe "/out:$projectRoot\bin\finley-tunnel-helper.exe" "$projectRoot\native\FinleyTunnelHelper.cs"
if ($LASTEXITCODE -ne 0) { throw 'Could not build the tunnel helper.' }
& $csc /nologo /target:exe "/out:$projectRoot\tests\fixtures\fake-engine.exe" "$projectRoot\tests\fixtures\FakeEngine.cs"
if ($LASTEXITCODE -ne 0) { throw 'Could not build the test fixture.' }
