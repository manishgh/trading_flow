# TradingFlow UI Design Review

Status: proposed
Review date: 2026-08-01
Companion to `docs/ui-trading-desk-plan.md` (capability gaps). This document covers the
experience layer: colour, information density, accessibility, and mobile haptics.

Measurements are computed, not estimated. Contrast values use the WCAG relative-luminance formula;
colour-vision-deficiency values use a Brettel/Viénot-style simulation in linear space.

## 1. Summary Of Findings

| Area | Finding | Severity |
|---|---|---|
| Colour | Gain and loss have near-identical luminance (1.14) and fail CVD separation | High |
| Colour | Control borders measure 1.48:1 — fails WCAG 1.4.11, repeated 48 times | High |
| Colour | No dark theme, so Audit invented its own inline dark theme | Medium |
| Density | Symbol rail stacks 7 headed sections; signal type rendered twice | High |
| Density | 7-item status strip always occupies the top of the desk viewport | Medium |
| Accessibility | Touch targets, landmarks, focus, live regions are genuinely good | — |
| Haptics | Zero haptics; `VIBRATE` permission absent from the manifest | Medium |

## 2. Colour

### 2.1 What is measured

Current tokens, contrast against panel white and page `#f4f6f8`:

| Token | Hex | On `#fff` | On page | Verdict |
|---|---|---:|---:|---|
| `--text-primary` | `#17202a` | 16.45 | 15.19 | Pass |
| `--text-secondary` | `#5f6b7a` | 5.43 | 5.01 | Pass |
| `--action-primary` | `#176b87` | 6.02 | 5.56 | Pass |
| `--state-positive` | `#116d42` | 6.38 | 5.89 | Pass |
| `--state-negative` | `#a12a2a` | 7.29 | 6.73 | Pass |
| `--state-warning` | `#8a5a00` | 5.93 | 5.47 | Pass |
| `--focus-ring` | `#2563eb` | 5.17 | 4.77 | Pass |
| `--border-default` | `#cbd5e1` | **1.48** | **1.37** | **Fail (1.4.11 needs 3.0)** |

Every text token passes AA. The palette was clearly chosen for text contrast and it succeeds at that.
It fails at the two jobs that matter most on a trading screen.

**Problem 1 — gain and loss are the same brightness.** `#116d42` against `#a12a2a` has a contrast
ratio of **1.14**. They differ almost purely in hue. Consequences:

- Under deuteranopia simulation the pair separates by only **2.73**; under protanopia, **2.04**.
  Both are below the 3.0 threshold at which two colours can be reliably told apart.
- Even with normal colour vision, a dense grid cannot be scanned pre-attentively. The eye sorts by
  lightness first; two colours of equal lightness force symbol-by-symbol reading.

**Problem 2 — control borders are invisible.** `--border-default` at 1.48:1 is applied as a 1 px
border in **48 places**, including inputs, selects, and buttons. WCAG 1.4.11 requires 3:1 for the
visual boundary of an interactive control.

**Problem 3 — no dark theme.** `color-scheme: light` is fixed. This is why `Audit.cshtml` carries a
190-line inline `<style>` block with hardcoded dark hex values: the page needed a dark treatment the
design system could not provide. Industry guidance treats dark mode on a monitoring surface as a
functional requirement for long sessions, not a preference.

### 2.2 Proposed palette

Convention is preserved. Green stays gain, red stays loss — the research is unambiguous that
inverting this breaks trader trust. The fix is to separate the pair by **lightness as well as hue**,
add a redundant non-colour channel, and ship a CVD theme as an option.

Dark theme (new default for the desk), panel `#161b22`, page `#0d1117`:

| Role | Hex | Contrast on panel |
|---|---|---:|
| `--text-primary` | `#e6edf3` | 14.64 |
| `--text-secondary` | `#9aa7b4` | 7.05 |
| `--text-muted` | `#7d8590` | 4.64 |
| `--border-control` | `#6e7681` | **3.77** |
| `--border-divider` | `#30363d` | decorative only |
| `--state-positive` | `#3fb950` | 6.81 |
| `--state-negative` | `#ff7b72` | 6.86 |

Light theme, panel `#ffffff`:

| Role | Hex | Contrast on panel |
|---|---|---:|
| `--border-control` | `#818b98` | **3.45** |
| `--border-divider` | `#e2e8f0` | decorative only |
| `--state-positive` | `#0f7b3d` | 5.36 |
| `--state-negative` | `#c02626` | 5.92 |

