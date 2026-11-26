# Arm on a disposable guest, reboot it, then Verify before starting ZapretLauncher. Clear removes the test policy.
param(
    [Parameter(Mandatory)][ValidateSet('Arm', 'Verify', 'Clear')][string]$Phase,
    [Parameter(Mandatory)][switch]$DedicatedVm,
    [string]$Exe,
    [string]$PeerAddress,
    [ValidateRange(1,65535)][int]$PeerPort
)

$ErrorActionPreference = 'Stop'
if (!$DedicatedVm -or !(New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run as administrator on a disposable Windows VM with no existing launcher rules. Disable launcher autostart for this test.'
}
if ((Get-Process ZapretLauncher -ErrorAction SilentlyContinue) -or (Get-NetAdapter -Name ZapretVPN -ErrorAction SilentlyContinue)) {
    throw 'The launcher and the VPN adapter must be stopped.'
}
if (!$Exe) { $Exe = Join-Path $PSScriptRoot '..\dist\ZapretLauncher.exe' }
$exePath = (Resolve-Path -LiteralPath $Exe).Path
$firewall = [Reflection.Assembly]::LoadFile($exePath).GetType('ZapretLauncher.VpnFirewall', $true)
$state = Join-Path (Split-Path -Parent $exePath) 'wfp-reboot-test'
$manifest = Join-Path $state 'boot.json'
$protected = Join-Path $state 'Protected.exe'
$ordinary = Join-Path $state 'Ordinary.exe'
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks

switch ($Phase) {
    'Arm' {
        if (Test-Path -LiteralPath $manifest) { throw 'The test is already armed. Verify or clear it first.' }
        $peer = $null
        if (![Net.IPAddress]::TryParse($PeerAddress, [ref]$peer) -or !$PeerPort -or [Net.IPAddress]::IsLoopback($peer) -or
            @(Get-NetIPAddress).IPAddress -contains $peer.IPAddressToString) {
            throw 'Provide a routable IP address and a TCP port on a separate test peer you control.'
        }
        New-Item -ItemType Directory -Force -Path $state | Out-Null
        & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo "/out:$protected" (Join-Path $PSScriptRoot 'VpnFirewallProbe.cs')
        if ($LASTEXITCODE) { throw 'Probe build failed.' }
        Copy-Item -LiteralPath $protected -Destination $ordinary -Force
        & $protected $peer.IPAddressToString $PeerPort
        if ($LASTEXITCODE) { throw 'The protected executable needs a working baseline before arming.' }
        try {
            $firewall.GetMethod('ApplyScoped').Invoke($null, [object[]]@($true, [uint64]0, [string[]]@($protected)))
            & $protected $peer.IPAddressToString $PeerPort
            if (!$LASTEXITCODE) { throw 'The selected executable was not blocked before reboot.' }
            & $ordinary $peer.IPAddressToString $PeerPort
            if ($LASTEXITCODE) { throw 'The unprotected executable lost the baseline before reboot.' }
            # Leave an active per-interface permit in place to prove precisely which rule survives reboot.
            $network = Find-NetRoute -RemoteIPAddress $peer.IPAddressToString | Where-Object DestinationPrefix | Select-Object -First 1
            if (!$network) { throw 'Could not determine the interface for the test peer.' }
            $convert = $firewall.GetMethod('ConvertInterfaceIndexToLuid', [Reflection.BindingFlags]'Static, NonPublic')
            $arguments = [object[]]@([uint32]$network.InterfaceIndex, [uint64]0)
            if ($convert.Invoke($null, $arguments) -ne 0 -or !$arguments[1]) { throw 'Could not resolve the test interface LUID.' }
            $firewall.GetMethod('ApplyScoped').Invoke($null, [object[]]@($true, [uint64]$arguments[1], [string[]]@($protected)))
            & $protected $peer.IPAddressToString $PeerPort
            if ($LASTEXITCODE) { throw 'The selected executable could not connect with the temporary permit.' }
            @{ BootTicks = "$boot"; Address = $peer.IPAddressToString; Port = $PeerPort } | ConvertTo-Json |
                Set-Content -LiteralPath $manifest -Encoding ASCII
        }
        catch {
            $firewall.GetMethod('Clear').Invoke($null, $null)
            throw
        }
        Write-Host 'Policy armed. Reboot this disposable guest and run Verify before opening the launcher.'
    }
    'Verify' {
        $saved = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($saved.BootTicks -eq "$boot") { throw 'Windows has not rebooted since the test was armed.' }
        try {
            & $ordinary $saved.Address $saved.Port
            if ($LASTEXITCODE) { throw 'Baseline failed after reboot. The test did not establish a usable route.' }
            & $protected $saved.Address $saved.Port
            if (!$LASTEXITCODE) { throw 'Protected executable connected before the launcher started.' }
            & $ordinary $saved.Address $saved.Port
            if ($LASTEXITCODE) { throw 'Baseline changed during the test.' }
        }
        finally {
            $firewall.GetMethod('Clear').Invoke($null, $null)
            Remove-Item -LiteralPath $manifest -Force
        }
        & $protected $saved.Address $saved.Port
        if ($LASTEXITCODE) { throw 'Protected executable still had no access after test policy removal.' }
        Write-Host 'Windows enforced the selected-app block after reboot before launcher startup.'
    }
    'Clear' {
        $firewall.GetMethod('Clear').Invoke($null, $null)
        if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
    }
}
