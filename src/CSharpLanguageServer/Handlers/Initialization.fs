namespace CSharpLanguageServer.Handlers

open System
open System.IO
open System.Reflection

open Microsoft.Build.Locator
open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Types
open Ionide.LanguageServerProtocol.Server
open Ionide.LanguageServerProtocol.JsonRpc

open CSharpLanguageServer.State
open CSharpLanguageServer.State.ServerState
open CSharpLanguageServer.Types
open CSharpLanguageServer.Logging

[<RequireQualifiedAccess>]
module Initialization =
    let private logger = LogProvider.getLoggerByName "Initialization"

    let handleInitialize (lspClient: ILspClient)
                         (setupTimer: unit -> unit)
                         (serverCapabilities: ServerCapabilities)
                         (context: ServerRequestContext)
                         (p: InitializeParams)
            : Async<LspResult<InitializeResult>> = async {
        // context.State.LspClient has not been initialized yet thus context.WindowShowMessage will not work
        let windowShowMessage m = lspClient.WindowLogMessage({ Type = MessageType.Info; Message = m })

        context.Emit(ClientChange (Some lspClient))

        let serverName = "csharp-ls"
        let serverVersion = Assembly.GetExecutingAssembly().GetName().Version |> string
        logger.info (
            Log.setMessage "initializing, {name} version {version}"
            >> Log.addContext "name" serverName
            >> Log.addContext "version" serverVersion
        )

        do! windowShowMessage(
            sprintf "csharp-ls: initializing, version %s" serverVersion)

        logger.info (
            Log.setMessage "{name} is released under MIT license and is not affiliated with Microsoft Corp.; see https://github.com/razzmatazz/csharp-language-server"
            >> Log.addContext "name" serverName
        )

        do! windowShowMessage(
            sprintf "csharp-ls: %s is released under MIT license and is not affiliated with Microsoft Corp.; see https://github.com/razzmatazz/csharp-language-server" serverName)

        // Initialize MSBuild with custom configuration or auto-discovery
        let initializeMSBuild() =
            match context.State.Settings.MSBuildExePath with
            | Some msbuildExePath when File.Exists(msbuildExePath) ->
                logger.info(
                    Log.setMessage "MSBuildLocator: registering custom MSBuild executable path: {msbuildExePath}"
                    >> Log.addContext "msbuildExePath" msbuildExePath
                )
                let msbuildDir = Path.GetDirectoryName(msbuildExePath)
                MSBuildLocator.RegisterMSBuildPath(msbuildDir)

            | _ ->
                match context.State.Settings.MSBuildPath with
                | Some msbuildPath when Directory.Exists(msbuildPath) ->
                    logger.info(
                        Log.setMessage "MSBuildLocator: registering custom MSBuild path: {msbuildPath}"
                        >> Log.addContext "msbuildPath" msbuildPath
                    )
                    MSBuildLocator.RegisterMSBuildPath(msbuildPath)

                | _ ->
                    // Try to use environment variables for Visual Studio 2022
                    let vsInstallDir = Environment.GetEnvironmentVariable("VS170COMNTOOLS")
                    let programFiles = Environment.GetEnvironmentVariable("ProgramFiles")
                    let vs2022CommunityPath = 
                        if not (String.IsNullOrEmpty(programFiles)) then
                            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Community", "MSBuild", "Current", "Bin")
                        else 
                            null

                    let vs2022ProfessionalPath = 
                        if not (String.IsNullOrEmpty(programFiles)) then
                            Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Professional", "MSBuild", "Current", "Bin")
                        else 
                            null

                    let customPathFound = 
                        if not (String.IsNullOrEmpty(vsInstallDir)) then
                            let msbuildPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(vsInstallDir)), "MSBuild", "Current", "Bin")
                            if Directory.Exists(msbuildPath) then
                                logger.info(
                                    Log.setMessage "MSBuildLocator: using VS170COMNTOOLS environment variable path: {msbuildPath}"
                                    >> Log.addContext "msbuildPath" msbuildPath
                                )
                                MSBuildLocator.RegisterMSBuildPath(msbuildPath)
                                true
                            else 
                                false
                        else if not (String.IsNullOrEmpty(vs2022CommunityPath)) && Directory.Exists(vs2022CommunityPath) then
                            logger.info(
                                Log.setMessage "MSBuildLocator: using Visual Studio 2022 Community path: {msbuildPath}"
                                >> Log.addContext "msbuildPath" vs2022CommunityPath
                            )
                            MSBuildLocator.RegisterMSBuildPath(vs2022CommunityPath)
                            true
                        else if not (String.IsNullOrEmpty(vs2022ProfessionalPath)) && Directory.Exists(vs2022ProfessionalPath) then
                            logger.info(
                                Log.setMessage "MSBuildLocator: using Visual Studio 2022 Professional path: {msbuildPath}"
                                >> Log.addContext "msbuildPath" vs2022ProfessionalPath
                            )
                            MSBuildLocator.RegisterMSBuildPath(vs2022ProfessionalPath)
                            true
                        else 
                            false

                    if not customPathFound then
                        // Fall back to auto-discovery, but prefer VS 2022 instances
                        let vsInstanceQueryOpt = VisualStudioInstanceQueryOptions.Default
                        let vsInstanceList = MSBuildLocator.QueryVisualStudioInstances(vsInstanceQueryOpt)
                        if Seq.isEmpty vsInstanceList then
                            raise (InvalidOperationException("No instances of MSBuild could be detected." + Environment.NewLine + "Try calling RegisterInstance or RegisterMSBuildPath to manually register one."))

                        // Prefer VS 2022 instances, then by version descending
                        let vsInstance = 
                            vsInstanceList
                            |> Seq.sortByDescending (fun vi -> 
                                let versionScore = if vi.Version.Major >= 17 then 1000 else 0  // VS 2022 is version 17.x
                                let nameScore = if vi.Name.Contains("2022") then 100 else 0
                                versionScore + nameScore + int vi.Version.Build)
                            |> Seq.head

                        logger.info(
                            Log.setMessage "MSBuildLocator: will register \"{vsInstanceName}\", Version={vsInstanceVersion} as default instance"
                            >> Log.addContext "vsInstanceName" vsInstance.Name
                            >> Log.addContext "vsInstanceVersion" (string vsInstance.Version)
                        )

                        MSBuildLocator.RegisterInstance(vsInstance)

        initializeMSBuild()

