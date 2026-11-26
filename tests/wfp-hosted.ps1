param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\ZapretLauncher.exe'),
    [Parameter(Mandatory)][string]$VpnCore
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    !(New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This test requires a disposable GitHub-hosted Windows runner with administrator access.'
}
if (Get-NetAdapter -Name ZapretVPN,ZapretTestDirect,ZapretTestReplacement -ErrorAction SilentlyContinue) {
    throw 'A test adapter already exists.'
}

$work = Join-Path $env:RUNNER_TEMP ('wfp-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$core = (Resolve-Path -LiteralPath $VpnCore).Path
$protected = Join-Path $work 'Protected.exe'
$ordinary = Join-Path $work 'Ordinary.exe'
$directPort = 31811
$tunnelPort = 31812
$peerPort = 31813
$directLog = Join-Path $work 'direct.log'
$tunnelLog = Join-Path $work 'tunnel.log'
$firewall = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Exe).Path).GetType('ZapretLauncher.VpnFirewall', $true)
$apply = $firewall.GetMethod('ApplyScoped')
$luid = $firewall.GetMethod('TunnelLuid')
$policyApplied = $false

function Wait-For([string]$what, [scriptblock]$condition) {
    for ($i = 0; $i -lt 100; $i++) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for $what."
}

function Start-TestProcess([string]$file, [string[]]$arguments) {
    $argsQuoted = $arguments | ForEach-Object { '"' + $_ + '"' }
    $p = Start-Process -FilePath $file -ArgumentList $argsQuoted -WorkingDirectory $work -WindowStyle Hidden -PassThru
    $processes.Add($p)
    return $p
}

function Stop-TestProcess($p) {
    if (!$p.HasExited) { Stop-Process -Id $p.Id }
    if (!$p.WaitForExit(5000)) { throw "Test process did not exit: $($p.Id)." }
}

