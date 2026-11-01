namespace backend.Models;

public sealed class PlayoffRatingsOptions
{
    public bool MoneyPuckSelectionEnabled { get; set; } = false;
    public bool MoneyPuckRefreshEnabled { get; set; } = false;
    public int[] MoneyPuckApprovedSeasons { get; set; } = [];

    private bool MoneyPuckSeasonApproved(string sport, int season) =>
        sport == "nhl" && season == 2027 && MoneyPuckApprovedSeasons is not null && MoneyPuckApprovedSeasons.Contains(season);

    public bool MoneyPuckSelectionEnabledFor(string sport, int season) =>
        MoneyPuckSelectionEnabled && MoneyPuckSeasonApproved(sport, season);
    public bool MoneyPuckRefreshEnabledFor(string sport, int season) =>
        MoneyPuckRefreshEnabled && MoneyPuckSeasonApproved(sport, season);

    public string NhlModelMode { get; set; } = "off";
    public bool NhlModelRefreshEnabled { get; set; } = false;
    public int[] ApprovedSeasons { get; set; } = [];

    public string ModeFor(string sport, int season)
    {
        if (sport == "nhl" && (MoneyPuckSelectionEnabled || MoneyPuckRefreshEnabled)) return "off";
        if (NhlModelMode is not ("off" or "shadow" or "fallback"))
            throw new ArgumentException("NhlModelMode must be off, shadow or fallback");
        return sport == "nhl" && ApprovedSeasons is not null && ApprovedSeasons.Contains(season)
            ? NhlModelMode : "off";
    }

    public bool RefreshEnabledFor(string sport, int season) =>
        sport == "nhl" && NhlModelRefreshEnabled && ModeFor(sport, season) == "fallback";
}
