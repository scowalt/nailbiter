namespace backend.Services.OpenF1

open System
open System.Text.Json.Serialization

type OpenF1Meeting =
    { [<JsonPropertyName("meeting_key")>] MeetingKey: int
      [<JsonPropertyName("meeting_name")>] MeetingName: string
      [<JsonPropertyName("meeting_official_name")>] MeetingOfficialName: string
      [<JsonPropertyName("location")>] Location: string
      [<JsonPropertyName("country_code")>] CountryCode: string
      [<JsonPropertyName("country_name")>] CountryName: string
      [<JsonPropertyName("circuit_key")>] CircuitKey: Nullable<int>
      [<JsonPropertyName("circuit_short_name")>] CircuitShortName: string
      [<JsonPropertyName("date_start")>] DateStart: Nullable<DateTime>
      [<JsonPropertyName("date_end")>] DateEnd: Nullable<DateTime>
      [<JsonPropertyName("year")>] Year: int }

type OpenF1Session =
    { [<JsonPropertyName("session_key")>] SessionKey: int
      [<JsonPropertyName("session_type")>] SessionType: string
      [<JsonPropertyName("session_name")>] SessionName: string
      [<JsonPropertyName("date_start")>] DateStart: Nullable<DateTime>
      [<JsonPropertyName("date_end")>] DateEnd: Nullable<DateTime>
      [<JsonPropertyName("meeting_key")>] MeetingKey: int
      [<JsonPropertyName("circuit_key")>] CircuitKey: Nullable<int>
      [<JsonPropertyName("circuit_short_name")>] CircuitShortName: string
      [<JsonPropertyName("country_code")>] CountryCode: string
      [<JsonPropertyName("country_name")>] CountryName: string
      [<JsonPropertyName("location")>] Location: string
      [<JsonPropertyName("year")>] Year: int }

type OpenF1Driver =
    { [<JsonPropertyName("driver_number")>] DriverNumber: int
      [<JsonPropertyName("full_name")>] FullName: string
      [<JsonPropertyName("name_acronym")>] NameAcronym: string
      [<JsonPropertyName("team_name")>] TeamName: string
      [<JsonPropertyName("team_colour")>] TeamColour: string
      [<JsonPropertyName("headshot_url")>] HeadshotUrl: string
      [<JsonPropertyName("country_code")>] CountryCode: string
      [<JsonPropertyName("session_key")>] SessionKey: Nullable<int>
      [<JsonPropertyName("meeting_key")>] MeetingKey: Nullable<int> }

type OpenF1Position =
    { [<JsonPropertyName("session_key")>] SessionKey: int
      [<JsonPropertyName("driver_number")>] DriverNumber: int
      [<JsonPropertyName("position")>] Position: Nullable<int>
      [<JsonPropertyName("date")>] Date: Nullable<DateTime>
      [<JsonPropertyName("meeting_key")>] MeetingKey: Nullable<int> }

type OpenF1Interval =
    { [<JsonPropertyName("session_key")>] SessionKey: int
      [<JsonPropertyName("driver_number")>] DriverNumber: int
      [<JsonPropertyName("gap_to_leader")>] GapToLeader: Nullable<float>
      [<JsonPropertyName("interval")>] Interval: Nullable<float>
      [<JsonPropertyName("date")>] Date: Nullable<DateTime> }

type OpenF1RaceControl =
    { [<JsonPropertyName("session_key")>] SessionKey: int
      [<JsonPropertyName("date")>] Date: Nullable<DateTime>
      [<JsonPropertyName("category")>] Category: string
      [<JsonPropertyName("flag")>] Flag: string
      [<JsonPropertyName("message")>] Message: string
      [<JsonPropertyName("driver_number")>] DriverNumber: Nullable<int>
      [<JsonPropertyName("lap_number")>] LapNumber: Nullable<int> }

type OpenF1Lap =
    { [<JsonPropertyName("session_key")>] SessionKey: int
      [<JsonPropertyName("driver_number")>] DriverNumber: int
      [<JsonPropertyName("lap_number")>] LapNumber: Nullable<int>
      [<JsonPropertyName("lap_duration")>] LapDuration: Nullable<float>
      [<JsonPropertyName("date_start")>] DateStart: Nullable<DateTime> }
