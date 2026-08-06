# Tokens

Source of truth: `tokens/modernist.styles.css` (copied verbatim). Written guidance:
`tokens/modernist.readme.md`.

## Colour roles

| Token | Value | Use |
| --- | --- | --- |
| `--color-bg` | `#f3f2f2` | Page ground |
| `--color-surface` | `#eae9e9` | Card fill |
| `--color-text` | `#201e1d` | Ink, and **gain** |
| `--color-accent` | `#ec3013` | Primary action, emphasis, **loss/attention** at display size |
| `--color-divider` | `color-mix(in srgb, #201e1d 40%, transparent)` | 2px section rules, 1px control borders |

### Neutral ramp
`100 #f8f4f4` · `200 #eae7e7` · `300 #d7d3d3` · `400 #bab6b6` · `500 #9b9797` ·
`600 #7d7979` · `700 #605d5d` · `800 #444141` · `900 #2d2b2b`

### Accent ramp
`100 #fff2ef` · `200 #ffe0d9` · `300 #ffc4b8` · `400 #ff9783` · `500 #ff563c` ·
`600 #dd2b0f` · `700 #ae1800` · `800 #7c1405` · `900 #4d170e`

Where each step is used in these screens:

- `accent-100` — selected row fill, active alert lane, callout background, "winner" cell
- `accent-200` — best-rank cell tint in the strategy matrix
- `accent-700` — **all accent-coloured body text** (loss figures, block detail, links).
  The base accent is only 3:1 on this ground, which is enough for chrome and large type
  but not body copy.
- `accent-800` / `accent-900` — text on an `accent-100` fill
- `neutral-100` — operational strip / summary band fill
- `neutral-200` — row hover, neutral status band
- `neutral-300` — 1px row rules, internal cell dividers
- `neutral-700` — micro labels, secondary text, "flat" direction
- `neutral-800` — secondary body copy

### Direction (gain / loss / flat)

Never colour alone. Every direction value carries **glyph + sign + colour**:

| Direction | Colour | Glyph | Sign |
| --- | --- | --- | --- |
| Gain | `var(--color-text)` | `▲` U+25B2 | `+` |
| Loss | `var(--color-accent-700)` | `▼` U+25BC | `−` U+2212 |
| Flat / unknown | `var(--color-neutral-700)` | `—` U+2014 | none |

Use U+2212 MINUS SIGN, not a hyphen, so figures align in tabular numerals.

This replaces the current `--good`/`--bad` pair. It is deliberately mono: the system has
one accent, so red means *loss, risk, attention* consistently, and gain is plain ink. It
also means the existing `data-theme="cvd"` scheme needs no separate treatment — but keep
the scheme switch, and keep `.value-signed` semantics.

### Agreement flag (TradingFlow verdict vs Market Predictor)

| State | When | Glyph | Colour | Band fill |
| --- | --- | --- | --- | --- |
| AGREE | eligibility and model point the same way | `●` U+25CF | `--color-text` | `--color-neutral-200` |
| CONFLICT | eligible but model signal is opposite | `◆` U+25C6 | `--color-accent-700` | `--color-accent-100` |
| PARTIAL | either side is neutral | `○` U+25CB | `--color-neutral-700` | `--color-neutral-200` |

## Type

Archivo throughout. `--font-heading` weight **800**, body **400**, emphasis 600/700.

| Role | Size | Weight | Tracking | Notes |
| --- | --- | --- | --- | --- |
| Page title | 27–30px | 800 | −0.02em | 1.05 line-height |
| Big numeric (KPI) | 22px | 800 | — | tabular-nums |
| Focus price | 34px | 800 | — | tabular-nums |
| Symbol in rail | 26px | 800 | −0.02em | |
| Section heading | 13px | 800 | 0.06em | uppercase |
| Sub-heading | 11px | 800 | 0.07em | uppercase |
| Body | 13px | 400 | — | `text-wrap: pretty` on paragraphs |
| Table cell | 13px | 400 | — | 12px in compact density |
| Table header | 10px | 700 | 0.08em | uppercase, `--color-neutral-700` |
| Cell sub-line | 10.5px | 400 | — | `--color-neutral-700` |
| Micro label | 9.5px | 700 | 0.07em | uppercase, `--color-neutral-700` |
| Tag / chip | 10.5–12px | 700 | 0.04–0.06em | |
| Monospace | 10.5–11.5px | 400 | — | `ui-monospace, monospace` — ids and query strings only |

Every numeric column, clock, price, quantity and percentage gets
`font-variant-numeric: tabular-nums`.

Minimum body size on desktop is 11px (micro labels only); nothing interactive is below
12px. On mobile nothing is below 11px and all targets are ≥44px.

## Spacing

`--space-1..8` = `4 · 8 · 12 · 16 · 24 · 32` px. Radius is **0** everywhere — do not
round a corner. Shadows only for overlays: `--shadow-lg` on the dialog and toast.

## Structure rules

- **2px** `--color-divider` between major sections and above table headers.
- **1px** `--color-neutral-300` between rows and between cells inside a metric strip.
- Everything flush left, including button labels. `.btn-block` is
  `justify-content: flex-start`.
- Metric strips are equal-width grid cells with 1px vertical rules and **no** outer
  radius; the strip itself is bounded by 2px rules top and bottom.
- Focus is `outline: 2px solid var(--color-accent); outline-offset: 2px` — already the
  design system default. Do not leave the browser default anywhere.

## Mapping onto the existing `site.css`

Keep the existing custom-property names so the three schemes keep working; change only
the values in `:root[data-theme="light"]` and add Modernist as the default.

| `site.css` | Modernist value |
| --- | --- |
| `--bg` | `#f3f2f2` |
| `--panel` | `#f3f2f2` (flat — panels are ruled, not filled) |
| `--panel-subtle` | `#f8f4f4` |
| `--panel-inset` | `#eae7e7` |
| `--ink` | `#201e1d` |
| `--muted` | `#605d5d` |
| `--line` | `#d7d3d3` |
| `--line-control` | `color-mix(in srgb, #201e1d 40%, transparent)` |
| `--accent` | `#ec3013` |
| `--accent-strong` | `#dd2b0f` |
| `--accent-surface` | `#fff2ef` |
| `--on-accent` | `#f3f2f2` |
| `--good` | `#201e1d` |
| `--good-surface` | `#eae7e7` |
| `--bad` | `#ae1800` |
| `--bad-surface` | `#fff2ef` |
| `--warn` | `#ae1800` |
| `--focus-ring` | `#ec3013` |

Then set every `border-radius` in `site.css` to `0`, and change the `.panel` rule from a
filled card to a ruled block.

## Icons used

Lucide, inline SVG, `fill="none" stroke="currentColor" stroke-width="2"`,
`stroke-linecap="round" stroke-linejoin="round"`, `viewBox="0 0 24 24"`.
Where a Lucide icon has a rounded `rx`, set `rx="0"`.

`bell` · `refresh-cw` · `columns-3` · `play` · `square` · `layers` · `trending-up` ·
`clock` · `briefcase` · `newspaper` · `chevron-right` · `chevron-left` · `check` ·
`download` · `calendar` · `arrow-right` · `area-chart` · `wifi` · `battery`
