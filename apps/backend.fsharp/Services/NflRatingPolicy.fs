namespace backend.Services

open System
open backend.Models

module NflRatingPolicy =
    let refreshInterval = PlayoffRatingPolicy.refreshInterval
    let maxAge = PlayoffRatingPolicy.maxAge
    let seasonAt (date: DateTime) = PlayoffRatingPolicy.seasonAt "nfl" date
    let gameSeason (game: Game) = PlayoffRatingPolicy.gameSeason game

    let isVerified (season: int) (rating: TeamRating) =
        rating.Season = season && rating.SourceSeason = Nullable season

    let playoffProbability season now ratings =
        PlayoffRatingPolicy.playoffProbability "nfl" season now ratings
