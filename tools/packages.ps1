# Манифесты winget и Scoop для версии: адрес установщика в релизе и его SHA256.
# CI запускает после публикации релиза: bucket/ сразу работает для Scoop, packaging/winget/ — для первой отправки
# в winget (wingetcreate submit), дальше версии отправляет сам CI (wingetcreate update, если есть секрет WINGET_TOKEN).
#   powershell -File tools\packages.ps1 -Version 4.0.8 -Installer dist\ZapretLauncherSetup.exe
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Installer
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 4.0.8, got '$Version'" }
$root = Split-Path $PSScriptRoot
$sha = (Get-FileHash -Algorithm SHA256 $Installer).Hash
$repo = 'Pont1fe/zapret-launcher'
$url = "https://github.com/$repo/releases/download/v$Version/ZapretLauncherSetup.exe"
$utf8 = New-Object Text.UTF8Encoding($false)
function Write-File([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    [IO.File]::WriteAllText($path, $text.Replace("`r`n", "`n"), $utf8)
}

# ---------- Scoop: ставит обычным установщиком (/S); при удалении — тихое удаление лаунчера ----------
# Лаунчеру нужна своя папка с правами только для администраторов (из неё запускается драйвер WinDivert),
# поэтому в папку Scoop он не кладётся. $cmd — команда Scoop: при обновлении старую версию не удаляем, иначе пропали бы настройки
$scoop = [ordered]@{
    version     = $Version
    description = 'Discord, YouTube and Telegram work again in one click: a launcher for zapret with a built-in VPN client'
    homepage    = "https://github.com/$repo"
    license     = 'MIT'
    notes       = @('Installed to C:\ZapretLauncher by its own installer (needs administrator rights).', 'The launcher updates itself; scoop update is optional.')
    url         = "$url#/setup.exe"
    hash        = $sha.ToLower()
    installer   = [ordered]@{ script = @('Start-Process "$dir\setup.exe" -ArgumentList ''/S'' -Verb RunAs -Wait') }
    uninstaller = [ordered]@{ script = @(
        'if ($cmd -ne ''uninstall'') { return }',
        '$loc = (Get-ItemProperty ''HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ZapretLauncher'' -ErrorAction SilentlyContinue).InstallLocation',
        'if ($loc -and (Test-Path "$loc\ZapretLauncher.exe")) { Start-Process "$loc\ZapretLauncher.exe" -ArgumentList ''--uninstall'', ''--quiet'' -Verb RunAs -Wait }') }
    checkver    = 'github'
    autoupdate  = [ordered]@{ url = "https://github.com/$repo/releases/download/v`$version/ZapretLauncherSetup.exe#/setup.exe" }
}
Write-File (Join-Path $root 'bucket\zapret-launcher.json') (($scoop | ConvertTo-Json -Depth 5) + "`n")

# ---------- winget: манифест из четырёх файлов (схема 1.6.0) ----------
$id = 'Pont1fe.ZapretLauncher'
$dir = Join-Path $root 'packaging\winget'
$head = "PackageIdentifier: $id`nPackageVersion: $Version`n"
Write-File (Join-Path $dir "$id.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
$($head)DefaultLocale: ru-RU
ManifestType: version
ManifestVersion: 1.6.0
"@
Write-File (Join-Path $dir "$id.installer.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
$($head)InstallerType: exe
Scope: machine
InstallModes:
  - interactive
  - silent
InstallerSwitches:
  Silent: /S
  SilentWithProgress: /S
  InstallLocation: /D=<INSTALLPATH>
ElevationRequirement: elevatesSelf
UpgradeBehavior: install
ProductCode: ZapretLauncher
AppsAndFeaturesEntries:
  - DisplayName: Zapret Launcher
    Publisher: Pont1fe (zapret by Flowseal)
    ProductCode: ZapretLauncher
Installers:
  - Architecture: neutral
    InstallerUrl: $url
    InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@
$common = @"
Publisher: Pont1fe
PublisherUrl: https://github.com/Pont1fe
PublisherSupportUrl: https://github.com/$repo/issues
PackageName: Zapret Launcher
PackageUrl: https://github.com/$repo
License: MIT
LicenseUrl: https://github.com/$repo/blob/main/LICENSE
Tags:
  - zapret
  - dpi
  - discord
  - youtube
  - telegram
  - vpn
  - sing-box
ReleaseNotesUrl: https://github.com/$repo/releases/tag/v$Version
"@
Write-File (Join-Path $dir "$id.locale.ru-RU.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
$($head)PackageLocale: ru-RU
$common
ShortDescription: Discord, YouTube и Telegram снова работают — одним нажатием
Description: Удобная оболочка для zapret-discord-youtube со встроенным VPN-клиентом. Сама подбирает рабочую стратегию, обновляет zapret и чинит обход, если он сломался.
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@
Write-File (Join-Path $dir "$id.locale.en-US.yaml") @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.1.6.0.schema.json
$($head)PackageLocale: en-US
$common
ShortDescription: Discord, YouTube and Telegram work again in one click
Description: A launcher for zapret-discord-youtube with a built-in VPN client. It finds a working strategy, keeps zapret up to date and repairs the bypass when it breaks.
ManifestType: locale
ManifestVersion: 1.6.0
"@
"Scoop and winget manifests for $Version (SHA256 $sha)"
