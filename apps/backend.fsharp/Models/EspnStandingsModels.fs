namespace backend.Services.Espn

open System.Text.Json.Serialization

type EspnStandingsStat =
    { [<JsonPropertyName("name")>] Name: string
      [<JsonPropertyName("value")>] Value: float
      [<JsonPropertyName("displayValue")>] DisplayValue: string }

type EspnStandingsTeam =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("displayName")>] DisplayName: string
      [<JsonPropertyName("abbreviation")>] Abbreviation: string }

type EspnStandingsEntry =
    { [<JsonPropertyName("team")>] Team: EspnStandingsTeam
      [<JsonPropertyName("stats")>] Stats: ResizeArray<EspnStandingsStat> }

type EspnStandingsData =
    { [<JsonPropertyName("entries")>] Entries: ResizeArray<EspnStandingsEntry> }

type EspnStandingsGroup =
    { [<JsonPropertyName("name")>] Name: string
      [<JsonPropertyName("abbreviation")>] Abbreviation: string
      [<JsonPropertyName("standings")>] Standings: EspnStandingsData
      [<JsonPropertyName("children")>] Children: ResizeArray<EspnStandingsGroup> }

type EspnStandingsResponse =
    { [<JsonPropertyName("children")>] Children: ResizeArray<EspnStandingsGroup> }
