namespace backend.Services

module internal NhlQualificationRoster =
    let teams =
        set [ "ANA"; "BOS"; "BUF"; "CGY"; "CAR"; "CHI"; "COL"; "CBJ"; "DAL"; "DET"; "EDM"; "FLA"
              "LA"; "MIN"; "MTL"; "NSH"; "NJ"; "NYI"; "NYR"; "OTT"; "PHI"; "PIT"; "SJ"; "SEA"
              "STL"; "TB"; "TOR"; "UTAH"; "VAN"; "VGK"; "WSH"; "WPG" ]
