namespace backend.Services.EspnNascar

open System.Text.Json.Serialization

type EspnNascarFlag =
    { [<JsonPropertyName("href")>] Href: string }

type EspnNascarAthlete =
    { [<JsonPropertyName("fullName")>] FullName: string
      [<JsonPropertyName("displayName")>] DisplayName: string
      [<JsonPropertyName("shortName")>] ShortName: string
      [<JsonPropertyName("flag")>] Flag: EspnNascarFlag }

type EspnNascarVehicle =
    { [<JsonPropertyName("number")>] Number: string
      [<JsonPropertyName("manufacturer")>] Manufacturer: string
      [<JsonPropertyName("team")>] Team: string }

type EspnNascarCompetitor =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("order")>] Order: System.Nullable<int>
      [<JsonPropertyName("winner")>] Winner: System.Nullable<bool>
      [<JsonPropertyName("athlete")>] Athlete: EspnNascarAthlete
      [<JsonPropertyName("vehicle")>] Vehicle: EspnNascarVehicle
      [<JsonPropertyName("statistics")>] Statistics: ResizeArray<obj> }

type EspnNascarStatusType =
    { [<JsonPropertyName("name")>] Name: string
      [<JsonPropertyName("state")>] State: string
      [<JsonPropertyName("completed")>] Completed: bool }

type EspnNascarEventStatus =
    { [<JsonPropertyName("period")>] Period: System.Nullable<int>
      [<JsonPropertyName("type")>] Type: EspnNascarStatusType }

type EspnNascarCompetitionType =
    { [<JsonPropertyName("abbreviation")>] Abbreviation: string }

type EspnNascarCompetition =
    { [<JsonPropertyName("competitors")>] Competitors: ResizeArray<EspnNascarCompetitor>
      [<JsonPropertyName("type")>] Type: EspnNascarCompetitionType
      [<JsonPropertyName("status")>] Status: EspnNascarEventStatus }

type EspnNascarCalendarEventRef =
    { [<JsonPropertyName("$ref")>] Ref: string }

type EspnNascarCalendarEntry =
    { [<JsonPropertyName("label")>] Label: string
      [<JsonPropertyName("startDate")>] StartDate: string
      [<JsonPropertyName("endDate")>] EndDate: string
      [<JsonPropertyName("event")>] Event: EspnNascarCalendarEventRef }

type EspnNascarLeague =
    { [<JsonPropertyName("calendar")>] Calendar: ResizeArray<EspnNascarCalendarEntry> }

type EspnNascarEvent =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("date")>] Date: string
      [<JsonPropertyName("name")>] Name: string
      [<JsonPropertyName("competitions")>] Competitions: ResizeArray<EspnNascarCompetition>
      [<JsonPropertyName("status")>] Status: EspnNascarEventStatus }

type EspnNascarScoreboardResponse =
    { [<JsonPropertyName("leagues")>] Leagues: ResizeArray<EspnNascarLeague>
      [<JsonPropertyName("events")>] Events: ResizeArray<EspnNascarEvent> }

type EspnCoreVehicle =
    { [<JsonPropertyName("number")>] Number: string
      [<JsonPropertyName("manufacturer")>] Manufacturer: string
      [<JsonPropertyName("team")>] Team: string }

type EspnCoreAthlete =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("fullName")>] FullName: string
      [<JsonPropertyName("displayName")>] DisplayName: string
      [<JsonPropertyName("shortName")>] ShortName: string
      [<JsonPropertyName("flag")>] Flag: EspnNascarFlag }

type EspnCoreCompetitorItem =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("order")>] Order: System.Nullable<int>
      [<JsonPropertyName("startOrder")>] StartOrder: System.Nullable<int>
      [<JsonPropertyName("athlete")>] Athlete: EspnCoreAthlete
      [<JsonPropertyName("vehicle")>] Vehicle: EspnCoreVehicle }

[<CLIMutable>]
type EspnCoreCompetitorsResponse =
    { [<JsonPropertyName("count")>] Count: int
      [<JsonPropertyName("items")>] Items: ResizeArray<EspnCoreCompetitorItem> }
