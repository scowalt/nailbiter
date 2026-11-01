using backend.Services;

namespace backend.Services.Highlights;

public sealed record HighlightsApplicationResult(int Selected, int Revalidated, string Reason);
public enum HighlightsOperatorAction { Inspect, Attach, Replace, Remove, Resume }
public sealed record HighlightsOperatorRequest(HighlightsOperatorAction Action, string GameId, string? VideoId = null, bool Apply = false);
public sealed record HighlightsOperatorResult(bool Accepted, bool Applied, string Reason, GameHighlightsInspection? Inspection);
