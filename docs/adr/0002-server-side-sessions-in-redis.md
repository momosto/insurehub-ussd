# ADR-0002: Keep USSD session state server-side in Redis, keyed by sessionId

- **Status:** Accepted · **Date:** 2026-09-30

## Context
Aggregators send the whole input history in `text` (e.g. `2*1234*1`). Rebuilding state from that string on every hop is stateless and simple, but it puts the **PIN into every request**, breaks when inputs contain `*`, and can't hold fetched data (e.g. which policies were listed on screen 2).

## Decision
Server-side session in Redis (`ussd:s:{sessionId}`, TTL 180 s, in line with typical operator session limits of ~90–180 s). Only the latest input is read from `text`. The PIN is verified once, and after that only a `pinVerified` flag is kept.

## Consequences
- ➕ PIN handled once; lists and pending payments kept consistently; replays are detectable.
- ➖ Redis is a dependency (Upstash free as fallback: 256 MB, 500k commands/month).

## Sources
- [BFA Global — serverless USSD with Africa's Talking](https://bfaglobal.com/insights/serverless-ussd-with-africas-talking-part-1/)
- [Huntress — what is USSD (session timeouts)](https://www.huntress.com/cybersecurity-101/topic/ussd)
- [Layerbase — Upstash free tier](https://layerbase.com/blog/redis-free-tier-comparison)
