# Run explicitly from an administrator terminal after npm run build:native.
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Open an administrator terminal to install the development service.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$targetRoot = Join-Path $env:ProgramFiles 'Finley Development'
$targetBin = Join-Path $targetRoot 'resources\bin'
$serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\FinleyTunnelService'
if (Test-Path -LiteralPath $serviceKey) {
    $registered = (Get-ItemProperty -LiteralPath $serviceKey).ImagePath
    if ($registered -ne ('"' + (Join-Path $targetBin 'finley-tunnel-service.exe') + '"')) { throw 'The installed Finley service uses another location. Uninstall it before installing the development service.' }
    & (Join-Path $targetBin 'finley-tunnel-service.exe') --stop
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the development service safely.' }
}
# A single SCM identity and pipe prevent production and development tunnels
# coexisting. The service never executes any file from this checkout.
foreach ($directory in @($targetRoot, (Join-Path $targetRoot 'resources'), $targetBin)) {
    if (Test-Path -LiteralPath $directory) {
        if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a reparse point in the development installation.' }
    } else { New-Item -ItemType Directory -Path $directory | Out-Null }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $directory -AclObject $acl
}
foreach ($file in @('finley-tunnel-service.exe', 'finley-service-client.exe', 'finley-tunnel-helper.exe', 'sing-box.exe')) {
    $destination = Join-Path $targetBin $file
    if (Test-Path -LiteralPath $destination) {
        if ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a reparse point in the development installation.' }
    }
    Copy-Item -LiteralPath (Join-Path (Join-Path $projectRoot 'bin') $file) -Destination $destination -Force
}
& (Join-Path $targetBin 'finley-tunnel-service.exe') --install
if ($LASTEXITCODE -ne 0) { throw 'Development service installation failed.' }
Write-Output 'Development service installed. Run npm run dev from your normal user terminal.'
