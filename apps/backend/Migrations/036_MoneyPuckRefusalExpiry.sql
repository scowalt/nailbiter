BEGIN;

ALTER TABLE moneypuck_refresh_state
    ADD COLUMN IF NOT EXISTS denied_until_ticks BIGINT
    CONSTRAINT ck_moneypuck_denied_until_ticks
    CHECK (denied_until_ticks IS NULL OR denied_until_ticks BETWEEN 1 AND 3155378975999999999);

UPDATE moneypuck_refresh_state
SET denied_until_ticks = (EXTRACT(EPOCH FROM clock_timestamp()) * 10000000)::BIGINT + 621355968000000000 + 864000000000
WHERE denied AND denied_until_ticks IS NULL;

COMMIT;
