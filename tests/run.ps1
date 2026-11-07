param([string]$Exe = (Join-Path $PSScriptRoot '..\dist\ZapretLauncher.exe'))
# Тесты логики лаунчера: вызывают чистые функции собранного exe через reflection.
# Окно не открывается, лаунчер не запускается, winws, VPN, hosts и настройки не трогаются — поэтому тесты безопасно
# запускать и в GitHub Actions, и на компьютере с работающим лаунчером. Не добавляйте сюда вызовы, которые что-то меняют в системе.
# Запуск: powershell -ExecutionPolicy Bypass -File tests\run.ps1   (сначала build.ps1)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

# exe копируется во временную папку: Paths.Root — папка exe, и всё, что лаунчер мог бы туда записать, останется там.
# Загруженный exe занят до конца процесса, поэтому папка одна и та же и перезаписывается при следующем запуске
$work = Join-Path ([IO.Path]::GetTempPath()) 'zl-tests'
if (Test-Path $work) { Remove-Item (Join-Path $work '*') -Recurse -Force -ErrorAction SilentlyContinue } else { New-Item -ItemType Directory $work | Out-Null }
Copy-Item $Exe (Join-Path $work 'ZapretLauncher.exe') -Force
$asm = [Reflection.Assembly]::LoadFile((Join-Path $work 'ZapretLauncher.exe'))

$script:fails = 0; $script:passed = 0
$all = [Reflection.BindingFlags]'Static, Public, NonPublic'
function TypeOf([string]$name) { $asm.GetType("ZapretLauncher.$name", $true) }
function Call([string]$type, [string]$method) {
    $argv = New-Object object[] $args.Count   # без обёрток PowerShell, иначе reflection не примет List<string>
    for ($i = 0; $i -lt $args.Count; $i++) { if ($null -ne $args[$i]) { $argv[$i] = $args[$i].PSObject.BaseObject } }
    $m = (TypeOf $type).GetMethods($all) | Where-Object { $_.Name -eq $method -and $_.GetParameters().Count -eq $argv.Count } | Select-Object -First 1
    if (-not $m) { throw "$type.$method($($argv.Count)) not found" }
    try { $m.Invoke($null, $argv) } catch { throw (Inner $_.Exception) }
}
function Field([string]$type, [string]$name) { $f = (TypeOf $type).GetField($name, $all); if ($f) { $f.GetValue($null) } else { (TypeOf $type).GetProperty($name, $all).GetValue($null) } }
function Inner([Exception]$e) { while ($e.InnerException) { $e = $e.InnerException }; $e }
function Ok([string]$name) { $script:passed++ }
function Fail([string]$name, [string]$detail) { $script:fails++; "  FAIL $name"; if ($detail) { "       $detail" } }
function Check([string]$name, $got, $want) { if ("$got" -ceq "$want") { Ok $name } else { Fail $name "got:  $got`n       want: $want" } }
function Throws([string]$name, [scriptblock]$code, [string]$part) {
    try { & $code | Out-Null; Fail $name 'no exception' }
    catch { $m = (Inner $_.Exception).Message; if ($m.Contains($part)) { Ok $name } else { Fail $name "message: $m" } }
}
# каждый раздел — отдельно: если в одном что-то упало, остальные всё равно проверяются
function Section([string]$name, [scriptblock]$code) {
    "== $name"
    try { . $code } catch { Fail "$name — раздел прервался" ((Inner $_.Exception).Message + "`n       " + $_.InvocationInfo.PositionMessage) }
}
function B64([string]$s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s)) }
function List([string[]]$items) { $l = New-Object 'System.Collections.Generic.List[string]'; foreach ($i in $items) { $l.Add($i) }; , $l }

