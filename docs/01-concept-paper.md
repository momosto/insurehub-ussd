# Concept paper — USSD self-service for InsureHub Group

**Prepared by:** Simbarashe Nyamusa · **Date:** 2026-09-30 · **Status:** Draft

## 1. Problem
A share of InsureHub's funeral-plan and microloan customers use basic phones or rarely have data. They can't use the web portal or WhatsApp, so they phone the contact centre or visit a branch to ask a balance, and they lapse because paying is inconvenient.

⏳ *Evidence to add before presenting:* current POTRAZ figures on feature-phone share and USSD usage (not yet researched; see [research and sources §7](https://github.com/momosto/insurehub-platform/blob/main/docs/research-and-sources.md)).

## 2. Objectives
1. Balance, status and payment in under 60 seconds on any phone, with no data.
2. Same business rules as every other channel (no logic duplicated in the USSD layer).
3. Works reliably within aggregator time limits.

## 3. Options

| Option | Pros | Cons |
|---|---|---|
| 0. Do nothing | — | excludes customers without data |
| 1. SMS keyword service ("BAL 1234") | simple | clumsy, per-SMS cost, no payment flow |
| 2. **USSD menu via an aggregator** | works on every phone, familiar to EcoCash users, session-based | short code and aggregator costs; strict time and length limits |
| 3. IVR (voice menus) | good for low literacy | expensive telephony; slower |

## 4. Recommendation
Option 2, delivered as a thin channel adapter over the existing APIs, with a web-based phone simulator for demos. Tariffs and short-code leasing are commercial matters for a real launch (USSD tariffs are regulated by POTRAZ — see [Business Times](https://businesstimes.co.zw/potraz-slashes-data-ussd-tariffs-effective-july-1/) for an example of tariff regulation).

## 5. Benefits (targets)
Fewer balance calls to the contact centre; more on-time premium and loan payments from customers without data; measurable via session analytics (completion rate per menu).

## 6. Risks
| Risk | Mitigation |
|---|---|
| Aggregator timeouts during slow backend calls | 1.5 s per-call timeouts, cached reads, "we'll SMS you" fallback |
| SIM-swap fraud | PIN for sensitive actions; SIM-swap check hook (simulated) before payments |
| Shared phones expose data | mask policy/loan numbers; PIN before personal data |
