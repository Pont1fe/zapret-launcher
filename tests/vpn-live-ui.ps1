param(
    [string]$Exe,
    [Parameter(Mandatory)][string]$VpnCore
)
$ErrorActionPreference = 'Stop'
if (!$Exe) { $Exe = Join-Path $PSScriptRoot '..\dist\ZapretLauncher.exe' }
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    !(New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run in Windows PowerShell -STA on a disposable GitHub-hosted runner as administrator.'
}
if (Get-NetAdapter -Name ZapretVPN -ErrorAction SilentlyContinue) { throw 'A VPN adapter already exists.' }

$env:ZAPRETLAUNCHER_DEMO = '1' # Skip unrelated startup services while exercising the actual VPN controls and core.
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms
$work = Join-Path $env:RUNNER_TEMP ('vpn-ui-' + [guid]::NewGuid().ToString('N'))
$vpnDir = Join-Path $work 'vpn'
New-Item -ItemType Directory -Path $vpnDir -Force | Out-Null
Copy-Item -LiteralPath $Exe -Destination (Join-Path $work 'ZapretLauncher.exe')
Copy-Item -LiteralPath $VpnCore -Destination (Join-Path $vpnDir 'sing-box.exe')
Set-Content -LiteralPath (Join-Path $vpnDir 'version.txt') -Value '1.14.4' -Encoding ASCII
$protected = Join-Path $work 'Protected.exe'
$ordinary = Join-Path $work 'Ordinary.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo "/out:$protected" (Join-Path $PSScriptRoot 'VpnFirewallProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
Copy-Item -LiteralPath $protected -Destination $ordinary

