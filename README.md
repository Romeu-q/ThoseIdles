# ThoseIdles

Gerenciador de **4 contas** do [PokeIdle.io](https://pokeidle.io/) em uma janela só, com Microsoft Edge
WebView2, em C#/.NET 10 + WPF. Cada conta tem sessão própria e persistente, e as quatro continuam
rodando ao mesmo tempo, inclusive quando só uma está em destaque ou quando a janela está minimizada.

> É um gerenciador de sessões de navegador. Não automatiza jogadas, não altera requisições e não
> contorna proteções do servidor.

**Baixar:** pegue o `ThoseIdles.exe` na página [Releases](../../releases). Ele não precisa de instalação
nem do .NET instalado; precisa só do Microsoft Edge WebView2 Runtime, que já vem no Windows 11 e na
maioria dos Windows 10 (veja a seção 6).

---

## É seguro usar?

O código inteiro está neste repositório, e qualquer pessoa pode ler, compilar e conferir. Em resumo:

**O app nunca pede a sua senha.** O login é feito na **própria página do PokeIdle** (ou do Google,
Discord ou Twitch, se você usar esses logins), dentro de um navegador embutido, do mesmo jeito que no
Chrome. O app não lê o que você digita.

**Com quem o app se comunica:**

| Destino | Quando | Para quê |
|---|---|---|
| `pokeidle.io` | sempre | o jogo (a página ou, no modo econômico, a conexão leve) |
| Twitch (`twitch.tv`) | só se você abrir lives | as lives do bônus de XP |
| Google, Discord, Twitch, Cloudflare | só no login | as páginas de login e o captcha do próprio jogo |
| `clients2.google.com` | só se você ativar a extensão /hunt | baixar e atualizar a extensão |

**Não existe servidor do ThoseIdles.** O app não tem telemetria nem estatísticas de uso, não tem
anúncios e não envia seus dados para lugar nenhum além dessa tabela. As páginas do jogo e da Twitch
carregam o que esses sites carregariam no Chrome; o app não acrescenta nada.

**Seus dados ficam no seu computador**, em `%LOCALAPPDATA%\ThoseIdles`:
- as sessões de cada conta (cookies e dados de navegação), cada uma num perfil separado;
- o token do modo econômico (o mesmo que o jogo guarda no navegador), **criptografado com o DPAPI do
  Windows**, que só o seu usuário do Windows consegue ler;
- logs, que **removem tokens e parâmetros de URLs** e nunca registram cookies nem mensagens do jogo.

Para desinstalar, apague o `ThoseIdles.exe` e a pasta `%LOCALAPPDATA%\ThoseIdles`.

**O que o app não faz:** não joga por você, não clica, não manda ações de jogo, não altera mensagens
do jogo e não burla limites. O modo econômico só mantém a conta conectada, mandando ao servidor as
mesmas mensagens de conexão que a página oficial manda (`hello` e `ping`). Os termos do PokeIdle
(seção 5) permitem automação e até 4 contas por pessoa. A página da conta não consegue pedir ao app
para executar nada (sem ponte entre página e app), e o app só abre links `http`/`https` no seu
navegador.

**Como conferir por conta própria:**
1. **Ler o código.** Os pontos que tocam em rede e credenciais são
   [EconomyClient.cs](src/ThoseIdles/Services/EconomyClient.cs) (modo econômico),
   [BrowserSession.Economy.cs](src/ThoseIdles/Services/BrowserSession.Economy.cs) (credenciais com DPAPI),
   [NavigationPolicy.cs](src/ThoseIdles/Services/NavigationPolicy.cs) (para onde as páginas podem navegar),
   [HuntExtensionService.cs](src/ThoseIdles/Services/HuntExtensionService.cs) (download da extensão) e
   [LoggingService.cs](src/ThoseIdles/Services/LoggingService.cs) (o que vai para o log).
2. **Conferir o `.exe`.** Cada Release traz o **SHA-256** do arquivo. No PowerShell:
   `Get-FileHash .\ThoseIdles.exe -Algorithm SHA256`. O resultado tem que ser igual ao da página.
3. **Compilar você mesmo** a partir deste código (seção 7) e usar o seu próprio `.exe`.

**Aviso do Windows:** o `.exe` não tem assinatura digital paga, então na primeira vez o Windows pode
mostrar "O Windows protegeu o computador". Clique em **Mais informações → Executar assim mesmo**.
Alguns antivírus desconfiam de qualquer `.exe` sem assinatura; o código aqui mostra o que ele faz.

---

## 1. Arquitetura e por que ela economiza memória

```
ThoseIdles.exe (WPF, MVVM)
└── 1 CoreWebView2Environment  (pasta de dados única)
    ├── msedgewebview2.exe  navegador     ┐
    ├── msedgewebview2.exe  GPU           │ compartilhados pelas 4 contas
    ├── msedgewebview2.exe  rede/storage  │ (1 cópia de cada)
    ├── msedgewebview2.exe  crashpad      ┘
    ├── perfil "conta1" ── renderer(s) da conta 1   ┐
    ├── perfil "conta2" ── renderer(s) da conta 2   │ isolados: cookies, Local Storage,
    ├── perfil "conta3" ── renderer(s) da conta 3   │ IndexedDB, cache e service workers
    └── perfil "conta4" ── renderer(s) da conta 4   ┘ em pastas separadas
```

**Um ambiente com quatro perfis nomeados** (`CoreWebView2ControllerOptions.ProfileName`). Esta é a
decisão central:

| Opção | Processos de navegador/GPU/rede | Isolamento de sessão | Custo |
|---|---|---|---|
| 4 Chromes com `--user-data-dir` próprio | 4 árvores completas | sim | alto |
| 4 ambientes WebView2 (4 pastas de dados) | 4 árvores completas | sim | ≈ igual ao anterior |
| 4 WebView2 no mesmo ambiente **sem** perfil | 1 árvore | **não** (sessão compartilhada) | inaceitável |
| **1 ambiente + 4 perfis (escolhida)** | **1 árvore** | **sim** | **menor** |

- Perfis diferentes **nunca** compartilham renderer: o Chromium separa as *StoragePartitions* por
  perfil. Compartilhar processos aqui não significa compartilhar sessão; o autoteste comprova isso.
- O nome do perfil vem do slot (`conta1`…`conta4`), não do nome exibido. **Renomear não muda a sessão.**
- No disco: `%LOCALAPPDATA%\ThoseIdles\WebView2\EBWebView\WV2Profile_conta1` … `_conta4`.

**Outras economias aplicadas** (nenhuma desliga GPU, JavaScript, armazenamento ou rede):

- O WebView2 não carrega as extensões internas que o Chrome sobe por perfil. No benchmark, o
  Chrome de teste com 4 perfis abriu 8 renderers de extensão.
- Sem sincronização de conta Google, sem autofill/salvamento de senha
  (`IsPasswordAutosaveEnabled=false`, `IsGeneralAutofillEnabled=false`). Extensões ficam desligadas
  no ambiente, a menos que você ative a /hunt (seção 8.1).
- A interface WPF é renderizada por software (configurável), então o processo do app não carrega
  Direct3D. Os jogos continuam usando a GPU normalmente, no processo de GPU do WebView2.
- GC de estação sem thread concorrente no processo do app.
- O monitor de conexão usa `Network.enable` com buffers de resposta **zerados**
  (`maxTotalBufferSize: 0`), para o DevTools não reter corpos de resposta.
- Ao fechar ou reiniciar uma conta, o controller é descartado (`WebView2.Dispose()`) e o renderer
  dela termina. Os eventos são desinscritos antes do descarte.
- Um Job Object (`KILL_ON_JOB_CLOSE`) garante que nenhum `msedgewebview2.exe` fique órfão, mesmo
  se o app for finalizado à força.
- **Opcional e desligado por padrão:** `MemoryUsageTargetLevel.Low` nas miniaturas. Ele não pausa
  scripts, mas pode descartar caches. Meça antes de deixar ligado.

**Rejeitado de propósito:** desligar o isolamento de sites (`--disable-site-isolation-trials`)
pouparia alguns renderers de iframes de terceiros, mas enfraquece a segurança. Limitar o heap do V8
(`--js-flags=--max-old-space-size`) pode derrubar o jogo. Ocultar ou suspender contas fora de vista
viola o requisito de execução contínua.

## 2. As 4 contas continuam rodando (requisito 3.3)

Sem configuração, o Chromium desacelera páginas "ocultas": timers limitados a 1/s, depois 1/min após
5 minutos (*intensive wake-up throttling*), renderer com prioridade baixa e janelas cobertas tratadas
como ocultas. O app impede isso de duas formas:

1. **Os painéis nunca são ocultados nem tirados da árvore visual.** O modo "uma conta" só muda
   retângulos: a conta escolhida fica grande e as outras três viram **miniaturas vivas** à direita,
   com zoom reduzido para manter o layout desktop da página.
2. **Flags do ambiente** (`ProfileManager.BrowserArguments`):
   `--disable-background-timer-throttling --disable-backgrounding-occluded-windows
   --disable-renderer-backgrounding --disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling`.

O app não usa `TrySuspendAsync`, não descarta abas e não congela timers.

## 3. Indicador de estado

O app **não** marca "Conectada" só porque a página abriu. O jogo usa um WebSocket em
`wss://pokeidle.io`, e o app observa (somente leitura, via DevTools Protocol) os eventos de criação,
handshake e fechamento desse socket. O conteúdo das mensagens não é lido.

| Estado | Significado |
|---|---|
| Carregando | navegação em andamento |
| Sem conexão do jogo | página aberta, nenhum WebSocket do jogo aberto (ex.: tela de login) |
| **Conectada** | WebSocket do servidor do jogo aberto (handshake HTTP 101 confirmado) |
| Reconectando | estava conectada e o socket caiu; o próprio jogo tenta reconectar |
| Offline | o Windows está sem rede |
| Erro | falha de carregamento, processo da página travou ou caiu (com nova tentativa automática) |
| Fechada | instância encerrada pelo usuário |

Se o monitor for desligado nas configurações, o estado após carregar passa a ser "Carregada", sem
confirmação de conexão.

## 4. Recuperação de falhas

- **Falha ao carregar / site fora do ar (HTTP 5xx, rede):** novas tentativas com espera crescente
  (5 s, 15 s, 30 s, 1 min, 2 min, 5 min…), só naquela conta. HTTP 4xx (ex.: desafio ou bloqueio do
  Cloudflare, 429) **não** entra em loop; o botão "Tentar agora" fica na faixa da conta.
- **Renderer caiu:** recarrega só aquela conta.
- **Renderer travado:** espera 30 s e testa com um script trivial; se continuar travado, reinicia só
  aquela instância.
- **Processo do navegador caiu:** recria o ambiente e reabre as contas que estavam abertas.
- **Rede do Windows voltou:** contas em erro tentam de novo na hora.
- O app nunca tenta logar sozinho e nunca guarda senha. O login é o que o próprio site guardou no
  perfil (cookies/Local Storage).

## 5. Segurança

- Nenhuma ponte página → app: `AreHostObjectsAllowed=false`, `IsWebMessageEnabled=false`. A página não
  consegue pedir ao app para executar nada.
- Navegação do frame principal validada (`NavigationPolicy`): só HTTPS, só hosts permitidos
  (`pokeidle.io` e os provedores de login Google/Discord/Twitch). Redirecionamentos de servidor iniciados
  num host permitido são aceitos. Links externos clicados pelo jogador abrem no navegador padrão
  (só http/https). Esquemas externos (`mailto:`, apps) são bloqueados.
- Pop-ups (`window.open`) nunca viram janelas dentro do app. Vão para o navegador padrão, com limite
  de frequência.
- Permissões (câmera, microfone, localização, notificações, área de transferência…) são **negadas**.
  Downloads são bloqueados.
- DevTools só com "Modo desenvolvedor" ligado. **Sem porta de depuração remota.**
- Logs: toda URL perde query string e fragmento (`?sessao=…`, tokens de OAuth). Cookies, cabeçalhos e
  corpos nunca são registrados. Rotação em 1 MB × 4 arquivos.
- Limpar dados de uma conta usa `CoreWebView2Profile.ClearBrowsingDataAsync()` **naquele perfil**.
  Antes, a página vai para `about:blank`, para o jogo em memória não regravar o token.
- Uma instância por pasta de dados (mutex) e `ExclusiveUserDataFolderAccess=true`.

## 6. Requisitos

- Windows 10 (1809+) ou 11, x64.
- **Microsoft Edge WebView2 Runtime (Evergreen).** Já vem no Windows 11 e na maioria dos Windows 10
  com Edge atualizado. Se faltar, o app avisa ao abrir. Instalação:
  <https://developer.microsoft.com/microsoft-edge/webview2/> → "Evergreen Bootstrapper".
  O executável **não é autossuficiente** quanto a isso: o WebView2 Runtime é um componente externo,
  compartilhado e atualizado pelo Windows.
- Para compilar: .NET SDK 10 (LTS). Testado com SDK 10.0.302, WebView2 SDK 1.0.4258.31 e Runtime 155.0.4283.45.

## 7. Compilar e gerar o `.exe`

```bash
dotnet build ThoseIdles.slnx -c Release
```

**Executável único, sem precisar instalar o .NET** (recomendado):

```bash
dotnet publish src/ThoseIdles/ThoseIdles.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

Resultado: `publish/win-x64/ThoseIdles.exe` (~127 MB, sem compressão de propósito; veja a seção 10).
Ele ainda depende do WebView2 Runtime (seção 6).

**Executável pequeno, dependente do .NET 10 Desktop Runtime instalado:**

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
  e ⚙ configurações.
- **Cada painel:** status, zoom −/+ e porcentagem (clique volta a 100%), memória dos renderers daquela
  conta, renomear ✎, limpar dados 🗑 (com confirmação na própria barra), DevTools `</>` (modo
  desenvolvedor), recarregar, reiniciar só aquela instância, fechar/abrir e maximizar.
- **Tamanho de cada tela (arrastar):** na grade 2x2 há uma divisória entre as linhas e uma divisória
  **em cada linha**, independentes entre si. Assim cada conta pode ter largura e altura próprias. No
  modo "uma conta", a divisória ajusta a largura da coluna de miniaturas. Duplo clique numa divisória
  volta ao meio. As posições ficam salvas. Arrastar só muda o tamanho: nenhuma conta recarrega.
- **Zoom de cada conta:** botões −/+ no cabeçalho (passos de 10%) ou Ctrl + roda do mouse na página.
  Fica salvo por conta. "Zoom de todas as contas", nas configurações, ajusta as 4 de uma vez.
- **Configurações:** abrir as 4 ao iniciar, monitor de conexão, zoom da grade, miniaturas com zoom,
  memória baixa nas miniaturas (experimental), UI por software, CSV de memória, intervalo de medição,
  exportar snapshot por processo, abrir pastas de logs e dados.

Dados em `%LOCALAPPDATA%\ThoseIdles\`. O app se chamava "PokeIdle Multi": se existir a pasta antiga
`%LOCALAPPDATA%\PokeIdleMulti`, ela é movida para a nova na primeira abertura, com logins,
configurações e extensão.

| Caminho | Conteúdo |
|---|---|
| `settings.json` | preferências (nomes, layout, janela, opções, `allowedHosts`) |
| `WebView2\` | os 4 perfis (sessões) |
| `logs\app.log` | log com rotação |
| `benchmark\` | `startup-*.csv` (linha do tempo), `memoria-app-*.csv`, `snapshot-*.csv` |

`allowedHosts` pode ser editado no `settings.json` (com o app fechado), caso o site passe a usar outro
provedor de login.

### 8.1 Extensão "/hunt for PokeIdle"

A [/hunt for PokeIdle](https://chromewebstore.google.com/detail/hunt-for-pokeidle/mhnplloljgchpfpcpbhmcokmnefodjnh)
é um guia de hunts que o próprio jogo recomenda nos comandos `/guia` e `/hunt`. Ela desenha um painel
sobre o seu Pokémon com as melhores hunts por XP/h e gold/h. O app pode carregá-la em cada conta.

**Como usar:** ⚙ Configurações → **Baixar / atualizar**. O app baixa a versão atual da loja, valida e
liga a extensão (as contas abertas são reabertas, continuam logadas). Em cada painel aparece o botão 🔍
para ligar ou desligar a extensão só naquela conta; ele recarrega só aquela conta. A caixa "Usar nas
contas" desliga a extensão em todas.

**O que foi verificado no código dela (v1.0.2):**

- Só tem content scripts (MV3, `world: MAIN`) que rodam só em `https://pokeidle.io/*`. Não tem service
  worker, popup nem permissões.
- O `hook.js` só **acrescenta um ouvinte** às mensagens do WebSocket do jogo. Não envia, não altera e
  não clica em nada: não é automação.
- Os `fetch`/`import` buscam só módulos e JSONs do próprio pokeidle.io (as fórmulas do jogo). Nada vai
  para outro servidor.
- Grava só no `localStorage` do pokeidle.io, que no app é separado por conta.

**Proteções do app:**

- A extensão **não vem junto com o app** (não tem licença declarada). É baixada do servidor de
  atualização do Google quando você clica no botão.
- Antes de instalar, o manifesto é validado: só são aceitos content scripts em `https://pokeidle.io/*`,
  sem `permissions`, `host_permissions`, `background`, popup ou outras páginas, e sem caminhos com `..`.
  Se uma versão futura pedir mais do que isso, **ela é recusada** e o motivo aparece na tela.
- Com a extensão desligada, o ambiente WebView2 é criado com `AreBrowserExtensionsEnabled=false`.

**Atualização automática** (opção "Atualizar automaticamente", ligada por padrão). O WebView2 não
atualiza extensões sozinho como o Chrome, então o app faz isso:

- Consulta a loja ao abrir (até 8 s, antes de abrir as contas) e depois a cada 12 h, pelo mesmo
  protocolo de atualização que o Chrome usa.
- Se houver versão nova, baixa e confere o **tamanho e o hash SHA-256** informados pela loja, depois
  valida o manifesto (mesmas regras acima).
- **Nenhuma conta é recarregada sozinha.** Contas que abrirem depois já usam a versão nova. As contas
  abertas passam a usá-la quando você recarregar ⟳ ou reabrir a conta. "Baixar / atualizar" aplica
  na hora em todas.
- Sem rede ou com a loja fora do ar, o app tenta de novo na próxima rodada, sem incomodar.

**Custo medido:** com as 4 contas no pokeidle.io (sem login), ligar a extensão não criou nenhum
processo novo (15 → 15) e mudou a memória em +9 MB no total, dentro do ruído. Sem login ela só
instala o hook. Logada, ela carrega o catálogo do jogo, e o custo real só dá para medir com as suas
contas (o detalhamento por conta na barra superior mostra isso).

### 8.1.1 Modo econômico (conta conectada sem a página do jogo)

A página do jogo logada usa ~300–330 MB por conta. No **modo econômico** (botão **Eco** no cabeçalho
da conta), o app fecha a página e mantém a conta conectada com um **cliente leve** dentro do próprio
app, sem página, sem JavaScript e sem processo extra. A caçada continua, porque o servidor do jogo
caça enquanto a conta está conectada (e segura a hunt por 90 s se ela cair). As lives da Twitch da
conta continuam abertas. "Abrir o jogo desta conta" volta para a página normal.

- **Permitido pelos termos do jogo** (seção 5: "Automações são permitidas", até 4 contas por pessoa,
  sem explorar falhas nem sobrecarregar o servidor). O cliente faz só o que o cliente oficial faz para
  manter a conexão: o mesmo `hello` (nick, token e id do aparelho que o jogo guardou no navegador da
  conta), `ping` a cada 25 s e reconexão com espera crescente (2 s → 60 s). **Não envia nenhuma ação
  de jogo.**
- Se o jogo recusar (conta aberta em outro lugar, código 4001; IP bloqueado, 4003; ou erro na
  entrada, como token inválido ou rede cheia), o cliente **para e avisa**, sem insistir.
- Nick, token e id do aparelho ficam guardados **criptografados com o DPAPI do Windows** (só o seu
  usuário do Windows lê). Se o servidor renovar o token, o app guarda o novo e o entrega à página
  quando você abrir o jogo de novo.
- A conta reabre em modo econômico quando o app abre. Limpar os dados da conta desliga o modo e apaga
  as credenciais guardadas.
- **Validado contra um servidor que imita o do jogo** (autoteste). Com o servidor real e uma conta
  real, isso ainda depende do seu teste: se o servidor do jogo pedir algo que o navegador manda e o
  cliente leve não manda, o status da conta vai mostrar o motivo.

### 8.2 Live da Twitch (bônus de XP do jogo)

O PokeIdle dá **+15% de XP** a quem assiste a uma live oficial **logado numa conta da Twitch
vinculada**, e +2,5% por live oficial a mais. O servidor do jogo confere uma vez por minuto se o seu
login da Twitch está **no chat** da live. Basta a aba da live estar aberta e logada; ela não precisa
estar na tela.

**Como usar:**

1. Em cada conta, faça o "Vincular Twitch" do próprio jogo com a conta da Twitch daquela conta. O
   login fica no perfil da conta: cada conta pode ter uma Twitch diferente.
2. Na tela "Twitch" do jogo, clique nos canais. **Cada canal abre mais uma live** (o bônus soma por
   live), dentro daquela conta e em segundo plano: muda, com padrão de 160p, sem ocupar espaço. Clicar
   num canal que já está aberto não abre de novo. São até 10 lives por conta. Fora do app, o link
   abriria no seu navegador padrão, que tem outro login.
3. O botão 📺 no cabeçalho mostra **quantas lives** a conta tem e mostra ou esconde o painel. Escondidas,
   todas **continuam abertas**, como abas do Chrome em segundo plano. O painel tem uma aba por canal
   (clique para ver, × para fechar e esquecer aquele canal), mudo/som e abrir no navegador para a live
   escolhida, esconder, e 🗑 para fechar todas.
4. As lives reabrem sozinhas quando a conta abre (opção "Reabrir a live com a conta").

**Como funciona:** é um segundo WebView2 **no mesmo perfil da conta**, então tem o mesmo login da
Twitch e outra conta nunca o vê. Escondida, a página fica `hidden` para o navegador, como uma aba em
segundo plano. O vídeo nem é decodificado, mas o chat continua conectado, porque as flags
anti-throttling do app valem para ela também. Na aba escondida, o app também pede ao WebView2 meta de
memória baixa.

> O app **não** usa truques como abrir só o chat sem a live, nem bloqueia anúncios ou partes da página
> da Twitch: é a mesma página que você teria numa aba do Chrome.

**Custo medido** (live real da Twitch, canal lofigirl, 4 contas abertas numa página neutra no lugar
do jogo, 45 s após abrir):

| | Memória total | Processos | CPU da máquina |
|---|---|---|---|
| 4 contas sem live | 214 MB | 10 | 0,0% |
| live **escondida** em 1 conta | 777 MB (+563 MB) | 36 | 0,1% |
| live **escondida** nas 4 contas | 2,24 GB (+2,03 GB, ≈ 520 MB por live) | 113 | 0,3% |
| para comparar: live **visível** nas 4 contas | 2,71–2,99 GB (≈ 640–714 MB por live) | 105–120 | 5,8–7,5% |

**Memória ao longo do tempo:** medindo uma sessão real (4 contas logadas + lives), as páginas do jogo
ficam estáveis (~330 MB cada), mas a live **cresce ~6 MB/min** porque o chat acumula mensagens. Por
isso o app **recarrega cada live a cada 60 min** (configurável: 30/60/120/240 min ou nunca, e há o
botão "Recarregar lives agora"). As lives recarregam uma por vez, com 20 s entre elas, continuam
escondidas, e o jogo não é tocado. O chat fica fora só alguns segundos, e o jogo confere uma vez por
minuto.

**Várias lives na mesma conta** (canais reais gaules, alanzoka, kaicenat e ibai, todos de chat grande,
abertos um por vez na conta 1, todos escondidos):

| Conta 1 com… | Memória a mais (total do app) | Processos | CPU da máquina |
|---|---|---|---|
| 1 live | +585 MB | 35 | 0,3% |
| 2 lives | +781 MB (+196 MB) | 36 | 0,6% |
| 3 lives | +1,09 GB (+310 MB) | 37 | 1,1% |
| 4 lives | +1,28 GB (+190 MB) | 38 | 1,6% |

A **primeira** live de cada conta é a cara (~0,5–0,6 GB): ela carrega o site da Twitch e seus
processos (anúncios, extensões de canal). As **seguintes, na mesma conta,** custam ~0,2–0,3 GB cada,
porque as abas da Twitch do mesmo perfil dividem processos. Para o bônus acumulativo, empilhar lives
numa conta sai bem mais barato que espalhar.

**Modo leve (experimental, desligado por padrão):** em vez do site inteiro, uma página mínima do app
com o player e o chat **oficiais incorporados** da Twitch (embed). Com a live visível, mediu ≈ 255 MB
por live, mas **não está verificado**: o player incorporado não chegou a decodificar vídeo no teste, e
eu não consegui confirmar se o chat incorporado reconhece o seu login. Sem login reconhecido, o jogo
não detecta você. Se for testar, confira no chat se aparece o seu nome.

### Exportar shinies para TXT

Na página **Shinies** do jogo aparece o botão **Exportar TXT**, ao lado do contador. Ele percorre
todas as páginas da lista, uma por vez, respeitando os filtros que estão na tela, e salva
`shinies-pokeidle-AAAAMMDD-HHmm.txt` na pasta **Downloads** no formato:

```
ESPECIE, POTENCIA, QUALIDADE, IVS, VALOR NO RMT
Charizard, P3, 87, 61 (hp 20 / atk 31 / def 10), 1500 Gemas
Gengar, P1, -, 42, - (à venda por 90000 ouro, não é RMT)
```

"Valor no RMT" é o preço do anúncio em **Gemas**; anúncios em ouro são indicados mas não contam
como RMT. O app só deixa a página baixar esse arquivo: qualquer outro download continua bloqueado.
Pode ser desligado em Configurações.

**Sem o app (Chrome/Edge):** o mesmo script existe como extensão em `extensions/shiny-export`.
Abra `chrome://extensions`, ative o **Modo do desenvolvedor**, clique em **Carregar sem compactação**
e escolha essa pasta.

### Alerta de RMT

No canto inferior esquerdo do jogo aparece o botão **🔔 RMT**. Nele você cria regras (nome contém,
só shiny, potência mínima, preço máximo em Gemas). Quando você abrir uma lista no mercado e aparecer
um anúncio **em Gemas** que bate com uma regra, surge um aviso no canto da tela com um bipe, e o
botão mostra quantos alertas novos há. Anúncios em ouro são ignorados.

O alerta **só lê** as mensagens que o jogo já manda para a página: não envia nada ao servidor, não
consulta o mercado sozinho e **não compra**. A compra é sempre você que faz. Pode ser desligado em
Configurações.

**Sem o app (Chrome/Edge):** também existe como extensão em `extensions/rmt-alert`, carregada do
mesmo jeito que a de shinies (Modo do desenvolvedor → Carregar sem compactação).

### Sniper do mercado (compra automática)

Réplica própria, escrita do zero, do "Rotom Sniper" divulgado no Discord do jogo. Vem **desativado**:
ative em Configurações → Ferramentas no jogo e reinicie a conta (↻). Aparece então o botão
**🎯 Sniper** no jogo, com:

- **Regras:** Pokémon ou item, nome contém, só shiny, potência mínima, **preço máximo** (obrigatório),
  moeda (ouro ou Gemas) e **limite de compras** por regra.
- **Interruptor "Sniper ligado":** só com ele ligado o sniper consulta o mercado (a cada 5 s por
  padrão, mínimo 3 s) e compra. Ele **volta desligado** sempre que a página recarrega.
- **Carência:** anúncio que ainda não pode ser comprado é agendado para logo depois de liberar.
- **Registro:** o que foi enviado, comprado, recusado ou ignorado por falta de saldo.

Use por sua conta e risco: compra automática gasta seu ouro e suas Gemas sem perguntar. Confira as
regras antes de ligar.

**Sem o app (Chrome/Edge):** extensão em `extensions/sniper`, carregada como as outras.

### Aviso de versão nova

Ao abrir, e a cada 6 h, o app consulta a última Release deste repositório no GitHub (consulta
pública, sem login, sem enviar dados). Se houver versão mais nova, aparece uma faixa verde com o
botão **Baixar**, que abre a página da Release no navegador. O app **não** se baixa nem se instala
sozinho: feche o app e troque o `.exe` pelo novo (as contas continuam logadas). O **X** dispensa o
aviso daquela versão. Pode ser desligado em Configurações.

## 9. Testes

### Autoteste automático (WebView2 real, sem tocar nas suas contas)

```bash
powershell -ExecutionPolicy Bypass -File tests/run-selftest.ps1
```

O autoteste usa uma pasta de dados temporária e uma página local que imita o jogo (HTTP + WebSocket em
`localhost`). Ele roda o app duas vezes e verifica processos órfãos entre as execuções. Para testar o
`.exe` publicado: `-Exe publish\win-x64\ThoseIdles.exe`.

**Resultado (15/10/2026, Windows 10 22H2, WebView2 Runtime 155):** aprovado nos 38 itens, tanto no
build normal quanto no `.exe` publicado. Os testes de extensão usam uma extensão de teste com o mesmo
formato da /hunt (content script em `world: MAIN`, `document_start`), só que para `localhost`:

| Teste | Resultado |
|---|---|
| Política de navegação (8 URLs) + remoção de query string do log | OK |
| 4 contas abrem e chegam a **Conectada** (WebSocket detectado) | OK, em ~1 s |
| Um único processo de navegador compartilhado pelas 4 contas | OK (mesmo `BrowserProcessId`) |
| Cada conta grava localStorage, cookie e IndexedDB | OK |
| Nenhuma conta enxerga dados de outra (JS + `CookieManager`) | OK |
| Perfis em pastas separadas (`WV2Profile_conta1…4`) | OK |
| Reiniciar a conta 3 não recarrega 1, 2 e 4; a 3 mantém os dados | OK |
| Fechar a conta 2 não afeta as outras (timers continuam); ela reabre logada | OK |
| Limpar dados da conta 4 apaga só a conta 4 | OK |
| Modo "uma conta": as 3 miniaturas seguem com timers a 10/s, `visible`, WebSocket aberto | OK (50/50 ticks em 5 s) |
| Janela **minimizada**: as 4 contas seguem com timers a 10/s e WebSocket aberto | OK (50/50 ticks em 5 s) |
| Divisórias: cada conta muda de tamanho de forma independente, sem recarregar | OK (ex.: 409×495, 957×495, 957×253, 409×253) |
| Divisória do modo "uma conta" | OK |
| Zoom por conta independente (conta 2 a 150%, as outras inalteradas) | OK |
| Botões −/+ de zoom ajustam só a conta e salvam | OK |
| Live: canal da Twitch clicado no jogo abre na conta, em segundo plano, sem tirar o jogo da tela | OK |
| Live: mesmo perfil da conta, muda, padrão 160p | OK |
| Live: memória separada da do jogo (renderer próprio) | OK |
| Live escondida fica `hidden` e continua rodando (timers a 10/s) | OK |
| Live: mostrar, esconder (sem recarregar) e fechar (esquece o canal) | OK |
| Live: reabrir pelo canal guardado | OK |
| Várias lives acumuladas na mesma conta (todas `hidden` e rodando; fechar uma não afeta as outras) | OK |
| Recarga das lives (página nova, continuam escondidas, jogo intacto) | OK |
| Modo econômico: fecha a página e conecta com o mesmo `hello` do navegador (0 renderers da conta) | OK |
| Modo econômico: `ping` periódico; token renovado guardado criptografado (nada em texto no `settings.json`) | OK |
| Modo econômico: 4001 e recusa na entrada param sem reconectar | OK |
| Modo econômico: "Abrir o jogo" volta logado com o token renovado | OK |
| Extensão: manifesto real da /hunt aceito; 5 manifestos perigosos recusados | OK |
| Extensão carregada em cada perfil, antes do script da página | OK |
| Desligar a extensão só na conta 2: some só nela, 1/3/4 não recarregam | OK |
| Desligar/ligar no app recria o ambiente e mantém as sessões | OK (~2 s) |
| Memória atribuída por conta (renderers distintos) | OK |
| Nenhum `msedgewebview2.exe` órfão depois de fechar | OK |
| Reabrir o app: contas 1–3 mantêm a sessão, a 4 continua limpa | OK |
| Reabrir o app: extensão continua ativa nas 4 | OK |
| Reabrir o app: divisórias e zoom de cada conta continuam iguais | OK |
| Reabrir o app: as lives guardadas da conta voltam, mudas e em segundo plano | OK |
| Reabrir o app: conta em modo econômico volta conectada, sem página | OK |

**Teste on-line da extensão real** (baixa a /hunt da loja e abre o pokeidle.io de verdade, sem login):

```bash
src\ThoseIdles\bin\Debug\net10.0-windows\ThoseIdles.exe --data-root %TEMP%\pim-hunt --selftest-hunt %TEMP%\pim-hunt\hunt.json
```

Resultado: v1.0.2 baixada, com hash SHA-256 conferido e manifesto validado. A verificação de
atualização respondeu "noupdate" para a versão instalada. Com a extensão ligada, o hook está ativo
nas 4 contas (`WebSocket = WebSocketObservado`), sem processos extras.

### Teste manual com as contas reais (só você pode fazer)

1. Abra o app. Em cada painel, entre com uma conta diferente.
2. Confirme que cada painel mostra **Conectada** e o nick certo.
3. Feche o app e abra de novo: as 4 devem voltar logadas.
4. Reinicie a conta 2 (↻): as outras não podem recarregar.
5. Use "Uma conta" e, depois de alguns minutos, confira se as miniaturas seguiram progredindo.

## 10. Benchmark: Chrome × ThoseIdles

### Como medir com as suas contas (o benchmark que importa)

As duas medições devem ser feitas nas mesmas condições: as 4 contas logadas e jogando, o mesmo tempo
de execução, e nenhum outro programa pesado mudando entre uma e outra.

**Cenário A — seus 4 Chromes:** abra as 4 contas como você faz hoje, espere estabilizar e rode:

```bash
publish/membench/MemBench.exe sample --target chrome --duration 1800 --interval 10 --label A-chrome --out A.csv
```

> `--target chrome` soma **todos** os `chrome.exe`. Feche as outras janelas/abas do Chrome antes, ou
> elas entram na conta.

**Cenário B — o app:** feche o Chrome, abra o ThoseIdles com as 4 contas e rode:

```bash
publish/membench/MemBench.exe sample --target app --duration 1800 --interval 10 --label B-app --out B.csv
```

**Comparar:**

```bash
publish/membench/MemBench.exe compare A.csv B.csv
```

Leia a linha **"privada em RAM — 2ª metade"**: é a memória que o Gerenciador de Tarefas mostra,
medida depois do aquecimento. O MemBench soma **todos** os processos auxiliares (renderers, GPU, rede,
crashpad), nunca só o processo principal. Para tempo de inicialização, `MemBench launch` mede até a
carga estabilizar, e o app grava a linha do tempo exata por conta em `benchmark\startup-*.csv`.

### Medição feita no ambiente de desenvolvimento (sem login)

Gerada por `tools/benchmark.ps1`, com perfis **temporários** (não usa o seu Chrome nem as suas contas),
4 sessões por cenário, 60 s de amostragem a cada 5 s, no mesmo PC (16 GB, Windows 10 22H2, Chrome do
dia, WebView2 155). Os CSVs brutos estão em `benchmark-results/`. Valores = **memória privada em RAM,
média da 2ª metade**, somando todos os processos do cenário.

**1) Sobrecarga da arquitetura: mesma página neutra (`https://example.com`) nas 4 sessões**

