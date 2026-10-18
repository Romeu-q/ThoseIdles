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
    toast(descricao || u?.desc || '');
  }

  function registrar(status, desc) {
    st.log.unshift({ em: Date.now(), status, desc });
    st.log.length = Math.min(st.log.length, MAX_LOG);
    // Não redesenha enquanto você digita numa regra (perderia o que está sendo digitado).
    if (painelAberto && !document.getElementById('ti-snp')?.contains(document.activeElement)) desenhar();
    else atualizarBotao();
  }

  // ------------------------------------------------------------------ interface (peças em ti-ui.js)

  const UI = () => window.__tiUI;
  const PAINEL = 'ti-snp';
  let painelAberto = false;

  function montar() {
    if (!UI() || !document.body) return;
    UI().pill('ti-snp-btn', 'Sniper do mercado: compra sozinho o que bater com as suas regras', () => {
      painelAberto = !painelAberto;
      desenhar();
    });
    atualizarBotao();
  }

  function atualizarBotao() {
    const btn = document.getElementById('ti-snp-btn');
    if (!btn) return;
    btn.innerHTML = ligado ? '🎯 Sniper <span class="ti-badge">LIGADO</span>' : '🎯 Sniper';
    btn.classList.toggle('alerta', ligado);
    btn.classList.toggle('ativo', painelAberto && !ligado);
  }

  const STATUS = {
    COMPRADA: 'ok', ENVIADA: 'roxo', AGENDADA: 'warn', RECUSADA: 'no', 'SEM SALDO': 'no', LIGADO: 'no', DESLIGADO: '',
  };

  function cartaoRegra(r, i) {
    const { esc, opcoes } = UI();
    const item = r.tipo === 'item';
    const feitas = `${num(r.compradas)}${num(r.maxCompras) > 0 ? ` / ${num(r.maxCompras)}` : ''} compra(s)`;
    return `
      <div class="ti-card regra ${r.ativa ? '' : 'off'}" data-i="${i}">
        <div class="ti-row">
          <label class="ti-switch" title="Regra ligada"><input type="checkbox" data-k="ativa" ${r.ativa ? 'checked' : ''}></label>
          <select class="ti-in" data-k="tipo" style="width:110px">${opcoes(r.tipo, [['pokemon', 'Pokémon'], ['item', 'Item']])}</select>
          <input class="ti-in ti-grow" data-k="texto" value="${esc(r.texto || '')}" placeholder="Nome contém… (vazio = qualquer)">
          <button class="ti-btn danger sm" data-rm="${i}" title="Remover regra">✕</button>
        </div>
        <div class="ti-grid">
          ${item
            ? `<label class="ti-field">Qtd. por compra<input class="ti-in" data-k="qtdPorCompra" type="number" min="1" value="${r.qtdPorCompra || 1}"></label>`
            : `<label class="ti-field">Potência mínima<select class="ti-in" data-k="potMin">${opcoes(r.potMin || 0, [[0, 'Qualquer'], [2, 'P2+'], [3, 'P3+'], [4, 'P4+'], [5, 'P5']])}</select></label>`}
          <label class="ti-field">Pagar no máximo<input class="ti-in" data-k="precoMax" type="number" min="0" value="${r.precoMax || ''}" placeholder="obrigatório"></label>
          <label class="ti-field">Moeda<select class="ti-in" data-k="moeda">${opcoes(r.moeda, [['gold', 'Ouro'], ['orb', 'Gemas']])}</select></label>
        </div>
        <div class="ti-row" style="margin-top:10px">
          ${item ? '' : `<label class="ti-switch"><input type="checkbox" data-k="soShiny" ${r.soShiny ? 'checked' : ''}>Só shiny</label>`}
          <span class="ti-grow"></span>
          <label class="ti-field" style="flex-direction:row;align-items:center;gap:6px">Parar após
            <input class="ti-in" data-k="maxCompras" type="number" min="0" value="${r.maxCompras || 0}" style="width:56px" title="0 = sem limite"></label>
          <span class="ti-chip ${num(r.maxCompras) > 0 && num(r.compradas) >= num(r.maxCompras) ? 'ok' : ''}">${feitas}</span>
          <button class="ti-btn ghost sm" data-zera="${i}" title="Zerar o contador">↺</button>
        </div>
        ${num(r.precoMax) > 0 ? '' : '<div class="ti-muted" style="margin-top:8px">⚠ Sem "pagar no máximo" esta regra não compra nada.</div>'}
      </div>`;
  }

  function desenhar() {
    atualizarBotao();
    if (!UI()) return;
    if (!painelAberto) { UI().fecharPainel(PAINEL); return; }
    const { esc, opcoes, hora } = UI();
    const p = UI().painel(PAINEL, { icone: '🎯', titulo: 'Sniper do mercado', sub: 'Compra sozinho o que bater com as suas regras' },
      () => { painelAberto = false; desenhar(); });
    const saldo = v => (v >= 0 ? v.toLocaleString('pt-BR') : '?');
    p.body.innerHTML = `
      <div class="ti-hero ${ligado ? 'on' : ''}">
        <label class="ti-switch red"><input type="checkbox" id="ti-snp-on" ${ligado ? 'checked' : ''}></label>
        <div class="ti-grow">
          <b>${ligado ? 'Sniper LIGADO — comprando sozinho' : 'Sniper desligado'}</b>
          <div class="ti-muted">Desliga sozinho quando a página recarrega.</div>
        </div>
        <label class="ti-field">Consultar a cada
          <select class="ti-in" id="ti-snp-int" style="width:90px">${opcoes(cfg.intervalo, [[3, '3 s'], [5, '5 s'], [10, '10 s'], [15, '15 s'], [30, '30 s']])}</select></label>
      </div>
      <div class="ti-row" style="margin-top:8px">
        <span class="ti-chip warn">${saldo(st.ouro)} ouro</span>
        <span class="ti-chip roxo">💎 ${saldo(st.gemas)} Gemas</span>
      </div>

      <div class="ti-sec"><span>Regras</span><button class="ti-btn primary sm" id="ti-snp-add">+ Nova regra</button></div>
      ${cfg.regras.length ? cfg.regras.map(cartaoRegra).join('') : '<div class="ti-empty">Nenhuma regra. Clique em <b>+ Nova regra</b> para dizer o que comprar.</div>'}

      <div class="ti-sec"><span>Registro</span></div>
      ${st.log.length
        ? st.log.map(l => `<div class="ti-log"><time>${hora(l.em)}</time><span class="ti-chip ${STATUS[l.status] ?? ''}">${esc(l.status)}</span><span>${esc(l.desc)}</span></div>`).join('')
        : '<div class="ti-muted">Nada ainda.</div>'}`;

    p.body.querySelector('#ti-snp-on').onchange = e => {
      ligado = e.target.checked;
      if (!ligado) { for (const ts of st.agendados.values()) ts.forEach(clearTimeout); st.agendados.clear(); }
      registrar(ligado ? 'LIGADO' : 'DESLIGADO', ligado ? 'sniper ligado' : 'sniper desligado');
      desenhar();
      if (ligado) varrer();
    };
    p.body.querySelector('#ti-snp-int').onchange = e => { cfg.intervalo = Math.max(MIN_INTERVALO, num(e.target.value)); save(); reagendar(); };
    p.body.querySelector('#ti-snp-add').onclick = () => {
      cfg.regras.push({ ativa: true, tipo: 'pokemon', texto: '', soShiny: true, potMin: 0, precoMax: 0, moeda: 'gold', maxCompras: 1, compradas: 0 });
      save(); desenhar();
    };
    p.body.querySelectorAll('[data-rm]').forEach(b => b.onclick = () => { cfg.regras.splice(+b.dataset.rm, 1); save(); desenhar(); });
    p.body.querySelectorAll('[data-zera]').forEach(b => b.onclick = () => { cfg.regras[+b.dataset.zera].compradas = 0; save(); desenhar(); });
    p.body.querySelectorAll('.regra [data-k]').forEach(inp => inp.onchange = () => {
      const r = cfg.regras[+inp.closest('.regra').dataset.i];
      const k = inp.dataset.k;
      r[k] = inp.type === 'checkbox' ? inp.checked : (inp.type === 'number' || k === 'potMin') ? num(inp.value) : inp.value.trim();
      save();
      if (k === 'tipo' || k === 'ativa' || k === 'precoMax' || k === 'maxCompras') desenhar();
    });
  }

  function toast(texto) {
    UI()?.toast('Sniper: compra feita', texto, 'ok');
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
