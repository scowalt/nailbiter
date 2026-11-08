module QueryLambdaTests

open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Linq.Expressions
open System.Reflection
open System.Reflection.Emit
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Runtime.CompilerServices
open Microsoft.EntityFrameworkCore
open Microsoft.FSharp.Linq
open Microsoft.FSharp.Linq.RuntimeHelpers
open Microsoft.FSharp.Quotations
open Xunit
open backend.Data
open backend.Models

type private Replay =
    | Lambda of Expression
    | QueryExpression
    | NotReplayed of reason: string

type private QuotationSite =
    { Location: string
      Method: string
      InGenericCode: bool
      Replay: Replay }

type private Slot =
    | TypeToken of Type
    | FieldToken of FieldInfo
    | TypeValue of Type
    | Int of int
    | Str of string
    | Arr of Array
    | Quoted of Expr * site: int option
    | Unknown

let private opCodes =
    typeof<OpCodes>.GetFields(BindingFlags.Public ||| BindingFlags.Static)
    |> Array.map (fun f -> f.GetValue null :?> OpCode)
    |> Array.map (fun op -> op.Value, op)
    |> dict

let private operandSize (op: OpCode) (il: byte[]) at =
    match op.OperandType with
    | OperandType.InlineNone -> 0
    | OperandType.ShortInlineBrTarget
    | OperandType.ShortInlineI
    | OperandType.ShortInlineVar -> 1
    | OperandType.InlineVar -> 2
    | OperandType.InlineI8
    | OperandType.InlineR -> 8
    | OperandType.InlineSwitch -> 4 + 4 * BitConverter.ToInt32(il, at)
    | _ -> 4

let private pops (op: OpCode) =
    match op.StackBehaviourPop with
    | StackBehaviour.Pop0 -> 0
    | StackBehaviour.Pop1
    | StackBehaviour.Popi
    | StackBehaviour.Popref -> 1
    | StackBehaviour.Popi_popi_popi
    | StackBehaviour.Popref_popi_popi
    | StackBehaviour.Popref_popi_popi8
    | StackBehaviour.Popref_popi_popr4
    | StackBehaviour.Popref_popi_popr8
    | StackBehaviour.Popref_popi_popref -> 3
    | _ -> 2

let private pushes (op: OpCode) =
    match op.StackBehaviourPush with
    | StackBehaviour.Push0 -> 0
    | StackBehaviour.Push1_push1 -> 2
    | _ -> 1

let private defaultOf (t: Type) =
    if t.IsValueType then Activator.CreateInstance t else null

let private runsQuery (callee: MethodBase) =
    (callee.DeclaringType = typeof<QueryBuilder> && callee.Name = "Run")
    || (callee.Name.StartsWith "RunQueryAs"
        && not (isNull callee.DeclaringType)
        && callee.DeclaringType.Namespace = "Microsoft.FSharp.Linq.QueryRunExtensions")

let private convertLambda (converter: MethodInfo) (quotation: Expr) =
    let parameter = (converter.GetParameters()).[0].ParameterType.GetGenericArguments()[0]
    let typed = typeof<Expr>.GetMethod("Cast").MakeGenericMethod(parameter).Invoke(null, [| quotation |])

    try
        Lambda(converter.Invoke(null, [| typed |]) :?> Expression)
    with :? TargetInvocationException as ex ->
        NotReplayed $"QuotationToLambdaExpression failed: {ex.InnerException.Message}"

type private TreeScan =
    { Blocks: int
      Expanded: int
      Hidden: string list }

let private buildsATreeWhenRun (node: Expression) =
    let holdsATree =
        match node with
        | :? ConstantExpression as constant -> constant.Value :? Expression || constant.Value :? Expr
        | _ -> false

    holdsATree
    || node.NodeType <> ExpressionType.Quote
       && (typeof<Expression>.IsAssignableFrom node.Type || typeof<Expr>.IsAssignableFrom node.Type)

let private readsAParameter (expression: Expression) =
    let mutable reads = false

    let visitor =
        { new ExpressionVisitor() with
            override _.VisitParameter node =
                reads <- true
                node }

    visitor.Visit expression |> ignore
    reads

