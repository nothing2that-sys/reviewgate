# Prices announced by the provider (no price page to look up)

`announced.json` holds rows for models whose provider publishes no per-token price page, only an
announcement. They are added to whichever price snapshot is in use (shipped or user-approved) when that
snapshot has no row for the model, and they are not part of the price refresh.

| Model key | Price | Source | Checked |
|---|---|---|---|
| `lgai/k-exaone-v2.5` (EXAONE API `k-exaone_v2.5`) | 0 (free) | docs.exaone.ai changelog, 2026-11-09: the service is currently free. Terms 9.2: if it becomes paid, prices are posted on the site. | 2026-11-10 |

When a provider starts charging, replace its row here with the real prices (or remove it and add the
model to the price refresh). Until then ReviewGate books these calls at 0.
