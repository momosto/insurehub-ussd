# InsureHub USSD: test cases

**Version:** 0.1.0 · **Date:** 2026-10-01 · **Run:** 37 xUnit tests passed (`dotnet test`), simulator and container checked by hand

This is the test-case catalogue behind [05-test-strategy.md](05-test-strategy.md). Flow tests dial the real `/ussd` endpoint through `WebApplicationFactory` and assert every screen, the way a phone would see it.

**Levels:** U = unit · S = screen/content check over every menu and language · F = flow (HTTP, scripted dial sequence) · R = resilience (injected slowness and outages) · I = integration with a real Redis (Testcontainers) · E = end-to-end (container + browser simulator).
**Result:** ✅ passed on 2026-10-01 · ⏳ not automated yet (reason given).

Fixture customers (fictional): Farai `263733456789` (motor policy in arrears USD 187.20), Tendai `263772123456` (motor + funeral policies, claim under review), Mai Chipo `263779000111` (loan, no policies), Nyasha `263712987654` (recent SIM swap), `263771999888` (not registered).

## 1. Screens and content (`ScreenTests.cs`)

| ID | Req | Scenario | Expected | Level | Automated by | Result |
|---|---|---|---|---|---|---|
| TC-SCR-01 | NFR screen size | Render every menu and view node in EN, SN, ND | Each screen ≤ 182 GSM-7 characters and GSM-7 only | S | `Every_menu_and_view_screen_fits_in_182_gsm7_characters` (×3 languages) | ✅ |
| TC-SCR-02 | US-08 | Render full list pages (3 items + "98 More" + "0 Back") | Fit in 182 characters in every language | S | `Full_list_pages_fit` (×3) | ✅ |
| TC-SCR-03 | NFR screen size | Fill every text with worst-case data (longest names, amounts, references) | Fits its limit (182 for screens, 160 for SMS) and stays GSM-7 | S | `Every_text_fits_and_is_gsm7_when_filled_with_worst_case_data` (×3) | ✅ |
| TC-SCR-04 | US-01 | Compare the three language files | Exactly the same keys in EN, SN and ND | S | `Every_language_has_exactly_the_same_keys` | ✅ |
| TC-SCR-05 | NFR maintainability | Validate `data/menus/main.json` | No unknown targets, missing texts, unknown handlers or orphan nodes | S | `The_menu_graph_is_valid` | ✅ |
| TC-SCR-06 | NFR maintainability | Feed the validator a broken menu | Reports unknown target, missing text, unknown handler, dead end and unreachable node | U | `The_validator_catches_broken_menus` | ✅ |
| TC-SCR-07 | NFR GSM-7 | Data with curly quotes, en dash, euro sign | Replaced by plain characters; `€` counts as 2 | U | `Non_gsm_characters_from_data_are_replaced` | ✅ |

## 2. Customer flows (`FlowTests.cs`)

| ID | Req | Scenario | Expected | Level | Automated by | Result |
|---|---|---|---|---|---|---|
| TC-FL-01 | US-01 | Dial `*xxx#` while every backend is down | `CON Welcome to InsureHub` main menu; no backend call | F | `Dial_shows_the_main_menu_without_calling_any_backend` | ✅ |
| TC-FL-02 | NFR security | POST `/ussd` without `X-Ussd-Secret` | 401 | F | `Callbacks_without_the_shared_secret_are_rejected` | ✅ |
| TC-FL-03 | US-02, US-04 | Farai's first use: choose Pay → wrong OTP → right OTP → weak PIN `1234` → PIN `4826` twice → policy → pay full arrears | "Wrong code", "easy to guess", "PIN saved"; policy shown masked as `MOT-..0301`; "Due: USD 187.20"; `END Approve the EcoCash prompt`; exactly one payment of 187.20 with idempotency key `ussd:{sessionId}:…` | F | `First_use_sets_a_pin_by_otp_then_pays_the_arrears` | ✅ |
| TC-FL-04 | US-04 | Other amount: `abc`, then 50, then decline | "valid amount" error; "Pay USD 50.00"; `END Cancelled. Nothing was charged.`; no payment | F | `Other_amount_is_validated_and_declining_charges_nothing` | ✅ |
| TC-FL-05 | US-02, US-03 | Tendai's second session | PIN only (no OTP); list shows `2. FUN-..0102`; detail `END MOT-..0101` with "Premium: USD 94.50/month" | F | `Returning_customer_enters_the_pin_and_sees_policy_details` | ✅ |
| TC-FL-06 | US-02 | Three wrong PINs, then dial again | "2 tries left", "1 tries left", `END Too many wrong PINs`; still locked on the next session | F | `Three_wrong_pins_lock_the_number_for_30_minutes` | ✅ |
| TC-FL-07 | US-05 | Claim status | `END … CLM-..0111: UNDER REVIEW`; full claim number never shown | F | `Claim_status_is_masked_and_ends_the_session` | ✅ |
| TC-FL-08 | US-06 | Mai Chipo: Loan → pay instalment | "Loan LN-..0001", "Balance: USD 554.88", "Pay USD 57.03"; one payment of kind `loan` on LN-2610-000001 | F | `Loan_instalment_is_paid_through_the_loan_flow` | ✅ |
| TC-FL-09 | US-03, US-06 | Farai opens Loans; Mai Chipo opens Policies | "You have no loans with InsureHub Microfinance."; "You have no policies with us." | F | `Customers_without_loans_or_policies_get_a_clear_end_message` | ✅ |
| TC-FL-10 | NFR security (SIM swap) | Nyasha tries to pay | `END For your safety, payments are paused…`; no payment | F | `Recent_sim_swap_blocks_payments` | ✅ |
| TC-FL-11 | NFR privacy | Unregistered number opens Policies | `END This number is not registered…` | F | `Unregistered_numbers_cannot_reach_personal_data` | ✅ |
| TC-FL-12 | US-07 | Farai asks for a call-back about a claim | `END Thank you. An agent will call you on ...6789`; call-back sent to the core with reason `claim`; confirmation SMS | F | `Call_back_requests_reach_the_core_and_confirm_by_sms` | ✅ |
| TC-FL-13 | US-01 | Mai Chipo picks Shona, then dials again | `END Mutauro wachengetwa`; next dial opens `CON Mauya kuInsureHub`; OTP screen and SMS also in Shona | F | `Language_is_remembered_per_number` | ✅ |
| TC-FL-14 | NFR usability | Invalid option `9` on the main menu | `CON Invalid choice.` followed by the same menu | F | `Invalid_choices_repeat_the_screen` | ✅ |
| TC-FL-15 | US-08 | Customer with 6 policies | Page 1 items 1–3 + "98. More"; page 2 items 4–6 without "More"; choosing 3 opens `FUN-..0106` | F | `Long_lists_page_with_98_more_and_0_back` | ✅ |
| TC-FL-16 | NFR privacy | Sign in with PIN 4826 and pay | Neither the PIN nor the `text` input history appears in any log line | F | `The_pin_and_input_history_never_reach_the_logs` | ✅ |