let private expand (node: Expression) =
    match node with
    | :? MethodCallExpression as call when
        call.Method.DeclaringType.FullName = "Microsoft.FSharp.Linq.RuntimeHelpers.LeafExpressionConverter"
        && call.Method.Name.StartsWith "QuotationTo"
        && not (readsAParameter call)
        ->
        try
            match Expression.Lambda<Func<obj>>(Expression.Convert(call, typeof<obj>)).Compile().Invoke() with
            | :? Expression as tree -> Ok tree
            | other -> Error $"{call.Method.Name} returned {other}"
        with ex ->
            Error $"{call.Method.Name} failed: {ex.Message}"
    | :? ConstantExpression as constant when (constant.Value :? Expression) -> Ok(constant.Value :?> Expression)
    | _ -> Error $"a {node.NodeType} node of type {node.Type.Name}: {node}"

let rec private scanTree (expression: Expression) =
    let mutable blocks = 0
    let mutable expanded = 0
    let hidden = ResizeArray<string>()

    let visitor =
        { new ExpressionVisitor() with
            override _.Visit(node: Expression) =
                if isNull node || not (buildsATreeWhenRun node) then
                    base.Visit node
                else
                    match expand node with
                    | Ok tree ->
                        let inner = scanTree tree
                        blocks <- blocks + inner.Blocks
                        expanded <- expanded + 1 + inner.Expanded
                        hidden.AddRange inner.Hidden
                    | Error reason -> hidden.Add reason

                    node

            override _.VisitBlock node =
                blocks <- blocks + 1
                base.VisitBlock node }

    visitor.Visit expression |> ignore

    { Blocks = blocks
      Expanded = expanded
      Hidden = List.ofSeq hidden }

let private blockCount (expression: Expression) = (scanTree expression).Blocks

let private locator (pdb: MetadataReader) =
    fun (m: MethodBase) offset ->
        pdb.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle m.MetadataToken).GetSequencePoints()
        |> Seq.filter (fun point -> not point.IsHidden && point.Offset <= offset)
        |> Seq.tryLast
        |> Option.map (fun point ->
            let document = pdb.GetString(pdb.GetDocument(point.Document).Name)
            $"{Path.GetFileName document}:{point.StartLine}")
        |> Option.defaultValue "unknown location"

