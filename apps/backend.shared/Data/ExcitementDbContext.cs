using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Data;

public class ExcitementDbContext : DbContext
{
    public ExcitementDbContext(DbContextOptions<ExcitementDbContext> options) : base(options) { }

    public DbSet<Game> Games => Set<Game>();
    public DbSet<ProHypeSnapshot> ProHypeSnapshots => Set<ProHypeSnapshot>();
    public DbSet<ProHypeEvidenceChange> ProHypeEvidenceChanges => Set<ProHypeEvidenceChange>();
    public DbSet<ProHypeRevision> ProHypeRevisions => Set<ProHypeRevision>();
    public DbSet<StartHype> StartHypes => Set<StartHype>();
    public DbSet<GameHighlights> GameHighlights => Set<GameHighlights>();
    public DbSet<HighlightsOperationState> HighlightsOperations => Set<HighlightsOperationState>();
    public DbSet<CollegeTeam> CollegeTeams => Set<CollegeTeam>();
    public DbSet<CollegeImport> CollegeImports => Set<CollegeImport>();
    public DbSet<OddsSnapshot> OddsSnapshots => Set<OddsSnapshot>();
    public DbSet<PregameOdds> PregameOdds => Set<PregameOdds>();
    public DbSet<TeamRating> TeamRatings => Set<TeamRating>();
    public DbSet<MoneyPuckRefreshState> MoneyPuckRefreshOperations => Set<MoneyPuckRefreshState>();
    public DbSet<NhlGuruRefreshState> NhlGuruRefreshOperations => Set<NhlGuruRefreshState>();
    public DbSet<TeamStanding> TeamStandings => Set<TeamStanding>();
    public DbSet<F1Race> F1Races => Set<F1Race>();
    public DbSet<F1Driver> F1Drivers => Set<F1Driver>();
    public DbSet<F1ChampionshipStanding> F1ChampionshipStandings => Set<F1ChampionshipStanding>();
    public DbSet<F1RaceSnapshot> F1RaceSnapshots => Set<F1RaceSnapshot>();
    public DbSet<NascarRace> NascarRaces => Set<NascarRace>();
    public DbSet<NascarDriver> NascarDrivers => Set<NascarDriver>();
    public DbSet<NascarRaceResult> NascarRaceResults => Set<NascarRaceResult>();
    public DbSet<NascarRaceSnapshot> NascarRaceSnapshots => Set<NascarRaceSnapshot>();
    public DbSet<IndyCarRace> IndyCarRaces => Set<IndyCarRace>();
    public DbSet<IndyCarDriver> IndyCarDrivers => Set<IndyCarDriver>();
    public DbSet<IndyCarRaceResult> IndyCarRaceResults => Set<IndyCarRaceResult>();
    public DbSet<IndyCarRaceSnapshot> IndyCarRaceSnapshots => Set<IndyCarRaceSnapshot>();

