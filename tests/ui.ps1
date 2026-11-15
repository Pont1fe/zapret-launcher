param([string]$Exe = (Join-Path $PSScriptRoot '..\dist\ZapretLauncher.exe'))
# Живая проверка окна: копия лаунчера в демо-режиме (ZAPRETLAUNCHER_DEMO=1) открывает настоящее окно, а скрипт
# нажимает кнопки, переходит по страницам, спрашивает у Windows, за какие места окно перетаскивается, и меняет масштаб.
# Демо-режим не запускает обход, VPN, обновления и таймеры; всё, что лаунчер пишет, остаётся во временной папке.
# Запуск: powershell -STA -ExecutionPolicy Bypass -File tests\ui.ps1   (сначала build.ps1). Без рабочего стола — пропускается
$ErrorActionPreference = 'Stop'
$env:ZAPRETLAUNCHER_DEMO = '1'   # до первого обращения к классу Launcher
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { 'FAILED: запускайте с ключом -STA (окнам WPF нужен STA-поток)'; exit 1 }

# своя папка: exe, пустой config и несколько файлов стратегий — установленный zapret не нужен и не трогается
$work = Join-Path ([IO.Path]::GetTempPath()) 'zl-ui-tests'
if (Test-Path $work) { Remove-Item (Join-Path $work '*') -Recurse -Force -ErrorAction SilentlyContinue } else { New-Item -ItemType Directory $work | Out-Null }
Copy-Item $Exe (Join-Path $work 'ZapretLauncher.exe') -Force
New-Item -ItemType Directory (Join-Path $work 'zapret') -Force | Out-Null
foreach ($s in 'general.bat', 'general (ALT).bat', 'general (ALT2).bat', 'general (ALT7).bat') { Set-Content (Join-Path $work "zapret\$s") '@echo off' }

$asm = [Reflection.Assembly]::LoadFile((Join-Path $work 'ZapretLauncher.exe'))
function T([string]$n) { $asm.GetType("ZapretLauncher.$n", $true) }
$SF = [Reflection.BindingFlags]'Static, Public, NonPublic'
$IF = [Reflection.BindingFlags]'Instance, Public, NonPublic'
$script:fails = 0; $script:passed = 0
function Check([string]$name, $got, $want) { if ("$got" -ceq "$want") { $script:passed++ } else { $script:fails++; "  FAIL $name"; "       got:  $got"; "       want: $want" } }

$app = New-Object Windows.Application
$app.ShutdownMode = 'OnExplicitShutdown'
$app.Resources.MergedDictionaries.Add([Windows.Markup.XamlReader]::Load($asm.GetManifestResourceStream('styles.xaml')))
$cfg = (T 'Config').GetMethod('Load', $SF).Invoke($null, $null)
$cfg.Language = 'ru'; $cfg.Theme = 'light'; $cfg.Transparent = $false; $cfg.Telegram = $true; $cfg.OnboardingDone = $true; $cfg.Scale = 100
$cfg.Strategy = 'general (ALT2).bat'; $cfg.WindowWidth = 1240; $cfg.WindowHeight = 760; $cfg.WindowMaximized = $false; $cfg.Watchdog = $true
$cfg.PickScores['general (ALT2).bat'] = '3/3/628'; $cfg.PickScores['general (ALT7).bat'] = '3/3/702'
$VS = T 'VpnServer'
foreach ($n in 'Amsterdam #1', 'Amsterdam #2') { $s = [Activator]::CreateInstance($VS); $s.Name = $n; $s.Link = "vless://x@$($n.Length).example.com:443"; $cfg.VpnServers.Add($s) }
$cfg.VpnSelected = 1; $cfg.VpnHelpDone = $true; $cfg.LastSeenVersion = (T 'AppVersion').GetField('Value').GetValue($null)
(T 'I18n').GetMethod('Init', $SF).Invoke($null, @('ru'))
(T 'Theme').GetMethod('Apply', $SF).Invoke($null, @('light', $false, 'violet', $false))

