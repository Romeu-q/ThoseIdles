// ThoseIdles — Alerta de RMT para o PokéIdle.
// Avisa quando aparece, numa lista do mercado que VOCÊ abriu, um anúncio em Gemas (a moeda do RMT)
// que bate com as suas regras. Só LÊ as mensagens que o jogo já manda para a página: nunca envia
// nada ao servidor, não compra, não consulta o mercado sozinho. O mesmo arquivo serve o app e a
// extensão do Chrome.
(() => {
  'use strict';
  if (window.top !== window || window.__tiRmtAlert) return;
  // Só no jogo (o app injeta o script em toda página da conta; localhost = autoteste).
  if (!/(^|\.)pokeidle\.io$/.test(location.hostname) && location.hostname !== 'localhost') return;
  window.__tiRmtAlert = true;

  const KEY_RULES = 'ti-rmt-regras';
  const MAX_ALERTAS = 30;

  const load = (k, def) => { try { return JSON.parse(localStorage.getItem(k)) ?? def; } catch { return def; } };
  const save = (k, v) => { try { localStorage.setItem(k, JSON.stringify(v)); } catch { } };
  const num = v => Number(v) || 0;

  let regras = load(KEY_RULES, []);
  const alertas = [];
  const jaAlertados = new Set();

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
    if (!texto.includes('"t":"market"')) return; // filtro barato antes do JSON.parse
    let m;
    try { m = JSON.parse(texto); } catch { return; }
    if (m.t === 'market') onMercado(m);
  }

  // ------------------------------------------------------------------ regras

  const ehGemas = moeda => ['orb', 'orbs', 'gemas', 'gema', 'gems'].includes(moeda);

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
    if (!r.ativa || !x.aberto || !x.nome || !ehGemas(x.moeda)) return false;
    if (r.texto && !x.nome.toLowerCase().includes(r.texto.toLowerCase())) return false;
    if (r.soShiny && !x.shiny) return false;
    if (r.potMin > 0 && x.potencia < r.potMin) return false;
    if (r.precoMax > 0 && (x.preco <= 0 || x.preco > r.precoMax)) return false;
    return true;
  }

  function onMercado(m) {
    if (String(m.aba || '').startsWith('historico') || m.aba === 'meus') return;
    const ativas = regras.filter(r => r.ativa);
    if (!ativas.length) return;
    const lista = [].concat(m.anuncios || [], m.linhas || [], m.anuncio ? [m.anuncio] : []);
    let novos = 0;
    for (const a of lista) {
      if (!a || a.id == null) continue;
      const x = anuncioInfo(a);
      if (jaAlertados.has(x.id) || !ativas.some(r => bate(r, x))) continue;
      jaAlertados.add(x.id);
      const desc = `${x.nome}${x.shiny ? ' ✨' : ''}${x.potencia ? ` P${x.potencia}` : ''} por ${x.preco} Gemas${x.vendedor ? ` (@${x.vendedor})` : ''}`;
      alertas.unshift({ em: Date.now(), desc });
      alertas.length = Math.min(alertas.length, MAX_ALERTAS);
      aviso(desc);
      novos++;
    }
    if (novos) { atualizarBotao(); if (painelAberto) desenhar(); }
  }

  // ------------------------------------------------------------------ interface

  const css = `
    #ti-rmt-btn{position:fixed;left:8px;bottom:8px;z-index:2147483000;background:#1d1a2bdd;color:#e6e6ea;border:1px solid #4a3f7a;
      border-radius:14px;font:600 12px Segoe UI,sans-serif;padding:4px 10px;cursor:pointer;opacity:.8}
    #ti-rmt-btn:hover{opacity:1} #ti-rmt-btn .n{background:#8b6cff;border-radius:8px;padding:0 5px;margin-left:5px}
    #ti-rmt{position:fixed;left:8px;bottom:40px;z-index:2147483000;width:320px;max-height:70vh;overflow:auto;background:#17151ff5;
      color:#ddd;border:1px solid #4a3f7a;border-radius:10px;font:12px Segoe UI,sans-serif;padding:10px 12px;box-shadow:0 6px 20px #0009}
    #ti-rmt h4{margin:10px 0 4px;font-size:12px;color:#b9a7ff}
    #ti-rmt .t{font-size:13px;font-weight:600}
    #ti-rmt button{background:#2a2540;color:#ddd;border:1px solid #4a3f7a;border-radius:5px;font:11px Segoe UI,sans-serif;padding:3px 8px;cursor:pointer}
    #ti-rmt input{background:#0f0e16;color:#ddd;border:1px solid #4a3f7a;border-radius:4px;font:11px Segoe UI,sans-serif;padding:2px 4px}
    #ti-rmt .regra{display:flex;gap:5px;align-items:center;margin:4px 0;flex-wrap:wrap;background:#201c30;border-radius:6px;padding:5px}
    #ti-rmt .muted{color:#8f8aa6}
    .ti-rmt-toast{position:fixed;right:10px;z-index:2147483001;background:#2b2150f2;color:#fff;border:1px solid #8b6cff;border-radius:8px;
      font:12px Segoe UI,sans-serif;padding:7px 11px;max-width:320px;box-shadow:0 4px 14px #0009}`;

  let painelAberto = false;
  let painel = null;
  let naoVistos = 0;

  function montar() {
    if (document.getElementById('ti-rmt-btn') || !document.body) return;
    const st = document.createElement('style'); st.textContent = css; document.head.appendChild(st);
    const btn = document.createElement('button');
    btn.id = 'ti-rmt-btn';
    btn.title = 'ThoseIdles: alerta de anúncios em Gemas (RMT). Só avisa; não compra.';
    btn.onclick = () => { painelAberto = !painelAberto; naoVistos = 0; atualizarBotao(); desenhar(); };
    document.body.appendChild(btn);
    atualizarBotao();
  }

  function atualizarBotao() {
    const btn = document.getElementById('ti-rmt-btn');
    if (!btn) return;
    btn.innerHTML = `🔔 RMT${naoVistos ? `<span class="n">${naoVistos}</span>` : ''}`;
  }

  const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

  function desenhar() {
    if (!painelAberto) { painel?.remove(); painel = null; return; }
    if (!painel) { painel = document.createElement('div'); painel.id = 'ti-rmt'; document.body.appendChild(painel); }
    painel.innerHTML = `
      <div class="t">Alerta de RMT</div>
      <div class="muted">Avisa quando, numa lista do mercado que você abrir, aparecer um anúncio <b>em Gemas</b> que bate com uma regra. Não compra nem consulta nada sozinho.</div>
      <h4>Regras</h4>
      ${regras.length ? '' : '<div class="muted">Nenhuma regra ainda.</div>'}
      ${regras.map((r, i) => `
        <div class="regra" data-i="${i}">
          <input type="checkbox" data-k="ativa" ${r.ativa ? 'checked' : ''} title="Ligada">
          <input data-k="texto" value="${esc(r.texto || '')}" placeholder="nome contém (vazio = qualquer)" style="width:150px">
          <label><input type="checkbox" data-k="soShiny" ${r.soShiny ? 'checked' : ''}> só shiny</label>
          <label>P mín. <input data-k="potMin" type="number" min="0" max="5" value="${r.potMin || 0}" style="width:34px"></label>
          <label>até <input data-k="precoMax" type="number" min="0" value="${r.precoMax || 0}" style="width:64px" title="0 = qualquer preço"> Gemas</label>
          <button data-rm="${i}" title="Remover regra">Remover</button>
        </div>`).join('')}
      <button id="ti-rmt-add">+ Nova regra</button>
      <h4>Últimos alertas</h4>
      ${alertas.length ? alertas.map(a => `<div>${new Date(a.em).toLocaleTimeString('pt-BR')} — ${esc(a.desc)}</div>`).join('') : '<div class="muted">Nenhum ainda.</div>'}`;

    painel.querySelector('#ti-rmt-add').onclick = () => { regras.push({ ativa: true, texto: '', soShiny: true, potMin: 0, precoMax: 0 }); save(KEY_RULES, regras); desenhar(); };
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
    if (!painelAberto) naoVistos++;
    try {
      const t = document.createElement('div');
      t.className = 'ti-rmt-toast';
      t.style.top = `${10 + (toasts++ % 4) * 46}px`;
      t.textContent = `🔔 RMT: ${texto}`;
      document.body.appendChild(t);
      setTimeout(() => t.remove(), 12000);
      const ctx = new AudioContext(); const o = ctx.createOscillator(); const g = ctx.createGain();
      o.frequency.value = 880; g.gain.value = 0.05; o.connect(g); g.connect(ctx.destination);
      o.start(); o.stop(ctx.currentTime + 0.15); o.onended = () => ctx.close();
    } catch { }
  }

  setInterval(montar, 5000);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar); else montar();

  // Para o autoteste (e para depurar): regras e alertas, sem nada da conta.
  window.__tiRmtAlertState = () => ({ regras, alertas: alertas.slice() });
})();
