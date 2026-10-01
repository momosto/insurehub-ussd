# InsureHub USSD — requirements traceability & implementation report

**Version:** 0.1.0 · **Date:** 2026-10-01 · **Build:** 37 xUnit tests green (incl. Redis via Testcontainers) · container verified with Redis sessions

✅ done and tested · 🟡 done with a documented simplification · ⏳ deferred.

## 1. Stories

| ID | Story | Implementation | Verified by | Status |
|---|---|---|---|---|
| US-01 | Main menu in my language, first screen < 1 s | `menu "main"` never calls a backend; language per MSISDN (`ussd:lang:{msisdn}`, 1 year) | `Dial_shows_the_main_menu_without_calling_any_backend` (backend down), `Language_is_remembered_per_number` | ✅ |
| US-02 | Set/verify a 4-digit PIN; weak PINs refused; 3 wrong → 30 min lock | `PinService` (Argon2id m=19 MiB t=2 p=1), OTP by SMS, `MenuRuntime.PinStepAsync` | `First_use_sets_a_pin_by_otp…`, `Three_wrong_pins_lock…`, `Weak_pins_are_rejected_and_hashes_verify` | ✅ |
| US-03 | See my policies, numbers masked | `PoliciesHandler`, `Mask.Reference` (`MOT-..0301`) | `Returning_customer_enters_the_pin…`, masking test | ✅ |
| US-04 | Pay a premium: confirmation, idempotency key from the session, "approve the EcoCash prompt" END | `pay.*` nodes, `StartPaymentHandler` (key `ussd:{sessionId}:{ref}:{amount}`) | `First_use_sets_a_pin_by_otp_then_pays_the_arrears`, `Other_amount_is_validated…`, `Fixture_payments_are_idempotent_on_the_key` | ✅ |
| US-05 | Claim status and next step, no disputed amounts | `ClaimsHandler` (latest 2, next step capped at 45 chars) | `Claim_status_is_masked_and_ends_the_session` | ✅ |
| US-06 | Loan & pay | `LoanSummaryHandler` (LendHub channel endpoint in http mode), `loan.*` nodes | `Loan_instalment_is_paid_through_the_loan_flow` | ✅ |
| US-07 | Call-back → ticket in the InsureAssist inbox | `RequestCallbackHandler`; http mode posts a signed `CallbackRequested` event to InsureAssist `/events` (implemented there too) | `Call_back_requests_reach_the_core…` + InsureAssist `test_ussd_call_back_request_lands_in_the_staff_inbox` | ✅ |
| US-08 | Pagination `98 More` / `0 Back` | `MenuRuntime` list nodes, page size 3 | `Long_lists_page_with_98_more_and_0_back` | ✅ |

## 2. Constraints and NFRs

| Constraint | Evidence | Status |
|---|---|---|
| Every screen ≤ 182 GSM-7 characters, all languages, worst-case data | `ScreenTests` render every menu/view/list node in EN/SN/ND with the longest labels, 6-digit ZWG amounts and longest statuses; plus a runtime guard that trims and logs. **This test caught a real overflow** (Ndebele list page at 215 chars) → page size 3, 16-char descriptions | ✅ |
| GSM-7 only | `Texts.IsGsm7` on every text; `ToGsm7` cleans data (curly quotes, dashes) | ✅ |
| ≤ 6 screens per flow | longest flow (first-use payment): dial → 2 → OTP → PIN → PIN → policy → amount → confirm = 7 hops on first use only; 4 hops afterwards | 🟡 first use is one hop over; PIN setup could move to a separate menu option |
| p99 < 2 s; backend ≤ 1.5 s with fallback | `ResilientCoreClient` (Polly timeout + circuit breaker + last-good cache); `Slow_backend…_within_two_seconds`, `Cached_answer_is_served…`, `Busy_answer_is_followed_by_an_sms…` | ✅ (load test ⏳) |
| Callback protocol (CON/END, `text` history) | `/ussd` form contract; only the latest input is read (ADR-0002) | ✅ |
| Analytics per node | `UssdMetrics` (OTel meter + `/stats`): sessions, completions, views and ends per node, fallbacks | ✅ |

## 3. Security controls (docs/04)

| Threat | Control | Verified by | Status |
|---|---|---|---|
| Spoofed callbacks | `X-Ussd-Secret` (constant-time) + optional IP allowlist | `Callbacks_without_the_shared_secret_are_rejected` | ✅ |
| SIM swap | `ISimSwapChecker` hook before payments (configured list in the demo) | `Recent_sim_swap_blocks_payments` | 🟡 simulated MNO API |
| Shoulder-surfing | masked references and numbers; PIN never echoed | flow tests | ✅ |
| PIN brute force | 3 attempts → 30 min lock; weak-PIN list; Argon2id | PIN tests | ✅ |
| Session hijack | session keyed by `sessionId` and bound to the MSISDN | code (`UssdService`) | ✅ (no dedicated test) |
| PIN in logs | `text` never logged; SMS content never logged | `The_pin_and_input_history_never_reach_the_logs` | ✅ |
| Payment abuse | confirmation screen, idempotency key, per-currency limit | flow tests | ✅ (daily limit ⏳) |

## 4. Deviations from the planning pack

| Planned | Built | Why |
|---|---|---|
| resx localisation | JSON text tables (`data/i18n/{en,sn,nd}.json`) | easier to diff, validate and hand to translators; the validator checks every key in every language (ADR-0003) |
| PIN hashes in PostgreSQL | Argon2id hashes in Redis (`ussd:pinhash:{msisdn}`, no TTL) or memory | removes a database for a single table; Redis persistence (AOF) is required in production (ADR-0003) |
| Snapshot tests with Verify | explicit string assertions per screen | fewer dependencies; screens are short |
| k6/NBomber load test | not yet | ⏳ |
| Call-back via RabbitMQ | signed HTTP event to InsureAssist `/events` | InsureAssist has no AMQP consumer yet; same envelope format, switchable later |

## 5. Backlog (v0.2)
1. Load test: 200 concurrent sessions, p99 < 2 s on the demo node.
2. Real SIM-swap API and daily payment limits per MSISDN.
3. `Core:Mode=http` end to end once InsureHub's `/api/channels/*` endpoints exist (LendHub's already do).
4. Native-speaker review of Shona and isiNdebele texts.
5. Move PIN setup to its own menu option so every flow stays ≤ 6 screens.