$lt = T 'Launcher'
$l = [Activator]::CreateInstance($lt, $IF, $null, @($false, $cfg), $null)
$w = $lt.GetField('window', $IF).GetValue($l)
function Call([string]$m) { $lt.GetMethod($m, $IF).Invoke($l, [object[]]$args) }
function Pump([int]$ms) { $f = New-Object Windows.Threading.DispatcherFrame; $t = New-Object Windows.Threading.DispatcherTimer; $t.Interval = [TimeSpan]::FromMilliseconds($ms); $t.Add_Tick({ $t.Stop(); $f.Continue = $false }.GetNewClosure()); $t.Start(); [Windows.Threading.Dispatcher]::PushFrame($f) }
function Page { foreach ($p in 'Home', 'Strategies', 'Vpn', 'Domains', 'Settings', 'Log') { if ($w.FindName("Page$p").Visibility -eq 'Visible') { return $p } } }
function Nav([string]$name) { $w.FindName("Nav$name").IsChecked = $true; Pump 300 }
function Click([string]$el) { $b = $w.FindName($el); $b.RaiseEvent((New-Object Windows.RoutedEventArgs ([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $b))); Pump 300 }
function Up([string]$el) { $e = $w.FindName($el); $a = New-Object Windows.Input.MouseButtonEventArgs ([Windows.Input.Mouse]::PrimaryDevice, 0, [Windows.Input.MouseButton]::Left); $a.RoutedEvent = [Windows.UIElement]::MouseLeftButtonUpEvent; $e.RaiseEvent($a); Pump 300 }
function State([string]$card, [bool]$on) {
    $lt.GetField('cardState', $IF).SetValue($l, $null); $lt.GetField('visualActive', $IF).SetValue($l, $null)
    Call 'UpdateCardState' $card | Out-Null; Call 'SetActive' $on | Out-Null; Call 'UpdateGreeting' | Out-Null; Pump 200
    $w.FindName('GreetTitle').Text -replace "`n", ' | '
}

Pump 2000
$hwnd = (New-Object Windows.Interop.WindowInteropHelper $w).Handle
if (-not $w.IsVisible -or $w.ActualWidth -le 0 -or $hwnd -eq [IntPtr]::Zero) { 'SKIP: окно не открылось — нет рабочего стола'; exit 0 }
$w.Left = 40; $w.Top = 20
# демо-данные: обход работает, три сайта открываются, VPN подключён
$lt.GetField('exiting', $IF).SetValue($l, $true)   # таймеры и обновления интерфейса не перезаписывают демо-данные
$res = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType((T 'SiteResult')))
foreach ($n in @(@('Discord', 742), @('YouTube', 688), @('Telegram', 455))) { $r = [Activator]::CreateInstance((T 'SiteResult')); $r.Name = $n[0]; $r.Ok = $true; $r.Ms = $n[1]; $r.TcpMs = 20; $res.Add($r) }
$lt.GetField('siteResults', $IF).SetValue($l, $res)
$lt.GetField('vpnRunning', $IF).SetValue($l, $true)
Call 'UpdateStrategyList'; Call 'BuildSitePanel'
Pump 500

