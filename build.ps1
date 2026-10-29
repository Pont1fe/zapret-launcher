# Сборка: dist\ZapretLauncher.exe и dist\ZapretLauncherSetup.exe встроенным в Windows компилятором C# (.NET Framework 4.8)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $dist, $obj | Out-Null
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'

# Значок: щит в скруглённом квадрате (тот же рисунок, что в лаунчере — Icons.Draw)
function New-AppBitmap([int]$Size) {
    $bmp = New-Object Drawing.Bitmap $Size, $Size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $pad = [Math]::Max(1, $Size / 32)
    $rect = New-Object Drawing.RectangleF($pad, $pad, ($Size - 2 * $pad), ($Size - 2 * $pad))
    $r = $rect.Width * 0.28
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(44, 211, 177), [Drawing.Color]::FromArgb(18, 140, 117), [single]45)
    $g.FillPath($brush, $path)
    # белый щит с буквой Z, по центру по границам фигуры (как Icons.Draw в лаунчере)
    $shield = New-Object Drawing.Drawing2D.GraphicsPath
    $shield.AddBezier(50, 8, 64, 16, 76, 18, 88, 18); $shield.AddLine(88, 18, 88, 46); $shield.AddBezier(88, 46, 88, 70, 72, 86, 50, 94)
    $shield.AddBezier(50, 94, 28, 86, 12, 70, 12, 46); $shield.AddLine(12, 46, 12, 18); $shield.AddBezier(12, 18, 24, 18, 36, 16, 50, 8); $shield.CloseFigure()
    $z = New-Object Drawing.Drawing2D.GraphicsPath
    $z.AddLines([Drawing.PointF[]]@((New-Object Drawing.PointF 34, 32), (New-Object Drawing.PointF 66, 32), (New-Object Drawing.PointF 34, 68), (New-Object Drawing.PointF 66, 68)))
    $b = $shield.GetBounds()
    $k = $Size * $(if ($Size -le 24) { 0.66 } else { 0.56 }) / [Math]::Max($b.Width, $b.Height)
    $m = New-Object Drawing.Drawing2D.Matrix
    $m.Translate($Size / 2, $Size / 2); $m.Scale($k, $k); $m.Translate(-($b.X + $b.Width / 2), -($b.Y + $b.Height / 2))
    $shield.Transform($m); $z.Transform($m)
    $g.FillPath([Drawing.Brushes]::White, $shield)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(31, 175, 147)), ([Math]::Max(1.3, $Size * 0.07))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawPath($pen, $z)
    $g.Dispose()
    $bmp
}
# .ico из нескольких PNG-кадров (Windows Vista+)
$sizes = 16, 24, 32, 48, 64, 256
$frames = foreach ($s in $sizes) { $ms = New-Object IO.MemoryStream; (New-AppBitmap $s).Save($ms, [Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray() }
$ico = Join-Path $obj 'app.ico'
$w = New-Object IO.BinaryWriter([IO.File]::Create($ico))
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i] % 256
    $w.Write([byte]$s); $w.Write([byte]$s); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($fr in $frames) { $w.Write($fr) }
$w.Close()
$logo = Join-Path $obj 'logo.png'
(New-AppBitmap 112).Save($logo, [Drawing.Imaging.ImageFormat]::Png)

function Invoke-Csc([string[]]$CscArgs) {
    & $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu $CscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc завершился с кодом $LASTEXITCODE" }
}

# ZXing.Net — чтение QR-кодов (ключи VPN с картинки или экрана). Версия и контрольная сумма закреплены; библиотека встраивается в exe
$zxingVersion = '0.16.11'
$zxingSha = '7D39234D668E558B3D374D18116ED57021D7C0DF482801F1E1DB4F1DB9314EC3'
$zxingDll = Join-Path $obj 'zxing.dll'
if (-not (Test-Path $zxingDll)) {
    $pkg = Join-Path $obj 'zxing.nupkg'
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/zxing.net/$zxingVersion/zxing.net.$zxingVersion.nupkg" -OutFile $pkg -UseBasicParsing
    if ((Get-FileHash $pkg -Algorithm SHA256).Hash -ne $zxingSha) { Remove-Item $pkg; throw "Контрольная сумма ZXing.Net не совпала" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipFile = [IO.Compression.ZipFile]::OpenRead($pkg)
    try { [IO.Compression.ZipFileExtensions]::ExtractToFile(($zipFile.Entries | Where-Object { $_.FullName -eq 'lib/net48/zxing.dll' }), $zxingDll, $true) } finally { $zipFile.Dispose() }
    Remove-Item $pkg
}

# Помощник для меню в панели задач (без прав администратора) — встраивается в лаунчер, тот кладёт его рядом с собой
$helperExe = Join-Path $obj 'ZapretLauncherCmd.exe'
Invoke-Csc @("/out:$helperExe", "/win32icon:$ico", "/win32manifest:$(Join-Path $root 'helper\app.manifest')", (Join-Path $root 'helper\Cmd.cs'))

# Лаунчер
$app = Join-Path $root 'app'
$launcherExe = Join-Path $dist 'ZapretLauncher.exe'
# флаги стран для списка серверов VPN (flagcdn.com, изображения флагов — общественное достояние)
$flagArgs = @(Get-ChildItem (Join-Path $app 'flags') -Filter *.png | ForEach-Object { "/resource:$($_.FullName),flag-$($_.Name)" })
Invoke-Csc (@(
    "/resource:$helperExe,ZapretLauncherCmd.exe", "/r:$zxingDll", "/resource:$zxingDll,zxing.dll",
    "/out:$launcherExe", "/win32icon:$ico", "/win32manifest:$(Join-Path $app 'app.manifest')",
    "/lib:$wpf", '/r:PresentationFramework.dll', '/r:PresentationCore.dll', '/r:WindowsBase.dll', '/r:System.Xaml.dll',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll', '/r:System.ServiceProcess.dll',
    '/r:System.Net.Http.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
    '/r:System.Web.Extensions.dll', '/r:Microsoft.CSharp.dll', '/r:System.Core.dll',
    "/resource:$(Join-Path $app 'ui.xaml'),ui.xaml", "/resource:$(Join-Path $app 'styles.xaml'),styles.xaml", "/resource:$(Join-Path $app 'flyout.xaml'),flyout.xaml",
    (Join-Path $app '*.cs')
) + $flagArgs)

# Установщик (внутри — только что собранный лаунчер) получает ту же версию
$version = [regex]::Match((Get-Content (Join-Path $app 'App.cs') -Raw), 'Value = "([^"]+)"').Groups[1].Value
$setupVersion = Join-Path $obj 'SetupVersion.cs'
[IO.File]::WriteAllText($setupVersion, "[assembly: System.Reflection.AssemblyVersion(`"$version.0`")]`n[assembly: System.Reflection.AssemblyFileVersion(`"$version.0`")]`n")
$inst = Join-Path $root 'installer'
Invoke-Csc @(
    "/out:$(Join-Path $dist 'ZapretLauncherSetup.exe')", "/win32icon:$ico", "/win32manifest:$(Join-Path $inst 'app.manifest')",
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll', '/r:Microsoft.CSharp.dll', '/r:System.Core.dll',
    "/resource:$launcherExe,ZapretLauncher.exe", "/resource:$logo,logo.png",
    (Join-Path $inst 'Setup.cs'), $setupVersion
)
Get-ChildItem $dist -Filter *.exe | Select-Object Name, Length, @{ n = 'Version'; e = { $_.VersionInfo.FileVersion } }
