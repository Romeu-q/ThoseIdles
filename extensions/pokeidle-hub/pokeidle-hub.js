// ThoseIdles — PokéIdle Hub (https://pokeidleguide.io) dentro do app.
// Na ficha de qualquer Pokémon do jogo entram os botões "📘 Pokédex", "📈 Calcular XP" e
// "⚖ Vale a pena?", junto das ações da ficha. Cada um pede ao app para abrir o Hub numa janela
// (no perfil desta conta) na página certa, já com a ficha do Pokémon colada — o mesmo texto que você
// copiaria à mão "do nome até Stats-base da espécie". No perfil do treinador, abaixo das moedas, o
// botão "📘 PokéIdle Hub" abre o site (Pokédex, Calculadora de XP, Hunt, Vale a pena?, Mercado, Guias),
// e "📈 Calcular XP" abre a Calculadora JÁ PREENCHIDA com o que o jogo diz da conta: nível do
// treinador, Pokémon ativo (espécie, nível, Potência, Qualidade, IVs) e os bônus de XP ligados (VIP,
// boosts, guild, Twitch/Kick, evento, Atrasados).
// "Ir caçar": a Calculadora mostra um "▶ Caçar" em cada hunt; o app pede a esta página `cacar(slug)`,
// que manda ao servidor a MESMA mensagem do clique no mapa ("hunt.select"). Fora isso, nada é enviado.
// Só do app (usa chrome.webview).
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

  // ------------------------------------------------------------------ o estado da conta (só leitura)

  // O servidor manda o estado em DELTA (só o que mudou; pokémon em pkMud/pkFora). Remontagem igual à
  // do jogo (shared/estado-delta.mjs): `cheio` recomeça, chave ausente = não mudou.
  let campos = null;
  let pokemons = new Map();
  let ws = null;
  let hunts = []; // as do welcome: { slug, nivel, ... }
  function mesclar(parcial) {
    if (!parcial || typeof parcial !== 'object') return;
    const { cheio, pokemons: lista, pkMud, pkFora, dexMud, dexFora, ...resto } = parcial;
    if (cheio || !campos) {
      campos = resto;
      pokemons = new Map((lista ?? []).map(k => [k.id, k]));
      return;
    }
    Object.assign(campos, resto);
    if (lista) pokemons = new Map(lista.map(k => [k.id, k]));
    for (const k of pkMud ?? []) pokemons.set(k.id, k);
    for (const id of pkFora ?? []) pokemons.delete(id);
  }
  const Original = window.WebSocket;
  window.WebSocket = new Proxy(Original, {
    construct(target, args) {
      const s = Reflect.construct(target, args);
      try {
        if (new URL(String(args[0]), location.href).host === location.host) {
          ws = s;
          campos = null;
          s.addEventListener('message', ev => {
            if (typeof ev.data !== 'string' || !ev.data.includes('"estado"')) return;
            let m;
            try { m = JSON.parse(ev.data); } catch { return; }
            if (m?.t === 'welcome') {
              if (Array.isArray(m.hunts)) hunts = m.hunts;
              if (m.estado) mesclar({ ...m.estado, cheio: true });
            } else if (m?.t === 'estado' && m.estado) mesclar(m.estado);
          });
        }
      } catch { }
      return s;
    },
  });

  /** Os bônus de XP ligados agora, no formato das "escolhas" da Calculadora do Hub. */
  function escolhasDeXp(e, agora = Date.now()) {
    const l = e.loja ?? {};
    const vale = t => Number(t) > agora;
    const ev = e.evento && vale(e.evento.terminaEm) ? e.evento : null;
    const rank = Number(e.guildBonusPct) > 0 ? Number(e.guildBonusRank) || 0 : 0;
    const alvoAtr = Number(l.atrasados?.alvo) || 0;
    return {
      vip: vale(l.vipAte),
      xpBoost: vale(l.boosts?.xp),
      xpBoostPokemon: vale(l.boosts?.pokexp),
      guildPosicao: rank <= 0 ? 0 : Math.min(rank, 10),
      boostGuild: vale(e.guild?.boostAte),
      twitch: vale(e.twitch?.bonus?.xp),
      kick: vale(e.kick?.bonus?.xp),
      eventoPct: ev ? Number(ev.xpTreinadorPct || ev.xpPokemonPct) || 0 : 0,
      atrasados: !!l.atrasados && (Number(e.level) || 0) < alvoAtr,
      ...(alvoAtr ? { atrasadosAlvo: alvoAtr } : {}),
    };
  }

  /** O Pokémon ativo como a cópia "Copiar p/ ChatGPT" (o Hub lê espécie, nível, Potência, Qualidade, IVs). */
  function fichaDoAtivo(e) {
    const p = pokemons.get(e.activeId);
    if (!p) return '';
    const nome = p.nome || p.especie || '';
    if (!nome || !p.level) return '';
    const l = ['Ficha do PokeIdle (ThoseIdles)', `- Espécie: ${nome}`, `- Shiny: ${p.shiny || p.isShiny || p.lookShiny === 1 ? 'sim' : 'não'}`, `- Nível: ${Math.floor(p.level)}`];
    const pot = Number(p.potencia ?? p.ficha?.potencia);
    if (pot >= 1 && pot <= 5) l.push(`- Potência: P${pot}`);
    let q = Number(p.qualidade ?? p.quality);
    if (q > 10) q /= 1000;
    if (q > 0) l.push(`- Qualidade: ${q.toFixed(3)}`);
    const iv = p.ivs && typeof p.ivs === 'object' ? p.ivs : null;
    const chaves = { HP: 'hp', ATK: 'atk', DEF: 'def', 'SP.ATK': 'spAtk', 'SP.DEF': 'spDef', SPD: 'speed' };
    if (iv && STATS.every(k => Number(iv[chaves[k]]) >= 1)) {
      l.push('', '| Stat | IV |', '|---|---|');
      for (const k of STATS) l.push(`| ${k} | ${iv[chaves[k]]} |`);
    }
    return l.join('\n');
  }

  /** O que a Calculadora de XP precisa da conta. null se o estado ainda não chegou. */
  function dadosDeXp() {
    const e = campos;
    if (!e || !(Number(e.level) > 0)) return null;
    return { nivelTreinador: Math.floor(e.level), escolhas: escolhasDeXp(e), ativo: fichaDoAtivo(e) };
  }

  function abrirCalculadora() {
    const xp = dadosDeXp();
    if (!xp) { UI()?.toast('Calcular XP', 'Ainda não recebi os dados da conta; abrindo a calculadora vazia.', 'erro'); abrir('xp'); return; }
    abrir('xp', { xp, ficha: xp.ativo || undefined });
  }

  // ------------------------------------------------------------------ ir caçar (pedido pelo app)

  /**
   * Manda a conta caçar na hunt `slug`: a mesma mensagem do clique no mapa. Não sai do Safari (o jogo
   * pede confirmação para isso). Responde ao app { ti: 'cacou', slug, ok, msg } quando a hunt mudar.
   */
  async function cacar(slug) {
    const responder = (ok, msg) => { try { window.chrome.webview.postMessage({ ti: 'cacou', slug, ok, msg }); } catch { } };
    slug = String(slug || '');
    if (!/^[a-z0-9][a-z0-9_-]{0,63}$/i.test(slug)) return responder(false, 'Hunt inválida.');
    if (!ws || ws.readyState !== 1 || !campos) return responder(false, 'O jogo desta conta ainda não está conectado.');
    const h = hunts.find(x => x.slug === slug);
    if (hunts.length && !h) return responder(false, 'O jogo não tem essa hunt.');
    if (h && Number(h.nivel) > (Number(campos.level) || 0)) return responder(false, `Essa hunt pede Nv ${h.nivel}.`);
    if (campos.safari?.dentro && !campos.noCentro) return responder(false, 'A conta está no Safari: saia por lá antes (o jogo pede confirmação).');
    if (campos.huntSlug === slug) return responder(true, 'A conta já está caçando aí.');
    ws.send(JSON.stringify({ t: 'hunt.select', slug }));
    for (let i = 0; i < 20 && campos.huntSlug !== slug; i++) await new Promise(r => setTimeout(r, 200));
    if (campos.huntSlug === slug) {
      responder(true, `Pronto: caçando em ${h?.nome ?? slug}.`);
      UI()?.toast('Ir caçar', `Caçando em ${h?.nome ?? slug} (pedido do PokéIdle Hub).`, 'ok');
    } else responder(false, 'O jogo não trocou de hunt (a troca pode estar travada por alguns segundos, ou você está em luta/boss).');
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
    // Na Calculadora de XP vão junto o nível do treinador e os bônus da conta.
    const xp = rota === 'xp' ? dadosDeXp() : null;
    abrir(rota, { ficha, alvo: rota === 'vale-a-pena' && ehAnuncio(corpo) ? 'candidato' : 'atual', ...(xp ? { xp } : {}) });
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
    const x = UI().pill('ti-xp-btn', 'Calculadora de XP do PokéIdle Hub, já com o seu nível, o Pokémon ativo e os bônus ligados', abrirCalculadora);
    if (x && !x.innerHTML) x.innerHTML = '<i>📈</i><span>Calcular XP</span>';
  }
  setInterval(montar, 2000);
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', montar); else montar();

  // O app chama `cacar`; o autoteste usa o resto.
  window.__tiHub = { lerFicha, textoDaFicha, ehAnuncio, dadosDeXp, cacar, mesclar };
})();
