namespace backend.Services.Espn

module EspnSiteApi =
    [<Literal>]
    let Origin = "https://site.web.api.espn.com"

    let url (path: string) = $"{Origin}{path}"
