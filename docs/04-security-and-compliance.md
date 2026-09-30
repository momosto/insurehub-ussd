# InsureHub USSD — security & compliance

| Threat | Control |
|---|---|
| Spoofed callbacks (someone posts to `/ussd` pretending to be the aggregator) | IP allowlist of the aggregator + shared-secret header; not publicly documented |
| SIM-swap fraud (attacker takes over the number) | PIN for personal data and payments; a **SIM-swap check hook** (simulated MNO API) before payments, blocking payments within 72 h of a swap (illustrative) |
| Shoulder-surfing / shared phones | masked policy and loan numbers; PIN digits are not echoed back in later screens |
| PIN brute force | 3 attempts → 30 min lock; weak-PIN list; Argon2id hashes |
| Session hijack | session keyed by aggregator `sessionId` + MSISDN; mismatches rejected |
| Data in logs | `text` is never logged raw (it contains the PIN); structured logs include the node only |
| Payment abuse | confirmation screen; idempotency key from the session ID; amount limits per day |

**Data protection:** only the minimum data per screen; Redis TTLs keep transient data short-lived; a privacy notice link is sent by SMS on first use; breach handling via the platform runbook (24-hour POTRAZ notification, [Act](https://potraz.gov.zw/wp-content/uploads/2025/02/Cyber-and-Data-Protection-Act-Chapter-1207.pdf)).
