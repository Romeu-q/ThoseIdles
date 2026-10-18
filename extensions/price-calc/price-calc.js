// ThoseIdles — Calculadora de preço para o PokéIdle.
// Estima quanto vale um Pokémon (em Gemas/RMT e em ouro) comparando com anúncios parecidos do mercado.
// "Parecido" = mesma espécie e shiny, mesma Potência e um VALOR próximo, onde o valor (0–10) junta
// Qualidade e os IVs que importam para aquela espécie: atacante físico valoriza IV de Ataque,
// especial valoriza Ataque Especial; Velocidade e HP pesam para todos.
// O perfil (físico/especial/misto) vem dos status base da espécie (PokeAPI, uma vez por espécie,
// guardado em cache) e pode ser trocado à mão no painel.
// RMT (Gemas) só aceita Pokémon com nota a partir de 3,5.
// Aprende com as listas do mercado que a página recebe; "Buscar no mercado" faz UMA busca daquela
// espécie quando você clica. O mesmo arquivo serve o app e a extensão do Chrome.
(() => {
  'use strict';
  if (window.top !== window || window.__tiPreco) return;
  if (!/(^|\.)pokeidle\.io$/.test(location.hostname) && location.hostname !== 'localhost') return;
  window.__tiPreco = true;

  const NOTA_MIN_RMT = 3.5;
  const KEY = 'ti-preco-anuncios';
  const KEY_PERFIS = 'ti-preco-perfis';
  const MAX_ANUNCIOS = 4000;
  const VALIDADE_MS = 14 * 24 * 3600 * 1000; // anúncios vistos há mais de 14 dias não contam
  const IV_MAX = 32; // no PokéIdle cada IV vai de 1 a 32 (6 a 192 somados)
  const PESO_POT = 1.5; // 1 nível de Potência de diferença "vale" 1,5 ponto de valor

  const num = v => Number(v) || 0;
  const ehGemas = m => ['orb', 'orbs', 'gemas', 'gema', 'gems'].includes(String(m || '').toLowerCase());
  const norm = s => String(s || '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').trim();
  const load = (k, def) => { try { return JSON.parse(localStorage.getItem(k)) ?? def; } catch { return def; } };
  const save = (k, v) => { try { localStorage.setItem(k, JSON.stringify(v)); } catch { } };

  // ------------------------------------------------------------------ IVs, perfil e valor

  /** Classifica o nome de um IV (o jogo pode usar nomes em PT ou EN). */
  function statDe(chave) {
    const k = norm(chave).replace(/[^a-z_]/g, '');
    if (/^(hp|ps|vida)$/.test(k)) return 'hp';
    if (/^(atk|ataque|attack|at)$/.test(k)) return 'atk';
    if (/^(def|defesa|defense)$/.test(k)) return 'def';
    if (/^(spa|spatk|sp_atk|satk|sa|atqesp|atq_esp|ataque_?especial|special_?attack|spattack)$/.test(k)) return 'spa';
    if (/^(sdf|spdef|sp_def|sdef|sd|defesp|def_esp|defesa_?especial|special_?defense|spdefense)$/.test(k)) return 'sdf';
    // No PokéIdle "SPD" é a Velocidade (a Defesa Especial é SP.DEF).
    if (/^(spd|spe|vel|velocidade|speed)$/.test(k)) return 'spe';
    return null;
  }

  const PESOS = {
    fisico:   { atk: 3, spa: 0, spe: 2, hp: 1.5, def: 1, sdf: 1 },
    especial: { atk: 0, spa: 3, spe: 2, hp: 1.5, def: 1, sdf: 1 },
    misto:    { atk: 2, spa: 2, spe: 2, hp: 1.5, def: 1, sdf: 1 },
  };
  const NOME_PERFIL = { fisico: 'físico', especial: 'especial', misto: 'misto' };

  /** Fração (0–1) dos IVs que importam para o perfil. null se o Pokémon não tiver IVs por status. */
  function ivUtil(ivs, perfil) {
    if (!ivs || typeof ivs !== 'object') return null;
    const w = PESOS[perfil] || PESOS.misto;
    let soma = 0, max = 0;
    for (const [k, v] of Object.entries(ivs)) {
      const s = statDe(k);
      if (!s || !w[s]) continue;
      soma += w[s] * Math.min(num(v), IV_MAX);
      max += w[s] * IV_MAX;
    }
    return max > 0 ? soma / max : null;
  }

  /**
   * Qualidade como fração 0–1. No PokéIdle ela vai de 0,8 a 1,8 (1,796 = quase a melhor possível).
   * Aceita também 800–1800 (milésimos) e 0–100 (porcentagem), por segurança.
   */
  function qualFrac(q) {
    if (q == null || q === '' || isNaN(Number(q))) return null;
    q = Number(q);
    if (q >= 800) q = q / 1000;                      // 1796 → 1,796
    else if (q > 2) return Math.min(1, q / 100);     // porcentagem
    return Math.min(1, Math.max(0, q - 0.8));        // 0,8 → 0 · 1,8 → 1
  }

  /**
   * Nota 0–10 do jogo (dentro da espécie; "4283" vira 4,283). Sem a nota do jogo, uma aproximação
   * por IVs + Qualidade, só para ordenar.
   */
  function notaDe(p) {
    let n = p.nota ?? p.ficha?.nota;
    if (n != null && !isNaN(Number(n))) { n = Number(n); return n > 10 ? n / 1000 : n; }
    const ivs = p.ivs || p.ficha?.ivs;
    const ivTot = ivs && typeof ivs === 'object' ? Object.values(ivs).reduce((s, v) => s + num(v), 0) : num(p.iv ?? p.ivTotal);
    const q = qualFrac(p.qualidade ?? p.quality ?? p.ficha?.qualidade ?? p.ficha?.quality);
    if (!ivTot && q == null) return null;
    return Math.round(((ivTot / 192) * 5 + (q ?? 0) * 5) * 10) / 10;
  }

  /**
   * Valor 0–10 usado para comparar: 60% IVs úteis + 40% Qualidade.
   * Sem IVs por status, cai para a nota do jogo (e marca como aproximado).
   */
  function valorDe(p) {
    const perfil = perfilDe(p.nome);
    const u = ivUtil(p.ivs, perfil);
    const q = qualFrac(p.qual);
    if (u != null && q != null) return { v: 10 * (0.6 * u + 0.4 * q), aprox: false, u, perfil };
    if (u != null) return { v: 10 * u, aprox: true, u, perfil };
    if (p.nota != null) return { v: p.nota, aprox: true, u: null, perfil };
    return { v: null, aprox: true, u: null, perfil };
  }

  // Perfis por espécie: { slug: { atk, spa, manual } }
  const perfis = load(KEY_PERFIS, {});
  const buscando = new Set();
  const slug = nome => norm(nome)
    .replace(/♀/g, '-f').replace(/♂/g, '-m')
    .replace(/^mega (.+?)( ([xy]))?$/, (_, n, _2, xy) => `${n}-mega${xy ? '-' + xy : ''}`)
    .replace(/[.'’:]/g, '').replace(/\s+/g, '-');

  const perfilPorStatus = (atk, spa) => atk >= spa * 1.15 ? 'fisico' : spa >= atk * 1.15 ? 'especial' : 'misto';

  // Status base do CATÁLOGO DO JOGO (os mesmos arquivos públicos que a página já baixa). É a fonte
  // preferida: são os números do PokéIdle. As Megas não estão nele como espécie: para elas vale a
  // PokeAPI (charizard-mega-y etc.) e, por último, a forma base do catálogo.
  const catalogo = new Map(); // nome normalizado → { atk, spa }
  let catalogoPedido = false;
  function carregarCatalogo() {
    if (catalogoPedido) return;
    catalogoPedido = true;
    const arquivos = ['/assets/creatures.json', '/assets/creatures-novos.json', '/assets/creatures-outland-novos.json'];
    Promise.all(arquivos.map(u => fetch(u).then(r => (r.ok ? r.json() : null)).catch(() => null))).then(lista => {
      for (const j of lista) for (const c of j?.creatures ?? []) {
        if (c?.name && c.baseAtk != null && c.baseSpAtk != null) catalogo.set(norm(c.name), { atk: num(c.baseAtk), spa: num(c.baseSpAtk) });
      }
      if (catalogo.size) redesenharTudo();
    });
  }

  function perfilDe(nome) {
    const s = slug(nome);
    const p = perfis[s];
    if (p?.manual) return p.manual;
    carregarCatalogo();
    const c = catalogo.get(norm(nome));
    if (c) return perfilPorStatus(c.atk, c.spa);
    if (p?.atk && p?.spa) return perfilPorStatus(p.atk, p.spa);
    if (!p && s && !buscando.has(s)) buscarStatusBase(s);
    // Sem nada: a forma base no catálogo ("Mega Charizard Y" → "Charizard").
    const base = catalogo.get(norm(nome).replace(/^(mega|brave|shadow|alolan|galarian)\s+/, '').replace(/\s+[xy]$/, ''));
    return base ? perfilPorStatus(base.atk, base.spa) : 'misto';
  }

  let filaPokeApi = Promise.resolve();
  function buscarStatusBase(s) {
    buscando.add(s);
    filaPokeApi = filaPokeApi.then(async () => {
      try {
        const r = await fetch(`https://pokeapi.co/api/v2/pokemon/${encodeURIComponent(s)}`);
        if (!r.ok) { perfis[s] = { erro: r.status }; return; }
        const j = await r.json();
        const st = n => j.stats?.find(x => x.stat?.name === n)?.base_stat || 0;
        perfis[s] = { atk: st('attack'), spa: st('special-attack') };
      } catch { perfis[s] = { erro: 'rede' }; }
      save(KEY_PERFIS, perfis);
      redesenharTudo();
      await new Promise(ok => setTimeout(ok, 400)); // devagar com a PokeAPI
    });
  }

  function definirPerfil(nome, perfil) {
    const s = slug(nome);
    perfis[s] = { ...(perfis[s] || {}), manual: perfil || undefined };
    save(KEY_PERFIS, perfis);
  }

  // ------------------------------------------------------------------ dados: anúncios e meus Pokémon

  function pokemonDe(a) {
    const f = a.ficha || {};
    return {
      nome: String(a.pokemonNome || f.nome || a.nome || a.especie || ''),
      shiny: !!(a.shiny || a.isShiny || a.lookShiny === 1 || f.shiny || f.isShiny || f.lookShiny === 1),
      pot: num(a.potencia ?? f.potencia) || 1,
      qual: (a.qualidade ?? a.quality ?? f.qualidade ?? f.quality) != null ? num(a.qualidade ?? a.quality ?? f.qualidade ?? f.quality) : null,
      ivs: (a.ivs && typeof a.ivs === 'object') ? a.ivs : (f.ivs && typeof f.ivs === 'object') ? f.ivs : null,
      nota: notaDe(a),
    };
  }

  let base = load(KEY, []).filter(a => Date.now() - a.em < VALIDADE_MS);
  let salvarPendente = false;
  function salvar() {
    if (salvarPendente) return;
    salvarPendente = true;
    setTimeout(() => {
      salvarPendente = false;
      if (base.length > MAX_ANUNCIOS) base = base.slice(-MAX_ANUNCIOS);
      save(KEY, base);
    }, 2000);
  }

  function aprender(a) {
    const preco = num(a.preco);
    if (!a.id || preco <= 0 || a.itemId || a.itemNome || a.tipo === 'item') return;
    if (a.comprador || (a.estado && a.estado !== 'aberto')) return;
    const p = pokemonDe(a);
    if (!p.nome) return;
    const reg = { id: num(a.id), esp: norm(p.nome), ...p, preco, gemas: ehGemas(a.moeda), em: Date.now() };
    const i = base.findIndex(b => b.id === reg.id);
    if (i >= 0) base[i] = reg; else base.push(reg);
  }

  let meus = [];
  function lerMeus(m) {
    const est = m.estado || m.payload?.estado || (m.t === 'estado' ? m : null);
    const lista = (est && Array.isArray(est.pokemons)) ? est.pokemons
      : Array.isArray(m.pokemons) ? m.pokemons
      : Array.isArray(m.depot) ? m.depot : null;
    if (lista && lista.length) {
      meus = lista.filter(p => p && (p.nome || p.especie)).map(pokemonDe);
      redesenharTudo();
    }
  }

  // ------------------------------------------------------------------ WebSocket do jogo

  let ws = null;
  const Original = window.WebSocket;
  window.WebSocket = new Proxy(Original, {
    construct(target, args) {
      const s = Reflect.construct(target, args);
      try {
        if (new URL(String(args[0]), location.href).host === location.host) {
          ws = s;
          s.addEventListener('message', ev => { if (typeof ev.data === 'string') onMensagem(ev.data); });
        }
      } catch { }
      return s;
    },
  });

  function onMensagem(texto) {
    if (!texto.includes('"market"') && !texto.includes('pokemons') && !texto.includes('"depot"')) return;
    let m;
    try { m = JSON.parse(texto); } catch { return; }
    lerMeus(m);
    if (m.t === 'market' && !String(m.aba || '').startsWith('historico') && m.aba !== 'meus') {
      const lista = [].concat(m.anuncios || [], m.linhas || [], m.anuncio ? [m.anuncio] : []);
      let n = 0;
      for (const a of lista) if (a) { aprender(a); n++; }
      if (n) { salvar(); redesenharTudo(); }
    }
  }

  let ultimaBusca = 0;
  function buscarNoMercado(nome) {
    // Só quando você clica, no máximo uma busca a cada 3 s (são 2 pedidos: Gemas e ouro).
    if (!ws || ws.readyState !== 1 || Date.now() - ultimaBusca < 3000) return false;
    ultimaBusca = Date.now();
    for (const moeda of ['orb', 'gold'])
      ws.send(JSON.stringify({ t: 'market.listar', tipo: 'pokemon', ordem: 'menor_preco', moeda, busca: nome, criterios: [], elemento: '' }));
    return true;
  }

  // ------------------------------------------------------------------ estimativa

  function quantil(ordenados, q) {
    if (!ordenados.length) return 0;
    const pos = (ordenados.length - 1) * q;
    const a = Math.floor(pos), b = Math.ceil(pos);
    return ordenados[a] + (ordenados[b] - ordenados[a]) * (pos - a);
  }
  const arred = (v, gemas) => gemas ? Math.round(v * 10) / 10 : Math.round(v);

  /** Preço numa moeda a partir dos anúncios parecidos. Retorna { preco, de, ate, menor, n, confianca, base } ou { semDados }. */
  function estimar(alvo, gemas) {
    const esp = norm(alvo.nome);
    const va = valorDe(alvo);
    if (va.v == null) return { semDados: true, motivo: 'sem IVs/Qualidade/nota para comparar' };
    const dist = b => {
      const vb = valorDe(b);
      if (vb.v == null) return Infinity;
      return Math.abs(vb.v - va.v) + Math.abs(b.pot - alvo.pot) * PESO_POT + (vb.aprox || va.aprox ? 0.4 : 0);
    };
    const pool = base.filter(b => b.gemas === gemas && b.shiny === !!alvo.shiny);

    let comp = pool.filter(b => b.esp === esp).map(b => ({ b, d: dist(b) })).filter(x => x.d <= 3);
    let origem = 'mesma espécie';
    if (comp.length < 2) {
      // Sem a espécie: Pokémon de qualquer espécie com a mesma Potência e valor bem próximo.
      comp = pool.filter(b => b.pot === alvo.pot).map(b => ({ b, d: dist(b) + 1 })).filter(x => x.d <= 2);
      origem = 'outras espécies parecidas';
    }
    if (!comp.length) return { semDados: true };
    comp.sort((x, y) => x.d - y.d);
    comp = comp.slice(0, 10);

    // Mediana ponderada: os mais parecidos pesam mais.
    const pesos = comp.map(x => ({ p: x.b.preco, w: 1 / (0.3 + x.d) })).sort((a, b) => a.p - b.p);
    const total = pesos.reduce((s, x) => s + x.w, 0);
    let acc = 0, mediana = pesos[0].p;
    for (const x of pesos) { acc += x.w; if (acc >= total / 2) { mediana = x.p; break; } }

    const precos = pesos.map(x => x.p);
    const dMedia = comp.reduce((s, x) => s + x.d, 0) / comp.length;
    const confianca = origem !== 'mesma espécie' || comp.length < 3 ? 'baixa'
      : comp.length >= 6 && dMedia < 1 ? 'alta' : 'média';
    return {
      preco: arred(mediana, gemas), de: arred(quantil(precos, 0.25), gemas), ate: arred(quantil(precos, 0.75), gemas),
      menor: arred(precos[0], gemas), n: comp.length, confianca, base: origem,
    };
  }

  /** Avaliação completa: valor, perfil, se pode ir ao RMT e os preços. */
  function avaliar(alvo) {
    const a = { pot: 1, ...alvo, pot: num(alvo.pot) || 1 };
    const va = valorDe(a);
    const podeRmt = (a.nota ?? notaDe(a) ?? 0) >= NOTA_MIN_RMT;
    return {
      valor: va.v == null ? null : Math.round(va.v * 10) / 10,
      ivUtil: va.u == null ? null : Math.round(va.u * 100),
      perfil: va.perfil,
      podeRmt,
      gemas: podeRmt ? estimar(a, true) : null,
      ouro: estimar(a, false),
    };
  }

  // ------------------------------------------------------------------ interface (peças em ti-ui.js)

  const UI = () => window.__tiUI;
  const PAINEL = 'ti-prc';
  let painelAberto = false;
  let manual = { nome: '', shiny: false, pot: 1, qual: 1.3, nota: 4, ivs: { hp: 16, atk: 16, def: 16, spAtk: 16, spDef: 16, speed: 16 } };
  let calculouManual = false;
  const porChave = new Map(); // chave do cartão → Pokémon (para os botões dos cartões)

  const dec = (n, casas = 2) => n == null ? '?' : n.toLocaleString('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: casas });
  const fmtPreco = (v, gemas) => gemas ? v.toLocaleString('pt-BR', { maximumFractionDigits: 1 }) : Math.round(v).toLocaleString('pt-BR');

  function caixaPreco(e, gemas, bloqueio) {
    const classe = gemas ? 'gem' : 'gold';
    const rotulo = gemas ? '💎 RMT · Gemas' : 'Ouro';
    if (bloqueio) return `<div class="${classe}"><small>${rotulo}</small><span class="v" style="font-size:14px;color:#ff8a95">Não pode</span><em>${bloqueio}</em></div>`;
    if (!e || e.semDados) return `<div class="${classe}"><small>${rotulo}</small><span class="v" style="font-size:14px;color:#9e99b3">Sem dados</span><em>Clique em "Buscar no mercado".</em></div>`;
    return `<div class="${classe}"><small>${rotulo}</small><span class="v">${fmtPreco(e.preco, gemas)}</span>
      <em>${fmtPreco(e.de, gemas)} – ${fmtPreco(e.ate, gemas)} · menor ${fmtPreco(e.menor, gemas)}<br>${e.n} anúncio(s) · confiança ${e.confianca}</em></div>`;
  }

  /** O cartão de resultado (usado no painel e na ficha do jogo). */
  function cartaoResultado(p, chave) {
    const { esc, opcoes } = UI();
    porChave.set(String(chave), p);
    const r = avaliar(p);
    const manualPerfil = perfis[slug(p.nome)]?.manual || '';
    return `
      <div class="ti-card prc-res" data-chave="${esc(chave)}">
        <div class="ti-row">
          <b class="ti-grow" style="font-size:14px">${esc(p.nome || '—')}${p.shiny ? ' ✨' : ''}</b>
          <span class="ti-chip roxo">P${p.pot}</span>
          <span class="ti-chip">nota ${dec(p.nota, 3)}</span>
          ${r.podeRmt ? '<span class="ti-chip ok">✓ pode RMT</span>' : '<span class="ti-chip no">sem RMT</span>'}
        </div>
        <div class="ti-row ti-muted" style="margin-top:6px">
          <span>Valor <b style="color:#fff">${dec(r.valor, 1)}</b>/10</span>·
          <span>IVs úteis ${r.ivUtil == null ? '?' : r.ivUtil + '%'}</span>·
          <span>Qualidade ${p.qual == null ? '?' : dec(p.qual, 3)}</span>
          <span class="ti-grow"></span>
          <select class="ti-in" data-perfil style="width:auto;height:26px;font-size:11.5px" title="O que esta espécie mais usa. Automático pelos status base do jogo.">
            ${opcoes(manualPerfil, [['', `Auto: ${NOME_PERFIL[r.perfil]}`], ['fisico', 'Físico (ATK)'], ['especial', 'Especial (SP.ATK)'], ['misto', 'Misto']])}
          </select>
        </div>
        <div class="ti-bar"><i style="width:${Math.max(0, Math.min(100, (r.valor ?? 0) * 10))}%"></i></div>
        <div class="ti-price">
          ${caixaPreco(r.gemas, true, r.podeRmt ? '' : `Nota ${dec(p.nota, 3)}; o RMT pede ${dec(NOTA_MIN_RMT, 1)} ou mais.`)}
          ${caixaPreco(r.ouro, false)}
        </div>
        <div class="ti-row" style="margin-top:10px">
          <button class="ti-btn sm" data-busca>🔎 Buscar no mercado</button>
          <span class="ti-muted ti-grow" style="text-align:right">Preços pedidos em anúncios parecidos</span>
        </div>
      </div>`;
  }

  /** Liga os botões dos cartões de resultado dentro de um container. */
  function ligarCartoes(container) {
    container.querySelectorAll('.prc-res').forEach(card => {
      const p = porChave.get(card.dataset.chave);
      if (!p) return;
      card.querySelector('[data-perfil]').onchange = e => { definirPerfil(p.nome, e.target.value); redesenharTudo(true); };
      card.querySelector('[data-busca]').onclick = e => {
        e.target.textContent = buscarNoMercado(p.nome) ? 'Buscando…' : 'Aguarde uns segundos…';
        setTimeout(() => redesenharTudo(true), 2500);
      };
    });
  }

  function montar() {
    if (!UI() || !document.body) return;
    UI().pill('ti-prc-btn', 'Calculadora de preço: quanto vale o seu Pokémon no RMT e em ouro', () => {
      painelAberto = !painelAberto;
      desenhar();
    }).innerHTML = '💰 Preço';
    document.getElementById('ti-prc-btn')?.classList.toggle('ativo', painelAberto);
  }

  function desenhar() {
    if (!UI()) return;
    document.getElementById('ti-prc-btn')?.classList.toggle('ativo', painelAberto);
    if (!painelAberto) { UI().fecharPainel(PAINEL); return; }
    const { esc } = UI();
    const p = UI().painel(PAINEL, { icone: '💰', titulo: 'Calculadora de preço', sub: 'Quanto vale no RMT e em ouro' },
      () => { painelAberto = false; desenhar(); });
    const lista = [...meus].sort((a, b) => (avaliar(b).valor ?? 0) - (avaliar(a).valor ?? 0)).slice(0, 30);
    const iv = ([k, rot]) => `<label class="ti-field">${rot}<input class="ti-in" data-iv="${k}" type="number" min="1" max="32" value="${manual.ivs[k] ?? 16}"></label>`;
    p.body.innerHTML = `
      <div class="ti-muted" style="margin-top:12px">O valor junta <b>IVs</b> (os que a espécie usa), <b>Qualidade</b> e <b>Potência</b>,
        e o preço vem de anúncios parecidos. 💡 Também dá para calcular direto na ficha de qualquer Pokémon: botão <b>💰 Calcular preço</b>.</div>

      <div class="ti-sec"><span>Calcular um Pokémon</span></div>
      <div class="ti-card" id="ti-prc-form">
        <label class="ti-field">Espécie<input class="ti-in" id="ti-prc-nome" placeholder="Ex.: Mega Charizard Y" value="${esc(manual.nome)}"></label>
        <div class="ti-grid">
          <label class="ti-field">Potência<select class="ti-in" id="ti-prc-pot">${UI().opcoes(manual.pot, [[1, 'P1'], [2, 'P2'], [3, 'P3'], [4, 'P4'], [5, 'P5']])}</select></label>
          <label class="ti-field">Qualidade (0,8–1,8)<input class="ti-in" id="ti-prc-qual" type="number" min="0.8" max="1.8" step="0.001" value="${manual.qual}"></label>
          <label class="ti-field">Nota (0–10)<input class="ti-in" id="ti-prc-nota" type="number" min="0" max="10" step="0.001" value="${manual.nota}"></label>
        </div>
        <div class="ti-grid g6">${[['hp', 'HP'], ['atk', 'ATK'], ['def', 'DEF'], ['spAtk', 'SP.ATK'], ['spDef', 'SP.DEF'], ['speed', 'SPD']].map(iv).join('')}</div>
        <div class="ti-row" style="margin-top:10px">
          <label class="ti-switch"><input type="checkbox" id="ti-prc-shiny" ${manual.shiny ? 'checked' : ''}>Shiny</label>
          <span class="ti-grow"></span>
          <button class="ti-btn primary" id="ti-prc-calc">Calcular</button>
        </div>
      </div>
      ${calculouManual && manual.nome ? cartaoResultado(manual, 'm') : ''}

      <div class="ti-sec"><span>Meus Pokémon</span><span>${lista.length ? 'melhores primeiro' : ''}</span></div>
      ${lista.length ? lista.map((pk, i) => cartaoResultado(pk, `l${i}`)).join('')
        : '<div class="ti-empty">Ainda não recebi a lista dos seus Pokémon.<br>Abra a tela de Pokémon / depósito no jogo.</div>'}

      <div class="ti-muted" style="margin-top:12px">Base: ${base.length} anúncio(s) vistos nos últimos 14 dias. Quanto mais você navega no mercado, melhor a estimativa.</div>`;

    const q = s => p.body.querySelector(s);
    q('#ti-prc-calc').onclick = () => {
      const ivs = {};
      p.body.querySelectorAll('[data-iv]').forEach(inp => { ivs[inp.dataset.iv] = Math.min(IV_MAX, Math.max(1, num(inp.value))); });
      manual = {
        nome: q('#ti-prc-nome').value.trim(), shiny: q('#ti-prc-shiny').checked,
        pot: Math.min(5, Math.max(1, num(q('#ti-prc-pot').value) || 1)),
        qual: Math.min(1.8, Math.max(0.8, num(String(q('#ti-prc-qual').value).replace(',', '.')))),
        nota: num(String(q('#ti-prc-nota').value).replace(',', '.')), ivs,
      };
      calculouManual = true;
      desenhar();
    };
    ligarCartoes(p.body);
  }

  // ------------------------------------------------------------------ dentro da ficha do Pokémon (jogo)

  // A ficha do jogo (#ficha-corpo) mostra Potência, Qualidade, IV de cada stat e a nota. O botão
  // "💰 Calcular preço" entra junto das ações dela (Calcular, Comparar…) e abre um cartão logo
  // abaixo do topo da ficha, com o mesmo resultado do painel.
  const IV_DA_FICHA = { HP: 'hp', ATK: 'atk', DEF: 'def', 'SP.ATK': 'spAtk', 'SP.DEF': 'spDef', SPD: 'speed' };
  let fichaPk = null;

  function lerFicha(corpo) {
    const txt = el => (el?.textContent || '').replace(/\s+/g, ' ').trim();
    let nome = '';
    for (const b of corpo.querySelectorAll('.fi-placa b')) {
      const m = txt(b).match(/^#[\d.,]+\s+(.+)$/);
      if (m) { nome = m[1]; break; }
    }
    if (!nome) nome = txt(corpo.querySelector('.fi-nome')).replace(/^[^\p{L}]+/u, '');
    const pot = num((txt(corpo.querySelector('.pk-selo-pot b')).match(/P(\d)/) || [])[1]) || 1;
    const qTxt = txt(corpo.querySelector('.pk-selo-q b')).replace(/\./g, '').replace(',', '.');
    const qual = qTxt ? Number(qTxt) : null;
    const notaEl = corpo.querySelector('.fi-nota-num[data-nota]');
    const nota = notaEl ? Number(notaEl.dataset.nota) : null;
    const ivs = {};
    for (const linha of corpo.querySelectorAll('.fi-stat')) {
      const em = linha.querySelector('.fi-iv');
      const k = IV_DA_FICHA[txt(linha.querySelector('.fi-stat-nome')).toUpperCase()];
      if (em && k) ivs[k] = num((txt(em).match(/(\d+)/) || [])[1]);
    }
    const shiny = !!corpo.querySelector('.hf-shiny');
    if (!nome || (!Object.keys(ivs).length && qual == null)) return null;
    return { nome, shiny, pot, qual: Number.isFinite(qual) ? qual : null, nota: Number.isFinite(nota) ? nota : null, ivs: Object.keys(ivs).length ? ivs : null };
  }

  function desenharNaFicha() {
    const corpo = document.getElementById('ficha-corpo');
    const card = document.getElementById('ti-fi-card');
    if (!corpo || !card || !fichaPk || !UI()) return;
    card.innerHTML = `<h3 class="fi-cartao-tit"><span>💰 Preço no mercado</span><em>ThoseIdles</em></h3>${cartaoResultado(fichaPk, 'f')}`;
    ligarCartoes(card);
  }

  function abrirNaFicha() {
    const corpo = document.getElementById('ficha-corpo');
    if (!corpo) return;
    const pk = lerFicha(corpo);
    if (!pk) { UI()?.toast('Calcular preço', 'Esta ficha não mostra IVs nem Qualidade, então não dá para calcular.', 'erro'); return; }
    fichaPk = pk;
    let card = document.getElementById('ti-fi-card');
    if (!card) {
      card = document.createElement('section');
      card.id = 'ti-fi-card';
      card.className = 'fi-cartao ti-fi-card';
      (corpo.querySelector('.fi-heroi') || corpo.firstElementChild)?.after(card);
    }
    desenharNaFicha();
    card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    // Você pediu: já busca os anúncios desta espécie e atualiza o cartão.
    if (buscarNoMercado(pk.nome)) setTimeout(desenharNaFicha, 2500);
  }

  function prepararFicha() {
    const acoes = document.querySelector('#ficha-corpo .fi-acoes');
    if (!acoes || acoes.querySelector('#ti-fi-preco')) return;
    UI()?.injetarCss();
    fichaPk = null;
    const b = document.createElement('button');
    b.type = 'button';
    b.id = 'ti-fi-preco';
    b.className = 'fi-calc fi-acao';
    b.title = 'ThoseIdles: quanto este Pokémon vale no RMT e em ouro';
    b.innerHTML = '<span>💰 Calcular preço</span>';
    b.onclick = abrirNaFicha;
    acoes.prepend(b);
  }

  let fichaAgendada = false;
  new MutationObserver(() => {
    if (fichaAgendada) return;
    fichaAgendada = true;
    requestAnimationFrame(() => { fichaAgendada = false; prepararFicha(); });
  // `document` e não `documentElement`: o script entra antes de a página ter o <html>.
  }).observe(document, { childList: true, subtree: true });

  /** Redesenha o que estiver aberto (painel e cartão da ficha). force = mesmo com foco num campo. */
  function redesenharTudo(force) {
    if (painelAberto && (force || !document.getElementById(PAINEL)?.contains(document.activeElement))) desenhar();
    desenharNaFicha();
  }

  setInterval(montar, 5000);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar); else montar();

  // Para o autoteste: avaliar sem abrir o painel.
  window.__tiPreco = { avaliar, meus: () => meus.slice(), base: () => base.length, definirPerfil, buscar: buscarNoMercado, lerFicha };
})();
