module ProviderBodyReadTests

open System
open System.IO
open Xunit

let private htmlReaders =
    set [ "apps/backend.fsharp/Services/PowerRankingsGuruService.fs"
          "apps/backend.fsharp/Services/PlayoffStatusService.fs"
          "apps/backend.shared/Services/NhlPowerRankingsGuruService.cs" ]

let private repoRoot () =
    Seq.unfold (fun (dir: DirectoryInfo) -> if isNull dir then None else Some(dir, dir.Parent)) (DirectoryInfo(AppContext.BaseDirectory))
    |> Seq.find (fun dir -> Directory.Exists(Path.Combine(dir.FullName, "apps", "backend.shared")))

let private stringReads root =
    [ for project in [ "backend"; "backend.fsharp"; "backend.shared" ] do
        for file in Directory.EnumerateFiles(Path.Combine(root, "apps", project), "*.*", SearchOption.AllDirectories) do
            let path = Path.GetRelativePath(root, file).Replace('\\', '/')
            let isSource = path.EndsWith(".fs") || path.EndsWith(".cs")
            let isBuildOutput = path.Contains("/bin/") || path.Contains("/obj/")
            if isSource && not isBuildOutput && not (htmlReaders.Contains path) then
                for number, line in File.ReadLines(file) |> Seq.indexed do
                    if line.Contains("GetStringAsync(") || line.Contains("ReadAsStringAsync(") then
                        yield $"{path}:{number + 1}: {line.Trim()}" ]

[<Fact>]
let ``provider JSON is parsed from the response stream, not a string`` () =
    let reads = stringReads (repoRoot ()).FullName
    Assert.True(
        List.isEmpty reads,
        "Reading a provider body into a string puts a UTF-16 copy of it, twice its size, on the large object heap, and parsing it allocates again. "
        + "Use GetFromJsonAsync/ReadFromJsonAsync, or JsonDocument.ParseAsync over GetStreamAsync. Only the HTML readers listed in ProviderBodyReadTests may read strings:\n"
        + String.Join("\n", reads))