let private replay (m: MethodBase) (il: byte[]) (locate: MethodBase -> int -> string) =
    let typeArgs =
        if m.DeclaringType.IsGenericType then m.DeclaringType.GetGenericArguments() else null

    let methodArgs =
        if m.IsGenericMethod then m.GetGenericArguments() else null

    let resolve token = m.Module.ResolveMember(token, typeArgs, methodArgs)
    let stack = Stack<Slot>()
    let pop () = if stack.Count = 0 then Unknown else stack.Pop()
    let popN n = List.init n (fun _ -> pop ()) |> List.rev
    let sites = ResizeArray<QuotationSite>()

    let site offset replay =
        { Location = locate m offset
          Method = $"{m.DeclaringType.FullName}.{m.Name}"
          InGenericCode = m.ContainsGenericParameters || m.DeclaringType.ContainsGenericParameters
          Replay = replay }

    let consume index replay =
        sites[index] <- { sites[index] with Replay = replay }

    let call offset (callee: MethodBase) isNewObj =
        let args = popN (callee.GetParameters().Length)

        if not callee.IsStatic && not isNewObj then
            pop () |> ignore

        let returns =
            match callee with
            | :? MethodInfo as mi -> mi.ReturnType <> typeof<Void>
            | _ -> isNewObj

        let result =
            match callee.DeclaringType, callee.Name, args with
            | t, "GetTypeFromHandle", [ TypeToken token ] when t = typeof<Type> -> TypeValue token
            | t, "Empty", [] when t = typeof<Array> -> Arr(Array.CreateInstance(callee.GetGenericArguments()[0], 0))
            | t, "InitializeArray", [ Arr bytes; FieldToken field ] when t = typeof<RuntimeHelpers> ->
                RuntimeHelpers.InitializeArray(bytes, field.FieldHandle)
                Unknown
            | t, ("Value" | "ValueWithName" as name), _ when t = typeof<Expr> && callee.IsGenericMethod ->
                let spliced = callee.GetGenericArguments()[0]

                if spliced.ContainsGenericParameters then
                    Unknown
                else
                    match name, args with
                    | "ValueWithName", [ _; Str label ] ->
                        Quoted(Expr.ValueWithName(defaultOf spliced, spliced, label), None)
                    | _ -> Quoted(Expr.Value(defaultOf spliced, spliced), None)
            | t, "Deserialize40", [ TypeValue qualifying; Arr referenced; Arr spliceTypes; Arr splices; Arr bytes ] when
                t = typeof<Expr>
                && not (Seq.exists isNull (Seq.cast<obj> referenced))
                && not (Seq.exists isNull (Seq.cast<obj> spliceTypes))
                && not (Seq.exists isNull (Seq.cast<obj> splices))
                ->
                try
                    let quoted =
                        Expr.Deserialize40(
                            qualifying,
                            referenced :?> Type[],
                            spliceTypes :?> Type[],
                            splices :?> Expr[],
                            bytes :?> byte[]
                        )

                    sites.Add(
                        site
                            offset
                            (NotReplayed "the literal is not passed straight to LeafExpressionConverter.QuotationToLambdaExpression")
                    )

                    Quoted(quoted, Some(sites.Count - 1))
                with ex ->
                    sites.Add(site offset (NotReplayed $"Deserialize40 failed: {ex.Message}"))
                    Unknown
            | t, name, _ when t = typeof<Expr> && name.StartsWith "Deserialize" ->
                sites.Add(site offset (NotReplayed "quotation literal built from values this scanner cannot replay"))
                Unknown
            | t, "Cast", [ Quoted(quotation, index) ] when t = typeof<Expr> -> Quoted(quotation, index)
            | t, "QuotationToLambdaExpression", [ Quoted(quotation, Some index) ] when
                t.FullName = "Microsoft.FSharp.Linq.RuntimeHelpers.LeafExpressionConverter"
                ->
                if callee.ContainsGenericParameters then
                    consume index (NotReplayed "the lambda's type contains a generic parameter")
                else
                    consume index (convertLambda (callee :?> MethodInfo) quotation)

                Unknown
            | _, _, arguments when runsQuery callee ->
                for argument in arguments do
                    match argument with
                    | Quoted(_, Some index) -> consume index QueryExpression
                    | _ -> ()

                Unknown
            | _ -> Unknown

        if returns then
            stack.Push result

    let mutable at = 0

    while at < il.Length do
        let start = at

        let op =
            if il[at] = 0xFEuy then
                at <- at + 2
                opCodes[int16 (0xFE00 ||| int il[at - 1])]
            else
                at <- at + 1
                opCodes[int16 il[at - 1]]

        let operand = at
        at <- at + operandSize op il operand
        let token () = BitConverter.ToInt32(il, operand)

        if op = OpCodes.Ldc_I4_M1 then stack.Push(Int -1)
        elif op.Value >= OpCodes.Ldc_I4_0.Value && op.Value <= OpCodes.Ldc_I4_8.Value then
            stack.Push(Int(int (op.Value - OpCodes.Ldc_I4_0.Value)))
        elif op = OpCodes.Ldc_I4_S then stack.Push(Int(int (sbyte il[operand])))
        elif op = OpCodes.Ldc_I4 then stack.Push(Int(token ()))
        elif op = OpCodes.Ldstr then stack.Push(Str(m.Module.ResolveString(token ())))
        elif op = OpCodes.Ldtoken then
            match resolve (token ()) with
            | :? Type as t -> stack.Push(TypeToken t)
            | :? FieldInfo as f -> stack.Push(FieldToken f)
            | _ -> stack.Push Unknown
        elif op = OpCodes.Dup then
            let top = pop ()
            stack.Push top
            stack.Push top
        elif op = OpCodes.Newarr then
            match pop (), resolve (token ()) with
            | Int length, (:? Type as element) when not element.ContainsGenericParameters ->
                stack.Push(Arr(Array.CreateInstance(element, length)))
            | _ -> stack.Push Unknown
        elif op.Name.StartsWith "stelem" then
            match popN 3 with
            | [ Arr target; Int index; TypeValue t ] -> target.SetValue(t, index)
            | [ Arr target; Int index; Quoted(e, _) ] -> target.SetValue(e, index)
            | _ -> ()
        elif op = OpCodes.Call || op = OpCodes.Callvirt || op = OpCodes.Newobj then
            call start (resolve (token ()) :?> MethodBase) (op = OpCodes.Newobj)
        elif
            op.FlowControl = FlowControl.Branch
            || op.FlowControl = FlowControl.Return
            || op.FlowControl = FlowControl.Throw
            || op = OpCodes.Calli
        then
            stack.Clear()
        else
            popN (pops op) |> ignore

            for _ in 1 .. pushes op do
                stack.Push Unknown

    sites :> seq<_>

