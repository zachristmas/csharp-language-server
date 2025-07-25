module CSharpLanguageServer.RoslynHelpers

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open System.Collections.Immutable
open System.Text.RegularExpressions

open Castle.DynamicProxy
open ICSharpCode.Decompiler
open ICSharpCode.Decompiler.CSharp
open ICSharpCode.Decompiler.CSharp.Transforms
open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Types
open Microsoft.Build.Exceptions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Host
open Microsoft.CodeAnalysis.Host.Mef
open Microsoft.CodeAnalysis.MSBuild
open Microsoft.CodeAnalysis.Text

open CSharpLanguageServer
open CSharpLanguageServer.Conversions
open CSharpLanguageServer.Logging

type DocumentSymbolCollectorForMatchingSymbolName
        (documentUri, sym: ISymbol) =
    inherit CSharpSyntaxWalker(SyntaxWalkerDepth.Token)

    let mutable collectedLocations = []
    let mutable suggestedLocations = []

    let collectIdentifier (identifier: SyntaxToken) exactMatch =
        let location: Types.Location =
            { Uri = documentUri
              Range = identifier.GetLocation().GetLineSpan().Span
                      |> Range.fromLinePositionSpan }

        if exactMatch then
            collectedLocations <- location :: collectedLocations
        else
            suggestedLocations <- location :: suggestedLocations

    member __.GetLocations() =
        if not (Seq.isEmpty collectedLocations) then
            collectedLocations |> Seq.rev |> List.ofSeq
        else
            suggestedLocations |> Seq.rev |> List.ofSeq

    override __.Visit(node) =
        if sym.Kind = SymbolKind.Method then
            if node :? MethodDeclarationSyntax then
                let nodeMethodDecl = node :?> MethodDeclarationSyntax

                if nodeMethodDecl.Identifier.ValueText = sym.Name then
                    let methodArityMatches =
                        let symMethod = sym :?> IMethodSymbol
                        symMethod.Parameters.Length = nodeMethodDecl.ParameterList.Parameters.Count

                    collectIdentifier nodeMethodDecl.Identifier methodArityMatches
        else
            if node :? TypeDeclarationSyntax then
                let typeDecl = node :?> TypeDeclarationSyntax
                if typeDecl.Identifier.ValueText = sym.Name then
                    collectIdentifier typeDecl.Identifier false

            else if node :? PropertyDeclarationSyntax then
                let propertyDecl = node :?> PropertyDeclarationSyntax
                if propertyDecl.Identifier.ValueText = sym.Name then
                    collectIdentifier propertyDecl.Identifier false

            else if node :? EventDeclarationSyntax then
                let eventDecl = node :?> EventDeclarationSyntax
                if eventDecl.Identifier.ValueText = sym.Name then
                    collectIdentifier eventDecl.Identifier false

            // TODO: collect other type of syntax nodes too

        base.Visit(node)

type CleanCodeGenerationOptionsProviderInterceptor (_logMessage) =
    interface IInterceptor with
        member __.Intercept(invocation: IInvocation) =
            match invocation.Method.Name with
            "GetCleanCodeGenerationOptionsAsync" ->
                let workspacesAssembly = Assembly.Load("Microsoft.CodeAnalysis.Workspaces")
                let cleanCodeGenOptionsType = workspacesAssembly.GetType("Microsoft.CodeAnalysis.CodeGeneration.CleanCodeGenerationOptions")

                let methodGetDefault = cleanCodeGenOptionsType.GetMethod("GetDefault")

                let argLanguageServices = invocation.Arguments[0]
                let defaultCleanCodeGenOptions = methodGetDefault.Invoke(null, [| argLanguageServices |])

                let valueTaskType = typedefof<ValueTask<_>>
                let valueTaskTypeForCleanCodeGenOptions = valueTaskType.MakeGenericType([| cleanCodeGenOptionsType |])

                invocation.ReturnValue <-
                    Activator.CreateInstance(valueTaskTypeForCleanCodeGenOptions, defaultCleanCodeGenOptions)

            | _ ->
                NotImplementedException(string invocation.Method) |> raise

