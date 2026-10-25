# Generates Inno Setup wizard bitmaps from the app icon named by SetupIconFile, and wires them into the .iss.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repoRoot = Split-Path $PSScriptRoot -Parent
$iss = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'installer') -Filter *.iss | Select-Object -First 1
$installerDir = $iss.DirectoryName
$iconLine = Select-String -LiteralPath $iss.FullName -Pattern '^SetupIconFile=(.+)$' | Select-Object -First 1
$iconPath = [IO.Path]::GetFullPath((Join-Path $installerDir $iconLine.Matches[0].Groups[1].Value.Trim()))

# Load the largest icon frame; PNG frames load directly, BMP frames through System.Drawing.Icon.
$bytes = [IO.File]::ReadAllBytes($iconPath)
$best = $null
for ($i = 0; $i -lt [BitConverter]::ToUInt16($bytes, 4); $i++) {
    $entry = 6 + 16 * $i
    $size = if ($bytes[$entry] -eq 0) { 256 } else { $bytes[$entry] }
    if (!$best -or $size -gt $best.Size) {
        $best = @{ Size = $size; Length = [BitConverter]::ToUInt32($bytes, $entry + 8); Offset = [BitConverter]::ToUInt32($bytes, $entry + 12) }
    }
}
if ($bytes[$best.Offset] -eq 0x89) {
    $stream = New-Object IO.MemoryStream(, $bytes[$best.Offset..($best.Offset + $best.Length - 1)])
    $logo = New-Object Drawing.Bitmap($stream)
} else {
    $logo = (New-Object Drawing.Icon($iconPath, $best.Size, $best.Size)).ToBitmap()
}

# Tint the large panel with the icon's average opaque colour so each app keeps its own identity.
$r = $g = $b = $n = 0
for ($y = 0; $y -lt $logo.Height; $y += 4) {
    for ($x = 0; $x -lt $logo.Width; $x += 4) {
        $c = $logo.GetPixel($x, $y)
        if ($c.A -gt 200) { $r += $c.R; $g += $c.G; $b += $c.B; $n++ }
    }
}
$tint = { param($v) [int][Math]::Round(255 - (255 - $v / [Math]::Max($n, 1)) * 0.08) }
$panel = [Drawing.Color]::FromArgb((& $tint $r), (& $tint $g), (& $tint $b))

function Save-Wizard([int]$width, [int]$height, [double]$logoSize, [double]$top, [Drawing.Color]$background, [string]$path) {
    $bitmap = New-Object Drawing.Bitmap($width, $height, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear($background)
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($logo, [single](($width - $logoSize) / 2), [single]$top, [single]$logoSize, [single]$logoSize)
    $graphics.Dispose()
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Bmp)
    $bitmap.Dispose()
}

# Inno Setup's recommended sizes for 100% to 250% display scaling.
$large = @(@(164, 314), @(192, 386), @(246, 459), @(273, 556), @(328, 604), @(355, 700), @(410, 797))
$small = @(@(55, 55), @(64, 68), @(83, 80), @(92, 97), @(110, 106), @(119, 123), @(138, 140))
$images = Join-Path $installerDir 'images'
New-Item -ItemType Directory -Force -Path $images | Out-Null
foreach ($s in $large) {
    $size = $s[0] * 0.56
    Save-Wizard $s[0] $s[1] $size ($s[1] * 0.40 - $size / 2) $panel (Join-Path $images "wizard-$($s[0])x$($s[1]).bmp")
}
foreach ($s in $small) {
    $size = [Math]::Min($s[0], $s[1]) * 0.86
    Save-Wizard $s[0] $s[1] $size (($s[1] - $size) / 2) ([Drawing.Color]::White) (Join-Path $images "wizard-small-$($s[0])x$($s[1]).bmp")
}
$logo.Dispose()

$lines = @(
    ('WizardImageFile=' + (($large | ForEach-Object { "images\wizard-$($_[0])x$($_[1]).bmp" }) -join ',')),
    ('WizardSmallImageFile=' + (($small | ForEach-Object { "images\wizard-small-$($_[0])x$($_[1]).bmp" }) -join ','))
)
$text = [IO.File]::ReadAllText($iss.FullName)
$text = [regex]::Replace($text, '(?m)^Wizard(Small)?ImageFile=.*\r?\n', '')
$newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$text = [regex]::Replace($text, '(?m)^(WizardStyle=.*?)\r?$', "`$1$newline$($lines -join $newline)")
[IO.File]::WriteAllText($iss.FullName, $text, (New-Object Text.UTF8Encoding $false))
Write-Host "Wrote wizard images to $images from $iconPath"
