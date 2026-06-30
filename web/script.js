/* ============================================================================
   Dockable — landing page behaviour
   ----------------------------------------------------------------------------
   1. Steam link — set this once when you have the store URL / App ID.
   ============================================================================ */
const STEAM_URL = "#"; // ← replace with e.g. "https://store.steampowered.com/app/APPID/Dockable/"

(function applySteamLinks() {
  const placeholder = STEAM_URL === "#";
  document.querySelectorAll("[data-steam]").forEach((a) => {
    a.href = STEAM_URL;
    if (placeholder) {
      // Until the real link is set, don't navigate anywhere.
      a.addEventListener("click", (e) => e.preventDefault());
      a.title = "Steam page coming soon";
    } else {
      a.target = "_blank";
      a.rel = "noopener";
    }
  });
})();

/* ============================================================================
   2. Theme — Auto (follow OS) / Light / Dark, mirroring the app's DockTheme.
      <html data-theme> is the user's choice; data-theme-resolved is what CSS uses.
   ============================================================================ */
(function theme() {
  const root = document.documentElement;
  const mq = window.matchMedia("(prefers-color-scheme: dark)");
  const STORE_KEY = "dockable-theme";
  const ORDER = ["auto", "light", "dark"];

  let choice = localStorage.getItem(STORE_KEY) || "auto";

  // Labels come from i18n (theme_auto/theme_light/theme_dark + theme_word).
  const label = (c) => (window.Dockable ? Dockable.t("theme_" + c) : c);

  function resolve() {
    const resolved = choice === "auto" ? (mq.matches ? "dark" : "light") : choice;
    root.setAttribute("data-theme", choice);
    root.setAttribute("data-theme-resolved", resolved);
    const toggle = document.getElementById("themeToggle");
    if (toggle) {
      const word = window.Dockable ? Dockable.t("theme_word") : "Theme";
      toggle.title = word + ": " + label(choice);
      const lbl = toggle.querySelector(".theme-toggle-label");
      if (lbl) lbl.textContent = label(choice);
    }
  }

  function set(next) {
    choice = next;
    localStorage.setItem(STORE_KEY, choice);
    resolve();
  }

  // Toggle cycles Auto → Light → Dark.
  const toggle = document.getElementById("themeToggle");
  if (toggle) toggle.addEventListener("click", () => set(ORDER[(ORDER.indexOf(choice) + 1) % ORDER.length]));

  // Theme showcase tiles set the theme directly.
  document.querySelectorAll("[data-set-theme]").forEach((el) =>
    el.addEventListener("click", () => set(el.getAttribute("data-set-theme")))
  );

  // Re-resolve live when the OS theme flips (only matters in Auto).
  mq.addEventListener("change", () => { if (choice === "auto") resolve(); });

  // Re-label the toggle when the language changes.
  if (window.Dockable) Dockable.onLangChange(resolve);

  resolve();
})();

/* ============================================================================
   3. Dock magnification — fisheye via a raised-cosine falloff, like the app's
      DockLayoutEngine. Each icon scales by its distance from the cursor.
   ============================================================================ */
(function magnify() {
  const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const dock = document.getElementById("heroDock");
  if (!dock || reduce) return;

  const items = [...dock.querySelectorAll(".dock-item")];
  const MAX = 1.6;    // peak factor
  const RADIUS = 170; // px of cursor influence

  // Each icon's factor drives its width/height via calc(), so the flex bar
  // reflows: neighbours part and the bar widens — icons can't overlap. The CSS
  // transition on --s smooths it (the app smooths the same value per frame).
  function update(clientX) {
    for (const item of items) {
      const r = item.getBoundingClientRect();
      const center = r.left + r.width / 2;
      const d = Math.abs(clientX - center);
      let s = 1;
      if (d < RADIUS) {
        // raised cosine: peak at the cursor, smoothly back to 1 at the radius
        const f = 0.5 * (1 + Math.cos((d / RADIUS) * Math.PI));
        s = 1 + (MAX - 1) * f;
      }
      item.style.setProperty("--s", s.toFixed(3));
    }
  }
  function reset() { for (const item of items) item.style.setProperty("--s", "1"); }

  dock.addEventListener("mousemove", (e) => update(e.clientX));
  dock.addEventListener("mouseleave", reset);
})();

