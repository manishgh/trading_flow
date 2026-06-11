# Swing 6-Ticker 180-Day Research Snapshot

This snapshot promotes two separate swing strategies rather than merging them into one mixed long/short strategy.

## Promotion Decision

- Primary swing long: Swing Reversal Reclaim Bull Quality No News V1.
  - Best balance of return and drawdown in the cleaned run.
  - 11.9532% total return, 3.8461% max drawdown, 10 trades, 6 wins.
- Candidate swing short: Swing Overbought Rollover Short No News V5.
  - Captured the APP rollover from the chart review and avoided the noisy short entries from earlier V2/V3/V4 attempts.
  - 3.6649% total return, 0% max drawdown, 2 trades, 2 wins.
  - Kept as a candidate because the sample is too small for live promotion by itself.

## Why Not Merge Yet

The long and short setups have different market structure assumptions. They should remain separate configs until the portfolio layer has explicit conflict handling for simultaneous long/short candidates on the same ticker and sector.

## Research Lesson

The short side improved only after requiring follow-through: three consecutive lower closes after a strong advance, a close below SMA10, controlled drop from high, and weak close location. This filtered the obvious bad shorts while still catching APP and INTC.
