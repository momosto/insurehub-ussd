# InsureHub USSD — delivery plan

**Window:** 2026-11-26 → 2026-12-09

| Step | Scope | Done when |
|---|---|---|
| 1 | minimal API `/ussd`, menu engine, JSON menus, resx EN, Redis sessions, main menu + help | simulator shows the main menu |
| 2 | PIN setup (OTP simulated) + verify, My policies, Claim status (InsureHub) | read flows work end to end |
| 3 | Pay premium (Payments) + My loan & pay (LendHub) with confirmation and idempotency | EcoCash simulator prompt fires |
| 4 | Shona + Ndebele strings, screen property test, resilience (Polly + cache + SMS fallback) | all tests green |
| 5 | call-back → InsureAssist inbox via RabbitMQ; OTel; phone simulator page; deploy; README + video | live at `ussd.<domain>` |

## Demo script (90 s)
1. Dial `*263#` on the simulator, switch to Shona.
2. Set the PIN, view policies (masked numbers).
3. Pay Farai's kombi arrears → EcoCash prompt → policy reinstated in InsureHub (show the portal side by side).
4. Kill the InsureHub API → the menu still answers from cache, or ends gracefully within 2 s.
5. Request a call-back → the ticket appears in the InsureAssist staff inbox.
