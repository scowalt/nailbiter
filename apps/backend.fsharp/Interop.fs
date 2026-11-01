namespace Backend.FSharp

[<AutoOpen>]
module Interop =

    let ofObj (value: 'T) : 'T option when 'T : not struct =
        if isNull (box value) then None else Some value

    let toObj (value: 'T option) : 'T when 'T : not struct =
        match value with
        | Some v -> v
        | None -> Unchecked.defaultof<'T>

    let ofNullable (value: System.Nullable<'T>) : 'T option =
        if value.HasValue then Some value.Value else None

    let toNullable (value: 'T option) : System.Nullable<'T> =
        match value with
        | Some v -> System.Nullable<'T>(v)
        | None -> System.Nullable<'T>()

    let seqOfObj (value: #seq<'T>) : seq<'T> =
        if isNull (box value) then Seq.empty else value :> seq<'T>
