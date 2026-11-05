using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using backend.Models;

namespace backend.Scoring;

[JsonConverter(typeof(ProEvidenceKindConverter))]
public enum ProEvidenceKind { Competition, Odds, Qualification, Ranking, Conditional }

public static class ProEvidenceKinds
{
    public static string StoredName(this ProEvidenceKind kind) => kind switch
    {
        ProEvidenceKind.Competition => "competition",
        ProEvidenceKind.Odds => "odds",
        ProEvidenceKind.Qualification => "qualification",
        ProEvidenceKind.Ranking => "ranking",
        ProEvidenceKind.Conditional => "conditional",
        _ => throw new JsonException("Unknown pro evidence kind")
    };
}

public sealed class ProEvidenceKindConverter : JsonConverter<ProEvidenceKind>
{
    public override ProEvidenceKind Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Stored pro evidence kind must be a name");
        return reader.GetString() switch
        {
            "competition" => ProEvidenceKind.Competition,
            "odds" => ProEvidenceKind.Odds,
            "qualification" => ProEvidenceKind.Qualification,
            "ranking" => ProEvidenceKind.Ranking,
            "conditional" => ProEvidenceKind.Conditional,
            _ => throw new JsonException("Unknown stored pro evidence kind")
        };
    }
    public override void Write(Utf8JsonWriter writer, ProEvidenceKind value, JsonSerializerOptions options) => writer.WriteStringValue(value.StoredName());
}

public enum ProEvidenceState { Accepted, Gap, Rejected }
public enum ProHypeProblem { EvidenceGap, CompletedResultLag, InvalidGame, InvalidEvidence }
public sealed record ProEvidenceReference(ProEvidenceKind Kind, string Source, string Team, string Revision, DateTime ObservedAt,
    DateTime? PublishedAt, ProEvidenceState State, string Reason, DateTime[] RejectedPublications, bool BlocksSelection = false, double? ExpiredProbability = null, int? PowerRank = null)
{
    public static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static ProEvidenceReference Conditional(NflPlayoffLeverageEvidence? evidence, string team, DateTime fallback,
        ProEvidenceState state)
    {
        var revision = Fingerprint(new { evidence?.Version, evidence?.Source, evidence?.GameId, evidence?.Season, evidence?.Home, evidence?.Away,
            evidence?.ScheduledStart, evidence?.ObservedAt, evidence?.SchedulePublishedAt, evidence?.PublicationLabel,
            evidence?.ScheduleVerified, evidence?.CompletedGameIds,
            Odds = team == evidence?.Home ? evidence.HomeOdds : evidence?.AwayOdds });
        if (evidence?.Version == 2) revision = Fingerprint(new { revision, evidence.CorroboratedKickoff });
        return new(ProEvidenceKind.Conditional, NflPlayoffLeveragePolicy.Source, team, revision,
            evidence?.ObservedAt ?? fallback, team == evidence?.Home ? evidence.HomeOdds?.PublishedAt : evidence?.AwayOdds?.PublishedAt,
            state, "conditional_qualification", []);
    }
    public static ProEvidenceReference Qualification(TeamRating row, string reason, DateTime? evidenceAt, bool blocksSelection) =>
        new(ProEvidenceKind.Qualification, row.Source, row.TeamAbbreviation, Fingerprint(new { row.FetchedAt, row.SourceSeason, row.PlayoffProbability,
            row.PlayoffProbabilityIsFinal, row.PlayoffEvidenceJson }), row.FetchedAt, evidenceAt,
            reason == "accepted" ? ProEvidenceState.Accepted : reason == "expired" ? ProEvidenceState.Gap : ProEvidenceState.Rejected,
            reason, PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)?.MoneyPuckRejectedPublications ?? [], blocksSelection,
            reason == "expired" ? PlayoffModelEvidence.Parse(row.PlayoffEvidenceJson)?.MoneyPuckExpiredProbability : null);
}

public sealed record ProHypeAssessment(ProHypeProblem[] Problems, ProEvidenceReference[] SupportingEvidence,
    ProEvidenceReference[] Observations);
