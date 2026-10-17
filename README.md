# ThoseIdles

Gerenciador de **4 contas** do [PokeIdle.io](https://pokeidle.io/) em uma janela sÃ³, com Microsoft Edge
WebView2, em C#/.NET 10 + WPF. Cada conta tem sessÃ£o prÃ³pria e persistente, e as quatro continuam
rodando ao mesmo tempo, inclusive quando sÃ³ uma estÃ¡ em destaque ou quando a janela estÃ¡ minimizada.

> Ã‰ um gerenciador de sessÃµes de navegador. NÃ£o automatiza jogadas, nÃ£o altera requisiÃ§Ãµes e nÃ£o
> contorna proteÃ§Ãµes do servidor.

---

## 1. Arquitetura e por que ela economiza memÃ³ria

```
ThoseIdles.exe (WPF, MVVM)
â””â”€â”€ 1 CoreWebView2Environment  (pasta de dados Ãºnica)
    â”œâ”€â”€ msedgewebview2.exe  navegador     â”
    â”œâ”€â”€ msedgewebview2.exe  GPU           â”‚ compartilhados pelas 4 contas
    â”œâ”€â”€ msedgewebview2.exe  rede/storage  â”‚ (1 cÃ³pia de cada)
    â”œâ”€â”€ msedgewebview2.exe  crashpad      â”˜
    â”œâ”€â”€ perfil "conta1" â”€â”€ renderer(s) da conta 1   â”
    â”œâ”€â”€ perfil "conta2" â”€â”€ renderer(s) da conta 2   â”‚ isolados: cookies, Local Storage,
    â”œâ”€â”€ perfil "conta3" â”€â”€ renderer(s) da conta 3   â”‚ IndexedDB, cache e service workers
    â””â”€â”€ perfil "conta4" â”€â”€ renderer(s) da conta 4   â”˜ em pastas separadas
```

**Um ambiente com quatro perfis nomeados** (`CoreWebView2ControllerOptions.ProfileName`). Esta Ã© a
decisÃ£o central:

| OpÃ§Ã£o | Processos de navegador/GPU/rede | Isolamento de sessÃ£o | Custo |
|---|---|---|---|
| 4 Chromes com `--user-data-dir` prÃ³prio | 4 Ã¡rvores completas | sim | alto |
| 4 ambientes WebView2 (4 pastas de dados) | 4 Ã¡rvores completas | sim | â‰ˆ igual ao anterior |
| 4 WebView2 no mesmo ambiente **sem** perfil | 1 Ã¡rvore | **nÃ£o** (sessÃ£o compartilhada) | inaceitÃ¡vel |
| **1 ambiente + 4 perfis (escolhida)** | **1 Ã¡rvore** | **sim** | **menor** |

- Perfis diferentes **nunca** compartilham renderer: o Chromium separa as *StoragePartitions* por
  perfil. Compartilhar processos aqui nÃ£o significa compartilhar sessÃ£o; o autoteste comprova isso.
- O nome do perfil vem do slot (`conta1`â€¦`conta4`), nÃ£o do nome exibido. **Renomear nÃ£o muda a sessÃ£o.**
- No disco: `%LOCALAPPDATA%\ThoseIdles\WebView2\EBWebView\WV2Profile_conta1` â€¦ `_conta4`.

**Outras economias aplicadas** (nenhuma desliga GPU, JavaScript, armazenamento ou rede):

- O WebView2 nÃ£o carrega as extensÃµes internas que o Chrome sobe por perfil. No benchmark, o
  Chrome de teste com 4 perfis abriu 8 renderers de extensÃ£o.
- Sem sincronizaÃ§Ã£o de conta Google, sem autofill/salvamento de senha
  (`IsPasswordAutosaveEnabled=false`, `IsGeneralAutofillEnabled=false`). ExtensÃµes ficam desligadas
  no ambiente, a menos que vocÃª ative a /hunt (seÃ§Ã£o 8.1).
- A interface WPF Ã© renderizada por software (configurÃ¡vel), entÃ£o o processo do app nÃ£o carrega
  Direct3D. Os jogos continuam usando a GPU normalmente, no processo de GPU do WebView2.
- GC de estaÃ§Ã£o sem thread concorrente no processo do app.
- O monitor de conexÃ£o usa `Network.enable` com buffers de resposta **zerados**
  (`maxTotalBufferSize: 0`), para o DevTools nÃ£o reter corpos de resposta.
- Ao fechar ou reiniciar uma conta, o controller Ã© descartado (`WebView2.Dispose()`) e o renderer
  dela termina. Os eventos sÃ£o desinscritos antes do descarte.
- Um Job Object (`KILL_ON_JOB_CLOSE`) garante que nenhum `msedgewebview2.exe` fique Ã³rfÃ£o, mesmo
  se o app for finalizado Ã  forÃ§a.
- **Opcional e desligado por padrÃ£o:** `MemoryUsageTargetLevel.Low` nas miniaturas. Ele nÃ£o pausa
  scripts, mas pode descartar caches. MeÃ§a antes de deixar ligado.

**Rejeitado de propÃ³sito:** desligar o isolamento de sites (`--disable-site-isolation-trials`)
pouparia alguns renderers de iframes de terceiros, mas enfraquece a seguranÃ§a. Limitar o heap do V8
(`--js-flags=--max-old-space-size`) pode derrubar o jogo. Ocultar ou suspender contas fora de vista
viola o requisito de execuÃ§Ã£o contÃ­nua.

## 2. As 4 contas continuam rodando (requisito 3.3)

Sem configuraÃ§Ã£o, o Chromium desacelera pÃ¡ginas "ocultas": timers limitados a 1/s, depois 1/min apÃ³s
5 minutos (*intensive wake-up throttling*), renderer com prioridade baixa e janelas cobertas tratadas
como ocultas. O app impede isso de duas formas:

1. **Os painÃ©is nunca sÃ£o ocultados nem tirados da Ã¡rvore visual.** O modo "uma conta" sÃ³ muda
   retÃ¢ngulos: a conta escolhida fica grande e as outras trÃªs viram **miniaturas vivas** Ã  direita,
   com zoom reduzido para manter o layout desktop da pÃ¡gina.
2. **Flags do ambiente** (`ProfileManager.BrowserArguments`):
   `--disable-background-timer-throttling --disable-backgrounding-occluded-windows
   --disable-renderer-backgrounding --disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling`.

O app nÃ£o usa `TrySuspendAsync`, nÃ£o descarta abas e nÃ£o congela timers.

## 3. Indicador de estado

O app **nÃ£o** marca "Conectada" sÃ³ porque a pÃ¡gina abriu. O jogo usa um WebSocket em
`wss://pokeidle.io`, e o app observa (somente leitura, via DevTools Protocol) os eventos de criaÃ§Ã£o,
handshake e fechamento desse socket. O conteÃºdo das mensagens nÃ£o Ã© lido.

| Estado | Significado |
|---|---|
| Carregando | navegaÃ§Ã£o em andamento |
| Sem conexÃ£o do jogo | pÃ¡gina aberta, nenhum WebSocket do jogo aberto (ex.: tela de login) |
| **Conectada** | WebSocket do servidor do jogo aberto (handshake HTTP 101 confirmado) |
| Reconectando | estava conectada e o socket caiu; o prÃ³prio jogo tenta reconectar |
| Offline | o Windows estÃ¡ sem rede |
| Erro | falha de carregamento, processo da pÃ¡gina travou ou caiu (com nova tentativa automÃ¡tica) |
| Fechada | instÃ¢ncia encerrada pelo usuÃ¡rio |

Se o monitor for desligado nas configuraÃ§Ãµes, o estado apÃ³s carregar passa a ser "Carregada", sem
confirmaÃ§Ã£o de conexÃ£o.

## 4. RecuperaÃ§Ã£o de falhas

- **Falha ao carregar / site fora do ar (HTTP 5xx, rede):** novas tentativas com espera crescente
  (5 s, 15 s, 30 s, 1 min, 2 min, 5 minâ€¦), sÃ³ naquela conta. HTTP 4xx (ex.: desafio ou bloqueio do
  Cloudflare, 429) **nÃ£o** entra em loop; o botÃ£o "Tentar agora" fica na faixa da conta.
- **Renderer caiu:** recarrega sÃ³ aquela conta.
- **Renderer travado:** espera 30 s e testa com um script trivial; se continuar travado, reinicia sÃ³
  aquela instÃ¢ncia.
- **Processo do navegador caiu:** recria o ambiente e reabre as contas que estavam abertas.
- **Rede do Windows voltou:** contas em erro tentam de novo na hora.
- O app nunca tenta logar sozinho e nunca guarda senha. O login Ã© o que o prÃ³prio site guardou no
  perfil (cookies/Local Storage).

## 5. SeguranÃ§a

- Nenhuma ponte pÃ¡gina â†’ app: `AreHostObjectsAllowed=false`, `IsWebMessageEnabled=false`. A pÃ¡gina nÃ£o
  consegue pedir ao app para executar nada.
- NavegaÃ§Ã£o do frame principal validada (`NavigationPolicy`): sÃ³ HTTPS, sÃ³ hosts permitidos
  (`pokeidle.io` e os provedores de login Google/Discord/Twitch). Redirecionamentos de servidor iniciados
  num host permitido sÃ£o aceitos. Links externos clicados pelo jogador abrem no navegador padrÃ£o
  (sÃ³ http/https). Esquemas externos (`mailto:`, apps) sÃ£o bloqueados.
- Pop-ups (`window.open`) nunca viram janelas dentro do app. VÃ£o para o navegador padrÃ£o, com limite
  de frequÃªncia.
- PermissÃµes (cÃ¢mera, microfone, localizaÃ§Ã£o, notificaÃ§Ãµes, Ã¡rea de transferÃªnciaâ€¦) sÃ£o **negadas**.
  Downloads sÃ£o bloqueados.
- DevTools sÃ³ com "Modo desenvolvedor" ligado. **Sem porta de depuraÃ§Ã£o remota.**
- Logs: toda URL perde query string e fragmento (`?sessao=â€¦`, tokens de OAuth). Cookies, cabeÃ§alhos e
  corpos nunca sÃ£o registrados. RotaÃ§Ã£o em 1 MB Ã— 4 arquivos.
- Limpar dados de uma conta usa `CoreWebView2Profile.ClearBrowsingDataAsync()` **naquele perfil**.
  Antes, a pÃ¡gina vai para `about:blank`, para o jogo em memÃ³ria nÃ£o regravar o token.
- Uma instÃ¢ncia por pasta de dados (mutex) e `ExclusiveUserDataFolderAccess=true`.

## 6. Requisitos

- Windows 10 (1809+) ou 11, x64.
- **Microsoft Edge WebView2 Runtime (Evergreen).** JÃ¡ vem no Windows 11 e na maioria dos Windows 10
  com Edge atualizado. Se faltar, o app avisa ao abrir. InstalaÃ§Ã£o:
  <https://developer.microsoft.com/microsoft-edge/webview2/> â†’ "Evergreen Bootstrapper".
  O executÃ¡vel **nÃ£o Ã© autossuficiente** quanto a isso: o WebView2 Runtime Ã© um componente externo,
  compartilhado e atualizado pelo Windows.
- Para compilar: .NET SDK 10 (LTS). Testado com SDK 10.0.302, WebView2 SDK 1.0.4258.31 e Runtime 155.0.4283.45.

## 7. Compilar e gerar o `.exe`

```bash
dotnet build ThoseIdles.slnx -c Release
```

**ExecutÃ¡vel Ãºnico, sem precisar instalar o .NET** (recomendado):

```bash
dotnet publish src/ThoseIdles/ThoseIdles.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

Resultado: `publish/win-x64/ThoseIdles.exe` (~127 MB, sem compressÃ£o de propÃ³sito; veja a seÃ§Ã£o 10).
Ele ainda depende do WebView2 Runtime (seÃ§Ã£o 6).

**ExecutÃ¡vel pequeno, dependente do .NET 10 Desktop Runtime instalado:**

```bash
dotnet publish src/ThoseIdles/ThoseIdles.csproj -c Release -r win-x64 --self-contained false -o publish/fd
```

**Ferramenta de benchmark:**

```bash
dotnet publish tools/MemBench/MemBench.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/membench
```

## 8. Uso

- **Barra superior:** estado geral (`n/4 conectadas`), RAM total aproximada (passe o mouse para ver o
  detalhamento: app, compartilhado, cada conta), "Uma conta"/"Grade 2x2", Abrir/Fechar/Reiniciar todas
  e âš™ configuraÃ§Ãµes.
- **Cada painel:** status, zoom âˆ’/+ e porcentagem (clique volta a 100%), memÃ³ria dos renderers daquela
  conta, renomear âœŽ, limpar dados ðŸ—‘ (com confirmaÃ§Ã£o na prÃ³pria barra), DevTools `</>` (modo
  desenvolvedor), recarregar, reiniciar sÃ³ aquela instÃ¢ncia, fechar/abrir e maximizar.
- **Tamanho de cada tela (arrastar):** na grade 2x2 hÃ¡ uma divisÃ³ria entre as linhas e uma divisÃ³ria
  **em cada linha**, independentes entre si. Assim cada conta pode ter largura e altura prÃ³prias. No
  modo "uma conta", a divisÃ³ria ajusta a largura da coluna de miniaturas. Duplo clique numa divisÃ³ria
  volta ao meio. As posiÃ§Ãµes ficam salvas. Arrastar sÃ³ muda o tamanho: nenhuma conta recarrega.
- **Zoom de cada conta:** botÃµes âˆ’/+ no cabeÃ§alho (passos de 10%) ou Ctrl + roda do mouse na pÃ¡gina.
  Fica salvo por conta. "Zoom de todas as contas", nas configuraÃ§Ãµes, ajusta as 4 de uma vez.
- **ConfiguraÃ§Ãµes:** abrir as 4 ao iniciar, monitor de conexÃ£o, zoom da grade, miniaturas com zoom,
  memÃ³ria baixa nas miniaturas (experimental), UI por software, CSV de memÃ³ria, intervalo de mediÃ§Ã£o,
  exportar snapshot por processo, abrir pastas de logs e dados.

Dados em `%LOCALAPPDATA%\ThoseIdles\`. O app se chamava "PokeIdle Multi": se existir a pasta antiga
`%LOCALAPPDATA%\PokeIdleMulti`, ela Ã© movida para a nova na primeira abertura, com logins,
configuraÃ§Ãµes e extensÃ£o.

| Caminho | ConteÃºdo |
|---|---|
| `settings.json` | preferÃªncias (nomes, layout, janela, opÃ§Ãµes, `allowedHosts`) |
| `WebView2\` | os 4 perfis (sessÃµes) |
| `logs\app.log` | log com rotaÃ§Ã£o |
| `benchmark\` | `startup-*.csv` (linha do tempo), `memoria-app-*.csv`, `snapshot-*.csv` |

`allowedHosts` pode ser editado no `settings.json` (com o app fechado), caso o site passe a usar outro
provedor de login.

### 8.1 ExtensÃ£o "/hunt for PokeIdle"

A [/hunt for PokeIdle](https://chromewebstore.google.com/detail/hunt-for-pokeidle/mhnplloljgchpfpcpbhmcokmnefodjnh)
Ã© um guia de hunts que o prÃ³prio jogo recomenda nos comandos `/guia` e `/hunt`. Ela desenha um painel
sobre o seu PokÃ©mon com as melhores hunts por XP/h e gold/h. O app pode carregÃ¡-la em cada conta.

**Como usar:** âš™ ConfiguraÃ§Ãµes â†’ **Baixar / atualizar**. O app baixa a versÃ£o atual da loja, valida e
liga a extensÃ£o (as contas abertas sÃ£o reabertas, continuam logadas). Em cada painel aparece o botÃ£o ðŸ”
para ligar ou desligar a extensÃ£o sÃ³ naquela conta; ele recarrega sÃ³ aquela conta. A caixa "Usar nas
contas" desliga a extensÃ£o em todas.

**O que foi verificado no cÃ³digo dela (v1.0.2):**

- SÃ³ tem content scripts (MV3, `world: MAIN`) que rodam sÃ³ em `https://pokeidle.io/*`. NÃ£o tem service
  worker, popup nem permissÃµes.
- O `hook.js` sÃ³ **acrescenta um ouvinte** Ã s mensagens do WebSocket do jogo. NÃ£o envia, nÃ£o altera e
  nÃ£o clica em nada: nÃ£o Ã© automaÃ§Ã£o.
- Os `fetch`/`import` buscam sÃ³ mÃ³dulos e JSONs do prÃ³prio pokeidle.io (as fÃ³rmulas do jogo). Nada vai
  para outro servidor.
- Grava sÃ³ no `localStorage` do pokeidle.io, que no app Ã© separado por conta.

**ProteÃ§Ãµes do app:**

- A extensÃ£o **nÃ£o vem junto com o app** (nÃ£o tem licenÃ§a declarada). Ã‰ baixada do servidor de
  atualizaÃ§Ã£o do Google quando vocÃª clica no botÃ£o.
- Antes de instalar, o manifesto Ã© validado: sÃ³ sÃ£o aceitos content scripts em `https://pokeidle.io/*`,
  sem `permissions`, `host_permissions`, `background`, popup ou outras pÃ¡ginas, e sem caminhos com `..`.
  Se uma versÃ£o futura pedir mais do que isso, **ela Ã© recusada** e o motivo aparece na tela.
- Com a extensÃ£o desligada, o ambiente WebView2 Ã© criado com `AreBrowserExtensionsEnabled=false`.

**AtualizaÃ§Ã£o automÃ¡tica** (opÃ§Ã£o "Atualizar automaticamente", ligada por padrÃ£o). O WebView2 nÃ£o
atualiza extensÃµes sozinho como o Chrome, entÃ£o o app faz isso:

- Consulta a loja ao abrir (atÃ© 8 s, antes de abrir as contas) e depois a cada 12 h, pelo mesmo
  protocolo de atualizaÃ§Ã£o que o Chrome usa.
- Se houver versÃ£o nova, baixa e confere o **tamanho e o hash SHA-256** informados pela loja, depois
  valida o manifesto (mesmas regras acima).
- **Nenhuma conta Ã© recarregada sozinha.** Contas que abrirem depois jÃ¡ usam a versÃ£o nova. As contas
  abertas passam a usÃ¡-la quando vocÃª recarregar âŸ³ ou reabrir a conta. "Baixar / atualizar" aplica
  na hora em todas.
- Sem rede ou com a loja fora do ar, o app tenta de novo na prÃ³xima rodada, sem incomodar.

**Custo medido:** com as 4 contas no pokeidle.io (sem login), ligar a extensÃ£o nÃ£o criou nenhum
processo novo (15 â†’ 15) e mudou a memÃ³ria em +9 MB no total, dentro do ruÃ­do. Sem login ela sÃ³
instala o hook. Logada, ela carrega o catÃ¡logo do jogo, e o custo real sÃ³ dÃ¡ para medir com as suas
contas (o detalhamento por conta na barra superior mostra isso).

### 8.2 Live da Twitch (bÃ´nus de XP do jogo)

O PokeIdle dÃ¡ **+15% de XP** a quem assiste a uma live oficial **logado numa conta da Twitch
vinculada**, e +2,5% por live oficial a mais. O servidor do jogo confere uma vez por minuto se o seu
login da Twitch estÃ¡ **no chat** da live. Basta a aba da live estar aberta e logada; ela nÃ£o precisa
estar na tela.

**Como usar:**

1. Em cada conta, faÃ§a o "Vincular Twitch" do prÃ³prio jogo com a conta da Twitch daquela conta. O
   login fica no perfil da conta: cada conta pode ter uma Twitch diferente.
2. Na tela "Twitch" do jogo, clique nos canais. **Cada canal abre mais uma live** (o bÃ´nus soma por
   live), dentro daquela conta e em segundo plano: muda, com padrÃ£o de 160p, sem ocupar espaÃ§o. Clicar
   num canal que jÃ¡ estÃ¡ aberto nÃ£o abre de novo. SÃ£o atÃ© 10 lives por conta. Fora do app, o link
   abriria no seu navegador padrÃ£o, que tem outro login.
3. O botÃ£o ðŸ“º no cabeÃ§alho mostra **quantas lives** a conta tem e mostra ou esconde o painel. Escondidas,
   todas **continuam abertas**, como abas do Chrome em segundo plano. O painel tem uma aba por canal
   (clique para ver, Ã— para fechar e esquecer aquele canal), mudo/som e abrir no navegador para a live
   escolhida, esconder, e ðŸ—‘ para fechar todas.
4. As lives reabrem sozinhas quando a conta abre (opÃ§Ã£o "Reabrir a live com a conta").

**Como funciona:** Ã© um segundo WebView2 **no mesmo perfil da conta**, entÃ£o tem o mesmo login da
Twitch e outra conta nunca o vÃª. Escondida, a pÃ¡gina fica `hidden` para o navegador, como uma aba em
segundo plano. O vÃ­deo nem Ã© decodificado, mas o chat continua conectado, porque as flags
anti-throttling do app valem para ela tambÃ©m. Na aba escondida, o app tambÃ©m pede ao WebView2 meta de
memÃ³ria baixa.

> O app **nÃ£o** usa truques como abrir sÃ³ o chat sem a live, nem bloqueia anÃºncios ou partes da pÃ¡gina
> da Twitch: Ã© a mesma pÃ¡gina que vocÃª teria numa aba do Chrome.

**Custo medido** (live real da Twitch, canal lofigirl, 4 contas abertas numa pÃ¡gina neutra no lugar
do jogo, 45 s apÃ³s abrir):

| | MemÃ³ria total | Processos | CPU da mÃ¡quina |
|---|---|---|---|
| 4 contas sem live | 214 MB | 10 | 0,0% |
| live **escondida** em 1 conta | 777 MB (+563 MB) | 36 | 0,1% |
| live **escondida** nas 4 contas | 2,24 GB (+2,03 GB, â‰ˆ 520 MB por live) | 113 | 0,3% |
| para comparar: live **visÃ­vel** nas 4 contas | 2,71â€“2,99 GB (â‰ˆ 640â€“714 MB por live) | 105â€“120 | 5,8â€“7,5% |

**MemÃ³ria ao longo do tempo:** medindo uma sessÃ£o real (4 contas logadas + lives), as pÃ¡ginas do jogo
ficam estÃ¡veis (~330 MB cada), mas a live **cresce ~6 MB/min** porque o chat acumula mensagens. Por
isso o app **recarrega cada live a cada 60 min** (configurÃ¡vel: 30/60/120/240 min ou nunca, e hÃ¡ o
botÃ£o "Recarregar lives agora"). As lives recarregam uma por vez, com 20 s entre elas, continuam
escondidas, e o jogo nÃ£o Ã© tocado. O chat fica fora sÃ³ alguns segundos, e o jogo confere uma vez por
minuto.

**VÃ¡rias lives na mesma conta** (canais reais gaules, alanzoka, kaicenat e ibai, todos de chat grande,
abertos um por vez na conta 1, todos escondidos):

| Conta 1 comâ€¦ | MemÃ³ria a mais (total do app) | Processos | CPU da mÃ¡quina |
|---|---|---|---|
| 1 live | +585 MB | 35 | 0,3% |
| 2 lives | +781 MB (+196 MB) | 36 | 0,6% |
| 3 lives | +1,09 GB (+310 MB) | 37 | 1,1% |
| 4 lives | +1,28 GB (+190 MB) | 38 | 1,6% |

A **primeira** live de cada conta Ã© a cara (~0,5â€“0,6 GB): ela carrega o site da Twitch e seus
processos (anÃºncios, extensÃµes de canal). As **seguintes, na mesma conta,** custam ~0,2â€“0,3 GB cada,
porque as abas da Twitch do mesmo perfil dividem processos. Para o bÃ´nus acumulativo, empilhar lives
numa conta sai bem mais barato que espalhar.

**Modo leve (experimental, desligado por padrÃ£o):** em vez do site inteiro, uma pÃ¡gina mÃ­nima do app
com o player e o chat **oficiais incorporados** da Twitch (embed). Com a live visÃ­vel, mediu â‰ˆ 255 MB
por live, mas **nÃ£o estÃ¡ verificado**: o player incorporado nÃ£o chegou a decodificar vÃ­deo no teste, e
eu nÃ£o consegui confirmar se o chat incorporado reconhece o seu login. Sem login reconhecido, o jogo
nÃ£o detecta vocÃª. Se for testar, confira no chat se aparece o seu nome.

## 9. Testes

### Autoteste automÃ¡tico (WebView2 real, sem tocar nas suas contas)

```bash
powershell -ExecutionPolicy Bypass -File tests/run-selftest.ps1
```

O autoteste usa uma pasta de dados temporÃ¡ria e uma pÃ¡gina local que imita o jogo (HTTP + WebSocket em
`localhost`). Ele roda o app duas vezes e verifica processos Ã³rfÃ£os entre as execuÃ§Ãµes. Para testar o
`.exe` publicado: `-Exe publish\win-x64\ThoseIdles.exe`.

**Resultado (15/10/2026, Windows 10 22H2, WebView2 Runtime 155):** aprovado nos 38 itens, tanto no
build normal quanto no `.exe` publicado. Os testes de extensÃ£o usam uma extensÃ£o de teste com o mesmo
formato da /hunt (content script em `world: MAIN`, `document_start`), sÃ³ que para `localhost`:

| Teste | Resultado |
|---|---|
| PolÃ­tica de navegaÃ§Ã£o (8 URLs) + remoÃ§Ã£o de query string do log | OK |
| 4 contas abrem e chegam a **Conectada** (WebSocket detectado) | OK, em ~1 s |
| Um Ãºnico processo de navegador compartilhado pelas 4 contas | OK (mesmo `BrowserProcessId`) |
| Cada conta grava localStorage, cookie e IndexedDB | OK |
| Nenhuma conta enxerga dados de outra (JS + `CookieManager`) | OK |
| Perfis em pastas separadas (`WV2Profile_conta1â€¦4`) | OK |
| Reiniciar a conta 3 nÃ£o recarrega 1, 2 e 4; a 3 mantÃ©m os dados | OK |
| Fechar a conta 2 nÃ£o afeta as outras (timers continuam); ela reabre logada | OK |
| Limpar dados da conta 4 apaga sÃ³ a conta 4 | OK |
| Modo "uma conta": as 3 miniaturas seguem com timers a 10/s, `visible`, WebSocket aberto | OK (50/50 ticks em 5 s) |
| Janela **minimizada**: as 4 contas seguem com timers a 10/s e WebSocket aberto | OK (50/50 ticks em 5 s) |
| DivisÃ³rias: cada conta muda de tamanho de forma independente, sem recarregar | OK (ex.: 409Ã—495, 957Ã—495, 957Ã—253, 409Ã—253) |
| DivisÃ³ria do modo "uma conta" | OK |
| Zoom por conta independente (conta 2 a 150%, as outras inalteradas) | OK |
| BotÃµes âˆ’/+ de zoom ajustam sÃ³ a conta e salvam | OK |
| Live: canal da Twitch clicado no jogo abre na conta, em segundo plano, sem tirar o jogo da tela | OK |
| Live: mesmo perfil da conta, muda, padrÃ£o 160p | OK |
| Live: memÃ³ria separada da do jogo (renderer prÃ³prio) | OK |
| Live escondida fica `hidden` e continua rodando (timers a 10/s) | OK |
| Live: mostrar, esconder (sem recarregar) e fechar (esquece o canal) | OK |
| Live: reabrir pelo canal guardado | OK |
| VÃ¡rias lives acumuladas na mesma conta (todas `hidden` e rodando; fechar uma nÃ£o afeta as outras) | OK |
| ExtensÃ£o: manifesto real da /hunt aceito; 5 manifestos perigosos recusados | OK |
| ExtensÃ£o carregada em cada perfil, antes do script da pÃ¡gina | OK |
| Desligar a extensÃ£o sÃ³ na conta 2: some sÃ³ nela, 1/3/4 nÃ£o recarregam | OK |
| Desligar/ligar no app recria o ambiente e mantÃ©m as sessÃµes | OK (~2 s) |
| MemÃ³ria atribuÃ­da por conta (renderers distintos) | OK |
| Nenhum `msedgewebview2.exe` Ã³rfÃ£o depois de fechar | OK |
| Reabrir o app: contas 1â€“3 mantÃªm a sessÃ£o, a 4 continua limpa | OK |
| Reabrir o app: extensÃ£o continua ativa nas 4 | OK |
| Reabrir o app: divisÃ³rias e zoom de cada conta continuam iguais | OK |
| Reabrir o app: as lives guardadas da conta voltam, mudas e em segundo plano | OK |

**Teste on-line da extensÃ£o real** (baixa a /hunt da loja e abre o pokeidle.io de verdade, sem login):

```bash
src\ThoseIdles\bin\Debug\net10.0-windows\ThoseIdles.exe --data-root %TEMP%\pim-hunt --selftest-hunt %TEMP%\pim-hunt\hunt.json
```

Resultado: v1.0.2 baixada, com hash SHA-256 conferido e manifesto validado. A verificaÃ§Ã£o de
atualizaÃ§Ã£o respondeu "noupdate" para a versÃ£o instalada. Com a extensÃ£o ligada, o hook estÃ¡ ativo
nas 4 contas (`WebSocket = WebSocketObservado`), sem processos extras.

### Teste manual com as contas reais (sÃ³ vocÃª pode fazer)

1. Abra o app. Em cada painel, entre com uma conta diferente.
2. Confirme que cada painel mostra **Conectada** e o nick certo.
3. Feche o app e abra de novo: as 4 devem voltar logadas.
4. Reinicie a conta 2 (â†»): as outras nÃ£o podem recarregar.
5. Use "Uma conta" e, depois de alguns minutos, confira se as miniaturas seguiram progredindo.

## 10. Benchmark: Chrome Ã— ThoseIdles

### Como medir com as suas contas (o benchmark que importa)

As duas mediÃ§Ãµes devem ser feitas nas mesmas condiÃ§Ãµes: as 4 contas logadas e jogando, o mesmo tempo
de execuÃ§Ã£o, e nenhum outro programa pesado mudando entre uma e outra.

**CenÃ¡rio A â€” seus 4 Chromes:** abra as 4 contas como vocÃª faz hoje, espere estabilizar e rode:

```bash
publish/membench/MemBench.exe sample --target chrome --duration 1800 --interval 10 --label A-chrome --out A.csv
```

> `--target chrome` soma **todos** os `chrome.exe`. Feche as outras janelas/abas do Chrome antes, ou
> elas entram na conta.

**CenÃ¡rio B â€” o app:** feche o Chrome, abra o ThoseIdles com as 4 contas e rode:

```bash
publish/membench/MemBench.exe sample --target app --duration 1800 --interval 10 --label B-app --out B.csv
```

**Comparar:**

```bash
publish/membench/MemBench.exe compare A.csv B.csv
```

Leia a linha **"privada em RAM â€” 2Âª metade"**: Ã© a memÃ³ria que o Gerenciador de Tarefas mostra,
medida depois do aquecimento. O MemBench soma **todos** os processos auxiliares (renderers, GPU, rede,
crashpad), nunca sÃ³ o processo principal. Para tempo de inicializaÃ§Ã£o, `MemBench launch` mede atÃ© a
carga estabilizar, e o app grava a linha do tempo exata por conta em `benchmark\startup-*.csv`.

### MediÃ§Ã£o feita no ambiente de desenvolvimento (sem login)

Gerada por `tools/benchmark.ps1`, com perfis **temporÃ¡rios** (nÃ£o usa o seu Chrome nem as suas contas),
4 sessÃµes por cenÃ¡rio, 60 s de amostragem a cada 5 s, no mesmo PC (16 GB, Windows 10 22H2, Chrome do
dia, WebView2 155). Os CSVs brutos estÃ£o em `benchmark-results/`. Valores = **memÃ³ria privada em RAM,
mÃ©dia da 2Âª metade**, somando todos os processos do cenÃ¡rio.

**1) Sobrecarga da arquitetura: mesma pÃ¡gina neutra (`https://example.com`) nas 4 sessÃµes**