Splitting the border token into `--border-control` and `--border-divider` is deliberate. Raising all
48 borders to 3:1 would make the UI look caged. Only boundaries that *identify an interactive
control* need 3:1; panel dividers and table rules may stay subtle.

CVD separation of the proposed gain/loss pairs improves to **3.16 (dark) / 3.31 (light)** under
deuteranopia, from 2.73 today. Protanopia still measures ~2.3, which no red/green pair resolves —
hence the next section.

### 2.3 Colour-blind theme

Approximately 8% of men have a colour vision deficiency. Both Bloomberg and TradingView ship
dedicated CVD schemes rather than relying on a tuned red/green; Bloomberg specifically rebuilt its
up/down market-sentiment colours and validated them with Ishihara testing.

Ship a third theme option using the Wong palette, blue for up and vermillion for down:

| Role | Hex | Deuteranopia separation | Protanopia separation |
|---|---|---:|---:|
| up | `#0072b2` | **4.04** | **3.05** |
| down | `#d55e00` | | |

This is the only pair tested that passes both simulations. Offer three schemes — Classic, CVD, and
Monochrome — matching established practice.

**Constraint found during implementation.** CVD separation comes from a luminance gap between the
two colours, but the 4.5:1 text requirement on a dark background pushes both members toward the same
high luminance — which collapses the gap. Brightening `#0072b2`/`#d55e00` enough to read as body text
on `#1e293b` drops deuteranopia separation from 4.04 to about 2.6. The two goals are in direct
tension and cannot both be maximised.

Resolution: the shipped CVD scheme uses `#3d9fe0` / `#e08214`, which clear 4.5:1 as text and sit on
the blue-versus-warm axis that both deuteranopes and protanopes retain. The residual separation gap
is covered by making colour never the only signal — `.value-signed` supplies a sign and a ▲/▼ glyph
on every directional value. On light backgrounds no such tension exists, so the scheme uses the
stronger `#0060a0` / `#c25400` (deuteranopia 3.98, protanopia 3.10).

### 2.4 Redundant encoding

Colour must never be the only carrier. On every gain/loss value:

- an explicit sign (`+1.24%` / `−0.83%`)
- a direction glyph (`▲` / `▼`) with an accessible name
- tabular numerals so columns align and magnitude is readable by shape

The codebase already leans this way — `PlClass`/`StateClass` pair colour with text in 12 places.
Make it a rule rather than a habit.

## 3. Information Density And Segregation

The complaint is correct and specific. Measured on Trade Desk:

**The symbol rail stacks seven headed sections** in one vertical column: symbol overview, with
nested *TradingFlow decision* and *Position* blocks, then Model Intelligence, Latest signal, Selected
Symbol News, and Wishlist News.

**Two of those sections show the same data.** `selected.LatestSignal.SignalType` is rendered at
[TradeDesk.cshtml:287](src/TradingFlow.Web/Pages/TradeDesk.cshtml:287) under the heading
*TradingFlow decision*, and again at
[TradeDesk.cshtml:388](src/TradingFlow.Web/Pages/TradeDesk.cshtml:388) under *Latest signal*. The
reason string is likewise duplicated across both.

**Two news panels coexist.** *Selected Symbol News* and *Wishlist News* are both rendered; the
selected symbol's stories appear in both lists simultaneously.

**Roughly 25 elements precede the first symbol row**: a page header with two actions, a seven-item
status strip carrying nine values, a three-select toolbar plus Apply, a four-part summary row, and a
tab strip. All of it is above the data.

### 3.1 Rules to apply

1. **One fact, one place.** Merge *TradingFlow decision* and *Latest signal* into a single decision
   block: verdict, reason, matched values, timestamp. Delete the duplicate.
2. **Scope news once.** One news panel with a scope control (This symbol / This group), not two
   panels containing overlapping items.
3. **Status is exception-driven.** Collapse the seven-item strip to a single compact bar showing
   environment, session, and clock. The other four — quotes, model, broker sync, entry gate — are
   steady-state almost always; render them as one aggregate health chip that expands only when
   something is degraded. A screen full of green tells the operator nothing.
4. **Progressive disclosure in the rail.** Show Decision and Position by default. Model Intelligence,
   full news, and readiness evidence become tabs or disclosures within one panel, not stacked peers.
5. **Typographic hierarchy carries priority.** Price large and high contrast; secondary metrics
   smaller and muted. Currently the rail renders most values at similar weight, so nothing leads.
