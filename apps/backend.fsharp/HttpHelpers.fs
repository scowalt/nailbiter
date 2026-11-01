namespace Backend.FSharp

open System.Text.Json

module HttpHelpers =

    let jsonOptions =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true)