let private quotationSites (assembly: Assembly) =
    use pdb =
        MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(assembly.Location, ".pdb")))

    let locate = locator (pdb.GetMetadataReader())

    let flags =
        BindingFlags.DeclaredOnly
        ||| BindingFlags.Instance
        ||| BindingFlags.Static
        ||| BindingFlags.Public
        ||| BindingFlags.NonPublic

    assembly.GetTypes()
    |> Seq.collect (fun t -> Seq.append (t.GetMethods flags |> Seq.cast<MethodBase>) (t.GetConstructors flags |> Seq.cast<MethodBase>))
    |> Seq.choose (fun m ->
        match m.GetMethodBody() with
        | null -> None
        | body -> Some(m, body.GetILAsByteArray()))
    |> Seq.collect (fun (m, il) -> replay m il locate)
    |> Seq.toList

let private treeScan site =
    match site.Replay with
    | Lambda tree -> scanTree tree
    | QueryExpression
    | NotReplayed _ -> { Blocks = 0; Expanded = 0; Hidden = [] }

let private blocksIn site = (treeScan site).Blocks

let private blockedHelp =
    "These F# query lambdas compile to a BlockExpression, and EF Core throws NotSupportedException "
    + "on it every time the query runs (#55, #66). F# adds the block when a lambda reads a member of a "
    + "struct that is not a local, such as DateTime.UtcNow.AddDays(-1.0) or g.HomeScore.HasValue. "
    + "Hoist the value into a local before the query, or compare g.HomeScore <> Nullable() instead of "
    + "reading HasValue."

let private genericHelp =
    "QueryLambdaTests cannot replay query lambdas in a generic function or type, because the entity "
    + "type is unknown until a call. Write the query in the non-generic caller, or give the helper a "
    + "concrete entity type such as IQueryable<Game> instead of IQueryable<'T>."

let private unreplayedHelp =
    "QueryLambdaTests could not replay these quotation literals, so it cannot tell whether they compile "
    + "to a block. Extend the IL replay in QueryLambdaTests.fs to cover how they are built."

let private queryExpressionHelp =
    "query { } expressions are not checked by QueryLambdaTests. Write EF queries as method chains "
    + "(.Where(fun ...)), or extend QueryLambdaTests.fs to translate query expressions."

let private hiddenTreeHelp =
    "These query lambdas contain an expression tree that EF Core builds only when the query runs, and "
    + "QueryLambdaTests cannot build it, so it cannot tell whether that tree has a block. Write the inner "
    + "query as a lambda literal inside the outer one, or extend the expansion in QueryLambdaTests.fs."

let private problems sites =
    let report help (found: QuotationSite list) =
        if found.IsEmpty then
            None
        else
            found
            |> List.map (fun site ->
                let reasons =
                    match site.Replay with
                    | NotReplayed reason -> [ reason ]
                    | Lambda _
                    | QueryExpression -> (treeScan site).Hidden

                match reasons with
                | [] -> $"  {site.Location} in {site.Method}"
                | _ -> $"""  {site.Location} in {site.Method} ({String.concat "; " reasons})""")
            |> String.concat "\n"
            |> fun lines -> Some $"{help} Each line number is where the query statement starts.\n{lines}"

    let notReplayed inGenericCode =
        sites
        |> List.filter (fun site ->
            match site.Replay with
            | NotReplayed _ -> site.InGenericCode = inGenericCode
            | Lambda _
            | QueryExpression -> false)

    [ report queryExpressionHelp (sites |> List.filter (fun site -> site.Replay = QueryExpression))
      report genericHelp (notReplayed true)
      report unreplayedHelp (notReplayed false)
      report hiddenTreeHelp (sites |> List.filter (fun site -> not (treeScan site).Hidden.IsEmpty))
      report blockedHelp (sites |> List.filter (fun site -> blocksIn site > 0)) ]
    |> List.choose id