type LegacyWorkspaceOptionServiceInterceptor (logMessage) =
    interface IInterceptor with
        member __.Intercept(invocation: IInvocation) =
            //logMessage (sprintf "LegacyWorkspaceOptionServiceInterceptor: %s" (string invocation.Method))

            match invocation.Method.Name with
            | "RegisterWorkspace" ->
                ()
            | "GetGenerateEqualsAndGetHashCodeFromMembersGenerateOperators" ->
                invocation.ReturnValue <- box true
            | "GetGenerateEqualsAndGetHashCodeFromMembersImplementIEquatable" ->
                invocation.ReturnValue <- box true
            | "GetGenerateConstructorFromMembersOptionsAddNullChecks" ->
                invocation.ReturnValue <- box true
            | "get_GenerateOverrides" ->
                invocation.ReturnValue <- box true
            | "get_CleanCodeGenerationOptionsProvider" ->
                let workspacesAssembly = Assembly.Load("Microsoft.CodeAnalysis.Workspaces")
                let cleanCodeGenOptionsProvType = workspacesAssembly.GetType("Microsoft.CodeAnalysis.CodeGeneration.AbstractCleanCodeGenerationOptionsProvider")

                let generator = ProxyGenerator()
                let interceptor = CleanCodeGenerationOptionsProviderInterceptor(logMessage)
                let proxy = generator.CreateClassProxy(cleanCodeGenOptionsProvType, interceptor)
                invocation.ReturnValue <- proxy

            | _ ->
                NotImplementedException(string invocation.Method) |> raise

type PickMembersServiceInterceptor (_logMessage) =
    interface IInterceptor with
         member __.Intercept(invocation: IInvocation) =

            match invocation.Method.Name with
            | "PickMembers" ->
                let argMembers = invocation.Arguments[1]
                let argOptions = invocation.Arguments[2]

                let pickMembersResultType = invocation.Method.ReturnType

                invocation.ReturnValue <-
                    Activator.CreateInstance(pickMembersResultType, argMembers, argOptions, box true)

            | _ ->
                NotImplementedException(string invocation.Method) |> raise

type ExtractClassOptionsServiceInterceptor (_logMessage) =

    let getExtractClassOptionsImpl(argOriginalType: INamedTypeSymbol): Object =
        let featuresAssembly = Assembly.Load("Microsoft.CodeAnalysis.Features")

        let typeName = "Base" + argOriginalType.Name
        let fileName = typeName + ".cs"
        let sameFile = box true

        let immArrayType = typeof<ImmutableArray>
        let extractClassMemberAnalysisResultType = featuresAssembly.GetType("Microsoft.CodeAnalysis.ExtractClass.ExtractClassMemberAnalysisResult")

        let resultListType = typedefof<List<_>>.MakeGenericType(extractClassMemberAnalysisResultType)
        let resultList = Activator.CreateInstance(resultListType)

        let memberFilter (m: ISymbol) =
            match m with
            | :? IMethodSymbol as ms -> ms.MethodKind = MethodKind.Ordinary
            | :? IFieldSymbol as fs -> not fs.IsImplicitlyDeclared
            | _ -> m.Kind = SymbolKind.Property || m.Kind = SymbolKind.Event

        let selectedMembersToAdd =
            argOriginalType.GetMembers()
            |> Seq.filter memberFilter

        for memberToAdd in selectedMembersToAdd do
            let memberAnalysisResult =
                Activator.CreateInstance(extractClassMemberAnalysisResultType, memberToAdd, false)

            resultListType.GetMethod("Add").Invoke(resultList, [| memberAnalysisResult |])
            |> ignore

        let resultListAsArray =
            resultListType.GetMethod("ToArray").Invoke(resultList, null)

        let immArrayCreateFromArray =
            immArrayType.GetMethods()
            |> Seq.filter (fun m -> m.GetParameters().Length = 1 && (m.GetParameters()[0]).ParameterType.IsArray)
            |> Seq.head

        let emptyMemberAnalysisResults =
            immArrayCreateFromArray.MakeGenericMethod([| extractClassMemberAnalysisResultType |]).Invoke(null, [| resultListAsArray |])

        let extractClassOptionsType = featuresAssembly.GetType("Microsoft.CodeAnalysis.ExtractClass.ExtractClassOptions")

        Activator.CreateInstance(
            extractClassOptionsType, fileName, typeName, sameFile, emptyMemberAnalysisResults)

    interface IInterceptor with
        member __.Intercept(invocation: IInvocation) =

            match invocation.Method.Name with
            | "GetExtractClassOptionsAsync" ->
                let argOriginalType = invocation.Arguments[1] :?> INamedTypeSymbol
                let extractClassOptionsValue = getExtractClassOptionsImpl(argOriginalType)

                let fromResultMethod = typeof<Task>.GetMethod("FromResult")
                let typedFromResultMethod = fromResultMethod.MakeGenericMethod([| extractClassOptionsValue.GetType() |])

                invocation.ReturnValue <- typedFromResultMethod.Invoke(null, [| extractClassOptionsValue |])

            | "GetExtractClassOptions" ->
                let argOriginalType = invocation.Arguments[1] :?> INamedTypeSymbol
                invocation.ReturnValue <- getExtractClassOptionsImpl(argOriginalType)

            | _ ->
                NotImplementedException(string invocation.Method) |> raise

