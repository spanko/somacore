# Integrations build loop — plan

**Written:** 2026-07-10, for execution in a fresh session.
**Kickoff (paste in the fresh session):**

> /loop Work the integrations queue at `docs/loops/integrations-QUEUE.md` per the plan at `docs/loops/integrations-loop.md`. One item per iteration: implement → evaluate with the loop-engineering:loop-evaluator agent → commit+push only on PASS → update the queue file. Halt conditions and caps are in the plan. Stop when the queue is done or a halt condition fires.

**Scope decision — what loops and what doesn't.** A loop's evaluator must be able to *act* on the work (run it, test it). That admits the two server-side tracks and excludes the rest:

| Track | Loopable? | Why |
|---|---|---|
| Strava server-side (S1–S7) | **Yes** | Full brief with exit criteria; evaluator can build, run the suite, check DoD |
| MFP CSV-upload path (M1) | **Yes** | Same |
| iOS companion | **No** | No Mac/Xcode here — nothing can compile or run Swift+HealthKit, so an evaluator could only *read* the code, which is the nodding-loop anti-pattern. One-shot scaffold task + Adam pairing instead (§6). |
| Function Health Phase 2 | **No** | Blocked on Function-side OAuth client registration — an external gate no loop can open. Adam action (§7). |

---

## 1. The five moves, mapped

1. **Discovery** — read `integrations-QUEUE.md`; the work item is the first not marked `done`. The queue is ordered by dependency (S1 → S7, then M1); never skip ahead past a `blocked` item's dependents.
2. **Handoff** — items run **serially** (each depends on the previous; parallelism is deliberately absent — see caps). Work directly in the repo checkout; the queue file marks exactly one item `in_progress`.
3. **Verification** — after implementing an item, invoke the **`loop-engineering:loop-evaluator`** agent (a different agent, skeptical by design) with the item's DoD block verbatim. The evaluator must RUN: `dotnet build src/SomaCore.sln`, `dotnet test src/SomaCore.sln`, `dotnet format src/SomaCore.sln --verify-no-changes`, plus the item-specific checks. PASS requires all green + every DoD line evidenced. The generator never grades its own work.
4. **Persistence** — on PASS: one commit per item (conventional message, `Co-Authored-By: Claude`), push to `main`, mark the item `done` in the queue file with the commit hash, and append one line to the Learnings section (§5) if anything surprised you. On FAIL: fix and re-evaluate (max 2 retries), then follow halt rules.
5. **Scheduling** — local `/loop` (self-paced) while Adam's machine is on: the suite needs Docker (Testcontainers) and the local .NET toolchain. No cloud/cron variant — do not run this loop where `docker info` fails.

## 2. The human-review door (adapted to this repo, deliberately)

This repo's standing directive is commit-to-main, no branches (Adam, 2026-06-24). The review door is therefore **not** a PR gate; it is these three, all hard:

1. **Nothing the loop builds is reachable by users.** Every surface ships behind a flag that is FALSE by default and FALSE in dev parameters until Adam flips it (`Strava:Enabled`; MFP CSV rides the existing `QuickLog:Enabled`... no — new surfaces get `Mfp:CsvUploadEnabled`). The loop never edits `parameters.dev.json` to enable anything.
2. **The loop never deploys.** No `az` calls, no `deploy-*.ps1`, no dev-DB migrations. Migrations are committed but applied by Adam at deploy time. CI (build+test+format on push) is the second, independent checker after the evaluator.
3. **Anything uncertain goes to the inbox, not into code.** Ambiguity in a brief, a needed secret, a schema question, an API surprise → append to `docs/loops/integrations-INBOX.md` with context, mark the item `blocked`, move to the next non-dependent item or halt per §4.

If Adam prefers a PR gate instead, change §2.1 of this file to "branch `loop/integrations`, open PR, never merge" — one-line change, rest of the plan unchanged.

## 3. Evaluator contract (paste to loop-evaluator per item)

