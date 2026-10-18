// ThoseIdles — peças de interface compartilhadas pelos painéis do jogo (🔔 RMT, 🎯 Sniper, 💰 Preço).
// Um estilo só para os três: barra de botões no canto, painel com cabeçalho, cartões, interruptores,
// campos e avisos. Carregado ANTES dos scripts das ferramentas (no app e em cada extensão do Chrome,
// que leva uma cópia deste arquivo — ver tools/sync-extensions.ps1).
(() => {
  'use strict';
  if (window.top !== window || window.__tiUI) return;

  const CSS = `
  .ti-dock{position:fixed;left:12px;bottom:12px;z-index:2147483000;display:flex;gap:8px;font-family:'Segoe UI',system-ui,sans-serif}
  .ti-pill{display:inline-flex;align-items:center;gap:7px;height:32px;padding:0 14px;border-radius:16px;
    background:rgba(23,21,31,.94);border:1px solid #3a3550;color:#ece9f5;font:600 12.5px 'Segoe UI',system-ui,sans-serif;
    cursor:pointer;box-shadow:0 4px 14px rgba(0,0,0,.45);transition:border-color .15s,transform .15s}
  .ti-pill:hover{border-color:#8b6cff;transform:translateY(-1px)}
  .ti-pill.ativo{border-color:#8b6cff;background:#2a2342}
  .ti-pill.alerta{background:#5a1d22;border-color:#e5484d}
  .ti-badge{min-width:18px;height:18px;padding:0 5px;border-radius:9px;background:#ff5d6c;color:#fff;font-size:11px;
    display:inline-flex;align-items:center;justify-content:center}

  .ti-panel{position:fixed;left:12px;bottom:54px;z-index:2147483000;width:440px;max-width:calc(100vw - 24px);max-height:74vh;
    display:flex;flex-direction:column;background:#16141e;border:1px solid #2f2b40;border-radius:16px;color:#ece9f5;
    font:13px/1.45 'Segoe UI',system-ui,sans-serif;box-shadow:0 18px 50px rgba(0,0,0,.6);overflow:hidden;animation:ti-in .14s ease-out}
  @keyframes ti-in{from{opacity:0;transform:translateY(6px)}to{opacity:1;transform:none}}
  .ti-head{display:flex;align-items:center;gap:12px;padding:14px 16px;background:linear-gradient(180deg,#211d30,#1a1726);border-bottom:1px solid #2a2638}
  .ti-head-ico{width:36px;height:36px;border-radius:10px;background:#2c2547;display:flex;align-items:center;justify-content:center;font-size:19px}
  .ti-head-txt{flex:1;min-width:0}
  .ti-head-txt b{display:block;font-size:15px}
  .ti-head-txt span{display:block;color:#9e99b3;font-size:12px}
  .ti-x{width:30px;height:30px;border:0;border-radius:8px;background:transparent;color:#9e99b3;font-size:18px;cursor:pointer}
  .ti-x:hover{background:#2a2638;color:#fff}
  .ti-body{padding:6px 16px 16px;overflow:auto}

  .ti-sec{display:flex;align-items:center;justify-content:space-between;margin:16px 0 8px;
    font-size:11px;font-weight:700;letter-spacing:.07em;text-transform:uppercase;color:#9e99b3}
  .ti-card{background:#1e1b29;border:1px solid #2a2638;border-radius:12px;padding:12px;margin:8px 0}
  .ti-card.off{opacity:.55}
  .ti-row{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
  .ti-row + .ti-row{margin-top:8px}
  .ti-grow{flex:1;min-width:0}
  .ti-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:8px;margin-top:8px}
  .ti-grid.g2{grid-template-columns:1fr 1fr} .ti-grid.g6{grid-template-columns:repeat(6,1fr)}
  .ti-muted{color:#9e99b3;font-size:12px}
  .ti-empty{text-align:center;color:#9e99b3;padding:18px 8px;font-size:12.5px;border:1px dashed #2f2b40;border-radius:12px}

  .ti-field{display:flex;flex-direction:column;gap:4px;font-size:11px;font-weight:600;color:#9e99b3}
  .ti-in{width:100%;height:32px;padding:0 10px;border-radius:8px;border:1px solid #3a3550;background:#100e17;color:#ece9f5;
    font:13px 'Segoe UI',system-ui,sans-serif;box-sizing:border-box}
  .ti-in:focus{outline:none;border-color:#8b6cff;box-shadow:0 0 0 3px rgba(139,108,255,.18)}
  select.ti-in{cursor:pointer}

  .ti-btn{height:32px;padding:0 14px;border-radius:8px;border:1px solid #3a3550;background:#262236;color:#ece9f5;
    font:600 12.5px 'Segoe UI',system-ui,sans-serif;cursor:pointer;white-space:nowrap}
  .ti-btn:hover{border-color:#8b6cff}
  .ti-btn.primary{background:#8b6cff;border-color:#8b6cff;color:#fff} .ti-btn.primary:hover{background:#9b80ff}
  .ti-btn.ghost{background:transparent} .ti-btn.danger{background:transparent;border-color:#5a2a33;color:#ff8a95}
  .ti-btn.sm{height:26px;padding:0 10px;font-size:11.5px} .ti-btn.full{width:100%}

  .ti-switch{display:inline-flex;align-items:center;gap:9px;cursor:pointer;font-size:12.5px;color:#ece9f5;user-select:none}
  .ti-switch input{appearance:none;-webkit-appearance:none;flex:none;width:36px;height:20px;border-radius:10px;background:#3a3550;
    position:relative;cursor:pointer;margin:0;transition:background .15s}
  .ti-switch input::after{content:'';position:absolute;left:3px;top:3px;width:14px;height:14px;border-radius:50%;background:#cfcbe0;transition:left .15s}
  .ti-switch input:checked{background:#8b6cff} .ti-switch input:checked::after{left:19px;background:#fff}
  .ti-switch.red input:checked{background:#e5484d}

  .ti-chip{display:inline-flex;align-items:center;gap:4px;height:20px;padding:0 8px;border-radius:10px;background:#2a2638;
    font-size:11px;font-weight:600;color:#cfcbe0;white-space:nowrap}
  .ti-chip.ok{background:#173323;color:#6fe39a} .ti-chip.no{background:#3a1d24;color:#ff8a95}
  .ti-chip.warn{background:#3a3020;color:#ffd27a} .ti-chip.roxo{background:#2c2547;color:#c7b8ff}

  .ti-hero{display:flex;align-items:center;gap:12px;padding:14px;border-radius:12px;background:#1e1b29;border:1px solid #2a2638;margin-top:12px}
  .ti-hero.on{background:#2a1418;border-color:#e5484d}
  .ti-hero b{font-size:14px}

  .ti-price{display:grid;grid-template-columns:1fr 1fr;gap:8px;margin-top:10px}
  .ti-price > div{background:#141220;border:1px solid #2a2638;border-radius:10px;padding:10px}
  .ti-price small{display:block;font-size:10.5px;font-weight:700;letter-spacing:.06em;text-transform:uppercase;color:#9e99b3}
  .ti-price .v{display:block;font-size:19px;font-weight:800;color:#fff;margin:2px 0}
  .ti-price .gem .v{color:#c7b8ff} .ti-price .gold .v{color:#ffd27a}
  .ti-price em{font-style:normal;font-size:11px;color:#9e99b3}
  .ti-bar{height:6px;border-radius:3px;background:#2a2638;overflow:hidden;margin-top:6px}
  .ti-bar i{display:block;height:100%;background:linear-gradient(90deg,#8b6cff,#c7b8ff);border-radius:3px}

  .ti-log{display:flex;gap:8px;align-items:baseline;font-size:12px;padding:6px 0;border-bottom:1px solid #221f2e}
  .ti-log time{color:#7d7893;font-variant-numeric:tabular-nums}

  .ti-toast{position:fixed;right:16px;z-index:2147483001;width:330px;background:#1d1a28;border:1px solid #3a3550;
    border-left:4px solid #8b6cff;border-radius:12px;color:#ece9f5;font:12.5px/1.4 'Segoe UI',system-ui,sans-serif;
    padding:11px 13px;box-shadow:0 10px 30px rgba(0,0,0,.55);animation:ti-in .14s ease-out}
  .ti-toast b{display:block;font-size:13px;margin-bottom:2px}
  .ti-toast.ok{border-left-color:#3fc46e} .ti-toast.erro{border-left-color:#e5484d}
  `;

  function injetarCss() {
    if (document.getElementById('ti-ui-css')) return;
    const s = document.createElement('style');
    s.id = 'ti-ui-css';
    s.textContent = CSS;
    (document.head || document.documentElement).appendChild(s);
  }

  function dock() {
    injetarCss();
    let d = document.getElementById('ti-dock');
    if (!d) { d = document.createElement('div'); d.id = 'ti-dock'; d.className = 'ti-dock'; document.body.appendChild(d); }
    return d;
  }

  /** Botão da barra do canto. Devolve o botão (ou o que já existia). */
  function pill(id, titulo, onclick) {
    if (!document.body) return null;
    let b = document.getElementById(id);
    if (b) return b;
    b = document.createElement('button');
    b.id = id;
    b.className = 'ti-pill';
    b.title = titulo;
    b.onclick = onclick;
    dock().appendChild(b);
    return b;
  }

  /**
   * Painel com cabeçalho. Só um aberto por vez: abrir um fecha os outros.
   * Devolve { el, body } ou null se fechou.
   */
  const fechadores = new Map();
  function painel(id, { icone, titulo, sub }, aoFechar) {
    injetarCss();
    for (const [outro, fechar] of fechadores) if (outro !== id) fechar();
    let el = document.getElementById(id);
    if (!el) {
      el = document.createElement('div');
      el.id = id;
      el.className = 'ti-panel';
      el.innerHTML = `<div class="ti-head"><div class="ti-head-ico">${icone}</div>
        <div class="ti-head-txt"><b>${esc(titulo)}</b><span>${esc(sub || '')}</span></div>
        <button class="ti-x" title="Fechar">✕</button></div><div class="ti-body"></div>`;
      el.querySelector('.ti-x').onclick = () => aoFechar();
      document.body.appendChild(el);
      fechadores.set(id, aoFechar);
    }
    return { el, body: el.querySelector('.ti-body') };
  }

  function fecharPainel(id) {
    document.getElementById(id)?.remove();
    fechadores.delete(id);
  }

  let toasts = 0;
  /** Aviso no canto direito. tipo: '' | 'ok' | 'erro'. */
  function toast(titulo, texto, tipo = '') {
    try {
      injetarCss();
      const t = document.createElement('div');
      t.className = `ti-toast ${tipo}`;
      t.style.top = `${16 + (toasts++ % 4) * 70}px`;
      t.innerHTML = `<b>${esc(titulo)}</b>${esc(texto)}`;
      document.body.appendChild(t);
      setTimeout(() => t.remove(), 12000);
    } catch { }
  }

  function bipe() {
    try {
      const ctx = new AudioContext(); const o = ctx.createOscillator(); const g = ctx.createGain();
      o.frequency.value = 880; g.gain.value = 0.05; o.connect(g); g.connect(ctx.destination);
      o.start(); o.stop(ctx.currentTime + 0.15); o.onended = () => ctx.close();
    } catch { }
  }

  const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const opcoes = (atual, lista) => lista.map(([v, txt]) => `<option value="${esc(v)}" ${String(atual) === String(v) ? 'selected' : ''}>${esc(txt)}</option>`).join('');
  const hora = ms => new Date(ms).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

  window.__tiUI = { injetarCss, dock, pill, painel, fecharPainel, toast, bipe, esc, opcoes, hora };
})();
