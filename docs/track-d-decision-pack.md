# New data sources for the coach — what we found, what we recommend

**For:** Adam + Tai review, July 2, 2026
**Covers:** the four data sources Tai asked for on June 28 (Function Health, MyFitnessPal, Strava, Lumen), plus the protein-target personalization she flagged for the next iteration.

---

## The short version

We researched all five requests and verified everything against each company's own documentation. Three are ready to build. One is blocked by the vendor. One is designed and waiting on inputs only Tai can provide.

**The single biggest finding:** we're going to build a small iPhone app. Not the full mobile app — a lightweight companion that reads Apple Health on the user's phone and sends the data to our system. That one app unlocks MyFitnessPal, parts of Strava, possibly Lumen, cycle tracking, body weight, and everything an Apple Watch records — all through a single door, at no ongoing cost, with no outside company handling the data. Adam confirmed we can build and distribute it (he has three apps in TestFlight already), and that we'll do iPhone and Android as separate native apps rather than a cross-platform shortcut.

**The alternative we rejected:** paying a middleman. There are companies (Terra, Junction, and others) whose whole business is piping fitness-app data to products like ours. We checked all of them. The two that actually work cost **$300–$500 per month** — $3,600–$6,000 a year — to serve our three users, and they put a third company in the middle of our users' health data. The iPhone app does the same job for free, and the data goes straight from the user's phone to us.

---

## Summary table

| Source | Can we get the data? | How | Ongoing cost | Status |
|---|---|---|---|---|
| **Function Health** | Yes | User uploads their results PDF; we extract the values; user confirms them before the coach sees them | $0 | **Ready to build** |
| **MyFitnessPal** | Yes | iPhone app reads what MFP shares with Apple Health; spreadsheet upload as backup | $0 | **Ready to build** |
| **Strava** | Yes | Connect directly to Strava (like we did with WHOOP), plus the iPhone app as backup | $12/month | **Ready to build** |
| **Lumen** | Not today | No developer access exists; a 10-minute check during the iPhone-app build may open a side door | $0 | **On hold** |
| **Protein target** | N/A (internal work) | Needs a short user profile (goal, cycle status) + a target table Tai authors | $0 | **Designed, waiting on Tai** |

---

## Function Health — ready to build

**What we found.** Function has no way for us to pull a user's actual lab values directly. They offer a connection for AI tools, but we verified it only shares category summaries ("3 markers out of range in Heart") — never the numbers themselves. Their own documentation is explicit about this.

**What we recommend.** Two pieces that work together:

1. **Now:** the user downloads their results PDF from Function and uploads it to our site. We extract every biomarker automatically, then show the user exactly what we extracted and ask them to confirm it's right. **The coach never uses lab values the user hasn't personally confirmed** — that's our safeguard against extraction errors. Once confirmed, the coach can say things like *"Vitamin D is 22, below the reference range — take 2000 IU with your first meal,"* always citing which upload it came from.
2. **Later:** we use Function's summary connection for one narrow job — noticing when something changed. If Function's summary stops matching the user's last upload, we show a nudge: *"Your Function panel has changed since your last upload — upload the new PDF."* So the user never has to remember.

**Cost:** none. **Effort:** the smallest of the three builds.

**What we need:** Tai's sign-off on a new privacy-document section covering lab data (drafted, ready for her review), and a real results PDF from each of the three of us so we can test extraction against known-correct answers.

---

## MyFitnessPal — ready to build, and the reason we're building the iPhone app

**What we found.**

- MyFitnessPal shut their developer program years ago and their site still says they're not accepting anyone. A direct connection is off the table.
- The middleman services can pipe MFP data to us, but at the $300–$500/month floors described above — and one of the two has already marked its MFP connection as deprecated.
- **The good news:** when a user connects MyFitnessPal to Apple Health on their iPhone, MFP sends over each meal's calories and nutrients, labeled breakfast/lunch/dinner/snack. We verified this in MFP's own documentation. So an iPhone app that reads Apple Health gets us the nutrition data — meal by meal — without MyFitnessPal's permission, without a middleman, and without the user doing anything after a one-time setup.

**What we recommend.** Build the iPhone companion app. It asks the user's permission to read Apple Health (Apple's standard permission screen), then quietly forwards new nutrition entries to our system. For anyone not on iPhone — or for loading history — MyFitnessPal's own data export (a file the user downloads and uploads to us) covers the gap.

**What the coach gains:** real numbers instead of guesses. *"You're at 68g protein with dinner left — aim for 45g at your next meal."* *"Your first meal keeps landing at 10:45 — if you want a longer overnight fast, hold to 11:15 tomorrow."*

**Privacy note for Tai:** we designed this so **the names of foods never leave our system**. The coach sees protein/carbs/fat totals and meal timing — not "Chicken Caesar Salad." What someone eats can reveal sensitive things (dietary restrictions, disordered-eating patterns, cultural identity); the totals are all the coach needs.

**Cost:** none. **What we need:** Tai's sign-off on the privacy additions (drafted), and confirmation her MFP subscription is Premium (the export feature requires it — Adam's is).

---

## Strava — ready to build, the one where we pay a little

**What we found.**

- Strava still has a proper developer program — the same kind of direct connection we built for WHOOP. The patterns are so similar that roughly 70% of the WHOOP work carries over.
- Strava now charges developers **$11.99/month** (new this June). That's the entire ongoing cost.
- Strava explicitly banned middleman services last year — kicked them out by name. So "direct or nothing" isn't our preference here; it's Strava's rule.
- The Apple Health route alone isn't enough for Strava: what Strava shares with Apple Health is a thin summary. The details that make Strava worth having — mile-by-mile pace and heart rate, time in each effort zone, elevation, cycling power — only come through the direct connection.

