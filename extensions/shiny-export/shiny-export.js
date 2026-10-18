// ThoseIdles — Exportar shinies (pokeidle.io)
//
// Põe um botão "Exportar TXT" na página Shinies do jogo. Ao clicar, percorre TODAS as páginas da
// lista com os filtros que estão na tela (busca, tipo, "só à venda", ordem) e baixa um .txt:
//
//   ESPECIE, POTENCIA, QUALIDADE, IVS, VALOR NO RMT
//
// Como funciona (e por que é seguro):
//  - Só LÊ. O único pedido enviado é o mesmo "shiny.listar" que o jogo manda quando você troca de
//    página na lista — um por vez, com intervalo de 0,8 s entre eles. Nenhuma ação de jogo.
//  - Usa a conexão que o próprio jogo já abriu (não abre outra), e no fim devolve a lista para a
//    página em que você estava.
//  - Nada sai do seu computador: o arquivo é gerado no navegador e baixado direto.
//
// Roda no contexto da página (world MAIN), antes do jogo abrir o socket. É o mesmo arquivo na
// extensão do Chrome e no app ThoseIdles.
(function () {
  'use strict';
  if (window.top !== window || window.__thoseIdlesShinyExport) return;
  window.__thoseIdlesShinyExport = true;

  const PAUSA_MS = 800;        // entre um pedido de página e o próximo
  const ESPERA_MS = 10000;     // sem resposta em 10 s: para
  const MAX_PAGINAS = 500;

  let socketDoJogo = null;
  let ultimoPedido = null;     // os filtros que a página usou por último
  let exportando = false;
  let aoReceberLista = null;

  // ---- observa o socket do jogo (sem mudar nada do que ele envia ou recebe) -----------------
  const Original = window.WebSocket;
  function WebSocketObservado(...args) {
    const ws = new Original(...args);
    try {
      const alvo = new URL(String(args[0]), location.href);
      if (alvo.host === location.host) {
        socketDoJogo = ws;
        const enviarOriginal = ws.send.bind(ws);
        ws.send = (dados) => {
          if (!exportando && typeof dados === 'string' && dados.includes('"shiny.listar"')) {
            try { ultimoPedido = JSON.parse(dados); } catch { /* ignora */ }
          }
          return enviarOriginal(dados);
        };
        ws.addEventListener('message', (ev) => {
          if (!aoReceberLista || typeof ev.data !== 'string') return;
          if (!ev.data.slice(0, 40).includes('"t":"shiny.lista"')) return;
          try { aoReceberLista(JSON.parse(ev.data)); } catch { /* ignora */ }
        });
      }
    } catch { /* URL estranha: não é o socket do jogo */ }
    return ws;
  }
  WebSocketObservado.prototype = Original.prototype;
  for (const k of ['CONNECTING', 'OPEN', 'CLOSING', 'CLOSED']) WebSocketObservado[k] = Original[k];
  window.WebSocket = WebSocketObservado;

  // ---- exportação ----------------------------------------------------------------------------
  const esperar = (ms) => new Promise((r) => setTimeout(r, ms));

  function pedirPagina(pedido) {
    return new Promise((resolve) => {
      const relogio = setTimeout(() => { aoReceberLista = null; resolve(null); }, ESPERA_MS);
      aoReceberLista = (m) => { clearTimeout(relogio); aoReceberLista = null; resolve(m); };
      socketDoJogo.send(JSON.stringify(pedido));
    });
  }

  function ivTotal(s) {
    const direto = s.iv ?? s.ivTotal;
    if (direto != null && Number.isFinite(Number(direto))) return Math.round(Number(direto));
    if (!s.ivs) return null;
    return Object.values(s.ivs).reduce((soma, v) => soma + (Number(v) || 0), 0);
  }

  const limpo = (v) => String(v ?? '').replace(/[\r\n,]+/g, ' ').trim();

  function linhaTxt(s) {
    const especie = limpo(s.nome || s.especie || s.speciesId || '?');
    const potencia = `P${Number(s.potencia) || 1}`;
    const q = Number(s.quality ?? s.qualidade);
    const qualidade = Number.isFinite(q) && q > 0 ? String(q) : '-';
    const iv = ivTotal(s);
    const detalhe = s.ivs && typeof s.ivs === 'object'
      ? ` (${Object.entries(s.ivs).map(([k, v]) => `${k} ${v}`).join(' / ')})` : '';
    const ivs = iv == null ? '-' : `${iv}${detalhe}`;
    const a = s.anuncio;
    let rmt = '-';
    if (a && a.moeda === 'orb') rmt = `${a.preco} Gemas`;
    else if (a) rmt = `- (à venda por ${a.preco} ${a.moeda === 'gold' ? 'ouro' : limpo(a.moeda)}, não é RMT)`;
    return `${especie}, ${potencia}, ${qualidade}, ${limpo(ivs)}, ${rmt}`;
  }

  function baixar(texto) {
    const d = new Date();
    const p = (n) => String(n).padStart(2, '0');
    const nome = `shinies-pokeidle-${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}.txt`;
    const url = URL.createObjectURL(new Blob(['﻿' + texto], { type: 'text/plain;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = nome;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  }

  async function exportar(botao) {
    if (exportando) return;
    if (!socketDoJogo || socketDoJogo.readyState !== 1) {
      botao.textContent = 'Sem conexão com o jogo';
      setTimeout(() => { botao.textContent = 'Exportar TXT'; }, 3000);
      return;
    }
    const base = { t: 'shiny.listar', busca: '', tipo: '', soAVenda: false, ordem: 'recentes', pagina: 0, ...(ultimoPedido ?? {}) };
    const paginaOriginal = base.pagina ?? 0;
    const linhas = [];
    let total = null;
    let erro = null;
    exportando = true;
    botao.disabled = true;
    try {
      for (let p = 0; p < MAX_PAGINAS; p++) {
        botao.textContent = `Exportando… ${linhas.length}${total != null ? `/${total}` : ''}`;
        const m = await pedirPagina({ ...base, pagina: p });
        if (!m) { erro = 'o jogo não respondeu'; break; }
        if (Number.isFinite(Number(m.total))) total = Number(m.total);
        const pagina = Array.isArray(m.linhas) ? m.linhas : [];
        if (!pagina.length) break;
        linhas.push(...pagina);
        if (total != null && linhas.length >= total) break;
        await esperar(PAUSA_MS);
      }
    } finally {
      exportando = false;
      // Devolve a lista para a página em que o jogador estava.
      try { socketDoJogo.send(JSON.stringify({ ...base, pagina: paginaOriginal })); } catch { /* ignora */ }
    }

    const filtros = [
      base.busca ? `busca "${base.busca}"` : null,
      base.tipo ? `tipo ${base.tipo}` : null,
      base.soAVenda ? 'só à venda' : null,
      base.ordem ? `ordem ${base.ordem}` : null,
    ].filter(Boolean).join(', ');
    const texto = [
      `# Shinies exportados de pokeidle.io em ${new Date().toLocaleString('pt-BR')}`,
      `# Filtros: ${filtros || 'nenhum'} — ${linhas.length} shiny(s)${total != null ? ` de ${total}` : ''}${erro ? ` (parou: ${erro})` : ''}`,
      '# VALOR NO RMT = preço do anúncio no Mercado em Gemas (a moeda que vira USDT); "-" = não está à venda em Gemas.',
      'ESPECIE, POTENCIA, QUALIDADE, IVS, VALOR NO RMT',
      ...linhas.map(linhaTxt),
      '',
    ].join('\r\n');
    baixar(texto);
    botao.disabled = false;
    botao.textContent = erro ? `Exportado ${linhas.length} (incompleto)` : `Exportado ${linhas.length} ✓`;
    setTimeout(() => { botao.textContent = 'Exportar TXT'; }, 5000);
  }

  // ---- o botão na página Shinies -------------------------------------------------------------
  function porBotao() {
    const conta = document.getElementById('shl-conta');
    if (!conta || document.getElementById('ti-shl-exportar')) return;
    const b = document.createElement('button');
    b.id = 'ti-shl-exportar';
    b.type = 'button';
    b.textContent = 'Exportar TXT';
    b.title = 'Baixa todos os shinies desta lista (com os filtros atuais) num .txt: espécie, potência, qualidade, IVs e valor no RMT';
    b.style.cssText = 'margin-left:8px;padding:2px 10px;border-radius:6px;border:1px solid #8b6cff;background:#2a1f4a;color:#fff;font:inherit;font-size:12px;cursor:pointer';
    b.addEventListener('click', (ev) => { ev.stopPropagation(); exportar(b); });
    conta.insertAdjacentElement('afterend', b);
  }
  const iniciar = () => {
    porBotao();
    new MutationObserver(porBotao).observe(document.body, { childList: true, subtree: true });
  };
  if (document.body) iniciar();
  else document.addEventListener('DOMContentLoaded', iniciar, { once: true });
})();
