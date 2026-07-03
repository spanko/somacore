# DRAFT — proposed privacy-doc additions for the new data sources

**Status.** DRAFT for Tai's review, prepared 2026-07-01 for the 2026-07-02 meeting. **Nothing here is live.** These are the proposed additions to [`privacy-data-handling.md`](privacy-data-handling.md) for the three builds in the [decision pack](track-d-decision-pack.md). Per our working agreement, nothing touching consent, retention, or disclosures merges without Tai's review — this file exists so that review can happen against exact text instead of a promise of text.

**How to read this.** Three parts, one per build. Each part has (a) the proposed new/changed language and (b) the review checkboxes that would be appended to the "For Tai's review" section. Sign-off can be per-part — each build is gated only on its own part.

**On merge:** each approved part moves into `privacy-data-handling.md` (Section F for labs; D.1/D.2 edits for nutrition and Strava; new Section G for the iPhone app), and this draft file is deleted.

---

# Part 1 — Function Health lab uploads (gates the Function Health build)

## 1a. Edit to existing Section D.2

Section D.2 currently excludes all lab data with the line:

> Any **lab data, clinical notes, or external documents.** [...] That ingestion surface does not exist yet. When it lands it will get its own section here before any lab content can be sent to Anthropic.

Proposed replacement — the surface now lands, and Section F below is that "own section":

> **Structured lab biomarker data**, when the user has uploaded a Function Health PDF via the `/me/labs` surface described in Section F. What gets sent: canonical biomarker name (e.g. `vitamin_d_25_hydroxy`), numeric value, unit, reference range low/high, collection date, and per-marker in/out-of-range flag. What does NOT get sent — see Section F.2 for the full list, but note here that we do not forward: the PDF file itself, the PDF filename, any personal identifiers extracted from the PDF header, clinician notes, or non-biomarker page content. Only the structured biomarker rows leave our infrastructure.

## 1b. New Section F

