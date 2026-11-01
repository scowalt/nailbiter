
ALTER TABLE games ALTER COLUMN home_team_abbr TYPE VARCHAR(20);
ALTER TABLE games ALTER COLUMN away_team_abbr TYPE VARCHAR(20);

ALTER TABLE team_ratings ALTER COLUMN team_abbreviation TYPE VARCHAR(20);

ALTER TABLE team_standings ALTER COLUMN team_abbreviation TYPE VARCHAR(20);
