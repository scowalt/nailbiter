DO $$
BEGIN
    IF (SELECT character_maximum_length FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'games' AND column_name = 'status') <= 30 THEN
        ALTER TABLE games ALTER COLUMN status TYPE VARCHAR(30);
    END IF;
END $$;
