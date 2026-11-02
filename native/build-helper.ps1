$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw 'Windows .NET Framework C# compiler not found.' }
foreach ($target in @(
    @{ Source = "$projectRoot\native\FinleyTunnelHelper.cs"; Output = "$projectRoot\bin\finley-tunnel-helper.exe" },
    @{ Source = "$projectRoot\tests\fixtures\FakeEngine.cs"; Output = "$projectRoot\tests\fixtures\fake-engine.exe" }
)) {
    # Avoid rewriting an unchanged executable that a local test may still hold.
    if ((Test-Path -LiteralPath $target.Output) -and (Get-Item -LiteralPath $target.Output).LastWriteTimeUtc -ge (Get-Item -LiteralPath $target.Source).LastWriteTimeUtc) { continue }
    & $csc /nologo /target:exe "/out:$($target.Output)" $target.Source
    if ($LASTEXITCODE -ne 0) { throw 'Could not build a native helper/fixture. Stop processes using that binary and retry.' }
}
