# Dockable — landing page

A static, single-page, Apple-style marketing site for Dockable. No build step, no
dependencies — just open it or serve the folder.

## Files

- `index.html` — page structure & content (strings tagged with `data-i18n`)
- `styles.css` — design system (mirrors the app: glass dock, blue icon gradient, Light/Dark/Auto)
- `i18n.js` — localization runtime + string tables (en / pt-BR / es / uk / zh-Hans)
- `script.js` — theme resolution, dock magnification, scroll reveals, canvas minimize demos
- `assets/` — the app icon (copied from `src/Dockable/Assets`)

## Run it

Open `index.html` directly, or serve the folder (so relative asset paths resolve cleanly):

```powershell
python -m http.server 8080 --directory web
# then visit http://localhost:8080
```

Or with Docker (nginx, from the repo root) — serves on **http://localhost:8086**:

```powershell
docker compose up -d     # stop with: docker compose down
```

## Set the Steam link

When you have the Steam store URL / App ID, set it in **one place** — the top of `script.js`:

```js
const STEAM_URL = "https://store.steampowered.com/app/APPID/Dockable/";
```

Every "Get on Steam" button reads from it. Until it's set, the buttons are inert
(they don't navigate) and a "coming soon" note shows under the final CTA.

## Notes

- **Theme** follows the OS by default (Auto); the nav toggle cycles Auto → Light → Dark and
  persists the choice. The "Themes" section tiles set it directly.
- Big feature words announced on scroll: **App Launcher, Menu Bar, Genie, Suck, Scale, Liquid Glass**.
  They reveal **per-letter, scroll-driven**: `script.js` splits each word into letter spans and, on
  scroll, computes how far the word has risen up the viewport (0→1) and drives every letter from it
  with a stagger (rise + un-blur + flip-up). It's scrubbed by scroll position — forward scrolling
  down, reversed scrolling up — done in JS (not CSS scroll-timelines) so it behaves the same in every
  browser. Under `prefers-reduced-motion` it's skipped and the block-level `.reveal` fade is used.
- The hero dock magnifies by sizing each icon (driven by a registered `--s` custom property),
  so the bar reflows and widens — magnified icons never overlap.
- **Genie / Suck / Scale** are real per-row mesh warps on a `<canvas>` (CSS transforms are affine
  and can't bend a window into a neck): a window bitmap sliced into ~74 strips, each repositioned
  and resized per frame, looping minimize → restore. The **Genie** strip math is a direct port of
  the app's `GenieAnimator.UpdateMeshGenie` (`src/Dockable/Genie/GenieAnimator.cs`) — same
  staggered flow, front-loaded pinch (`ShapeEnd`), mid-neck bulge (`WidthBulge`), and tile-width
  landing. Keep them in sync if that file changes.
- Respects `prefers-reduced-motion` (disables parallax, magnification, and the looped warps).
- All visuals are CSS/SVG/canvas recreations of the app — no screenshots required.
- **Localized** in the same five languages as the app (English, Português BR, Español, Українська,
  中文). `i18n.js` holds in-code string tables keyed like the app's `LocData.cs`; English is the
  fallback. On first visit the language follows the browser, then the nav picker sets it (persisted).
  Switching is live — the theme labels, clock locale, and per-letter heading split all re-render.
  Add/adjust copy by editing the keyed tables in `i18n.js`; tag new HTML with `data-i18n` (text),
  `data-i18n-html` (markup), `data-i18n-label` (dock tooltip), or `data-i18n-aria` (aria-label).
