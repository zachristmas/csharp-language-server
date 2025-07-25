namespace CSharpLanguageServer.Conversions

open System
open System.IO

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Completion
open Microsoft.CodeAnalysis.Text
open Ionide.LanguageServerProtocol.Types

open CSharpLanguageServer.Util
open CSharpLanguageServer.Logging

module Uri =
    // Unescape some necessary char before passing string to Uri.
    // Can't use Uri.UnescapeDataString here. For example, if uri is "file:///z%3a/src/c%23/ProjDir" ("%3a" is
    // ":" and "%23" is "#"), Uri.UnescapeDataString will unescape both "%3a" and "%23". Then Uri will think
    /// "#/ProjDir" is Fragment instead of part of LocalPath.
    let unescape (uri: string) = uri.Replace("%3a", ":", true, null)

    let toPath (uri: string) = Uri.UnescapeDataString(Uri(unescape(uri)).LocalPath)

    // Normalize path for consistent URI generation across different path formats
    let private normalizePath (path: string) = 
        try
            // Get the full normalized path to ensure consistency
            System.IO.Path.GetFullPath(path)
        with
        | _ -> path

    let fromPath (path: string) =
        let metadataPrefix = "$metadata$/"
        if path.StartsWith(metadataPrefix) then
            "csharp:/metadata/" + path.Substring(metadataPrefix.Length)
        else
            let normalizedPath = normalizePath path
            Uri(normalizedPath).ToString()

    let toWorkspaceFolder(uri: string): WorkspaceFolder =
        { Uri = uri
          Name = Uri.UnescapeDataString(Uri(unescape(uri)).Segments |> Array.last) }


module Path =
    let toUri = Uri.fromPath

    let fromUri = Uri.toPath

    let toWorkspaceFolder = toUri >> Uri.toWorkspaceFolder


module Position =
    let fromLinePosition (pos: LinePosition): Position =
        { Line = uint32 pos.Line ; Character = uint32 pos.Character }

    let toLinePosition (lines: TextLineCollection) (pos: Position): LinePosition =
        if (int pos.Line) >= lines.Count then
            LinePosition(lines.Count - 1, lines[lines.Count - 1].EndIncludingLineBreak - lines[lines.Count - 1].Start)
        else
            LinePosition(int pos.Line, int pos.Character)

    let toRoslynPosition (lines: TextLineCollection) = toLinePosition lines >> lines.GetPosition


module Range =
    let toLinePositionSpan (lines: TextLineCollection) (range: Range): LinePositionSpan =
        LinePositionSpan(
            Position.toLinePosition lines range.Start,
            Position.toLinePosition lines range.End)

    let fromLinePositionSpan (pos: LinePositionSpan): Range =
        { Start = Position.fromLinePosition pos.Start
          End = Position.fromLinePosition pos.End }

    let toTextSpan (lines: TextLineCollection) = toLinePositionSpan lines >> lines.GetTextSpan

    let fromTextSpan (lines: TextLineCollection) = lines.GetLinePositionSpan >> fromLinePositionSpan


module Location =
    let fromRoslynLocation (loc: Microsoft.CodeAnalysis.Location): option<Location> =
        let logger = LogProvider.getLoggerByName "Location"
        
        let toLspLocation (path: string) span: Location =
            let uri = path |> Path.toUri
            logger.debug (
                Log.setMessage "Converting location: path={path}, uri={uri}"
                >> Log.addContext "path" path
                >> Log.addContext "uri" uri
            )
            { Uri = uri
              Range = span |> Range.fromLinePositionSpan }

        match loc.Kind with
        | LocationKind.SourceFile ->
            let mappedLoc = loc.GetMappedLineSpan()

            logger.debug (
                Log.setMessage "Processing source location: mappedPath={mappedPath}, sourcePath={sourcePath}, mappedValid={mappedValid}"
                >> Log.addContext "mappedPath" (if mappedLoc.IsValid then mappedLoc.Path else "invalid")
                >> Log.addContext "sourcePath" (Option.ofObj loc.SourceTree |> Option.map (fun st -> st.FilePath) |> Option.defaultValue "null")
                >> Log.addContext "mappedValid" mappedLoc.IsValid
            )

            if mappedLoc.IsValid && File.Exists(mappedLoc.Path) then
                toLspLocation mappedLoc.Path mappedLoc.Span
                |> Some
            elif Option.ofObj loc.SourceTree |> Option.exists (fun st -> File.Exists(st.FilePath)) then
                toLspLocation loc.SourceTree.FilePath (loc.GetLineSpan().Span)
                |> Some
            else
                logger.warn (
                    Log.setMessage "Could not find valid file path for location"
                )
                None

        | _ -> 
            logger.debug (
                Log.setMessage "Skipping non-source location: kind={kind}"
                >> Log.addContext "kind" (loc.Kind.ToString())
            )
            None


