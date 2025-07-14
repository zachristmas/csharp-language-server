module CSharpLanguageServer.State.ServerState

open System
open System.IO
open System.Threading
open System.Threading.Tasks

open Microsoft.CodeAnalysis
open Ionide.LanguageServerProtocol.Types
open Ionide.LanguageServerProtocol

open CSharpLanguageServer.RoslynHelpers
open CSharpLanguageServer.Types
open CSharpLanguageServer.Logging
open CSharpLanguageServer.Conversions
open CSharpLanguageServer.Util

type DecompiledMetadataDocument = {
    Metadata: CSharpMetadataInformation
    Document: Document
}

type ServerRequestType = ReadOnly | ReadWrite

type SolutionInfo = {
    Solution: Solution
    SolutionPath: string
    LastAccessed: DateTime
    ProjectPaths: Set<string>
}

type ServerOpenDocInfo =
    {
        Version: int
        Touched: DateTime
        SolutionPath: string option  // Track which solution this document belongs to
    }

type ServerRequest = {
    Id: int
    Name: string
    Type: ServerRequestType
    Semaphore: SemaphoreSlim
    Priority: int // 0 is the highest priority, 1 is lower prio, etc.
                  // priority is used to order pending R/O requests and is ignored wrt R/W requests
    Enqueued: DateTime
}
and ServerState = {
    Settings: ServerSettings
    RootPath: string
    LspClient: ILspClient option
    ClientCapabilities: ClientCapabilities
    Solution: Solution option  // Keep for backward compatibility
    Solutions: Map<string, SolutionInfo>  // Map of solution path to solution info
    OpenDocs: Map<string, ServerOpenDocInfo>
    DecompiledMetadata: Map<string, DecompiledMetadataDocument>
    LastRequestId: int
    RunningRequests: Map<int, ServerRequest>
    RequestQueue: ServerRequest list
    SolutionReloadPending: DateTime option
    PushDiagnosticsDocumentBacklog: string list
    PushDiagnosticsCurrentDocTask: (string * Task) option
}

let pullFirstRequestMaybe requestQueue =
    match requestQueue with
    | [] -> (None, [])
    | (firstRequest :: queueRemainder) -> (Some firstRequest, queueRemainder)

let pullNextRequestMaybe requestQueue =
    match requestQueue with
    | [] -> (None, requestQueue)

    | nonEmptyRequestQueue ->
        let requestIsReadOnly r = r.Type = ServerRequestType.ReadOnly

        // here we will try to take non-interrupted r/o request sequence at the front,
        // order it by priority and run the most prioritized one first
        let nextRoRequestByPriorityMaybe =
            nonEmptyRequestQueue
            |> Seq.takeWhile requestIsReadOnly
            |> Seq.sortBy (fun r -> r.Priority)
            |> Seq.tryHead

        // otherwise, if no r/o request by priority was found then we should just take the first request
        let nextRequest =
            nextRoRequestByPriorityMaybe
            |> Option.defaultValue (nonEmptyRequestQueue |> Seq.head)

        let queueRemainder =
            nonEmptyRequestQueue |> List.except [nextRequest]

        (Some nextRequest, queueRemainder)

let emptyServerState = { Settings = ServerSettings.Default
                         RootPath = Directory.GetCurrentDirectory()
                         LspClient = None
                         ClientCapabilities = emptyClientCapabilities
                         Solution = None
                         Solutions = Map.empty
                         OpenDocs = Map.empty
                         DecompiledMetadata = Map.empty
                         LastRequestId = 0
                         RunningRequests = Map.empty
                         RequestQueue = []
                         SolutionReloadPending = None
                         PushDiagnosticsDocumentBacklog = []
                         PushDiagnosticsCurrentDocTask = None }


type ServerDocumentType =
     | UserDocument // user Document from solution, on disk
     | DecompiledDocument // Document decompiled from metadata, readonly
     | AnyDocument