try {
    "== Перетаскивание за верхнюю панель"
    Add-Type -Namespace ZlUi -Name User32 -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);'
    # WM_NCHITTEST: Windows спрашивает окно, что под точкой; 2 — заголовок (окно тянется), 1 — содержимое (клик доходит до кнопки)
    function Hit($el, [double]$dx, [double]$dy) {
        $p = $el.PointToScreen((New-Object Windows.Point $dx, $dy))
        [int][ZlUi.User32]::SendMessage($hwnd, 0x84, [IntPtr]::Zero, [IntPtr](([int]$p.Y -shl 16) -bor ([int]$p.X -band 0xFFFF)))
    }
    function Mid([string]$name) { $e = $w.FindName($name); Hit $e ($e.ActualWidth / 2) ($e.ActualHeight / 2) }
    $search = $w.FindName('TopSearch').Parent.Parent
    Check 'пустое место слева от поиска тянет окно' (Hit $search -20 ($search.ActualHeight / 2)) 2
    Check 'поиск — поле, не заголовок' (Mid 'TopSearch') 1
    Check '«Отчёт» нажимается' (Mid 'BtnReport') 1
    Check '«Подобрать стратегию» нажимается' (Mid 'BtnMain') 1
    Check '«Свернуть» нажимается' (Mid 'BtnMinimize') 1
    Check '«Закрыть» нажимается' (Mid 'BtnClose') 1
    Check 'главная под панелью — содержимое' (Mid 'PowerButton') 1

    "== Навигация"
    Check 'старт — главная' (Page) 'Home'
    Click 'HomeAllStrategies'; Check '«все ›» открывает список стратегий' (Page) 'Strategies'
    Click 'NavHome'; Check 'повторный клик «Обход» возвращает на главную' (Page) 'Home'
    Up 'IllStrat'; Check 'карточка «Стратегия» открывает список' (Page) 'Strategies'
    Nav 'Vpn'; Check 'боковое меню → VPN' (Page) 'Vpn'
    Nav 'Home'; Up 'IllVpn'; Check 'карточка «VPN» → страница VPN' (Page) 'Vpn'
    Nav 'Home'; Up 'IllWatch'; Check 'карточка «Сторож» → настройки' (Page) 'Settings'
    Nav 'Home'; Up 'HomeVpnCard'; Check 'нижняя карточка VPN → страница VPN' (Page) 'Vpn'
    Nav 'Home'; Check 'запасная стратегия показана' ($w.FindName('HomeAlt').Visibility) 'Visible'
    Check '«все ›» с числом стратегий' ($w.FindName('HomeAllStrategies').Content) 'все 4 ›'
    $w.FindName('TopSearch').Text = 'ALT7'; Pump 400
    Check 'поиск сверху открывает список' (Page) 'Strategies'
    Check 'поиск сверху фильтрует список' ($w.FindName('StrategyList').Items.Count) 1
    Check 'есть результаты — пустой заглушки нет' ($w.FindName('StrategyEmpty').Visibility) 'Collapsed'
    $w.FindName('TopSearch').Text = 'нет-такой'; Pump 400
    Check 'поиск без результатов — заглушка «Ничего не найдено»' ("{0}|{1}" -f $w.FindName('StrategyEmpty').Visibility, $w.FindName('StrategyEmpty').Content.Children[1].Text) 'Visible|Ничего не найдено'
    $w.FindName('TopSearch').Text = ''; Pump 300
    Check 'очистка поиска возвращает весь список' ($w.FindName('StrategyList').Items.Count) 4
    Nav 'Home'
    $w.FindName('ConflictBar').Visibility = 'Visible'; Nav 'Vpn'
    Check 'предупреждение видно и на странице VPN' ([int]($w.FindName('ConflictBar').IsVisible -and $w.FindName('ConflictBar').ActualHeight -gt 0)) 1
    $w.FindName('ConflictBar').Visibility = 'Collapsed'; Nav 'Home'

    "== Состояния главной"
    Check 'обход работает, всё открывается' (State 'on' $true) 'Обход работает. | Всё открывается.'
    Check 'обход выключен' (State 'off' $false) 'Обход выключен. | Включить — один клик.'
    Check 'пауза из-за VPN' (State 'pause' $false) 'Обход на паузе. | Работает VPN.'
    $saved = @($cfg.VpnServers); $cfg.VpnServers.Clear(); $lt.GetField('vpnRunning', $IF).SetValue($l, $false); State 'on' $true | Out-Null
    Check 'без серверов: карточка VPN' $w.FindName('IllVpnTitle').Text 'VPN не настроен'
    Check 'без серверов: нижняя карточка' $w.FindName('HomeVpnName').Text 'Сервер не добавлен'
    foreach ($s in $saved) { $cfg.VpnServers.Add($s) }
    # живые картинки: сторож спит, VPN выключен, идёт подбор
    $cfg.Watchdog = $false; $lt.GetField('vpnRunning', $IF).SetValue($l, $false); Call 'UpdateGreeting' | Out-Null
    Check 'сторож спит — «zZ» на щите' $w.FindName('IllWatchCheck').Text 'zZ'
    Check 'VPN выключен — круг серый' ([string]$w.FindName('IllVpnCircle').Fill -eq [string]$app.Resources['IconBg']) 'True'
    $lt.GetField('picking', $IF).SetValue($l, $true); $w.FindName('PowerStatus').Text = 'Подбор 5 / 19'; Call 'UpdateGreeting' | Out-Null
    Check 'подбор — заголовок карточки стратегии' $w.FindName('IllStratTitle').Text 'Идёт подбор'
    Check 'подбор — счётчик в подписи' $w.FindName('IllStratSub').Text 'Подбор 5 / 19'
    Check 'подбор — стрелки крутятся' ($w.FindName('IllStratCheck').RenderTransform -is [Windows.Media.RotateTransform]) 'True'
    $lt.GetField('picking', $IF).SetValue($l, $false); $cfg.Watchdog = $true; $lt.GetField('vpnRunning', $IF).SetValue($l, $true); Call 'UpdateGreeting' | Out-Null
    Check 'сторож снова на посту — галочка' ([int][char]$w.FindName('IllWatchCheck').Text[0]) 0xE8FB

    "== Размер окна и масштаб"
    $area = [Windows.SystemParameters]::WorkArea
    Check 'минимальная ширина — 1080 или экран' $w.MinWidth ([Math]::Min(1080, $area.Width))
    $cfg.Scale = 130; Call 'ApplyScale'; Pump 200
    Check 'масштаб 130%: минимум не шире экрана' ([int]($w.MinWidth -le $area.Width)) 1
    Check 'масштаб 130%: минимум не выше экрана' ([int]($w.MinHeight -le $area.Height)) 1
    $w.Width = $w.MinWidth; $w.Height = $w.MinHeight; Pump 400
    Check 'масштаб 130%: кнопка «Закрыть» на экране' ([int]($w.FindName('BtnClose').PointToScreen((New-Object Windows.Point 0, 0)).X -lt ($area.Right * [Windows.PresentationSource]::FromVisual($w).CompositionTarget.TransformToDevice.M11))) 1
    $cfg.Scale = 100; Call 'ApplyScale'
}
catch { $script:fails++; "  FAIL проверка прервалась: $($_.Exception.Message)"; "       " + $_.InvocationInfo.PositionMessage }
finally { try { $lt.GetField('tray', $IF).GetValue($l).Visible = $false } catch { } }   # значок копии не остаётся в трее

""
if ($script:fails -gt 0) { "FAILED: $script:fails, passed: $script:passed"; exit 1 }
"OK: $script:passed checks passed"
