# Seeds

Focused briefs for work that hasn't started yet. Each seed is a **research + spec starting point**, not a decided plan. The intent is: hand a seed to a research pass (agent or session), let it produce an integration spec that answers the seed's questions, iterate until the spec is testable, then promote to a Track / ADR / session prompt.

Seeds live here until they're either:

- **Promoted** — the research pass produces a spec that becomes a docs/track-* or docs/session-* file and this seed gets a `→ promoted to <path>` line at the top
- **Dropped** — with a one-line reason in a `Dropped` note

New seeds should include:

1. **Why now** — the specific ask or feedback that triggered this
2. **Priority signal** — where it sits relative to other work
3. **Concrete deliverables** — what the research pass has to produce
4. **Known context** — what's already relevant in the codebase / prior ADRs so the researcher doesn't rediscover it
5. **Open questions** — the specific unknowns the research pass needs to answer

Seeds are NOT specs. They ask questions rather than answer them. A good seed makes the researcher's job clearer, not the implementer's.
