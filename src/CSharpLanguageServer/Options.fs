namespace CSharpLanguageServer

module Options =
  open Argu

  type CLIArguments =
      | [<AltCommandLine("-v")>] Version
      | [<AltCommandLine("-l")>] LogLevel of level:string
      | [<AltCommandLine("-s")>] Solution of solution:string
      | [<AltCommandLine("-m")>] MSBuildPath of path:string
      | [<AltCommandLine("-e")>] MSBuildExePath of path:string
      with
          interface IArgParserTemplate with
              member s.Usage =
                  match s with
                  | Version -> "display versioning information"
                  | Solution _ -> ".sln file to load (relative to CWD)"
                  | LogLevel _ -> "log level, <log|info|warning|error>; default is `log`"
                  | MSBuildPath _ -> "path to MSBuild directory (e.g., 'C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild')"
                  | MSBuildExePath _ -> "path to MSBuild executable (e.g., 'C:\\Program Files\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\MSBuild.exe')"