6. **Data first.** The grid should begin within the first screenful, not below 25 chrome elements.

## 4. Accessibility

### 4.1 Already good — protect it

- MAUI applies `MinimumHeightRequest = 48` consistently across button, entry, picker, and editor
  styles. Android's 48 dp guidance is met by construction.
- 7 `aria-live="polite"` regions, 7 `role="status"`, 5 `role="alert"`. Announcements are deliberate.
- Skip link, `aria-current="page"`, `focus-visible` with a 3 px `#2563eb` outline at 5.17:1, and
  `tabindex="-1"` on the main landmark are all present and correct.
- Table scroll containers carry `role="region"`, a label, and `tabindex="0"`, so keyboard users can
  reach horizontally scrolling content.

### 4.2 Gaps

1. **Non-text contrast (1.4.11)** — the 1.48:1 control border, 48 occurrences. Fix per §2.2.
2. **No CVD provision** — §2.3.
3. **No dark or high-contrast scheme** — `color-scheme: light` is hardcoded.
4. **Sortable headers do not exist**, so `aria-sort` cannot be announced. When sorting is added
   (plan TD3), headers must be buttons carrying `aria-sort`, not clickable `<th>` text.
5. **Live-region volume risk** — as streaming values grow, per-tick announcements must stay
   suppressed; only connection loss, staleness, order state, and new actionable signals should
   speak.
6. **Reduced motion** — no `prefers-reduced-motion` handling exists; add it before introducing any
   flash-on-update treatment for price changes.

## 5. Mobile Haptics

### 5.1 Current state

There are **zero** haptic or vibration call sites in `src/TradingFlow.Mobile`, and
`Platforms/Android/AndroidManifest.xml` declares only `ACCESS_NETWORK_STATE`, `INTERNET`, and
`POST_NOTIFICATIONS`. The `VIBRATE` permission is absent, so haptics cannot fire even if called.

There are 30 `DisplayAlert` call sites — confirmations and errors that currently land with no
tactile signal at all.

### 5.2 Enablement

Add the permission via assembly attribute in `Platforms/Android/MainApplication.cs`:

```csharp
[assembly: UsesPermission(Android.Manifest.Permission.Vibrate)]
```

`HapticFeedback.Default.Perform(...)` offers only `Click` and `LongPress`. That is sufficient for
most of the map below; the two multi-pulse patterns need `VibratorManager` with
`VibrationEffect.createWaveform` behind a platform service, with a `Click` fallback.

### 5.3 Proposed map

Android guidance is explicit that less is more, that a small set of 3–6 reusable patterns should
carry consistent meaning, and that strength should correlate with importance and inverse frequency.

| Event | Pattern | Rationale |
|---|---|---|
| Order confirmed / accepted | Double pulse (waveform) | Highest-stakes success; must be unmistakable |
| Order rejected or blocked | Sharp triple pulse (waveform) | Sharper pulse warns of risk |
| Review passed, confirm now enabled | `Click` | State confirmation before a deliberate act |
| Position exit confirmed | Double pulse | Same weight as entry |
| Destructive confirm (delete group, cancel order) | `LongPress` | Warns before an irreversible act |
| New actionable signal arrives | `Click` | Event notification, but only when app is foregrounded |
| Filter chip / segment change | none | Too frequent; would numb the channel |
| Pull-to-refresh | none | Platform already handles it |
| Row selection, scrolling | none | Never |

Rules:

1. Haptics accompany a state change the operator caused or must act on. Never decorative.
2. A user-facing toggle in Settings disables all haptics; default on.
3. Respect the system haptic setting; never bypass it.
4. Co-design with the visual confirmation — the pulse fires with the state change, not before it.
5. Never use haptics as the only feedback channel.

### 5.4 The deliberate-execution gesture

Reference platforms require a deliberate interaction for execution — swipe-to-confirm or
press-and-hold — rather than a plain tap, to create intentional friction. The current
`OrderTicketPage` confirms on a standard button tap. Pair a press-and-hold confirm with a rising
haptic ramp and a filling progress ring: the tactile channel then communicates commitment, which is
exactly what haptics are for on a trading surface.

## 6. Live And Paper Separation

Covered as checkpoint TD2 in `docs/ui-trading-desk-plan.md`; the visual specification belongs here.

Environment must be legible from a glance at any screenshot, without reading text. A badge is not
enough — today `"PAPER"` is a hardcoded literal in three files and the only differentiator.