/* ============================================================================
   4. Scroll reveals — fade/slide elements in as they enter the viewport.
   ============================================================================ */
(function reveals() {
  const els = document.querySelectorAll(".reveal, .reveal-soft");
  if (!("IntersectionObserver" in window)) {
    els.forEach((el) => el.classList.add("in"));
    return;
  }
  const io = new IntersectionObserver(
    (entries) => entries.forEach((en) => { if (en.isIntersecting) { en.target.classList.add("in"); io.unobserve(en.target); } }),
    { threshold: 0.15, rootMargin: "0px 0px -8% 0px" }
  );
  els.forEach((el) => io.observe(el));
})();

/* ============================================================================
   4b. Scroll-driven per-letter reveal of the big feature words.
       Each word is split into letter spans; on scroll we compute how far the word
       has travelled up the viewport (0→1) and drive every letter from it with a
       stagger, so the letters rise / un-blur / flip up progressively — scrubbed by
       scroll position (forward scrolling down, reversed scrolling up). Done in JS
       (not CSS scroll-timelines) so it works the same in every browser.
   ============================================================================ */
(function scrollLetters() {
  const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  if (reduce) return; // leave the block-level .reveal fallback in place

  const clampN = (x, a, b) => (x < a ? a : x > b ? b : x);
  const ss = (t) => t * t * (3 - 2 * t);
  const STAGGER = 0.7; // how far the first letters lead the last ones

  // (Re)split every feature word into letter spans. Re-run on language change
  // (i18n resets the heading text first, then calls this via onLangChange).
  const words = [];
  function buildAll() {
    words.length = 0;
    document.querySelectorAll(".feature-word").forEach((word) => {
    const h2 = word.querySelector("h2");
    if (!h2) return;

    // Rebuild the heading: wrap each visible character in a span (keeping spaces as
    // plain text and any <br> intact).
    const letters = [];
    const frag = document.createDocumentFragment();
    [...h2.childNodes].forEach((node) => {
      if (node.nodeType === Node.TEXT_NODE) {
        // Group each word's letters into a .word span (white-space: nowrap) with a
        // normal breaking space between words — so the heading wraps per word, never
        // mid-word. An authored <br> still forces its break.
        let wordEl = null;
        for (const ch of node.textContent) {
          if (ch === " " || ch.charCodeAt(0) === 0xa0) {
            wordEl = null;
            frag.appendChild(document.createTextNode(" "));
          } else {
            if (!wordEl) {
              wordEl = document.createElement("span");
              wordEl.className = "word";
              frag.appendChild(wordEl);
            }
            const sp = document.createElement("span");
            sp.className = "ltr";
            sp.textContent = ch;
            wordEl.appendChild(sp);
            letters.push(sp);
          }
        }
      } else if (node.nodeName === "BR") {
        frag.appendChild(document.createElement("br"));
      } else {
        frag.appendChild(node.cloneNode(true));
      }
    });
    h2.textContent = "";
    h2.appendChild(frag);

    word.classList.remove("reveal");   // we drive the letters now, not the block fade
    word.classList.add("scroll-letters");
    words.push({ h2, letters });
    });
  }
  buildAll();

  if (!words.length) return;

  let ticking = false;
  function frame() {
    ticking = false;
    const vh = window.innerHeight;
    for (const { h2, letters } of words) {
      const rect = h2.getBoundingClientRect();
      if (rect.bottom < -80 || rect.top > vh + 80) continue; // off-screen — skip
      // Word progress: 0 when its centre sits at the viewport bottom, 1 once it has
      // risen to ~38% from the top. Recomputed each scroll → scrubbed & reversible.
      const cy = rect.top + rect.height / 2;
      const p = clampN((vh - cy) / (vh * 0.62), 0, 1);
      const n = letters.length;
      for (let i = 0; i < n; i++) {
        const lead = n > 1 ? i / (n - 1) : 0;              // 0 (first letter) … 1 (last)
        const e = ss(clampN(p * (1 + STAGGER) - lead * STAGGER, 0, 1));
        const inv = 1 - e;
        const s = letters[i].style;
        s.opacity = e.toFixed(3);
        s.transform = `translateY(${(inv * 0.5).toFixed(3)}em) translateZ(${(inv * -80).toFixed(1)}px) rotateX(${(inv * -55).toFixed(1)}deg)`;
        s.filter = inv > 0.01 ? `blur(${(inv * 8).toFixed(2)}px)` : "none";
      }
    }
  }
  function onScroll() { if (!ticking) { ticking = true; requestAnimationFrame(frame); } }

  window.addEventListener("scroll", onScroll, { passive: true });
  window.addEventListener("resize", onScroll);

  // Re-split when the language changes (i18n has already reset the heading text).
  if (window.Dockable) Dockable.onLangChange(() => { buildAll(); frame(); });

  frame(); // initial paint
})();

