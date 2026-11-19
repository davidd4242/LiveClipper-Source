/* LiveClipper 启动遮罩（仅 window.chrome.webview 存在时启用；同一 WebView 合成面，不截图第二页面）。
 * 协议见文件末尾 PROTOCOL。经典同步脚本放在 <head>，在 React chunk 之前建立遮罩。 */
(function () {
  'use strict';
  var wv = window.chrome && window.chrome.webview;
  if (!wv || typeof wv.postMessage !== 'function' || typeof wv.addEventListener !== 'function') return;

  var LOGO_SRC = '/assets/liveclipper.ico';
  var LOGO_CSS_PX = 160;
  var BG = '#2c2c2e';
  var SPRING = { m: 1, c: 30, k: 300 }; // m*x'' + c*x' + k*x = 0
  var PULL_PX = 10;   // 先轻微向下拉
  var UP_PX = -28;    // 再上弹（translateY 负向）
  var FALL_MARGIN = 160; // 下落目标超出“上缘越过视口底”位置的余量
  var BG_RGB = [44, 44, 46]; // #2c2c2e，下落段连续过渡到 #000
  var SETTLE_ALPHA = 0.002;
  var SETTLE_VEL = 0.01;
  var STAGE_MAX = 4;
  var SPRING_ALPHA = SPRING.c / (2 * SPRING.m); // 欠阻尼：alpha=c/2m, omega=sqrt(k/m-alpha^2)
  var SPRING_OMEGA = Math.sqrt(SPRING.k / SPRING.m - SPRING_ALPHA * SPRING_ALPHA);

  var overlay, logo, msg, style;
  var root = null;
  var savedInert = null; // null=尚未接管；否则 {had:boolean,value:string}
  var stage = 0;
  var requested = false;
  var readySent = false;
  var exiting = false;
  var finished = false;
  var raf = 0;
  var abort = new AbortController();
  var sig = { signal: abort.signal };

  function post(o) { wv.postMessage(o); }
  function showMsg(text) { msg.textContent = text; }
  function fail(text) {
    showMsg(text);
    post({ type: 'lc-splash-error', message: text });
  }

  function build() {
    style = document.createElement('style');
    style.textContent =
      '#lc-splash{position:fixed;inset:0;z-index:2147483647;background:' + BG + ';overflow:hidden;' +
      'display:flex;align-items:center;justify-content:center;pointer-events:auto;user-select:none;cursor:default}' +
      '#lc-splash img{width:' + LOGO_CSS_PX + 'px;height:' + LOGO_CSS_PX + 'px;object-fit:contain;opacity:0;' +
      'transform:translateY(0);will-change:transform,opacity;pointer-events:none}' +
      '#lc-splash .lc-msg{position:absolute;left:24px;right:24px;top:calc(50% + ' + (LOGO_CSS_PX / 2 + 44) + 'px);' +
      'text-align:center;color:#c7c7cc;font:13px/1.5 "Segoe UI","Microsoft YaHei",sans-serif;word-break:break-word}';
    document.documentElement.appendChild(style);
    overlay = document.createElement('div');
    overlay.id = 'lc-splash';
    logo = new Image();
    logo.alt = '';
    logo.decoding = 'sync';
    logo.src = LOGO_SRC;
    msg = document.createElement('div');
    msg.className = 'lc-msg';
    msg.setAttribute('role', 'status'); // 空时不占视觉；文本变化由读屏播报
    msg.setAttribute('aria-live', 'polite');
    overlay.appendChild(logo);
    overlay.appendChild(msg);
    document.documentElement.appendChild(overlay);
    // 遮罩期间拦截键鼠，root inert 之外再兜底（焦点可能在 body）。
    ['keydown', 'keyup', 'keypress', 'contextmenu', 'wheel'].forEach(function (t) {
      window.addEventListener(t, function (e) { e.preventDefault(); e.stopPropagation(); }, { capture: true, passive: false, signal: abort.signal });
    });
  }

  function applyStage(s) {
    stage = s;
    if (exiting) return;
    logo.style.opacity = String(s * 0.2); // stage0..4 -> 0,.2,.4,.6,.8；第五真实阶段由 exit 门槛后置 1
  }

  function twoRaf(cb) {
    raf = requestAnimationFrame(function () { raf = requestAnimationFrame(function () { raf = 0; cb(); }); });
  }
  function whenVisible(cb) {
    if (document.visibilityState === 'visible') return cb();
    document.addEventListener('visibilitychange', function h() {
      if (document.visibilityState !== 'visible') return;
      document.removeEventListener('visibilitychange', h);
      cb();
    }, sig);
  }
  function viewOk() { return window.innerWidth > 0 && window.innerHeight > 0; }

  // ---- bootstrap 握手 ----
  function requestBootstrap() {
    requested = true;
    post({ type: 'lc-splash-bootstrap-request' });
  }
  function maybeReady() {
    if (readySent || !requested) return;
    twoRaf(function () {
      whenVisible(function () {
        if (readySent || !viewOk()) {
          if (!readySent) window.addEventListener('resize', function h() {
            if (!viewOk()) return;
            window.removeEventListener('resize', h);
            readySent = true;
            post({ type: 'lc-splash-bootstrap-ready' });
          }, sig);
          return;
        }
        readySent = true;
        post({ type: 'lc-splash-bootstrap-ready' });
      });
    });
  }

  // ---- 退出 ----
  function takeRoot() {
    root = document.getElementById('root');
    if (!root || savedInert) return;
    savedInert = { had: root.hasAttribute('inert'), value: root.getAttribute('inert') };
    root.setAttribute('inert', '');
  }
  function restoreRoot() {
    if (!root || !savedInert) return;
    if (savedInert.had) root.setAttribute('inert', savedInert.value || ''); else root.removeAttribute('inert');
    savedInert = null;
  }

  function gate() {
    var r = document.getElementById('root');
    if (!r || r.childElementCount === 0) return Promise.reject(new Error('root 为空，页面尚未渲染'));
    var rect = r.getBoundingClientRect();
    if (!(rect.width > 0 && rect.height > 0) || !viewOk()) return Promise.reject(new Error('root 或视口布局为零'));
    var fonts = document.fonts;
    var fontFamily = getComputedStyle(document.body || r).fontFamily;
    var p = fonts && fonts.ready ? fonts.ready : Promise.resolve();
    // 只核对实际使用的 family：load 仅在命中的 @font-face 失败时 reject；无命中即系统 fallback，合法。
    return p.then(function () {
      var spec = '16px ' + fontFamily;
      return fonts && fonts.load ? fonts.load(spec, '中文Aa0').then(function () {
        if (fonts.check && !fonts.check(spec, '中文Aa0')) throw new Error('实际字体未就绪: ' + fontFamily);
      }) : undefined;
    }).then(function () {
      return logo.decode ? logo.decode() : undefined;
    }).then(function () {
      if (!(logo.naturalWidth > 0)) throw new Error('Logo 未解码');
    });
  }

  function step(state, dt, target) {
    // 欠阻尼解析解（与 FilmCraftToolDrawer 同式），完整真实 dt。
    var offset = state.x - target;
    var decay = Math.exp(-SPRING_ALPHA * dt);
    var cosine = Math.cos(SPRING_OMEGA * dt);
    var sine = Math.sin(SPRING_OMEGA * dt);
    var v = state.v;
    state.x = target + decay * (offset * cosine + (v + SPRING_ALPHA * offset) / SPRING_OMEGA * sine);
    state.v = decay * (v * cosine - (SPRING_ALPHA * v + SPRING.k / SPRING.m * offset) / SPRING_OMEGA * sine);
  }

  function finish() {
    if (finished) return;
    finished = true;
    if (raf) cancelAnimationFrame(raf);
    raf = 0;
    abort.abort();
    restoreRoot();
    overlay.remove();
    style.remove();
    post({ type: 'lc-splash-complete' });
  }

  function animate() {
    var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var vh = window.innerHeight;
    var offY = vh / 2 + LOGO_CSS_PX / 2 + 2; // translateY 超过它时 Logo 上缘已越过视口底
    var fallTarget = offY + FALL_MARGIN;
    var last = 0;
    if (reduce) {
      var t0 = 0, D = 180;
      var fade = function (ts) {
        if (!t0) t0 = ts;
        var p = Math.min(1, (ts - t0) / D);
        overlay.style.opacity = String(1 - p);
        if (p >= 1) { raf = 0; finish(); } else raf = requestAnimationFrame(fade);
      };
      raf = requestAnimationFrame(fade);
      return;
    }
    var phase = 0; // 0 下拉, 1 上弹, 2 下落出屏, 3 遮罩 alpha 弹簧
    var sc = { x: 0, v: 0 }; // Logo translateY(px)，四阶段中同一 {x,v} 只换目标
    var al = { x: 1, v: 0 };
    var tick = function (ts) {
      var dt = last ? Math.max(0, (ts - last) / 1000) : 0; // 第一帧 dt=0：先显示初态
      last = ts;
      if (phase < 3) {
        step(sc, dt, phase === 0 ? PULL_PX : phase === 1 ? UP_PX : fallTarget);
        // 换相只看位置/速度：到达拉点附近或速度反向 → 上弹；到达上弹点附近或速度转为向下 → 下落
        if (phase === 0 && (sc.x >= PULL_PX - 1 || sc.v < 0)) phase = 1;
        else if (phase === 1 && (sc.x <= UP_PX + 3 || (sc.v >= 0 && sc.x < 0))) phase = 2;
        var p = phase === 2 ? Math.max(0, Math.min(1, sc.x / offY)) : 0;
        var gray = function (c) { return String(Math.round(c * (1 - p))); };
        overlay.style.background = 'rgb(' + gray(BG_RGB[0]) + ',' + gray(BG_RGB[1]) + ',' + gray(BG_RGB[2]) + ')';
        logo.style.transform = 'translateY(' + sc.x + 'px)';
        if (phase === 2 && sc.x > offY) { overlay.style.background = '#000'; phase = 3; }
      } else {
        step(al, dt, 0);
        var a = Math.max(0, Math.min(1, al.x));
        overlay.style.opacity = String(a);
        if (Math.abs(al.x) < SETTLE_ALPHA && Math.abs(al.v) < SETTLE_VEL) { raf = 0; finish(); return; }
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
  }

  function onExit() {
    if (exiting || finished) return;
    takeRoot();
    gate().then(function () {
      if (finished) return;
      whenVisible(function () {
        twoRaf(function () {
          if (finished) return;
          exiting = true;
          logo.style.opacity = '1'; // 第五真实阶段
          twoRaf(animate);
        });
      });
    }).catch(function (e) {
      fail('启动遮罩退出门槛失败: ' + (e && e.message ? e.message : String(e)));
    });
  }

  function onMessage(ev) {
    var d = ev && ev.data;
    if (!d || typeof d !== 'object' || typeof d.type !== 'string') return;
    switch (d.type) {
      case 'lc-splash-stage':
        if (typeof d.stage === 'number' && Number.isInteger(d.stage) && d.stage >= 0 && d.stage <= STAGE_MAX) {
          applyStage(d.stage);
          if (requested && !readySent) maybeReady();
        }
        break;
      case 'lc-splash-exit': onExit(); break;
      case 'lc-splash-error':
        if (typeof d.message === 'string' && d.message) showMsg(d.message);
        break;
    }
  }

  build();
  wv.addEventListener('message', onMessage, sig);

  // DOM ready + Logo decode 后发起握手
  var domReady = new Promise(function (res) {
    if (document.readyState !== 'loading') res();
    else document.addEventListener('DOMContentLoaded', function () { res(); }, { once: true, signal: abort.signal });
  });
  Promise.all([domReady, logo.decode()]).then(function () {
    if (finished) return;
    takeRoot();
    requestBootstrap();
  }).catch(function (e) {
    fail('Logo 解码失败: ' + (e && e.message ? e.message : String(e)));
  });

  /* PROTOCOL
   * host -> page (chrome.webview message, e.data 为对象):
   *   {type:'lc-splash-stage', stage:0..4}   同步进度；logo.opacity = stage*0.2；也是 bootstrap-request 的回复快照
   *   {type:'lc-splash-exit'}                首屏数据 ready 后触发；页面自测字体/Logo/root 门槛
   *   {type:'lc-splash-error', message}      显示原因，保留进度，不退出
   * page -> host (chrome.webview.postMessage):
   *   {type:'lc-splash-bootstrap-request'}   DOM ready + Logo decode 完
   *   {type:'lc-splash-bootstrap-ready'}     已应用 stage 快照并 2 rAF、visible、视口非零
   *   {type:'lc-splash-error', message}      Logo 解码/exit 门槛失败（可再发 exit 重试，不自动重试）
   *   {type:'lc-splash-complete'}            遮罩已移除，root inert 已恢复
   */
})();
