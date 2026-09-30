# InsureHub USSD — feature-phone self-service (`*263#`)

**Status:** 📝 Planned — build 2026-11-26 → 2026-12-09 (2 weekends + evenings) · **Stack rotation slot:** C#
**Stack:** .NET 8 · ASP.NET Core minimal APIs · Redis (sessions) · Polly (timeouts, circuit breakers) · declarative menu engine (JSON) · resx localisation (English, Shona, Ndebele) · xUnit + WebApplicationFactory · OpenTelemetry · vanilla JS phone simulator · Docker (multi-arch)

**Author:** Simbarashe Nyamusa, Senior Software Engineer

Customers without smartphones or data dial **`*263#`** to check policies, pay premiums by EcoCash, check claims and loans, and request a call-back. It reuses the same core APIs as the web portal and the WhatsApp assistant (channel-agnostic core, principle P6).

## Why this project
- Hiring managers at Zimbabwean insurers, banks and telcos recognise USSD straight away, and I built USSD-adjacent systems at TelOne (EcoCash/TelPay payment flows).
- It's a quick win: 1–2 weekends, since the APIs already exist.
- It shows a different engineering problem: **hard real-time limits** (the aggregator times out in seconds), **182-character screens**, and stateful sessions over a stateless protocol.

## Planning pack

| Document | Contents |
|---|---|
| [docs/01-concept-paper.md](docs/01-concept-paper.md) | why USSD, options, recommendation |
| [docs/02-requirements.md](docs/02-requirements.md) | menu tree, stories, constraints, NFRs |
| [docs/03-architecture.md](docs/03-architecture.md) | callback contract, session design, menu engine, resilience |
| [docs/04-security-and-compliance.md](docs/04-security-and-compliance.md) | PIN, SIM-swap risk, data exposure on shared phones |
| [docs/05-test-strategy.md](docs/05-test-strategy.md) | flow tests, screen-length property, latency tests |
| [docs/06-delivery-plan.md](docs/06-delivery-plan.md) | plan and demo script |
| [docs/adr/](docs/adr/) | decisions |
