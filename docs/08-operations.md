# InsureHub USSD — operations runbook

| Item | Value |
|---|---|
| Service | `insurehub-ussd` (stateless, 2 replicas), port 5300 |
| Callback | `POST /ussd` (form: `sessionId`, `phoneNumber`, `text`, …) with header `X-Ussd-Secret` |
| State | Redis: `ussd:s:{sessionId}` (180 s), `ussd:lang:{msisdn}` (1 year), `ussd:pin:{msisdn}` (attempts/lock, 30 min), `ussd:pinhash:{msisdn}` (no TTL) |
| Health | `/health/live`, `/health/ready` (reads Redis) |
| Metrics | OTel meter `InsureHub.Ussd` (`ussd.sessions.*`, `ussd.node.views`, `ussd.backend.fallbacks`, `ussd.request.duration`); quick view `/stats` |
| Config | `Ussd__CallbackSecret`, `Ussd__AllowedIps__0..`, `Core__Mode` (`fixtures`/`http`), `Core__TimeoutMs` (1500), `Redis__ConnectionString`, `Security__RecentSimSwaps__0..`, `Simulator__Enabled` (false with a real aggregator) |

## Alerts and responses

| Signal | Likely cause | Action |
|---|---|---|
| `ussd.backend.fallbacks` rising | a core API is slow or down | check InsureHub/LendHub/Payments health; customers get cached answers or "we'll SMS you"; nothing to do in the gateway |
| p99 `ussd.request.duration` > 2 s | Redis latency or node CPU | `redis-cli --latency`; scale the deployment; the aggregator drops slow sessions |
| Many `pin.locked` ends | brute-force attempt or a confusing PIN screen | look at `endsByNode` in `/stats`; if one MSISDN, report to fraud |
| Aggregator reports 401 | secret rotated on one side | rotate `Ussd__CallbackSecret` with the aggregator at the same time |
| Startup fails with "Invalid menu" | a menu or text edit broke the graph | the message lists every problem; fix `data/menus/main.json` or `data/i18n/*.json` and redeploy |

## Changing a menu or text
1. Edit `src/Ussd.Gateway/data/menus/main.json` and/or `data/i18n/*.json` (all three languages).
2. `dotnet test` — the screen-length and translation tests must pass.
3. Merge; ArgoCD rolls it out. No code change is needed unless a new handler is introduced.

## Customer support
- **Forgotten PIN:** the customer can't reset it themselves yet; support deletes `ussd:pinhash:{msisdn}` after verifying identity, and the next dial sets a new PIN by OTP.
- **Locked out:** wait 30 minutes, or support deletes `ussd:pin:{msisdn}` after verifying identity.