type ExtractInterfaceOptionsServiceInterceptor (logMessage) =
    interface IInterceptor with

        member __.Intercept(invocation: IInvocation) =
            match invocation.Method.Name with
            | "GetExtractInterfaceOptionsAsync" ->
                let argExtractableMembers = invocation.Arguments[2] :?> List<ISymbol>
                let argDefaultInterfaceName = invocation.Arguments[3] :?> string

                let fileName = sprintf "%s.cs" argDefaultInterfaceName

                let featuresAssembly = Assembly.Load("Microsoft.CodeAnalysis.Features")

                let extractInterfaceOptionsResultType =
                    featuresAssembly.GetType("Microsoft.CodeAnalysis.ExtractInterface.ExtractInterfaceOptionsResult")

                let locationEnumType = extractInterfaceOptionsResultType.GetNestedType("ExtractLocation")
                let location = Enum.Parse(locationEnumType, "NewFile") // or "SameFile"

                let workspacesAssembly = Assembly.Load("Microsoft.CodeAnalysis.Workspaces")
                let cleanCodeGenOptionsProvType = workspacesAssembly.GetType("Microsoft.CodeAnalysis.CodeGeneration.AbstractCleanCodeGenerationOptionsProvider")

                let generator = ProxyGenerator()
                let interceptor = CleanCodeGenerationOptionsProviderInterceptor(logMessage)
                let cleanCodeGenerationOptionsProvider =
                     generator.CreateClassProxy(cleanCodeGenOptionsProvType, interceptor)

                let extractInterfaceOptionsResultValue =
                    Activator.CreateInstance(
                        extractInterfaceOptionsResultType,
                        false, // isCancelled
                        argExtractableMembers.ToImmutableArray(),
                        argDefaultInterfaceName,
                        fileName,
                        location,
                        cleanCodeGenerationOptionsProvider)

                let fromResultMethod = typeof<Task>.GetMethod("FromResult")
                let typedFromResultMethod = fromResultMethod.MakeGenericMethod([| extractInterfaceOptionsResultType |])

                invocation.ReturnValue <-
                    typedFromResultMethod.Invoke(null, [| extractInterfaceOptionsResultValue |])

            | _ ->
                NotImplementedException(string invocation.Method.Name) |> raise

type MoveStaticMembersOptionsServiceInterceptor (_logMessage) =
    interface IInterceptor with
       member __.Intercept(invocation: IInvocation) =

            match invocation.Method.Name with
            | "GetMoveMembersToTypeOptions" ->
                let _argDocument = invocation.Arguments[0] :?> Document
                let _argOriginalType = invocation.Arguments[1] :?> INamedTypeSymbol
                let argSelectedMembers = invocation.Arguments[2] :?> ImmutableArray<ISymbol>

                let featuresAssembly = Assembly.Load("Microsoft.CodeAnalysis.Features")
                let msmOptionsType = featuresAssembly.GetType("Microsoft.CodeAnalysis.MoveStaticMembers.MoveStaticMembersOptions")

                let newStaticClassName = "NewStaticClass"

                let msmOptions =
                    Activator.CreateInstance(
                        msmOptionsType,
                        newStaticClassName + ".cs",
                        newStaticClassName,
                        argSelectedMembers,
                        false |> box)

                invocation.ReturnValue <- msmOptions

            | _ ->
                NotImplementedException(string invocation.Method) |> raise

type RemoteHostClientProviderInterceptor (_logMessage) =
    interface IInterceptor with
       member __.Intercept(invocation: IInvocation) =

            match invocation.Method.Name with
            | "TryGetRemoteHostClientAsync" ->
                let workspacesAssembly = Assembly.Load("Microsoft.CodeAnalysis.Workspaces")
                let remoteHostClientType = workspacesAssembly.GetType("Microsoft.CodeAnalysis.Remote.RemoteHostClient")
                let methodInfo = typeof<Task>.GetMethod("FromResult", BindingFlags.Static ||| BindingFlags.Public)
                let genericMethod = methodInfo.MakeGenericMethod(remoteHostClientType)
                let nullResultTask = genericMethod.Invoke(null, [| null |])
                invocation.ReturnValue <- nullResultTask

            | _ ->
                NotImplementedException(string invocation.Method) |> raise

