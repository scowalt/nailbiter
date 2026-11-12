# Styling

The backend serves plain CSS from `apps/backend/wwwroot/css`. There is no CSS build step and no styling dependency: edit the files and reload. [ADR 0005](adr/0005-plain-layered-css.md) records why.

## Files

`Layout.fs` links the stylesheets in this order:

| File | Contents |
| --- | --- |
| `global.css` | The layer order, the design tokens and the base reset. |
| `components.css` | Site chrome and classes shared by more than one area, such as `panel`, `status-badge` and the probability bar. |
| `games.css` | Home, league, playoff bracket and game detail pages. |
| `world-cup.css` | World Cup pages. |
| `motorsport.css` | F1, NASCAR and IndyCar calendars, race detail pages and the race leaderboard component. |

When you change any stylesheet, change `styleReleaseVersion` in `apps/backend.fsharp/Rendering/Layout.fs` so browsers fetch the new files.

## Layers

`global.css` declares `@layer theme, base, components;` once. Every rule belongs to one of these layers:

- `theme` holds the design tokens as custom properties on `:root`: colors, type scale, radii and fonts. Use a token instead of repeating a raw value that has one.
- `base` holds the element reset and page-wide element rules, such as `body`, `a` and the scrollbar.
- `components` holds semantic classes.

## Light and dark

The site follows the operating system setting; there is no manual toggle. `:root` in the `theme` layer holds the dark palette. A `@media (prefers-color-scheme: light)` block in the same layer overrides the tokens that differ. Components use only tokens, so a new color needs a token with a value in both blocks. Use `--color-ink` for a tint that must contrast with the page, and `--color-white` only for a color that is white in both schemes. The win probability chart reads its axis colors from the tokens when it renders.

Team colors from the server are chosen for contrast on the dark card, so they can be pale on the light theme.

Do not use `!important` or raise specificity to win a conflict. Fix the rule order or the class design instead. Only third-party CSS may be unlayered.

## Class names

Name a class for what the element is, such as `race-card` or `hype-score`, not for how it looks. Do not recreate utility classes. Express a state or variant as an extra class on the same element, such as `league-game is-live` or `bracket-team is-winner`, with a `.base.modifier` rule that contains only the declarations that change. Show and hide elements with the `hidden` attribute. The `[hidden]` rule is in `base`, so a class on an element that can be hidden must not set `display`.

Renderers build conditional class names with plain string interpolation. Tests select elements by these semantic classes, by IDs or by data attributes.
