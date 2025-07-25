namespace CSharpLanguageServer.Handlers

open System

open Ionide.LanguageServerProtocol.Server
open Ionide.LanguageServerProtocol.Types
open Ionide.LanguageServerProtocol.JsonRpc

open CSharpLanguageServer.Types
open CSharpLanguageServer.State
open CSharpLanguageServer.Util

[<RequireQualifiedAccess>]
module Implementation =
    let private dynamicRegistration (clientCapabilities: ClientCapabilities) =
        clientCapabilities.TextDocument
        |> Option.bind (fun x -> x.Implementation)
        |> Option.bind (fun x -> x.DynamicRegistration)
        |> Option.defaultValue false

    let provider (clientCapabilities: ClientCapabilities) : U3<bool,ImplementationOptions,ImplementationRegistrationOptions> option =
        // Always provide the capability to ensure Implementation works  
        Some (U3.C1 true)

    let registration (clientCapabilities: ClientCapabilities) : Registration option =
        match dynamicRegistration clientCapabilities with
        | false -> None
        | true ->
            let registerOptions: ImplementationRegistrationOptions =
                { DocumentSelector = Some defaultDocumentSelector
                  Id = None
                  WorkDoneProgress = None }

            Some
                { Id = Guid.NewGuid().ToString()
                  Method = "textDocument/implementation"
                  RegisterOptions = registerOptions |> serialize |> Some }

    let handle (context: ServerRequestContext) (p: ImplementationParams) : Async<LspResult<U2<Definition, DefinitionLink array> option>> = async {
        match! context.FindSymbol p.TextDocument.Uri p.Position with
        | None -> return None |> LspResult.success
        | Some symbol ->
            let! impls = context.FindImplementations symbol
            let! locations = impls |> Seq.map (flip context.ResolveSymbolLocations None) |> Async.Parallel

            return
                locations
                |> Array.collect List.toArray
                |> Declaration.C2
                |> U2.C1
                |> Some
                |> LspResult.success
    }
