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
  const IV_MAX = 31;
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
    if (/^(spd|spdef|sp_def|sdef|sd|defesp|def_esp|defesa_?especial|special_?defense|spdefense)$/.test(k)) return 'spd';
    if (/^(spe|vel|velocidade|speed|spd_?speed)$/.test(k)) return 'spe';
    return null;
  }

  const PESOS = {
    fisico:   { atk: 3, spa: 0, spe: 2, hp: 1.5, def: 1, spd: 1 },
    especial: { atk: 0, spa: 3, spe: 2, hp: 1.5, def: 1, spd: 1 },
    misto:    { atk: 2, spa: 2, spe: 2, hp: 1.5, def: 1, spd: 1 },
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

  /** Nota na escala 0–10 (a do jogo; "3500" vira 3,5). Sem nota, calcula por IVs + Qualidade. */
  function notaDe(p) {
    let n = p.nota ?? p.ficha?.nota;
    if (n != null && !isNaN(Number(n))) { n = Number(n); return n > 10 ? n / 1000 : n; }
    const ivs = p.ivs || p.ficha?.ivs;
    const ivTot = ivs && typeof ivs === 'object' ? Object.values(ivs).reduce((s, v) => s + num(v), 0) : num(p.iv ?? p.ivTotal);
    const q = num(p.qualidade ?? p.quality ?? p.ficha?.qualidade ?? p.ficha?.quality);
    if (!ivTot && !q) return null;
    return Math.round(((ivTot / 192) * 5 + (q / 100) * 5) * 10) / 10;
  }

  /**
   * Valor 0–10 usado para comparar: 60% IVs úteis + 40% Qualidade.
   * Sem IVs por status, cai para a nota do jogo (e marca como aproximado).
   */
  function valorDe(p) {
    const perfil = perfilDe(p.nome);
    const u = ivUtil(p.ivs, perfil);
    const q = p.qual;
    if (u != null && q != null) return { v: 10 * (0.6 * u + 0.4 * q / 100), aprox: false, u, perfil };
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

  function perfilDe(nome) {
    const s = slug(nome);
    const p = perfis[s];
    if (p?.manual) return p.manual;
    if (p?.atk && p?.spa) return p.atk >= p.spa * 1.15 ? 'fisico' : p.spa >= p.atk * 1.15 ? 'especial' : 'misto';
    if (!p && s && !buscando.has(s)) buscarStatusBase(s);
    return 'misto';
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
      if (painelAberto) desenhar();
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
      if (painelAberto) desenhar();
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
      if (n) { salvar(); if (painelAberto) desenhar(); }
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

  // ------------------------------------------------------------------ interface

  const css = `
    #ti-prc-btn{background:#1a2420dd;color:#eee;border:1px solid #3f7a5a;border-radius:14px;font:600 12px Segoe UI,sans-serif;
      padding:4px 10px;cursor:pointer;opacity:.85} #ti-prc-btn:hover{opacity:1}
    #ti-prc{position:fixed;left:8px;bottom:40px;z-index:2147483000;width:450px;max-height:75vh;overflow:auto;background:#141b18f5;
      color:#ddd;border:1px solid #3f7a5a;border-radius:10px;font:12px Segoe UI,sans-serif;padding:10px 12px;box-shadow:0 6px 20px #0009}
    #ti-prc .t{font-size:13px;font-weight:600} #ti-prc h4{margin:10px 0 4px;font-size:12px;color:#8fe0b0}
    #ti-prc button{background:#203a2c;color:#ddd;border:1px solid #3f7a5a;border-radius:5px;font:11px Segoe UI,sans-serif;padding:3px 8px;cursor:pointer}
    #ti-prc input,#ti-prc select{background:#0d1210;color:#ddd;border:1px solid #3f7a5a;border-radius:4px;font:11px Segoe UI,sans-serif;padding:2px 4px}
    #ti-prc .muted{color:#8aa597} #ti-prc .no{color:#ff9c9c}
    #ti-prc .card{background:#1b2621;border-radius:6px;padding:6px 8px;margin:4px 0}
    #ti-prc .preco{font-size:14px;font-weight:700;color:#fff}`;

  let painelAberto = false;
  let painel = null;
  let manual = { nome: '', shiny: false, pot: 1, qual: 80, nota: 4, ivs: { atk: 25, spa: 25, spe: 25, hp: 25, def: 25, spd: 25 } };

  function dock() {
    let d = document.getElementById('ti-dock');
    if (!d) { d = document.createElement('div'); d.id = 'ti-dock'; d.style.cssText = 'position:fixed;left:8px;bottom:8px;z-index:2147483000;display:flex;gap:6px'; document.body.appendChild(d); }
    return d;
  }

  function montar() {
    if (document.getElementById('ti-prc-btn') || !document.body) return;
    const s = document.createElement('style'); s.textContent = css; document.head.appendChild(s);
    const btn = document.createElement('button');
    btn.id = 'ti-prc-btn';
    btn.textContent = '💰 Preço';
    btn.title = 'ThoseIdles: quanto vale o seu Pokémon no mercado e no RMT';
    btn.onclick = () => { painelAberto = !painelAberto; desenhar(); };
    dock().appendChild(btn);
  }

  const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const fmt = (v, gemas) => gemas ? `${v.toLocaleString('pt-BR', { maximumFractionDigits: 1 })} Gemas` : `${Math.round(v).toLocaleString('pt-BR')} ouro`;
  const dec = n => n == null ? '?' : n.toLocaleString('pt-BR', { minimumFractionDigits: 1, maximumFractionDigits: 2 });

  function blocoPreco(e, gemas) {
    if (!e) return '';
    if (e.semDados) return `<div class="muted">${gemas ? 'RMT' : 'Ouro'}: sem anúncios parecidos ainda — clique em "Buscar no mercado".</div>`;
    return `<div>${gemas ? 'RMT' : 'Ouro'}: <span class="preco">${fmt(e.preco, gemas)}</span>
      <span class="muted"> (faixa ${fmt(e.de, gemas)} – ${fmt(e.ate, gemas)} · menor ${fmt(e.menor, gemas)} · ${e.n} anúncio(s) de ${e.base} · confiança ${e.confianca})</span></div>`;
  }

  function cartao(p, i) {
    const r = avaliar(p);
    const perfilSel = `<select data-perfil="${i}" title="O que esta espécie mais usa (automático pelos status base)">
      ${['', 'fisico', 'especial', 'misto'].map(v => `<option value="${v}" ${(perfis[slug(p.nome)]?.manual || '') === v ? 'selected' : ''}>${v ? NOME_PERFIL[v] : `auto (${NOME_PERFIL[r.perfil]})`}</option>`).join('')}
    </select>`;
    return `<div class="card">
      <div><b>${esc(p.nome)}</b>${p.shiny ? ' ✨' : ''} · P${p.pot} <button data-busca="${i}" style="float:right">Buscar no mercado</button></div>
      <div class="muted">Valor <b style="color:#fff">${dec(r.valor)}/10</b> ·
        IVs úteis ${r.ivUtil == null ? '?' : r.ivUtil + '%'} · Qualidade ${p.qual ?? '?'} · nota ${dec(p.nota)} · perfil ${perfilSel}</div>
      ${r.podeRmt ? blocoPreco(r.gemas, true) : `<div class="no">Não pode ir para o RMT: nota ${dec(p.nota)} (mínimo ${dec(NOTA_MIN_RMT)}).</div>`}
      ${blocoPreco(r.ouro, false)}
    </div>`;
  }

  function desenhar() {
    if (!painelAberto) { painel?.remove(); painel = null; return; }
    if (!painel) { painel = document.createElement('div'); painel.id = 'ti-prc'; document.body.appendChild(painel); }
    const lista = [...meus].sort((a, b) => (avaliar(b).valor ?? 0) - (avaliar(a).valor ?? 0)).slice(0, 30);
    const iv = k => `<label>${k.toUpperCase()} <input data-iv="${k}" type="number" min="0" max="31" value="${manual.ivs[k] ?? 0}" style="width:38px"></label>`;
    painel.innerHTML = `
      <div class="t">Calculadora de preço</div>
      <div class="muted">O valor junta <b>IVs</b> (os que a espécie usa: físico → Ataque, especial → Atq. Especial; Velocidade e HP contam para todos),
        <b>Qualidade</b> e <b>Potência</b>, e compara com anúncios parecidos. São preços <b>pedidos</b>, não vendas fechadas.
        RMT só a partir de nota ${dec(NOTA_MIN_RMT)}.</div>

      <h4>Calcular um Pokémon</h4>
      <div class="card" id="ti-prc-form">
        <input id="ti-prc-nome" placeholder="espécie (ex.: Charizard)" value="${esc(manual.nome)}" style="width:130px">
        <label><input type="checkbox" id="ti-prc-shiny" ${manual.shiny ? 'checked' : ''}> shiny</label>
        P <input id="ti-prc-pot" type="number" min="1" max="5" value="${manual.pot}" style="width:34px">
        Qualidade <input id="ti-prc-qual" type="number" min="0" max="100" value="${manual.qual}" style="width:42px">
        nota <input id="ti-prc-nota" type="number" step="0.1" min="0" max="10" value="${manual.nota}" style="width:46px">
        <div style="margin-top:4px">IVs: ${['hp', 'atk', 'def', 'spa', 'spd', 'spe'].map(iv).join(' ')}</div>
        <button id="ti-prc-calc" style="margin-top:4px">Calcular</button>
        <div style="margin-top:4px">${manual.nome ? cartao(manual, 'm') : ''}</div>
      </div>

      <h4>Meus Pokémon (melhores primeiro)</h4>
      ${lista.length ? lista.map(cartao).join('') : '<div class="muted">Ainda não recebi a lista dos seus Pokémon. Abra a tela de Pokémon/depósito no jogo.</div>'}

      <div class="muted" style="margin-top:8px">Base: ${base.length} anúncio(s) vistos nos últimos 14 dias. Quanto mais você navega no mercado, melhor a estimativa.</div>`;

    painel.querySelector('#ti-prc-calc').onclick = () => {
      const q = sel => painel.querySelector(sel);
      const ivs = {};
      painel.querySelectorAll('[data-iv]').forEach(inp => { ivs[inp.dataset.iv] = Math.min(IV_MAX, Math.max(0, num(inp.value))); });
      manual = {
        nome: q('#ti-prc-nome').value.trim(), shiny: q('#ti-prc-shiny').checked,
        pot: Math.min(5, Math.max(1, num(q('#ti-prc-pot').value) || 1)),
        qual: Math.min(100, Math.max(0, num(q('#ti-prc-qual').value))),
        nota: num(String(q('#ti-prc-nota').value).replace(',', '.')), ivs,
      };
      desenhar();
    };
    painel.querySelectorAll('[data-perfil]').forEach(s => s.onchange = () => {
      const p = s.dataset.perfil === 'm' ? manual : lista[+s.dataset.perfil];
      definirPerfil(p.nome, s.value);
      desenhar();
    });
    painel.querySelectorAll('[data-busca]').forEach(b => b.onclick = () => {
      const p = b.dataset.busca === 'm' ? manual : lista[+b.dataset.busca];
      b.textContent = buscarNoMercado(p.nome) ? 'Buscando…' : 'Aguarde…';
      setTimeout(() => painelAberto && desenhar(), 2500);
    });
  }

  setInterval(montar, 5000);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar); else montar();

  // Para o autoteste: avaliar sem abrir o painel.
  window.__tiPreco = { avaliar, meus: () => meus.slice(), base: () => base.length, definirPerfil, buscar: buscarNoMercado };
})();