| Cenário | Processos | Privada em RAM | Commit privado | B economiza |
|---|---|---|---|---|
| A1: 4 Chromes independentes (`--user-data-dir` cada) | 36 | 628 MB | 1138 MB | **−66%** (−416 MB) |
| A2: 1 Chrome com 4 perfis | 14–30 | 329 MB | 636 MB | **−35%** (−116 MB) |
| **B: ThoseIdles** | **10** | **213 MB** | **320 MB** | — |

Isso mede o que a arquitetura de fato muda: processos de navegador, GPU e rede, extensões internas e
o anfitrião. Com 4 Chromes independentes, a economia é de ~400 MB. Contra perfis no mesmo Chrome, que
já compartilham o navegador, sobra ~120 MB (sem extensões internas, sem processos de serviço do Chrome).

**2) Página real do jogo (`/app`, deslogado → tela de login)**

| Cenário | Processos | Privada em RAM (média) | 2ª metade | CPU |
|---|---|---|---|---|
| A1 | 38 | 1142 MB | 1068 MB | 1,0% |
| A2 | 23 | 1239 MB | 1169 MB | 0,7% |
| B | 15 | 1191 MB | 1241 MB | 3,8% |

**Este teste não permite concluir nada sobre contas logadas.** Na tela de login, a memória é dominada
pelo widget do Cloudflare Turnstile, que cresce enquanto é renderizado. O Chrome congela janelas
encobertas (CPU 0,7%, crescimento parado), enquanto o app mantém as 4 contas renderizando (que é o
requisito) e o crescimento continua. O jogo remove o Turnstile após o login, então esse efeito não
existe na sessão real.

