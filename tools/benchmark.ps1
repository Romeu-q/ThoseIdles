<#
.SYNOPSIS
  Benchmark: Chrome (cenário A) x ThoseIdles (cenário B), medidos com o MESMO método (MemBench).

.DESCRIPTION
  Modo "isolado" (padrão): usa perfis TEMPORÁRIOS — não toca no seu Chrome nem nas suas contas.
  As páginas ficam na tela de login (sem sessão), igual nos dois cenários. Serve para comparar a
  sobrecarga do navegador em condições idênticas e reproduzíveis.

    A1 = 4 instâncias independentes do Chrome (--user-data-dir separado para cada)
    A2 = 1 Chrome com 4 perfis (mesma pasta de dados, --profile-directory diferente)
    B  = ThoseIdles (1 ambiente WebView2, 4 perfis)

  Para o benchmark com as SUAS contas logadas, veja a seção "Benchmark real" do README
  (MemBench sample --target chrome / --target app com tudo aberto e jogando).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\benchmark.ps1 -Duration 600
#>
param(
    [int]$Duration = 300,
    [int]$Interval = 5,
    [string]$Url = 'https://pokeidle.io/app',
    [string]$App = (Join-Path $PSScriptRoot '..\src\ThoseIdles\bin\Release\net10.0-windows\ThoseIdles.exe'),
    [string]$MemBench = (Join-Path $PSScriptRoot 'MemBench\bin\Release\net10.0-windows\MemBench.exe'),
    [string]$Chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    [string]$OutDir = (Join-Path $PSScriptRoot '..\benchmark-results'),
    [string]$Prefix = '',
    [ValidateSet('A1', 'A2', 'B')][string[]]$Scenarios = @('A1', 'A2', 'B')
)
$ErrorActionPreference = 'Stop'
$App = (Resolve-Path $App).Path
$MemBench = (Resolve-Path $MemBench).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$tmp = Join-Path $env:TEMP ("pim-bench-" + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Force $tmp | Out-Null
$common = '--no-first-run --no-default-browser-check --disable-sync --new-window'

function Stop-Tree([string]$marker) {
    # Encerra só os processos cuja linha de comando contém a pasta temporária deste benchmark.
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$marker*" } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch {} }
}

function Invoke-Scenario([string]$name, [string]$exe, [string[]]$argSets, [string]$marker) {
    Write-Host "== Cenário $name ==" -ForegroundColor Cyan
    # Argumentos vão por arquivo (uma linha por processo): o PowerShell 5.1 corrompe aspas internas
    # ao repassar argumentos para executáveis nativos.
    $argsFile = Join-Path $tmp "$name.args.txt"
    [System.IO.File]::WriteAllLines($argsFile, [string[]]$argSets)
    & $MemBench launch --target launched --exe $exe --args-file $argsFile --duration $Duration --interval $Interval `
        --label "$Prefix$name" --out (Join-Path $OutDir "$Prefix$name.csv") --detail
    if ($name -eq 'B') {
        # Fechamento normal do app (ele encerra o WebView2 sozinho).
        Get-Process ThoseIdles -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | ForEach-Object { [void]$_.CloseMainWindow() }
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Process ThoseIdles -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    }
    Stop-Tree $marker
}

if ($Scenarios -contains 'A1') {
    $sets = 1..4 | ForEach-Object { "--user-data-dir=`"$tmp\A1-$_`" $common $Url" }
    Invoke-Scenario 'A1' $Chrome $sets "$tmp\A1-"
}
if ($Scenarios -contains 'A2') {
    $sets = 1..4 | ForEach-Object { "--user-data-dir=`"$tmp\A2`" --profile-directory=`"Profile $_`" $common $Url" }
    Invoke-Scenario 'A2' $Chrome $sets "$tmp\A2"
}
if ($Scenarios -contains 'B') {
    # O app abre a mesma URL dos cenários do Chrome (pasta de dados temporária com settings.json próprio).
    New-Item -ItemType Directory -Force "$tmp\B" | Out-Null
    $hostName = ([Uri]$Url).Host
    $settings = @{ startUrl = $Url; startAllOnLaunch = $true; allowedHosts = @('pokeidle.io', '*.pokeidle.io', 'challenges.cloudflare.com', $hostName) }
    $settings | ConvertTo-Json | Set-Content -Encoding UTF8 "$tmp\B\settings.json"
    Invoke-Scenario 'B' $App @("--data-root `"$tmp\B`"") "$tmp\B"
}

Write-Host "`n== Comparações ==" -ForegroundColor Cyan
if (($Scenarios -contains 'A1') -and ($Scenarios -contains 'B')) { & $MemBench compare (Join-Path $OutDir "${Prefix}A1.csv") (Join-Path $OutDir "${Prefix}B.csv") }
if (($Scenarios -contains 'A2') -and ($Scenarios -contains 'B')) { & $MemBench compare (Join-Path $OutDir "${Prefix}A2.csv") (Join-Path $OutDir "${Prefix}B.csv") }
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