function Start-Tunnel([string]$alias, [string[]]$addresses, [string[]]$routes, [int]$port) {
    $config = Join-Path $work "$alias.json"
    $log = Join-Path $work "$alias.log"
    @{
        log = @{ level = 'info'; output = $log }
        inbounds = @(@{ type = 'tun'; interface_name = $alias; address = $addresses; auto_route = $true; route_address = $routes; stack = 'system' })
        outbounds = @(@{ type = 'direct'; tag = 'direct' })
        route = @{ rules = @(@{ action = 'route'; outbound = 'direct'; override_address = '127.0.0.1'; override_port = $port }) }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $config -Encoding utf8NoBOM
    & $core check -c $config
    if ($LASTEXITCODE -ne 0) { throw "Invalid test tunnel config for $alias." }
    $p = Start-TestProcess $core @('run','-c',$config)
    foreach ($route in $routes) {
        Wait-For "$alias route $route" {
            if ($p.HasExited) { throw "$alias exited: $(Get-Content -LiteralPath $log -Tail 10 -ErrorAction SilentlyContinue)" }
            Get-NetRoute -InterfaceAlias $alias -DestinationPrefix $route -ErrorAction SilentlyContinue
        }
    }
    return $p
}

function Check-Traffic([string]$file, [string]$address, [string]$protocol, [string]$expectedLog) {
    $token = [guid]::NewGuid().ToString('N')
    & $file $address $peerPort $protocol $token
    $ok = $LASTEXITCODE -eq 0
    if ($ok -ne [bool]$expectedLog) { throw "$address $protocol $([IO.Path]::GetFileName($file)) expected connectivity $([bool]$expectedLog), got $ok." }
    foreach ($log in $directLog, $tunnelLog) {
        $seen = (Get-Content -LiteralPath $log -ErrorAction SilentlyContinue) -contains $token
        if ($seen -ne ($expectedLog -eq $log)) { throw "$address $protocol unexpected receipt in $log ($seen)." }
    }
}

try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo "/out:$protected" (Join-Path $PSScriptRoot 'VpnFirewallProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    Copy-Item -LiteralPath $protected -Destination $ordinary
    foreach ($peer in @(@($directPort, $directLog), @($tunnelPort, $tunnelLog))) {
        $ready = "$($peer[1]).ready"
        $listener = Start-TestProcess $ordinary @('listen', $peer[0], $peer[1], $ready)
        Wait-For "peer $($peer[0])" { (Test-Path $ready) -and !$listener.HasExited }
    }

    $targets = @('198.51.100.42', '2001:db8::42')
    $hostRoutes = @('198.51.100.42/32', '2001:db8::42/128')
    $firstAddresses = @('172.31.255.1/30', 'fd42:7a71:4::1/126')
    $direct = Start-Tunnel 'ZapretTestDirect' @('172.31.255.5/30', 'fd42:7a71:5::1/126') @('198.51.100.0/24', '2001:db8::/64') $directPort
    foreach ($address in $targets) {
        foreach ($protocol in 'tcp', 'udp') { Check-Traffic $protected $address $protocol $directLog }
        Write-Host "$address TCP and UDP baseline passed."
    }

    $tunnel = Start-Tunnel 'ZapretVPN' $firstAddresses $hostRoutes $tunnelPort
    Wait-For 'first VPN LUID' { $luid.Invoke($null, $null) -ne 0 }
    $firstId = $luid.Invoke($null, $null)
    $policyApplied = $true
    $apply.Invoke($null, [object[]]@($true, $firstId, [string[]]@($protected)))
    $sessions = foreach ($address in $targets) {
        foreach ($protocol in 'tcp', 'udp') { Check-Traffic $protected $address $protocol $tunnelLog }
        $token = [guid]::NewGuid().ToString('N')
        $ready = Join-Path $work "$token.ready"
        $signal = Join-Path $work "$token.send"
        $held = Start-TestProcess $protected @($address, $peerPort, 'hold', $token, $ready, $signal, "$token-after")
        Wait-For "$address established TCP" { (Test-Path $ready) -and !$held.HasExited }
        if ((Get-Content $tunnelLog) -notcontains $token) { throw "$address held TCP did not use the tunnel." }
        @{ address = $address; process = $held; signal = $signal; token = "$token-after" }
    }

    Stop-TestProcess $tunnel
    Wait-For 'crashed VPN to disappear' { $luid.Invoke($null, $null) -eq 0 }
    foreach ($session in $sessions) { New-Item -ItemType File -Path $session.signal | Out-Null }
    foreach ($address in $targets) {
        foreach ($protocol in 'tcp', 'udp') {
            Check-Traffic $protected $address $protocol ''
            Check-Traffic $ordinary $address $protocol $directLog
        }
        Write-Host "$address TCP and UDP scoped protection passed after crash."
    }
    foreach ($session in $sessions) {
        if (!$session.process.WaitForExit(5000) -or $session.process.ExitCode -ne 1 -or
            ((Get-Content $directLog,$tunnelLog) -contains $session.token)) {
            throw "$($session.address) already-open TCP sent data after the crash or the probe did not complete."
        }
        Write-Host "$($session.address) held TCP stopped after crash."
    }

    $replacement = Start-Tunnel 'ZapretTestReplacement' @('172.31.255.9/30', 'fd42:7a71:6::1/126') $hostRoutes $tunnelPort
    # Windows can retain the crashed device name even after the adapter disappears.
    $replacementAdapter = Get-NetAdapter -Name ZapretTestReplacement
    Add-Type -Namespace WfpTest -Name Native -MemberDefinition '[DllImport("iphlpapi.dll")] public static extern uint ConvertInterfaceIndexToLuid(uint index, out ulong luid);'
    [uint64]$secondId = 0
    $errorCode = [WfpTest.Native]::ConvertInterfaceIndexToLuid($replacementAdapter.ifIndex, [ref]$secondId)
    if ($errorCode -ne 0 -or $secondId -eq 0) { throw "Replacement LUID lookup failed: $errorCode." }
    if ($secondId -eq $firstId) { throw 'The replacement reused the original LUID.' }
    Write-Host "VPN adapter LUID changed from $firstId to $secondId."
    foreach ($address in $targets) {
        Check-Traffic $ordinary $address 'tcp' $tunnelLog
        Check-Traffic $protected $address 'tcp' ''
    }
    $apply.Invoke($null, [object[]]@($true, [uint64]0, [string[]]@($protected)))
    foreach ($address in $targets) { Check-Traffic $protected $address 'udp' '' }
    $apply.Invoke($null, [object[]]@($true, $secondId, [string[]]@($protected)))
    foreach ($address in $targets) {
        foreach ($protocol in 'tcp', 'udp') { Check-Traffic $protected $address $protocol $tunnelLog }
        Write-Host "$address TCP and UDP recovered on the replacement adapter."
    }
} catch {
    foreach ($name in 'ZapretVPN', 'ZapretTestDirect', 'ZapretTestReplacement') {
        $log = Join-Path $work "$name.log"
        if (Test-Path $log) { Get-Content -LiteralPath $log -Tail 20 | Out-Host }
    }
    throw
} finally {
    try { if ($policyApplied) { $firewall.GetMethod('Clear').Invoke($null, $null) } }
    finally { foreach ($p in $processes) { Stop-TestProcess $p } }
}
