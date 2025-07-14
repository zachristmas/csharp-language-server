# C# Language Server - VS 2022 Enhanced Fork

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-purple.svg)](https://dotnet.microsoft.com/)
[![Visual Studio 2022](https://img.shields.io/badge/VS-2022-blue.svg)](https://visualstudio.microsoft.com/)

**Enhanced by Zach Christmas** | **Original by Saulius Menkevičius**

This is a fork of the excellent [csharp-language-server](https://github.com/razzmatazz/csharp-language-server) project with enhancements for Visual Studio 2022 MSBuild support and lazy solution loading.

## 🚀 What's New in This Fork

### Visual Studio 2022 MSBuild Support
- ✅ **Automatic detection** of VS 2022 Community/Professional installations
- ✅ **Environment variable integration** (`VS170COMNTOOLS` support)
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
dotnet tool install --global csharp-ls

# Verify installation
csharp-ls --version
```

## ⚙️ Usage

### Command Line Options
```bash
# Basic usage
csharp-ls

# With specific solution
csharp-ls --solution MySolution.sln

# With custom MSBuild path (VS 2022 Community)
csharp-ls --msbuildpath "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin"

# With custom MSBuild executable
csharp-ls --msbuildexepath "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
```

### VS Code Integration
Use with the companion VS Code extension: [csharp-ls (VS 2022 Fork)](https://marketplace.visualstudio.com/items?itemName=zachchristmas.csharp-ls)

## 🛠️ Requirements

- **.NET 9.0 SDK** or later
- **Visual Studio 2022** Community/Professional (recommended for MSBuild)
- **Windows** (primary platform, may work on other platforms)

## 📄 Attribution & License

### 🙏 Original Work
This project is a fork of the outstanding work by:
- **[Saulius Menkevičius](https://github.com/razzmatazz)** - Original [csharp-language-server](https://github.com/razzmatazz/csharp-language-server)

### 🔧 Fork Enhancements
- **[Zach Christmas](https://github.com/zachristmas)** - VS 2022 MSBuild support and lazy loading features

### 📋 License
This project maintains the same MIT license as the original work.

### ⚠️ Support Notice
**This is a fork with limited support.** For general language server issues not related to VS 2022 MSBuild or lazy loading, please check the [original project](https://github.com/razzmatazz/csharp-language-server) first.

For issues specific to this fork's enhancements, please use the [fork's issue tracker](https://github.com/zachristmas/csharp-language-server/issues).

## 🔗 Related Projects

- **Original Language Server**: [razzmatazz/csharp-language-server](https://github.com/razzmatazz/csharp-language-server)
- **VS Code Extension Fork**: [zachristmas/vscode-csharp-ls-vs](https://github.com/zachristmas/vscode-csharp-ls-vs)
- **Original VS Code Extension**: [vytautassurvila/vscode-csharp-ls](https://github.com/vytautassurvila/vscode-csharp-ls)

---

**Thank you to the original authors for their excellent foundation!** 🎉
