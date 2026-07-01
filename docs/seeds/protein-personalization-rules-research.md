# Protein-target personalization — research pass

**Status.** Research pass, 2026-07-01. Answers every open question in [`protein-personalization-rules.md`](protein-personalization-rules.md).

**Shape.** This seed is different in kind from the four data-source seeds (Function Health, MFP, Strava, Lumen). Those seeds resolve to "how do we get data X into our backend." This seed resolves to "what shape does Track B's rules engine take, and what signals does it need to see?" It's an internal-architecture spec, not an external-API research task.

**Not being promoted to a session brief.** Tai flagged this as a Track B / next-iteration item; Track B has not started. This doc lands the input contract so Track B's first session can pick up from a specified design rather than re-designing from scratch. When Track B kicks off, promote this to `session-protein-personalization.md` at that time.

**Author.** Claude, grounded against existing codebase (`AgentInputSnapshotBuilder.cs`, `agent-bounds.md`, ADR 0012).

**Date.** 2026-07-01.

---

## 0. Framework the seed answers to

The comparison-chart framework applies only to data-source seeds. This seed sits inside a different framework:

- **Layer 1:** LLM daily card ships first (ADR 0012). ← Done.
- **Layer 2:** Rules engine ships next, computing personalized targets that feed the LLM's input snapshot.
- **Layer 3:** LLM narrates the rules engine's output — original architecture per `architecture.md`.

Tai's protein-personalization feedback IS the shape-signal Track B's rules engine will consume. This seed's job is to lock the **input contract** the rules engine needs, so both Track B (engine) and the current coach (LLM) can be written against the same contract without churn.

---

## 1. Executive summary (60-second read)

**What the rules engine needs, in one sentence:** a `PersonalizedTargets` object keyed on `(user_id, date)` that produces specific g/lb protein numbers (and equivalent single-number targets for carbs, hydration, and caffeine cutoff) instead of the population ranges the coach falls back to today.

**What the engine needs to see to compute those numbers:**
1. **User profile** — body-composition goal, current phase (e.g., "week 3 of 12-week cut"), cycle status (menstrual/perimenopause/menopause), cycle-phase (luteal / follicular / etc. if applicable).
2. **Training summary** — rolled-up view of last 8-12 weeks: weekly volume trend, intensity distribution (percent-time-per-HR-zone or per-strain-band), taper detection signal.
3. **Recent workload** — same as the current coach snapshot, but explicitly tagged.

**What the user has to tell us that we don't already know:** goal + cycle. Everything else derives from data we already ingest (WHOOP now, Strava soon per session-strava). Two new fields on a new `/me/profile` page. Minimum viable is ~4 questions.

**How the coach consumes it:** the input snapshot gets a new `personalized_targets` block with pre-computed numbers, and the persona prompt is updated so the coach uses those numbers instead of the current population ranges. When targets can't be computed (goal not declared, cycle unknown), the block is absent and the coach falls back to the current range language.

**Sequencing recommendation:**
- **Track B Session 1** — build the profile surface + cycle-phase read + rules-engine skeleton computing ONLY the protein target. Everything else (carbs, hydration, caffeine) generalizes from this shape in later sessions.
- **Prerequisites:** MFP session shipped (macro data on the input snapshot); Strava session shipped (better training signal for the volume/intensity rollup).
- **Blocking dependency:** Tai (or a domain SME) authors the actual g/lb table or formula the engine encodes. This is the not-a-code-problem gate.

---

## 2. What Tai actually said

Verbatim, 2026-06-28 after seeing the live coach card:

> One note for the next iteration: the protein target (0.7–1g per lb bodyweight) is a population-level range. As we add more user context — specific body composition goal, training history, cycle phase — I want that to resolve to a specific personalized target rather than a general range. Flag for the rules engine.

Three inputs called out by name: **body-composition goal, training history, cycle phase**. This seed answers each explicitly.

---

## 3. Where the rules engine lives + what it produces

### 3.1 Boundary

New project: `SomaCore.Domain.Rules` (Track B first commit). Plain C# — no I/O deps, no EF Core, matches `SomaCore.Domain` posture. Consumed by `SomaCore.Infrastructure.Agent` when building the input snapshot.

