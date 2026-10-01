# ADR-0003: JSON text tables, Argon2id PIN hashes in Redis, three items per list page

- **Status:** Accepted · **Date:** 2026-10-01

## Context
The plan named resx files for localisation and a PostgreSQL schema for PIN hashes. Building v0.1 showed simpler
options that keep the same guarantees, and the screen-length test showed four list items per page do not fit.

## Decisions
1. **Texts in JSON** (`data/i18n/{en,sn,nd}.json`), loaded at startup. `MenuValidator` fails startup (and the build's
   tests) if any key is missing in any language; `ScreenTests` render every screen with worst-case data.
2. **PIN hashes in Redis** (`ussd:pinhash:{msisdn}`, no TTL), Argon2id with OWASP's minimum parameters
   (m = 19 MiB, t = 2, p = 1) and a per-PIN salt, encoded as `argon2id$m$t$p$salt$hash` so parameters can rise later.
   Production Redis must run with AOF persistence and backups; the gateway owns no other relational data.
3. **Three items per list page** with 16-character descriptions: four items overflowed 182 characters in isiNdebele.

## Consequences
- ➕ One fewer database; texts are reviewable diffs; the 182-character rule is enforced by tests, not by hope.
- ➖ Losing Redis without a backup means customers reset their PIN by OTP (acceptable for a demo; a production
  deployment would back up Redis or move hashes to PostgreSQL behind the same `IPinStore` interface).
