# Nailbiter mark

**Swing** is the approved Nailbiter identity: a white, angular N inspired by a
win-probability line on Nailbiter blue (`#3b82f6`). It represents the whole app,
not a particular sport. Team and league logos remain separate.

`nailbiter.svg` is the sole artwork source. Use the same mark on the marketing
site and every live-app page, in the shared header, browser tabs/bookmarks, and
phone home-screen shortcuts. Keep it static; do not recolor it by league or score.

## Generated assets

Both `apps/www/public/` and `apps/backend/wwwroot/` contain committed outputs so
the Astro site and .NET app can build and deploy independently. Do not hand-edit
those copies.

| Asset | Use |
| --- | --- |
| `favicon.svg` | Scalable browser icon and visible site mark |
| `favicon.ico` | 16, 32 and 48px compatibility icons; conventional root fallback |
| `apple-touch-icon.png` | 180px opaque iOS shortcut, device-applied corner mask |
| `icon-192.png`, `icon-512.png` | Opaque Android/browser shortcut images |
| `icon-maskable-512.png` | Inset artwork inside the central 80% safe circle |
| `site.webmanifest` | Origin-local shortcut metadata, deliberately `display: browser` |

There is no service worker or new standalone/installable-app behavior. The
manifest stays on each site's own origin and starts at `/`. Rounded SVG/ICO
corners suit tabs; phone assets have full square backgrounds for device masking.
The visible mark has empty alt text when adjacent to the Nailbiter name, avoiding
a duplicate screen-reader label.

## Rebuild and verify

From the repo root, using the Bun version pinned in `.mise.toml`:

```sh
bun install --frozen-lockfile
bun install --cwd apps/www --frozen-lockfile
bunx playwright install chromium
bun run icons:generate
bun run test:icons
```

Generation uses the repository's pinned Playwright/Chromium, without external
images, fonts or an image-service dependency. `test:icons` rejects missing,
outdated, or divergent outputs, builds the marketing page, and checks its icon
links plus raster sizes, opacity, ICO frames and the maskable safe area. Shared
live-page declarations and header identity are covered by `LayoutTests.fs` for
the home page and every sport, including World Cup and motorsport contexts.

For artwork changes, bump `swing-1` in `scripts/generate-icons.js`, the Astro
page, and `Rendering/Layout.fs` together, update the corresponding expectations,
and regenerate. Versioned links help browsers leave cached emoji icons behind;
`/favicon.ico` remains available for clients using the conventional fallback.
