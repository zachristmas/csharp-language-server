namespace CSharpLanguageServer.Handlers

open System
open System.IO
open System.Reflection
open System.Collections.Generic
open System.Xml.Linq

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

    let private asmNs = XNamespace.Get "urn:schemas-microsoft-com:asm.v1"

    // Set/insert binding redirects in the build host's .exe.config so its own code AND the
    // (newer) VS MSBuild we copied in both bind to the present assembly versions.
    let private updateBuildHostRedirects (cfgPath: string) (redirects: IDictionary<string, string * string>) =
        let doc = XDocument.Load(cfgPath)
        let runtime = doc.Root.Element(XName.Get "runtime")
        if not (isNull runtime) then
            let ab =
                match runtime.Elements(asmNs + "assemblyBinding") |> Seq.tryHead with
                | Some e -> e
                | None ->
                    let e = XElement(asmNs + "assemblyBinding")
                    runtime.Add e
                    e
            for kv in redirects do
                let name = kv.Key
                let ver, tok = kv.Value
                let depOpt =
                    runtime.Elements(asmNs + "assemblyBinding")
                    |> Seq.collect (fun b -> b.Elements(asmNs + "dependentAssembly"))
                    |> Seq.tryFind (fun d ->
                        let id = d.Element(asmNs + "assemblyIdentity")
                        not (isNull id)
                        && (let a = id.Attribute(XName.Get "name") in not (isNull a) && a.Value = name))
                match depOpt with
                | Some d ->
                    let br = d.Element(asmNs + "bindingRedirect")
                    if not (isNull br) then
                        br.SetAttributeValue(XName.Get "oldVersion", "0.0.0.0-" + ver)
                        br.SetAttributeValue(XName.Get "newVersion", ver)
                | None ->
                    ab.Add(
                        XElement(asmNs + "dependentAssembly",
                            XElement(asmNs + "assemblyIdentity",
                                XAttribute(XName.Get "name", name),
                                XAttribute(XName.Get "publicKeyToken", tok),
                                XAttribute(XName.Get "culture", "neutral")),
                            XElement(asmNs + "bindingRedirect",
                                XAttribute(XName.Get "oldVersion", "0.0.0.0-" + ver),
                                XAttribute(XName.Get "newVersion", ver))))
            doc.Save cfgPath

    // The net472 build host runs on .NET Framework, so it sees & picks the highest-version VS
    // MSBuild. We run on .NET (Core), where MSBuildLocator.QueryVisualStudioInstances() returns
    // only .NET SDK instances (never VS) -- so use vswhere to resolve the same VS MSBuild the
    // build host will load.
    let private findHighestVsMSBuildBin () : string option =
        try
            let vswhere =
                Path.Combine(
                    Environment.GetFolderPath Environment.SpecialFolder.ProgramFilesX86,
                    "Microsoft Visual Studio", "Installer", "vswhere.exe")
            if not (File.Exists vswhere) then None
            else
                let psi = System.Diagnostics.ProcessStartInfo(vswhere, "-latest -prerelease -products * -property installationPath")
                psi.RedirectStandardOutput <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                use p = System.Diagnostics.Process.Start psi
                let out = p.StandardOutput.ReadToEnd().Trim()
                p.WaitForExit()
                if String.IsNullOrWhiteSpace out then None
                else
                    let bin = Path.Combine(out, "MSBuild", "Current", "Bin")
                    if Directory.Exists bin then Some bin else None
        with _ -> None

    // The build host ships older dependency assemblies (e.g. System.Collections.Immutable 9.x)
    // pinned via binding redirects; loading a newer MSBuild (e.g. VS 2026 / v18, which needs
    // Immutable 10.x) crashes in XMakeElements' type initializer. Align the build host's deps +
    // redirects to that VS MSBuild so it can load. Idempotent (only copies when VS has a newer
    // version) and best-effort (never fails startup).
    let private alignBuildHostDependencies () =
        try
            match findHighestVsMSBuildBin () with
            | None -> ()
            | Some msbuildBin ->
                let bhDir = Path.Combine(AppContext.BaseDirectory, "BuildHost-net472")
                let cfg = Path.Combine(bhDir, "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config")
                if Directory.Exists bhDir && File.Exists cfg then
                    let redirects = Dictionary<string, string * string>()
                    for dll in Directory.GetFiles(bhDir, "*.dll") do
                        let name = Path.GetFileName dll
                        if not (name.StartsWith "Microsoft.CodeAnalysis") then
                            let src = Path.Combine(msbuildBin, name)
                            if File.Exists src then
                                try
                                    let sn = AssemblyName.GetAssemblyName src
                                    let bn = AssemblyName.GetAssemblyName dll
                                    if sn.Version > bn.Version then
                                        File.Copy(src, dll, true)
                                        let tok = sn.GetPublicKeyToken()
                                        // strong-named (redirectable) and not MSBuild itself (keep its frozen 15.1.0.0)
                                        if not (isNull tok) && tok.Length > 0 && not (sn.Name.StartsWith "Microsoft.Build") then
                                            let tokStr = tok |> Array.map (fun b -> b.ToString "x2") |> String.concat ""
                                            redirects.[sn.Name] <- (string sn.Version, tokStr)
                                with _ -> ()
                    if redirects.Count > 0 then
                        updateBuildHostRedirects cfg redirects
                        logger.info (
                            Log.setMessage "Aligned net472 build host deps to {bin} ({count} assemblies)"
                            >> Log.addContext "bin" msbuildBin
                            >> Log.addContext "count" redirects.Count)
        with ex ->
            logger.warn (Log.setMessage (sprintf "Build host dependency alignment skipped: %s" ex.Message))

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

        // Align the out-of-process net472 build host's MSBuild deps to the VS it will load
        // (must run before any solution load spawns the build host).
        alignBuildHostDependencies()

        initializeMSBuild()

(*
        logger.trace (
            Log.setMessage "handleInitialize: p.Capabilities={caps}"
            >> Log.addContext "caps" (serialize p.Capabilities)
        )
*)
        context.Emit(ClientCapabilityChange p.Capabilities)

        // Anchor rootPath at the LSP workspace root (rootUri) so nearest-.sln discovery
        // (findSolutionPathForDocument) is bounded by the real workspace - e.g. a monorepo root -
        // rather than the server's process CWD. Falls back to CWD when rootUri is absent.
        let rootPath =
            p.RootUri
            |> Option.bind CSharpLanguageServer.Util.tryParseFileUri
            |> Option.defaultWith Directory.GetCurrentDirectory
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

            //
            // Start loading the solution immediately. Do NOT gate this behind the client-dependent
            // requests below: some LSP clients (e.g. Claude Code's LSP tool) never answer
            // client/registerCapability or workspace/configuration, which would otherwise block here
            // indefinitely so the solution would never load. If a `solution` setting arrives later via
            // workspace/configuration, the resulting SettingsChange re-triggers a reload.
            //
            stateActor.Post(SolutionReloadRequest (TimeSpan.FromMilliseconds(100)))

            // Register dynamic capabilities + fetch the `csharp` workspace configuration in the
            // background, so an unresponsive client cannot stall initialization or solution loading.
            Async.Start(
                async {
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
                })

            logger.trace(
                Log.setMessage "handleInitialized: OK")

            return Ok()
        }

    let handleShutdown (context: ServerRequestContext) (_: unit) : Async<LspResult<unit>> = async {
        context.Emit(ClientCapabilityChange emptyClientCapabilities)
        context.Emit(ClientChange None)
        return Ok()
    }
