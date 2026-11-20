param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\ZapretLauncher.exe'),
    [Parameter(Mandatory)][string]$VpnCore,
    [Parameter(Mandatory)][string]$PeerAddress,
    [Parameter(Mandatory)][ValidateRange(1,65535)][int]$PeerPort,
    [switch]$DedicatedVm
)
$ErrorActionPreference = 'Stop'
if (!$DedicatedVm) { throw 'Run only on an isolated Windows test VM without launcher firewall rules.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (!(New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this test as administrator.'
}
$peer = [Net.IPAddress]::Parse($PeerAddress)
if ($peer.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($peer) -or
    @(Get-NetIPAddress -AddressFamily IPv4).IPAddress -contains $peer.IPAddressToString) {
    throw 'Peer must be a TCP listener on another machine controlled by the test operator.'
}
if ((Get-Process ZapretLauncher -ErrorAction SilentlyContinue) -or (Get-NetAdapter -Name ZapretVPN -ErrorAction SilentlyContinue)) {
    throw 'A launcher process or tunnel already exists.'
}

$core = (Resolve-Path -LiteralPath $VpnCore).Path
$plugin = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Exe).Path).GetType('ZapretLauncher.VpnFirewall', $true)
$scope = $plugin.GetMethod('ApplyScoped')
$clear = $plugin.GetMethod('Clear')
$luid = $plugin.GetMethod('TunnelLuid')
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$work = [IO.Path]::GetFullPath((Join-Path $tempRoot ('zapret-wfp-test-' + [guid]::NewGuid().ToString('N'))))
if (![IO.Path]::GetDirectoryName($work).Equals($tempRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Invalid test directory.'
}
New-Item -ItemType Directory -Path $work | Out-Null
$protected = Join-Path $work 'Protected.exe'
$ordinary = Join-Path $work 'Ordinary.exe'
$config = Join-Path $work 'tun.json'
$tunnel = $null
$applied = $false

function Test-Paths([string]$phase, [bool]$protectedOnline) {
    & $protected $peer.IPAddressToString $PeerPort
    $allowed = $LASTEXITCODE -eq 0
    if ($allowed -ne $protectedOnline) { throw "$phase - protected process connectivity: $allowed" }
    & $ordinary $peer.IPAddressToString $PeerPort
    if ($LASTEXITCODE -ne 0) { throw "$phase - ordinary process lost access" }
    "$phase - protected=$allowed ordinary=True"
}

function Start-Tunnel {
    $script:tunnel = Start-Process -FilePath $core -ArgumentList @('run','-c',('"' + $config + '"')) -WorkingDirectory $work -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ($script:tunnel.HasExited) { throw 'Test tunnel exited early.' }
        $id = $luid.Invoke($null, $null)
        if ($id -ne 0) { return $id }
    }
    throw 'Test tunnel adapter did not start.'
}

try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo "/out:$protected" (Join-Path $PSScriptRoot 'VpnFirewallProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    Copy-Item -LiteralPath $protected -Destination $ordinary
    @"
{"inbounds":[{"type":"tun","interface_name":"ZapretVPN","address":["172.31.255.1/30"],"auto_route":true,"route_address":["$($peer.IPAddressToString)/32"],"stack":"system"}],"outbounds":[{"type":"direct","tag":"direct"}],"route":{"auto_detect_interface":true,"final":"direct"}}
"@ | Set-Content -LiteralPath $config -Encoding ASCII
    Test-Paths 'Before policy' $true
    $id = Start-Tunnel
    $applied = $true
    $scope.Invoke($null, [object[]]@($true, $id, [string[]]@($protected)))
    Test-Paths 'Tunnel up' $true
    Stop-Process -Id $tunnel.Id
    $tunnel.WaitForExit(5000) | Out-Null
    for ($i = 0; $i -lt 40 -and $luid.Invoke($null, $null) -ne 0; $i++) { Start-Sleep -Milliseconds 250 }
    if ($luid.Invoke($null, $null) -ne 0) { throw 'Test tunnel adapter did not stop.' }
    Test-Paths 'Tunnel dropped with original policy still active' $false
    $scope.Invoke($null, [object[]]@($true, [uint64]0, [string[]]@($protected)))
    Test-Paths 'Closed protection during restart' $false
    $id = Start-Tunnel
    $scope.Invoke($null, [object[]]@($true, $id, [string[]]@($protected)))
    Test-Paths 'Tunnel restored' $true
}
finally {
    try { if ($applied) { $clear.Invoke($null, $null) } }
    finally {
        if ($tunnel -and !$tunnel.HasExited) { Stop-Process -Id $tunnel.Id; $tunnel.WaitForExit(5000) | Out-Null }
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