type WorkspaceServicesInterceptor () =
    let logger = LogProvider.getLoggerByName "WorkspaceServicesInterceptor"

    interface IInterceptor with
        member __.Intercept(invocation: IInvocation) =
            invocation.Proceed()

            if invocation.Method.Name = "GetService" && invocation.ReturnValue = null then
                let updatedReturnValue =
                    let serviceType = invocation.GenericArguments[0]
                    let generator = ProxyGenerator()

                    match serviceType.FullName with
                    | "Microsoft.CodeAnalysis.Options.ILegacyGlobalOptionsWorkspaceService" ->
                        let interceptor = LegacyWorkspaceOptionServiceInterceptor()
                        generator.CreateInterfaceProxyWithoutTarget(serviceType, interceptor)

                    | "Microsoft.CodeAnalysis.PickMembers.IPickMembersService" ->
                        let interceptor = PickMembersServiceInterceptor()
                        generator.CreateInterfaceProxyWithoutTarget(serviceType, interceptor)

                    | "Microsoft.CodeAnalysis.ExtractClass.IExtractClassOptionsService" ->
                        let interceptor = ExtractClassOptionsServiceInterceptor()
                        generator.CreateInterfaceProxyWithoutTarget(serviceType, interceptor)

                    | "Microsoft.CodeAnalysis.ExtractInterface.IExtractInterfaceOptionsService" ->
                        let interceptor = ExtractInterfaceOptionsServiceInterceptor()
                        generator.CreateInterfaceProxyWithoutTarget(serviceType, interceptor)

                    | "Microsoft.CodeAnalysis.MoveStaticMembers.IMoveStaticMembersOptionsService" ->
                        let interceptor = MoveStaticMembersOptionsServiceInterceptor()
                        generator.CreateInterfaceProxyWithoutTarget(serviceType, interceptor)

                    | "Microsoft.CodeAnalysis.Remote.IRemoteHostClientProvider" ->
                        let interceptor = RemoteHostClientProviderInterceptor()
                        generator.CreateInterfaceProxyWithoutTarget(serviceType, interceptor)

                    | "Microsoft.CodeAnalysis.SourceGeneratorTelemetry.ISourceGeneratorTelemetryCollectorWorkspaceService"
                    | "Microsoft.CodeAnalysis.CodeRefactorings.PullMemberUp.Dialog.IPullMemberUpOptionsService"
                    | "Microsoft.CodeAnalysis.Packaging.IPackageInstallerService" ->
                        // supress "GetService failed" messages for these services
                        null

                    | _ ->
                        logger.debug (
                            Log.setMessage "GetService failed for {serviceType}"
                            >> Log.addContext "serviceType" (serviceType.FullName)
                        )
                        null

                invocation.ReturnValue <- updatedReturnValue

type CSharpLspHostServices () =
    inherit HostServices()

    member private this.hostServices = MSBuildMefHostServices.DefaultServices

    override this.CreateWorkspaceServices (workspace: Workspace) =
        // Ugly but we can't:
        // 1. use Castle since there is no default constructor of MefHostServices.
        // 2. call this.hostServices.CreateWorkspaceServices directly since it's internal.
        let methodInfo = this.hostServices.GetType().GetMethod("CreateWorkspaceServices", BindingFlags.Instance|||BindingFlags.NonPublic)
        let services =
            methodInfo.Invoke(this.hostServices, [| workspace |])
            |> Unchecked.unbox<HostWorkspaceServices>
        let generator = ProxyGenerator()
        let interceptor = WorkspaceServicesInterceptor()
        generator.CreateClassProxyWithTarget(services, interceptor)


let isWorkspaceCompatibleProject (projectPath: string) =
    let extension = Path.GetExtension(projectPath).ToLowerInvariant()
    match extension with
    | ".csproj" | ".fsproj" | ".vbproj" -> true
    | ".sqlproj" -> false  // SQL Server projects use different project system
    | _ -> false

let loadProjectFilenamesFromSolution (solutionPath: string) =
    assert Path.IsPathRooted(solutionPath)
    let projectFilenames = new List<string>()

    let solutionFile = Microsoft.Build.Construction.SolutionFile.Parse(solutionPath)
    for project in solutionFile.ProjectsInOrder do
        if project.ProjectType = Microsoft.Build.Construction.SolutionProjectType.KnownToBeMSBuildFormat then
            projectFilenames.Add(project.AbsolutePath)

    // Filter to only include workspace-compatible projects for MSBuildWorkspace
    projectFilenames 
    |> Seq.filter isWorkspaceCompatibleProject
    |> Set.ofSeq


