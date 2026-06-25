# C# Language Server - Visual Studio Enhanced Fork

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)
[![Visual Studio](https://img.shields.io/badge/Visual%20Studio-Supported-blue.svg)](https://visualstudio.microsoft.com/)

**Enhanced by Zach Christmas** | **Original by Saulius Menkevičius**

This is a fork of the excellent [csharp-language-server](https://github.com/razzmatazz/csharp-language-server) project with enhancements for Visual Studio MSBuild support and lazy solution loading.

## 🚀 What's New in This Fork

### Visual Studio MSBuild Support
- ✅ **Automatic detection** of the installed Visual Studio (2022 or 2026 — Community, Professional, or Build Tools) via vswhere
- ✅ **Environment variable integration** (`VS170COMNTOOLS` and future versions)
- ✅ **Custom MSBuild path configuration** options
- ✅ **Intelligent fallback** to auto-discovery

### Lazy Solution Loading
- ⚡ **On-demand loading** - solutions load only when you open C# files
- 🚀 **Improved startup performance** - no more waiting for all solutions to load
- 🎯 **Multi-solution support** - handle complex workspaces efficiently
- 💾 **Memory optimization** - only loads what you're actively working on

## 📦 Installation

```bash
# Install as global .NET tool
dotnet tool install --global csharp-ls-vs

# Verify installation
csharp-ls-vs --version
```

## ⚙️ Usage

### Command Line Options
```bash
# Basic usage
csharp-ls-vs

# With specific solution
csharp-ls-vs --solution MySolution.sln

# With custom MSBuild path (Visual Studio)
csharp-ls-vs --msbuildpath "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin"

# With custom MSBuild executable
csharp-ls-vs --msbuildexepath "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
```

### VS Code Integration
Use with the companion VS Code extension: [csharp-ls-vs](https://marketplace.visualstudio.com/items?itemName=ZacharyChristmas.csharp-ls-vs)

## 🛠️ Requirements

- **.NET 9.0 SDK** or later
- **Visual Studio** Community/Professional (recommended for MSBuild)
- **Windows** (primary platform, may work on other platforms)

## 📄 Attribution & License

### 🙏 Original Work
This project is a fork of the outstanding work by:
- **[Saulius Menkevičius](https://github.com/razzmatazz)** - Original [csharp-language-server](https://github.com/razzmatazz/csharp-language-server)

### 🔧 Fork Enhancements
- **[Zach Christmas](https://github.com/zachristmas)** - Visual Studio MSBuild support and lazy loading features

### 📋 License
This project maintains the same MIT license as the original work.

### ⚠️ Support Notice
**This is a fork with limited support.** For general language server issues not related to Visual Studio MSBuild or lazy loading, please check the [original project](https://github.com/razzmatazz/csharp-language-server) first.

For issues specific to this fork's enhancements, please use the [fork's issue tracker](https://github.com/zachristmas/csharp-language-server/issues).

## 🔗 Related Projects

- **Original Language Server**: [razzmatazz/csharp-language-server](https://github.com/razzmatazz/csharp-language-server)
- **VS Code Extension Fork**: [zachristmas/vscode-csharp-ls-vs](https://github.com/zachristmas/vscode-csharp-ls-vs)
- **Original VS Code Extension**: [vytautassurvila/vscode-csharp-ls](https://github.com/vytautassurvila/vscode-csharp-ls)

---

**Thank you to the original authors for their excellent foundation!** 🎉

## 📝 Changelog

**1.2.4** — Visual Studio 2026 / MSBuild v18 support: the net472 build host now self-aligns its MSBuild dependencies to the installed Visual Studio at startup (via vswhere), fixing the `XMakeElements` crash when loading old-style .NET Framework solutions on machines with VS 2026. No-op on VS 2022.

See the full [CHANGELOG](https://github.com/zachristmas/csharp-language-server/blob/main/CHANGELOG.md).