[<Fact>]
let ``no backend query lambda compiles to a block that EF Core rejects`` () =
    let sites = quotationSites typeof<backend.Services.GameDataService>.Assembly
    Assert.NotEmpty sites
    let found = problems sites
    Assert.True(found.IsEmpty, String.concat "\n\n" found)

let private controlSites (name: string) =
    quotationSites (Assembly.GetExecutingAssembly())
    |> List.filter (fun site -> site.Method.EndsWith $".{name}")

let private npgsqlContext () =
    new ExcitementDbContext(DbContextOptionsBuilder<ExcitementDbContext>().UseNpgsql("Host=unused.invalid").Options)

let private copiesTheStruct (games: IQueryable<Game>) =
    games.Where(fun g -> g.ScheduledStart >= DateTime.UtcNow.AddDays(-1.0))

let private hoistsTheValue (games: IQueryable<Game>) =
    let cutoff = DateTime.UtcNow.AddDays(-1.0)
    games.Where(fun g -> g.ScheduledStart >= cutoff)

[<Fact>]
let ``the scan flags the struct copy EF Core rejects and passes the hoisted form`` () =
    use db = npgsqlContext ()
    let copied = copiesTheStruct db.Games
    let hoisted = hoistsTheValue db.Games

    Assert.Equal(1, blockCount copied.Expression)
    Assert.Throws<NotSupportedException>(fun () -> copied.ToQueryString() |> ignore) |> ignore
    Assert.Equal(0, blockCount hoisted.Expression)
    Assert.Contains("WHERE", hoisted.ToQueryString())

    Assert.Equal<int list>([ 1 ], controlSites (nameof copiesTheStruct) |> List.map blocksIn)
    Assert.Equal<int list>([ 0 ], controlSites (nameof hoistsTheValue) |> List.map blocksIn)

let private filtersInsideAQuery (db: ExcitementDbContext) =
    query {
        for g in db.Games.Where(fun g -> g.HomeScore.HasValue) do
            select g.Id
    }

let private countsScoredGames (db: ExcitementDbContext) =
    query {
        for g in db.Games do
            where (g.HomeScore <> Nullable())
            count
    }

[<Fact>]
let ``the scan refuses query expressions instead of checking them`` () =
    for name in [ nameof filtersInsideAQuery; nameof countsScoredGames ] do
        let report = Assert.Single(problems (controlSites name))
        Assert.StartsWith(queryExpressionHelp, report)
        Assert.DoesNotContain("Hoist", report)

let private quotesAFilter () =
    <@ fun (g: Game) -> g.ScheduledStart >= DateTime.UtcNow.AddDays(-1.0) @>

[<Fact>]
let ``the scan fails a literal it does not hand to the lambda converter`` () =
    let report = Assert.Single(problems (controlSites (nameof quotesAFilter)))
    Assert.StartsWith(unreplayedHelp, report)
    Assert.Contains("not passed straight to LeafExpressionConverter.QuotationToLambdaExpression", report)

