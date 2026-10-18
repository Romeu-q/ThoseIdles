// ThoseIdles — Painel de farm + alerta de mercado para o PokéIdle.
// Só LÊ as mensagens que o jogo já manda para a página: nunca envia nada ao servidor,
// não compra, não consulta o mercado sozinho. O mesmo arquivo serve o app e a extensão do Chrome.
(() => {
  'use strict';
  if (window.top !== window || window.__tiFarmAssist) return;
  window.__tiFarmAssist = true;

  const KEY_STATS = 'ti-farm-stats';
  const KEY_RULES = 'ti-mercado-regras';
  const MAX_ALERTAS = 30;

  // ------------------------------------------------------------------ armazenamento

  const load = (k, def) => { try { return JSON.parse(localStorage.getItem(k)) ?? def; } catch { return def; } };
  const save = (k, v) => { try { localStorage.setItem(k, JSON.stringify(v)); } catch { } };
  const novaSessao = () => ({ inicio: Date.now(), kills: 0, capturas: 0, shinies: 0, xp: 0, ouro: 0, drops: {} });

  let stats = load(KEY_STATS, null) || novaSessao();
  let regras = load(KEY_RULES, []);
  const alertas = [];
  const jaAlertados = new Set();
  let salvarPendente = false;
  const salvarStats = () => {
    if (salvarPendente) return;
    salvarPendente = true;
    setTimeout(() => { salvarPendente = false; save(KEY_STATS, stats); }, 2000);
  };

  // ------------------------------------------------------------------ escuta do WebSocket (só recebe)

  const Original = window.WebSocket;
  window.WebSocket = new Proxy(Original, {
    construct(target, args) {
      const ws = Reflect.construct(target, args);
      try {
        if (new URL(String(args[0]), location.href).host === location.host)
          ws.addEventListener('message', ev => { if (typeof ev.data === 'string') onMensagem(ev.data); });
      } catch { }
      return ws;
    },
  });

  function onMensagem(texto) {
    // Filtro barato antes do JSON.parse: só interessam batalha e mercado.
    if (!texto.includes('"t":"batalha"') && !texto.includes('"t":"market"')) return;
    let m;
    try { m = JSON.parse(texto); } catch { return; }
    if (m.t === 'batalha' && Array.isArray(m.ev)) onBatalha(m.ev);
    else if (m.t === 'market') onMercado(m);
  }

  // ------------------------------------------------------------------ telemetria de farm

  const num = v => Number(v) || 0;

  function somaDrop(nome, qtd) {
    if (!nome) return;
    stats.drops[nome] = (stats.drops[nome] || 0) + qtd;
  }

  function onBatalha(eventos) {
    let mudou = false;
    for (const e of eventos) {
      if (!e) continue;
      const k = String(e.k || e.t || '');
      if (k === 'morte' || k === 'kill' || k === 'derrota' || k === 'monstro_morto') {
        stats.kills++;
        stats.xp += num(e.xp);
        stats.ouro += num(e.ouro ?? e.gold);
        if (Array.isArray(e.drops))
          for (const d of e.drops) if (d) somaDrop(d.nome || d.name || (d.itemId ? `Item #${d.itemId}` : ''), num(d.qtd ?? d.quantidade) || 1);
        mudou = true;
      } else if (k === 'capturado' || k === 'captura' || k === 'catch' || (k === 'bola' && (e.sucesso === true || e.sucesso === 1))) {
        stats.capturas++;
        if (e.shiny || e.isShiny || e.pokemon?.shiny) stats.shinies++;
        mudou = true;
      } else if (k === 'xp' && e.tipo !== 'treinador' && e.dest !== 'treinador') {
        stats.xp += num(e.qtd ?? e.valor ?? e.xp); mudou = true;
      } else if (k === 'gold' || k === 'ouro') {
        stats.ouro += num(e.qtd ?? e.valor ?? e.gold ?? e.ouro); mudou = true;
      } else if (k === 'drop' || k === 'item') {
        somaDrop(e.nome || e.itemNome || (e.itemId ? `Item #${e.itemId}` : ''), num(e.qtd ?? e.quantidade) || 1);
        mudou = true;
      }
    }
    if (mudou) { salvarStats(); if (painelAberto) desenhar(); }
  }

  // ------------------------------------------------------------------ alerta de mercado

  function anuncioInfo(a) {
    const f = a.ficha || {};
    return {
      id: a.id,
      nome: String(a.pokemonNome || f.nome || a.itemNome || a.nome || ''),
      shiny: !!(a.shiny || a.isShiny || a.lookShiny === 1 || f.shiny || f.isShiny || f.lookShiny === 1),
      potencia: num(a.potencia ?? f.potencia),
      preco: num(a.preco),
      moeda: String(a.moeda || 'gold').toLowerCase(),
      vendedor: String(a.vendedor || a.vendedorNome || ''),
      aberto: !a.comprador && (!a.estado || a.estado === 'aberto'),
    };
  }

  function bate(r, x) {
    if (!r.ativa || !x.aberto || !x.nome) return false;
    if (r.texto && !x.nome.toLowerCase().includes(r.texto.toLowerCase())) return false;
    if (r.soShiny && !x.shiny) return false;
    if (r.potMin > 0 && x.potencia < r.potMin) return false;
    if (r.moeda && x.moeda !== r.moeda) return false;
    if (r.precoMax > 0 && (x.preco <= 0 || x.preco > r.precoMax)) return false;
    return true;
  }

  function onMercado(m) {
    if (String(m.aba || '').startsWith('historico') || m.aba === 'meus') return;
    const lista = [].concat(m.anuncios || [], m.linhas || [], m.anuncio ? [m.anuncio] : []);
    const ativas = regras.filter(r => r.ativa);
    if (!ativas.length) return;
    for (const a of lista) {
      if (!a || a.id == null) continue;
      const x = anuncioInfo(a);
      if (jaAlertados.has(x.id) || !ativas.some(r => bate(r, x))) continue;
      jaAlertados.add(x.id);
      const moeda = x.moeda === 'orb' ? 'Gemas' : 'ouro';
      const desc = `${x.nome}${x.shiny ? ' ✨' : ''}${x.potencia ? ` P${x.potencia}` : ''} por ${x.preco} ${moeda}${x.vendedor ? ` (@${x.vendedor})` : ''}`;
      alertas.unshift({ em: Date.now(), desc });
      alertas.length = Math.min(alertas.length, MAX_ALERTAS);
      aviso(desc);
    }
    if (painelAberto) desenhar();
  }

  // ------------------------------------------------------------------ interface

  const css = `
    #ti-fa-btn{position:fixed;left:8px;bottom:8px;z-index:2147483000;background:#1e2433cc;color:#e6e6e6;border:1px solid #3a4560;
      border-radius:6px;font:12px Segoe UI,sans-serif;padding:3px 8px;cursor:pointer;opacity:.75}
    #ti-fa-btn:hover{opacity:1}
    #ti-fa{position:fixed;left:8px;bottom:36px;z-index:2147483000;width:300px;max-height:70vh;overflow:auto;background:#161a24f2;
      color:#ddd;border:1px solid #3a4560;border-radius:8px;font:12px Segoe UI,sans-serif;padding:8px;box-shadow:0 4px 16px #0008}
    #ti-fa h4{margin:8px 0 4px;font-size:12px;color:#9fb3ff}
    #ti-fa table{width:100%;border-collapse:collapse} #ti-fa td{padding:1px 2px} #ti-fa td:last-child{text-align:right}
    #ti-fa button{background:#2a3247;color:#ddd;border:1px solid #3a4560;border-radius:4px;font:11px Segoe UI,sans-serif;padding:2px 6px;cursor:pointer}
    #ti-fa input,#ti-fa select{background:#0f121a;color:#ddd;border:1px solid #3a4560;border-radius:3px;font:11px Segoe UI,sans-serif;padding:1px 3px}
    #ti-fa .regra{display:flex;gap:4px;align-items:center;margin:2px 0;flex-wrap:wrap}
    #ti-fa .muted{color:#8a93a8}
    .ti-fa-toast{position:fixed;right:10px;z-index:2147483001;background:#24402af2;color:#fff;border:1px solid #4c8a5a;border-radius:6px;
      font:12px Segoe UI,sans-serif;padding:6px 10px;max-width:320px;box-shadow:0 4px 12px #0008}`;

  let painelAberto = false;
  let painel = null;

  function montar() {
    if (document.getElementById('ti-fa-btn') || !document.body) return;
    const st = document.createElement('style'); st.textContent = css; document.head.appendChild(st);
    const btn = document.createElement('button');
    btn.id = 'ti-fa-btn'; btn.textContent = '📊 Farm';
    btn.title = 'ThoseIdles: painel de farm e alerta de mercado (só leitura)';
    btn.onclick = () => { painelAberto = !painelAberto; desenhar(); };
    document.body.appendChild(btn);
  }

  const fmt = n => Math.round(n).toLocaleString('pt-BR');
  const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

  function desenhar() {
    if (!painelAberto) { painel?.remove(); painel = null; return; }
    if (!painel) { painel = document.createElement('div'); painel.id = 'ti-fa'; document.body.appendChild(painel); }
    const horas = Math.max((Date.now() - stats.inicio) / 3600000, 1 / 60);
    const porH = v => fmt(v / horas);
    const min = Math.floor((Date.now() - stats.inicio) / 60000);
    const drops = Object.entries(stats.drops).sort((a, b) => b[1] - a[1]).slice(0, 10);
    painel.innerHTML = `
      <b>Farm desta sessão</b> <span class="muted">(${Math.floor(min / 60)}h${String(min % 60).padStart(2, '0')})</span>
      <table>
        <tr><td>Abates</td><td>${fmt(stats.kills)} · ${porH(stats.kills)}/h</td></tr>
        <tr><td>XP</td><td>${fmt(stats.xp)} · ${porH(stats.xp)}/h</td></tr>
        <tr><td>Ouro</td><td>${fmt(stats.ouro)} · ${porH(stats.ouro)}/h</td></tr>
        <tr><td>Capturas</td><td>${fmt(stats.capturas)} (${fmt(stats.shinies)} shiny)</td></tr>
      </table>
      <h4>Drops</h4>
      ${drops.length ? `<table>${drops.map(([n, q]) => `<tr><td>${esc(n)}</td><td>${fmt(q)} · ${porH(q)}/h</td></tr>`).join('')}</table>` : '<span class="muted">nenhum ainda</span>'}
      <div style="margin-top:6px"><button id="ti-fa-zerar">Zerar sessão</button></div>
      <h4>Alerta de mercado</h4>
      <div class="muted">Avisa quando aparecer um anúncio que bate com a regra, nas listas que você abrir no mercado. Não compra nem consulta nada sozinho.</div>
      <div id="ti-fa-regras">${regras.map((r, i) => `
        <div class="regra" data-i="${i}">
          <input type="checkbox" data-k="ativa" ${r.ativa ? 'checked' : ''} title="Ligada">
          <input data-k="texto" value="${esc(r.texto || '')}" placeholder="nome contém" style="width:80px">
          <label><input type="checkbox" data-k="soShiny" ${r.soShiny ? 'checked' : ''}>shiny</label>
          P≥<input data-k="potMin" type="number" min="0" max="5" value="${r.potMin || 0}" style="width:34px">
          ≤<input data-k="precoMax" type="number" min="0" value="${r.precoMax || 0}" style="width:60px" title="Preço máximo (0 = qualquer)">
          <select data-k="moeda"><option value="">qualquer</option><option value="orb" ${r.moeda === 'orb' ? 'selected' : ''}>Gemas</option><option value="gold" ${r.moeda === 'gold' ? 'selected' : ''}>ouro</option></select>
          <button data-rm="${i}" title="Remover">✕</button>
        </div>`).join('')}</div>
      <button id="ti-fa-add">+ regra</button>
      ${alertas.length ? `<h4>Últimos alertas</h4>${alertas.map(a => `<div>${new Date(a.em).toLocaleTimeString('pt-BR')} — ${esc(a.desc)}</div>`).join('')}` : ''}`;

    painel.querySelector('#ti-fa-zerar').onclick = () => { stats = novaSessao(); save(KEY_STATS, stats); desenhar(); };
    painel.querySelector('#ti-fa-add').onclick = () => { regras.push({ ativa: true, texto: '', soShiny: true, potMin: 0, precoMax: 0, moeda: '' }); save(KEY_RULES, regras); desenhar(); };
    painel.querySelectorAll('[data-rm]').forEach(b => b.onclick = () => { regras.splice(+b.dataset.rm, 1); save(KEY_RULES, regras); desenhar(); });
    painel.querySelectorAll('.regra [data-k]').forEach(inp => inp.onchange = () => {
      const r = regras[+inp.closest('.regra').dataset.i];
      const k = inp.dataset.k;
      r[k] = inp.type === 'checkbox' ? inp.checked : inp.type === 'number' ? num(inp.value) : inp.value.trim();
      save(KEY_RULES, regras);
    });
  }

  let toasts = 0;
  function aviso(texto) {
    try {
      const t = document.createElement('div');
      t.className = 'ti-fa-toast';
      t.style.top = `${10 + (toasts++ % 4) * 44}px`;
      t.textContent = `🔔 Mercado: ${texto}`;
      document.body.appendChild(t);
      setTimeout(() => t.remove(), 12000);
      const ctx = new AudioContext(); const o = ctx.createOscillator(); const g = ctx.createGain();
      o.frequency.value = 880; g.gain.value = 0.05; o.connect(g); g.connect(ctx.destination);
      o.start(); o.stop(ctx.currentTime + 0.15); o.onended = () => ctx.close();
    } catch { }
  }

  // Atualiza o tempo/taxas do painel aberto; monta o botão quando o body existir.
  setInterval(() => { montar(); if (painelAberto && !painel?.contains(document.activeElement)) desenhar(); }, 5000);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar); else montar();

  // Para o autoteste (e para depurar): estado atual, sem expor nada da conta.
  window.__tiFarmAssistState = () => ({ stats, regras, alertas: alertas.slice() });
})();
