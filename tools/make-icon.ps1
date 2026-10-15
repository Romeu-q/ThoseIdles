<#
.SYNOPSIS
  Gera o ícone do app (pokébola) em src/ThoseIdles/Assets/pokeball.ico, com vários tamanhos.
  Desenhado do zero com System.Drawing; cada tamanho é renderizado separadamente para ficar nítido.
#>
param([string]$Out = (Join-Path $PSScriptRoot '..\src\ThoseIdles\Assets\pokeball.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Pokeball([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Contorno proporcionalmente mais grosso nos tamanhos pequenos, para continuar legível.
    $stroke = [Math]::Max(1.2, $size * $(if ($size -le 24) { 0.085 } else { 0.06 }))
    $m = $stroke / 2 + [Math]::Max(0.5, $size * 0.02)
    $d = $size - 2 * $m
    $rect = New-Object System.Drawing.RectangleF $m, $m, $d, $d

    $red = [System.Drawing.Color]::FromArgb(255, 0xE3, 0x35, 0x0D)
    $redDark = [System.Drawing.Color]::FromArgb(255, 0xB0, 0x22, 0x05)
    $white = [System.Drawing.Color]::FromArgb(255, 0xF7, 0xF7, 0xF7)
    $whiteShade = [System.Drawing.Color]::FromArgb(255, 0xD6, 0xD6, 0xDC)
    $black = [System.Drawing.Color]::FromArgb(255, 0x1E, 0x1E, 0x24)

    # Metade de cima (vermelha, com leve degradê) e de baixo (branca).
    $top = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $red, $redDark, 90.0
    $g.FillPie($top, $rect.X, $rect.Y, $rect.Width, $rect.Height, 180, 180)
    $bottom = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $white, $whiteShade, 90.0
    $g.FillPie($bottom, $rect.X, $rect.Y, $rect.Width, $rect.Height, 0, 180)

    # Faixa central.
    $c = $size / 2
    $band = [Math]::Max(1.5, $size * 0.075)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $black), $m, $c - $band / 2, $d, $band)

    # Contorno externo.
    $pen = New-Object System.Drawing.Pen $black, $stroke
    $g.DrawEllipse($pen, $rect)

    # Botão central: anel preto + miolo branco.
    $outerR = $size * 0.17
    $innerR = $size * $(if ($size -le 24) { 0.08 } else { 0.10 })
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $black), $c - $outerR, $c - $outerR, 2 * $outerR, 2 * $outerR)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $white), $c - $innerR, $c - $innerR, 2 * $innerR, 2 * $innerR)

    # Brilho no vermelho (só nos tamanhos maiores).
    if ($size -ge 48) {
        $shine = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 255, 255, 255))
        $g.FillEllipse($shine, $m + $d * 0.20, $m + $d * 0.13, $d * 0.20, $d * 0.13)
    }
    $g.Dispose()
    return $bmp
}

# Do maior para o menor: o Windows escolhe pelo tamanho, mas o <Image> do WPF usa o PRIMEIRO quadro.
$sizes = 256, 128, 64, 48, 40, 32, 24, 20, 16
$images = foreach ($s in $sizes) {
    $bmp = New-Pokeball $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# Formato ICO com imagens PNG embutidas (aceito pelo Windows Vista+ e pelo compilador .NET).
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s })); $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s }))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Dispose()

# Prévia lado a lado para conferência.
$preview = New-Object System.Drawing.Bitmap 650, 270
$pg = [System.Drawing.Graphics]::FromImage($preview)
$pg.Clear([System.Drawing.Color]::FromArgb(255, 0x12, 0x12, 0x16))
$x = 10
foreach ($s in 256, 128, 64, 48, 32, 24, 16) {
    $b = New-Pokeball $s
    $pg.DrawImage($b, $x, 10, $s, $s)
    $x += $s + 8
    $b.Dispose()
}
$pg.Dispose()
$previewPath = [System.IO.Path]::ChangeExtension($Out, '.preview.png')
$preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
"ícone: $Out ($((Get-Item $Out).Length) bytes)"
"prévia: $previewPath"