type TfmCategory =
    | NetFramework of Version
    | NetStandard of Version
    | NetCoreApp of Version
    | Net of Version
    | Unknown


let selectMostCapableCompatibleTfm (tfms: string seq) : string option =
    let parseTfm (tfm: string) : TfmCategory =
        let patterns = [
            @"^net(?<major>\d)(?<minor>\d)?(?<build>\d)?$", NetFramework
            @"^netstandard(?<major>\d+)\.(?<minor>\d+)$", NetStandard
            @"^netcoreapp(?<major>\d+)\.(?<minor>\d+)$", NetCoreApp
            @"^net(?<major>\d+)\.(?<minor>\d+)$", Net
        ]

        let matchingTfmCategory (pat, categoryCtor) =
            let m = Regex.Match(tfm.ToLowerInvariant(), pat)
            if m.Success then
                let readVersionNum (groupName: string) =
                    let group = m.Groups.[groupName]
                    if group.Success then (int group.Value) else 0

                Version(readVersionNum "major", readVersionNum "minor", readVersionNum "build")
                |> categoryCtor
                |> Some
            else
                None

        patterns
        |> List.tryPick matchingTfmCategory
        |> Option.defaultValue Unknown

    let rankTfm = function
        | Net v -> 3000 + v.Major * 10 + v.Minor
        | NetCoreApp v -> 2000 + v.Major * 10 + v.Minor
        | NetStandard v -> 1000 + v.Major * 10 + v.Minor
        | NetFramework v -> 0 + v.Major * 10 + v.Minor
        | Unknown -> -1

    tfms
    |> Seq.sortByDescending (parseTfm >> rankTfm)
    |> Seq.tryHead


let applyWorkspaceTargetFrameworkProp (logger: ILog) (projs: string seq) props =
    let tfms = new List<string>()

    // Only process workspace-compatible projects
    let compatibleProjs = projs |> Seq.filter isWorkspaceCompatibleProject

    for projectFilename in compatibleProjs do
        let projectCollection = new Microsoft.Build.Evaluation.ProjectCollection();
        let props = new Dictionary<string, string>();

        try
            let buildProject = projectCollection.LoadProject(projectFilename, props, toolsVersion=null)

            let noneIfEmpty s =
                s |> Option.ofObj
                    |> Option.bind (fun s -> if String.IsNullOrEmpty(s) then None else Some s)

            let targetFramework = buildProject.GetPropertyValue("TargetFramework") |> noneIfEmpty

            match targetFramework with
            | Some tfm ->
                tfms.Add(tfm.Trim())
            | _ -> ()

            let targetFrameworks = buildProject.GetPropertyValue("TargetFrameworks") |> noneIfEmpty

            match targetFrameworks with
            | Some semicolonSeparatedTfms ->
                for tfm in semicolonSeparatedTfms.Split(";") do
                    tfms.Add(tfm.Trim())
            | _ -> ()

            projectCollection.UnloadProject(buildProject)
        with
        | :? InvalidProjectFileException as ipfe ->
            logger.debug (
                Log.setMessage "applyWorkspaceTargetFrameworkProp: failed to load {projectFilename}: {ex}"
                >> Log.addContext "projectFilename" projectFilename
                >> Log.addContext "ex" (string (ipfe.GetType()))
            )

    let distinctTfms = tfms |> Set.ofSeq

    logger.debug (
        Log.setMessage "applyWorkspaceTargetFrameworkProp: distinctTfms={distinctTfms}"
        >> Log.addContext "distinctTfms" (String.Join(";", distinctTfms))
    )

    match distinctTfms.Count with
    | 0 -> props
    | 1 -> props
    | _ ->
        match selectMostCapableCompatibleTfm distinctTfms with
        | Some tfm -> props |> Map.add "TargetFramework" tfm
        | None -> props


let resolveDefaultWorkspaceProps (logger: ILog) projs : Map<string, string> =
    Map.empty
    |> applyWorkspaceTargetFrameworkProp logger projs


