// ThoseIdles — PokéIdle Hub (https://pokeidle-gg.pages.dev) dentro do app.
// Na ficha de qualquer Pokémon do jogo entram os botões "📘 Pokédex", "📈 Calcular XP" e
// "⚖ Vale a pena?", junto das ações da ficha. Cada um pede ao app para abrir o Hub numa janela
// (no perfil desta conta) na página certa, já com a ficha do Pokémon colada — o mesmo texto que você
// copiaria à mão "do nome até Stats-base da espécie". No perfil do treinador, abaixo das moedas, o
// botão "📘 PokéIdle Hub" abre o site (Pokédex, Calculadora de XP, Hunt, Vale a pena?, Mercado, Guias).
// Só do app (usa chrome.webview); não lê nem envia nada ao jogo.
(() => {
  'use strict';
  if (window.top !== window || window.__tiHub || !window.chrome?.webview) return;
  if (!/(^|\.)pokeidle\.io$/.test(location.hostname) && location.hostname !== 'localhost') return;
  window.__tiHub = true;

  const UI = () => window.__tiUI;
  const txt = el => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const digitos = s => String(s || '').replace(/\D/g, '');
  const STATS = ['HP', 'ATK', 'DEF', 'SP.ATK', 'SP.DEF', 'SPD'];

  /** Pede ao app para abrir o Hub. rota: pokedex | xp | vale-a-pena | guias…; ficha/busca opcionais. */
  function abrir(rota, extra = {}) {
    try { window.chrome.webview.postMessage({ ti: 'hub', rota, ...extra }); } catch { }
  }

  /** Lê a ficha aberta (#ficha-corpo). null se não der para identificar a espécie. */
  function lerFicha(corpo) {
    let nome = '';
    for (const b of corpo.querySelectorAll('.fi-placa b')) {
      const m = txt(b).match(/^#[\d.,]+\s+(.+)$/);
      if (m) { nome = m[1]; break; }
    }
    if (!nome) nome = txt(corpo.querySelector('.fi-nome')).replace(/^[^\p{L}]+/u, '').replace(/\s*\+\d+$/, '');
    if (!nome) return null;
    const heroi = corpo.querySelector('.fi-heroi') || corpo;
    const pot = (txt(corpo.querySelector('.pk-selo-pot b')).match(/P([1-5])/) || [])[1] || null;
    const qual = txt(corpo.querySelector('.pk-selo-q b')).replace(/[^\d,.]/g, '') || null;
    const stats = {};
    for (const linha of corpo.querySelectorAll('.fi-stat')) {
      const k = txt(linha.querySelector('.fi-stat-nome')).toUpperCase();
      if (!STATS.includes(k)) continue;
      stats[k] = {
        iv: Number((txt(linha.querySelector('.fi-iv')).match(/(\d+)/) || [])[1]) || null,
        atual: Number(digitos(txt(linha.querySelector('.fi-stat-num')))) || null,
      };
    }
    return {
      nome,
      shiny: !!corpo.querySelector('.hf-shiny'),
      nivel: Number(digitos(txt(heroi.querySelector('.fi-nivel-chip')))) || null,
      refino: Number(digitos(txt(heroi.querySelector('.selo-refino')))) || 0,
      aoe: !!heroi.querySelector('.tm-selo.aoe'),
      pot, qual, stats,
    };
  }

  /**
   * O texto da ficha no formato que o Hub lê. Com os seis stats atuais: o da tela do jogo (nome, Nv,
   * selos, Potência, Qualidade, stats com IV, "Stats-base da espécie"), que leva refino e AoE. Sem
   * eles: o da cópia "Copiar p/ ChatGPT" (espécie, nível, Potência, Qualidade e IVs).
   */
  function textoDaFicha(f) {
    if (!f?.nivel) return '';
    let qualPt = f.qual ? Number(String(f.qual).replace(/\./g, '').replace(',', '.')) : null;
    if (qualPt > 10) qualPt /= 1000; // "1.796" escrito com ponto
    if (!Number.isFinite(qualPt) || qualPt <= 0) qualPt = null;
    const completos = STATS.every(k => f.stats[k]?.iv && f.stats[k]?.atual);
    if (completos) {
      const l = [`${f.shiny ? '✨ Shiny ' : ''}${f.nome}${f.refino > 0 ? ` +${f.refino}` : ''}`, `Nv ${f.nivel}`];
      if (f.aoe) l.push('AoE');
      if (f.pot) l.push('Potência', `P${f.pot}`);
      if (qualPt) l.push('Qualidade', qualPt.toFixed(3).replace('.', ','));
      for (const k of STATS) l.push(k, String(f.stats[k].atual), `IV ${f.stats[k].iv}`);
      l.push('Stats-base da espécie');
      return l.join('\n');
    }
    const l = ['Ficha do PokeIdle (ThoseIdles)', `- Espécie: ${f.nome}`, `- Shiny: ${f.shiny ? 'sim' : 'não'}`, `- Nível: ${f.nivel}`];
    if (f.pot) l.push(`- Potência: P${f.pot}`);
    if (qualPt) l.push(`- Qualidade: ${qualPt.toFixed(3)}`);
    if (STATS.every(k => f.stats[k]?.iv)) {
      l.push('', '| Stat | IV |', '|---|---|');
      for (const k of STATS) l.push(`| ${k} | ${f.stats[k].iv} |`);
    }
    return l.join('\n');
  }

  /** A ficha aberta é de um anúncio do mercado (tem botão de comprar)? */
  function ehAnuncio(corpo) {
    const raiz = corpo.closest('[role="dialog"], .modal, .ficha, .fi-modal') || corpo;
    return [...raiz.querySelectorAll('button')].some(b => /^\s*(comprar|buy)\b/i.test(b.textContent || ''));
  }

  function daFicha(rota) {
    const corpo = document.getElementById('ficha-corpo');
    const f = corpo && lerFicha(corpo);
    if (!f) { UI()?.toast('PokéIdle Hub', 'Não consegui ler esta ficha; abrindo o Hub mesmo assim.', 'erro'); abrir(rota); return; }
    const ficha = textoDaFicha(f);
    if (rota === 'pokedex') { abrir('pokedex', { busca: f.nome }); return; }
    if (!ficha) UI()?.toast('PokéIdle Hub', 'A ficha não mostra o nível; cole-a à mão no Hub.', 'erro');
    abrir(rota, { ficha, alvo: rota === 'vale-a-pena' && ehAnuncio(corpo) ? 'candidato' : 'atual' });
  }

  const BOTOES = [
    ['ti-fi-dex', '📘 Pokédex', 'PokéIdle Hub: esta espécie na Pokédex', 'pokedex'],
    ['ti-fi-xp', '📈 Calcular XP', 'PokéIdle Hub: calculadora de XP com esta ficha', 'xp'],
    ['ti-fi-vale', '⚖ Vale a pena?', 'PokéIdle Hub: comparar este Pokémon (o seu, ou o do anúncio) com outro', 'vale-a-pena'],
  ];

  function prepararFicha() {
    const acoes = document.querySelector('#ficha-corpo .fi-acoes');
    if (!acoes || acoes.querySelector('#ti-fi-dex')) return;
    let depois = null;
    for (const [id, rotulo, titulo, rota] of BOTOES) {
      const b = document.createElement('button');
      b.type = 'button';
      b.id = id;
      b.className = 'fi-calc fi-acao';
      b.title = titulo;
      b.innerHTML = `<span>${rotulo}</span>`;
      b.onclick = () => daFicha(rota);
      if (depois) depois.after(b); else acoes.prepend(b);
      depois = b;
    }
  }

  let agendado = false;
  new MutationObserver(() => {
    if (agendado) return;
    agendado = true;
    requestAnimationFrame(() => { agendado = false; prepararFicha(); });
  // `document` e não `documentElement`: o script entra antes de a página ter o <html>.
  }).observe(document, { childList: true, subtree: true });

  function montar() {
    if (!UI() || !document.body) return;
    const b = UI().pill('ti-hub-btn', 'PokéIdle Hub: Pokédex, Calculadora de XP, Vale a pena?, Mercado e Guias', () => abrir('pokedex'));
    if (b && !b.innerHTML) b.innerHTML = '<i>📘</i><span>PokéIdle Hub</span>';
  }
  setInterval(montar, 2000);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar); else montar();

  // Para o autoteste.
  window.__tiHub = { lerFicha, textoDaFicha, ehAnuncio };
})();