## 3. Security units (`FlowTests.cs`)

| ID | Req | Scenario | Expected | Level | Automated by | Result |
|---|---|---|---|---|---|---|
| TC-SEC-01 | US-02 | PINs 0000, 1111, 1234, 4321, 6789, 1990, 2026, 12a4, 123 | All weak; 4826, 7391, 0518 accepted | U | `Weak_pins_are_rejected_and_hashes_verify` | ✅ |
| TC-SEC-02 | US-02 | Hash and verify a PIN | Argon2id `m=19456 t=2 p=1`; right PIN verifies, wrong does not; same PIN hashes differently (salted) | U | same | ✅ |
| TC-SEC-03 | US-03 | Mask references and numbers; normalise `+263 73 …` and `07…` | `MOT-..0301`, `LN-..0001`, `...6789`; both normalise to `263733456789` | U | `References_and_numbers_are_masked` | ✅ |
| TC-SEC-04 | NFR security | Session ID reused from another MSISDN | Refused | – | code review (`UssdService`) | ⏳ no dedicated test yet |

## 4. Resilience and storage (`ResilienceTests.cs`)

| ID | Req | Scenario | Expected | Level | Automated by | Result |
|---|---|---|---|---|---|---|
| TC-RS-01 | NFR p99 < 2 s | Backend takes 1.7 s, nothing cached | `END We are busy right now…` in under 2 s | R | `Slow_backend_without_cache_ends_gracefully_within_two_seconds_and_sms_follows` | ✅ |
| TC-RS-02 | NFR availability | Policies loaded once, then the backend goes down | Same list served from the last good copy; "Serving cached" logged | R | `Cached_answer_is_served_when_the_backend_goes_down` | ✅ |
| TC-RS-03 | NFR availability | Claim status while the backend is down, then it recovers | `END We are busy`; SMS "CLM-..0111 UNDER REVIEW" arrives after recovery | R | `Busy_answer_is_followed_by_an_sms_once_the_backend_recovers` | ✅ |
| TC-RS-04 | NFR availability | Resilient client with a 200 ms budget and a 2 s backend | Stale policies returned in < 1 s; claims and payments (no cache) raise `BackendUnavailableException` | U | `Resilient_client_times_out_and_falls_back_to_the_last_good_copy` | ✅ |
| TC-RS-05 | US-04 | Start the same payment twice with one idempotency key | Same payment ID; one payment | U | `Fixture_payments_are_idempotent_on_the_key` | ✅ |
| TC-RS-06 | NFR scale-out | Real Redis: save/load a session, language, PIN lock state; delete | Values round-trip; lock time kept; deleted session is gone | I | `Redis_store_keeps_sessions_languages_and_pin_state` | ✅ (skips itself when Docker is missing; always runs in CI) |

## 5. End-to-end

| ID | Scenario | Expected | Result |
|---|---|---|---|
| TC-E2E-01 | `docker compose up` (gateway + Redis); open the simulator at `/`; walk the Pay flow | Screens as in TC-FL-03, sessions stored in Redis | ✅ (screenshot `docs/img/simulator.jpg`) |
| TC-E2E-02 | `curl -X POST /ussd` to the container without the secret | 401 | ✅ |
| TC-E2E-03 | Real aggregator (Africa's Talking sandbox) on a phone | Same screens on a handset | ⏳ needs an aggregator account and a public URL (platform deploy) |
| TC-E2E-04 | Load: 50 concurrent sessions at p99 < 2 s | Meets the budget | ⏳ k6 script planned for 0.2 |