The engine's public surface is one method:

```csharp
public interface IPersonalizedTargetsEngine
{
    PersonalizedTargets Compute(UserProfileSnapshot profile, TrainingSummary training, DateOnly forDate);
}
```

### 3.2 Output shape

```csharp
public sealed record PersonalizedTargets(
    DateOnly ForDate,
    ProteinTarget? Protein,
    CarbsTarget? Carbs,
    HydrationTarget? Hydration,
    CaffeineCutoff? CaffeineCutoff,
    IReadOnlyList<TargetTrace> ReasoningTrail);

public sealed record ProteinTarget(
    decimal GramsPerPoundBodyweight,
    decimal GramsAbsolute,
    TargetConfidence Confidence,
    string Rationale);

public enum TargetConfidence { High, Medium, Low, Unknown }
```

Every target is nullable. If the engine can't compute confidently, it returns `null` for that target and the coach falls back to the population range for that specific number. See §8.

`ReasoningTrail` is a machine-readable audit trail — one entry per rule that fired — so `/admin/agent` can show exactly which rules contributed which numbers. Not sent to the LLM (bloats the prompt); logged in the invocation record for debugging.

### 3.3 What the engine is NOT

- Not a natural-language generator. It emits numbers and confidence, nothing else.
- Not a hard refusal layer. It computes what it can; the bounds validator per `agent-bounds.md` is a separate concern.
- Not sequenced ahead of user profile capture. If profile is empty, engine returns `PersonalizedTargets` with every field null and confidence Unknown.

---

## 4. Required inputs — three signal groups

### 4.1 UserProfileSnapshot

```csharp
public sealed record UserProfileSnapshot(
    Guid UserId,
    decimal BodyweightPounds,          // Latest weight recorded
    DateOnly BodyweightRecordedOn,
    BodyCompositionGoal Goal,          // 'recomp' | 'cut' | 'maintain' | 'gain' | 'not_declared'
    GoalPhase? GoalPhase,              // { started_on, ends_on, weekly_target_delta_lbs }
    CycleStatus CycleStatus,           // 'menstruating' | 'perimenopausal' | 'menopausal' | 'na_male' | 'not_declared'
    CyclePhase? CyclePhase);           // 'follicular' | 'ovulation' | 'luteal' | 'menstrual' — only if CycleStatus=menstruating

public enum BodyCompositionGoal { Recomp, Cut, Maintain, Gain, NotDeclared }
public enum CycleStatus { Menstruating, Perimenopausal, Menopausal, NaMale, NotDeclared }
public enum CyclePhase { Follicular, Ovulation, Luteal, Menstrual }
```

**Bodyweight source:** either user-declared on `/me/profile` OR read from HealthKit (Apple Health cycles it back from Withings, Renpho, MFP, etc). Prefer HealthKit if recent; fall through to user-declared.

**Goal + goal phase:** user-declared. New `/me/profile` fields (§5).

**Cycle status + phase:** either user-declared OR read from HealthKit's Cycle Tracking category (§6). Prefer HealthKit read if it exists; fall through to user-declared.

### 4.2 TrainingSummary

```csharp
public sealed record TrainingSummary(
    Guid UserId,
    DateOnly AsOf,
    int RollingWeeks,                  // Default 8; up to 12
    WeeklyVolume LatestWeek,
    IReadOnlyList<WeeklyVolume> HistoricalWeeks,
    IntensityDistribution IntensityLast8Weeks,
    TaperSignal? Taper);

public sealed record WeeklyVolume(
    DateOnly WeekStart,
    int SessionCount,
    decimal TotalMinutes,
    decimal? TotalDistanceKm,
    decimal? TotalStrainWhoop,
    int SessionsOverZone3);

public sealed record IntensityDistribution(
    decimal PctZone1, decimal PctZone2, decimal PctZone3,
    decimal PctZone4, decimal PctZone5);

public sealed record TaperSignal(
    decimal WeekOverWeekVolumeDelta,   // Negative = decreasing volume
    bool AppearsInTaper);              // Simple heuristic: >30% drop from 4-week baseline
```