**3) Tempo até a carga estabilizar** (MemBench: processos constantes e memória variando < 2%/s por 5 s):
A1 7,5 s · A2 8,6 s · **B 14,6 s** na página do jogo; A1 7,5 s · A2 4,4 s · B 13,7 s na página neutra.
O app inicializa mais devagar que um Chrome já instalado (o .NET, a janela WPF e o ambiente WebView2
sobem do zero), mas a linha do tempo interna mostra janela pronta em ~1,7 s e cada conta carregada
~0,9 s depois de aberta (página local).

**Conclusão honesta:** o ganho garantido pela arquitetura é a sobrecarga fixa: ~400 MB contra 4 Chromes
independentes, ~120 MB contra 4 perfis no mesmo Chrome. O que cada conta do jogo consome em JavaScript
é praticamente igual nos dois (mesmo motor Chromium/V8). Os ~40% de RAM que você vê hoje no Chrome
provavelmente incluem outras abas, extensões e o histórico de horas de uso. **O número que vale é o seu
benchmark com as contas logadas** (procedimento acima), que eu não tenho como executar.

Também medido: o `.exe` single-file **comprimido** fazia o processo do app subir de ~34 MB para ~98 MB
(assemblies descompactados em memória). Por isso a publicação é sem compressão (127 MB em disco).

