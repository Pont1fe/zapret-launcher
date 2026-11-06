# Проверка файлов релиза на VirusTotal (запускает сборка на GitHub, см. .github/workflows/release.yml)
#
# Два шага. До публикации релиза — отправить файлы и дописать в заметки ссылки на их проверку:
#   ./tools/virustotal.ps1 -Files dist/ZapretLauncherSetup.exe, dist/ZapretLauncher.exe -NotesIn RELEASE_NOTES.md -NotesOut release-notes.md
# После публикации — дождаться результата и показать его в журнале сборки (на релиз он уже не влияет):
#   ./tools/virustotal.ps1 -Report
# Ключ API — в переменной окружения VT_API_KEY (секрет репозитория). Ошибки VirusTotal релиз не останавливают:
# файл, который не удалось отправить, просто не попадёт в заметки.
# -DryRun — ничего не отправлять, только посчитать хэши и собрать заметки (для проверки на своём компьютере).
param(
    [string[]]$Files,
    [string]$NotesIn,
    [string]$NotesOut,
    [switch]$Report,
    [int]$WaitMinutes = 10,
    [string]$State = 'virustotal.json',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$api = 'https://www.virustotal.com/api/v3'
$key = $env:VT_API_KEY

# Бесплатный ключ: 4 запроса в минуту. На 429 ждём минуту и повторяем.
function Vt([string[]]$CurlArgs) {
    if (-not $key) { throw 'VT_API_KEY is not set' }
    $out = [IO.Path]::GetTempFileName()
    try {
        for ($try = 1; $try -le 4; $try++) {
            $code = & curl.exe -s -S --max-time 300 -o $out -w '%{http_code}' -H "x-apikey: $key" @CurlArgs
            if ($code -eq '200') { return (Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json) }
            if ($code -ne '429' -and $code -notmatch '^5' -and $code -ne '000') {
                throw "VirusTotal answered $code`: $(Get-Content $out -Raw -Encoding UTF8)"
            }
            Start-Sleep -Seconds 60
        }
        throw "VirusTotal does not answer (last code $code)"
    } finally { Remove-Item $out -ErrorAction SilentlyContinue }
}

$Files = @($Files | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (-not $Report -and (-not $Files -or -not $NotesIn -or -not $NotesOut)) { throw 'Pass -Files, -NotesIn and -NotesOut, or -Report' }

if (-not $Report) {
$checked = @()
foreach ($f in $Files) {
    $name = Split-Path $f -Leaf
    $sha = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    $item = [pscustomobject]@{ Name = $name; Sha = $sha; Link = "https://www.virustotal.com/gui/file/$sha"; Analysis = $null; Result = $null }
    if ($DryRun) { $checked += $item; "$name  $sha  (dry run)"; continue }
    try {
        # до 32 МБ — обычная загрузка, крупнее — через отдельный адрес для загрузки
        $url = "$api/files"
        if ((Get-Item $f).Length -gt 32MB) { $url = (Vt @("$api/files/upload_url")).data }
        $r = Vt @('-X', 'POST', $url, '-F', "file=@$f")
        $item.Analysis = $r.data.id
        $checked += $item
        "$name sent: $($item.Link)"
    } catch { Write-Warning "$name was not checked: $($_.Exception.Message)" }
}

# Заметки релиза: исходный текст + ссылки на проверку
$notes = [IO.File]::ReadAllText((Resolve-Path $NotesIn), [Text.Encoding]::UTF8).TrimEnd()
if ($checked.Count -gt 0) {
    $notes += "`n`n### VirusTotal`n`n"
    $notes += ($checked | ForEach-Object { "- [$($_.Name)]($($_.Link)) — SHA-256 ``$($_.Sha)``" }) -join "`n"
}
[IO.File]::WriteAllText((Join-Path (Get-Location) $NotesOut), $notes + "`n", (New-Object Text.UTF8Encoding $false))
"notes: $NotesOut ($($checked.Count) of $($Files.Count) files with a VirusTotal link)"
if (-not $DryRun -and $checked.Count -gt 0) { ConvertTo-Json @($checked) -Depth 5 | Set-Content $State -Encoding UTF8 }
return
}

# Ждём результат проверки — только для журнала сборки
if (-not (Test-Path $State)) { 'nothing was sent to VirusTotal'; return }
$checked = @(Get-Content $State -Raw -Encoding UTF8 | ConvertFrom-Json)
$deadline = (Get-Date).AddMinutes($WaitMinutes)
while (($checked | Where-Object { -not $_.Result }) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 40
    foreach ($item in $checked | Where-Object { -not $_.Result }) {
        try {
            $a = (Vt @("$api/analyses/$($item.Analysis)")).data.attributes
            if ($a.status -eq 'completed') { $item.Result = $a.stats }
        } catch { Write-Warning "$($item.Name): $($_.Exception.Message)" }
    }
}
$summary = @('### VirusTotal', '')
foreach ($item in $checked) {
    if ($item.Result) {
        $s = $item.Result
        $total = [int]$s.malicious + [int]$s.suspicious + [int]$s.undetected + [int]$s.harmless
        $line = "$([int]$s.malicious) malicious, $([int]$s.suspicious) suspicious of $total engines"
        if ([int]$s.malicious -gt 0) { Write-Warning "$($item.Name): $line" } else { "$($item.Name): $line" }
    } else {
        $line = 'the scan is not finished yet, see the link'
        "$($item.Name): $line"
    }
    $summary += "- [$($item.Name)]($($item.Link)) — $line"
}
if ($env:GITHUB_STEP_SUMMARY) { Add-Content $env:GITHUB_STEP_SUMMARY ($summary -join "`n") -Encoding UTF8 }