/* ============================================================================
   5. Minimize-effect demos — Genie, Suck & Scale as a REAL per-row mesh warp.

   CSS transforms are affine, so they can't bend a window into a genie neck.
   Instead each demo draws a window bitmap onto a <canvas>, sliced into ~74
   horizontal strips; per strip we compute a destination y, width and centre so
   the mesh deforms — exactly how the app's WPF 3-D mesh warps the captured
   window. The loop plays minimize → hold → restore, only while on screen.
   ============================================================================ */
(function effects() {
  const canvases = document.querySelectorAll(".effect-canvas");
  if (!canvases.length) return;
  const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  /* ---- math helpers ---- */
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const lerp = (a, b, t) => a + (b - a) * t;
  const smooth = (a, b, x) => { const t = clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); };
  const ss = (t) => t * t * (3 - 2 * t);                       // SmoothStep, like the app
  const easeIO = (t) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);

  function roundRect(ctx, x, y, w, h, r) {
    r = Math.min(r, w / 2, h / 2);
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }

  /* ---- the window bitmap (rebuilt per theme) — a Windows 11 styled window:
         small rounded corners, app icon + title on the left, minimize / maximize /
         close caption buttons on the right, and an Explorer-style nav pane. ---- */
  function buildWindow(dark) {
    const bw = 460, bh = 300, r = 10, titleH = 40;
    const c = document.createElement("canvas");
    c.width = bw; c.height = bh;
    const x = c.getContext("2d");
    roundRect(x, 0.5, 0.5, bw - 1, bh - 1, r); x.save(); x.clip();

    // window body + title bar (Win11 mica-ish: title bar a touch lighter than the body)
    x.fillStyle = dark ? "#202020" : "#f3f3f3"; x.fillRect(0, 0, bw, bh);
    x.fillStyle = dark ? "#2b2b2b" : "#ffffff"; x.fillRect(0, 0, bw, titleH);

    // app icon (left) + title placeholder
    const iconSize = 18, iconX = 14, iconY = (titleH - iconSize) / 2;
    roundRect(x, iconX, iconY, iconSize, iconSize, 4);
    const ig = x.createLinearGradient(iconX, iconY, iconX, iconY + iconSize);
    ig.addColorStop(0, "#5cb8f7"); ig.addColorStop(1, "#1f76e6");
    x.fillStyle = ig; x.fill();
    x.fillStyle = dark ? "#3a3a3a" : "#dcdcdc";
    roundRect(x, iconX + iconSize + 10, titleH / 2 - 5, 120, 10, 5); x.fill();

    // caption buttons: minimize ─, maximize ▢, close ✕ (neutral glyphs, Win11 style)
    const glyph = dark ? "#e6e6e6" : "#1a1a1a";
    const btnW = 44, cy = titleH / 2;
    x.strokeStyle = glyph; x.lineWidth = 1.3;
    const mMin = bw - btnW * 2.5;                       // minimize centre
    x.beginPath(); x.moveTo(mMin - 6, cy); x.lineTo(mMin + 6, cy); x.stroke();
    const mMax = bw - btnW * 1.5;                       // maximize centre
    x.strokeRect(mMax - 5.5, cy - 5.5, 11, 11);
    const mCls = bw - btnW * 0.5;                       // close centre
    x.beginPath();
    x.moveTo(mCls - 5.5, cy - 5.5); x.lineTo(mCls + 5.5, cy + 5.5);
    x.moveTo(mCls + 5.5, cy - 5.5); x.lineTo(mCls - 5.5, cy + 5.5); x.stroke();

    // title-bar separator
    x.strokeStyle = dark ? "rgba(255,255,255,0.06)" : "rgba(0,0,0,0.07)"; x.lineWidth = 1;
    x.beginPath(); x.moveTo(0, titleH + 0.5); x.lineTo(bw, titleH + 0.5); x.stroke();

    // nav pane (Explorer/Settings-style) with one selected item + accent bar
    const sideW = 130;
    x.fillStyle = dark ? "#272727" : "#eaeaea"; x.fillRect(0, titleH, sideW, bh - titleH);
    for (let i = 0; i < 6; i++) {
      const iy = titleH + 12 + i * 30;
      if (i === 1) {
        x.fillStyle = dark ? "#323232" : "#ffffff";
        roundRect(x, 8, iy, sideW - 16, 22, 6); x.fill();
        x.fillStyle = "#1f76e6"; roundRect(x, 8, iy + 5, 3, 12, 1.5); x.fill();
      }
      x.fillStyle = dark ? "#3f3f3f" : "#d2d2d2";
      roundRect(x, 18, iy + 7, 80, 8, 4); x.fill();
    }

    // content: a hero card + text lines
    const cxs = sideW + 16, cys = titleH + 16;
    const g = x.createLinearGradient(cxs, cys, bw, bh);
    g.addColorStop(0, dark ? "#2b4d7a" : "#cfe6ff");
    g.addColorStop(1, dark ? "#3b2f6b" : "#e9d8ff");
    roundRect(x, cxs, cys, bw - cxs - 16, 96, 8); x.fillStyle = g; x.fill();
    x.fillStyle = dark ? "#3a3a42" : "#d6dae2";
    for (let i = 0; i < 4; i++) { roundRect(x, cxs, cys + 112 + i * 20, (i % 2 ? bw - cxs - 60 : bw - cxs - 16), 11, 5); x.fill(); }

    x.restore();
    roundRect(x, 0.5, 0.5, bw - 1, bh - 1, r);
    x.strokeStyle = dark ? "rgba(255,255,255,0.10)" : "rgba(0,0,0,0.12)"; x.lineWidth = 1; x.stroke();
    return c;
  }

  /* ---- per-row warp models. Each returns {y, w, cx} for source fraction v. ---- */

  // Faithful port of GenieAnimator.UpdateMeshGenie (the C# "Genie" style). Every mesh row v
  // warps on its own: a staggered flow where the dock-side edge leads (Stagger), a front-loaded
  // horizontal pinch to the tile width with a mid-neck belly (ShapeEnd + WidthBulge), then a
  // descent into the tile (decoupled from the pinch). `warp` is LINEAR 0→1 — the smoothing is
  // internal (SmoothStep on the shape/descent), exactly as the app feeds it from the render loop.
  // StyleParams for GenieStyle.Genie: Stagger 0.5, WidthBulge 0.35, ShapeEnd 0.66, DescendStart 0.
  const STAGGER = 0.5, SHAPE_END = 0.66, BULGE = 0.35;
  function genie(v, warp, G) {
    // Window is above the dock here, so the dock-side edge is the bottom → lead = 1 - v
    // (LeadsFromTop() is false when the target sits below the window's centre).
    const lead = 1 - v;
    const lp = clamp(warp * (1 + STAGGER) - lead * STAGGER, 0, 1);
    const eShape = ss(clamp(lp / SHAPE_END, 0, 1));         // pinch finishes by lp = ShapeEnd
    const eDescend = ss(lp);                                // DescendStart = 0 → descend the whole time
    const cx = lerp(G.Xw, G.Xd, eShape);                   // row centre slides toward the tile
    const baseW = lerp(G.Wfull, G.Wd, eShape);             // taper body → tile (neck) width
    const bulge = BULGE * G.Wfull * Math.sin(Math.PI * eShape); // belly the mid-neck (smoke into a bottle)
    const w = Math.max(G.Wd, baseW + bulge);
    const y = lerp(G.Yw + v * G.H, G.Yd, eDescend);
    return { y, w, cx };
  }
  function suck(v, p, G) {
    const ep = smooth(0, 1, p);
    const lead = 0.8;
    const lp = clamp(ep * (1 + lead) - (1 - v) * lead, 0, 1); // bottom whips down first
    return {
      y: lerp(G.Yw + v * G.H, G.Yd, lp),
      w: G.Wfull * Math.pow(1 - lp, 1.6),                    // funnels to a point
      cx: lerp(G.Xw, G.Xd, Math.pow(lp, 1.4)),
    };
  }

  function drawMesh(ctx, bmp, p, G, fn) {
    const alpha = 1 - smooth(0.86, 1, p);
    if (alpha <= 0) return;
    ctx.save(); ctx.globalAlpha = alpha;
    const N = 74, bw = bmp.width, bh = bmp.height;
    let prev = fn(0, p, G);
    for (let i = 1; i <= N; i++) {
      const v = i / N, cur = fn(v, p, G);
      const h = cur.y - prev.y;
      const wMid = (prev.w + cur.w) / 2, cxMid = (prev.cx + cur.cx) / 2;
      if (h > 0.05 && wMid > 0.5) {
        ctx.drawImage(bmp, 0, ((i - 1) / N) * bh, bw, (1 / N) * bh, cxMid - wMid / 2, prev.y, wMid, h + 0.6);
      }
      prev = cur;
    }
    ctx.restore();
  }
  function drawScale(ctx, bmp, p, G) {
    const ep = easeIO(p), alpha = 1 - smooth(0.9, 1, p);
    const w = lerp(G.Wfull, G.Wd, ep), h = lerp(G.H, G.Wd, ep);
    const x = lerp(G.Xw, G.Xd, ep) - w / 2, y = lerp(G.Yw, G.Yd, ep);
    ctx.save(); ctx.globalAlpha = alpha; ctx.drawImage(bmp, x, y, w, h); ctx.restore();
  }

  /* ---- timeline: rest → minimize → hold → restore → rest (per-effect tempo) ----
     Warp durations track the app's StyleParams: Genie ≈430 ms, Suck ≈300 ms — eased up
     a touch here so they read on a page, with a hold so both directions are visible. */
  const TIMING = {
    genie: { rest1: 0.45, down: 0.62, hold: 0.6, up: 0.62, rest2: 0.45 },
    suck:  { rest1: 0.45, down: 0.44, hold: 0.55, up: 0.44, rest2: 0.45 },
    scale: { rest1: 0.45, down: 0.5,  hold: 0.55, up: 0.5,  rest2: 0.45 },
  };
  function phase(t, d) {
    let x = t % (d.rest1 + d.down + d.hold + d.up + d.rest2);
    if (x < d.rest1) return 0; x -= d.rest1;
    if (x < d.down) return x / d.down; x -= d.down;
    if (x < d.hold) return 1; x -= d.hold;
    if (x < d.up) return 1 - x / d.up;
    return 0;
  }

  /* ---- one demo per canvas ---- */
  const demos = [...canvases].map((canvas) => {
    const effect = canvas.parentElement.getAttribute("data-effect");
    const ctx = canvas.getContext("2d");
    const d = { canvas, ctx, effect, timing: TIMING[effect] || TIMING.scale,
                active: false, theme: null, bmp: null, W: 0, H: 0, G: null };

    d.resize = function () {
      const r = canvas.getBoundingClientRect();
      if (!r.width) return;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      canvas.width = Math.round(r.width * dpr);
      canvas.height = Math.round(r.height * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      d.W = r.width; d.H = r.height;
      const W = d.W, H = d.H;
      const barW = Math.min(220, W * 0.74), barH = 46;
      const Wfull = Math.min(230, W * 0.6);
      const barY = H - barH - 16, tileY = barY + (barH - 30) / 2;
      // Wd is the dock tile width: the genie neck shrinks only to this (lands as the thumbnail).
      // Yd targets the tile centre — where the window pours in.
      d.G = { barW, barH, barX: (W - barW) / 2, barY, tileY,
              Xw: W / 2, Yw: 24, Wfull, H: Wfull * 0.62,
              Xd: W / 2, Yd: tileY + 15, Wd: 30 };
    };

    d.drawDock = function () {
      const G = d.G, dark = d.theme === "dark";
      roundRect(ctx, G.barX, G.barY, G.barW, G.barH, 16);
      ctx.fillStyle = dark ? "rgba(40,40,46,0.85)" : "rgba(255,255,255,0.75)";
      ctx.fill();
      ctx.strokeStyle = dark ? "rgba(255,255,255,0.12)" : "rgba(0,0,0,0.08)";
      ctx.lineWidth = 1; ctx.stroke();
      const centers = [G.Xd - 42, G.Xd, G.Xd + 42];
      centers.forEach((cx, i) => {
        roundRect(ctx, cx - 15, G.tileY, 30, 30, 9);
        const grad = ctx.createLinearGradient(cx, G.tileY, cx, G.tileY + 30);
        if (i === 1) { grad.addColorStop(0, "#5cb8f7"); grad.addColorStop(1, "#1f76e6"); }
        else if (dark) { grad.addColorStop(0, "#4a4f59"); grad.addColorStop(1, "#33373f"); }
        else { grad.addColorStop(0, "#aeb4be"); grad.addColorStop(1, "#878d99"); }
        ctx.fillStyle = grad; ctx.fill();
      });
    };

    d.render = function (now) {
      // (re)build the window bitmap when the resolved theme changes
      const theme = document.documentElement.getAttribute("data-theme-resolved") || "light";
      if (theme !== d.theme) { d.theme = theme; d.bmp = buildWindow(theme === "dark"); }
      if (!d.G) d.resize();
      if (!d.G) return;            // canvas not laid out yet (zero size)
      const G = d.G;
      ctx.clearRect(0, 0, d.W, d.H);
      d.drawDock();
      const p = reduce ? 0 : phase(now / 1000, d.timing);
      if (d.effect === "scale") drawScale(ctx, d.bmp, p, G);
      else drawMesh(ctx, d.bmp, p, G, d.effect === "suck" ? suck : genie);
    };

    new ResizeObserver(() => { d.resize(); if (reduce) d.render(0); }).observe(canvas);
    return d;
  });

  // Only animate demos that are on screen.
  if ("IntersectionObserver" in window) {
    const io = new IntersectionObserver(
      (entries) => entries.forEach((en) => {
        const demo = demos.find((x) => x.canvas === en.target);
        if (demo) demo.active = en.isIntersecting;
      }),
      { threshold: 0.25 }
    );
    demos.forEach((d) => io.observe(d.canvas));
  } else {
    demos.forEach((d) => (d.active = true));
  }

  if (reduce) {
    // Static rest frame — no animation.
    demos.forEach((d) => requestAnimationFrame(() => d.render(0)));
    return;
  }

  function loop(now) {
    for (const d of demos) if (d.active) d.render(now);
    requestAnimationFrame(loop);
  }
  requestAnimationFrame(loop);
})();