module TextEdit =
    let fromTextChange (lines: TextLineCollection) (changes: TextChange): TextEdit =
        { Range = changes.Span |> Range.fromTextSpan lines
          NewText = changes.NewText }


module SymbolKind =
    let fromSymbol (symbol: ISymbol): SymbolKind =
        match symbol with
        | :? ILocalSymbol -> SymbolKind.Variable
        | :? IFieldSymbol as fs ->
            if not(isNull fs.ContainingType) && fs.ContainingType.TypeKind = TypeKind.Enum && fs.HasConstantValue then
                SymbolKind.EnumMember
            else
                SymbolKind.Field
        | :? IPropertySymbol -> SymbolKind.Property
        | :? IMethodSymbol as ms ->
            match ms.MethodKind with
            | MethodKind.Constructor -> SymbolKind.Constructor
            | MethodKind.StaticConstructor -> SymbolKind.Constructor
            | MethodKind.BuiltinOperator -> SymbolKind.Operator
            | MethodKind.UserDefinedOperator -> SymbolKind.Operator
            | MethodKind.Conversion -> SymbolKind.Operator
            | _ -> SymbolKind.Method
        | :? ITypeSymbol as ts ->
            match ts.TypeKind with
            | TypeKind.Class -> SymbolKind.Class
            | TypeKind.Enum -> SymbolKind.Enum
            | TypeKind.Struct -> SymbolKind.Struct
            | TypeKind.Interface -> SymbolKind.Interface
            | TypeKind.Delegate -> SymbolKind.Class
            | TypeKind.Array -> SymbolKind.Array
            | TypeKind.TypeParameter -> SymbolKind.TypeParameter
            | _ -> SymbolKind.Class
        | :? IEventSymbol -> SymbolKind.Event
        | :? INamespaceSymbol -> SymbolKind.Namespace
        | _ -> SymbolKind.File


module SymbolName =
    let fromSymbol (format: SymbolDisplayFormat) (symbol: ISymbol): string = symbol.ToDisplayString(format)


module CallHierarchyItem =
    let private displayStyle =
        SymbolDisplayFormat(
            typeQualificationStyle = SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions = SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions =
                (SymbolDisplayMemberOptions.IncludeParameters
                 ||| SymbolDisplayMemberOptions.IncludeExplicitInterface),
            parameterOptions =
                (SymbolDisplayParameterOptions.IncludeParamsRefOut
                 ||| SymbolDisplayParameterOptions.IncludeExtensionThis
                 ||| SymbolDisplayParameterOptions.IncludeType),
            miscellaneousOptions = SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        )

    let fromSymbolAndLocation (symbol: ISymbol) (location: Location): CallHierarchyItem =
        let kind = SymbolKind.fromSymbol symbol
        let containingType = (symbol.ContainingType :> ISymbol) |> Option.ofObj
        let containingNamespace = (symbol.ContainingNamespace :> ISymbol) |> Option.ofObj

        { Name = symbol.ToDisplayString(displayStyle)
          Kind = kind
          Tags = None
          Detail =
            containingType
            |> Option.orElse containingNamespace
            |> Option.map (fun sym -> sym.ToDisplayString())
          Uri = location.Uri
          Range = location.Range
          SelectionRange = location.Range
          Data = None }

    let fromSymbol (wmResolveSymbolLocations: ISymbol -> Project option -> Async<list<Location>>) (symbol: ISymbol): Async<CallHierarchyItem list> =
        wmResolveSymbolLocations symbol None
        |> Async.map (List.map (fromSymbolAndLocation symbol))

