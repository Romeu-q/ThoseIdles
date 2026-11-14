// ThoseIdles — repasse para o RESUMO das contas (só do app).
// Com a página do jogo aberta, o servidor já manda a ela o estado da conta ("welcome", "estado") e os
// eventos da caça ("batalha"). Este script entrega ao app uma CÓPIA dessas três mensagens, para o app
// somar abates, XP/h, ouro/h, capturas e shinies das quatro contas num lugar só. Não lê nada além disso
// e NÃO envia nada ao servidor nem a outro site: só chrome.webview.postMessage, para o app.
(() => {
  'use strict';
  if (window.top !== window || window.__tiRelay || !window.chrome?.webview) return;
  if (!/(^|\.)pokeidle\.io$/.test(location.hostname) && location.hostname !== 'localhost') return;
  window.__tiRelay = true;

  const TIPOS = /"t":"(batalha|estado|welcome)"/;
  const Original = window.WebSocket;
  window.WebSocket = new Proxy(Original, {
    construct(target, args) {
      const ws = Reflect.construct(target, args);
      try {
        if (new URL(String(args[0]), location.href).host === location.host) {
          ws.addEventListener('message', ev => {
            const d = ev.data;
            if (typeof d !== 'string' || !TIPOS.test(d.slice(0, 200))) return;
            try { window.chrome.webview.postMessage('J' + d); } catch { }
          });
        }
      } catch { }
      return ws;
    },
  });
})();