| CenÃ¡rio | Processos | Privada em RAM | Commit privado | B economiza |
|---|---|---|---|---|
| A1: 4 Chromes independentes (`--user-data-dir` cada) | 36 | 628 MB | 1138 MB | **âˆ’66%** (âˆ’416 MB) |
| A2: 1 Chrome com 4 perfis | 14â€“30 | 329 MB | 636 MB | **âˆ’35%** (âˆ’116 MB) |
| **B: ThoseIdles** | **10** | **213 MB** | **320 MB** | â€” |

Isso mede o que a arquitetura de fato muda: processos de navegador, GPU e rede, extensÃµes internas e
o anfitriÃ£o. Com 4 Chromes independentes, a economia Ã© de ~400 MB. Contra perfis no mesmo Chrome, que
jÃ¡ compartilham o navegador, sobra ~120 MB (sem extensÃµes internas, sem processos de serviÃ§o do Chrome).

**2) PÃ¡gina real do jogo (`/app`, deslogado â†’ tela de login)**

| CenÃ¡rio | Processos | Privada em RAM (mÃ©dia) | 2Âª metade | CPU |
|---|---|---|---|---|
| A1 | 38 | 1142 MB | 1068 MB | 1,0% |
| A2 | 23 | 1239 MB | 1169 MB | 0,7% |
| B | 15 | 1191 MB | 1241 MB | 3,8% |