> You are the skeptical checker. The generator claims item <ID> is done. Assume it is broken until proven otherwise. Run: (1) `dotnet build src/SomaCore.sln` — zero warnings/errors; (2) `dotnet test src/SomaCore.sln` — full suite green, and confirm the item's NEW tests exist and actually exercise the claimed behavior (read them; a test that can't fail is a FAIL); (3) `dotnet format src/SomaCore.sln --verify-no-changes`; (4) every line of the item's DoD block below, with evidence (file:line or test name or command output); (5) the privacy greps in the DoD where present — these are commitments to a lawyer, treat a miss as a hard FAIL; (6) confirm no flag default flipped to true and no deploy/az/db-update calls were added. Verdict: PASS or FAIL with the specific failing check.

## 4. Caps + halt conditions (set before the first unattended run)

- **Retries:** max 2 evaluator round-trips per item. Third FAIL → item `blocked` + inbox entry.
- **Halt the whole loop when:** (a) 2 consecutive items end `blocked`; (b) the full suite is red on `main` for any reason the current item didn't cause; (c) any change would touch auth flows, secret storage, retention, webhook contracts, or repo layout beyond what the session briefs specify (CLAUDE.md escalation list) — inbox + halt; (d) queue is empty.
- **Budget:** this is bounded work (8 items, each ≈ one focused implementation pass + one evaluator pass). If any single item exceeds ~3 hours of iteration or the loop exceeds ~10 total evaluator invocations, halt and write the inbox entry — that smells like a misunderstood brief, and grinding past it burns tokens on the wrong problem.
- **No parallelism.** Items are dependency-ordered; a second worker would tangle. Parallelism is earned later, not default.

## 5. State + learnings

`integrations-QUEUE.md` is the loop's memory: item statuses (`todo` / `in_progress` / `done <hash>` / `blocked`), plus a **Learnings** section at the bottom — one line per surprise (wrong assumption in a brief, flaky test, API mismatch). The next session reads it before its first item. The inbox (`integrations-INBOX.md`) is for Adam; the queue is for the loop.

## 6. NOT in the loop: iOS companion scaffold (one-shot, human-paired)

Generate once, verify by pairing: a SwiftPM-structured skeleton under `ios/SomaCoreCompanion/` — MSAL Entra sign-in, Keychain token storage, `HKObserverQuery` + `HKAnchoredObjectQuery` for the nutrition types, batch POST to `/api/ingest/healthkit` (endpoint to be built as part of the MFP session proper, NOT this queue), `Info.plist` requirements documented inline, plus the on-device spike checklist from `session-myfitnesspal-integration.md` §1.1 (MFP's HK meal-slot metadata key; the 10-min Lumen `com.metaflow.lumen` check rides along). **Adam builds it in Xcode; the first compile is the evaluation.** Do not put this in the queue — an unverifiable item teaches the loop to trust unread code.

## 7. Blocked / Adam-side gates (the loop can't open these)

- **Strava dev account** ($11.99/mo) + `strava-client-id`/`strava-client-secret` in Key Vault + webhook subscription registration → unblocks *deploying* S-items (building them isn't blocked: tests use canned handlers).
- **Function Health Phase 2:** probe DCR against Function's Auth0 / email `hello@functionhealth.com` for a client_id. Until answered, Phase 2 stays a watchlist item.
- **Real-data acceptance fixtures** (post-loop, pre-flag-flip): Adam's real MFP export ZIP for M1; real Function PDFs for the labs surface already live; a real Strava account connect for S-items end-to-end.
- **Tai:** privacy Parts 1, 4, 5 review + the Strava location language (drafted in `privacy-draft-track-d.md` Part 3); conversational voice addendum.

## 8. Comprehension guard

After the loop finishes (or halts), Adam reads the queue's Learnings + `git log --oneline` for the loop's commits and spot-reads ONE diff end-to-end (suggested: S3, the webhook receiver — it's the security-sensitive one). The loop executes; it does not decide.
