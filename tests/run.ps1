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
function Field([string]$type, [string]$name) { (TypeOf $type).GetField($name, $all).GetValue($null) }
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

Section 'Перевод' {
    Call I18n Init 'en'
    $cyr = New-Object Text.RegularExpressions.Regex '[А-Яа-яЁё]'
    $map = Field I18n Map
    foreach ($kv in $map.GetEnumerator()) { if ($kv.Value.Trim().Length -eq 0 -or $cyr.IsMatch($kv.Value)) { Fail "перевод '$($kv.Key)'" "'$($kv.Value)' — пары в I18n.cs сдвинулись?" } }
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