let tryLoadSolutionOnPath
        (lspClient: ILspClient)
        (logger: ILog)
        (solutionPath: string) =
    assert Path.IsPathRooted(solutionPath)
    let progress = ProgressReporter(lspClient)
    
    // Normalize the solution path for consistent display
    let displayPath = solutionPath.Replace('\\', '/')

    let logMessage m =
        lspClient.WindowLogMessage({
            Type = MessageType.Info
            Message = sprintf "csharp-ls: %s" m
        })

    let showMessage m =
        lspClient.WindowShowMessage({
            Type = MessageType.Info
            Message = sprintf "csharp-ls: %s" m
        })

    async {
        try
            let beginMessage = sprintf "Loading solution \"%s\"..." displayPath
            do! progress.Begin(beginMessage)

            let projs = loadProjectFilenamesFromSolution solutionPath
            let workspaceProps = resolveDefaultWorkspaceProps logger projs

            if workspaceProps.Count > 0 then
                logger.info (
                    Log.setMessage "Will use these MSBuild props: {workspaceProps}"
                    >> Log.addContext "workspaceProps" (string workspaceProps)
                )

            let msbuildWorkspace = MSBuildWorkspace.Create(workspaceProps, CSharpLspHostServices())
            msbuildWorkspace.LoadMetadataForReferencedProjects <- true

            let! solution = msbuildWorkspace.OpenSolutionAsync(solutionPath) |> Async.AwaitTask

            for diag in msbuildWorkspace.Diagnostics do
                logger.info (
                    Log.setMessage "msbuildWorkspace.Diagnostics: {message}"
                    >> Log.addContext "message" (diag.ToString())
                )

                // Remove duplicate logging - already logged above
                // Only log warnings and errors to the client to reduce verbosity
                // match diag.Kind with
                // | Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Warning 
                // | Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Failure ->
                //     do! logMessage (sprintf "msbuildWorkspace.Diagnostics: %s" (diag.ToString()))
                // | _ -> ()

            let endMessage = sprintf "Finished loading solution \"%s\"" displayPath
            do! progress.End endMessage

            return Some solution
        with
        | ex ->
            let errorMessage = sprintf "Solution \"%s\" could not be loaded: %s" displayPath (ex.ToString())
            do! progress.End errorMessage
            do! showMessage errorMessage
            return None
    }

let tryLoadSolutionFromProjectFiles
        (lspClient: ILspClient)
        (logger: ILog)
        (logMessage: string -> Async<unit>)
        (projs: string list) =
    let progress = ProgressReporter(lspClient)

    async {
        // Filter to only workspace-compatible projects for MSBuildWorkspace
        let compatibleProjs = projs |> List.filter isWorkspaceCompatibleProject
        let skippedCount = projs.Length - compatibleProjs.Length

        if skippedCount > 0 then
            logger.info (
                Log.setMessage "Skipping {skippedCount} non-workspace-compatible project(s) (e.g., .sqlproj)"
                >> Log.addContext "skippedCount" skippedCount
            )

        do! progress.Begin($"Loading {compatibleProjs.Length} workspace-compatible project(s)...", false, $"0/{compatibleProjs.Length}", 0u)
        let loadedProj = ref 0

        let workspaceProps = resolveDefaultWorkspaceProps logger compatibleProjs

        if workspaceProps.Count > 0 then
            logger.info (
                Log.setMessage "Will use these MSBuild props: {workspaceProps}"
                >> Log.addContext "workspaceProps" (string workspaceProps)
            )

        let msbuildWorkspace = MSBuildWorkspace.Create(workspaceProps, CSharpLspHostServices())
        msbuildWorkspace.LoadMetadataForReferencedProjects <- true

        for file in compatibleProjs do
            if compatibleProjs.Length < 10 then
              do! logMessage (sprintf "loading project \"%s\".." file)
            try
                do! msbuildWorkspace.OpenProjectAsync(file) |> Async.AwaitTask |> Async.Ignore
            with ex ->
                logger.error (
                    Log.setMessage "could not OpenProjectAsync('{file}'): {exception}"
                    >> Log.addContext "file" file
                    >> Log.addContext "ex" (string ex)
                )
            let projectFile = new FileInfo(file)
            let projName = projectFile.Name
            let loaded = Interlocked.Increment(loadedProj)
            let percent = 100 * loaded / compatibleProjs.Length |> uint
            do! progress.Report(false, $"{projName} {loaded}/{compatibleProjs.Length}", percent)

        for diag in msbuildWorkspace.Diagnostics do
            logger.trace (
                Log.setMessage "msbuildWorkspace.Diagnostics: {message}"
                >> Log.addContext "message" (diag.ToString())
            )

        do! progress.End (sprintf "OK, %d workspace-compatible project file(s) loaded" compatibleProjs.Length)

        //workspace <- Some(msbuildWorkspace :> Workspace)
        return Some msbuildWorkspace.CurrentSolution
    }