**Computed on-the-fly during input-snapshot build.** No new tables. Query the existing WHOOP + Strava + HealthKit workout stores (once those ship), roll up in memory, hand to the engine.

For phase 1 (only WHOOP shipped): only `SessionCount`, `TotalMinutes`, `TotalStrainWhoop` populated. `IntensityDistribution` unavailable (WHOOP flattens HR into strain; no zone breakdown). Rules engine handles nulls gracefully.

For phase 2 (Strava shipped per `session-strava-integration.md`): all fields populated. `IntensityDistribution` comes from Strava's `hr_zones` field via the merged workout view.

### 4.3 Recent workload

Same shape the input snapshot already carries — last 7 days of recoveries, sleeps, workouts. Rules engine reads it directly from the existing snapshot builder output. No new query.

---

## 5. `/me/profile` — the onboarding surface

Smallest surface that captures goal + cycle without turning `/me` into a health-app funnel.

### 5.1 First iteration (4 fields, single page)

New Razor Page at `/me/profile`, behind existing `[Authorize]`.

1. **Bodyweight** (numeric, pounds, latest reading).
   - Show a "Prefer to pull from Apple Health? Install the iOS companion" link if the user hasn't installed it yet.
2. **What are you working toward right now?** (radio buttons):
   - Recomp (lose fat while gaining/maintaining muscle)
   - Cut (lose weight primarily)
   - Maintain (stay where I am)
   - Gain (build muscle / gain weight)
   - I'd rather not say
3. **Where are you in that goal?** (2 fields, optional):
   - Start date (date picker)
   - Weekly target (numeric, lbs — negative for cut, positive for gain)
4. **Cycle status** (radio buttons — copy Tai-authored):
   - I have a menstrual cycle
   - I'm perimenopausal
   - I'm menopausal / post-menopausal
   - Not applicable
   - I'd rather not say

If (1) chose "I have a menstrual cycle," show one more question:

5. **Where are you in your cycle right now?** (radio buttons):
   - Menstrual (bleeding)
   - Follicular (post-bleed, pre-ovulation)
   - Ovulation
   - Luteal (post-ovulation, pre-bleed)
   - I don't know / I'm not tracking
   - Prefer to pull from Apple Health (iOS companion required)

**Total time to fill out:** ~90 seconds. This is the whole surface. No BMR calculator, no macro-target calculator, no injury history, no dietary restriction list. Those are separate future surfaces if we need them.

### 5.2 What NOT to include in first iteration

