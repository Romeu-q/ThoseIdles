# Copia as peças de interface compartilhadas (extensions/shared/ti-ui.js) para cada extensão do
# Chrome que as usa. O Chrome só carrega arquivos de dentro da pasta da própria extensão, por isso
# cada uma leva uma cópia; o app usa o original (embutido no .exe).
# Rode depois de mexer em ti-ui.js:  powershell -File tools\sync-extensions.ps1
$root = Split-Path $PSScriptRoot -Parent
$fonte = Join-Path $root 'extensions\shared\ti-ui.js'
foreach ($ext in 'rmt-alert', 'sniper', 'price-calc') {
    $destino = Join-Path $root "extensions\$ext\ti-ui.js"
    Copy-Item $fonte $destino -Force
    Write-Output "copiado para extensions\$ext"
}