| Aspect | Paper | Live |
|---|---|---|
| Top bar | Neutral surface, standard border | Persistent amber accent bar, full width |
| Environment chip | Neutral, lowercase `paper` | High-contrast `LIVE`, always first in reading order |
| Order ticket | Standard chrome | Amber-bordered panel, environment repeated at the confirm control |
| Route | `/paper/...` | `/live/...` |
| Android | Standard tab bar | Amber shell accent, environment in every page header |

Amber, not red: red is committed to loss. Environment identity and P/L direction must never share a
colour.

`docs/operating-boundaries.md:20` keeps live routing disabled. `/live/*` therefore renders as a
locked surface naming the outstanding promotion gate, with no order controls present in the DOM at
all — fail closed, not merely disabled.

## 7. Backtest Research Audit

Covered as checkpoint TD8. The design-layer points:

- The Audit page's 190-line inline dark theme disappears once §2.2 ships a real dark theme; the page
  stops being visually foreign to the product.
- Rejection evidence must show **matched value beside threshold**, not just a failed chip. `AGENTS.md`
  already requires audit pages to show exact matched values and reasons.
- The decision funnel — candidates → evaluated → per-gate survivors → entries → exits — answers "why
  did nothing trade" in one view and should lead the page, above the record table.
- Every research and backtest run needs an audit trail reachable from the run itself; audit must not
  be a separate destination the operator has to know to visit.

## 8. Sequencing

These fold into the existing plan rather than forming a parallel track:

| Work | Checkpoint |
|---|---|
| Token consolidation, dark theme, CVD theme, border split, reduced motion | TD1 |
| Environment visual identity | TD2 |
| `aria-sort` on sortable headers, tabular numerals, redundant sign/glyph | TD3 |
| Rail de-duplication, status-strip collapse, single scoped news panel | TD3 and TD5 |
| Haptics, permission, settings toggle, press-and-hold confirm | TD7 and TD9 |
| Audit funnel and matched-value evidence | TD8 |

## 9. External Review Reconciliation

An external (Gemini) review of the Earnings page was supplied during this work. Its layout
recommendations — a 70/30 split of chronological feed and a News/Evidence sidebar, weekday-only day
grouping that skips weekends, per-ticker cards with BMO/AMC badges and EPS/revenue surprises — are
already implemented in `src/TradingFlow.Web/Pages/Earnings.cshtml` and
`wwwroot/js/earnings.js`. No change was required.

Its remaining recommendation was a deep dark mode at `#0f172a` / `#1e293b`. Adopted: those are now
the shipped dark page and panel surfaces. They were already present in the stylesheet for the signal
toast, so the whole product converged on values the codebase was using.

Two points where this review differs, with reasons:

1. **Glassmorphism on data cards — not adopted.** Translucent backgrounds make text contrast depend
   on whatever sits behind the card, so a value that measures 6:1 in one scroll position can fail in
   another. On a dense, live-updating grid it also costs compositing work on every tick. If the
   effect is wanted, confine it to modal overlays and transient surfaces, never to cells carrying
   prices or P/L.
2. **Micro-animations on hover — adopted with a limit.** Hover transitions are fine on static
   elements. They must not be applied to values that stream, where an animation competes with a real
   data change for attention, and they are now suppressed entirely under `prefers-reduced-motion`.

Typography was left on the system stack rather than loading Inter or Roboto. The functional need was
digit alignment, which `font-variant-numeric: tabular-nums` provides without a webfont download on a
latency-sensitive surface.

## 10. References

- [Bloomberg — Designing the Terminal for Color Accessibility](https://www.bloomberg.com/company/stories/designing-the-terminal-for-color-accessibility/)
- [Bloomberg UX — Accessibility](https://www.bloomberg.com/ux/accessibility/)
- [Android — Haptics design principles](https://developer.android.com/develop/ui/views/haptics/haptics-principles)
- [.NET MAUI — Haptic feedback](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/device/haptic-feedback)
- [Trading App Design: UI, UX & System Architecture](https://lollypop.design/blog/2026/june/trading-app-design/)
- [Coloring for Colorblindness — Wong palette simulator](https://davidmathlogic.com/colorblind/)
- [Tableau — Examining data viz rules: don't use red/green together](https://www.tableau.com/blog/examining-data-viz-rules-dont-use-red-green-together)
- [WCAG 2.2 — Non-text Contrast (1.4.11)](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html)
