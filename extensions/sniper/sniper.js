// ThoseIdles — Sniper do mercado do PokéIdle (compra automática por regras).
// Escrito do zero para o ThoseIdles, no espírito do "Rotom Sniper" divulgado no Discord do jogo.
// Desligado por padrão: só consulta o mercado e compra com o interruptor "Sniper ligado" ativo.
// O mesmo arquivo serve o app e a extensão do Chrome.
(() => {
  'use strict';
  if (window.top !== window || window.__tiSniper) return;
  if (!/(^|\.)pokeidle\.io$/.test(location.hostname) && location.hostname !== 'localhost') return;
  window.__tiSniper = true;

  const KEY = 'ti-sniper';
  const MIN_INTERVALO = 3; // segundos entre varreduras (o mínimo que o painel aceita)
  const MAX_LOG = 40;

  const load = (k, def) => { try { return JSON.parse(localStorage.getItem(k)) ?? def; } catch { return def; } };
  const save = () => { try { localStorage.setItem(KEY, JSON.stringify(cfg)); } catch { } };
  const num = v => Number(v) || 0;
  const ehGemas = m => ['orb', 'orbs', 'gemas', 'gema', 'gems'].includes(m);

  // ligado SEMPRE começa desligado ao abrir a página: compra automática nunca volta sozinha.
  const cfg = Object.assign({ intervalo: 5, regras: [] }, load(KEY, {}));
  let ligado = false;

  const st = {
    ws: null,
    ouro: -1, gemas: -1,       // saldo lido dos pacotes "estado" (-1 = ainda não sabe)
    tentados: new Map(),        // id do anúncio -> horário da tentativa
    agendados: new Map(),       // id do anúncio -> timers
    ultimaCompra: null,         // { id, desc, regra }
    log: [],
    varreduras: 0,
  };

  // ------------------------------------------------------------------ WebSocket do jogo

  const Original = window.WebSocket;
  window.WebSocket = new Proxy(Original, {
    construct(target, args) {
      const ws = Reflect.construct(target, args);
      try {
        if (new URL(String(args[0]), location.href).host === location.host) {
          st.ws = ws;
          ws.addEventListener('message', ev => { if (typeof ev.data === 'string') onMensagem(ev.data); });
        }
      } catch { }
      return ws;
    },
  });

  function enviar(obj) {
    const ws = st.ws;
    if (!ws || ws.readyState !== 1) return false;
    try { ws.send(JSON.stringify(obj)); return true; } catch { return false; }
  }

  function onMensagem(texto) {
    if (!/"t":"(market|estado|welcome|batalha|erro)"/.test(texto) && !texto.includes('"estado"')) return;
    let m;
    try { m = JSON.parse(texto); } catch { return; }
    const est = m.estado || m.payload?.estado || (m.t === 'estado' ? m : null);
    if (est) {
      if (typeof est.gold === 'number') st.ouro = est.gold;
      if (typeof est.orbs === 'number') st.gemas = est.orbs;
    }
    if (m.t === 'market') onMercado(m);
    else if (m.t === 'batalha' && Array.isArray(m.ev)) {
      for (const e of m.ev) if (e && (e.k || e.t) === 'marketComprado') confirmada(e.descricao);
    } else if (m.t === 'erro' && st.ultimaCompra && Date.now() - st.ultimaCompra.em < 5000) {
      registrar('RECUSADA', `${st.ultimaCompra.desc}: ${m.msg || m.chave || 'recusada pelo servidor'}`);
    }
  }

  // ------------------------------------------------------------------ varredura

  let ciclo = 0;
  let timer = null;
  function reagendar() {
    clearInterval(timer);
    timer = setInterval(varrer, Math.max(MIN_INTERVALO, num(cfg.intervalo)) * 1000);
  }

  function varrer() {
    if (!ligado) return;
    const ativas = cfg.regras.filter(r => r.ativa && !esgotada(r));
    if (!ativas.length) return;
    ciclo++;
    st.varreduras++;
    if (ativas.some(r => r.tipo === 'item')) enviar({ t: 'market.itens' });
    if (ativas.some(r => r.tipo !== 'item')) {
      const ordens = [
        { ordem: 'recentes', moeda: '' },
        { ordem: 'menor_preco', moeda: 'gold' },
        { ordem: 'menor_preco', moeda: 'orb' },
      ];
      const o = ordens[ciclo % ordens.length];
      setTimeout(() => ligado && enviar({ t: 'market.listar', tipo: 'pokemon', ordem: o.ordem, moeda: o.moeda, busca: '', criterios: [], elemento: '' }), 700);
    }
  }

  // ------------------------------------------------------------------ regras

  const esgotada = r => num(r.maxCompras) > 0 && num(r.compradas) >= num(r.maxCompras);

  function anuncio(a, extra) {
    const f = a.ficha || {};
    const itemId = num(a.itemId || f.itemId || extra?.itemId);
    return {
      id: num(a.id),
      item: itemId > 0 || !!a.itemNome || a.tipo === 'item',
      nome: String(a.pokemonNome || f.nome || a.itemNome || extra?.itemNome || a.nome || ''),
      shiny: !!(a.shiny || a.isShiny || a.lookShiny === 1 || f.shiny || f.isShiny || f.lookShiny === 1),
      potencia: num(a.potencia ?? f.potencia),
      preco: num(a.preco),
      qtd: num(a.qtd) || 1,
      moeda: String(a.moeda || extra?.moeda || 'gold').toLowerCase(),
      vendedor: String(a.vendedor || a.vendedorNome || ''),
      compravelEm: num(a.compravelEm || a.compravelEmMs),
      aberto: !a.comprador && (!a.estado || a.estado === 'aberto'),
    };
  }

  function bate(r, x) {
    if (!r.ativa || esgotada(r) || !x.aberto || !x.id || x.preco <= 0 || !x.nome) return false;
    if ((r.tipo === 'item') !== x.item) return false;
    if (r.moeda === 'orb' ? !ehGemas(x.moeda) : ehGemas(x.moeda)) return false;
    if (r.texto && !x.nome.toLowerCase().includes(r.texto.toLowerCase())) return false;
    if (r.tipo !== 'item' && r.soShiny && !x.shiny) return false;
    if (r.tipo !== 'item' && num(r.potMin) > 0 && x.potencia < num(r.potMin)) return false;
    if (num(r.precoMax) <= 0 || x.preco > num(r.precoMax)) return false; // preço máximo é obrigatório
    return true;
  }

  function onMercado(m) {
    const aba = String(m.aba || '');
    if (aba.startsWith('historico') || aba === 'meus') return;

    // Resumo de itens: pede os vendedores só dos itens com preço mínimo dentro de alguma regra.
    if (aba === 'itens' && m.resumo && ligado) {
      for (const [id, res] of Object.entries(m.resumo)) {
        const nome = String(res?.nome || res?.name || '');
        for (const r of cfg.regras.filter(r => r.ativa && r.tipo === 'item' && !esgotada(r))) {
          if (r.texto && !nome.toLowerCase().includes(r.texto.toLowerCase())) continue;
          const min = num(r.moeda === 'orb' ? res?.minOrb : res?.minGold);
          if (min > 0 && min <= num(r.precoMax)) { enviar({ t: 'market.item', itemId: num(id), moeda: r.moeda === 'orb' ? 'orb' : 'gold' }); break; }
        }
      }
      return;
    }

    const extra = aba === 'item' ? { itemId: m.itemId, moeda: m.moeda, itemNome: m.nome || m.itemNome } : null;
    const lista = [].concat(m.anuncios || [], m.linhas || [], m.anuncio ? [m.anuncio] : []);
    if (!ligado) return;
    for (const a of lista) {
      if (!a) continue;
      const x = anuncio(a, extra);
      if (st.tentados.has(x.id) || st.agendados.has(x.id)) continue;
      const r = cfg.regras.find(r => bate(r, x));
      if (r) disparar(r, x);
    }
  }

  // ------------------------------------------------------------------ compra

  function saldoOk(x, total) {
    const saldo = ehGemas(x.moeda) ? st.gemas : st.ouro;
    return saldo < 0 || saldo >= total; // saldo desconhecido: deixa o servidor decidir
  }

  function disparar(r, x) {
    const qtd = r.tipo === 'item' ? Math.max(1, Math.min(x.qtd, num(r.qtdPorCompra) || 1)) : 1;
    const desc = `${x.nome}${x.shiny ? ' ✨' : ''}${x.potencia ? ` P${x.potencia}` : ''}${qtd > 1 ? ` ×${qtd}` : ''} por ${x.preco} ${ehGemas(x.moeda) ? 'Gemas' : 'ouro'}${x.vendedor ? ` (@${x.vendedor})` : ''}`;
    if (!saldoOk(x, x.preco * qtd)) {
      st.tentados.set(x.id, Date.now());
      registrar('SEM SALDO', desc);
      return;
    }
    const pedido = { t: 'market.comprar', id: x.id, qtd, preco: x.preco, moeda: x.moeda };
    const espera = x.compravelEm - Date.now();
    if (espera > 50) {
      // Anúncio ainda em carência: tenta logo depois de liberar (antes disso o servidor recusa).
      const t0 = espera + 100;
      const timers = [t0, t0 + 600, t0 + 1500].map((ms, i) => setTimeout(() => {
        if (!ligado) return;
        if (i === 0) marcarTentativa(r, x, desc);
        enviar(pedido);
        if (i === 2) st.agendados.delete(x.id);
      }, ms));
      st.agendados.set(x.id, timers);
      registrar('AGENDADA', `${desc} — libera em ${Math.ceil(espera / 1000)} s`);
      return;
    }
    marcarTentativa(r, x, desc);
    enviar(pedido);
  }

  function marcarTentativa(r, x, desc) {
    st.tentados.set(x.id, Date.now());
    st.ultimaCompra = { id: x.id, desc, regra: r, em: Date.now() };
    registrar('ENVIADA', desc);
  }

  function confirmada(descricao) {
    const u = st.ultimaCompra;
    if (u?.regra) { u.regra.compradas = num(u.regra.compradas) + 1; save(); }
    // Deu certo: cancela as tentativas repetidas que ainda estavam agendadas para esse anúncio.
    if (u && st.agendados.has(u.id)) { st.agendados.get(u.id).forEach(clearTimeout); st.agendados.delete(u.id); }
    registrar('COMPRADA', descricao || u?.desc || 'compra confirmada');
    toast(`🎯 Comprado: ${descricao || u?.desc || ''}`);
  }

  function registrar(status, desc) {
    st.log.unshift({ em: Date.now(), status, desc });
    st.log.length = Math.min(st.log.length, MAX_LOG);
    if (painelAberto) desenhar();
  }

  // ------------------------------------------------------------------ interface

  const css = `
    #ti-snp-btn{position:fixed;left:84px;bottom:8px;z-index:2147483000;background:#2a1a1add;color:#eee;border:1px solid #7a3f3f;
      border-radius:14px;font:600 12px Segoe UI,sans-serif;padding:4px 10px;cursor:pointer;opacity:.8}
    #ti-snp-btn:hover{opacity:1} #ti-snp-btn.on{background:#5a1d22;border-color:#e5484d;opacity:1}
    #ti-snp{position:fixed;left:8px;bottom:40px;z-index:2147483000;width:380px;max-height:75vh;overflow:auto;background:#1b1517f5;
      color:#ddd;border:1px solid #7a3f3f;border-radius:10px;font:12px Segoe UI,sans-serif;padding:10px 12px;box-shadow:0 6px 20px #0009}
    #ti-snp h4{margin:10px 0 4px;font-size:12px;color:#ffaaa8}
    #ti-snp button{background:#3a2326;color:#ddd;border:1px solid #7a3f3f;border-radius:5px;font:11px Segoe UI,sans-serif;padding:3px 8px;cursor:pointer}
    #ti-snp input,#ti-snp select{background:#120e0f;color:#ddd;border:1px solid #7a3f3f;border-radius:4px;font:11px Segoe UI,sans-serif;padding:2px 4px}
    #ti-snp .regra{display:flex;gap:5px;align-items:center;margin:4px 0;flex-wrap:wrap;background:#271c1e;border-radius:6px;padding:6px}
    #ti-snp .muted{color:#a89194} #ti-snp .mestre{display:flex;align-items:center;gap:8px;font-size:13px;font-weight:600}
    .ti-snp-toast{position:fixed;right:10px;z-index:2147483001;background:#5a1d22f2;color:#fff;border:1px solid #e5484d;border-radius:8px;
      font:12px Segoe UI,sans-serif;padding:7px 11px;max-width:320px;box-shadow:0 4px 14px #0009}`;

  let painelAberto = false;
  let painel = null;

  function montar() {
    if (document.getElementById('ti-snp-btn') || !document.body) return;
    const s = document.createElement('style'); s.textContent = css; document.head.appendChild(s);
    const btn = document.createElement('button');
    btn.id = 'ti-snp-btn';
    btn.title = 'ThoseIdles: sniper do mercado (compra automática por regras)';
    btn.onclick = () => { painelAberto = !painelAberto; desenhar(); };
    document.body.appendChild(btn);
    atualizarBotao();
  }

  function atualizarBotao() {
    const btn = document.getElementById('ti-snp-btn');
    if (!btn) return;
    btn.textContent = ligado ? '🎯 Sniper: LIGADO' : '🎯 Sniper';
    btn.classList.toggle('on', ligado);
  }

  const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const sel = (v, opts) => opts.map(([val, txt]) => `<option value="${val}" ${v === val ? 'selected' : ''}>${txt}</option>`).join('');

  function desenhar() {
    atualizarBotao();
    if (!painelAberto) { painel?.remove(); painel = null; return; }
    if (!painel) { painel = document.createElement('div'); painel.id = 'ti-snp'; document.body.appendChild(painel); }
    const saldo = `${st.ouro >= 0 ? st.ouro.toLocaleString('pt-BR') : '?'} ouro · ${st.gemas >= 0 ? st.gemas.toLocaleString('pt-BR') : '?'} Gemas`;
    painel.innerHTML = `
      <label class="mestre"><input type="checkbox" id="ti-snp-on" ${ligado ? 'checked' : ''}> Sniper ligado</label>
      <div class="muted">Quando ligado, consulta o mercado a cada
        <input id="ti-snp-int" type="number" min="${MIN_INTERVALO}" value="${cfg.intervalo}" style="width:40px"> s
        e <b>compra sozinho</b> o que bater com uma regra. Desliga ao recarregar a página. Saldo: ${saldo}.</div>
      <h4>Regras</h4>
      ${cfg.regras.length ? '' : '<div class="muted">Nenhuma regra ainda.</div>'}
      ${cfg.regras.map((r, i) => `
        <div class="regra" data-i="${i}">
          <input type="checkbox" data-k="ativa" ${r.ativa ? 'checked' : ''} title="Regra ativa">
          <select data-k="tipo">${sel(r.tipo, [['pokemon', 'Pokémon'], ['item', 'Item']])}</select>
          <input data-k="texto" value="${esc(r.texto || '')}" placeholder="nome contém" style="width:110px">
          ${r.tipo === 'item'
            ? `<label>qtd/compra <input data-k="qtdPorCompra" type="number" min="1" value="${r.qtdPorCompra || 1}" style="width:40px"></label>`
            : `<label><input type="checkbox" data-k="soShiny" ${r.soShiny ? 'checked' : ''}> shiny</label>
               <label>P≥ <input data-k="potMin" type="number" min="0" max="5" value="${r.potMin || 0}" style="width:34px"></label>`}
          <label>até <input data-k="precoMax" type="number" min="0" value="${r.precoMax || 0}" style="width:70px"></label>
          <select data-k="moeda">${sel(r.moeda, [['gold', 'ouro'], ['orb', 'Gemas']])}</select>
          <label>máx. <input data-k="maxCompras" type="number" min="0" value="${r.maxCompras || 0}" style="width:40px" title="Para de comprar depois de N compras (0 = sem limite)"> compras</label>
          <span class="muted">(${num(r.compradas)} feitas)</span>
          <button data-zera="${i}" title="Zerar contador">↺</button>
          <button data-rm="${i}" title="Remover regra">Remover</button>
        </div>`).join('')}
      <button id="ti-snp-add">+ Nova regra</button>
      <div class="muted" style="margin-top:4px">Regra sem preço máximo não compra nada.</div>
      <h4>Registro</h4>
      ${st.log.length ? st.log.map(l => `<div>${new Date(l.em).toLocaleTimeString('pt-BR')} <b>${l.status}</b> — ${esc(l.desc)}</div>`).join('') : '<div class="muted">Nada ainda.</div>'}`;

    painel.querySelector('#ti-snp-on').onchange = e => {
      ligado = e.target.checked;
      if (!ligado) { for (const ts of st.agendados.values()) ts.forEach(clearTimeout); st.agendados.clear(); }
      registrar(ligado ? 'LIGADO' : 'DESLIGADO', ligado ? 'sniper ligado' : 'sniper desligado');
      desenhar();
      if (ligado) varrer();
    };
    painel.querySelector('#ti-snp-int').onchange = e => { cfg.intervalo = Math.max(MIN_INTERVALO, num(e.target.value)); save(); reagendar(); desenhar(); };
    painel.querySelector('#ti-snp-add').onclick = () => {
      cfg.regras.push({ ativa: true, tipo: 'pokemon', texto: '', soShiny: true, potMin: 0, precoMax: 0, moeda: 'gold', maxCompras: 1, compradas: 0 });
      save(); desenhar();
    };
    painel.querySelectorAll('[data-rm]').forEach(b => b.onclick = () => { cfg.regras.splice(+b.dataset.rm, 1); save(); desenhar(); });
    painel.querySelectorAll('[data-zera]').forEach(b => b.onclick = () => { cfg.regras[+b.dataset.zera].compradas = 0; save(); desenhar(); });
    painel.querySelectorAll('.regra [data-k]').forEach(inp => inp.onchange = () => {
      const r = cfg.regras[+inp.closest('.regra').dataset.i];
      const k = inp.dataset.k;
      r[k] = inp.type === 'checkbox' ? inp.checked : inp.type === 'number' ? num(inp.value) : inp.value.trim();
      save();
      if (k === 'tipo') desenhar();
    });
  }

  let toasts = 0;
  function toast(texto) {
    try {
      const t = document.createElement('div');
      t.className = 'ti-snp-toast';
      t.style.top = `${10 + (toasts++ % 4) * 46}px`;
      t.textContent = texto;
      document.body.appendChild(t);
      setTimeout(() => t.remove(), 12000);
    } catch { }
  }

  reagendar();
  setInterval(montar, 5000);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar); else montar();

  // Para o autoteste: liga/desliga e lê o estado (nada da conta além do saldo mostrado no painel).
  window.__tiSniper = {
    ligar(v) { ligado = !!v; desenhar(); if (ligado) varrer(); },
    estado: () => ({ ligado, regras: cfg.regras, log: st.log.slice(), varreduras: st.varreduras }),
  };
})();