/* ============================================================================
   6. Hero parallax — drift the dock & title gently as you scroll past the hero.
   ============================================================================ */
(function parallax() {
  const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const hero = document.getElementById("hero");
  const dock = document.querySelector(".hero-dock-stage");
  const content = document.querySelector(".hero-content");
  if (reduce || !hero) return;

  let ticking = false;
  function frame() {
    const y = window.scrollY;
    const h = hero.offsetHeight || 1;
    const p = Math.min(y / h, 1); // 0 → 1 across the hero
    if (content) { content.style.transform = `translateY(${p * -40}px)`; content.style.opacity = (1 - p * 1.1).toFixed(3); }
    if (dock) dock.style.transform = `translateY(${p * 60}px) scale(${1 - p * 0.08})`;
    ticking = false;
  }
  window.addEventListener("scroll", () => { if (!ticking) { ticking = true; requestAnimationFrame(frame); } }, { passive: true });
  frame();
})();

/* ============================================================================
   7. Live clock in the menu-bar demo.
   ============================================================================ */
(function clock() {
  const el = document.getElementById("mbClock");
  if (!el) return;
  const tick = () => {
    const loc = window.Dockable ? Dockable.lang : [];
    el.textContent = new Date().toLocaleTimeString(loc, { hour: "numeric", minute: "2-digit" });
  };
  tick();
  setInterval(tick, 15000);
  if (window.Dockable) Dockable.onLangChange(tick); // re-format in the new locale
})();

/* ============================================================================
   8. Shrink the nav once you scroll off the hero (subtle).
   ============================================================================ */
(function navShadow() {
  const nav = document.getElementById("nav");
  if (!nav) return;
  const onScroll = () => nav.style.boxShadow = window.scrollY > 20 ? "0 4px 24px -12px rgba(0,0,0,.35)" : "none";
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();
})();