    public async Task LockProHypeFenceAsync(CancellationToken ct) => await Database.ExecuteSqlRawAsync(
        "LOCK TABLE games IN ROW EXCLUSIVE MODE; SELECT id FROM pro_hype_revision WHERE id = 1 FOR UPDATE", ct);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProHypeSnapshot>(entity =>
        {
            entity.ToTable("pro_hype_snapshots");
            entity.HasKey(e => e.GameId);
            entity.Property(e => e.GameId).HasColumnName("game_id");
            entity.Property(e => e.LastEvaluationTicks).HasColumnName("last_evaluation_ticks");
            entity.Property(e => e.EvidenceRevision).HasColumnName("evidence_revision");
            entity.Property(e => e.ConfirmedStarted).HasColumnName("confirmed_started");
            entity.Property(e => e.SnapshotJson).HasColumnName("snapshot_json");
            entity.Property(e => e.RevokedEvidenceJson).HasColumnName("revoked_evidence_json");
            entity.HasOne<Game>().WithOne().HasForeignKey<ProHypeSnapshot>(e => e.GameId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StartHype>(entity =>
        {
            entity.ToTable("start_hype");
            entity.HasKey(e => e.GameId);
            entity.Property(e => e.GameId).HasColumnName("game_id");
            entity.Property(e => e.Total).HasColumnName("total");
            entity.Property(e => e.QualitySource).HasColumnName("quality_source");
            entity.Property(e => e.MoneyPuckTeams).HasColumnName("moneypuck_teams");
            entity.HasOne<Game>().WithOne().HasForeignKey<StartHype>(e => e.GameId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProHypeRevision>(entity =>
        {
            entity.ToTable("pro_hype_revision");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.Revision).HasColumnName("revision");
            entity.HasData(new ProHypeRevision());
        });
        modelBuilder.Entity<ProHypeEvidenceChange>(entity =>
        {
            entity.ToTable("pro_hype_evidence_changes");
            entity.HasKey(e => e.Revision);
            entity.Property(e => e.Revision).HasColumnName("revision").ValueGeneratedNever();
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.BeforeSubject).HasColumnName("before_subject");
            entity.Property(e => e.AfterSubject).HasColumnName("after_subject");
            entity.HasIndex(e => new { e.BeforeSubject, e.Revision }).HasDatabaseName("ix_pro_hype_changes_before_subject_revision");
            entity.HasIndex(e => new { e.AfterSubject, e.Revision }).HasDatabaseName("ix_pro_hype_changes_after_subject_revision");
            entity.Property(e => e.BeforeJson).HasColumnName("before_json");
            entity.Property(e => e.AfterJson).HasColumnName("after_json");
        });

        modelBuilder.Entity<MoneyPuckRefreshState>(entity =>
        {
            entity.ToTable("moneypuck_refresh_state", table =>
            {
                table.HasCheckConstraint("ck_moneypuck_refresh_singleton", "id = 1");
                table.HasCheckConstraint("ck_moneypuck_refresh_ticks", "next_attempt_at_ticks BETWEEN 1 AND 3155378975999999999");
                table.HasCheckConstraint("ck_moneypuck_denied_until_ticks", "denied_until_ticks IS NULL OR denied_until_ticks BETWEEN 1 AND 3155378975999999999");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.NextAttemptAtTicks).HasColumnName("next_attempt_at_ticks");
            entity.Property(e => e.Active).HasColumnName("active");
            entity.Property(e => e.Denied).HasColumnName("denied");
            entity.Property(e => e.DeniedUntilTicks).HasColumnName("denied_until_ticks");
        });

        modelBuilder.Entity<NhlGuruRefreshState>(entity =>
        {
            entity.ToTable("nhl_guru_refresh_state", table =>
            {
                table.HasCheckConstraint("ck_nhl_guru_singleton", "id = 1");
                table.HasCheckConstraint("ck_nhl_guru_ticks", "next_attempt_at_ticks BETWEEN 1 AND 3155378975999999999");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.NextAttemptAtTicks).HasColumnName("next_attempt_at_ticks");
        });

        modelBuilder.Entity<CollegeTeam>(entity =>
        {
            entity.ToTable("college_teams");
            entity.HasKey(e => new { e.Season, e.EspnId });
            entity.Property(e => e.Season).HasColumnName("season");
            entity.Property(e => e.EspnId).HasColumnName("espn_id");
            entity.Property(e => e.Abbreviation).HasColumnName("abbreviation");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Fpi).HasColumnName("fpi");
            entity.Property(e => e.FpiUpdatedAt).HasColumnName("fpi_updated_at");
            entity.Property(e => e.ApRank).HasColumnName("ap_rank");
            entity.Property(e => e.ApUpdatedAt).HasColumnName("ap_updated_at");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
        });
        modelBuilder.Entity<CollegeImport>(entity =>
        {
            entity.ToTable("college_imports");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Season).HasColumnName("season");
            entity.Property(e => e.FirstImportAt).HasColumnName("first_import_at");
            entity.Property(e => e.WindowStart).HasColumnName("window_start");
        });

        modelBuilder.Entity<Game>(entity =>
        {
            entity.ToTable("games");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Sport).HasColumnName("sport");
            entity.Property(e => e.HomeTeam).HasColumnName("home_team");
            entity.Property(e => e.AwayTeam).HasColumnName("away_team");
            entity.Property(e => e.HomeTeamAbbreviation).HasColumnName("home_team_abbr");
            entity.Property(e => e.AwayTeamAbbreviation).HasColumnName("away_team_abbr");
            entity.Property(e => e.ScheduledStart).HasColumnName("scheduled_start");
            entity.Property(e => e.ActualStart).HasColumnName("actual_start");
            entity.Property(e => e.EndTime).HasColumnName("end_time");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.HomeScore).HasColumnName("home_score");
            entity.Property(e => e.AwayScore).HasColumnName("away_score");
            entity.Property(e => e.IsOvertime).HasColumnName("is_overtime");
            entity.Property(e => e.SeasonYear).HasColumnName("season_year");
            entity.Property(e => e.SeasonType).HasColumnName("season_type");
            entity.Property(e => e.WeekNumber).HasColumnName("week_number");
            entity.Property(e => e.WeekLabel).HasColumnName("week_label");
            entity.Property(e => e.HomeTeamId).HasColumnName("home_team_id");
            entity.Property(e => e.AwayTeamId).HasColumnName("away_team_id");
            entity.Property(e => e.HomeColor).HasColumnName("home_color");
            entity.Property(e => e.AwayColor).HasColumnName("away_color");
            entity.Property(e => e.Period).HasColumnName("period");
            entity.Property(e => e.GameClock).HasColumnName("game_clock");
            entity.Property(e => e.StatusDetail).HasColumnName("status_detail");
            entity.Property(e => e.ScoreboardUpdatedAt).HasColumnName("scoreboard_updated_at");
            entity.Property(e => e.HomeMarketTicker).HasColumnName("home_market_ticker");
            entity.Property(e => e.AwayMarketTicker).HasColumnName("away_market_ticker");
            entity.Property(e => e.CollegeCompetition).HasColumnName("college_competition");
            entity.Property(e => e.CompetitionEvidence).HasColumnName("competition_evidence");
            entity.Property(e => e.IsHistoricalImport).HasColumnName("is_historical_import").HasDefaultValue(false);
            entity.Property(e => e.HomeFpi).HasColumnName("home_fpi");
            entity.Property(e => e.AwayFpi).HasColumnName("away_fpi");
            entity.Property(e => e.HomeApRank).HasColumnName("home_ap_rank");
            entity.Property(e => e.AwayApRank).HasColumnName("away_ap_rank");
            entity.Property(e => e.RatingsObservedAt).HasColumnName("ratings_observed_at");
            entity.Property(e => e.MarketObservedAt).HasColumnName("market_observed_at");
            entity.Property(e => e.SeriesObservationJson).HasColumnName("series_observation_json");
            entity.Property(e => e.ProEvidenceJson).HasColumnName("pro_evidence_json");
            entity.Property(e => e.EspnCompletionEvidenceJson).HasColumnName("espn_completion_evidence_json");
            entity.Property(e => e.EspnCompletionObservedAt).HasColumnName("espn_completion_observed_at");
            entity.Property(e => e.CompletionConfirmedAt).HasColumnName("completion_confirmed_at");
            entity.Property(e => e.HighlightsDiscoveryExpiresAt).HasColumnName("highlights_discovery_expires_at");
            entity.Property(e => e.MlbGamePk).HasColumnName("mlb_game_pk");
            entity.Property(e => e.MlbOfficialDate).HasColumnName("mlb_official_date");
            entity.Property(e => e.HighlightsIdentityEvidenceJson).HasColumnName("highlights_identity_evidence_json");
            entity.Property(e => e.BroadcastNetworks).HasColumnName("broadcast_networks");
            entity.HasIndex(e => e.MlbGamePk).IsUnique();

            entity.HasIndex(e => e.Sport);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledStart);
        });

        modelBuilder.Entity<GameHighlights>(entity =>
        {
            entity.ToTable("game_highlights", table =>
            {
                table.HasCheckConstraint("ck_highlights_ownership", "ownership IS NULL OR ownership IN ('Automatic', 'Manual')");
                table.HasCheckConstraint("ck_highlights_availability", "availability IN ('Unknown', 'Playable', 'Unavailable')");
                table.HasCheckConstraint("ck_highlights_selection_owner", "selected_video_id IS NULL OR ownership IS NOT NULL");
            });
            entity.HasKey(e => e.GameId);
            entity.Property(e => e.GameId).HasColumnName("game_id");
            entity.Property(e => e.SelectedVideoId).HasColumnName("selected_video_id");
            entity.Property(e => e.Ownership).HasColumnName("ownership").HasConversion<string>();
            entity.Property(e => e.Suppressed).HasColumnName("suppressed").HasDefaultValue(false);
            entity.Property(e => e.SuppressedAt).HasColumnName("suppressed_at");
            entity.Property(e => e.SelectedAt).HasColumnName("selected_at");
            entity.Property(e => e.Availability).HasColumnName("availability").HasConversion<string>().HasDefaultValue(HighlightsAvailability.Unknown);
            entity.Property(e => e.UnavailableReason).HasColumnName("unavailable_reason");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.ChannelId).HasColumnName("channel_id");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.MadeForKids).HasColumnName("made_for_kids");
            entity.Property(e => e.AllowedRegions).HasColumnName("allowed_regions");
            entity.Property(e => e.BlockedRegions).HasColumnName("blocked_regions");
            entity.Property(e => e.SelectionEvidenceJson).HasColumnName("selection_evidence_json");
            entity.Property(e => e.ValidatedAt).HasColumnName("validated_at");
            entity.Property(e => e.ProviderDataRefreshedAt).HasColumnName("provider_data_refreshed_at");
            entity.Property(e => e.ProviderDataExpiresAt).HasColumnName("provider_data_expires_at");
            if (Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                entity.Property(e => e.Version).IsRowVersion().HasColumnName("xmin");
            else
                entity.Ignore(e => e.Version);
            entity.HasOne<Game>().WithOne().HasForeignKey<GameHighlights>(e => e.GameId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HighlightsOperationState>(entity =>
        {
            entity.ToTable("highlights_operations", table =>
            {
                table.HasCheckConstraint("ck_highlights_operations_singleton", "id = 1");
                table.HasCheckConstraint("ck_highlights_operations_quota", "quota_units_used BETWEEN 0 AND 1000 AND consecutive_failures >= 0");
                table.HasCheckConstraint("ck_highlights_worker_reservation", "worker_reserved_units BETWEEN 0 AND 10");
                table.HasCheckConstraint("ck_highlights_catchup_pages", "catchup_pages_scanned BETWEEN 0 AND 16");
                if (Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                    table.HasCheckConstraint("ck_highlights_operations_window", "(launch_boundary_at IS NULL AND catchup_window_start IS NULL) OR (launch_boundary_at IS NOT NULL AND catchup_window_start IS NOT NULL AND catchup_window_start = launch_boundary_at - INTERVAL '7 days')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(e => e.LaunchBoundaryAt).HasColumnName("launch_boundary_at");
            entity.Property(e => e.CatchupWindowStart).HasColumnName("catchup_window_start");
            entity.Property(e => e.CatchupCursorGameId).HasColumnName("catchup_cursor_game_id");
            entity.Property(e => e.CatchupRemainingGameIds).HasColumnName("catchup_remaining_game_ids");
            entity.Property(e => e.CatchupPagesScanned).HasColumnName("catchup_pages_scanned").HasDefaultValue(0);
            entity.Property(e => e.CatchupCompletedAt).HasColumnName("catchup_completed_at");
            entity.Property(e => e.UploadsPlaylistId).HasColumnName("uploads_playlist_id");
            entity.Property(e => e.UploadsPageToken).HasColumnName("uploads_page_token");
            entity.Property(e => e.ProviderDataRefreshedAt).HasColumnName("provider_data_refreshed_at");
            entity.Property(e => e.ProviderDataExpiresAt).HasColumnName("provider_data_expires_at");
            entity.Property(e => e.LastPollAttemptAt).HasColumnName("last_poll_attempt_at");
            entity.Property(e => e.NextPollAt).HasColumnName("next_poll_at");
            entity.Property(e => e.NextValidationAttemptAt).HasColumnName("next_validation_attempt_at");
            entity.Property(e => e.BackoffUntil).HasColumnName("backoff_until");
            entity.Property(e => e.QuotaDay).HasColumnName("quota_day");
            entity.Property(e => e.QuotaUnitsUsed).HasColumnName("quota_units_used").HasDefaultValue(0);
            entity.Property(e => e.WorkerReservedUnits).HasColumnName("worker_reserved_units").HasDefaultValue(0);
            entity.Property(e => e.ConsecutiveFailures).HasColumnName("consecutive_failures").HasDefaultValue(0);
            entity.Property(e => e.LastReason).HasColumnName("last_reason");
            entity.Property(e => e.ProviderConfigurationRejected).HasColumnName("provider_configuration_rejected").HasDefaultValue(false);
            if (Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                entity.Property(e => e.Version).IsRowVersion().HasColumnName("xmin");
            else
                entity.Ignore(e => e.Version);
            entity.HasData(new HighlightsOperationState());
        });

        modelBuilder.Entity<OddsSnapshot>(entity =>
        {
            entity.ToTable("odds_snapshots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.GameId).HasColumnName("game_id");
            entity.Property(e => e.Timestamp).HasColumnName("timestamp");
            entity.Property(e => e.HomeMoneyline).HasColumnName("home_moneyline");
            entity.Property(e => e.AwayMoneyline).HasColumnName("away_moneyline");
            entity.Property(e => e.DrawMoneyline).HasColumnName("draw_moneyline");
            entity.Property(e => e.Spread).HasColumnName("spread");
            entity.Property(e => e.OverUnder).HasColumnName("over_under");
            entity.Property(e => e.HomeWinProbability).HasColumnName("home_win_probability");
            entity.Property(e => e.AwayWinProbability).HasColumnName("away_win_probability");
            entity.Property(e => e.DrawWinProbability).HasColumnName("draw_win_probability");
            entity.Property(e => e.GameClock).HasColumnName("game_clock");
            entity.Property(e => e.Period).HasColumnName("period");
            entity.Property(e => e.IsHistorical).HasColumnName("is_historical").HasDefaultValue(false);
            entity.Property(e => e.HomeScore).HasColumnName("home_score");
            entity.Property(e => e.AwayScore).HasColumnName("away_score");
            entity.Property(e => e.Source).HasColumnName("source");

            entity.HasOne(e => e.Game)
                .WithMany(g => g.OddsSnapshots)
                .HasForeignKey(e => e.GameId);

            entity.HasIndex(e => new { e.GameId, e.Timestamp });
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<PregameOdds>(entity =>
        {
            entity.ToTable("pregame_odds");
            entity.HasKey(e => e.GameId);
            entity.Property(e => e.GameId).HasColumnName("game_id");
            entity.Property(e => e.SourceSeason).HasColumnName("source_season");
            entity.Property(e => e.ApplicableStart).HasColumnName("applicable_start");
            entity.Property(e => e.HomeTeamAbbreviation).HasColumnName("home_team_abbreviation");
            entity.Property(e => e.AwayTeamAbbreviation).HasColumnName("away_team_abbreviation");
            entity.Property(e => e.HomeMoneyline).HasColumnName("home_moneyline");
            entity.Property(e => e.AwayMoneyline).HasColumnName("away_moneyline");
            entity.Property(e => e.DrawMoneyline).HasColumnName("draw_moneyline");
            entity.Property(e => e.Spread).HasColumnName("spread");
            entity.Property(e => e.OverUnder).HasColumnName("over_under");
            entity.Property(e => e.HomeWinProbability).HasColumnName("home_win_probability");
            entity.Property(e => e.AwayWinProbability).HasColumnName("away_win_probability");
            entity.Property(e => e.DrawWinProbability).HasColumnName("draw_win_probability");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");
            entity.Property(e => e.Source).HasColumnName("source");

            entity.HasOne(e => e.Game)
                .WithOne(g => g.PregameOdds)
                .HasForeignKey<PregameOdds>(e => e.GameId);

            entity.HasIndex(e => e.FetchedAt);
        });

        modelBuilder.Entity<TeamRating>(entity =>
        {
            entity.ToTable("team_ratings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Sport).HasColumnName("sport");
            entity.Property(e => e.TeamAbbreviation).HasColumnName("team_abbreviation");
            entity.Property(e => e.TeamName).HasColumnName("team_name");
            entity.Property(e => e.EloRating).HasColumnName("elo_rating");
            entity.Property(e => e.BpiRating).HasColumnName("bpi_rating");
            entity.Property(e => e.FpiRating).HasColumnName("fpi_rating");
            entity.Property(e => e.PowerRank).HasColumnName("power_rank");
            entity.Property(e => e.PlayoffProbability).HasColumnName("playoff_probability");
            entity.Property(e => e.PlayoffProbabilityIsFinal).HasColumnName("playoff_probability_is_final").HasDefaultValue(false);
            entity.Property(e => e.PlayoffEvidenceJson).HasColumnName("playoff_evidence_json");
            entity.Property(e => e.ChampionshipProbability).HasColumnName("championship_probability");
            entity.Property(e => e.GameQuality).HasColumnName("game_quality");
            entity.Property(e => e.GameImportance).HasColumnName("game_importance");
            entity.Property(e => e.SourceSeason).HasColumnName("source_season");
            entity.Property(e => e.RankPublishedAt).HasColumnName("rank_published_at");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.Season).HasColumnName("season");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");

            entity.HasIndex(e => e.Sport);
            entity.HasIndex(e => new { e.Sport, e.TeamAbbreviation, e.Season });
            entity.HasIndex(e => e.Source);
            entity.HasIndex(e => e.FetchedAt);
        });

        modelBuilder.Entity<TeamStanding>(entity =>
        {
            entity.ToTable("team_standings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Sport).HasColumnName("sport");
            entity.Property(e => e.TeamAbbreviation).HasColumnName("team_abbreviation");
            entity.Property(e => e.TeamName).HasColumnName("team_name");
            entity.Property(e => e.Division).HasColumnName("division");
            entity.Property(e => e.Conference).HasColumnName("conference");
            entity.Property(e => e.DivisionRank).HasColumnName("division_rank");
            entity.Property(e => e.ConferenceRank).HasColumnName("conference_rank");
            entity.Property(e => e.NationalRank).HasColumnName("national_rank");
            entity.Property(e => e.RankingPoll).HasColumnName("ranking_poll");
            entity.Property(e => e.Wins).HasColumnName("wins");
            entity.Property(e => e.Losses).HasColumnName("losses");
            entity.Property(e => e.Ties).HasColumnName("ties");
            entity.Ignore(e => e.GamesPlayed);  
            entity.Property(e => e.Season).HasColumnName("season");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at");

            entity.HasIndex(e => e.Sport);
            entity.HasIndex(e => new { e.Sport, e.TeamAbbreviation, e.Season });
            entity.HasIndex(e => e.FetchedAt);
        });

        modelBuilder.Entity<F1Race>(entity =>
        {
            entity.ToTable("f1_races");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.EspnEventId).HasColumnName("espn_event_id");
            entity.Property(e => e.MeetingName).HasColumnName("meeting_name");
            entity.Property(e => e.CircuitName).HasColumnName("circuit_name");
            entity.Property(e => e.CircuitShortName).HasColumnName("circuit_short_name");
            entity.Property(e => e.Country).HasColumnName("country");
            entity.Property(e => e.Year).HasColumnName("year");
            entity.Property(e => e.RoundNumber).HasColumnName("round_number");
            entity.Property(e => e.ScheduledStart).HasColumnName("scheduled_start");
            entity.Property(e => e.ActualStart).HasColumnName("actual_start");
            entity.Property(e => e.EndTime).HasColumnName("end_time");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.TotalLaps).HasColumnName("total_laps");
            entity.Property(e => e.CompletedLaps).HasColumnName("completed_laps");
            entity.Property(e => e.ExcitementScore).HasColumnName("excitement_score");

            entity.HasIndex(e => e.Year);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledStart);
            entity.HasIndex(e => e.EspnEventId).IsUnique();
            entity.HasIndex(e => new { e.Year, e.RoundNumber }).IsUnique();
        });

        modelBuilder.Entity<F1RaceSnapshot>(entity =>
        {
            entity.ToTable("f1_race_snapshots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.RaceId).HasColumnName("race_id");
            entity.Property(e => e.EspnAthleteId).HasColumnName("espn_athlete_id");
            entity.Property(e => e.Timestamp).HasColumnName("timestamp");
            entity.Property(e => e.Position).HasColumnName("position");
            entity.Property(e => e.WinProbability).HasColumnName("win_probability");
            entity.Property(e => e.CompletedLaps).HasColumnName("completed_laps");
            entity.Property(e => e.Source).HasColumnName("source");

            entity.HasIndex(e => new { e.RaceId, e.Timestamp });
            entity.HasIndex(e => new { e.RaceId, e.EspnAthleteId, e.Timestamp });
        });

        modelBuilder.Entity<F1Driver>(entity =>
        {
            entity.ToTable("f1_drivers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.DriverNumber).HasColumnName("driver_number");
            entity.Property(e => e.FullName).HasColumnName("full_name");
            entity.Property(e => e.Abbreviation).HasColumnName("abbreviation");
            entity.Property(e => e.TeamName).HasColumnName("team_name");
            entity.Property(e => e.TeamColor).HasColumnName("team_color");
            entity.Property(e => e.HeadshotUrl).HasColumnName("headshot_url");
            entity.Property(e => e.CountryCode).HasColumnName("country_code");
            entity.Property(e => e.Season).HasColumnName("season");
            entity.Property(e => e.QualifyingPosition).HasColumnName("qualifying_position");

            entity.HasIndex(e => e.Season);
            entity.HasIndex(e => new { e.DriverNumber, e.Season }).IsUnique();
        });

        modelBuilder.Entity<F1ChampionshipStanding>(entity =>
        {
            entity.ToTable("f1_championship_standings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.Season).HasColumnName("season");
            entity.Property(e => e.Type).HasColumnName("type");
            entity.Property(e => e.EntityName).HasColumnName("entity_name");
            entity.Property(e => e.Abbreviation).HasColumnName("abbreviation");
            entity.Property(e => e.Position).HasColumnName("position");
            entity.Property(e => e.Points).HasColumnName("points");
            entity.Property(e => e.Wins).HasColumnName("wins");
            entity.Property(e => e.AfterRound).HasColumnName("after_round");

            entity.HasIndex(e => new { e.Season, e.Type });
            entity.HasIndex(e => new { e.Season, e.Type, e.AfterRound });
        });

        modelBuilder.Entity<NascarRace>(entity =>
        {
            entity.ToTable("nascar_races");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.RaceName).HasColumnName("race_name");
            entity.Property(e => e.Year).HasColumnName("year");
            entity.Property(e => e.RaceNumber).HasColumnName("race_number");
            entity.Property(e => e.ScheduledStart).HasColumnName("scheduled_start");
            entity.Property(e => e.ActualStart).HasColumnName("actual_start");
            entity.Property(e => e.EndTime).HasColumnName("end_time");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.TotalLaps).HasColumnName("total_laps");
            entity.Property(e => e.CompletedLaps).HasColumnName("completed_laps");
            entity.Property(e => e.ExcitementScore).HasColumnName("excitement_score");
            entity.Property(e => e.FlagState).HasColumnName("flag_state");

            entity.HasIndex(e => e.Year);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledStart);
            entity.HasIndex(e => new { e.Year, e.RaceNumber }).IsUnique();
        });

        modelBuilder.Entity<NascarDriver>(entity =>
        {
            entity.ToTable("nascar_drivers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.EspnAthleteId).HasColumnName("espn_athlete_id");
            entity.Property(e => e.FullName).HasColumnName("full_name");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.ShortName).HasColumnName("short_name");
            entity.Property(e => e.CountryFlagHref).HasColumnName("country_flag_href");
            entity.Property(e => e.CarNumber).HasColumnName("car_number");
            entity.Property(e => e.Manufacturer).HasColumnName("manufacturer");
            entity.Property(e => e.TeamName).HasColumnName("team_name");
            entity.Property(e => e.Season).HasColumnName("season");

            entity.HasIndex(e => e.Season);
            entity.HasIndex(e => new { e.EspnAthleteId, e.Season }).IsUnique();
        });

        modelBuilder.Entity<NascarRaceResult>(entity =>
        {
            entity.ToTable("nascar_race_results");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.RaceId).HasColumnName("race_id");
            entity.Property(e => e.EspnAthleteId).HasColumnName("espn_athlete_id");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.ShortName).HasColumnName("short_name");
            entity.Property(e => e.FinishPosition).HasColumnName("finish_position");
            entity.Property(e => e.Winner).HasColumnName("winner");
            entity.Property(e => e.CountryFlagHref).HasColumnName("country_flag_href");
            entity.Property(e => e.CarNumber).HasColumnName("car_number");
            entity.Property(e => e.StartingPosition).HasColumnName("starting_position");

            entity.HasIndex(e => e.RaceId);
            entity.HasIndex(e => new { e.RaceId, e.EspnAthleteId }).IsUnique();
        });

        modelBuilder.Entity<NascarRaceSnapshot>(entity =>
        {
            entity.ToTable("nascar_race_snapshots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.RaceId).HasColumnName("race_id");
            entity.Property(e => e.EspnAthleteId).HasColumnName("espn_athlete_id");
            entity.Property(e => e.Timestamp).HasColumnName("timestamp");
            entity.Property(e => e.Position).HasColumnName("position");
            entity.Property(e => e.WinProbability).HasColumnName("win_probability");
            entity.Property(e => e.CompletedLaps).HasColumnName("completed_laps");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.IsOnTrack).HasColumnName("is_on_track");

            entity.HasIndex(e => new { e.RaceId, e.Timestamp });
            entity.HasIndex(e => new { e.RaceId, e.EspnAthleteId, e.Timestamp });
        });

        modelBuilder.Entity<IndyCarRace>(entity =>
        {
            entity.ToTable("indycar_races");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.EspnEventId).HasColumnName("espn_event_id");
            entity.Property(e => e.RaceName).HasColumnName("race_name");
            entity.Property(e => e.Year).HasColumnName("year");
            entity.Property(e => e.RaceNumber).HasColumnName("race_number");
            entity.Property(e => e.ScheduledStart).HasColumnName("scheduled_start");
            entity.Property(e => e.ActualStart).HasColumnName("actual_start");
            entity.Property(e => e.EndTime).HasColumnName("end_time");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.TotalLaps).HasColumnName("total_laps");
            entity.Property(e => e.CompletedLaps).HasColumnName("completed_laps");
            entity.Property(e => e.ExcitementScore).HasColumnName("excitement_score");

            entity.HasIndex(e => e.Year);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ScheduledStart);
            entity.HasIndex(e => e.EspnEventId).IsUnique();
            entity.HasIndex(e => new { e.Year, e.RaceNumber }).IsUnique();
        });

        modelBuilder.Entity<IndyCarDriver>(entity =>
        {
            entity.ToTable("indycar_drivers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.EspnAthleteId).HasColumnName("espn_athlete_id");
            entity.Property(e => e.FullName).HasColumnName("full_name");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.ShortName).HasColumnName("short_name");
            entity.Property(e => e.CountryFlagHref).HasColumnName("country_flag_href");
            entity.Property(e => e.CarNumber).HasColumnName("car_number");
            entity.Property(e => e.TeamName).HasColumnName("team_name");
            entity.Property(e => e.Season).HasColumnName("season");

            entity.HasIndex(e => e.Season);
            entity.HasIndex(e => new { e.EspnAthleteId, e.Season }).IsUnique();
        });

        modelBuilder.Entity<IndyCarRaceResult>(entity =>
        {
            entity.ToTable("indycar_race_results");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.RaceId).HasColumnName("race_id");
            entity.Property(e => e.EspnAthleteId).HasColumnName("espn_athlete_id");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.ShortName).HasColumnName("short_name");
            entity.Property(e => e.FinishPosition).HasColumnName("finish_position");
            entity.Property(e => e.Winner).HasColumnName("winner");
            entity.Property(e => e.CountryFlagHref).HasColumnName("country_flag_href");
            entity.Property(e => e.CarNumber).HasColumnName("car_number");
            entity.Property(e => e.StartingPosition).HasColumnName("starting_position");

            entity.HasIndex(e => e.RaceId);
            entity.HasIndex(e => new { e.RaceId, e.EspnAthleteId }).IsUnique();
        });

        modelBuilder.Entity<IndyCarRaceSnapshot>(entity =>
        {
            entity.ToTable("indycar_race_snapshots");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.RaceId).HasColumnName("race_id");
            entity.Property(e => e.EspnAthleteId).HasColumnName("espn_athlete_id");
            entity.Property(e => e.Timestamp).HasColumnName("timestamp");
            entity.Property(e => e.Position).HasColumnName("position");
            entity.Property(e => e.WinProbability).HasColumnName("win_probability");
            entity.Property(e => e.CompletedLaps).HasColumnName("completed_laps");
            entity.Property(e => e.Source).HasColumnName("source");

            entity.HasIndex(e => new { e.RaceId, e.Timestamp });
            entity.HasIndex(e => new { e.RaceId, e.EspnAthleteId, e.Timestamp });
        });
    }
}
