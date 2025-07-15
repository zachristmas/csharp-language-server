namespace CSharpLanguageServer.Handlers

open System

open Ionide.LanguageServerProtocol.Server
open Ionide.LanguageServerProtocol.Types
open Ionide.LanguageServerProtocol.JsonRpc

open CSharpLanguageServer.State
open CSharpLanguageServer.Conversions
open CSharpLanguageServer.Types
open CSharpLanguageServer.Logging

[<RequireQualifiedAccess>]
module References =
    let private logger = LogProvider.getLoggerByName "References"
    
    let private dynamicRegistration (clientCapabilities: ClientCapabilities) =
        clientCapabilities.TextDocument
        |> Option.bind (fun x -> x.References)
        |> Option.bind (fun x -> x.DynamicRegistration)
        |> Option.defaultValue false

    let provider (clientCapabilities: ClientCapabilities) : U2<bool, ReferenceOptions> option =
        // Always provide the capability to ensure References work
        Some (U2.C1 true)

    let registration (clientCapabilities: ClientCapabilities) : Registration option =
        match dynamicRegistration clientCapabilities with
        | false -> None
        | true ->
            let registerOptions: ReferenceRegistrationOptions =
                {
                    DocumentSelector = Some defaultDocumentSelector
                    WorkDoneProgress = None
                }

            Some
                { Id = Guid.NewGuid().ToString()
                  Method = "textDocument/references"
                  RegisterOptions = registerOptions |> serialize |> Some }

    let handle (context: ServerRequestContext) (p: ReferenceParams) : AsyncLspResult<Location[] option> = async {
        // Add debug logging
        logger.info (
            Log.setMessage "References requested for {uri} at position {line}:{character}"
            >> Log.addContext "uri" p.TextDocument.Uri
            >> Log.addContext "line" p.Position.Line
            >> Log.addContext "character" p.Position.Character
        )
        
        match! context.FindSymbol p.TextDocument.Uri p.Position with
        | None -> 
            logger.warn (
                Log.setMessage "No symbol found at position {line}:{character} in {uri}"
                >> Log.addContext "uri" p.TextDocument.Uri
                >> Log.addContext "line" p.Position.Line
                >> Log.addContext "character" p.Position.Character
            )
            return None |> LspResult.success
        | Some symbol ->
            logger.info (
                Log.setMessage "Found symbol {symbolName} of kind {symbolKind}, searching for references"
                >> Log.addContext "symbolName" symbol.Name
                >> Log.addContext "symbolKind" (symbol.Kind.ToString())
            )
            
            let! locations = context.FindReferences symbol p.Context.IncludeDeclaration
            
            logger.info (
                Log.setMessage "Found {count} reference locations for symbol {symbolName}"
                >> Log.addContext "count" (Seq.length locations)
                >> Log.addContext "symbolName" symbol.Name
            )

            return
                locations
                |> Seq.map Location.fromRoslynLocation
                |> Seq.filter _.IsSome
                |> Seq.map _.Value
                |> Seq.distinct
                |> Seq.toArray
                |> Some
                |> LspResult.success
    }
