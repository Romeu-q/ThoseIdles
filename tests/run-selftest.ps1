<#
.SYNOPSIS
  Autoteste de ponta a ponta do ThoseIdles com WebView2 real.

.DESCRIPTION
  Usa uma pasta de dados TEMPORÁRIA (nunca os perfis reais) e uma página local que imita o jogo
  (HTTP + WebSocket em localhost). Duas execuções do app:
    1) fase "run":    abre 4 contas, testa isolamento, reinício/fechamento/limpeza isolados,
                      execução em modo foco e com a janela minimizada, memória por conta;
    2) checagem de processos órfãos depois que o app fechou;
    3) fase "verify": reabre o app e confirma que as sessões persistiram.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests\run-selftest.ps1
  powershell -ExecutionPolicy Bypass -File tests\run-selftest.ps1 -Exe .\publish\ThoseIdles.exe
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\ThoseIdles\bin\Debug\net10.0-windows\ThoseIdles.exe"),
    [switch]$KeepData
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$root = Join-Path $env:TEMP ("ThoseIdles-selftest-" + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Force $root | Out-Null

$tcp = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $tcp.Start()
$port = $tcp.LocalEndpoint.Port; $tcp.Stop()

function Invoke-Phase([string]$flag, [string]$json) {
    $p = Start-Process -FilePath $Exe -ArgumentList @('--data-root', "`"$root`"", $flag, "`"$json`"", '--selftest-port', $port) -PassThru
    if (-not $p.WaitForExit(300000)) { Stop-Process -Id $p.Id -Force; throw "fase $flag excedeu 5 minutos" }
    return $p.ExitCode
}

function Show-Result([string]$json) {
    $r = Get-Content $json -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($t in $r.results) {
        $tag = if ($t.passed) { '[OK]    ' } else { '[FALHOU]' }
        Write-Host ("{0} {1} ({2}s)" -f $tag, $t.name, $t.seconds) -ForegroundColor $(if ($t.passed) { 'Green' } else { 'Red' })
        Write-Host ("         " + $t.detail)
    }
    return [bool]$r.passed
}

Write-Host "Pasta de dados do teste: $root"
Write-Host "== Fase 1: execução ==" -ForegroundColor Cyan
$code1 = Invoke-Phase '--selftest' (Join-Path $root 'run.json')
$ok1 = Show-Result (Join-Path $root 'run.json')

Write-Host "== Processos órfãos após fechar o app ==" -ForegroundColor Cyan
Start-Sleep -Seconds 2
$orphans = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$root*" })
if ($orphans.Count -eq 0) { Write-Host "[OK]     nenhum msedgewebview2.exe desta pasta de dados ficou rodando" -ForegroundColor Green; $ok2 = $true }
else { Write-Host "[FALHOU] $($orphans.Count) processo(s) órfão(s): $($orphans.ProcessId -join ', ')" -ForegroundColor Red; $ok2 = $false }

Write-Host "== Fase 2: persistência após reiniciar o app ==" -ForegroundColor Cyan
$code3 = Invoke-Phase '--selftest-verify' (Join-Path $root 'verify.json')
$ok3 = Show-Result (Join-Path $root 'verify.json')

$all = $ok1 -and $ok2 -and $ok3 -and $code1 -eq 0 -and $code3 -eq 0
Write-Host ""
Write-Host ("RESULTADO GERAL: " + $(if ($all) { 'APROVADO' } else { 'REPROVADO' })) -ForegroundColor $(if ($all) { 'Green' } else { 'Red' })
Write-Host "Logs: $root\logs\app.log"
if (-not $KeepData -and $all) { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue }
exit $(if ($all) { 0 } else { 1 })