Section 'Списки доменов и приложений' {
    Check 'пробелы, запятые, строки' ((Call SitePresets SplitItems "a.com b.com,c.com;d.com`r`ne.com" $true) -join '|') 'a.com|b.com|c.com|d.com|e.com'
    Check 'комментарии и IP из hosts' ((Call SitePresets SplitItems "# list`n0.0.0.0 x.com # tail`n127.0.0.1 y.com`n::1 z.com" $true) -join '|') 'x.com|y.com|z.com'
    Check 'пути с пробелами' ((Call SitePresets SplitItems "`"C:\Program Files\Spotify\Spotify.exe`"`nDiscord" $false) -join '|') 'C:\Program Files\Spotify\Spotify.exe|Discord'
    Check 'пустой текст' (@(Call SitePresets SplitItems '' $true).Count) '0'
    foreach ($c in @(@('https://www.Example.com/path?x#y', 'example.com'), @('*.site.ru', 'site.ru'), @('пример.рф', 'xn--e1afmkfd.xn--p1ai'), @('192.168.1.20', ''),
                     @('localhost', ''), @('a.b.c.co.uk:443', 'a.b.c.co.uk'), @('t.co', 't.co'), @('x.ai', 'x.ai'), @('site.ru.', 'site.ru'), @('bad_domain.com', ''))) {
        Check ("домен " + $c[0]) (Call Zapret NormalizeDomain $c[0]) $c[1]
    }
    foreach ($c in @(@('"C:\Program Files\Spotify\Spotify.exe"', 'Spotify.exe'), @('discord', 'discord.exe'), @('Telegram.EXE', 'Telegram.EXE'), @('  ', ''), @('a|b', ''))) {
        Check ("приложение " + $c[0]) (Call Launcher NormalizeApp $c[0]) $c[1]
    }
}

Section 'Готовые наборы' {
    $presets = Field SitePresets All
    $groups = Field SitePresets Groups
    foreach ($p in $presets) {
        foreach ($d in $p.Domains) { if ((Call Zapret NormalizeDomain $d) -cne $d) { Fail "$($p.Name): домен $d" 'не в нормальной форме' } }
        foreach ($a in $p.Apps) { if ((Call Launcher NormalizeApp $a) -cne $a) { Fail "$($p.Name): программа $a" 'не в нормальной форме' } }
        if (-not ($groups -contains $p.Group)) { Fail "$($p.Name): вкладка" "неизвестная вкладка $($p.Group)" }
        try { [Windows.Media.ColorConverter]::ConvertFromString($p.Color) | Out-Null } catch { Fail "$($p.Name): цвет" $p.Color }
        if ($p.Domains.Count -eq 0) { Fail "$($p.Name): домены" 'пустой набор' }
    }
    Check 'имена наборов не повторяются' (@($presets | Group-Object Name | Where-Object Count -gt 1).Count) '0'
    Check 'в каждой вкладке есть наборы' (@($groups | Where-Object { $g = $_; -not ($presets | Where-Object Group -eq $g) }).Count) '0'
    $list = List @()
    foreach ($n in 'Instagram', 'Facebook') { foreach ($d in (Call SitePresets Find $n).Domains) { if (-not $list.Contains($d)) { $list.Add($d) } } }
    $insta = Call SitePresets Find 'Instagram'
    Check 'набор включён, если все его домены в списке' (Call SitePresets IsOn $insta $list) 'True'
    $removal = Call SitePresets RemovalSet $insta $list $false
    Check 'при удалении набора общие домены остаются' $removal.Contains('fbcdn.net') 'False'
    Check 'при удалении набора убираются только его домены' $removal.Count '4'
    $list.Remove('instagram.com') | Out-Null
    Check 'без одного домена набор выключен' (Call SitePresets IsOn $insta $list) 'False'
}

Section 'Наборы из репозитория' {
    function Parse([string]$json) { $err = $null; $m = (TypeOf SitePresets).GetMethod('Parse'); $a = [object[]]@($json, $null); $f = $m.Invoke($null, $a); @{ File = $f; Error = $a[1] } }
    function Json($sets, $groups = @('Общение', 'ИИ'), $format = 1) { @{ format = $format; updated = '2026-11-06'; groups = $groups; sets = @($sets) } | ConvertTo-Json -Depth 5 }
    $good = @{ name = 'Test'; group = 'ИИ'; mark = 'T'; color = '#10A37F'; domains = @('test.com', 'cdn-test.net'); apps = @('Test App.exe'); vpnOnly = $true }

    # файл в репозитории — тот, что скачают все лаунчеры: он должен читаться целиком, без пропущенных наборов
    $repoFile = Join-Path $PSScriptRoot '..\presets\sites.json'
    $r = Parse ([IO.File]::ReadAllText($repoFile, [Text.Encoding]::UTF8))
    Check 'presets/sites.json читается' $r.Error ''
    Check 'presets/sites.json: все наборы правильные' ($r.File.Skipped -join ', ') ''
    $builtIn = Field SitePresets BuiltIn
    Check 'presets/sites.json: встроенные наборы на месте' (@($builtIn | Where-Object { $n = $_.Name; -not ($r.File.Sets | Where-Object Name -eq $n) }).Count) '0'
    $rt = Parse (Call SitePresets BuiltInJson '2026-11-06')
    Check 'встроенные наборы → файл → наборы' ("{0} {1} {2}" -f $rt.File.Sets.Count, $rt.File.Skipped.Count, (($rt.File.Groups) -join ',')) "$($builtIn.Count) 0 $((Field SitePresets BuiltInGroups) -join ',')"
    $chat = $rt.File.Sets | Where-Object Name -eq 'ChatGPT'
    Check 'набор переносится полностью' ("{0}|{1}|{2}|{3}|{4}" -f $chat.Group, $chat.Color, ($chat.Domains -join ','), ($chat.Apps -join ','), $chat.VpnOnly) 'ИИ|#10A37F|chatgpt.com,openai.com,oaistatic.com,oaiusercontent.com,chat.com|ChatGPT.exe|True'

    $r = Parse (Json $good)
    Check 'правильный набор' ("{0} {1} {2}" -f $r.File.Sets[0].Name, ($r.File.Sets[0].Apps -join ','), $r.File.Sets[0].VpnOnly) 'Test Test App.exe True'
    Check 'пустая вкладка не показывается' ($r.File.Groups -join ',') 'ИИ'
    Check 'файл новее лаунчера не принимается' ("{0} {1}" -f ($null -eq $r.File), (Parse (Json $good -format 2)).Error) 'False format 2 — новее, чем понимает эта версия лаунчера'
    Check 'не JSON' ((Parse '<html>rate limit</html>').Error.StartsWith('не JSON')) 'True'
    Check 'пустой файл' (Parse '').Error 'пустой или слишком большой файл'
    Check 'без наборов' (Parse (Json @())).Error 'нет вкладок или наборов'
    $bad = @(
        @{ name = 'Домен'; group = 'ИИ'; mark = 'D'; color = '#000000'; domains = @('Https://Bad.com/x') },
        @{ name = 'Вкладка'; group = 'Нет такой'; mark = 'V'; color = '#000000'; domains = @('ok.com') },
        @{ name = 'Цвет'; group = 'ИИ'; mark = 'C'; color = 'red'; domains = @('ok.com') },
        @{ name = 'Программа'; group = 'ИИ'; mark = 'P'; color = '#000000'; domains = @('ok.com'); apps = @('C:\Windows\cmd.exe') },
        @{ name = 'Пусто'; group = 'ИИ'; mark = 'E'; color = '#000000'; domains = @() },
        @{ name = 'Test'; group = 'ИИ'; mark = 'T'; color = '#000000'; domains = @('dup.com') })
    $r = Parse (Json (@($good) + $bad))
    Check 'наборы с ошибками пропускаются, остальные работают' ("{0} | {1}" -f (($r.File.Sets | ForEach-Object Name) -join ','), ($r.File.Skipped -join ',')) 'Test | Домен,Вкладка,Цвет,Программа,Пусто,Test'
    Check 'одни ошибки — файл не принимается' (Parse (Json $bad[0..4])).Error 'ни одного правильного набора'

    # обновление: новые домены включённых наборов дописываются, выключенные наборы не трогаются
    $old = (Parse (Json $good)).File.Sets
    $newer = $good.Clone(); $newer.domains = @('test.com', 'cdn-test.net', 'test.ai'); $newer.apps = @('Test App.exe', 'TestHelper.exe')
    $fresh = (Parse (Json $newer)).File.Sets
    $domains = List 'test.com', 'cdn-test.net', 'other.org'; $apps = List 'test app.exe'
    Check 'новый домен включённого набора' ((Call SitePresets Additions $old $fresh $domains $domains $false) -join ',') 'test.ai'
    Check 'новая программа включённого набора' ((Call SitePresets Additions $old $fresh $domains $apps $true) -join ',') 'TestHelper.exe'
    $domains.Remove('cdn-test.net') | Out-Null
    Check 'выключенный набор не трогается' (@(Call SitePresets Additions $old $fresh $domains $domains $false).Count) '0'
}

Section 'Стратегии' {
    foreach ($c in @(@('general (FAKE TLS AUTO ALT2).bat', 'FAKE TLS AUTO ALT2', 'FA2', '#E0901A', 'FAKE TLS'), @('general.bat', 'Стандартная', 'G', '#3B82F6', 'Основная'),
                     @('general (ALT11).bat', 'ALT11', 'A11', '#7B6CF6', 'ALT'), @('general (SIMPLE FAKE ALT).bat', 'SIMPLE FAKE ALT', 'SA', '#14A37F', 'SIMPLE FAKE'))) {
        Check ("название " + $c[0]) (Call Zapret Title $c[0]) $c[1]
        Check ("значок " + $c[0]) (Call Zapret Badge $c[0]) $c[2]
        Check ("цвет " + $c[0]) (Call Zapret Color $c[0]) $c[3]
        Check ("группа " + $c[0]) (Call Launcher GroupOf $c[0]) $c[4]
    }
}

Section 'Версии' {
    foreach ($c in @(@('1.10.4', '1.10.3', 'True'), @('1.10.3', '1.10.4', 'False'), @('1.10.4', '1.10.4', 'False'), @('1.9.10', '1.9.9', 'True'), @('1.10.4', '', 'True'), @('', '1.0', 'False'))) {
        Check ("новее ли '$($c[0])' чем '$($c[1])'") (Call Util TestNewer $c[0] $c[1]) $c[2]
    }
}

Section 'Ключи VPN' {
    $o = Call Vpn BuildOutbound 'vless://uuid-1@example.com:443?security=reality&sni=www.google.com&pbk=KEY&sid=ab&flow=xtls-rprx-vision&type=tcp#My%20Server'
    Check 'vless reality: тип' $o['type'] 'vless'
    Check 'vless reality: сервер' ("{0}:{1}" -f $o['server'], $o['server_port']) 'example.com:443'
    Check 'vless reality: uuid и flow' ("{0} {1}" -f $o['uuid'], $o['flow']) 'uuid-1 xtls-rprx-vision'
    Check 'vless reality: sni' $o['tls']['server_name'] 'www.google.com'
    Check 'vless reality: ключ' ("{0} {1}" -f $o['tls']['reality']['public_key'], $o['tls']['reality']['short_id']) 'KEY ab'
    Check 'vless reality: отпечаток по умолчанию' $o['tls']['utls']['fingerprint'] 'chrome'
    $o = Call Vpn BuildOutbound 'vless://u@g.example.com:8443?type=grpc&serviceName=svc&security=tls&alpn=h2,http/1.1'
    Check 'vless grpc' ("{0} {1} {2}" -f $o['transport']['type'], $o['transport']['service_name'], ($o['tls']['alpn'] -join ',')) 'grpc svc h2,http/1.1'
    Throws 'неподдерживаемый транспорт' { Call Vpn BuildOutbound 'vless://u@k.example.com:443?type=kcp' } 'не поддерживается'
    $o = Call Vpn BuildOutbound 'trojan://pa%40ss@t.example.com:443?type=ws&path=%2Fws&host=cdn.example.com#T'
    Check 'trojan: пароль' $o['password'] 'pa@ss'
    Check 'trojan: ws' ("{0} {1} {2}" -f $o['transport']['type'], $o['transport']['path'], $o['transport']['headers']['Host']) 'ws /ws cdn.example.com'
    Check 'trojan: TLS всегда' ("{0} {1}" -f $o['tls']['enabled'], $o['tls']['server_name']) 'True cdn.example.com'
    $vm = 'vmess://' + (B64 '{"v":"2","ps":"VM","add":"v.example.com","port":"443","id":"uuid-v","aid":"0","net":"ws","path":"/p","host":"h.example.com","tls":"tls"}')
    $o = Call Vpn BuildOutbound $vm
    Check 'vmess: сервер' ("{0} {1}:{2} {3}" -f $o['type'], $o['server'], $o['server_port'], $o['uuid']) 'vmess v.example.com:443 uuid-v'
    Check 'vmess: ws и TLS' ("{0} {1} {2}" -f $o['transport']['path'], $o['transport']['headers']['Host'], $o['tls']['server_name']) '/p h.example.com h.example.com'
    Check 'vmess: имя из ps' (Call Vpn NameOf $vm) 'VM'
    $o = Call Vpn BuildOutbound ('ss://' + (B64 'aes-256-gcm:secret') + '@1.2.3.4:8388#SS')
    Check 'shadowsocks (SIP002)' ("{0} {1} {2} {3}:{4}" -f $o['type'], $o['method'], $o['password'], $o['server'], $o['server_port']) 'shadowsocks aes-256-gcm secret 1.2.3.4:8388'
    $o = Call Vpn BuildOutbound 'ss://2022-blake3-aes-128-gcm:pass%3D@s.example.com:443'
    Check 'shadowsocks 2022 без base64' ("{0} {1}" -f $o['method'], $o['password']) '2022-blake3-aes-128-gcm pass='
    $o = Call Vpn BuildOutbound ('ss://' + (B64 'chacha20-ietf-poly1305:pw@old.example.net:8443') + '#Old')
    Check 'shadowsocks старого формата' ("{0} {1} {2}:{3}" -f $o['method'], $o['password'], $o['server'], $o['server_port']) 'chacha20-ietf-poly1305 pw old.example.net:8443'
    $o = Call Vpn BuildOutbound 'hy2://pw@h.example.com:8443?sni=s.example.com&obfs=salamander&obfs-password=x#H'
    Check 'hysteria2' ("{0} {1} {2} {3}" -f $o['type'], $o['password'], $o['tls']['server_name'], $o['obfs']['password']) 'hysteria2 pw s.example.com x'
    $o = Call Vpn BuildOutbound 'tuic://uuid-t:pw@tu.example.com:443?congestion_control=cubic&alpn=h3&sni=sn.example.com'
    Check 'tuic' ("{0} {1} {2} {3} {4}" -f $o['type'], $o['uuid'], $o['password'], $o['congestion_control'], ($o['tls']['alpn'] -join ',')) 'tuic uuid-t pw cubic h3'
    Throws 'неизвестная схема' { Call Vpn BuildOutbound 'socks://u@h.example.com:1080' } 'Неизвестный тип ссылки'
    $xh = 'vless://u@x.example.com:443?type=xhttp&security=reality&pbk=K&sid=1&path=%2Fx#X'
    Check 'xhttp идёт через Xray' (Call Vpn NeedsXray $xh) 'True'
    Check 'обычный vless — без Xray' (Call Vpn NeedsXray 'vless://u@h.example.com:443?type=ws') 'False'
    $o = Call Vpn BuildOutbound $xh
    Check 'xhttp: sing-box отдаёт трафик в локальный SOCKS' ("{0} {1}" -f $o['type'], $o['server']) 'socks 127.0.0.1'
    $x = (Call Vpn BuildXrayConfig $xh 10808) | ConvertFrom-Json
    $st = $x.outbounds[0].streamSettings
    Check 'xhttp: настройки Xray' ("{0} {1} {2} {3}" -f $x.inbounds[0].port, $st.network, $st.xhttpSettings.path, $st.realitySettings.publicKey) '10808 xhttp /x K'
    $servers = (Call Vpn Parse "vless://a@h1.example.com:443#One`nмусор`nvless://b@h2.example.com:443").GetAwaiter().GetResult()
    Check 'список ключей: имена' (($servers | ForEach-Object { $_.Name }) -join '|') 'One|h2.example.com:443'
    Throws 'текст без ключей' { (Call Vpn Parse 'просто текст').GetAwaiter().GetResult() } 'Не нашёл ни одной ссылки'
    Throws 'зашифрованная ссылка Happ' { (Call Vpn Parse 'happ://crypt3/abc').GetAwaiter().GetResult() } 'Happ'
    Throws 'ключ AmneziaVPN' { (Call Vpn Parse 'vpn://AAAA_3icabc').GetAwaiter().GetResult() } 'AmneziaVPN'
    Check 'подписка в base64' (Call Vpn DecodeSubscription (B64 "vless://a@h1.example.com:443`nvless://b@h2.example.com:443")) "vless://a@h1.example.com:443`nvless://b@h2.example.com:443"
    Throws 'подписка Clash' { Call Vpn DecodeSubscription 'proxies: []' } 'JSON/Clash'
    $info = Call Vpn ParseUserInfo 'upload=1073741824; download=2147483648; total=10737418240; expire=0'
    Check 'сведения о подписке' ("{0} {1} {2}" -f $info.Used, $info.Total, ($null -eq $info.Expire)) '3221225472 10737418240 True'
}

Section 'AmneziaWG' {
    # выдуманные ключи и адрес из документационного диапазона 203.0.113.0/24
    $conf = "# AmneziaWG`r`n[Interface]`r`nPrivateKey = cHJpdmF0ZS1rZXktZm9yLXRlc3RzLW9ubHktMDAwMDA=`r`nAddress = 10.8.1.2/32`r`nDNS = 1.1.1.1`r`nJc = 4`r`nJmin = 40`r`nJmax = 70`r`n" +
            "S1 = 15`r`nS2 = 68`r`nH1 = 1185`r`nH2 = 2771`r`nH3 = 3391`r`nH4 = 4087`r`n`r`n[Peer]`r`nPublicKey = cHVibGljLWtleS1mb3ItdGVzdHMtb25seS0wMDAwMDA=`r`n" +
            "AllowedIPs = 0.0.0.0/0, ::/0`r`nEndpoint = 203.0.113.5:51820 # сервер`r`nPersistentKeepalive = 25`r`n"
    Check 'файл настроек распознаётся' (Call Awg LooksLikeConf $conf) 'True'
    Check 'ключ vless — не файл настроек' (Call Awg LooksLikeConf 'vless://u@h.example.com:443') 'False'
    $fields = Call Awg Fields $conf
    Check 'поля без комментариев' ("{0} | {1} | {2}" -f $fields['Peer.Endpoint'], $fields['Interface.Jc'], $fields['Peer.AllowedIPs']) '203.0.113.5:51820 | 4 | 0.0.0.0/0, ::/0'
    $de = Call Awg FromConf $conf 'C:\Users\me\Documents\de.conf'
    Check 'имя по файлу Amnezia (код страны)' $de.Name 'AmneziaWG · DE'
    Check 'по имени виден флаг страны' (Call Vpn CountryCode $de.Name) 'DE'
    Check 'хранится как ключ amneziawg://' ($de.Link.StartsWith('amneziawg://') -and (Call Vpn IsLink $de.Link)) 'True'
    Check 'из ключа возвращается тот же файл' ((Call Awg ConfOf $de.Link) -ceq ($conf.Replace("`r`n", "`n").Trim() + "`n")) 'True'
    Check 'без имени файла — имя по адресу сервера' (Call Awg FromConf $conf $null).Name 'AmneziaWG · 203.0.113.5'
    Check 'длинное имя файла остаётся как есть' (Call Awg FromConf $conf 'Мой сервер.conf').Name 'AmneziaWG · Мой сервер'
    $parsed = (Call Vpn Parse $de.Link).GetAwaiter().GetResult()
    Check 'ключ amneziawg:// добавляется как сервер' ("{0} {1}" -f @($parsed).Count, $parsed[0].Name) '1 AmneziaWG · DE'
    $pasted = (Call Vpn Parse $conf).GetAwaiter().GetResult()
    Check 'вставленный текст файла — тоже сервер' ("{0} {1}" -f @($pasted).Count, (Call Awg IsAwg $pasted[0].Link)) '1 True'
    Throws 'нет адреса сервера' { Call Awg Check ($conf -replace 'Endpoint = .*', '') } 'нет поля Endpoint'
    Throws 'ключ не подставлен' { Call Awg Check ($conf -replace 'PrivateKey = .*', 'PrivateKey = $WIREGUARD_CLIENT_PRIVATE_KEY') } 'нет ключа'
    Throws 'не файл настроек' { Call Awg Check 'просто текст' } 'не файл настроек'
    Throws 'повреждённый ключ' { (Call Vpn Parse 'amneziawg://%%%#x').GetAwaiter().GetResult() } 'повреждён'
    Check 'пинг: UDP, без проверки' (Call Vpn Ping $de).GetAwaiter().GetResult() '-2'
    # проверка серверов, где только AmneziaWG: без тестового ядра sing-box (и без его скачивания)
    $servers = [Activator]::CreateInstance([Collections.Generic.List`1].MakeGenericType((TypeOf VpnServer)))
    $servers.Add($de); $servers.Add((Call Awg FromConf $conf 'nl.conf'))
    Check 'проверка серверов AmneziaWG — «без проверки»' (((Call Vpn Delays $servers).GetAwaiter().GetResult()) -join ',') '-2,-2'
    Check 'ядро sing-box для этого не скачивалось' (Test-Path (Join-Path $work 'vpn\sing-box.exe')) 'False'

    # режимы «Только выбранное» и «Россия напрямую»: туннель без маршрутов и DNS — выход для sing-box
    $chain = Call Awg ChainConf $conf
    $cf = Call Awg Fields $chain
    Check 'туннель-выход: без маршрутов' $cf['Interface.Table'] 'off'
    Check 'туннель-выход: без DNS' $cf.ContainsKey('Interface.DNS') 'False'
    Check 'туннель-выход: маскировка и сервер на месте' ("{0} {1} {2}" -f $cf['Interface.Jc'], $cf['Interface.H4'], $cf['Peer.Endpoint']) '4 4087 203.0.113.5:51820'
    Check 'Table = off — в разделе [Interface], до [Peer]' ($chain.IndexOf('Table = off') -lt $chain.IndexOf('[Peer]')) 'True'
    Check 'старый Table заменяется' ([regex]::Matches((Call Awg ChainConf ($conf -replace 'DNS = 1.1.1.1', 'Table = auto')), '(?m)^Table').Count) '1'
    Check '[Interface] последним разделом' ((Call Awg Fields (Call Awg ChainConf "[Peer]`nPublicKey = x`nEndpoint = 1.2.3.4:1`n[Interface]`nPrivateKey = y`nAddress = 10.0.0.2/32"))['Interface.Table']) 'off'
    Check 'файл туннеля-выхода проходит проверку' (@((Call Awg Check $chain).Keys).Count -gt 0) 'True'
    foreach ($mode in 'split', 'ru') {
        $sb = (Call Vpn BuildConfig $de $mode (List 'spotify.com') (List 'Spotify.exe')) | ConvertFrom-Json
        Check "$($mode): трафик VPN — в адаптер AmneziaWG от его адреса" ("{0} {1} {2} {3}" -f $sb.outbounds[0].tag, $sb.outbounds[0].type, $sb.outbounds[0].bind_interface, $sb.outbounds[0].inet4_bind_address) 'proxy direct ZapretAWG 10.8.1.2'
        Check "$($mode): сам туннель AmneziaWG — напрямую" (@($sb.route.rules | Where-Object { $_.process_name -contains 'amneziawg.exe' -and $_.outbound -eq 'direct' }).Count) '1'
    }
    $sbRu = (Call Vpn BuildConfig $de 'ru' (List) (List)) | ConvertFrom-Json
    $first = @($sbRu.route.rules | Where-Object { $_.ip_cidr -contains '203.0.113.5/32' })
    Check 'сервер AmneziaWG — напрямую и по адресу' ("{0} {1}" -f $first.Count, $first[0].outbound) '1 direct'
    Check 'правило по адресу — раньше правил VPN' ([array]::IndexOf(@($sbRu.route.rules), $first[0]) -lt [array]::IndexOf(@($sbRu.route.rules), @($sbRu.route.rules | Where-Object { $_.rule_set })[0])) 'True'
    Check 'адрес сервера из файла' ((Call Awg EndpointIps $conf) -join ',') '203.0.113.5'
    Check 'адрес сервера IPv6 в скобках' ((Call Awg EndpointIps "[Interface]`nPrivateKey=a`nAddress=10.0.0.2/32`n[Peer]`nPublicKey=b`nEndpoint = [2001:db8::5]:51820") -join ',') '2001:db8::5'
    Check 'адрес туннеля из файла' (Call Awg TunnelAddress ($conf -replace 'Address = 10.8.1.2/32', 'Address = 10.8.1.2/32, fd00::2/128')) '10.8.1.2'
    Check 'адрес туннеля: только IPv6 — нет' ($null -eq (Call Awg TunnelAddress ($conf -replace 'Address = 10.8.1.2/32', 'Address = fd00::2/128'))) 'True'
    Check 'sing-box забирает трафик половинами — маршрут «для всего» остаётся у сети' ($sbRu.inbounds[0].route_address -join ',') '0.0.0.0/1,128.0.0.0/1'
    $vl = [Activator]::CreateInstance((TypeOf VpnServer)); $vl.Name = 'V'; $vl.Link = 'vless://u@h.example.com:443?security=tls'
    Check 'с обычным ключом — как раньше' ($null -eq ((Call Vpn BuildConfig $vl 'split' (List) (List)) | ConvertFrom-Json).inbounds[0].route_address) 'True'
    $sbSplit = (Call Vpn BuildConfig $de 'split' (List 'spotify.com') (List)) | ConvertFrom-Json
    Check 'выбранные сайты — через AmneziaWG, остальное напрямую' ("{0} {1}" -f @($sbSplit.route.rules | Where-Object { $_.domain_suffix -contains 'spotify.com' -and $_.outbound -eq 'proxy' }).Count, $sbSplit.route.final) '1 direct'
}

Section 'Страна сервера' {
    foreach ($c in @(@('🇳🇱 Amsterdam #1', 'NL'), @('[FI] Server', 'FI'), @('Германия · Франкфурт', 'DE'), @('USA New York', 'US'), @('Server NL-1', 'NL'),
                     @('Скорее быстрый', ''), @('Server', ''))) {
        Check ("страна '$($c[0])'") (Call Vpn CountryCode $c[0]) $c[1]
    }
    Check 'название без флага' (Call Vpn DisplayName '🇳🇱 Amsterdam #1') 'Amsterdam #1'
    $res = $asm.GetManifestResourceNames()
    foreach ($c in (Field Vpn Countries)) { if (-not ($res -contains ('flag-' + $c[0].ToLowerInvariant() + '.png'))) { Fail "флаг $($c[0])" 'нет картинки в app/flags' } }
}

Section 'Конфигурация VPN' {
    $srv = [Activator]::CreateInstance((TypeOf VpnServer)); $srv.Name = 'S'; $srv.Link = 'vless://u@h.example.com:443?security=tls'
    $split = (Call Vpn BuildConfig $srv 'split' (List 'spotify.com') (List 'Spotify.exe')) | ConvertFrom-Json
    Check 'выбранное: остальное напрямую' ("{0} {1} {2}" -f $split.route.final, $split.dns.final, $split.inbounds[0].strict_route) 'direct local False'
    Check 'выбранное: сайты через VPN' (@($split.route.rules | Where-Object { $_.domain_suffix -contains 'spotify.com' -and $_.outbound -eq 'proxy' }).Count) '1'
    Check 'выбранное: программы через VPN' (@($split.route.rules | Where-Object { $_.process_name -contains 'Spotify.exe' -and $_.outbound -eq 'proxy' }).Count) '1'
    Check 'выбранное: адреса сайтов через DNS VPN' (@($split.dns.rules | Where-Object { $_.domain_suffix -contains 'spotify.com' -and $_.server -eq 'remote' }).Count) '1'
    $allCfg = (Call Vpn BuildConfig $srv 'all' (List 'spotify.com') (List)) | ConvertFrom-Json
    Check 'весь трафик' ("{0} {1} {2}" -f $allCfg.route.final, $allCfg.dns.final, $allCfg.inbounds[0].strict_route) 'proxy remote True'
    Check 'весь трафик: без списков сайтов' (@($allCfg.route.rules | Where-Object { $_.domain_suffix }).Count) '0'
    $ru = (Call Vpn BuildConfig $srv 'ru' (List) (List)) | ConvertFrom-Json
    Check 'Россия напрямую' ("{0} {1}" -f $ru.route.final, (($ru.route.rule_set | ForEach-Object { $_.tag }) -join ',')) 'proxy geosite-ru,geoip-ru'
    Check 'Россия напрямую: .ru и .рф' (@($ru.route.rules | Where-Object { $_.domain_suffix -contains '.xn--p1ai' -and $_.outbound -eq 'direct' }).Count) '1'
    Check 'сервер VPN — первый выход' ("{0} {1}" -f $split.outbounds[0].tag, $split.outbounds[0].server) 'proxy h.example.com'
}

Section 'Telegram в hosts' {
    $hosts = "127.0.0.1 localhost`r`n# моя строка`r`n"
    $on = Call Telegram BuildHosts $hosts $true
    Check 'блок добавлен в конец' ($on.StartsWith($hosts.TrimEnd() + "`r`n`r`n# BEGIN ZapretLauncher Telegram`r`n") -and $on.EndsWith("# END ZapretLauncher Telegram`r`n")) 'True'
    Check 'повторное включение ничего не меняет' ((Call Telegram BuildHosts $on $true) -ceq $on) 'True'
    Check 'выключение возвращает hosts как был' ((Call Telegram BuildHosts $on $false) -ceq $hosts) 'True'
    Check 'выключение без блока ничего не меняет' ((Call Telegram BuildHosts $hosts $false) -ceq $hosts) 'True'
}

Section 'Журнал' {
    foreach ($c in @(@('[31.10 10:15:58] Сторож: не открывается YouTube (проверка 1 из 5)', 'warn'), @('[31.10 10:16:59] Не удалось показать уведомление', 'err'),
                     @('[31.10 10:17:10] Запущена стратегия: ALT', 'ok'), @('   в ZapretLauncher.Launcher.CreateFlyout()', 'trace'), @('[31.10 10:17:10] Обход остановлен', ''),
                     @('[31.10 10:17:10] Watchdog: not opening: YouTube (check 1 of 5)', 'warn'), @('[31.10 10:17:10] VPN connected: X', 'ok'))) {
        Check ("вид строки '$($c[0].Substring(17, [Math]::Min(30, $c[0].Length - 17)))'") (Call Launcher ParseLog $c[0]).Kind $c[1]
    }
    Check 'время отделяется от текста' ("{0}|{1}" -f (Call Launcher ParseLog '[31.10 10:17:10] Обход остановлен').Time, (Call Launcher ParseLog '[31.10 10:17:10] Обход остановлен').Text) '31.10 10:17:10|Обход остановлен'
}

Section 'Итог добавления' {
    Call I18n Init 'ru'
    Check 'много нераспознанных' (Call Launcher AddedInfo 3 3 (List 'a', 'b', 'c', 'd', 'e', 'f', 'g') 'Не похоже на домен' 'dup' 'normal') 'Добавлено: 3. Не похоже на домен: a, b, c, d, e (+2)'
    Check 'только повторы' (Call Launcher AddedInfo 0 2 (List) 'bad' 'Этот домен уже есть в списке' 'normal') 'Этот домен уже есть в списке'
}

Section 'Сторож' {
    $wt = TypeOf WatchdogPolicy
    $script:vpnAsked = 0
    $noVpn = [Func[bool]] { $script:vpnAsked++; $false }
    $vpnOn = [Func[bool]] { $true }
    $t0 = [DateTime]'2026-10-31 12:00'
    function New-Watchdog { [Activator]::CreateInstance($wt) }
    function Prop($w, [string]$name) { $wt.GetProperty($name).GetValue($w) }
    # одна проверка: ok из expected открылось, failing не открылось
    function Step($w, [int]$ok, [int]$expected, [int]$failing, [bool]$enabled, $vpn, [DateTime]$now) {
        '' + $wt.GetMethod('Next').Invoke($w, [object[]]@($ok, $expected, $failing, $enabled, $vpn, $now))
    }
    # n неудачных проверок подряд: failing сайтов из failing не открылись
    function Fails($w, [int]$n, [int]$failing = 1, [bool]$enabled = $true, $vpn = $noVpn, [DateTime]$now = $t0) {
        (1..$n | ForEach-Object { Step $w 0 $failing $failing $enabled $vpn $now }) -join ','
    }

    $w = New-Watchdog
    Check 'один сайт: 5 неудач, потом перезапуск' (Fails $w 5) 'Wait,Wait,Wait,Wait,Restart'
    Check 'один сайт: в журнале «проверка 5 из 5»' ("{0} из {1}" -f (Prop $w Attempt), (Prop $w Needed)) '5 из 5'
    Check 'про VPN спрашивает только перед действием' $script:vpnAsked '1'
    Check 'перезапуск не помог — подбор' (Fails $w 1) 'Pick'
    Check 'подбор начинает новую серию' ("{0} {1}" -f (Prop $w Restarted), (Prop $w LastPick).ToString('HH:mm')) 'False 12:00'
    Check 'через 10 минут снова сбой — только уведомление' (Fails $w 6 -now $t0.AddMinutes(10)) 'Wait,Wait,Wait,Wait,Restart,Cooldown'
    Check 'через 31 минуту можно подбирать снова' (Fails $w 6 -now $t0.AddMinutes(31)) 'Wait,Wait,Wait,Wait,Restart,Pick'

    $w = New-Watchdog
    Check 'несколько сайтов: 3 неудачи' (Fails $w 3 -failing 2) 'Wait,Wait,Restart'
    Check 'несколько сайтов: «проверка 3 из 3»' ("{0} из {1}" -f (Prop $w Attempt), (Prop $w Needed)) '3 из 3'

    $w = New-Watchdog
    Fails $w 3 | Out-Null
    Check 'сайты открылись — серия сброшена' (Step $w 1 1 0 $true $noVpn $t0) 'Healthy'
    Check 'после сброса счёт с начала' ((Fails $w 4) + " " + (Prop $w Attempt)) 'Wait,Wait,Wait,Wait 4'
    $w = New-Watchdog
    Fails $w 5 | Out-Null
    Step $w 1 1 0 $true $noVpn $t0 | Out-Null
    Check 'после того как сайты открылись, снова сначала перезапуск' (Fails $w 5) 'Wait,Wait,Wait,Wait,Restart'
    $w = New-Watchdog
    Fails $w 5 | Out-Null
    $wt.GetMethod('Reset').Invoke($w, $null) | Out-Null
    Check 'после сна снова сначала перезапуск' (Fails $w 5) 'Wait,Wait,Wait,Wait,Restart'
    Check 'один постоянно закрытый сайт не в счёт (baseline)' (Step (New-Watchdog) 2 2 1 $true $noVpn $t0) 'Healthy'

    $w = New-Watchdog
    $script:vpnAsked = 0
    Check 'сторож выключен — только уведомление' (Fails $w 5 -enabled $false) 'Wait,Wait,Wait,Wait,NotifyOnly'
    Check 'сторож выключен — VPN не проверяется' $script:vpnAsked '0'
    Check 'после уведомления — новая серия' ((Fails $w 1 -enabled $false) + " " + (Prop $w Attempt)) 'Wait 1'
    $w = New-Watchdog
    Check 'включён сторонний VPN — стратегию не трогает' (Fails $w 5 -vpn $vpnOn) 'Wait,Wait,Wait,Wait,VpnActive'
    Check 'при VPN перезапуска не было' ("{0} {1}" -f (Prop $w Restarted), (Fails $w 1 -vpn $vpnOn)) 'False Wait'
}

Section 'Список серверов' {
    # порядок: работающие по скорости → «без проверки» (UDP) → не проверены → не работают
    $pings = @(@('dead', -1), @('slow', 900), @('udp', -2), @('none', $null), @('fast', 120))
    $order = ($pings | Sort-Object { $r = if ($null -eq $_[1]) { Call Launcher PingRank $null } else { Call Launcher PingRank ([Nullable[int]]$_[1]) }; $r } | ForEach-Object { $_[0] }) -join ','
    Check 'работающие наверх, быстрые первыми' $order 'fast,slow,udp,none,dead'
}

Section 'Тестовое ядро: проверка и скорость' {
    function D($h) { $d = New-Object 'System.Collections.Generic.Dictionary[string,object]'; foreach ($k in $h.Keys) { $d[$k] = $h[$k] }; $d }
    function L($items) { $l = New-Object 'System.Collections.Generic.List[object]'; foreach ($x in $items) { $l.Add($x) }; , $l }
    $outs = L @((D @{ type = 'vless'; tag = 's3' }))
    $ping = (Call Vpn TestConfig $outs $null $null 40000 'k') | ConvertFrom-Json
    Check 'проверка: выходы и прямой' (($ping.outbounds | ForEach-Object tag) -join ',') 's3,direct'
    Check 'проверка: без входов и VPN-адаптера' ("{0} {1}" -f ($null -eq $ping.inbounds), $ping.route.final) 'True direct'
    $speed = (Call Vpn TestConfig $outs (L @((D @{ type = 'mixed'; tag = 'in3'; listen = '127.0.0.1'; listen_port = 41000 }))) (L @((D @{ inbound = [string[]]@('in3'); outbound = 's3' }))) 40000 'k') | ConvertFrom-Json
    Check 'скорость: свой прокси на сервер' ("{0} {1} {2}" -f $speed.inbounds[0].listen, $speed.route.rules[0].inbound[0], $speed.route.rules[0].outbound) '127.0.0.1 in3 s3'
    Check 'скорость: тестовый файл не больше 50 МБ' ([int64](([regex]'bytes=(\d+)').Match((Field Vpn SpeedUrl)).Groups[1].Value) -le 50000000) 'True'
}

Section 'Экспорт и импорт маршрутов' {
    $file = Call Launcher RoutesFile (List 'deepl.com', 'chatgpt.com') (List 'Spotify.exe')
    $d = List @(); $a = List @(); $bad = List @()
    Call Launcher SplitRoutes $file $d $a $bad
    Check 'файл → те же сайты' ($d -join ',') 'deepl.com,chatgpt.com'
    Check 'файл → те же программы' ($a -join ',') 'Spotify.exe'
    Check 'комментарии в файле не мешают' $bad.Count '0'
    $d = List @(); $a = List @(); $bad = List @()
    Call Launcher SplitRoutes "https://www.DeepL.com/translator`n`"C:\Program Files\Spotify\Spotify.exe`"`ndiscord.exe`nsite.com/path`ndeepl.com`n<плохо>`n# коммент" $d $a $bad
    Check 'чужой список: сайты' ($d -join ',') 'deepl.com,site.com'
    Check 'чужой список: программы' ($a -join ',') 'Spotify.exe,discord.exe'
    Check 'чужой список: мусор отдельно' ($bad -join ',') '<плохо>'
}

Section 'Напоминание о подписке' {
    $now = [DateTime]'2026-11-06 12:00'
    function Info($days, [long]$usedGb = 0, [long]$totalGb = 0) {
        $i = [Activator]::CreateInstance((TypeOf 'Vpn+SubscriptionInfo')); $i.Used = $usedGb * 1GB; $i.Total = $totalGb * 1GB
        if ($null -ne $days) { $i.Expire = $now.AddDays($days) }; $i
    }
    function Warn($info) { $w = Call Launcher SubscriptionWarnings $info $now; ($w.GetEnumerator() | Sort-Object Key | ForEach-Object { $_.Key -replace '^expire \d{12}', 'expire' -replace '^traffic \d+', 'traffic' }) -join ',' }
    Check 'нет сведений — молчим' (Warn $null) ''
    Check 'больше трёх дней — молчим' (Warn (Info 5)) ''
    Check 'бессрочная без лимита — молчим' (Warn (Info $null 500 0)) ''
    Check 'за три дня' (Warn (Info 2.5)) 'expire 3'
    Check 'за сутки' (Warn (Info 0.5)) 'expire 1'
    Check 'закончилась' (Warn (Info -0.1)) 'expire 0'
    $text = (Call Launcher SubscriptionWarnings (Info 2.5) $now).Values | Select-Object -First 1
    Check 'текст: дата и сколько дней' ($text.Contains('09.11.2026') -and $text.Contains('осталось дней: 3')) 'True'
    Check 'трафик 95%' (Warn (Info 30 95 100)) 'traffic 90'
    Check 'трафик кончился, и срок подходит' (Warn (Info 1.5 100 100)) 'expire 3,traffic 100'
    Check 'трафик 50% — молчим' (Warn (Info 30 50 100)) ''
    $a = (Call Launcher SubscriptionWarnings (Info 2) $now).Keys | Select-Object -First 1
    $b = (Call Launcher SubscriptionWarnings (Info 32) ($now.AddDays(30))).Keys | Select-Object -First 1
    Check 'после продления — новое напоминание' ($a -ne $b -and $null -ne $b) 'True'
}

Section 'Отчёт без секретов' {
    $sub = 'https://sub.example.com/api/sub/abcDEF123secretToken'
    $raw = "VPN: ключ vless://11111111-2222-3333-4444-555555555555@h.example.com:443?security=reality&pbk=KEY#S`n" +
           "файл amneziawg://W0ludGVyZmFjZV0KUHJpdmF0ZUtleSA9IGFhYQ#AmneziaWG`nPrivateKey = cHJpdmF0ZS1rZXktZm9yLXRlc3RzLW9ubHktMDAwMDA=`n" +
           "подписка $sub обновлена`nпапка $env:USERPROFILE\Desktop`nЗапущена стратегия: ALT9"
    $clean = Call Report Sanitize $raw $sub
    Check 'ключ vless скрыт' ($clean.Contains('11111111-2222') -or $clean.Contains('pbk=KEY')) 'False'
    Check 'ключ AmneziaWG скрыт' $clean.Contains('W0ludGVyZmFj') 'False'
    Check 'закрытый ключ скрыт' $clean.Contains('cHJpdmF0ZS1r') 'False'
    Check 'адрес подписки скрыт' $clean.Contains('secretToken') 'False'
    Check 'имя пользователя Windows скрыто' $clean.Contains($env:USERPROFILE) 'False'
    Check 'обычный текст на месте' ($clean.Contains('Запущена стратегия: ALT9') -and $clean.Contains('vless://<ключ>')) 'True'

    $cfg = [Activator]::CreateInstance((TypeOf Config))
    $s1 = [Activator]::CreateInstance((TypeOf VpnServer)); $s1.Name = 'Германия 3'; $s1.Link = 'vless://11111111-2222-3333-4444-555555555555@h.example.com:443?pbk=KEY#G3'
    $s2 = [Activator]::CreateInstance((TypeOf VpnServer)); $s2.Name = 'AmneziaWG · DE'; $s2.Link = 'amneziawg://W0ludGVyZmFjZV0KUHJpdmF0ZUtleSA9IGFhYQ#DE'
    $cfg.VpnServers.Add($s1); $cfg.VpnServers.Add($s2); $cfg.VpnSubscription = $sub; $cfg.VpnDomains.Add('deepl.com')
    $summary = Call Report Summary $cfg
    Check 'сводка: сервер и тип ключа видны' ($summary.Contains('Германия 3 (vless)') -and $summary.Contains('amneziawg 1')) 'True'
    Check 'сводка: без ключей и подписки' ($summary.Contains('11111111') -or $summary.Contains('W0ludGVy') -or $summary.Contains('secretToken')) 'False'
    Check 'сводка: сайты через VPN видны' $summary.Contains('deepl.com') 'True'
    $zip = Join-Path $work 'report-test.zip'
    Call Report Create $cfg $zip
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead($zip)
    $names = ($z.Entries | ForEach-Object { $_.FullName }) -join ','
    $text = ($z.Entries | ForEach-Object { (New-Object IO.StreamReader($_.Open())).ReadToEnd() }) -join "`n"
    $z.Dispose()
    Check 'архив: сводка и маршруты' ($names.Contains('report.txt') -and $names.Contains('routes.txt')) 'True'
    Check 'архив: без ключей и подписки' ($text.Contains('11111111') -or $text.Contains('secretToken')) 'False'

    # адреса серверов: отчёт может уйти в открытую заявку на GitHub
    $vmess = 'vmess://' + (B64 '{"v":"2","ps":"V","add":"vm.example.net","port":"443","id":"11111111-2222-3333-4444-555555555555","net":"ws"}')
    $ssOld = 'ss://' + (B64 'aes-256-gcm:pass@198.51.100.4:8388') + '#old'
    $awg = 'amneziawg://' + ((B64 "[Interface]`nPrivateKey = aaa`nAddress = 10.8.1.2/32`n[Peer]`nPublicKey = bbb`nEndpoint = 203.0.113.7:51820") -replace '\+', '-' -replace '/', '_' -replace '=+$', '') + '#DE'
    foreach ($c in @(@($s1.Link, 'h.example.com'), @($vmess, 'vm.example.net'), @($ssOld, '198.51.100.4'), @($awg, '203.0.113.7'),
                     @('hysteria2://pw@[2001:db8::5]:443#h', '2001:db8::5'), @('trojan://pw@tr.example.org:443#t', 'tr.example.org'))) {
        Check ("адрес сервера " + $c[0].Substring(0, $c[0].IndexOf('://'))) (Call Report ServerHost $c[0]) $c[1]
    }
    $hosts = List 'h.example.com', '203.0.113.7'
    $clean = Call Report Sanitize "к h.example.com:443 и 203.0.113.7:51820; чужие sub.h.example.com, 203.0.113.70, h.example.com.au" $null $hosts
    Check 'адреса серверов скрыты' $clean 'к <сервер>:443 и <сервер>:51820; чужие sub.h.example.com, 203.0.113.70, h.example.com.au'

    # заявка на GitHub: только версии и режимы
    $cfg.Strategy = 'ALT9'
    $system = Call Report IssueSystem $cfg
    Check 'заявка: версии и режимы' ($system.Contains('strategy: ALT9') -and $system.Contains('VPN: off') -and $system.Contains('vless') -and $system.Contains('(subscription)')) 'True'
    Check 'заявка: без сайтов, серверов и ключей' ($system.Contains('deepl.com') -or $system.Contains('Германия') -or $system.Contains('example') -or $system.Contains('11111111') -or $system.Contains('secretToken')) 'False'
    $url = Call Report IssueUrl 'Pont1fe/zapret-launcher' '4.0.5' "Windows: 10`nzapret: 1.9 & co"
    Check 'заявка: адрес формы' $url 'https://github.com/Pont1fe/zapret-launcher/issues/new?template=bug.yml&version=4.0.5&system=Windows%3A%2010%0Azapret%3A%201.9%20%26%20co'
}

Section 'Не пускать мимо VPN' {
    $lockSplit = (Call Vpn BuildLockConfig 'split' (List 'deepl.com') (List 'Spotify.exe')) | ConvertFrom-Json
    Check 'блокировка: выхода в VPN нет' (@($lockSplit.outbounds | Where-Object { $_.tag -eq 'proxy' }).Count) '0'
    Check 'блокировка: сайты из списка отклоняются' (@($lockSplit.route.rules | Where-Object { $_.domain_suffix -contains 'deepl.com' -and $_.action -eq 'reject' -and -not $_.outbound }).Count) '1'
    Check 'блокировка: программы из списка отклоняются' (@($lockSplit.route.rules | Where-Object { $_.process_name -contains 'Spotify.exe' -and $_.action -eq 'reject' }).Count) '1'
    Check 'блокировка «Только выбранное»: остальное напрямую' ("{0} {1}" -f $lockSplit.route.final, @($lockSplit.route.rules | Where-Object { $_.inbound }).Count) 'direct 0'
    Check 'блокировка: DNS только обычный' (($lockSplit.dns.servers | ForEach-Object { $_.tag }) -join ',') 'local'
    foreach ($mode in 'all', 'ru') {
        $lk = (Call Vpn BuildLockConfig $mode (List) (List)) | ConvertFrom-Json
        $rules = @($lk.route.rules)
        Check "блокировка «$mode»: всё остальное отклоняется — последним правилом" ("{0} {1}" -f ($rules[-1].inbound -join ','), $rules[-1].action) 'tun-in reject'
        Check "блокировка «$mode»: локальная сеть работает" ([array]::IndexOf($rules, @($rules | Where-Object { $_.ip_is_private })[0]) -lt ($rules.Count - 1)) 'True'
    }
    $lkRu = (Call Vpn BuildLockConfig 'ru' (List) (List)) | ConvertFrom-Json
    Check 'блокировка «Россия напрямую»: российское открывается' (@($lkRu.route.rules | Where-Object { $_.rule_set -and $_.outbound -eq 'direct' }).Count) '1'
    $vl = [Activator]::CreateInstance((TypeOf VpnServer)); $vl.Name = 'V'; $vl.Link = 'vless://u@h.example.com:443?security=tls'
    $normal = (Call Vpn BuildConfig $vl 'split' (List 'deepl.com') (List)) | ConvertFrom-Json
    Check 'без блокировки — как раньше, через VPN' (@($normal.route.rules | Where-Object { $_.domain_suffix -contains 'deepl.com' -and $_.outbound -eq 'proxy' }).Count) '1'
}

Section 'Надёжность и защита' {
    # список ключей: сервер с неподдерживаемым транспортом пропускается, один такой ключ — понятная ошибка
    $list = (Call Vpn Parse "vless://u@a.example.com:443?type=kcp#Bad`nvless://u@b.example.com:443?security=tls#Good").Result
    Check 'список ключей: плохой сервер не ломает остальные' (($list | ForEach-Object Name) -join ',') 'Good'
    Throws 'один плохой ключ — понятная ошибка' { (Call Vpn Parse 'vless://u@a.example.com:443?type=kcp').GetAwaiter().GetResult() } 'не поддерживается'

    # самообновление ставит только лаунчер нужной версии; подмену удаляет
    $exe = Join-Path $work 'ZapretLauncher.exe'
    $fake = Join-Path $work 'fake.exe'; [IO.File]::WriteAllText($fake, 'not an exe')
    Throws 'самообновление: не exe' { Call SelfUpdate Verify $fake '4.0.5' $false } 'повреждён'
    Check 'самообновление: подмена удалена' (Test-Path $fake) 'False'
    $old = Join-Path $work 'old.exe'; Copy-Item $exe $old
    Throws 'самообновление: версия старее нужной' { Call SelfUpdate Verify $old '99.0.0' $false } 'не похож'
    $ok = Join-Path $work 'ok.exe'; Copy-Item $exe $ok
    Call SelfUpdate Verify $ok (Field AppVersion Value) $true
    Check 'самообновление: нужная версия принимается' (Test-Path $ok) 'True'

    # испорченный launcher.json не затирается настройками по умолчанию молча
    $cfgFile = Join-Path $work 'launcher.json'
    [IO.File]::WriteAllText($cfgFile, '{"VpnServers":[{"Name":"x"')
    $c = Call Config Load
    Check 'испорченный launcher.json: как новая установка, копия отложена' ("{0} {1} {2}" -f (Field Config Broken), (Field Config Fresh), (Test-Path "$cfgFile.broken")) 'True True True'
    $c.Save()
    Check 'сохранение: файл читается, временного не осталось' ("{0} {1}" -f ($null -ne ((Get-Content $cfgFile -Raw) | ConvertFrom-Json)), (Test-Path "$cfgFile.tmp")) 'True False'
    Remove-Item $cfgFile, "$cfgFile.broken" -ErrorAction SilentlyContinue

    # удаление стирает только свои файлы — и знает их все
    $own = @(Field Uninstaller OwnFiles) + @(Field Uninstaller OwnDirs)
    $mine = @((Field Paths Config), (Field Paths Log), (Field Paths History), (Field Paths HostsCache), (Field Paths HostsBackup), (Field Paths Zapret),
              (Field Paths Backup), (Field Paths Temp), (Field Paths TgDir), (Field Vpn Dir), (Field SitePresets Cache), (Field SelfUpdate PrevFile), (Field TaskbarMenu Helper)) |
            ForEach-Object { Split-Path $_ -Leaf }
    Check 'удаление знает все файлы лаунчера' (@($mine | Where-Object { $own -notcontains $_ }) -join ',') ''

    # папка, которую может менять любой пользователь, распознаётся
    $d = Join-Path $work 'acl'; New-Item -ItemType Directory $d -Force | Out-Null
    Check 'папка: чужих прав на запись нет' (Call Shell OthersCanWrite $d) 'False'
    $acl = Get-Acl $d
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-11'), 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl $d $acl
    Check 'папка: «Прошедшие проверку» могут менять' (Call Shell OthersCanWrite $d) 'True'
}

Section 'Перевод' {
    Call I18n Init 'en'
    $cyr = New-Object Text.RegularExpressions.Regex '[А-Яа-яЁё]'
    $map = Field I18n Map
    foreach ($kv in $map.GetEnumerator()) { if ($kv.Value.Trim().Length -eq 0 -or $cyr.IsMatch($kv.Value)) { Fail "перевод '$($kv.Key)'" "'$($kv.Value)' — пары в I18n.cs сдвинулись?" } }
    # сообщения, собранные из частей, переводятся целиком
    foreach ($m in @('Подписка VPN заканчивается: 09.11.2026 12:00 (осталось дней: 3). Продлите её у своего провайдера',
                     'Трафик подписки VPN почти закончился: использовано 95.0 ГБ из 100.0 ГБ', 'VPN: режим из меню значка — Россия напрямую')) {
        $tr = Call I18n T $m; if ($cyr.IsMatch($tr)) { Fail "перевод сообщения" $tr }
    }
    # вся разметка окна, мини-окна и стилей должна переводиться целиком (название языка в выборе языка — нарочно на своём языке)
    $keep = @('Русский')
    foreach ($r in 'ui.xaml', 'flyout.xaml', 'styles.xaml') {
        $xaml = (New-Object IO.StreamReader ($asm.GetManifestResourceStream($r), [Text.Encoding]::UTF8)).ReadToEnd()
        foreach ($m in [regex]::Matches($xaml, '(Text|Content|ToolTip|Tag)="([^"]*)"')) {
            $v = [Net.WebUtility]::HtmlDecode($m.Groups[2].Value)
            if ($cyr.IsMatch($v) -and $keep -notcontains $v -and $cyr.IsMatch((Call I18n T $v))) { Fail "перевод из ${r}" "'$v' -> '$(Call I18n T $v)'" }
        }
    }
    foreach ($s in 'Общение', 'Медиа', 'Соцсети', 'Работа', 'ИИ', 'Готовые наборы', 'Сторож сменил стратегию', 'Вернуть прежнюю', 'Только важное',
                   'Сторож: не открывается YouTube (проверка 2 из 5)', 'Сторож: перезапускаю текущую стратегию — проверю ещё раз через минуту',
                   'Показаны первые 300 — остальные найдёт поиск', 'Работает 01:24:07', 'Запущена стратегия: ALT11',
                   'Это ключ AmneziaVPN — сам ключ лаунчер открыть не может. В приложении Amnezia откройте подписку → «Файлы конфигурации», скачайте файл нужной страны и добавьте его кнопкой «Файл AmneziaWG…»',
                   'Туннель AmneziaWG не поднялся: handshake did not complete', 'В файле настроек AmneziaWG нет поля Endpoint',
                   'Ядро AmneziaWG обновлено до версии 3.2.0', 'Ядро AmneziaWG: установлена версия 3.1.0, доступна 3.2.0', 'Ядро AmneziaWG: установлена версия 3.1.0 (последняя)') {
        $t = Call I18n T $s
        if ($cyr.IsMatch($t)) { Fail "перевод '$s'" $t } else { Ok $s }
    }
    Field SitePresets All | ForEach-Object { $_.Group } | Select-Object -Unique | ForEach-Object { if ($cyr.IsMatch((Call I18n T $_))) { Fail "перевод вкладки '$_'" } }
    # «Что нового» показывается и на английском — каждый пункт каждой версии должен переводиться
    $whatsNew = Field Launcher WhatsNew
    foreach ($kv in $whatsNew.GetEnumerator()) { foreach ($item in $kv.Value) { if ($cyr.IsMatch((Call I18n T $item))) { Fail "перевод «Что нового» $($kv.Key)" $item } } }
    Check 'в «Что нового» есть текущая версия' ($whatsNew.ContainsKey((Field AppVersion Value))) 'True'
}

''
if ($script:fails) { "FAILED: $($script:fails), passed: $($script:passed)"; exit 1 }
"OK: $($script:passed) checks passed"
