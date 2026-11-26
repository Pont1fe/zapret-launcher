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
        if (& $condition) { return }
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for $what. Status: $($window.FindName('VpnStatus').Text)"
}
function Wait-Task($task) {
    Wait-For 'launcher VPN task' { $task.IsCompleted }
    $task.GetAwaiter().GetResult()
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

$server = $null
$window = $null
try {
    Write-Host 'Preparing loopback proxy'
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
    $server = Start-Process -FilePath $VpnCore -ArgumentList @('run','-c',('"' + $serverConfig + '"')) -WorkingDirectory $work -WindowStyle Hidden -PassThru
    Wait-For 'loopback Shadowsocks test server' {
        if ($server.HasExited) { throw 'The test server exited.' }
        Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $listenPort -State Listen -ErrorAction SilentlyContinue
    }

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
    Wait-For 'launcher window' { $window.IsVisible -and $window.ActualWidth -gt 0 }
    $window.FindName('NavVpn').IsChecked = $true
    $window.FindName('VpnTabRoutes').IsChecked = $true
    Pump

    Write-Host 'Turning on selected-app protection'
    $window.FindName('SwVpnKill').IsChecked = $true
    Click 'SwVpnKill'
    Wait-For 'enabled kill switch' { $cfg.VpnKillSwitch -and !(Field 'busy').GetValue($launcher) }
    Check-Tcp $true
    Write-Host 'Connecting the launcher to the loopback proxy'
    Click 'BtnVpnToggle'
    Wait-For 'connected VPN' { (Field 'vpnRunning').GetValue($launcher) -and !(Field 'busy').GetValue($launcher) }
    Check-Tcp $true
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
    Check-Tcp $false
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
    Check-Tcp $true
    Write-Host 'The launcher guard restarted the real core, updated the UI and restored protected TCP.'
} catch {
    foreach ($log in (Join-Path $work 'launcher.log'), (Join-Path $vpnDir 'sing-box.log'), $serverLog) {
        if ($log -and (Test-Path -LiteralPath $log)) { Get-Content -LiteralPath $log -Tail 20 | Out-Host }
    }
    throw
} finally {
    try {
        Write-Host 'Stopping launcher test VPN'
    if ($launcher) { Wait-Task (Call 'StopVpn') }
    } finally {
        foreach ($process in @(Core-Processes)) { if (!$process.HasExited) { Stop-Process -Id $process.Id } }
        if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id }
    }
}