- Age / DOB / birth sex (we have Entra profile; if we need it, pull, don't ask)
- Height (only relevant if we compute BMI or BMR; we don't for protein-target)
- Injury history, dietary restrictions, allergies (coach handles gracefully by staying category-general; these fields become bounds-adjacent if we ever add them)
- Activity level / TDEE questionnaire (WHOOP + Strava give us this from actual data; we don't need self-reported)

### 5.3 Data model

```sql
CREATE TABLE user_profiles (
    user_id                     uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    bodyweight_pounds           numeric,
    bodyweight_recorded_on      date,
    bodyweight_source           text,          -- 'user_declared' / 'healthkit'
    goal                        text,          -- 'recomp' / 'cut' / 'maintain' / 'gain' / 'not_declared'
    goal_started_on             date,
    goal_ends_on                date,
    goal_weekly_target_pounds   numeric,
    cycle_status                text,          -- 'menstruating' / 'perimenopausal' / 'menopausal' / 'na_male' / 'not_declared'
    cycle_phase                 text,          -- 'follicular' / 'ovulation' / 'luteal' / 'menstrual' / null
    cycle_phase_source          text,          -- 'user_declared' / 'healthkit'
    cycle_phase_updated_at      timestamptz,
    updated_at                  timestamptz NOT NULL DEFAULT now()
);
```

One row per user. Nullable everything. Cascade-deletes on `users`.

### 5.4 Nudge posture

The coach today outputs the population range. When the engine detects an empty profile, it stays silent (returns nulls); the coach falls back to the range language AND is instructed by prompt to append a short "your target is a range until you fill in your goal at /me/profile" nudge on that action. Not on every card — once per week per action-category with missing profile.

---

## 6. Cycle-phase input model — user-declared vs. HealthKit read

### 6.1 Two paths, prefer HealthKit if present

- **User-declared** (§5.1 field 5): user picks the phase; we timestamp and hold. Stale after 5 days (rule of thumb — no phase lasts longer than a week for menstrual cycles).
- **HealthKit read**: Apple Health tracks menstrual cycles natively (`HKCategoryTypeIdentifierMenstrualFlow`, `HKCategoryTypeIdentifierIntermenstrualBleeding`, `HKCategoryTypeIdentifierOvulationTestResult`). Our iOS companion (session-mfp shipped) can read these with additional permission. Phase can be computed from cycle-start timing.

Prefer HealthKit if present + recent (within 30 days of last-tracked entry). Fall through to user-declared. Fall through further to "unknown."

**Additional HealthKit permission** to add to the iOS-companion permission set (extends session-mfp §1.1):
- `HKCategoryTypeIdentifierMenstrualFlow`
- `HKCategoryTypeIdentifierIntermenstrualBleeding`
- `HKCategoryTypeIdentifierOvulationTestResult`

### 6.2 Cycle-phase computation (deterministic function of HealthKit data)

Simple ovulatory-cycle model:
- Menstrual: days 1-N of a bleed event (from `MenstrualFlow` samples).
- Follicular: end-of-bleed through day 12-14.
- Ovulation: days 12-16 (or the day of an `OvulationTestResult=positive` sample).
- Luteal: post-ovulation through next bleed.

Falls back to user-declared or unknown for irregular cycles, perimenopause, or missing data.

### 6.3 Perimenopause / menopause handling

- **Perimenopausal**: cycle phase mostly not applicable (irregular cycles). Rules engine treats phase as null; uses `CycleStatus=Perimenopausal` as a signal for other rules (protein needs increase in perimenopause per some literature; see §9).
- **Menopausal**: same posture, `CyclePhase=null`.

---

## 7. Training-history rollup — how it's computed

### 7.1 Compute-time

On-the-fly during input-snapshot build. No new table.

`AgentInputSnapshotBuilder.BuildAsync` gains a new call to `TrainingSummaryBuilder.BuildAsync(db, userId, asOfUtc, ct)` that:

1. Queries WHOOP workouts (+ Strava when shipped) for the last 12 weeks.
2. Groups by ISO week; aggregates session count, total minutes, total strain, sessions-over-zone-3.
3. Computes the 8-week and 4-week rolling means for volume-trend / taper detection.
4. If Strava data present: rolls up HR-zone distribution (percent-time per zone).
5. Returns `TrainingSummary` record.

Perf: at three users × ~15 sessions/week × 12 weeks = ~180 rows. Trivial.

### 7.2 Storage

None new. Rollup is computed per invocation.

If perf becomes an issue at scale, materialize into a `training_summaries` table with cron-updated rows. Not needed for phase 1.

---

## 8. Fallback policy — when the engine can't compute confidently

Confidence ladder for each target:

- **High** — profile complete, training summary complete, engine's rule fired with all inputs. Coach uses the specific number.
- **Medium** — one input group partial (e.g., goal declared but cycle not; training partial). Engine returns a number with wider tolerance. Coach uses the number with hedged language ("aim for ~140g today; without your cycle-phase data I'd hedge ±10g").
- **Low** — two input groups missing / partial. Engine returns a number at the population-range midpoint with explicit low confidence. Coach reverts to population-range language.
- **Unknown** — everything missing. Engine returns null. Coach uses the current population-range language and appends a nudge to complete the profile.

**Coach prompt update** (encoded in `agent-voice-and-persona.md` when this ships):

> When the input snapshot includes `personalized_targets`, use the number in `targets.protein.grams_absolute` directly. If `targets.protein.confidence == "Low"` or the field is absent, use the population-level 0.7-1.0g/lb range and suggest the user complete their profile at /me/profile — but only once per week per user.

---

## 9. Opinionation choice — published formula vs. Tai-authored table

Three options for the actual g/lb decision the engine encodes:

- **A: Encode a published formula** (e.g., ISSN's evidence-based recommendation: cut phase 1.2-1.6g/lb; maintenance 0.7-1.0g/lb; recomp 1.0-1.2g/lb). Pick a midpoint per phase. Cite the source.
- **B: Encode Tai-authored g/lb table** by phase × goal × cycle. Tai writes it, we encode it verbatim. Auditable and adjustable by her without engineering changes.
- **C: Hybrid** — Tai's table is the primary; if Tai's table has a gap for a particular (goal, cycle-phase, training-load) combination, fall through to published formula midpoint.

**Recommended: C.** Tai's judgment is the source of truth for the parts she has opinions on; published literature covers the parts she hasn't opined on yet. Table lives at `docs/rules/protein-target.md` (new file) as a YAML-ish table so a non-engineer can edit.

Draft table shape (Tai fills in):

```yaml
# docs/rules/protein-target.md
# Protein target in grams per pound bodyweight
# Author: Tai Palacio
# Last reviewed: TBD

goals:
  cut:
    default: 1.2      # g/lb baseline for cut
    cycle_phase_adjustments:
      luteal: +0.1    # increased needs during luteal phase
    training_load_adjustments:
      high_volume_week: +0.1
      taper: -0.1
  recomp:
    default: 1.0
    # ...
  maintain:
    default: 0.85
    # ...
  gain:
    default: 1.1
    # ...

perimenopause_bonus: +0.15   # applied on top of goal baseline
```

The rules engine reads this file at process start, encodes into `PersonalizedTargets` computation logic. Change the file → change the rule (no code change).

**Not-a-code-problem gate:** Tai (or a domain SME she trusts) authors this table. Without it, the engine falls through to published-formula midpoint (Option A) as the default.

---

## 10. Generalization — same shape for other targets

The seed's last open question was about other "population range → personalized number" targets.

Same shape applies to:

- **Carbs** — goal × training load, no cycle adjustment necessary. Add `CarbsTarget` to `PersonalizedTargets`. Add `docs/rules/carbs-target.md`.
- **Hydration** — bodyweight × training load × climate (climate not on our radar yet; defer). Add `HydrationTarget`.
- **Caffeine cutoff** — sleep-target-time − 6 hours (rough default); adjustable per user's caffeine sensitivity (self-declared, or inferred from HRV trends). Add `CaffeineCutoff`.

None require new data sources beyond what's on the current roadmap. All generalize from the protein-target pattern.

**Sequencing:** protein first (Tai flagged it specifically); carbs second (natural pair); hydration third (needs training-load rollup which is dependent on Strava anyway); caffeine cutoff fourth (least urgent, most fun).

---

## 11. Answers to every open question in the seed

### 11.1 "Smallest surface that captures goal + cycle without turning /me into a funnel?"

Four required fields (bodyweight, goal, cycle-status, cycle-phase-if-applicable) + one optional goal-phase block. ~90 seconds. See §5.1. No BMR calc, no injury history, no dietary questionnaire.

### 11.2 "How opinionated should the engine be?"

Hybrid (§9): Tai-authored table primary, published-formula midpoint as fall-through. Table lives in `docs/rules/protein-target.md` as YAML for non-engineer editability.

### 11.3 "Cycle-phase input model — declared vs. app-read?"

Both. Prefer HealthKit read if present and recent (< 30 days from last tracked entry). Fall through to user-declared. Fall through to unknown. See §6.

### 11.4 "Fallback boundary — coach voice change?"

Yes but light. Confidence Unknown → coach uses population range + weekly nudge. Confidence Low → same. Confidence Medium → hedged language ("aim for ~140g today, ±10g"). Confidence High → specific number stated cleanly. §8.

### 11.5 "What other targets have the same shape?"

Carbs, hydration, caffeine cutoff. Same `PersonalizedTargets` record; each field nullable; engine computes independently. §10.

---

## 12. Recommended path forward

**No session yet.** Track B has not kicked off. This doc is the design spec Track B's Session 1 will pick up.

**When Track B Session 1 starts:**

1. Promote this doc to `session-protein-personalization.md` mirroring the shape of the other session briefs (Function Health, MFP, Strava).
2. Ship in this order:
   - `user_profiles` table + `/me/profile` Razor Page + HealthKit permission extension (cycle-tracking types).
   - `TrainingSummaryBuilder` extension in `AgentInputSnapshotBuilder`.
   - `SomaCore.Domain.Rules` project with `IPersonalizedTargetsEngine` skeleton.
   - `docs/rules/protein-target.md` (Tai authors; blocker for engine).
   - Coach persona prompt update to prefer `personalized_targets.protein` when present.

**Not-a-code-problem gates for Track B Session 1:**
- **Tai (or SME) authors `docs/rules/protein-target.md`.** This is the hard blocker. Without it, engine falls through to published-formula midpoint — usable but underwhelming.
- **Tai reviews the `/me/profile` question wording** (§5.1). Cycle-related questions are cycle-user-facing language; Tai has strong opinions on tone here.
- **Privacy doc addition** covering profile data + cycle data sent to Anthropic. New Section D bullet: cycle-phase category + goal category + bodyweight (rounded to nearest 5 lbs) are sent; specific goal-start-date and precise bodyweight stay server-side.

**Prerequisites in real-code terms:**
- MFP session (Track D Session 2) shipped — provides macro data the engine can reason against.
- Strava session (Track D Session 3) shipped — provides intensity-distribution + taper-detection signals the engine uses.
- iOS companion built — provides HealthKit reads for bodyweight + cycle-phase.

Track B Session 1 could technically start before Strava ships (rules engine handles nulls gracefully), but the engine's output is less useful without the training-signal rollup.

---

## 13. Verification appendix — sources

This seed is internal-architecture. Load-bearing citations are to our own codebase and ADRs:

- **`AgentInputSnapshotBuilder.cs`** — current input snapshot shape. Engine's output plugs in alongside `recoveries`, `sleeps`, `workouts`. — `src/SomaCore.Infrastructure/Agent/AgentInputSnapshotBuilder.cs`
- **`agent-bounds.md`** — Tai's IN BOUNDS list includes `Macros` and `Fueling and meal timing`; protein target lands under `Macros`. — [`docs/agent-bounds.md`](../agent-bounds.md)
- **`agent-voice-and-persona.md`** — Tai's voice spec; must be extended to instruct the coach on `personalized_targets` field usage. — [`docs/agent-voice-and-persona.md`](../agent-voice-and-persona.md)
- **ADR 0012** — LLM before rules engine sequencing. This seed is on-plan. — [`docs/decisions/0012-llm-card-before-rules-engine.md`](../decisions/0012-llm-card-before-rules-engine.md)

**External literature (for the fall-through published-formula case):**
- ISSN Position Stand: Protein and Exercise (multiple editions). Widely cited g/lb ranges by training population + goal.
- Helms, Zinn, Rowlands. "A Systematic Review of Dietary Protein During Caloric Restriction..." — supports 1.2-1.6g/lb during cut for trained lifters.
- Kwon, Hong, et al. "Menstrual cycle effects on protein turnover" — supports luteal-phase protein adjustment.

Not verifying these here — Tai's judgment overrides literature per §9 anyway. Citing for the fall-through case if Tai's table is incomplete when Track B ships.

---

## Change log

- **2026-07-01 (initial):** Research pass for the shape of Track B's rules engine input contract, driven by Tai's 2026-06-28 feedback. Not being promoted to session brief because Track B has not started. This doc is the design spec Track B Session 1 will pick up. Load-bearing decisions: (1) new `SomaCore.Domain.Rules` project boundary; (2) `PersonalizedTargets` record shape with nullable per-target fields and confidence ladder; (3) `/me/profile` surface with exactly four required fields; (4) HealthKit cycle-phase read preferred over user-declared when recent; (5) hybrid opinionation — Tai-authored table primary, published-formula midpoint as fall-through; (6) generalizes to carbs / hydration / caffeine cutoff without shape change.
