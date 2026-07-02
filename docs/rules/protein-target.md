# Protein target table

**Author:** Tai Palacio — this file is yours. Edit the numbers directly; no engineering involvement needed to change a value.
**Status:** DRAFT SKELETON prepared 2026-07-01. Every number below is a literature-based placeholder, **not** your authored value. Placeholders are marked `# DEFAULT`. Replace, keep, or delete each one — anything still marked `# DEFAULT` when the rules engine ships falls back to exactly that published-literature value, so the file is safe to ship half-edited.
**Last reviewed by Tai:** _not yet_

---

## How this file is used

The rules engine reads this table at startup and computes one protein number per user per day: it starts from the **goal baseline**, then applies the **cycle-phase** and **training-load** adjustments that match the user's current state. All values are **grams per pound of bodyweight per day**. The daily card shows the resulting absolute grams for the user's current weight.

If an input is missing (no goal declared, cycle unknown), the engine doesn't guess — the coach falls back to the population range (0.7–1.0 g/lb) it uses today, and nudges the user to complete their profile.

Adjustments are additive to the baseline. Example: cut baseline 1.2 + luteal 0.1 + high-volume week 0.1 = 1.4 g/lb.

---

## The table

```yaml
# All values: grams protein per pound bodyweight per day.
# "# DEFAULT" = literature placeholder, not Tai-authored. See Sources at bottom.

goals:
  cut:                       # actively losing weight in a deficit
    baseline: 1.2            # DEFAULT — Helms et al.: 1.0-1.4 g/lb for trained lifters in a deficit; midpoint-low chosen
    adjustments:
      luteal_phase: +0.10    # DEFAULT — modest bump for luteal-phase protein turnover
      high_volume_week: +0.10  # DEFAULT — week's training volume >130% of 8-week average
      taper_week: -0.10      # DEFAULT — volume <70% of 4-week baseline
      perimenopause: +0.15   # DEFAULT — increased anabolic resistance

  recomp:                    # losing fat while holding/gaining muscle at ~maintenance calories
    baseline: 1.0            # DEFAULT
    adjustments:
      luteal_phase: +0.10    # DEFAULT
      high_volume_week: +0.10  # DEFAULT
      taper_week: 0.0        # DEFAULT — no reduction; recomp keeps protein steady through tapers
      perimenopause: +0.15   # DEFAULT

  maintain:
    baseline: 0.85           # DEFAULT — mid population range
    adjustments:
      luteal_phase: +0.05    # DEFAULT
      high_volume_week: +0.10  # DEFAULT
      taper_week: 0.0        # DEFAULT
      perimenopause: +0.15   # DEFAULT

  gain:                      # deliberate muscle-gain phase in a surplus
    baseline: 1.0            # DEFAULT — surplus reduces protein leverage vs. cut
    adjustments:
      luteal_phase: +0.05    # DEFAULT
      high_volume_week: +0.10  # DEFAULT
      taper_week: -0.05      # DEFAULT
      perimenopause: +0.15   # DEFAULT

# Hard bounds — the engine never emits a number outside these, regardless of
# how adjustments stack. Safety rail, not a target.
floor: 0.6                   # DEFAULT
ceiling: 1.6                 # DEFAULT
```

---

## Definitions the engine uses (for your review, not your editing)

- **high_volume_week** — the current week's total training minutes exceed 130% of the trailing 8-week weekly average.
- **taper_week** — the current week's total training minutes are below 70% of the trailing 4-week weekly average.
- **luteal_phase** — from Apple Health cycle tracking when available and recent, else the user's own declaration on their profile page.
- **perimenopause** — user-declared on the profile page. Applied on top of the goal baseline; cycle-phase adjustments are not applied simultaneously (irregular cycles make phase unreliable).

Anything you want defined differently — say so and we change the engine, not this file.

---

## Questions for Tai while editing

1. Are additive stacking and the ±0.10-scale adjustments the right *shape*, or do you want certain combinations pinned explicitly (a small matrix instead of base + adders)?
2. Should menstrual phase (bleeding days) get its own adjustment? Currently only luteal is adjusted.
3. The floor/ceiling rail — right numbers?
4. Anything cycle-related you'd rather express as a range-tightening rather than a point value? The engine supports "±0.05 tolerance" language on any row.

---

## Sources for the DEFAULT placeholders

- ISSN Position Stand: Protein and Exercise — general training-population ranges.
- Helms, Zinn, Rowlands — systematic review of dietary protein during caloric restriction in resistance-trained, lean athletes (basis for the cut baseline).
- Luteal/menopause adjustments — directionally supported in the menstrual-cycle protein-turnover literature; magnitudes here are conservative placeholders chosen by engineering, which is exactly why they need your review.