type ServerStateEvent =
    | SettingsChange of ServerSettings
    | RootPathChange of string
    | ClientChange of ILspClient option
    | ClientCapabilityChange of ClientCapabilities
    | SolutionChange of Solution
    | SolutionAdd of string * Solution  // Add a new solution with its path
    | SolutionRemove of string  // Remove a solution by path
    | SolutionAccessUpdate of string * DateTime  // Update last accessed time
    | LazyLoadSolutionForDocument of string  // Load solution for a specific document URI
    | DecompiledMetadataAdd of string * DecompiledMetadataDocument
    | OpenDocAdd of string * int * DateTime
    | OpenDocRemove of string
    | OpenDocTouch of string * DateTime
    | GetState of AsyncReplyChannel<ServerState>
    | GetDocumentOfTypeForUri of ServerDocumentType * string * AsyncReplyChannel<Document option>
    | StartRequest of string * ServerRequestType * int * AsyncReplyChannel<int * SemaphoreSlim>
    | FinishRequest of int
    | ProcessRequestQueue
    | SolutionReloadRequest of TimeSpan
    | PushDiagnosticsDocumentBacklogUpdate
    | PushDiagnosticsProcessPendingDocuments
    | PushDiagnosticsDocumentDiagnosticsResolution of Result<(string * int option * Diagnostic array), Exception>
    | PeriodicTimerTick


let findSolutionPathForDocument (rootPath: string) (documentPath: string): string option =
    let rec searchForSolution currentDir =
        if String.IsNullOrEmpty(currentDir) || not (currentDir.StartsWith(rootPath)) then
            None
        else
            let solutionFiles = 
                [ "*.sln"; "*.slnx" ]
                |> List.collect(fun pattern -> 
                    try
                        Directory.GetFiles(currentDir, pattern) |> List.ofArray
                    with
                    | _ -> [])
            
            match solutionFiles with
            | [solutionFile] -> Some solutionFile
            | _ ->
                let parentDir = Path.GetDirectoryName(currentDir)
                if parentDir = currentDir then
                    None
                else
                    searchForSolution parentDir
    
    let documentDir = Path.GetDirectoryName(documentPath)
    searchForSolution documentDir

let getDocumentForUriOfType state docType (u: string) =
    let uri = Uri(u.Replace("%3A", ":", true, null))

    // First check the main solution for backward compatibility
    let mainSolutionDoc = 
        match state.Solution with
        | Some solution ->
            let matchingUserDocuments =
                solution.Projects
                |> Seq.collect (fun p -> p.Documents)
                |> Seq.filter (fun d -> Uri(d.FilePath, UriKind.Absolute) = uri) |> List.ofSeq

            match matchingUserDocuments with
            | [d] -> Some (d, UserDocument)
            | _ -> None
        | None -> None

    // Then check all lazy-loaded solutions
    let lazyLoadedSolutionDoc =
        if Option.isNone mainSolutionDoc then
            state.Solutions
            |> Map.toSeq
            |> Seq.map snd
            |> Seq.tryPick (fun solutionInfo ->
                let matchingUserDocuments =
                    solutionInfo.Solution.Projects
                    |> Seq.collect (fun p -> p.Documents)
                    |> Seq.filter (fun d -> Uri(d.FilePath, UriKind.Absolute) = uri) |> List.ofSeq

                match matchingUserDocuments with
                | [d] -> Some (d, UserDocument)
                | _ -> None)
        else 
            None

    let userDocumentMaybe = mainSolutionDoc |> Option.orElse lazyLoadedSolutionDoc

    let matchingDecompiledDocumentMaybe =
        Map.tryFind u state.DecompiledMetadata
        |> Option.map (fun x -> (x.Document, DecompiledDocument))

    match docType with
    | UserDocument -> userDocumentMaybe
    | DecompiledDocument -> matchingDecompiledDocumentMaybe
    | AnyDocument -> userDocumentMaybe |> Option.orElse matchingDecompiledDocumentMaybe

