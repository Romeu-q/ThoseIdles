# ThoseIdles

Gerenciador de **4 contas** do [PokeIdle.io](https://pokeidle.io/) em uma janela só, com Microsoft Edge
WebView2, em C#/.NET 10 + WPF. Cada conta tem sessão própria e persistente, e as quatro continuam
rodando ao mesmo tempo, inclusive quando só uma está em destaque ou quando a janela está minimizada.

> É um gerenciador de sessões de navegador. Não automatiza jogadas, não altera requisições e não
> contorna proteções do servidor.

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

## 9. Testes

### Autoteste automático (WebView2 real, sem tocar nas suas contas)

```bash
powershell -ExecutionPolicy Bypass -File tests/run-selftest.ps1
```

O autoteste usa uma pasta de dados temporária e uma página local que imita o jogo (HTTP + WebSocket em
`localhost`). Ele roda o app duas vezes e verifica processos órfãos entre as execuções. Para testar o
`.exe` publicado: `-Exe publish\win-x64\ThoseIdles.exe`.

**Resultado (15/10/2026, Windows 10 22H2, WebView2 Runtime 155):** aprovado nos 24 itens, tanto no
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
| Extensão: manifesto real da /hunt aceito; 5 manifestos perigosos recusados | OK |
| Extensão carregada em cada perfil, antes do script da página | OK |
| Desligar a extensão só na conta 2: some só nela, 1/3/4 não recarregam | OK |
| Desligar/ligar no app recria o ambiente e mantém as sessões | OK (~2 s) |
| Memória atribuída por conta (renderers distintos) | OK |
| Nenhum `msedgewebview2.exe` órfão depois de fechar | OK |
| Reabrir o app: contas 1–3 mantêm a sessão, a 4 continua limpa | OK |
| Reabrir o app: extensão continua ativa nas 4 | OK |
| Reabrir o app: divisórias e zoom de cada conta continuam iguais | OK |

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