> ## F. What we send to Anthropic when the user has uploaded lab results
>
> The SomaCore AI reads user-uploaded lab biomarker data (currently: Function Health panels) as one of its input surfaces. This section describes what leaves our infrastructure when a user has one or more uploads on file — and what does not. **This is a superset of Section D; D still holds for the WHOOP data. F adds the lab surface.**
>
> ### F.1 What gets sent
>
> For each biomarker we have a value for (across all of the user's **confirmed** uploads, most-recent-per-marker in the last 90 days by default):
>
> - `biomarker_name` — canonical name (e.g. `vitamin_d_25_hydroxy`, `ferritin`, `apolipoprotein_b`)
> - `display_name` — the human-readable name as it appears on the PDF
> - `category` — e.g. `nutrients`, `heart`, `metabolic`, `hormones`, `thyroid`
> - `numeric_value` (nullable) — the measured value
> - `string_value` (nullable) — for qualitative results (`Positive`, `Negative`, `Within reference range`)
> - `unit` — `ng/mL`, `mg/dL`, `IU/mL`, etc.
> - `reference_low`, `reference_high` (nullable)
> - `collected_at` — the panel's collection date (used to say "uploaded March 2026")
> - `flagged` — `in_range` / `low` / `high` / `unknown`
>
> Plus the two static context blocks Anthropic already sees in Section D: the system prompt (voice + bounds) and the anonymous internal user reference.
>
> **The user confirms extraction before any of this is eligible to be sent.** After upload, the user is shown every extracted biomarker and must confirm the extraction is correct. Unconfirmed uploads are never read by the coach and never sent to Anthropic.
>
> ### F.2 What we explicitly do NOT send
>
> - **The PDF file itself.** We do not attach the source document to any Anthropic request.
> - **The PDF filename.** May contain the user's real name in Function's default naming.
> - **Any personal identifiers from the PDF header** — name, date of birth, MRN, sample ID, Quest requisition number, ordering clinician name.
> - **Raw page images / OCR intermediate text.** Only the schema-validated biomarker rows.
> - **Clinician notes / interpretive text** written on the Function report. The coach interprets from the numbers, not from Function's clinician language.
> - **Non-biomarker page content.** Marketing, disclaimers, patient-instruction blocks — discarded during extraction.
> - **The user's Function account identity.** We never store the user's Function login, session token, or Function-side member ID. There is no Function OAuth grant in the upload path.
>
> ### F.3 Processing note — the extraction itself uses Anthropic
>
> The PDF-to-structured-rows extraction is performed by the Anthropic API: the PDF content is transmitted to Anthropic **once, at parse time**, to produce the structured rows. This is a separate, narrower exposure than F.1: it happens once per upload (not per card), under the same Commercial Terms posture as Section D.3 (no training, transient processing). F.2's exclusions apply to the *card-generation* payload; the parse-time payload necessarily includes the PDF content. The policy must describe both.
>
> ### F.4 Storage: the lab_uploads and lab_biomarkers tables
>
> - `lab_uploads` stores the PDF envelope: file bytes, collection date, parse status, confirmation status. Cascades on user deletion.
> - `lab_biomarkers` stores the parsed rows. Cascades on both user deletion and upload deletion.
> - `agent_actions.lab_upload_id` — nullable FK so every lab-sourced recommendation is traceable to its upload. Upload deletion nulls the FK (card history preserved); user deletion cascades everything.
>
> ### F.5 Retention
>
> No automatic purge in phase 1. Same posture as `agent_invocations` and `oauth_audit`. Recommendation: keep uploaded PDFs indefinitely (user-generated content the user can delete themselves) and keep parsed biomarker rows indefinitely (substrate for longitudinal trend analysis). Tai's call — if the policy commits to a retention window, both tables get the same scheduled cleanup job pattern.
>
> ### F.6 Who bears the recommendation risk
>
> The persona already carries the "consult your clinician" hedge. Cards that reference a lab-sourced action carry the `Source: Function Health panel, uploaded <date>` provenance tag mechanically — the validator rejects any lab-sourced action without the source + upload FK, so a lab-sourced action cannot render without the source disclosure. The coach never diagnoses: it states the value, states the general-wellness action, and hedges to a clinician on anomalous patterns. Function's own terms take the same posture (results are informational; discuss concerns with a clinician).
>
> ### F.7 What the user sees on /me when a lab-referenced card renders
>
> The Section D.6 disclosure gets a per-invocation extension when a lab-referenced action is included:
>
> > *This card references your Function Health panel uploaded [date]. We do not send your name, the PDF file, or your Function account information — only the numeric biomarker values and their reference ranges. [How this works.](TBD-link)*
>
> The extended disclosure appears only when a lab-sourced action is on the card.

## 1c. Review checkboxes (append to "For Tai's review")

- [ ] **Structured lab biomarker data goes to Anthropic** when a user has a confirmed upload (F.1). Confirm the surface area: biomarker name, value, unit, reference range, collection date, flag.
- [ ] **The PDF itself goes to Anthropic once, at parse time** (F.3). This is the extraction mechanism. Confirm acceptable and that the policy must describe it.
- [ ] **What does NOT go at card time** (F.2): PDF file, filename, personal identifiers, clinician notes, raw OCR text. Confirm the list is complete.
- [ ] **Confirm-before-coach-reads** (F.1): unconfirmed uploads are never used. Confirm this is the right safeguard shape.
- [ ] **Upload is user-initiated** — no Function OAuth, no automated Function-side access.
- [ ] **Provenance tagging** (F.6): every lab-sourced action mechanically carries `Source: Function Health panel, uploaded <date>`. Confirm sufficient.
- [ ] **Retention** (F.5): indefinite by default, user-deletable. Confirm or pick a window.

---

# Part 2 — MyFitnessPal nutrition + the iPhone companion app (gates the MFP build)

## 2a. Addition to Section D.1 (what gets sent)

New numbered item after the physiological input window:

> **Aggregated MyFitnessPal nutrition data.** When a user has installed the SomaCore iOS companion app (or uploaded their MFP data export), the input snapshot additionally includes: (a) the last 7 days of daily nutrition rollups — calories, protein, carbs, fat, fiber grams, and meals-logged count per day; (b) the last 3 days of per-meal entries — date, meal slot (breakfast/lunch/dinner/snack), and macronutrient totals per meal. **Individual food-item names never leave our infrastructure** — they are stripped before the snapshot is built, regardless of ingestion path.

## 2b. Addition to Section D.2 (what does NOT get sent)

> - **Individual food-item names, brands, or restaurant identifiers from MyFitnessPal.** The coach receives macronutrient totals per meal slot and meal timing — not the specific foods logged. Food-choice patterns can be sensitive (dietary restrictions, disordered-eating-adjacent behaviors, cultural food identity); they stay inside our infrastructure.

## 2c. New Section G — the iOS companion app

> ## G. The SomaCore iOS companion app
>
> ### G.1 What it is
>
> A small iPhone app (TestFlight distribution in the alpha) whose only jobs are: sign the user in with the same Microsoft Entra account as the website, ask the user's permission to read specific Apple Health categories, and forward new entries to our API. It renders no health data of its own and stores none on the device beyond the sign-in token (kept in the iOS Keychain).
>
> ### G.2 What it reads, with the user's explicit permission
>
> Apple's HealthKit permission screen lists every category we request, and the user can grant or deny each one individually. Initial request set (nutrition build): dietary energy, protein, carbohydrates, total fat, fiber, sugar, sodium. Later builds extend the request set (workouts for Strava; menstrual-cycle categories for target personalization) — **each extension re-prompts the user through Apple's permission screen; nothing is read without a granted permission.**
>
> ### G.3 Data flow
>
> MyFitnessPal (or any health app) writes to Apple Health on the user's phone. Apple Health is device-local — Apple's design; there is no server-side access to it, by us or anyone. Our companion app reads new entries on-device and transmits them over TLS to our API, authenticated as the user. No third party sits in the path. The data lands in the same Postgres tables and inherits the same cascade contract as everything else keyed to the user: disconnecting or uninstalling stops future ingestion; account deletion removes all ingested rows.
>
> ### G.4 What the companion never does
>
> - Never writes to Apple Health.
> - Never reads categories the user hasn't granted.
> - Never caches health data on the device (transmit-and-forget).
> - Never sends anything directly to Anthropic — all Anthropic-bound payloads are built server-side under Sections D/F rules.
>
> ### G.5 Revocation
>
> The user can revoke HealthKit permission in iOS Settings, or uninstall the app; both stop ingestion immediately. Previously ingested rows remain (same posture as WHOOP disconnect: severing the integration doesn't destroy the user's own history) and are removed by account deletion.

## 2d. Review checkboxes

- [ ] **Nutrition rollups + per-meal macros go to Anthropic** (2a). Confirm the surface area.
- [ ] **Food names never leave our infrastructure** (2b). Confirm this is the commitment you want stated.
- [ ] **The iOS companion's permission model** (G.2): per-category Apple permission screen, re-prompt on every extension. Confirm.
- [ ] **No new third-party processor** — the companion talks only to our API. Confirm this is accurately reflected in the policy.
- [ ] **Revocation posture** (G.5): revoke/uninstall stops ingestion, history stays until account deletion. Confirm.
- [ ] **CSV-upload path** (MFP data export): same data, same rules, user-initiated. No separate disclosures needed beyond 2a/2b — confirm.

---

# Part 3 — Strava (gates the Strava build)

## 3a. Addition to Section D.1 (what gets sent)

> **Strava activity data.** When a user has connected Strava, the input snapshot additionally includes, per workout in the window: activity type, start time, duration, distance (rounded to the nearest 100 m), total elevation gain, average and max heart rate, heart-rate-zone time distribution, split count with fastest/slowest split pace, average cadence, average power, and the WHOOP strain score when the same workout was also captured by WHOOP.

## 3b. Addition to Section D.2 (what does NOT get sent)

> - **Location or route data of any kind from Strava.** No GPS coordinates, no route polylines, no start/end points, no segment names, no gear identifiers, no kudos or comments, no activity descriptions or photos. Routes can reveal home address, workplace, and training partners — treated as maximally sensitive. The coach reasons about training effort; it does not need to know where the user was. This data is stored in our database (it arrives inside Strava's API responses) but is stripped before any Anthropic-bound payload is built.

## 3c. Storage + token note (folds into Sections A–C patterns)

> Strava connects via OAuth exactly like WHOOP: tokens in Key Vault (never the database), refresh rotation handled identically, disconnect deletes the connection row and soft-deletes the Key Vault secret, activity history survives disconnect anonymized to the deleted connection, account deletion cascades everything. Strava-side revocation is called best-effort on disconnect, matching the WHOOP posture in Section C.

## 3d. Review checkboxes

- [ ] **Strava workout detail goes to Anthropic** (3a) — zones, splits summary, cadence, power. Confirm the surface area.
- [ ] **No location data to Anthropic, ever** (3b). Confirm this is the commitment you want stated — note we DO store the raw Strava responses (including route data) in our database; the strip happens at snapshot-build time. If you'd rather we not store routes at all, that's a build-time decision to make now.
- [ ] **Strava token custody mirrors WHOOP** (3c). Confirm no new language needed beyond extending Sections A–C to name Strava.
- [ ] **Strava is a data source, not a processor** — no middleman service in the path (Strava bans them anyway). Confirm.

---

# Part 4 — Quick-log: user-typed entries on /me (gates enabling the quick-log feature)

**Added 2026-07-02.** The quick-log build (see [`session-quick-log.md`](session-quick-log.md)) ships with the feature flagged OFF; this part's sign-off flips it on.

## 4a. Addition to Section D.1 (what gets sent)

> **User-typed quick-log lines.** When the user submits a line in the "tell the coach something" box on `/me` (e.g. *"lunch: chicken bowl, ~50g protein"*), that text is sent to Anthropic once to extract a structured entry. The user then reviews and confirms the extraction before anything is stored; confirmed entries and notes subsequently appear in the daily-card input snapshot like any other user data. **We cannot pre-filter what a user chooses to type** — this is the first input surface whose content we don't construct. The input box carries an inline notice: *"What you type here is processed by our AI provider — same rules as your card data: never used for training, no identifiers attached."*

## 4b. What does NOT change

- No identifiers attached (same anonymous internal reference as all invocations).
- Same Anthropic Commercial Terms posture (Section D.3) — no training, transient processing.
- Extraction invocations are logged in `agent_invocations` like card invocations, same retention posture.
- Nothing persists without the user's explicit Confirm; discarded extractions leave only the invocation log row (which contains the typed line, per E.1's input-snapshot posture).

## 4c. Storage

- Confirmed meals → `mfp_food_entries` (`source='manual'`); workouts → `healthkit_workouts` (`source_bundle_id='manual'`); notes → `user_notes`. All cascade on account deletion; all user-deletable individually from `/me`.
- Notes are **visible memory**: shown on `/me` with a delete button, optionally auto-expiring ("traveling until Friday"). Deleting a note removes it from all future snapshots.

## 4d. Review checkboxes

- [ ] **User free text goes to Anthropic** for extraction and, once confirmed, in future card snapshots. This is user-authored content we can't pre-filter. Confirm the inline-notice approach is adequate disclosure.
- [ ] **Discarded extractions still leave an invocation-log row containing the typed text** (consistent with how we log all invocations). Confirm, or require purging discarded-extraction rows.
- [ ] **Notes-as-visible-memory** (4c): explicit, deletable, expiring. Confirm this is the memory posture you want.
- [ ] **Confirm-before-persist** applies to every quick-log write. Confirm.

---

# One open question that spans all four parts

**Does the `/me` disclosure block (Section D.6) need updating per-source, or does one sentence cover all sources?** Current draft thinking: the base disclosure grows to *"...from your last 7 days of WHOOP, nutrition, and workout data"* once those sources are live, and the lab-specific extension (F.7) appears only on cards that reference a lab. Tai to confirm the disclosure shape she wants.