**Este teste nÃ£o permite concluir nada sobre contas logadas.** Na tela de login, a memÃ³ria Ã© dominada
pelo widget do Cloudflare Turnstile, que cresce enquanto Ã© renderizado. O Chrome congela janelas
encobertas (CPU 0,7%, crescimento parado), enquanto o app mantÃ©m as 4 contas renderizando (que Ã© o
requisito) e o crescimento continua. O jogo remove o Turnstile apÃ³s o login, entÃ£o esse efeito nÃ£o
existe na sessÃ£o real.

**3) Tempo atÃ© a carga estabilizar** (MemBench: processos constantes e memÃ³ria variando < 2%/s por 5 s):
A1 7,5 s Â· A2 8,6 s Â· **B 14,6 s** na pÃ¡gina do jogo; A1 7,5 s Â· A2 4,4 s Â· B 13,7 s na pÃ¡gina neutra.
O app inicializa mais devagar que um Chrome jÃ¡ instalado (o .NET, a janela WPF e o ambiente WebView2
sobem do zero), mas a linha do tempo interna mostra janela pronta em ~1,7 s e cada conta carregada
~0,9 s depois de aberta (pÃ¡gina local).

**ConclusÃ£o honesta:** o ganho garantido pela arquitetura Ã© a sobrecarga fixa: ~400 MB contra 4 Chromes
independentes, ~120 MB contra 4 perfis no mesmo Chrome. O que cada conta do jogo consome em JavaScript
Ã© praticamente igual nos dois (mesmo motor Chromium/V8). Os ~40% de RAM que vocÃª vÃª hoje no Chrome
provavelmente incluem outras abas, extensÃµes e o histÃ³rico de horas de uso. **O nÃºmero que vale Ã© o seu
benchmark com as contas logadas** (procedimento acima), que eu nÃ£o tenho como executar.

