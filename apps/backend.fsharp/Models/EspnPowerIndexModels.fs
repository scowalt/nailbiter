namespace backend.Services.Espn

open System.Text.Json.Serialization

type EspnLogo =
    { [<JsonPropertyName("href")>] Href: string }

type EspnPowerIndexTeamInfo =
    { [<JsonPropertyName("id")>] Id: string
      [<JsonPropertyName("displayName")>] DisplayName: string
      [<JsonPropertyName("abbreviation")>] Abbreviation: string
      [<JsonPropertyName("nickname")>] Nickname: string
      [<JsonPropertyName("logos")>] Logos: ResizeArray<EspnLogo> }

type EspnPagination =
    { [<JsonPropertyName("count")>] Count: int
      [<JsonPropertyName("limit")>] Limit: int
      [<JsonPropertyName("page")>] Page: int
      [<JsonPropertyName("pages")>] Pages: int }

type EspnCategory =
    { [<JsonPropertyName("name")>] Name: string
      [<JsonPropertyName("displayName")>] DisplayName: string
      [<JsonPropertyName("names")>] Names: ResizeArray<string>
      [<JsonPropertyName("values")>] Values: ResizeArray<System.Nullable<float>>
      [<JsonPropertyName("totals")>] Totals: ResizeArray<string>
      [<JsonPropertyName("ranks")>] Ranks: ResizeArray<string> }
    member this.GetValueByName(fieldName: string) : System.Nullable<float> =
        if isNull (box this.Names) || isNull (box this.Values) then
            System.Nullable<float>()
        else
            let index = this.Names.IndexOf(fieldName)
            if index >= 0 && index < this.Values.Count then
                this.Values.[index]
            else
                System.Nullable<float>()

type EspnPowerIndexTeam =
    { [<JsonPropertyName("team")>] Team: EspnPowerIndexTeamInfo
      [<JsonPropertyName("categories")>] Categories: ResizeArray<EspnCategory> }

type EspnPowerIndexResponse =
    { [<JsonPropertyName("teams")>] Teams: ResizeArray<EspnPowerIndexTeam>
      [<JsonPropertyName("pagination")>] Pagination: EspnPagination
      [<JsonPropertyName("categories")>] Categories: ResizeArray<EspnCategory> }
