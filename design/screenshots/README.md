# Screenshots

Captured from the prototypes in `../prototypes/`. Wide screens were scaled to fit the
capture frame — read the pixel values from the screen specs, not from these images. The
figures shown are mock data.

| File | Screen | Spec |
| --- | --- | --- |
| `01-trading-desk-grid-rail.png` | Trading Desk, layout A (grid + rail) | `screens/01-trading-desk.md` |
| `02-trading-desk-ladder-focus.png` | Trading Desk, layout B (ladder + focus) | `screens/01-trading-desk.md` |
| `03-earnings.png` | Earnings — attention band, timeline, evidence rail | `screens/02-earnings.md` |
| `04-backtest-lab-results.png` | Backtest Lab, results phase | `screens/03-backtest-lab.md` |
| `05-backtest-lab-running.png` | Backtest Lab, running phase | `screens/03-backtest-lab.md` |
| `06-positions.png` | Positions | `screens/04-operations.md` (4a) |
| `07-orders.png` | Orders | `screens/04-operations.md` (4b) |
| `08-wishlists.png` | Wishlists | `screens/04-operations.md` (4c) |
| `09-operations.png` | Operations / health | `screens/04-operations.md` (4d) |
| `10-mobile-list.png` | Mobile — market list | `screens/05-mobile-desk.md` |
| `11-mobile-detail.png` | Mobile — symbol evidence | `screens/05-mobile-desk.md` |
| `12-mobile-ticket.png` | Mobile — order ticket | `screens/05-mobile-desk.md` |

## What to look for

- **01 vs 02** — the two desk layouts are the one open decision in this handoff. Both ship
  behind the Layout control; pick one after using both.
- **03** — the three counted lanes at the top are the whole point of the screen. Note that
  `ETSY` appears in two lanes at once (beat + breakout, and held) and gets both badges.
- **04** — cell tint in the matrix encodes rank per metric, not absolute value. The
  equity-curve panel is the part blocked on `api-gaps.md` item 1.
- **05** — four strategy groups progressing independently, each with its own current ticker.
- **06** — `COIN` shows the unprotected state: "No broker stop on file" in accent, and the
  `UNPROTECTED 1` count in the summary band.
- **07** — the states that need a decision are marked: partial fill in accent, working rows
  are the only ones with Cancel / Replace, terminal rows say so rather than showing nothing.
- **09** — every subsystem row names the owning service. `FinBERT` is `–` (not applicable,
  VADER fallback), not a failure. Backup is deliberately in attention.
- **12** — the ticket is shown in its blocked state: the duplicate check fails because a
  position is already open, so the submit button reads "Blocked — cannot submit".
