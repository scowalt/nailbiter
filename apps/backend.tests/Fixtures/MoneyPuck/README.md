# Synthetic ranking input

The CSV and publication marker are independently constructed, not MoneyPuck responses. The complete 32-team alias set and selected row positions test parser and ranking behavior. CHI deliberately precedes SEA despite a lower synthetic forecast value, proving that rank follows row order rather than forecast sorting. Extra goalkeeper columns contain invented sentinel names for quoted-field tests.

The publication clock is an authored timezone/season/expiry control. It is not a claim about MoneyPuck's publication history. Regenerate through `scripts/test-fixtures/generate.py`; see [the central fixture contract](../../../../tests/fixtures/README.md). Existing live-source approval/rate restrictions are unchanged by these tests.
