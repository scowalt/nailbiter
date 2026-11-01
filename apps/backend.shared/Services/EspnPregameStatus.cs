namespace backend.Services;

public static class EspnPregameStatus
{
    public static bool IsUnstarted(string sport, string? name, string? state, bool completed, int? period) =>
        name == "STATUS_SCHEDULED" && state == "pre" && !completed &&
        (period == 0 || (sport == "mlb" && period == 1));
}
