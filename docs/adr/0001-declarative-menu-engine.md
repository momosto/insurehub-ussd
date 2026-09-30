# ADR-0001: Define USSD menus as data interpreted by a small engine

- **Status:** Accepted · **Date:** 2026-09-30

## Context
USSD menus change often (new products, wording, languages) and must stay within 182 characters in three languages. Hard-coded `switch` statements on the `text` string are the common approach, and they become unmaintainable fast.

## Options
1. `switch` on the input path (`"2*1*1"`) in code.
2. **Declarative menu graph (JSON) + `MenuRuntime` + small handler classes.**
3. A third-party USSD framework.

## Decision
Option 2.

## Consequences
- ➕ Menus can be validated automatically: no dead ends, every language present, every screen ≤ 182 characters.
- ➕ Business users could edit wording without code changes (with review).
- ➖ A small engine to maintain; kept under ~300 lines and fully unit-tested.
