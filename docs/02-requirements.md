# InsureHub USSD — requirements

## 1. Menu tree (v1)

```
*263#
Welcome to InsureHub          (language remembered per number)
1. My policies
2. Pay premium
3. Claim status
4. My loan
5. Request a call-back
6. Language / Mutauro / Ulimi
0. Help

1 → Enter PIN → list (max 4 per screen, 98 = more) → policy → status, cover, arrears
2 → Enter PIN → choose policy → amount due → 1 Pay full / 2 Other amount → confirm → "Approve the EcoCash prompt" END
3 → Enter PIN → latest claims → status + next step
4 → Enter PIN → next instalment, balance → 1 Pay instalment → confirm → EcoCash END
5 → reason (1 Claim, 2 Payment, 3 Other) → "An agent will call you" END  (creates a handoff in the InsureAssist inbox)
```

## 2. Stories

| ID | Story | Acceptance criteria |
|---|---|---|
| US-01 | Dial and see the main menu in my language | first screen < 1 s; language stored per MSISDN |
| US-02 | Set/verify a 4-digit PIN | first use: OTP by SMS → set PIN (not 1234/0000/birth-year patterns); 3 wrong → 30 min lock |
| US-03 | See my policies | same data as the portal; numbers masked (`FUN-…7KQ2`) |
| US-04 | Pay a premium | confirmation screen repeats amount and policy; Payments called with an idempotency key built from the session ID; session ends with an instruction to approve the EcoCash prompt |
| US-05 | See claim status | status and next step; no amounts in dispute shown |
| US-06 | See my loan & pay | next instalment and balance from LendHub; payment as US-04 |
| US-07 | Request a call-back | creates a handoff ticket (InsureAssist inbox) with the reason |
| US-08 | Pagination | lists longer than one screen use `98 More` / `0 Back` |

## 3. Constraints and NFRs

| Constraint | Requirement |
|---|---|
| Screen length | every screen ≤ **182 characters** (GSM-7) including options ([Clickatell](https://www.clickatell.com/help-center/ussd/whats-the-maximum-character-length-for-ussd/)) — enforced by a test over every screen in every language |
| Session length | operators typically allow ~90–180 s per session ([Huntress](https://www.huntress.com/cybersecurity-101/topic/ussd)); flows designed for ≤ 6 screens |
| Response time | p99 < 2 s end to end (aggregators drop slow sessions); each backend call ≤ 1.5 s with a fallback |
| Protocol | aggregator callback (Africa's Talking-style): form fields `sessionId`, `serviceCode`, `phoneNumber`, `text` (inputs joined by `*`), `networkCode`; response body starting `CON ` or `END ` ([BFA Global](https://bfaglobal.com/insights/serverless-ussd-with-africas-talking-part-1/)) |
| Characters | GSM-7 only; Shona and Ndebele text checked for non-GSM characters |
| Availability | 99% (demo) |
| Analytics | per-menu completion and drop-off rates |