module TypeHierarchyItem =
    let private displayStyle =
        SymbolDisplayFormat(
            typeQualificationStyle = SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions = SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions =
                (SymbolDisplayMemberOptions.IncludeParameters
                 ||| SymbolDisplayMemberOptions.IncludeExplicitInterface),
            parameterOptions =
                (SymbolDisplayParameterOptions.IncludeParamsRefOut
                 ||| SymbolDisplayParameterOptions.IncludeExtensionThis
                 ||| SymbolDisplayParameterOptions.IncludeType),
            miscellaneousOptions = SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        )

    let fromSymbolAndLocation (symbol: ISymbol) (location: Location): TypeHierarchyItem =
        let kind = SymbolKind.fromSymbol symbol
        let containingType = (symbol.ContainingType :> ISymbol) |> Option.ofObj
        let containingNamespace = (symbol.ContainingNamespace :> ISymbol) |> Option.ofObj

        { Name = symbol.ToDisplayString(displayStyle)
          Kind = kind
          Tags = None
          Detail =
            containingType
            |> Option.orElse containingNamespace
            |> Option.map (fun sym -> sym.ToDisplayString())
          Uri = location.Uri
          Range = location.Range
          SelectionRange = location.Range
          Data = None }

    let fromSymbol (wmResolveSymbolLocations: ISymbol -> Project option -> Async<list<Location>>) (symbol: ISymbol): Async<TypeHierarchyItem list> =
        wmResolveSymbolLocations symbol None
        |> Async.map (List.map (fromSymbolAndLocation symbol))

module SymbolInformation =
    let fromSymbol (format: SymbolDisplayFormat) (symbol: ISymbol): SymbolInformation list =
        let toSymbolInformation loc =
            { Name = SymbolName.fromSymbol format symbol
              Kind = SymbolKind.fromSymbol symbol
              Location = loc
              ContainerName = None
              Deprecated = None
              Tags = None }

        symbol.Locations
        |> Seq.map Location.fromRoslynLocation
        |> Seq.filter _.IsSome
        |> Seq.map _.Value
        |> Seq.map toSymbolInformation
        |> Seq.toList


module DiagnosticSeverity =
    let fromRoslynDiagnosticSeverity (sev: Microsoft.CodeAnalysis.DiagnosticSeverity): DiagnosticSeverity =
        match sev with
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Info -> DiagnosticSeverity.Information
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Warning -> DiagnosticSeverity.Warning
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Error -> DiagnosticSeverity.Error
        | _ -> DiagnosticSeverity.Warning


module Diagnostic =
    let fromRoslynDiagnostic (diagnostic: Microsoft.CodeAnalysis.Diagnostic): Diagnostic =
        let diagnosticCodeUrl = diagnostic.Descriptor.HelpLinkUri |> Option.ofObj
        { Range = diagnostic.Location.GetLineSpan().Span |> Range.fromLinePositionSpan
          Severity = Some (diagnostic.Severity |> DiagnosticSeverity.fromRoslynDiagnosticSeverity)
          Code = Some (U2.C2 diagnostic.Id)
          CodeDescription = diagnosticCodeUrl |> Option.map (fun x -> { Href = x |> URI })
          Source = Some "lsp"
          Message = diagnostic.GetMessage()
          RelatedInformation = None
          // TODO: Convert diagnostic.Descriptor.CustomTags to Tags
          Tags = None
          Data = None }


module CompletionContext =
    let toCompletionTrigger (context: CompletionContext option): Completion.CompletionTrigger =
        context
        |> Option.bind (fun ctx ->
            match ctx.TriggerKind with
            | CompletionTriggerKind.Invoked
            | CompletionTriggerKind.TriggerForIncompleteCompletions ->
                Some Completion.CompletionTrigger.Invoke
            | CompletionTriggerKind.TriggerCharacter ->
                ctx.TriggerCharacter
                |> Option.map Seq.head
                |> Option.map Completion.CompletionTrigger.CreateInsertionTrigger
            | _ -> None)
        |> Option.defaultValue (Completion.CompletionTrigger.Invoke)


module CompletionDescription =
    let toMarkdownString (description: CompletionDescription) : string =
        description.TaggedParts
        |> Seq.map (fun taggedText ->
            // WTF, if the developers of Roslyn don't want users to use TaggedText, why they set TaggedText to public?
            // If they indeed want users to use it, why they set lots of imported fields to internal?
            match taggedText.Tag with
            // TODO: Support code block?
            | "CodeBlockStart"   -> "`` " + taggedText.Text
            | "CodeBlockEnd"     -> " ``" + taggedText.Text
            | TextTags.LineBreak -> "\n\n"
            | _                  -> taggedText.Text)
        |> String.concat ""

    let toDocumentation (description: CompletionDescription) : MarkupContent =
        { Kind = MarkupKind.Markdown; Value = toMarkdownString description }


module Documentation =
    let fromCompletionDescription = CompletionDescription.toDocumentation
