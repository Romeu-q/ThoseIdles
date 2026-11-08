// ThoseIdles — roda DENTRO da janela do PokéIdle Hub (pokeidle-gg.pages.dev), só no app.
// Em cada hunt que a Calculadora de XP mostra (rota otimizada, hunt fixa, ouro por hora) entra um
// botão "▶ Caçar": ele pede ao app para levar a conta até essa hunt (o app repassa ao jogo, que manda
// a mesma mensagem do clique no mapa). A resposta volta como um aviso no canto do Hub.
(() => {
  'use strict';
  if (window.top !== window || window.__tiHubSite || !window.chrome?.webview) return;
  if (location.hostname !== 'pokeidle-gg.pages.dev') return;
  window.__tiHubSite = true;

  const CSS = `
  .ti-cacar{margin-left:8px;padding:3px 9px;border-radius:6px;border:1px solid #3fc46e;background:#173a24;color:#c9f5d6;
    font:700 11px/1.2 'Segoe UI',system-ui,sans-serif;cursor:pointer;white-space:nowrap;vertical-align:middle}
  .ti-cacar:hover{background:#1f5131}
  .ti-cacar[disabled]{opacity:.6;cursor:default}
  .ti-cacar-aviso{position:fixed;right:16px;bottom:16px;z-index:99999;max-width:340px;padding:10px 14px;border-radius:10px;
    background:#17151f;border-left:4px solid #3fc46e;color:#ece9f5;font:13px/1.4 'Segoe UI',system-ui,sans-serif;
    box-shadow:0 10px 30px rgba(0,0,0,.55)}
  .ti-cacar-aviso.erro{border-left-color:#e5484d}`;

  // nome|nível → slug, do mesmo arquivo de hunts que a Calculadora usa.
  let mapa = null;
  const chave = (nome, nivel) => `${String(nome).trim().toLowerCase()}|${nivel}`;
  fetch('/data/jogo/hunts.json', { cache: 'no-cache' }).then(r => r.json()).then(d => {
    mapa = new Map();
    for (const h of d.hunts ?? []) mapa.set(chave(h.nome ?? h.slug, Number(h.nivel) || 1), h.slug);
    marcar();
  }).catch(() => { });

  function slugDe(el) {
    const linha = el.closest('tr[data-slug]');
    if (linha?.dataset.slug) return linha.dataset.slug;
    const nome = el.querySelector('.xpc-hunt-texto b')?.textContent;
    const nv = (el.querySelector('.xpc-hunt-texto small')?.textContent.match(/Nv\s*([\d.]+)/) || [])[1];
    if (!nome || !nv || !mapa) return null;
    return mapa.get(chave(nome, Number(nv.replace(/\./g, '')))) ?? null;
  }

  function marcar() {
    if (!document.getElementById('ti-hub-site-css')) {
      const s = document.createElement('style');
      s.id = 'ti-hub-site-css';
      s.textContent = CSS;
      (document.head || document.documentElement).appendChild(s);
    }
    for (const el of document.querySelectorAll('.xpc-tabela .xpc-hunt')) {
      if (el.querySelector('.ti-cacar')) continue;
      const slug = slugDe(el);
      if (!slug) continue;
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'ti-cacar';
      b.textContent = '▶ Caçar';
      b.title = 'ThoseIdles: levar a conta para caçar aqui agora';
      b.dataset.slug = slug;
      b.addEventListener('click', ev => {
        ev.preventDefault();
        ev.stopPropagation(); // a linha da "hunt fixa" também reage ao clique
        b.disabled = true;
        b.textContent = 'Indo…';
        try { window.chrome.webview.postMessage({ ti: 'cacar', slug }); } catch { }
        setTimeout(() => { b.disabled = false; b.textContent = '▶ Caçar'; }, 5000);
      });
      (el.querySelector('.xpc-hunt-texto b') || el).after(b);
    }
  }

  /** O app responde aqui: aviso no canto da janela. */
  window.__tiCacou = (ok, msg) => {
    document.querySelector('.ti-cacar-aviso')?.remove();
    const d = document.createElement('div');
    d.className = 'ti-cacar-aviso' + (ok ? '' : ' erro');
    d.textContent = msg;
    document.body.appendChild(d);
    setTimeout(() => d.remove(), 5000);
    for (const b of document.querySelectorAll('.ti-cacar[disabled]')) { b.disabled = false; b.textContent = '▶ Caçar'; }
  };

  let agendado = false;
  new MutationObserver(() => {
    if (agendado) return;
    agendado = true;
    requestAnimationFrame(() => { agendado = false; marcar(); });
  }).observe(document, { childList: true, subtree: true });
})();