TambÃ©m medido: o `.exe` single-file **comprimido** fazia o processo do app subir de ~34 MB para ~98 MB
(assemblies descompactados em memÃ³ria). Por isso a publicaÃ§Ã£o Ã© sem compressÃ£o (127 MB em disco).

## 11. LimitaÃ§Ãµes conhecidas

- **NÃ£o testei com contas reais logadas** (nÃ£o tenho as credenciais e nÃ£o devo usÃ¡-las). O que foi
  verificado com o site real: as 4 pÃ¡ginas `/app` abrem lado a lado, o Cloudflare Turnstile valida
  dentro do WebView2 ("Sucesso!") e o estado fica corretamente em "Sem conexÃ£o do jogo" na tela de
  login. A detecÃ§Ã£o de "Conectada" foi validada com um servidor WebSocket local, e o cÃ³digo do jogo
  confirma que ele usa `new WebSocket(wss://<mesmo host>)`. **Confirme com as suas contas** (seÃ§Ã£o 9).
- **Login com Google:** o Google Ã s vezes recusa login dentro de navegadores embutidos ("Este navegador
  ou app pode nÃ£o ser seguro"). Se acontecer, entre com usuÃ¡rio/e-mail e senha ou com Discord. O app
  **nÃ£o** falsifica user-agent para contornar isso.
- **Mesma conta em dois lugares:** o jogo derruba a sessÃ£o mais antiga ("sessÃ£o tomada"). NÃ£o deixe a
  mesma conta aberta no Chrome e no app ao mesmo tempo.
- **Tela de login consome memÃ³ria crescente:** o widget do Turnstile cresce ~2 MB/s por conta enquanto
  fica visÃ­vel. Isso acontece igualmente no Chrome e Ã© comportamento do site. O jogo remove o widget ao
  entrar (`turnstileDestruir`), entÃ£o nÃ£o afeta contas logadas. NÃ£o deixe contas paradas na tela de
  login por horas.
- **MemÃ³ria por conta** = renderers daquela conta (incluindo iframes dela). Navegador, GPU e rede sÃ£o
  compartilhados e aparecem Ã  parte no detalhamento da barra superior.
- A mÃ©trica "privada em RAM" exige Windows 10 21H2+ (`PROCESS_MEMORY_COUNTERS_EX2`). Em versÃµes
  anteriores, o app mostra o commit privado.
- **requestAnimationFrame com a janela minimizada:** neste PC continuou rodando (â‰ˆ144 quadros/s). Isso
  depende do driver e da versÃ£o do Windows; timers e WebSocket, que sustentam o jogo, nÃ£o dependem disso.
- As flags anti-throttling valem para o ambiente inteiro (todas as contas). Isso Ã© intencional.
- O WebView2 Runtime Ã© atualizado pelo Windows (Evergreen). Consumo e comportamento podem mudar entre
  versÃµes. Rode o benchmark de novo apÃ³s grandes atualizaÃ§Ãµes.
- O `.exe` single-file nÃ£o Ã© assinado: o SmartScreen pode avisar na primeira execuÃ§Ã£o. As bibliotecas
  nativas (WebView2Loader, WPF) sÃ£o extraÃ­das para a pasta temporÃ¡ria do .NET na primeira execuÃ§Ã£o.
- **Ctrl + roda do mouse** para o zoom de uma conta nÃ£o foi verificado automaticamente. O WebView2 sÃ³
  avisa mudanÃ§as de zoom feitas pelo usuÃ¡rio, e nenhuma alteraÃ§Ã£o por cÃ³digo simula isso. Quando
  tentei com entrada real, vocÃª estava usando o PC, entÃ£o o teste nÃ£o rodou. Os botÃµes âˆ’/+ do
  cabeÃ§alho fazem o mesmo e estÃ£o cobertos pelo autoteste.
- `allowedHosts` sÃ³ Ã© editÃ¡vel pelo `settings.json`. Se o site adotar outro provedor de login, o primeiro
  salto para ele abre no navegador padrÃ£o atÃ© o host ser adicionado.

## 12. Estrutura do cÃ³digo

```
src/ThoseIdles/
  App.xaml(.cs)                 composiÃ§Ã£o, argumentos, exceÃ§Ãµes globais, instÃ¢ncia Ãºnica
  MainWindow.xaml(.cs)          barra superior, configuraÃ§Ãµes, layout das 4 contas
  Views/AccountPanel.xaml       painel de uma conta (cabeÃ§alho, faixa de estado, WebView2)
  Views/SessionLayoutPanel.cs   grade 2x2 / foco + miniaturas (nunca oculta um painel)
  ViewModels/MainViewModel.cs   estado e comandos da janela, configuraÃ§Ãµes
  ViewModels/AccountViewModel.cs estado e comandos de uma conta
  Services/ProfileManager.cs    ambiente WebView2 Ãºnico, perfis conta1..4, flags
  Services/BrowserSession.cs    ciclo de vida de uma conta, estado, recuperaÃ§Ã£o, seguranÃ§a
  Services/BrowserSession.Live.cs  live da Twitch da conta (mesmo perfil, segundo plano, mudo, 160p)
  Services/BrowserSessionManager.cs  abrir/fechar todas, queda do navegador, rede, encerramento
  Services/NavigationPolicy.cs  validaÃ§Ã£o de URLs
  Services/HuntExtensionService.cs  download, validaÃ§Ã£o e versÃµes da extensÃ£o /hunt
  Services/MemoryMonitor.cs     memÃ³ria por processo e por conta
  Services/BenchmarkService.cs  linha do tempo de inicializaÃ§Ã£o e CSVs
  Services/SettingsService.cs   settings.json (escrita atÃ´mica)
  Services/LoggingService.cs    log com rotaÃ§Ã£o e remoÃ§Ã£o de query strings
  Services/ProcessJob.cs        Job Object contra processos Ã³rfÃ£os
  SelfTest/                     autoteste de ponta a ponta
src/Shared/                     leitura de memÃ³ria e Ã¡rvore de processos (app + MemBench)
tools/MemBench/                 ferramenta de benchmark (CSV, comparaÃ§Ã£o)
tools/benchmark.ps1             benchmark automÃ¡tico com perfis temporÃ¡rios (A1, A2, B)
tools/make-icon.ps1             gera o Ã­cone (pokÃ©bola) em src/ThoseIdles/Assets/pokeball.ico
tests/run-selftest.ps1          autoteste em duas fases
```