let findAndLoadSolutionOnDir
        (lspClient: ILspClient)
        (logger: ILog)
        dir =
    async {
        let fileNotOnNodeModules (filename: string) =
            filename.Split(Path.DirectorySeparatorChar)
            |> Seq.contains "node_modules"
            |> not

        let solutionFiles =
            [ "*.sln"; "*.slnx" ]
            |> List.collect(fun p -> Directory.GetFiles(dir, p, SearchOption.AllDirectories) |> List.ofArray)
            |> Seq.filter fileNotOnNodeModules
            |> Seq.toList

        let logMessage m =
            lspClient.WindowLogMessage({
                Type = MessageType.Info
                Message = sprintf "csharp-ls: %s" m
            })

        do! logMessage (sprintf "%d solution(s) found: [%s]" solutionFiles.Length (String.Join(", ", solutionFiles)) )

        let singleSolutionFound =
            match solutionFiles with
            | [x] -> Some x
            | _ -> None

        match singleSolutionFound with
        | None ->
            do! logMessage ("no or multiple .sln/.slnx files found on " + dir)
            do! logMessage ("looking for .csproj/fsproj files on " + dir + "..")

            let projFiles =
                let csprojFiles = Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories)
                let fsprojFiles = Directory.GetFiles(dir, "*.fsproj", SearchOption.AllDirectories)
                let sqlprojFiles = Directory.GetFiles(dir, "*.sqlproj", SearchOption.AllDirectories)

                [ csprojFiles; fsprojFiles; sqlprojFiles ] |> Seq.concat
                                                |> Seq.filter fileNotOnNodeModules
                                                |> Seq.filter isWorkspaceCompatibleProject  // Filter out non-compatible projects like .sqlproj
                                                |> Seq.toList

            if projFiles.Length = 0 then
                let message = "no .csproj/.fsproj/.sqlproj or sln files found on " + dir
                do! logMessage message
                Exception message |> raise

            return! tryLoadSolutionFromProjectFiles lspClient logger logMessage projFiles

        | Some solutionPath ->
            return! tryLoadSolutionOnPath lspClient logger solutionPath
    }

let loadSolutionOnSolutionPathOrDir
        (lspClient: ILspClient)
        (logger: ILog)
        (solutionPathMaybe: string option)
        (rootPath: string) =
    match solutionPathMaybe with
    | Some solutionPath -> async {
        let rootedSolutionPath =
            match (Path.IsPathRooted(solutionPath)) with
            | true -> solutionPath
            | false -> Path.Combine(rootPath, solutionPath)

        return! tryLoadSolutionOnPath lspClient logger rootedSolutionPath
      }

    | None -> async {
        let logMessage: LogMessageParams = {
            Type = MessageType.Info
            Message = sprintf "csharp-ls: attempting to find and load solution based on root path (\"%s\").." rootPath
        }

        do! lspClient.WindowLogMessage(logMessage)
        return! findAndLoadSolutionOnDir lspClient logger rootPath
      }

let getContainingTypeOrThis (symbol: ISymbol): INamedTypeSymbol =
    if (symbol :? INamedTypeSymbol) then
        symbol :?> INamedTypeSymbol
    else
        symbol.ContainingType

let getFullReflectionName (containingType: INamedTypeSymbol) =
    let stack = Stack<string>();
    stack.Push(containingType.MetadataName);
    let mutable ns = containingType.ContainingNamespace;

    let mutable doContinue = true
    while doContinue do
        stack.Push(ns.Name);
        ns <- ns.ContainingNamespace

        doContinue <- ns <> null && not ns.IsGlobalNamespace

    String.Join(".", stack)

