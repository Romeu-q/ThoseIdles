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

  // ------------------------------------------------------------------ interface (peças em ti-ui.js)

  const UI = () => window.__tiUI;
  const PAINEL = 'ti-rmt';
  let painelAberto = false;
  let naoVistos = 0;

  function montar() {
    if (!UI() || !document.body) return;
    UI().pill('ti-rmt-btn', 'Alerta de RMT: avisa anúncios em Gemas que batem com as suas regras. Nunca compra.', () => {
      painelAberto = !painelAberto;
      naoVistos = 0;
      desenhar();
    });
    atualizarBotao();
  }

  function atualizarBotao() {
    const btn = document.getElementById('ti-rmt-btn');
    if (!btn) return;
    btn.innerHTML = `🔔 RMT${naoVistos ? ` <span class="ti-badge">${naoVistos}</span>` : ''}`;
    btn.classList.toggle('ativo', painelAberto);
  }

  function cartaoRegra(r, i) {
    const { esc, opcoes } = UI();
    return `
      <div class="ti-card regra ${r.ativa ? '' : 'off'}" data-i="${i}">
        <div class="ti-row">
          <label class="ti-switch" title="Regra ligada"><input type="checkbox" data-k="ativa" ${r.ativa ? 'checked' : ''}></label>
          <input class="ti-in ti-grow" data-k="texto" value="${esc(r.texto || '')}" placeholder="Nome contém… (vazio = qualquer Pokémon)">
          <button class="ti-btn danger sm" data-rm="${i}" title="Remover regra">✕</button>
        </div>
        <div class="ti-grid">
          <label class="ti-field">Potência mínima<select class="ti-in" data-k="potMin">${opcoes(r.potMin || 0, [[0, 'Qualquer'], [2, 'P2+'], [3, 'P3+'], [4, 'P4+'], [5, 'P5']])}</select></label>
          <label class="ti-field">Até (Gemas)<input class="ti-in" data-k="precoMax" type="number" min="0" value="${r.precoMax || ''}" placeholder="qualquer"></label>
          <label class="ti-field">&nbsp;<span class="ti-switch" style="height:32px"><input type="checkbox" data-k="soShiny" ${r.soShiny ? 'checked' : ''}>Só shiny</span></label>
        </div>
      </div>`;
  }

  function desenhar() {
    atualizarBotao();
    if (!UI()) return;
    if (!painelAberto) { UI().fecharPainel(PAINEL); return; }
    const { esc, hora } = UI();
    const p = UI().painel(PAINEL, { icone: '🔔', titulo: 'Alerta de RMT', sub: 'Avisa anúncios em Gemas. Nunca compra.' },
      () => { painelAberto = false; desenhar(); });
    p.body.innerHTML = `
      <div class="ti-muted" style="margin-top:12px">Quando você abrir uma lista no mercado e aparecer um anúncio <b>em Gemas</b> que bate com uma regra, aparece um aviso com um bipe.</div>
      <div class="ti-sec"><span>Regras</span><button class="ti-btn primary sm" id="ti-rmt-add">+ Nova regra</button></div>
      ${regras.length ? regras.map(cartaoRegra).join('') : '<div class="ti-empty">Nenhuma regra. Clique em <b>+ Nova regra</b> para escolher o que avisar.</div>'}
      <div class="ti-sec"><span>Últimos alertas</span></div>
      ${alertas.length
        ? alertas.map(a => `<div class="ti-log"><time>${hora(a.em)}</time><span>${esc(a.desc)}</span></div>`).join('')
        : '<div class="ti-muted">Nenhum ainda.</div>'}`;

    p.body.querySelector('#ti-rmt-add').onclick = () => { regras.push({ ativa: true, texto: '', soShiny: true, potMin: 0, precoMax: 0 }); save(KEY_RULES, regras); desenhar(); };
    p.body.querySelectorAll('[data-rm]').forEach(b => b.onclick = () => { regras.splice(+b.dataset.rm, 1); save(KEY_RULES, regras); desenhar(); });
    p.body.querySelectorAll('.regra [data-k]').forEach(inp => inp.onchange = () => {
      const r = regras[+inp.closest('.regra').dataset.i];
      const k = inp.dataset.k;
      r[k] = inp.type === 'checkbox' ? inp.checked : (inp.type === 'number' || k === 'potMin') ? num(inp.value) : inp.value.trim();
      save(KEY_RULES, regras);
      if (k === 'ativa') desenhar();
    });
  }

  function aviso(texto) {
    if (!painelAberto) naoVistos++;
    UI()?.toast('🔔 Anúncio no RMT', texto);
    UI()?.bipe();
  }

  setInterval(montar, 5000);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar); else montar();

  // Para o autoteste (e para depurar): regras e alertas, sem nada da conta.
  window.__tiRmtAlertState = () => ({ regras, alertas: alertas.slice() });
})();
