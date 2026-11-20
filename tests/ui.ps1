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
foreach ($n in 'Amsterdam #1', 'Amsterdam #2') { $s = [Activator]::CreateInstance($VS); $s.Name = $n; $s.Link = "vless://x@$($n[-1]).example.com:443"; $cfg.VpnServers.Add($s) }
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
    $w.FindName('PowerStatus').Text = $(if ($card -eq 'on') { 'Включён' } elseif ($card -eq 'pause') { 'Пауза' } else { 'Выключен' })
    $w.FindName('PowerTimer').Text = $(if ($card -eq 'on') { 'Работает 01:24:07 · ALT2' } else { '' })
    $w.FindName('GreetTitle').Text -replace "`n", ' | '
}
function Capture([string]$name) {
    if (-not $env:ZL_SCREENSHOTS) { return }
    New-Item -ItemType Directory -Force $env:ZL_SCREENSHOTS | Out-Null
    $w.UpdateLayout()
    $image = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$w.ActualWidth), ([int]$w.ActualHeight), 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $image.Render($w)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
    $stream = [IO.File]::Create((Join-Path $env:ZL_SCREENSHOTS "$name.png"))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

Pump 2000
$hwnd = (New-Object Windows.Interop.WindowInteropHelper $w).Handle
if (-not $w.IsVisible -or $w.ActualWidth -le 0 -or $hwnd -eq [IntPtr]::Zero) { 'SKIP: окно не открылось — нет рабочего стола'; exit 0 }
$w.Left = 40; $w.Top = 20
# демо-данные: обход работает, три сайта открываются, VPN подключён
$lt.GetField('exiting', $IF).SetValue($l, $true)   # таймеры и обновления интерфейса не перезаписывают демо-данные
$res = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType((T 'SiteResult')))
foreach ($n in @(@('Discord', 742), @('YouTube', 688), @('Telegram', 455))) { $r = [Activator]::CreateInstance((T 'SiteResult')); $r.Name = $n[0]; $r.Ok = $true; $r.Ms = $n[1]; $r.TcpMs = 20; $r.CheckedAt = [DateTime]::Now.Date.AddHours(21).AddMinutes(39).ToUniversalTime(); $res.Add($r) }
$lt.GetField('siteResults', $IF).SetValue($l, $res)
$lt.GetField('vpnRunning', $IF).SetValue($l, $true)
$lt.GetField('vpnActive', $IF).SetValue($l, (Call 'VpnKey'))
$lt.GetField('vpnVerifiedMs', $IF).SetValue($l, 125)
$vpnPing = $lt.GetField('vpnPing', $IF).GetValue($l)
$vpnPing[$cfg.VpnServers[0].Link] = 36
$vpnPing[$cfg.VpnServers[1].Link] = 24
Call 'SetConnectionEvent' 'VPN переподключился' 'Amsterdam #2'
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
    Check '«Был сбой» нажимается' (Mid 'BtnIncident') 1
    Check '«Отчёт» нажимается' (Mid 'BtnReport') 1
    Check '«Исправить соединение» нажимается' (Mid 'BtnMain') 1
    Check '«Свернуть» нажимается' (Mid 'BtnMinimize') 1
    Check '«Закрыть» нажимается' (Mid 'BtnClose') 1
    Check 'главная под панелью — содержимое' (Mid 'PowerButton') 1

    "== Навигация"
    State 'on' $true | Out-Null; Capture 'home-light'
    Check 'старт — главная' (Page) 'Home'
    Check 'главная: одна кнопка исправления' $w.FindName('BtnMain').Content 'Исправить соединение'
    Check 'расширенные инструменты изначально свёрнуты' $w.FindName('HomeAdvanced').IsExpanded 'False'
    Click 'BtnHomeSites'
    Check 'свои сайты открывают настройки' (Page) 'Settings'
    Check 'поле своих сайтов доступно' $w.FindName('MySitesBox').IsVisible 'True'
    Nav 'Home'
    $palette = @{}
    $paletteTimer = New-Object Windows.Threading.DispatcherTimer
    $paletteTimer.Interval = [TimeSpan]::FromMilliseconds(100)
    $paletteTimer.Add_Tick({
        $paletteTimer.Stop()
        $dialog = @($w.OwnedWindows) | Where-Object Title -eq 'Команды' | Select-Object -First 1
        if (!$dialog) { return }
        try {
            $panel = $dialog.Content.Child
            $searchBox = $panel.Children[0].Child
            $list = $panel.Children[1]
            $searchBox.Text = 'команды нет'
            $palette.empty = $list.Items.Count -eq 0
            $searchBox.Text = 'жур отк'
            $palette.matches = $list.Items.Count
            $event = [Windows.Input.KeyEventArgs]::new([Windows.Input.Keyboard]::PrimaryDevice, [Windows.PresentationSource]::FromVisual($dialog), 0, [Windows.Input.Key]::Enter)
            $event.RoutedEvent = [Windows.Input.Keyboard]::PreviewKeyDownEvent
            $searchBox.RaiseEvent($event)
        } finally { if ($dialog.IsVisible) { $dialog.Close() } }
    }.GetNewClosure())
    $paletteTimer.Start()
    try { Click 'BtnCommands' } finally { $paletteTimer.Stop() }
    Check 'поиск показывает отсутствие команд' $palette.empty $true
    Check 'поиск находит команду по двум словам в любом порядке' $palette.matches 1
    Check 'Enter выполняет выбранную команду' (Page) 'Log'
    Nav 'Home'
    Check 'сводка стратегии, VPN и истории видна без раскрытия' $w.FindName('HomeRow3').IsVisible 'True'
    Check 'робот виден без раскрытия инструментов' $w.FindName('Mascot').IsVisible 'True'
    Check 'сторож в расширенных инструментах' $w.FindName('IllWatch').IsVisible 'False'
    Check 'переключатель обхода соответствует карточке' $w.FindName('HomeBypassToggle').IsChecked 'True'
    Check 'переключатель VPN соответствует карточке' $w.FindName('ChipVpn').IsChecked 'True'
    Check 'статус VPN подтверждает подключение' $w.FindName('HomeVpnName').Text 'Подключён'
    Check 'последнее событие показано на главной' ($w.FindName('HomeEventText').Text -like 'VPN переподключился · Amsterdam #2 · *') 'True'
    $siteCard = $w.FindName('SitePanel').Children[0].Children[1].Child
    Check 'доступность показана до задержек' $siteCard.Children[1].Text 'Доступен'
    Check 'время относится к проверке сайта' $siteCard.Children[2].Text 'Проверка в 21:39'
    $details = $siteCard.Children[3]
    Check 'задержки свернуты' $details.IsExpanded 'False'
    $details.IsExpanded = $true
    Check 'HTTPS и TCP доступны в деталях' $details.Content.Children[0].Text 'HTTPS 742 мс  ·  TCP 20 мс'
    $details.IsExpanded = $false
    Click 'BtnHomeEvent'; Check 'последнее событие ведёт в журнал' (Page) 'Log'
    Nav 'Home'
    Check 'ручной подбор скрыт до раскрытия' $w.FindName('BtnAdvancedPick').IsVisible 'False'
    $w.FindName('HomeAdvanced').IsExpanded = $true; Pump 300
    Capture 'advanced-light'
    Check 'сторож доступен после раскрытия' $w.FindName('IllWatch').IsVisible 'True'
    Check 'после раскрытия ручной подбор доступен' $w.FindName('BtnAdvancedPick').IsVisible 'True'
    Check 'диагностика осталась доступна' $w.FindName('BtnWhy').IsVisible 'True'
    "== Отмена настоящего окна исправления"
    $repairState=@{pressed=$false;finished=$false;released=$false;title='';deadline=[DateTime]::UtcNow.AddSeconds(20)}
    $repairTimer=New-Object Windows.Threading.DispatcherTimer
    $repairTimer.Interval=[TimeSpan]::FromMilliseconds(5)
    $repairTimer.Add_Tick({
        $dialog=@($w.OwnedWindows) | Where-Object Title -eq 'Исправить соединение' | Select-Object -First 1
        if ($dialog) {
            $content=$dialog.Content.Child; $stop=$content.Children[4].Children[0]
            if (-not $repairState.pressed -and $stop.Content -eq 'Остановить' -and $lt.GetField('busy',$IF).GetValue($l)) {
                $repairState.pressed=$true
                $stop.RaiseEvent((New-Object Windows.RoutedEventArgs ([Windows.Controls.Primitives.ButtonBase]::ClickEvent,$stop)))
            }
            elseif ($stop.Content -eq 'Закрыть') {
                $repairState.finished=$true; $repairState.released=-not $lt.GetField('busy',$IF).GetValue($l)
                $repairState.title=$content.Children[3].Child.Children[0].Text
                $repairTimer.Stop(); $dialog.Close()
            }
        }
        if ([DateTime]::UtcNow -gt $repairState.deadline) { throw 'Исправление не завершило отмену за 20 секунд' }
    }.GetNewClosure())
    $repairTimer.Start()
    try { $task=Call 'RepairConnectionUi'; $task.GetAwaiter().GetResult() | Out-Null } finally { $repairTimer.Stop() }
    Check 'кнопка остановки была нажата во время работы' $repairState.pressed 'True'
    Check 'отмена завершилась и окно можно закрыть' $repairState.finished 'True'
    Check 'отмена сняла busy' $repairState.released 'True'
    Check 'показан итог отмены' $repairState.title 'Исправление остановлено'
    Click 'HomeAllStrategies'; Check '«все ›» открывает список стратегий' (Page) 'Strategies'
    Click 'NavHome'; Check 'повторный клик «Обход» возвращает на главную' (Page) 'Home'
    Up 'IllStrat'; Check 'карточка «Стратегия» открывает список' (Page) 'Strategies'
    Nav 'Vpn'; Check 'боковое меню → VPN' (Page) 'Vpn'
    $lt.GetField('exiting', $IF).SetValue($l, $false)
    $subType = T 'VpnSubscription'
    $first = [Activator]::CreateInstance($subType); $first.Id = 'a'; $first.Url = 'https://first.example.net/demo'
    $second = [Activator]::CreateInstance($subType); $second.Id = 'b'; $second.Url = 'https://second.example.net/demo'
    $cfg.VpnSubscriptions.Add($first); $cfg.VpnSubscriptions.Add($second)
    $cfg.VpnServers[0].SubscriptionId = 'a'; $cfg.VpnServers[1].SubscriptionId = 'b'
    Call 'RefreshVpnUi'
    $sources = $w.FindName('VpnSubscriptionList')
    $servers = $w.FindName('VpnServerList')
    $clickEvent = [Windows.Controls.Primitives.ButtonBase]::ClickEvent
    $source = $sources.Children[1]
    $source.RaiseEvent([Windows.RoutedEventArgs]::new($clickEvent, $source))
    Check 'подписка фильтрует серверы' $servers.Children.Count 1
    Check 'текущий сервер виден вне выбранной подписки' $w.FindName('VpnConnectedServer').Text 'Amsterdam #2'
    $star = $servers.Children[0].Child.Children[-1].Children[0]
    $star.RaiseEvent([Windows.RoutedEventArgs]::new($clickEvent, $star))
    Check 'звезда отмечает сервер в настройках' $cfg.VpnServers[0].Favorite 'True'
    $source = $sources.Children[0]
    $source.RaiseEvent([Windows.RoutedEventArgs]::new($clickEvent, $source))
    $w.FindName('VpnFavoritesOnly').IsChecked = $true
    Click 'VpnFavoritesOnly'
    Check 'фильтр оставляет только избранное' $servers.Children.Count 1
    $w.FindName('VpnFavoritesOnly').IsChecked = $false
    Click 'VpnFavoritesOnly'
    $w.FindName('VpnSearch').Text = 'Amsterdam #2'; Pump 300
    Check 'поиск работает для короткого списка' $servers.Children.Count 1
    $w.FindName('VpnSearch').Clear(); Pump 300
    Capture 'vpn-light'
    Click 'BtnVpnImport'
    Check 'кнопка импорта раскрывает форму' $w.FindName('VpnImport').IsExpanded 'True'
    $w.FindName('VpnImport').IsExpanded = $false
    $lt.GetField('exiting', $IF).SetValue($l, $true)
    Click 'BtnVpnProtection'
    Check 'кнопка защиты открывает настройки' $w.FindName('VpnTabRoutes').IsChecked 'True'
    Check 'выбор .exe доступен в настройках защиты' $w.FindName('BtnVpnPickApp').IsVisible 'True'
    Check 'подсказка при выключенной защите' $w.FindName('VpnKillScope').Text 'Защита выключена.'
    $cfg.VpnKillSwitch = $true
    Call 'RefreshVpnKillScope'
    Check 'пустая защита предлагает выбрать приложения' $w.FindName('VpnKillScope').Text 'Выберите программы для защиты.'
    $claudePath = Join-Path $work 'Claude.exe'
    Copy-Item -LiteralPath (Join-Path $work 'ZapretLauncher.exe') -Destination $claudePath
    $cfg.VpnProtectedApps.Add($claudePath)
    $cfg.VpnDomains.Add('claude.ai')
    $cfg.VpnMode = 'all'
    Call 'RefreshVpnKillScope'
    Check 'домен и режим всего трафика не расширяют блокировку' $w.FindName('VpnKillScope').Text 'При обрыве остановится трафик выбранных программ.'
    Check 'выбранный .exe виден в отдельном списке' $w.FindName('VpnProtectedApps').Children.Count 1
    Check 'пресеты видны внутри блока защиты' $w.FindName('VpnProtectPresets').IsVisible 'True'
    $presetPanel = $w.FindName('VpnProtectPresets')
    $ai = $presetPanel.Children[0].Children | Where-Object { $_.Content.Children[0].Text -eq 'ИИ' }
    $ai.RaiseEvent([Windows.RoutedEventArgs]::new($clickEvent, $ai))
    Pump 150
    $chips = $presetPanel.Children[1].Children
    $claude = $chips | Where-Object { $_.Content.Children[1].Text -eq 'Claude' }
    Check 'пресет Claude отмечен, если выбран его путь' $claude.IsChecked 'True'
    $w.FindName('SwVpnKill').IsChecked = $true
    Capture 'vpn-protection'
    $w.FindName('SwVpnKill').IsChecked = $false
    $claude.RaiseEvent([Windows.RoutedEventArgs]::new($clickEvent, $claude))
    Pump 300
    Check 'снятие пресета убирает программу из защиты' $cfg.VpnProtectedApps.Count 0
    Check 'снятие пресета не трогает доменные маршруты' $cfg.VpnDomains.Count 1
    $cfg.VpnDomains.Clear()
    $cfg.VpnMode = 'split'
    $cfg.VpnKillSwitch = $false
    $lt.GetField('vpnRouteRevision',$IF).SetValue($l, 0)
    Call 'RefreshVpnKillScope'
    Call 'RefreshVpnHealthUi'
    Call 'ResetVpnHealth'
    Check 'VPN без пробы не показывает успех' $w.FindName('VpnHealthText').Text 'Трафик через VPN ещё не проверен'
    $lt.GetField('vpnVerifiedMs',$IF).SetValue($l, 125)
    $lt.GetField('vpnActive',$IF).SetValue($l, (Call 'VpnKey'))
    Call 'RefreshVpnHealthUi'
    Check 'VPN показывает подтверждённый запрос' $w.FindName('VpnHealthText').Text 'Проверочный запрос прошёл через VPN · 125 мс'
    $lt.GetField('vpnRouteRevision',$IF).SetValue($l, 1)
    Call 'RefreshVpnHealthUi'
    Check 'VPN со старыми правилами не показывает подтверждённый маршрут' $w.FindName('VpnHealthText').Text 'Трафик через VPN ещё не проверен'
    $lt.GetField('vpnRouteRevision',$IF).SetValue($l, 0)
    $lt.GetField('vpnRunning',$IF).SetValue($l, $false)
    $lt.GetField('vpnHealthIssue',$IF).SetValue($l, 'Тестовая ошибка')
    Call 'RefreshVpnHealthUi'
    Check 'VPN после ошибки предлагает повторить подключение' $w.FindName('BtnVpnHealth').Content 'Повторить подключение'
    $lt.GetField('vpnRunning',$IF).SetValue($l, $true)
    Call 'ResetVpnHealth'
    Nav 'Home'; Up 'IllVpn'; Check 'карточка «VPN» → страница VPN' (Page) 'Vpn'
    Nav 'Home'; Up 'IllWatch'; Check 'карточка «Сторож» → настройки' (Page) 'Settings'
    Nav 'Home'; Up 'HomeVpnCard'; Check 'верхняя карточка VPN → страница VPN' (Page) 'Vpn'
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
    Check 'без серверов: верхняя карточка' $w.FindName('HomeVpnInfo').Text 'Сервер не добавлен'
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
    $w.Width = 1240; $w.Height = 760; $w.FindName('HomeAdvanced').IsExpanded = $false
    $lt.GetField('vpnVerifiedMs', $IF).SetValue($l, 125)
    Call 'RefreshVpnHealthUi'
    $cfg.Theme = 'dark'; Call 'ApplyTheme'; State 'on' $true | Out-Null; Pump 300; Capture 'home-dark'
    Nav 'Vpn'; $w.FindName('VpnTabServers').IsChecked = $true; Pump 350; Capture 'vpn-dark'
    Nav 'Home'
    $restoredConfig = '{"Strategy":"general.bat","VpnMode":"all"}'
    $configFile = Join-Path $work 'launcher.json'
    [IO.File]::WriteAllText($configFile, $restoredConfig)
    $lt.GetField('exiting', $IF).SetValue($l, $false)
    Call 'Exit' $false
    Check 'выход после восстановления сохраняет копию на диске' ([IO.File]::ReadAllText($configFile)) $restoredConfig
}
catch { $script:fails++; "  FAIL проверка прервалась: $($_.Exception.Message)"; "       " + $_.InvocationInfo.PositionMessage }
finally { try { $lt.GetField('tray', $IF).GetValue($l).Visible = $false } catch { } }   # значок копии не остаётся в трее

""
if ($script:fails -gt 0) { "FAILED: $script:fails, passed: $script:passed"; exit 1 }
"OK: $script:passed checks passed"