let processServerEvent (logger: ILog) state postSelf msg : Async<ServerState> = async {
    match msg with
    | SettingsChange newSettings ->
        let newState: ServerState = { state with Settings = newSettings }

        let solutionChanged = not (state.Settings.SolutionPath = newState.Settings.SolutionPath)

        if solutionChanged then
            postSelf (SolutionReloadRequest (TimeSpan.FromMilliseconds(250)))

        return newState

    | GetState replyChannel ->
        replyChannel.Reply(state)
        return state

    | GetDocumentOfTypeForUri (docType, uri, replyChannel) ->
        let documentAndTypeMaybe = getDocumentForUriOfType state docType uri
        replyChannel.Reply(documentAndTypeMaybe |> Option.map fst)

        return state

    | StartRequest (name, requestType, requestPriority, replyChannel) ->
        postSelf ProcessRequestQueue

        let newRequest = { Id=state.LastRequestId+1
                           Name=name
                           Type=requestType
                           Semaphore=new SemaphoreSlim(0, 1)
                           Priority=requestPriority
                           Enqueued=DateTime.Now }

        replyChannel.Reply((newRequest.Id, newRequest.Semaphore))

        return { state with LastRequestId=newRequest.Id
                            RequestQueue=state.RequestQueue @ [newRequest] }

    | FinishRequest requestId ->
        let request = state.RunningRequests |> Map.tryFind requestId
        match request with
        | Some(request) ->
            request.Semaphore.Dispose()
            let newRunningRequests = state.RunningRequests |> Map.remove requestId
            let newState = { state with RunningRequests = newRunningRequests }

            postSelf ProcessRequestQueue
            return newState
        | None ->
            logger.debug (
                Log.setMessage "serverEventLoop/FinishRequest#{requestId}: request not found in state.RunningRequests"
                >> Log.addContext "requestId" (requestId |> string)
            )
            return state

    | ProcessRequestQueue ->
        let runningRWRequestMaybe =
            state.RunningRequests
            |> Seq.map (fun kv -> kv.Value)
            |> Seq.tryFind (fun r -> r.Type = ReadWrite)

        // let numRunningRequests = state.RunningRequests |> Map.count

        let canRunNextRequest =
            (Option.isNone runningRWRequestMaybe) // && (numRunningRequests < 4)

        return
            if not canRunNextRequest then
                state // block until current ReadWrite request is finished
            else
                let (nextRequestMaybe, queueRemainder) = pullNextRequestMaybe state.RequestQueue

                match nextRequestMaybe with
                | None -> state
                | Some nextRequest ->
                    // try to process next msg from the remainder, if possible, later
                    postSelf ProcessRequestQueue

                    let newState = { state with RequestQueue = queueRemainder
                                                RunningRequests = state.RunningRequests |> Map.add nextRequest.Id nextRequest }

                    // unblock this request to run by sending it current state
                    nextRequest.Semaphore.Release() |> ignore

                    newState

    | RootPathChange rootPath ->
        return { state with RootPath = rootPath }

    | ClientChange lspClient ->
        return { state with LspClient = lspClient }

    | ClientCapabilityChange cc ->
        return { state with ClientCapabilities = cc }

    | SolutionChange s ->
        postSelf PushDiagnosticsDocumentBacklogUpdate
        return { state with Solution = Some s }

    | SolutionAdd (solutionPath, solution) ->
        let projectPaths = 
            solution.Projects
            |> Seq.map (fun p -> p.FilePath)
            |> Set.ofSeq

        let solutionInfo = {
            Solution = solution
            SolutionPath = solutionPath
            LastAccessed = DateTime.Now
            ProjectPaths = projectPaths
        }

        let newSolutions = state.Solutions |> Map.add solutionPath solutionInfo
        postSelf PushDiagnosticsDocumentBacklogUpdate
        return { state with Solutions = newSolutions }

    | SolutionRemove solutionPath ->
        let newSolutions = state.Solutions |> Map.remove solutionPath
        return { state with Solutions = newSolutions }

    | SolutionAccessUpdate (solutionPath, accessTime) ->
        match state.Solutions |> Map.tryFind solutionPath with
        | Some solutionInfo ->
            let updatedSolutionInfo = { solutionInfo with LastAccessed = accessTime }
            let newSolutions = state.Solutions |> Map.add solutionPath updatedSolutionInfo
            return { state with Solutions = newSolutions }
        | None ->
            return state

    | LazyLoadSolutionForDocument documentUri ->
        let docFilePathMaybe = tryParseFileUri documentUri
        match docFilePathMaybe with
        | Some docFilePath ->
            let solutionPathMaybe = findSolutionPathForDocument state.RootPath docFilePath
            match solutionPathMaybe with
            | Some solutionPath when not (state.Solutions.ContainsKey solutionPath) ->
                // Load the solution asynchronously
                async {
                    match state.LspClient with
                    | Some lspClient ->
                        let! solutionMaybe = tryLoadSolutionOnPath lspClient logger solutionPath
                        match solutionMaybe with
                        | Some solution ->
                            postSelf (SolutionAdd (solutionPath, solution))
                        | None -> ()
                    | None -> ()
                } |> Async.Start
                return state
            | _ ->
                return state
        | None ->
            return state

    | DecompiledMetadataAdd (uri, md) ->
        let newDecompiledMd = Map.add uri md state.DecompiledMetadata
        return { state with DecompiledMetadata = newDecompiledMd }

    | OpenDocAdd (doc, ver, timestamp) ->
        postSelf PushDiagnosticsDocumentBacklogUpdate

        // Try to find which solution this document belongs to
        let docFilePathMaybe = tryParseFileUri doc
        let solutionPathMaybe = 
            match docFilePathMaybe with
            | Some docFilePath ->
                // First check if document exists in any loaded solution
                let existingDoc = getDocumentForUriOfType state UserDocument doc
                match existingDoc with
                | Some _ ->
                    // Document is already in a loaded solution, find which one
                    state.Solutions
                    |> Map.tryPick (fun solutionPath solutionInfo ->
                        let hasDocument = 
                            solutionInfo.Solution.Projects
                            |> Seq.collect (fun p -> p.Documents)
                            |> Seq.exists (fun d -> d.FilePath = docFilePath)
                        if hasDocument then Some solutionPath else None)
                | None ->
                    // Document not in any loaded solution, try to find solution to load
                    let solutionPath = findSolutionPathForDocument state.RootPath docFilePath
                    match solutionPath with
                    | Some path when not (state.Solutions.ContainsKey path) ->
                        // Trigger lazy loading
                        postSelf (LazyLoadSolutionForDocument doc)
                        solutionPath
                    | _ -> solutionPath
            | None -> None

        let openDocInfo = { 
            Version = ver
            Touched = timestamp 
            SolutionPath = solutionPathMaybe
        }
        
        let newOpenDocs = state.OpenDocs |> Map.add doc openDocInfo
        return { state with OpenDocs = newOpenDocs }

    | OpenDocRemove uri ->
        postSelf PushDiagnosticsDocumentBacklogUpdate

        let newOpenDocVersions = state.OpenDocs |> Map.remove uri

        return { state with OpenDocs = newOpenDocVersions }

    | OpenDocTouch (uri, timestamp) ->
        postSelf PushDiagnosticsDocumentBacklogUpdate

        let openDocInfo = state.OpenDocs |> Map.tryFind uri
        match openDocInfo with
        | None ->
            return state
        | Some openDocInfo ->
            let updatedOpenDocInfo = { openDocInfo with Touched = timestamp }
            let newOpenDocVersions = state.OpenDocs |> Map.add uri updatedOpenDocInfo
            return { state with OpenDocs = newOpenDocVersions }

    | SolutionReloadRequest reloadNoLaterThanIn ->
        // we need to wait a bit before starting this so we
        // can buffer many incoming requests at once
        let newSolutionReloadDeadline =
            let suggestedDeadline = DateTime.Now + reloadNoLaterThanIn

            match state.SolutionReloadPending with
            | Some currentDeadline ->
                if (suggestedDeadline < currentDeadline) then suggestedDeadline else currentDeadline
            | None -> suggestedDeadline

        return { state with SolutionReloadPending = newSolutionReloadDeadline |> Some }

    | PushDiagnosticsDocumentBacklogUpdate ->
        // here we build new backlog for background diagnostics processing
        // which will consider documents by their last modification date
        // for processing first
        let newBacklog =
            state.OpenDocs
            |> Seq.sortByDescending (fun kv -> kv.Value.Touched)
            |> Seq.map (fun kv -> kv.Key)
            |> List.ofSeq

        return { state with PushDiagnosticsDocumentBacklog = newBacklog }

    | PushDiagnosticsProcessPendingDocuments ->
        match state.PushDiagnosticsCurrentDocTask with
        | Some _ ->
            // another document is still being processed, do nothing
            return state
        | None ->
            // try pull next doc from the backlog to process
            let nextDocUri, newBacklog =
                match state.PushDiagnosticsDocumentBacklog with
                | [] -> (None, [])
                | uri :: remainder -> (Some uri, remainder)

            // push diagnostic is enabled only if pull diagnostics is
            // not reported to be supported by the client
            let diagnosticPullSupported =
                state.ClientCapabilities.TextDocument
                |> Option.map _.Diagnostic
                |> Option.map _.IsSome
                |> Option.defaultValue false

            match diagnosticPullSupported, nextDocUri with
            | false, Some docUri ->
                let newState = { state with PushDiagnosticsDocumentBacklog = newBacklog }

                let docAndTypeMaybe = docUri |> getDocumentForUriOfType state ServerDocumentType.AnyDocument
                match docAndTypeMaybe with
                | None ->
                    // could not find document for this enqueued uri
                    logger.debug (
                        Log.setMessage "PushDiagnosticsProcessPendingDocuments: could not find document w/ uri \"{docUri}\""
                        >> Log.addContext "docUri" (string docUri)
                    )
                    return newState

                | Some (doc, _docType) ->
                    let resolveDocumentDiagnostics (): Task = task {
                        let! semanticModelMaybe = doc.GetSemanticModelAsync()
                        match semanticModelMaybe |> Option.ofObj with
                        | None -> Error (Exception("could not GetSemanticModelAsync"))
                                  |> PushDiagnosticsDocumentDiagnosticsResolution
                                  |> postSelf

                        | Some semanticModel ->
                            let diagnostics =
                                semanticModel.GetDiagnostics()
                                |> Seq.map Diagnostic.fromRoslynDiagnostic
                                |> Array.ofSeq

                            Ok (docUri, None, diagnostics)
                            |> PushDiagnosticsDocumentDiagnosticsResolution
                            |> postSelf
                    }

                    let newTask = Task.Run(resolveDocumentDiagnostics)

                    let newState = { newState with PushDiagnosticsCurrentDocTask = Some (docUri, newTask)  }

                    return newState

            | _, _->
                // backlog is empty or pull diagnostics is enabled instead,--nothing to do
                return state

    | PushDiagnosticsDocumentDiagnosticsResolution result ->
        // enqueue processing for the next doc on the queue (if any)
        postSelf PushDiagnosticsProcessPendingDocuments

        let newState = { state with PushDiagnosticsCurrentDocTask = None }

        match result with
        | Error exn ->
            logger.debug (
                Log.setMessage "PushDiagnosticsDocumentDiagnosticsResolution: {exn}"
                >> Log.addContext "exn" (exn)
            )
            return newState

        | Ok (docUri, version, diagnostics) ->
            match state.LspClient with
            | None ->
                return newState

            | Some lspClient ->
                let resolvedDocumentDiagnostics =
                    { Uri = docUri
                      Version = version
                      Diagnostics = diagnostics }

                do! lspClient.TextDocumentPublishDiagnostics(resolvedDocumentDiagnostics)
                return newState

    | PeriodicTimerTick ->
        postSelf PushDiagnosticsProcessPendingDocuments

        let solutionReloadTime = state.SolutionReloadPending
                                 |> Option.defaultValue (DateTime.Now.AddDays(1))

        match solutionReloadTime < DateTime.Now with
        | true ->
            match state.Settings.SolutionPath with
            | Some _ ->
                // Only reload if a solution was explicitly configured
                let! newSolution =
                    loadSolutionOnSolutionPathOrDir
                        state.LspClient.Value
                        logger
                        state.Settings.SolutionPath
                        state.RootPath

                return { state with Solution = newSolution
                                    SolutionReloadPending = None }
            | None ->
                // No explicit solution configured, rely on lazy loading
                return { state with SolutionReloadPending = None }

        | false ->
            return state
}

let serverEventLoop initialState (inbox: MailboxProcessor<ServerStateEvent>) =
    let logger = LogProvider.getLoggerByName "serverEventLoop"

    let rec loop state = async {
        let! msg = inbox.Receive()

        try
            let! newState = msg |> processServerEvent logger state inbox.Post
            return! loop newState
        with ex ->
            logger.debug (
                Log.setMessage "serverEventLoop: crashed with {exception}"
                >> Log.addContext "exception" (string ex)
            )
            raise ex
    }

    loop initialState

type ServerSettingsDto = {
     csharp: ServerSettingsCSharpDto option
}
and ServerSettingsCSharpDto =
    {
        solution: string option
        applyFormattingOptions: bool option
    }
    static member Default = {
        solution = None
        applyFormattingOptions = None
    }