let private keepsRows (rows: IQueryable<'T>) =
    rows.Where(fun row -> not (isNull (box row)))

[<Fact>]
let ``the scan reports a generic query helper with advice to make it concrete`` () =
    let report = Assert.Single(problems (controlSites (nameof keepsRows)))
    Assert.StartsWith(genericHelp, report)
    Assert.Contains("the lambda's type contains a generic parameter", report)
    Assert.DoesNotContain("Hoist", report)

let private filtersOnAnotherTable (db: ExcitementDbContext) =
    db.Games.Where(fun g -> db.OddsSnapshots.Any(fun o -> o.DrawMoneyline.HasValue))

let private filtersOnAnotherTableWithoutTheCopy (db: ExcitementDbContext) =
    db.Games.Where(fun g -> db.OddsSnapshots.Any(fun o -> o.DrawMoneyline <> Nullable()))

let private compilesTheStructCopy () =
    EF.CompileQuery<ExcitementDbContext, Game>(fun (db: ExcitementDbContext) -> db.Games.Where(fun g -> g.HomeScore.HasValue))

let private compilesWithoutTheCopy () =
    EF.CompileQuery<ExcitementDbContext, Game>(fun (db: ExcitementDbContext) ->
        db.Games.Where(fun g -> g.HomeScore <> Nullable()))

[<Fact>]
let ``the scan checks lambdas nested inside a checked lambda`` () =
    use db = npgsqlContext ()
    let rejected =
        Assert.Throws<InvalidOperationException>(fun () -> (filtersOnAnotherTable db).ToQueryString() |> ignore)

    Assert.IsType<NotSupportedException>(rejected.InnerException) |> ignore

    for name in [ nameof filtersOnAnotherTable; nameof compilesTheStructCopy ] do
        let report = Assert.Single(problems (controlSites name))
        Assert.True(report.StartsWith blockedHelp, report)

    for name in [ nameof filtersOnAnotherTableWithoutTheCopy; nameof compilesWithoutTheCopy ] do
        let site = Assert.Single(controlSites name)
        Assert.Equal({ Blocks = 0; Expanded = 1; Hidden = [] }, treeScan site)
        Assert.Empty(problems [ site ])

let private passesAPredicateItCannotBuild (db: ExcitementDbContext) (scored: Expression<Func<OddsSnapshot, bool>>) =
    db.Games.Where(fun g -> db.OddsSnapshots.Any(scored))

[<Fact>]
let ``the scan reports a tree it cannot build instead of passing it`` () =
    let report = Assert.Single(problems (controlSites (nameof passesAPredicateItCannotBuild)))
    Assert.StartsWith(hiddenTreeHelp, report)
    Assert.DoesNotContain("Hoist", report)

let private correlatesADbSet (db: ExcitementDbContext) =
    db.Games.Where(fun g -> db.OddsSnapshots.Any(fun o -> o.GameId = g.Id))

let private correlatesANavigation (db: ExcitementDbContext) =
    db.Games.Where(fun g -> g.OddsSnapshots.Any(fun o -> o.DrawMoneyline <> Nullable()))

let private correlatesAJoin (db: ExcitementDbContext) =
    db.Games.Join(db.OddsSnapshots, (fun g -> g.Id), (fun o -> o.GameId), (fun g o -> g.Id))

[<Fact>]
let ``EF Core translates a correlated subquery through a navigation or a join but rejects the DbSet form`` () =
    use db = npgsqlContext ()

    let navigation = (correlatesANavigation db).ToQueryString()
    Assert.Contains("WHERE EXISTS", navigation)
    Assert.Contains("o.game_id", navigation)

    Assert.Contains("INNER JOIN odds_snapshots", (correlatesAJoin db).ToQueryString())

    Assert.Throws<InvalidCastException>(fun () -> (correlatesADbSet db).ToQueryString() |> ignore)
    |> ignore

let private reasonLines (report: string) = report.Split('\n') |> Array.skip 1

[<Fact>]
let ``the scan sends a correlated DbSet subquery to a navigation or a join`` () =
    let report = Assert.Single(problems (controlSites (nameof correlatesADbSet)))
    Assert.Contains("g.OddsSnapshots.Any(", report)
    Assert.Contains("join", report)
    Assert.DoesNotContain("lambda literal", report)
    Assert.DoesNotContain("Hoist", report)

[<Fact>]
let ``the scan reports each refused subquery on one bounded line`` () =
    for name in [ nameof correlatesADbSet; nameof passesAPredicateItCannotBuild ] do
        let line = Assert.Single(reasonLines (Assert.Single(problems (controlSites name))))
        Assert.True(line.Length <= 200, line)
        Assert.DoesNotContain("NewDelegate", line)
