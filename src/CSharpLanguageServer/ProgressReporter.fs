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
        // LSP tool) never answer it. This runs on the solution-load path, so AWAITING it would stall
        // solution loading indefinitely. Fire the create + begin-notification off DETACHED so the
        // caller never blocks; progress still shows for clients that do answer, and is silently
        // skipped for those that don't. Progress reporting is cosmetic, so a lost begin is harmless.
        Async.Start(
            async {
                try
                    let! progressCreateResult = client.WindowWorkDoneProgressCreate({ Token = this.Token })
                    match progressCreateResult with
                    | Ok() ->
                        canReport <- true
                        let param = WorkDoneProgressBegin.Create(
                            title = title,
                            ?cancellable = cancellable,
                            ?message = message,
                            ?percentage = percentage
                        )
                        do! client.Progress({ Token = this.Token; Value = serialize param })
                    | Error _ -> ()
                with _ -> ()
            })
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
