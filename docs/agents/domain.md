# Domain Docs

## Layout

This repo uses a single domain context:

- `GLOSSARY.md`: shared vocabulary at the repo root.
- `docs/adr/`: architectural decisions.

## Before exploring

Read `GLOSSARY.md` and ADRs relevant to the area being changed.

If these files or directories are absent, proceed silently.
The domain-modeling skill creates them lazily when terms or decisions
are resolved; do not scaffold empty domain documents upfront.

## Use the glossary's vocabulary

Use defined terms in issue titles, proposals, hypotheses, and tests.
Respect explicitly avoided synonyms.

If a needed concept is missing, reconsider the terminology or note the
gap for domain-modeling.

## Flag ADR conflicts

Explicitly identify any proposal that contradicts an existing ADR,
including the ADR reference and why the decision merits reconsideration.