let tryAddDocument (logger: ILog)
                   (docFilePath: string)
                   (text: string)
                   (solution: Solution)
                   : Async<Document option> =
  async {
    let docDir = Path.GetDirectoryName(docFilePath)
    //logMessage (sprintf "TextDocumentDidOpen: docFilename=%s docDir=%s" docFilename docDir)

    let fileOnProjectDir (p: Project) =
        let projectDir = Path.GetDirectoryName(p.FilePath)
        let projectDirWithDirSepChar = projectDir + (string Path.DirectorySeparatorChar)

        (docDir = projectDir) || docDir.StartsWith(projectDirWithDirSepChar)

    let projectOnPath =
        solution.Projects
        |> Seq.filter fileOnProjectDir
        |> Seq.tryHead

    let! newDocumentMaybe =
        match projectOnPath with
        | Some proj ->
            let projectBaseDir = Path.GetDirectoryName(proj.FilePath)
            let docName = docFilePath.Substring(projectBaseDir.Length+1)

            //logMessage (sprintf "Adding \"%s\" (\"%s\") to project %s" docName docFilePath proj.FilePath)

            let newDoc = proj.AddDocument(name=docName, text=SourceText.From(text), folders=null, filePath=docFilePath)
            Some newDoc |> async.Return

        | None -> async {
            logger.trace (
                Log.setMessage "No parent project could be resolved to add file \"{file}\" to workspace in solution \"{solutionPath}\""
                >> Log.addContext "file" docFilePath
                >> Log.addContext "solutionPath" (if isNull solution.FilePath then "Unknown" else solution.FilePath)
            )
            return None
          }

    return newDocumentMaybe
  }

let tryAddDocumentToAnySolution (logger: ILog)
                                (docFilePath: string)
                                (text: string)
                                (solutions: Solution list)
                                : Async<Document option> =
  async {
    let docDir = Path.GetDirectoryName(docFilePath)

    let fileOnProjectDir (p: Project) =
        let projectDir = Path.GetDirectoryName(p.FilePath)
        let projectDirWithDirSepChar = projectDir + (string Path.DirectorySeparatorChar)

        (docDir = projectDir) || docDir.StartsWith(projectDirWithDirSepChar)

    // Try to find a project in any of the available solutions
    let projectOnPath =
        solutions
        |> Seq.collect (fun solution -> solution.Projects)
        |> Seq.filter fileOnProjectDir
        |> Seq.tryHead

    match projectOnPath with
    | Some proj ->
        let projectBaseDir = Path.GetDirectoryName(proj.FilePath)
        let docName = docFilePath.Substring(projectBaseDir.Length+1)

        logger.info (
            Log.setMessage "Adding \"{docName}\" (\"{file}\") to project {projectPath} in solution {solutionPath}"
            >> Log.addContext "docName" docName
            >> Log.addContext "file" docFilePath
            >> Log.addContext "projectPath" proj.FilePath
            >> Log.addContext "solutionPath" (if isNull proj.Solution.FilePath then "Unknown" else proj.Solution.FilePath)
        )

        let newDoc = proj.AddDocument(name=docName, text=SourceText.From(text), folders=null, filePath=docFilePath)
        return Some newDoc

    | None ->
        logger.trace (
            Log.setMessage "No parent project could be resolved to add file \"{file}\" to workspace across {solutionCount} loaded solution(s)"
            >> Log.addContext "file" docFilePath
            >> Log.addContext "solutionCount" solutions.Length
        )
        return None
  }

let makeDocumentFromMetadata
        (compilation: Microsoft.CodeAnalysis.Compilation)
        (project: Microsoft.CodeAnalysis.Project)
        (l: Microsoft.CodeAnalysis.Location)
        (fullName: string) =
    let mdLocation = l
    let reference = compilation.GetMetadataReference(mdLocation.MetadataModule.ContainingAssembly)
    let peReference = reference :?> PortableExecutableReference |> Option.ofObj
    let assemblyLocation = peReference |> Option.map (fun r -> r.FilePath) |> Option.defaultValue "???"

    let decompilerSettings = DecompilerSettings()
    decompilerSettings.ThrowOnAssemblyResolveErrors <- false // this shouldn't be a showstopper for us

    let decompiler = CSharpDecompiler(assemblyLocation, decompilerSettings)

    // Escape invalid identifiers to prevent Roslyn from failing to parse the generated code.
    // (This happens for example, when there is compiler-generated code that is not yet recognized/transformed by the decompiler.)
    decompiler.AstTransforms.Add(EscapeInvalidIdentifiers())

    let fullTypeName = ICSharpCode.Decompiler.TypeSystem.FullTypeName(fullName)

    let text = decompiler.DecompileTypeAsString(fullTypeName)

    let mdDocumentFilename = $"$metadata$/projects/{project.Name}/assemblies/{mdLocation.MetadataModule.ContainingAssembly.Name}/symbols/{fullName}.cs"
    let mdDocumentEmpty = project.AddDocument(mdDocumentFilename, String.Empty)

    let mdDocument = SourceText.From(text) |> mdDocumentEmpty.WithText
    (mdDocument, text)