## 11. Limitações conhecidas

- **Não testei com contas reais logadas** (não tenho as credenciais e não devo usá-las). O que foi
  verificado com o site real: as 4 páginas `/app` abrem lado a lado, o Cloudflare Turnstile valida
  dentro do WebView2 ("Sucesso!") e o estado fica corretamente em "Sem conexão do jogo" na tela de
  login. A detecção de "Conectada" foi validada com um servidor WebSocket local, e o código do jogo
  confirma que ele usa `new WebSocket(wss://<mesmo host>)`. **Confirme com as suas contas** (seção 9).
- **Login com Google:** o Google às vezes recusa login dentro de navegadores embutidos ("Este navegador
  ou app pode não ser seguro"). Se acontecer, entre com usuário/e-mail e senha ou com Discord. O app
  **não** falsifica user-agent para contornar isso.
- **Mesma conta em dois lugares:** o jogo derruba a sessão mais antiga ("sessão tomada"). Não deixe a
  mesma conta aberta no Chrome e no app ao mesmo tempo.
- **Tela de login consome memória crescente:** o widget do Turnstile cresce ~2 MB/s por conta enquanto
  fica visível. Isso acontece igualmente no Chrome e é comportamento do site. O jogo remove o widget ao
  entrar (`turnstileDestruir`), então não afeta contas logadas. Não deixe contas paradas na tela de
  login por horas.
- **Memória por conta** = renderers daquela conta (incluindo iframes dela). Navegador, GPU e rede são
  compartilhados e aparecem à parte no detalhamento da barra superior.
- A métrica "privada em RAM" exige Windows 10 21H2+ (`PROCESS_MEMORY_COUNTERS_EX2`). Em versões
  anteriores, o app mostra o commit privado.
- **requestAnimationFrame com a janela minimizada:** neste PC continuou rodando (≈144 quadros/s). Isso
  depende do driver e da versão do Windows; timers e WebSocket, que sustentam o jogo, não dependem disso.
- As flags anti-throttling valem para o ambiente inteiro (todas as contas). Isso é intencional.
- O WebView2 Runtime é atualizado pelo Windows (Evergreen). Consumo e comportamento podem mudar entre
  versões. Rode o benchmark de novo após grandes atualizações.
- O `.exe` single-file não é assinado: o SmartScreen pode avisar na primeira execução. As bibliotecas
  nativas (WebView2Loader, WPF) são extraídas para a pasta temporária do .NET na primeira execução.
- **Ctrl + roda do mouse** para o zoom de uma conta não foi verificado automaticamente. O WebView2 só
  avisa mudanças de zoom feitas pelo usuário, e nenhuma alteração por código simula isso. Quando
  tentei com entrada real, você estava usando o PC, então o teste não rodou. Os botões −/+ do
  cabeçalho fazem o mesmo e estão cobertos pelo autoteste.
- `allowedHosts` só é editável pelo `settings.json`. Se o site adotar outro provedor de login, o primeiro
  salto para ele abre no navegador padrão até o host ser adicionado.

## 12. Estrutura do código

```
src/ThoseIdles/
  App.xaml(.cs)                 composição, argumentos, exceções globais, instância única
  MainWindow.xaml(.cs)          barra superior, configurações, layout das 4 contas
  Views/AccountPanel.xaml       painel de uma conta (cabeçalho, faixa de estado, WebView2)
  Views/SessionLayoutPanel.cs   grade 2x2 / foco + miniaturas (nunca oculta um painel)
  ViewModels/MainViewModel.cs   estado e comandos da janela, configurações
  ViewModels/AccountViewModel.cs estado e comandos de uma conta
  Services/ProfileManager.cs    ambiente WebView2 único, perfis conta1..4, flags
  Services/BrowserSession.cs    ciclo de vida de uma conta, estado, recuperação, segurança
  Services/BrowserSession.Live.cs  live da Twitch da conta (mesmo perfil, segundo plano, mudo, 160p)
  Services/BrowserSessionManager.cs  abrir/fechar todas, queda do navegador, rede, encerramento
  Services/NavigationPolicy.cs  validação de URLs
  Services/HuntExtensionService.cs  download, validação e versões da extensão /hunt
  Services/MemoryMonitor.cs     memória por processo e por conta
  Services/BenchmarkService.cs  linha do tempo de inicialização e CSVs
  Services/SettingsService.cs   settings.json (escrita atômica)
  Services/LoggingService.cs    log com rotação e remoção de query strings
  Services/ProcessJob.cs        Job Object contra processos órfãos
  SelfTest/                     autoteste de ponta a ponta
src/Shared/                     leitura de memória e árvore de processos (app + MemBench)
tools/MemBench/                 ferramenta de benchmark (CSV, comparação)
tools/benchmark.ps1             benchmark automático com perfis temporários (A1, A2, B)
tools/make-icon.ps1             gera o ícone (pokébola) em src/ThoseIdles/Assets/pokeball.ico
tests/run-selftest.ps1          autoteste em duas fases
```

## Licença

Código sob a [licença MIT](LICENSE): pode usar, estudar, modificar e redistribuir, mantendo o aviso de
copyright. O `.exe` inclui componentes de terceiros com licenças próprias (.NET e o SDK do Microsoft
Edge WebView2).

O ThoseIdles é um projeto independente, feito por fã. Não tem vínculo com o PokeIdle.io, a Twitch, a
Microsoft nem com os titulares de Pokémon (Nintendo, Game Freak, Creatures). Os nomes citados pertencem
aos seus donos.
