namespace Ionide.VSCode.FSharp

open System
open System.Text.RegularExpressions
open Fable.Core
open Fable.Core.JsInterop
open Fable.Import
open Fable.Import.vscode
open Fable.Import.Node

open DTO
open Ionide.VSCode.Helpers

module Expecto =
    let private outputChannel = window.createOutputChannel "Expecto"
    let mutable private lastOutput = System.Collections.Generic.Dictionary<string,string>()
    let private logger = ConsoleAndOutputChannelLogger(Some "Expecto", Level.DEBUG, None, Some Level.DEBUG)
    let mutable private watcherEnabled = false
    let private statusBar = window.createStatusBarItem(2 |> unbox, 100.)

    let private trySkip count seq =
        let mutable index = 0
        seq
        |> Seq.skipWhile (fun _ ->
            index <- index + 1
            index <= count)

    let private getAutoshow () =
        let cfg = vscode.workspace.getConfiguration()
        cfg.get ("Expecto.autoshow", true)

    let private getSequenced () =
        let cfg = vscode.workspace.getConfiguration()
        cfg.get ("Expecto.runSequenced", false)

    let private getDebug () =
        let cfg = vscode.workspace.getConfiguration()
        cfg.get ("Expecto.runDebug", false)

    let private getVersion () =
        let cfg = vscode.workspace.getConfiguration()
        cfg.get ("Expecto.runVersion", false)

    let private getFailOnFocusedTests () =
        let cfg = vscode.workspace.getConfiguration()
        cfg.get ("Expecto.runFailOnFocusedTests", false)

    let private getCustomArgs () =
        let cfg = vscode.workspace.getConfiguration()
        cfg.get ("Expecto.customArgs", "")

    let private isANetCoreAppProject (project:Project) =
        let projectContent = (fs.readFileSync project.Project).ToString()
        let netCoreTargets =
            [ "<TargetFramework>netcoreapp" ]

        let findInProject (toFind:string) =
            projectContent.IndexOf(toFind) >= 0

        netCoreTargets |> Seq.exists findInProject

    let private getExpectoProjects () =
        Project.getLoaded ()
        |> Seq.where (fun p ->
            p.References |> List.exists ( String.endWith "Expecto.dll" )  )
        |> Seq.toList

    let execWithDotnet cmd =
        promise {
            let! dotnet = Environment.dotnet
            logger.Debug ("%s %s", dotnet, cmd)
            return Process.spawnWithNotification dotnet dotnet cmd outputChannel
        }

    let exec exe cmd =
        promise {
            return Process.spawnWithNotification exe "mono" cmd outputChannel
        }

    let private getExpectoLauncher () =
        getExpectoProjects ()
        |> List.choose ( fun project ->
            let execDotnet = fun args ->
                let cmd = "run -p " + project.Project + " -- " + args
                execWithDotnet cmd
            match project.Output, isANetCoreAppProject project with
            | null, true
            | "null", true -> Some (execDotnet, project.Project)
            | out, _ when out |> String.endWith ".exe" -> Some ((fun args -> exec out args), out)
            | _ -> None
        )

    let getFilesToWatch () =
        let projects = Project.getLoaded () |> Seq.toArray
        let getProject (ref : string) = projects |> Array.tryFind (fun p -> p.Output.ToLower() = ref.ToLower())

        let rec loop p =
            [
                yield p.Project
                yield! p.Files
                yield! p.References |> List.collect (fun ref ->
                    match getProject ref with
                    | None -> [ref]
                    | Some proj -> loop proj)
            ]

        getExpectoProjects ()
        |> List.collect loop
        |> List.distinct
        |> List.toArray

    let private handleExpectoList (output : string) =
        if(output = "") then
            [||]
        else
            output.Split('\n')
            |> Array.filter((<>) "")
            |> Array.map (fun n -> n.Trim())

    let parseTestSummaryRecord (n : string) =
        let split = n.Split ('[')
        let loc = split.[1] |> String.replace "]" ""
        split.[0], loc

    let private getFailed () =
        printfn "last output: %O" lastOutput
        try
            lastOutput
            |> Seq.collect (fun kv ->
                kv.Value.Split('\n')
                |> Seq.map(String.trim)
                |> Seq.skipWhile (not << String.startWith "Failed:")
                |> Seq.filter (not << String.startWith "Failed:")
                |> Seq.filter (not << String.startWith "Errored:")
                |> Seq.filter (not << String.IsNullOrWhiteSpace)
                |> Seq.map (parseTestSummaryRecord)
            )
            |> Seq.map(fun (n,loc) -> if n.Contains " " then sprintf "\"%s\"" n,loc else n,loc)
            |> Seq.toArray
        with
        | _ -> [||]

    let private getPassed () =
        try
            lastOutput
            |> Seq.collect (fun kv ->
                kv.Value.Split('\n')
                |> Seq.map(String.trim)
                |> Seq.skipWhile (not << String.startWith "Passed:")
                |> Seq.takeWhile (not << String.startWith "Ignored:")
                |> trySkip 1
                |> Seq.map (parseTestSummaryRecord)
            )
            |> Seq.map(fun (n,loc) -> if n.Contains " " then sprintf "\"%s\"" n,loc else n,loc)
            |> Seq.toArray
        with
        | _ -> [||]

    let private getIgnored () =
        try
            lastOutput
            |> Seq.collect (fun kv ->
                kv.Value.Split('\n')
                |> Seq.map(String.trim)
                |> Seq.skipWhile (not << String.startWith "Ignored:")
                |> Seq.takeWhile (not << String.startWith "Failed:")
                |> trySkip 1
                |> Seq.map (parseTestSummaryRecord)
            )
            |> Seq.map(fun (n,loc) -> if n.Contains " " then sprintf "\"%s\"" n,loc else n,loc)
            |> Seq.toArray
        with
        | _ -> [||]

    let failedDecorationType =
        let opt = createEmpty<DecorationRenderOptions>
        let file = "testFailed.png"
        let path =
            try
                (VSCode.getPluginPath "Ionide.ionide-fsharp") + "/images/" + file |> Uri.file
            with
            | _ ->  (VSCode.getPluginPath "Ionide.Ionide-fsharp") + "/images/" + file |> Uri.file
        opt.gutterIconPath <- unbox path
        opt.overviewRulerLane <- Some OverviewRulerLane.Full
        opt.overviewRulerColor <- Some "rgba(224, 64, 6, 0.7)"
        window.createTextEditorDecorationType opt

    let passedDecorationType =
        let opt = createEmpty<DecorationRenderOptions>
        let file = "testPassed.png"
        let path =
            try
                (VSCode.getPluginPath "Ionide.ionide-fsharp") + "/images/" + file |> Uri.file
            with
            | _ ->  (VSCode.getPluginPath "Ionide.Ionide-fsharp") + "/images/" + file |> Uri.file
        opt.gutterIconPath <- unbox path
        opt.overviewRulerLane <- Some OverviewRulerLane.Full
        opt.overviewRulerColor <- Some "rgba(166, 215, 133, 0.7)"
        window.createTextEditorDecorationType opt

    let ignoredDecorationType =
        let opt = createEmpty<DecorationRenderOptions>
        let file = "testIgnored.png"
        let path =
            try
                (VSCode.getPluginPath "Ionide.ionide-fsharp") + "/images/" + file |> Uri.file
            with
            | _ ->  (VSCode.getPluginPath "Ionide.Ionide-fsharp") + "/images/" + file |> Uri.file
        opt.gutterIconPath <- unbox path
        opt.overviewRulerLane <- Some OverviewRulerLane.Full
        opt.overviewRulerColor <- Some "rgba(255, 188, 64, 0.7)"
        window.createTextEditorDecorationType opt


    let private setDecorations () =
        let transform =
            Array.choose ( fun (_, v) ->
                try
                    let split = String.split [|':'|] v
                    let leng = Array.length split
                    let fn = split.[0 .. leng-2] |> String.concat ":"
                    let line = split.[leng-1]
                    Some (fn,line)
                with
                | _ -> None )
            >> Array.groupBy (fst)
            >> Array.map (fun (k,vals) -> Uri.file k, vals |> Array.choose (fun (_, line) -> if int line > 1 then Some <| Range(float line - 1., 0., float line - 1., 0. ) else None))

        let get fn data  =
            try
                data
                |> Array.find(fun (k : Uri,_) -> (Uri.file fn).fsPath = k.fsPath)
                |> snd
                |> Seq.ofArray
                |> ResizeArray
            with
            | _ -> ResizeArray ()

        let failed fn = getFailed () |> transform |> get fn
        let passed fn = getPassed () |> transform |> get fn
        let ignored fn = getIgnored () |> transform |> get fn


        window.visibleTextEditors
        |> Seq.iter (fun te ->
            match te.document with
            | Document.FSharp ->
                let fld = failed te.document.fileName
                te.setDecorations(failedDecorationType, Case1 fld)

                let psd = passed te.document.fileName
                te.setDecorations(passedDecorationType, Case1 psd)

                let ign = ignored te.document.fileName
                te.setDecorations(ignoredDecorationType, Case1 ign)
            | _ -> ()
        )

        ()

    let private buildExpectoProjects watchMode =
        outputChannel.clear ()

        if getAutoshow () && not watchMode then outputChannel.show ()
        elif watchMode then statusBar.text <- "$(eye) Watch Mode - building"

        let buildWithMsbuild path =
            promise {
                let! msbuild = Environment.msbuild
                logger.Debug ("%s %s", msbuild, path)
                return! Process.spawnWithNotification msbuild "" path outputChannel
                |> Process.toPromise
            }

        let buildWithDotnet path =
            promise {
                let! childProcess = execWithDotnet ("build " + path)
                return!
                    childProcess
                    |> Process.toPromise
            }

        getExpectoProjects ()
        |> List.map(fun proj ->
            let path = proj.Project
            match isANetCoreAppProject proj with
            | true -> buildWithDotnet path
            | false -> buildWithMsbuild path
        )
        |> Promise.all
        |> Promise.bind (fun codes ->
            if codes |> Seq.exists ((<>) "0") then
                if not watchMode then
                    vscode.window.showErrorMessage("Build of Expecto tests failed", "Show")
                    |> Promise.map (fun n -> if n = "Show" then outputChannel.show () )
                    |> Promise.map (fun _ -> false)
                else
                    statusBar.text <- "$(eye) Watch Mode - building failed"
                    Promise.empty
            else
                Promise.lift true)

    let private runExpecto watchMode args =
        outputChannel.clear ()
        if getAutoshow () && not watchMode then outputChannel.show ()
        elif watchMode then statusBar.text <- "$(eye) Watch Mode - running"
        let args = if getSequenced () then args + " --sequenced" else args
        let args = if getDebug () then args + " --debug" else args
        let args = if getVersion () then args + " --version" else args
        let args = if getFailOnFocusedTests () then args + " --fail-on-focused-tests" else args
        let customArgs = getCustomArgs ()
        let args = if customArgs <> "" then customArgs + " " + args else args


        buildExpectoProjects watchMode
        |> Promise.bind (fun res ->
            if res then
                lastOutput.Clear()
                getExpectoLauncher ()
                |> List.map(fun (executor,exe) ->
                    lastOutput.[exe] <- ""
                    promise {
                        let! childProcess = executor args
                        return!
                            childProcess
                            |> Process.onOutput (fun out -> lastOutput.[exe] <- lastOutput.[exe] + out.ToString () )
                            |> Process.toPromise
                    })
                |> Promise.all
                |> Promise.bind (fun codes ->
                    setDecorations ()
                    if codes |> Seq.exists ((<>) "0") then
                        if not watchMode then
                            vscode.window.showErrorMessage("Expecto tests failed", "Show")
                            |> Promise.map (fun n -> if n = "Show" then outputChannel.show () )
                        else
                            let failed = getFailed () |> Seq.toArray

                            statusBar.text <- (sprintf "$(eye) Watch Mode - %d tests failed" failed.Length)
                            Promise.empty
                    else
                        if watchMode then
                            statusBar.text <- "$(eye) Watch Mode - tests passed"
                        Promise.empty)
            else Promise.empty)

    let private runAll watchMode = runExpecto watchMode "--summary-location"

    let private getTestCases () =
        buildExpectoProjects false
        |> Promise.bind (fun res ->
            if res then
                getExpectoLauncher ()
                |> List.map(fun (executor,exe) ->
                    lastOutput.[exe] <- ""
                    promise {
                        let! childProcess = executor "--list-tests"
                        return!
                            childProcess
                            |> Process.onOutput (fun out -> lastOutput.[exe] <- lastOutput.[exe] + out.ToString () )
                            |> Process.toPromise
                            |> Promise.map (fun res -> res, exe)
                    })
                |> Promise.all
                |> Promise.map (Seq.collect (fun (res, exe) -> lastOutput.[exe] |> handleExpectoList) >> ResizeArray)
            else
                Promise.lift (ResizeArray()))


    let private getTestLists () =
        let getTestList (s : string) =
            let all = s.Split ('/')
            match all with
            | [||] -> ""
            | [|x|] -> ""
            | xs -> xs.[0 .. all.Length - 2] |> String.concat "/"

        getTestCases ()
        |> Promise.map (Seq.map (getTestList) >> Seq.distinct >> Seq.filter ((<>) "") >> ResizeArray)

    let private runSingle () =
        window.showQuickPick (Case2 (getTestCases() ))
        |> Promise.bind(fun n ->
            if JS.isDefined n then
                (sprintf "--run \"%s\"" n) |> runExpecto false
            else
                Promise.empty
        )

    let private runList () =
        window.showQuickPick (Case2 (getTestLists() ))
        |> Promise.bind(fun n ->
            if JS.isDefined n then
                (sprintf "--filter \"%s\" --summary-location" n) |> runExpecto false
            else
                Promise.empty
        )

    let private runFailed () =
        getFailed ()
        |> Seq.map fst
        |> String.concat " "
        |> sprintf "--run %s --summary-location"
        |> runExpecto false

    let private onFileChanged (uri : Uri) =
        let files = getFilesToWatch ()

        if watcherEnabled && files |> Array.exists (fun n -> uri.fsPath = n) then
            runAll true
        else
            Promise.empty

    let mutable watcher = None

    let private startWatchMode () =
        let w = workspace.createFileSystemWatcher("**/*.fs")
        watcher <- w |> Some
        let _ = w.onDidChange $ onFileChanged
        statusBar.text <- "$(eye) Watch Mode On"
        watcherEnabled <- true
        runAll true

    let private stopWatchMode () =
        watcher |> Option.iter (fun w -> w.dispose() |> ignore)
        watcher <- None
        statusBar.text <- "$(eye) Watch Mode Off"
        watcherEnabled <- false



    let activate disposables =
        let registerCommand com (f: unit-> _) =
            vscode.commands.registerCommand(com, unbox<Func<obj,obj>> f)
            |> ignore

        registerCommand "Expecto.run" (fun _ -> runAll false)
        registerCommand "Expecto.runSingle" runSingle
        registerCommand "Expecto.runList" runList
        registerCommand "Expecto.runFailed" runFailed
        registerCommand "Expecto.startWatchMode" startWatchMode
        registerCommand "Expecto.stopWatchMode" stopWatchMode
        registerCommand "Expecto.watchMode" (fun _ -> outputChannel.show () )


        statusBar.text <- "$(eye) Watch Mode Off"
        statusBar.tooltip <- "Expecto continuous testing"
        statusBar.command <- "Expecto.watchMode"
        statusBar.show ()

        let _ = vscode.window.onDidChangeActiveTextEditor $ setDecorations
        ()
