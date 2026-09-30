# InsureHub USSD — test strategy

| Level | What |
|---|---|
| Unit | menu graph validation (no dead ends, every option leads somewhere, every node reachable), PIN rules, masking, `text` parsing |
| **Screen property test** | render every node × every language with worst-case realistic data (longest names, 4 policies, 6-digit ZWG amounts) → assert ≤ 182 chars and GSM-7 only |
| Flow tests | `WebApplicationFactory` + fake backends + Testcontainers Redis: scripted dial sequences (`""` → `"1"` → `"1*1234"` …) asserting each `CON`/`END` screen (snapshot tests with Verify) |
| Resilience | fake backend delays of 1.4 s / 1.6 s / 5 s → cached response or graceful `END`, always under 2 s total |
| Security | PIN lockout, weak PIN rejection, spoofed callback rejected, PIN never in logs (log capture assertion) |
| Load | k6 or NBomber: 200 concurrent sessions, p99 < 2 s on the demo node |
| Manual | simulator walkthrough in all three languages; review of the Shona and Ndebele text by a native speaker |
