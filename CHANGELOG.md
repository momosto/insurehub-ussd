# Changelog

## [0.1.0] — 2026-10-01

### Added
- .NET 8 minimal-API USSD gateway (`POST /ussd`, Africa's Talking-style form, CON/END) with shared-secret auth and IP allowlist.
- Declarative menu graph (`data/menus/main.json`) + `MenuRuntime` + small handlers; startup validation of targets, dead ends, reachability, handlers and translations.
- English, Shona and isiNdebele texts (`data/i18n`), GSM-7 enforcement, 182-character screens.
- Server-side sessions in Redis (or memory), language per number, PIN via OTP with Argon2id hashes, lockout, weak-PIN rules, SIM-swap hook.
- Flows: policies (masked), premium payment with confirmation and session-derived idempotency key, claims, loan and loan payment, call-back to the InsureAssist inbox, language, help.
- Resilience: Polly timeout (1.5 s) and circuit breaker, last-good cache, "we'll SMS you" fallback worker.
- OpenTelemetry traces and metrics; `/stats`; web phone simulator with backend chaos controls.
- 37 xUnit tests (screen property in all languages, flows, resilience, security, Redis via Testcontainers); Dockerfile, Compose, Kustomize, GitHub Actions.
