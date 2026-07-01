namespace CSharpLanguageServer

open System
open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Server
open Ionide.LanguageServerProtocol.Types

type ProgressReporter(client: ILspClient) =
    let mutable canReport = false

    let mutable endSent = false

    member val Token = ProgressToken.C2 (Guid.NewGuid().ToString())

    member this.Begin(title, ?cancellable, ?message, ?percentage) = async {
        // window/workDoneProgress/create is a server->client REQUEST. Some clients (e.g. Claude Code's
        // LSP tool) never answer it, which would block the caller here indefinitely - and this runs on
        // the solution-load path, so an unanswered progress-create stalls solution loading entirely.
        // Race it against a short timeout: if the client doesn't ack, skip progress and proceed.
        let! child =
            Async.StartChild(
                (async {
                    try
                        let! progressCreateResult = client.WindowWorkDoneProgressCreate({ Token = this.Token })
                        return (match progressCreateResult with | Ok() -> true | Error _ -> false)
                    with _ -> return false
                }), 2000)

        let! canCreate =
            async {
                try return! child
                with _ -> return false
            }

        canReport <- canCreate
        if canCreate then
            let param = WorkDoneProgressBegin.Create(
                title = title,
                ?cancellable = cancellable,
                ?message = message,
                ?percentage = percentage
            )
            do! client.Progress({ Token = this.Token; Value = serialize param })
    }

    member this.Report(?cancellable, ?message, ?percentage) = async {
        if canReport && not endSent then
            let param = WorkDoneProgressReport.Create(
                ?cancellable = cancellable,
                ?message = message,
                ?percentage = percentage
            )
            do! client.Progress({ Token = this.Token; Value = serialize param })
    }

    member this.End(?message) = async {
        if canReport && not endSent then
            endSent <- true
            let param = WorkDoneProgressEnd.Create(
                ?message = message
            )
            do! client.Progress({ Token = this.Token; Value = serialize param })
    }
