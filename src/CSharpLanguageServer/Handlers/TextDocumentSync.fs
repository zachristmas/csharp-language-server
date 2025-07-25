namespace CSharpLanguageServer.Handlers

open System

open Microsoft.CodeAnalysis.Text
open Ionide.LanguageServerProtocol.Server
open Ionide.LanguageServerProtocol.Types
open Ionide.LanguageServerProtocol.JsonRpc

open CSharpLanguageServer
open CSharpLanguageServer.Conversions
open CSharpLanguageServer.State
open CSharpLanguageServer.State.ServerState
open CSharpLanguageServer.Types
open CSharpLanguageServer.RoslynHelpers
open CSharpLanguageServer.Logging

[<RequireQualifiedAccess>]
module TextDocumentSync =
    let private logger = LogProvider.getLoggerByName "TextDocumentSync"

    let private applyLspContentChangesOnRoslynSourceText
            (changes: TextDocumentContentChangeEvent[])
            (initialSourceText: SourceText) =

        let applyLspContentChangeOnRoslynSourceText (sourceText: SourceText) (change: TextDocumentContentChangeEvent) =
            match change with
            | U2.C1 change ->
                let changeTextSpan =
                    change.Range
                    |> Range.toLinePositionSpan sourceText.Lines
                    |> sourceText.Lines.GetTextSpan

                TextChange(changeTextSpan, change.Text) |> sourceText.WithChanges
            | U2.C2 changeWoRange ->
                SourceText.From(changeWoRange.Text)

        changes |> Seq.fold applyLspContentChangeOnRoslynSourceText initialSourceText

    let private dynamicRegistration (clientCapabilities: ClientCapabilities) =
        clientCapabilities.TextDocument
        |> Option.bind (fun x -> x.Synchronization)
        |> Option.bind (fun x -> x.DynamicRegistration)
        |> Option.defaultValue false

    let provider (clientCapabilities: ClientCapabilities) : TextDocumentSyncOptions option =
        match dynamicRegistration clientCapabilities with
        | true -> None
        | false ->
            Some
                { TextDocumentSyncOptions.Default with
                    OpenClose = Some true
                    Save = Some (U2.C2 { IncludeText = Some true })
                    Change = Some TextDocumentSyncKind.Incremental }

    let didOpenRegistration (clientCapabilities: ClientCapabilities) : Registration option =
        match dynamicRegistration clientCapabilities with
        | false -> None
        | true ->
            let registerOptions = {
                DocumentSelector = Some defaultDocumentSelector }

            Some
                { Id = Guid.NewGuid().ToString()
                  Method = "textDocument/didOpen"
                  RegisterOptions = registerOptions |> serialize |> Some }


    let didChangeRegistration (clientCapabilities: ClientCapabilities) : Registration option =
        match dynamicRegistration clientCapabilities with
        | false -> None
        | true ->
            let registerOptions =
                { DocumentSelector = Some defaultDocumentSelector
                  SyncKind = TextDocumentSyncKind.Incremental }
            Some
                { Id = Guid.NewGuid().ToString()
                  Method = "textDocument/didChange"
                  RegisterOptions = registerOptions|> serialize |> Some }

    let didSaveRegistration (clientCapabilities: ClientCapabilities) : Registration option =
        match dynamicRegistration clientCapabilities with
        | false -> None
        | true ->
            let registerOptions =
                { DocumentSelector = Some defaultDocumentSelector
                  IncludeText = Some true }

            Some
                { Id = Guid.NewGuid().ToString()
                  Method = "textDocument/didSave"
                  RegisterOptions = registerOptions |> serialize |> Some }

    let didCloseRegistration (clientCapabilities: ClientCapabilities) : Registration option =
        match dynamicRegistration clientCapabilities with
        | false -> None
        | true ->
            let registerOptions = {
                DocumentSelector = Some defaultDocumentSelector }

            Some
                { Id = Guid.NewGuid().ToString()
                  Method = "textDocument/didClose"
                  RegisterOptions = registerOptions |> serialize |> Some }

    let willSaveRegistration (_clientCapabilities: ClientCapabilities) : Registration option = None

    let willSaveWaitUntilRegistration (_clientCapabilities: ClientCapabilities) : Registration option = None

    let didOpen (context: ServerRequestContext)
                (openParams: DidOpenTextDocumentParams)
            : Async<LspResult<unit>> =
        match context.GetDocumentForUriOfType AnyDocument openParams.TextDocument.Uri with
        | Some (doc, docType) ->
            match docType with
            | UserDocument ->
                // we want to load the document in case it has been changed since we have the solution loaded
                // also, as a bonus we can recover from corrupted document view in case document in roslyn solution
                // went out of sync with editor
                let updatedDoc = SourceText.From(openParams.TextDocument.Text) |> doc.WithText

                context.Emit(OpenDocAdd (openParams.TextDocument.Uri, openParams.TextDocument.Version, DateTime.Now))
                context.Emit(SolutionChange updatedDoc.Project.Solution)

                Ok() |> async.Return

            | _ ->
                Ok() |> async.Return

        | None ->
            let docFilePathMaybe = Util.tryParseFileUri openParams.TextDocument.Uri

            match docFilePathMaybe with
            | Some docFilePath -> async {
                // Trigger lazy loading first
                context.Emit(LazyLoadSolutionForDocument openParams.TextDocument.Uri)
                
                // Wait longer for the solution to potentially load
                // Check multiple times to see if loading has completed
                let rec waitForSolution attempts = async {
                    if attempts > 0 then
                        do! Async.Sleep(100)  // Wait 100ms between attempts
                        
                        // Check if document is now in a solution
                        match context.GetDocumentForUriOfType AnyDocument openParams.TextDocument.Uri with
                        | Some (doc, UserDocument) -> return Some doc
                        | _ -> return! waitForSolution (attempts - 1)
                    else
                        return None
                }
                
                // Try up to 10 times (1 second total)
                let! docMaybe = waitForSolution 10
                
                match docMaybe with
                | Some doc ->
                    let updatedDoc = SourceText.From(openParams.TextDocument.Text) |> doc.WithText
                    context.Emit(OpenDocAdd (openParams.TextDocument.Uri, openParams.TextDocument.Version, DateTime.Now))
                    context.Emit(SolutionChange updatedDoc.Project.Solution)
                | None ->
                    // Document still not in any solution after waiting, check if we should try to add it
                    let allSolutions = context.GetAllSolutions()
                    
                    // Only try to add if we have loaded solutions
                    if not (List.isEmpty allSolutions) then
                        let! newDocMaybe =
                            tryAddDocumentToAnySolution
                                logger
                                docFilePath
                                openParams.TextDocument.Text
                                allSolutions

                        match newDocMaybe with
                        | Some newDoc ->
                            context.Emit(OpenDocAdd (openParams.TextDocument.Uri, openParams.TextDocument.Version, DateTime.Now))
                            context.Emit(SolutionChange newDoc.Project.Solution)
                        | None -> 
                            // Still couldn't add document, but register it for tracking
                            context.Emit(OpenDocAdd (openParams.TextDocument.Uri, openParams.TextDocument.Version, DateTime.Now))
                    else
                        // No solutions loaded yet, just register the document
                        context.Emit(OpenDocAdd (openParams.TextDocument.Uri, openParams.TextDocument.Version, DateTime.Now))

                return Ok()
              }

            | None ->
                Ok() |> async.Return

    let didChange (context: ServerRequestContext)
                  (changeParams: DidChangeTextDocumentParams)
            : Async<LspResult<unit>> =
      async {
        let docMaybe = context.GetUserDocument changeParams.TextDocument.Uri
        match docMaybe with
        | None -> ()
        | Some doc ->
                let! ct = Async.CancellationToken
                let! sourceText = doc.GetTextAsync(ct) |> Async.AwaitTask
                //logMessage (sprintf "TextDocumentDidChange: changeParams: %s" (string changeParams))
                //logMessage (sprintf "TextDocumentDidChange: sourceText: %s" (string sourceText))

                let updatedSourceText = sourceText |> applyLspContentChangesOnRoslynSourceText changeParams.ContentChanges
                let updatedDoc = doc.WithText(updatedSourceText)

                //logMessage (sprintf "TextDocumentDidChange: newSourceText: %s" (string updatedSourceText))

                let updatedSolution = updatedDoc.Project.Solution

                context.Emit(SolutionChange updatedSolution)
                context.Emit(OpenDocAdd (changeParams.TextDocument.Uri, changeParams.TextDocument.Version, DateTime.Now))

        return Ok()
    }

    let didClose (context: ServerRequestContext)
                 (closeParams: DidCloseTextDocumentParams)
            : Async<LspResult<unit>> =
        context.Emit(OpenDocRemove closeParams.TextDocument.Uri)
        Ok() |> async.Return

    let willSave (_context: ServerRequestContext) (_p: WillSaveTextDocumentParams): Async<LspResult<unit>> = async {
        return Ok ()
    }

    let willSaveWaitUntil (_context: ServerRequestContext) (_p: WillSaveTextDocumentParams): AsyncLspResult<TextEdit [] option> = async {
        return LspResult.notImplemented<TextEdit [] option>
    }

    let didSave (context: ServerRequestContext)
                (saveParams: DidSaveTextDocumentParams)
            : Async<LspResult<unit>> =
        // we need to add this file to solution if not already
        let doc = context.GetDocument saveParams.TextDocument.Uri

        match doc with
        | Some _ ->
            Ok() |> async.Return

        | None -> async {
            let docFilePath = Util.parseFileUri saveParams.TextDocument.Uri
            let allSolutions = context.GetAllSolutions()
            let! newDocMaybe =
                tryAddDocumentToAnySolution
                    logger
                    docFilePath
                    saveParams.Text.Value
                    allSolutions

            match newDocMaybe with
            | Some newDoc ->
                context.Emit(OpenDocTouch (saveParams.TextDocument.Uri, DateTime.Now))
                context.Emit(SolutionChange newDoc.Project.Solution)

            | None -> ()

            return Ok()
          }
