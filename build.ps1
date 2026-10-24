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
    $font = New-Object Drawing.Font('Segoe MDL2 Assets', [single]($Size * 0.5), [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    $f = New-Object Drawing.StringFormat; $f.Alignment = 'Center'; $f.LineAlignment = 'Center'
    $g.DrawString([string][char]0xEA18, $font, [Drawing.Brushes]::White, $rect, $f)
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

# Лаунчер
$app = Join-Path $root 'app'
$launcherExe = Join-Path $dist 'ZapretLauncher.exe'
Invoke-Csc @(
    "/out:$launcherExe", "/win32icon:$ico", "/win32manifest:$(Join-Path $app 'app.manifest')",
    "/lib:$wpf", '/r:PresentationFramework.dll', '/r:PresentationCore.dll', '/r:WindowsBase.dll', '/r:System.Xaml.dll',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Management.dll', '/r:System.ServiceProcess.dll',
    '/r:System.Net.Http.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
    '/r:System.Web.Extensions.dll', '/r:Microsoft.CSharp.dll', '/r:System.Core.dll',
    "/resource:$(Join-Path $app 'ui.xaml'),ui.xaml", "/resource:$(Join-Path $app 'styles.xaml'),styles.xaml", "/resource:$(Join-Path $app 'flyout.xaml'),flyout.xaml",
    (Join-Path $app '*.cs')
)

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