(*
        logger.trace (
            Log.setMessage "handleInitialize: p.Capabilities={caps}"
            >> Log.addContext "caps" (serialize p.Capabilities)
        )
*)
        context.Emit(ClientCapabilityChange p.Capabilities)

        // TODO use p.RootUri
        let rootPath = Directory.GetCurrentDirectory()
        context.Emit(RootPathChange rootPath)

        // setup timer so actors get period ticks
        setupTimer()

        let initializeResult =
            { InitializeResult.Default with
                    Capabilities = serverCapabilities
                    ServerInfo =
                      Some
                        { Name = "csharp-ls"
                          Version = Some (Assembly.GetExecutingAssembly().GetName().Version.ToString()) }}

        return initializeResult |> LspResult.success
    }

    let handleInitialized (lspClient: ILspClient)
                          (stateActor: MailboxProcessor<ServerStateEvent>)
                          (getRegistrations: ClientCapabilities -> Registration list)
                          (context: ServerRequestContext)
                          (_p: unit)
            : Async<LspResult<unit>> =
        async {
            logger.trace (
                Log.setMessage "handleInitialized: \"initialized\" notification received from client"
            )

            let registrationParams = { Registrations = getRegistrations context.ClientCapabilities |> List.toArray }

            // TODO: Retry on error?
            try
                match! lspClient.ClientRegisterCapability registrationParams with
                | Ok _ -> ()
                | Error error ->
                    logger.warn(
                        Log.setMessage "handleInitialized: didChangeWatchedFiles registration has failed with {error}"
                        >> Log.addContext "error" (string error)
                    )
            with
            | ex ->
                logger.warn(
                    Log.setMessage "handleInitialized: didChangeWatchedFiles registration has failed with {error}"
                    >> Log.addContext "error" (string ex)
                )

            //
            // retrieve csharp settings
            //
            try
                let! workspaceCSharpConfig =
                    lspClient.WorkspaceConfiguration(
                        { Items=[| { Section=Some "csharp"; ScopeUri=None } |] })

                let csharpConfigTokensMaybe =
                    match workspaceCSharpConfig with
                    | Ok ts -> Some ts
                    | _ -> None

                let newSettingsMaybe =
                  match csharpConfigTokensMaybe with
                  | Some [| t |] ->
                      let csharpSettingsMaybe = t |> deserialize<ServerSettingsCSharpDto option>

                      match csharpSettingsMaybe with
                      | Some csharpSettings ->

                          match csharpSettings.solution with
                          | Some solutionPath-> Some { context.State.Settings with SolutionPath = Some solutionPath }
                          | _ -> None

                      | _ -> None
                  | _ -> None

                // do! logMessage (sprintf "handleInitialized: newSettingsMaybe=%s" (string newSettingsMaybe))

                match newSettingsMaybe with
                | Some newSettings ->
                    context.Emit(SettingsChange newSettings)
                | _ -> ()
            with
            | ex ->
                logger.warn(
                    Log.setMessage "handleInitialized: could not retrieve `csharp` workspace configuration section: {error}"
                    >> Log.addContext "error" (ex |> string)
                )

            //
            // start loading the solution
            //
            stateActor.Post(SolutionReloadRequest (TimeSpan.FromMilliseconds(100)))

            logger.trace(
                Log.setMessage "handleInitialized: OK")

            return Ok()
        }

    let handleShutdown (context: ServerRequestContext) (_: unit) : Async<LspResult<unit>> = async {
        context.Emit(ClientCapabilityChange emptyClientCapabilities)
        context.Emit(ClientChange None)
        return Ok()
    }