**What we recommend.** Connect directly to Strava for the rich detail, and let the iPhone app catch what Strava misses (like workouts recorded on an Apple Watch without opening Strava). When the same run shows up in both WHOOP and Strava, we merge them: Strava's detail + WHOOP's strain score, one workout, no double-counting.

**What the coach gains:** structure, not just totals. Today the coach sees "42 minutes, average heart rate 158." With Strava it sees *which miles* were hard: *"You crossed threshold in miles 3–5 — that's 22 minutes of high-intensity work this week already. Today should be easy: 45 minutes, keep your heart rate under 145."*

**Privacy note for Tai — the important one:** Strava data includes GPS routes. **No location data ever goes to the AI. None.** No coordinates, no route maps, no segment names, no gear, no social data. Routes reveal where someone lives and trains. The coach reasons about effort; it doesn't need to know where the run happened. This commitment is written into the draft privacy language for your review.

**One watch item:** Strava has been tightening its developer rules for two years. Nothing currently prohibits what we're doing, but if that changes we'd fall back to the Apple Health route and lose the detail. We'll keep an eye on it.

**Cost:** $12/month + Adam sets up the developer account. **What we need:** that account, and Tai's sign-off on the location-data language.

---

## Lumen — on hold, through no fault of ours

**What we found.** Lumen offers developers nothing: no developer program, no way to export your data, and their partnership page is literally a broken link. We confirmed Lumen *reads* from Apple Health (steps, sleep, weight — to feed its own recommendations), but we could not confirm anywhere whether it *writes* its breath-measurement results back.

**What we recommend.** Don't spend real time on it. While building the iPhone app anyway, we'll spend ten minutes checking whether Lumen leaves its measurements in Apple Health — Adam takes a reading, we look. If the data's there, Lumen becomes a small add-on to work already done. If not, it stays on hold until Lumen opens up. Adam will also send their partnership team a short note — costs nothing, might get a reply.

---

## Protein target — designed; the next move is Tai's

**What Tai asked for** (June 28): the coach currently gives a population-level protein range (0.7–1g per pound). With more context — body-composition goal, training history, cycle phase — it should resolve to one specific number.

**What we designed.**

- **A short profile page** — about 90 seconds: current weight, what you're working toward (recomp / cut / maintain / gain), where you are in that goal, and cycle status. That's it — no long health questionnaire. Cycle phase can also come from Apple Health automatically (via the iPhone app) if the user tracks it there; we ask nothing they've already answered elsewhere.
- **A calculation layer** that combines the profile with training history (which we'll have from WHOOP + Strava) and produces one number with a confidence level. When it's confident: *"140g today."* When it's missing inputs: the coach keeps today's range and gently points to the profile page. It never fakes precision it doesn't have.
- **The actual numbers come from a table Tai authors** — protein per pound by goal and cycle phase, in a simple document she can edit directly without engineering involved. Published sports-science recommendations fill any gaps she leaves. Her judgment is the source of truth; the literature is the backstop.

The same machinery then extends to carbs, hydration, and caffeine cutoff with no redesign.

**What we need:** Tai writes the target table (the one true blocker), reviews the profile-page wording — the cycle questions especially need her voice — and signs off on a small privacy addition (goal category and cycle phase go to the AI; exact weight and dates stay with us).

---

## What we're asking each of you to do

**Tai:**
1. Review + sign the privacy additions for lab data (Function Health)
2. Review + sign the privacy additions for nutrition data (MyFitnessPal / iPhone app)
3. Review + sign the location-data commitment (Strava)
4. Author the protein-target table
5. Review the profile-page question wording
6. OK using your Function results PDF as test material (separate from your existing opt-in)

**Adam:**
1. Create the Strava developer account ($11.99/month)
2. Send the two zero-cost partnership emails (Lumen, MyFitnessPal)
3. Provide his Function results PDF as test material; get Greg's OK for his

**Together:** confirm the build order. Our suggestion: **Function Health first** (smallest effort, Tai's top ask), **then MyFitnessPal** (builds the iPhone app everything else reuses), **then Strava** (extends it). The protein work follows once MFP and Strava are flowing and Tai's table exists.

---

## Decisions already made along the way (so we don't relitigate)

- **No middleman data services.** Too expensive at our size ($3,600–$6,000/year), an extra company holding health data, and Strava bans them anyway.
- **No cross-platform app framework.** Two native apps — iPhone now, Android when we have a non-iPhone user. (Adam, July 1)
- **No scraping.** Community tools exist that log into MFP or Lumen with the user's password and pull data out. Against the services' terms, requires storing users' passwords, breaks without warning. Never.
- **Food names, GPS routes, and exact body weight never go to the AI.** Totals, timing, zones, and categories do. This is the running privacy posture across all three builds.
- **Lab values require user confirmation before the coach may use them.** Automated extraction is good but not infallible; the user is the final check.

---

## If you want the full detail

Each recommendation above has a complete engineering plan and a research document with every source cited, in the repository under `docs/`:

- Build plans: `session-function-health-integration.md`, `session-myfitnesspal-integration.md`, `session-strava-integration.md`
- Research (the "why," with citations): `docs/seeds/*-research.md` for all five topics