$asm = [Reflection.Assembly]::LoadFile((Join-Path $work 'ZapretLauncher.exe'))
function T([string]$name) { $asm.GetType("ZapretLauncher.$name", $true) }
$sf = [Reflection.BindingFlags]'Static, Public, NonPublic'
$if = [Reflection.BindingFlags]'Instance, Public, NonPublic'
function Call([string]$method) { $lt.GetMethod($method, $if).Invoke($launcher, [object[]]$args) }
function Field([string]$name) { $lt.GetField($name, $if) }
function Pump {
    $frame = New-Object Windows.Threading.DispatcherFrame
    $timer = New-Object Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds(50)
    $timer.Add_Tick({ $timer.Stop(); $frame.Continue = $false }.GetNewClosure())
    $timer.Start()
    [Windows.Threading.Dispatcher]::PushFrame($frame)
}
function Wait-For([string]$what, [scriptblock]$condition) {
    $until = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Pump
        if ($script:unexpectedDialog) { throw "The launcher opened an unexpected dialog while waiting for $what." }
        if (& $condition) { return }
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for $what. Status: $($window.FindName('VpnStatus').Text)"
}
function Wait-Task($task) {
    Wait-For 'launcher VPN task' { $task.IsCompleted }
    [void]$task.GetAwaiter().GetResult()
}
function Click([string]$name) {
    $button = $window.FindName($name)
    if (!$button.IsEnabled) { throw "$name is disabled." }
    $button.RaiseEvent((New-Object Windows.RoutedEventArgs ([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $button)))
}
function Core-Processes { (T 'Vpn').GetMethod('Processes', $sf).Invoke($null, $null) }
function Check-Tcp([bool]$expected) {
    # Minimal TCP handshake against the documented Cloudflare HTTPS address.
    & $ordinary '1.0.0.1' 443
    if ($LASTEXITCODE -ne 0) { throw 'The ordinary application lost connectivity.' }
    & $protected '1.0.0.1' 443
    if (($LASTEXITCODE -eq 0) -ne $expected) { throw "Protected TCP connectivity should be $expected." }
}

function Proxy-Connections {
    @(Select-String -LiteralPath $serverLog -Pattern 'inbound/shadowsocks.*inbound connection to 1\.0\.0\.1:443$').Count
}
function Check-ProxyRoute([bool]$throughVpn, [bool]$online) {
    $before = Proxy-Connections
    Check-Tcp $online
    $after = Proxy-Connections
    $expected = if ($throughVpn) { 1 } else { 0 }
    if (($after - $before) -ne $expected) {
        throw "Expected $expected connection from the probes in the test VPN server log, saw $($after - $before)."
    }
}

function Start-Proxy {
    $process = Start-Process -FilePath $VpnCore -ArgumentList @('run','-c',('"' + $serverConfig + '"')) -WorkingDirectory $work -WindowStyle Hidden -PassThru
    Wait-For 'loopback Shadowsocks test server' {
        if ($process.HasExited) { throw 'The test server exited.' }
        Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $listenPort -State Listen -ErrorAction SilentlyContinue
    }
    return $process
}
function Run-VpnTick {
    (Field 'netChanged').SetValue($launcher, $false)
    (Field 'tick').SetValue($launcher, 4)
    Wait-Task (Call 'OnTick')
}
function Start-LauncherProcess {
    Remove-Item Env:ZAPRETLAUNCHER_DEMO
    try { Start-Process -FilePath (Join-Path $work 'ZapretLauncher.exe') -WorkingDirectory $work -PassThru }
    finally { $env:ZAPRETLAUNCHER_DEMO = '1' }
}
function Wait-LauncherVpn([int]$oldCore) {
    Wait-For 'real launcher to start a new VPN core' {
        if ($actualLauncher.HasExited) { throw "Launcher exited with code $($actualLauncher.ExitCode)." }
        $candidate = @(Core-Processes)
        $candidate.Count -eq 1 -and $candidate[0].Id -ne $oldCore -and
            (Get-NetAdapter -Name ZapretVPN -ErrorAction SilentlyContinue)
    }
    Wait-For 'real launcher to allow protected TCP through the VPN' {
        & $protected '1.0.0.1' 443
        $LASTEXITCODE -eq 0
    }
}

$server = $null
$actualLauncher = $null
$window = $null
try {
    Write-Host 'Preparing loopback proxy'
    Get-NetAdapter | Format-Table Name, InterfaceDescription, ifIndex | Out-Host
    $password = [guid]::NewGuid().ToString('N')
    $serverConfig = Join-Path $work 'server.json'
    $serverLog = Join-Path $work 'server.log'
    $listenPort = 31814
    $config = @{
        log = @{ level = 'info'; output = $serverLog }
        inbounds = @(@{ type = 'shadowsocks'; listen = '127.0.0.1'; listen_port = $listenPort; method = 'aes-128-gcm'; password = $password })
        outbounds = @(@{ type = 'direct'; tag = 'direct' })
    } | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($serverConfig, $config)
    $server = Start-Proxy

    Write-Host 'Creating WPF application'
    $app = New-Object Windows.Application
    $app.ShutdownMode = 'OnExplicitShutdown'
    $app.Resources.MergedDictionaries.Add([Windows.Markup.XamlReader]::Load($asm.GetManifestResourceStream('styles.xaml')))
    $cfg = (T 'Config').GetMethod('Load', $sf).Invoke($null, $null)
    $cfg.Language = 'ru'
    $cfg.Theme = 'dark'
    $cfg.OnboardingDone = $true
    $cfg.VpnHelpDone = $true
    $cfg.VpnProtectedApps.Add($protected)
    $profile = [Activator]::CreateInstance((T 'VpnServer'))
    $profile.Name = 'Local VPN test'
    $userinfo = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("aes-128-gcm:$password"))
    $profile.Link = "ss://$userinfo@127.0.0.1:$listenPort"
    $cfg.VpnServers.Add($profile)
    (T 'I18n').GetMethod('Init', $sf).Invoke($null, @('ru'))
    (T 'Theme').GetMethod('Apply', $sf).Invoke($null, @('dark', $false, 'violet', $false))
    $lt = T 'Launcher'
    Write-Host 'Opening launcher window'
    $launcher = [Activator]::CreateInstance($lt, $if, $null, @($false, $cfg), $null)
    Write-Host 'Launcher window constructed'
    $window = (Field 'window').GetValue($launcher)
    [Threading.SynchronizationContext]::SetSynchronizationContext((New-Object Windows.Threading.DispatcherSynchronizationContext $window.Dispatcher))
    $script:unexpectedDialog = $false
    $dialogWatch = New-Object Windows.Threading.DispatcherTimer
    $dialogWatch.Interval = [TimeSpan]::FromMilliseconds(150)
    $dialogWatch.Add_Tick({
        foreach ($item in @($app.Windows)) {
            if ($item.Owner -ne $window -or !$item.IsVisible) { continue }
            $script:unexpectedDialog = $true
            Write-Host "Unexpected launcher dialog: $($item.Title)"
            $item.Close()
        }
    })
    $dialogWatch.Start()
    Wait-For 'launcher window' { $window.IsVisible -and $window.ActualWidth -gt 0 }
    $window.FindName('NavVpn').IsChecked = $true
    $window.FindName('VpnTabRoutes').IsChecked = $true
    Pump

    Write-Host 'Turning on selected-app protection'
    $window.FindName('SwVpnKill').IsChecked = $true
    Click 'SwVpnKill'
    Wait-For 'enabled kill switch' { $cfg.VpnKillSwitch -and !(Field 'busy').GetValue($launcher) }
    Check-ProxyRoute $false $true
    Write-Host 'Connecting the launcher to the loopback proxy'
    Click 'BtnVpnToggle'
    Wait-For 'connected VPN' { (Field 'vpnRunning').GetValue($launcher) -and !(Field 'busy').GetValue($launcher) }
    Check-ProxyRoute $true $true
    Write-Host 'Launcher toggle enabled protection and the real core connected.'

    $old = @(Core-Processes)
    if ($old.Count -ne 1) { throw "Expected exactly one launcher VPN process, found $($old.Count)." }
    Stop-Process -Id $old[0].Id
    $old[0].WaitForExit(5000) | Out-Null
    Wait-For 'crashed adapter to disappear' { !(Get-NetAdapter -Name ZapretVPN -ErrorAction SilentlyContinue) }

    # Run the same tick that watches the VPN in the normal launcher.
    (Field 'vpnLastStart').SetValue($launcher, [DateTime]::Now)
    (Field 'netChanged').SetValue($launcher, $false)
    (Field 'tick').SetValue($launcher, 4)
    Wait-Task (Call 'OnTick')
    $status = $window.FindName('VpnStatus').Text
    $paused = 'Трафик приостановлен'
    if ($status -cne $paused -or !$window.FindName('SwVpnKill').IsChecked) { throw "Wrong UI state after the crash: $status" }
    Check-ProxyRoute $false $false
    Write-Host 'Launcher displayed paused traffic. The ordinary application still connected.'

    # Advance the retry timestamp; the production guard still decides and performs the restart.
    (Field 'vpnLastStart').SetValue($launcher, [DateTime]::Now.AddMinutes(-3))
    (Field 'netChanged').SetValue($launcher, $false)
    (Field 'tick').SetValue($launcher, 4)
    Wait-Task (Call 'OnTick')
    Wait-For 'recovered VPN' { (Field 'vpnRunning').GetValue($launcher) -and !(Field 'busy').GetValue($launcher) }
    $restarted = @(Core-Processes)
    if ($restarted.Count -ne 1 -or $restarted[0].Id -eq $old[0].Id -or $window.FindName('VpnStatus').Text -ceq $paused) {
        throw 'The launcher did not recover with a new VPN process and updated status.'
    }
    Check-ProxyRoute $true $true
    Write-Host 'The launcher guard restarted the real core, updated the UI and restored protected TCP.'

    Stop-Process -Id $server.Id
    if (!$server.WaitForExit(5000)) { throw 'The proxy did not stop.' }
    if ($restarted[0].HasExited) { throw 'The VPN core must still be running when the server stops responding.' }
    Wait-For 'initial health probe to finish' { !(Field 'vpnHealthChecking').GetValue($launcher) }
    (Field 'vpnHealthDue').SetValue($launcher, [DateTime]::MinValue)
    Run-VpnTick
    if ((Field 'vpnDeadChecks').GetValue($launcher) -ne 1 -or $restarted[0].HasExited) {
        throw 'One failed round must leave the current core running.'
    }

    (Field 'vpnHealthDue').SetValue($launcher, [DateTime]::MinValue)
    Run-VpnTick
    if (!(Field 'vpnHealthRecovery').GetValue($launcher) -or @(Core-Processes).Count -ne 0 -or
        $window.FindName('VpnStatus').Text -cne $paused -or
        $window.FindName('VpnHealthText').Text -notlike '*VPN не передаёт трафик*') {
        throw 'An unresponsive VPN must stay paused after a failed reconnect.'
    }
    Check-ProxyRoute $false $false
    Write-Host 'Stalled VPN was detected with the core alive. Protected TCP stayed blocked while the proxy was offline.'

    $server = Start-Proxy
    (Field 'vpnLastStart').SetValue($launcher, [DateTime]::Now.AddMinutes(-3))
    Run-VpnTick
    if ((Field 'vpnHealthRecovery').GetValue($launcher) -or !(Field 'vpnRunning').GetValue($launcher)) {
        throw 'Expected VPN recovery once the proxy responded.'
    }
    Check-ProxyRoute $true $true
    Write-Host 'The launcher verified the replacement tunnel before restoring protected TCP.'

    $cfg.StartOnOpen = $false
    $cfg.Telegram = $false
    $cfg.LastSeenVersion = (T 'AppVersion').GetField('Value', $sf).GetValue($null)
    $cfg.Save()
    $zapret = Join-Path $work 'zapret'
    New-Item -ItemType Directory -Force -Path $zapret | Out-Null
    Set-Content -LiteralPath (Join-Path $zapret 'service.bat') -Value 'set "LOCAL_VERSION=1.0.0"' -Encoding ASCII

    $previousVpn = @(Core-Processes)[0].Id
    $actualLauncher = Start-LauncherProcess
    Wait-LauncherVpn $previousVpn
    Check-ProxyRoute $true $true
    $liveCore = @(Core-Processes)[0]
    Stop-Process -Id $actualLauncher.Id
    if (!$actualLauncher.WaitForExit(5000)) { throw 'The launcher process did not exit.' }
    Check-ProxyRoute $true $true

    Stop-Process -Id $liveCore.Id
    if (!$liveCore.WaitForExit(5000)) { throw 'The VPN core did not exit.' }
    Wait-For 'adapter to disappear with both processes stopped' { !(Get-NetAdapter -Name ZapretVPN -ErrorAction SilentlyContinue) }
    Check-ProxyRoute $false $false
    Write-Host 'Persistent WFP blocked the protected app with both the launcher and the VPN core terminated.'

    $actualLauncher = Start-LauncherProcess
    Wait-LauncherVpn $liveCore.Id
    Check-ProxyRoute $true $true
    Write-Host 'Restarting the actual launcher restored VPN traffic from the saved configuration.'
} catch {
    foreach ($log in (Join-Path $work 'launcher.log'), (Join-Path $vpnDir 'sing-box.log'), $serverLog) {
        if ($log -and (Test-Path -LiteralPath $log)) { Get-Content -LiteralPath $log -Tail 20 | Out-Host }
    }
    throw
} finally {
    if ($actualLauncher -and !$actualLauncher.HasExited) { Stop-Process -Id $actualLauncher.Id; [void]$actualLauncher.WaitForExit(5000) }
    try {
    if ($dialogWatch) { $dialogWatch.Stop() }
    $script:unexpectedDialog = $false
    Write-Host 'Stopping launcher test VPN'
    if ($launcher) { Wait-Task (Call 'StopVpn') }
    } finally {
        foreach ($process in @(Core-Processes)) { if (!$process.HasExited) { Stop-Process -Id $process.Id } }
        if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id }
    }
}
