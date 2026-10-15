---
name: vs-project
description: "Visual Studio/MSBuild project knowledge for .NET solutions: SDK-style vs legacy .csproj, TargetFramework(s), LangVersion, Nullable, Directory.Build.props, PackageReference/NuGet, .sln/.slnx, VSIX projects. Read before hand-editing .csproj/.props/.sln/.slnx or diagnosing restore/build/target-framework errors."
---

# Visual Studio projects (pomp)

Use only tools that are in your tool list: pomp offers a subset of its host tools, and the "Visual Studio host (pomp)" section of the system prompt names the ones offered. If `query_solution` is among them, use it for solution structure; if `add_project_file` is, use it to add files to projects; if `manage_nuget` is, use it for packages; verify with `build_solution` + `read_error_list` when offered (otherwise `dotnet build` / `msbuild` in bash). A project file open in VS may hold unsaved edits: if `read_buffer` and `edit_buffer_hashline` are among your tools, use them on it; otherwise never overwrite it on disk (the pomp guard blocks that) - ask the user to save it in Visual Studio (Ctrl+S), then retry with the normal tools. VS reloads projects changed on disk (the user sees a prompt): keep hand edits few and small, and preserve BOM and line endings.

## SDK-style vs legacy
- **SDK-style**: `<Project Sdk="Microsoft.NET.Sdk">` (also `.Web`, `.Worker`, `.Razor`, `.BlazorWebAssembly`). Short file; sources are globbed (`**/*.cs`, `**/*.resx`), `bin/` and `obj/` excluded, so new source files are picked up automatically: **never add `<Compile Include>` for new files** (duplicate items, NETSDK1022), just create the file. Opt out with `EnableDefaultCompileItems=false`. A project nested in another project's folder is globbed by the parent: `<Compile Remove="Sub\**" />`.
- **Legacy** (.NET Framework era): `<Project ToolsVersion=... xmlns=".../msbuild/2003">`, `<Import ...Microsoft.CSharp.targets>`, one `<Compile Include>` per file, `<Reference>` + `HintPath`, `packages.config`, `Properties\AssemblyInfo.cs`, `<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>`. Added/removed files must be reflected in the csproj: use `add_project_file` if it is among your tools, otherwise make the smallest possible edit of the `<Compile Include>` items (or ask the user to add the file in Visual Studio). Legacy, VSIX and C++ projects often need Visual Studio's MSBuild (`dotnet build` may fail): use `build_solution` when it is offered.

## TargetFramework, LangVersion, Nullable
- `<TargetFramework>net8.0</TargetFramework>`; multi-target with `<TargetFrameworks>net8.0;net48</TargetFrameworks>` (plural, `;`-separated; per-TFM `Condition="'$(TargetFramework)'=='net48'"`; symbols `NET8_0_OR_GREATER`, `NETFRAMEWORK`, `NETSTANDARD2_0`). Monikers: `net10.0`/`net9.0`/`net8.0` (modern .NET), `netstandard2.0`/`2.1`, `net48`/`net472` (.NET Framework), `net8.0-windows` (+ `UseWPF`/`UseWindowsForms`).
- Default C# version by TFM: net10 = 14, net9 = 13, net8 = 12, net7 = 11, net6 = 10, net5 = 9, netcoreapp3.x/netstandard2.1 = 8, .NET Framework/netstandard2.0 = 7.3. Override with `<LangVersion>` (`latest`, `preview`, `12.0`); new syntax on old runtimes can still need polyfills (records/`init` need `IsExternalInit`).
- `<Nullable>enable</Nullable>` turns on nullable reference analysis (CS86xx warnings; off unless the template or a props file set it). `<ImplicitUsings>enable</ImplicitUsings>` (net6+) adds global usings. `TreatWarningsAsErrors`, `WarningsAsErrors`, `NoWarn` control diagnostics.

## Properties and items
- Properties: `OutputType` (Exe/WinExe/Library), `AssemblyName`, `RootNamespace`, `Version`, `IsPackable`, `AllowUnsafeBlocks`, `DefineConstants`, `PlatformTarget`, `RuntimeIdentifier`. Last assignment wins; `Condition="'$(Configuration)'=='Debug'"`.
- Items: `PackageReference`, `ProjectReference Include="..\Lib\Lib.csproj"`, `FrameworkReference`, `Reference` (legacy), `None|Content|EmbeddedResource` with `Include|Update|Remove` (`<None Update="a.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>`), `Using`, `InternalsVisibleTo`. `$(SolutionDir)` is empty when a lone project is built.

## Shared build files (nearest ancestor wins; only the first found is imported)
- `Directory.Build.props` (evaluated before the project body: defaults, the csproj may override) and `Directory.Build.targets` (after). Chain to a parent with `<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />`.
- `Directory.Packages.props` (central package management): `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` + `<PackageVersion Include="X" Version="1.2.3" />`; `PackageReference` must then omit `Version` (NU1008) or use `VersionOverride`.
- `global.json` (pins the SDK: `sdk.version`, `rollForward`), `nuget.config` (sources, `packageSourceMapping`), `.editorconfig`, `packages.lock.json`.

## NuGet
- Modern: `PackageReference` in the csproj (`Version` exact `1.2.3`, floating `1.*`, range `[1.0,2.0)`; `PrivateAssets="all"` for analyzers/dev-only); restore writes `obj/project.assets.json` and VS restores on build. Legacy: `packages.config` + `packages\` + `HintPath` (VS can migrate it to PackageReference; for these projects ask the user to use Visual Studio's NuGet UI). If `manage_nuget` is among your tools use it to list/add/update packages; otherwise prefer `dotnet add package <id> [--version <v>]` (the bash tool asks for approval; SDK-style projects) over hand-editing `PackageReference` items, then verify the build.
- Common errors: NU1101/NU1102 package or version not found (check sources), NU1605 downgrade, NETSDK1004 assets file missing (restore), NETSDK1045 SDK too old for the TFM (`global.json`/install SDK), MSB3644 targeting pack missing, MSB3277 assembly version conflict.

## Solutions
- `.sln` (text, CRLF): `Project("{type-guid}") = "Name", "rel\path.csproj", "{project-guid}"` ... `EndProject`, then `GlobalSection(SolutionConfigurationPlatforms | ProjectConfigurationPlatforms | NestedProjects)`. Type GUIDs: `{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}` C# legacy, `{9A19103F-16F7-4668-BE54-9A1E7A4F7556}` C# SDK-style, `{2150E333-8FDC-42A3-9474-1A3956D46DE8}` solution folder. Do not invent GUIDs for existing projects; add projects via VS or `dotnet sln add`.
- `.slnx` (XML, VS 17.14+/.NET SDK 9.0.200+): `<Solution><Folder Name="/src/"><Project Path="src/App/App.csproj" /></Folder></Solution>`; no GUIDs; `dotnet sln migrate` converts.

## VSIX (Visual Studio extension) projects
- Usually a legacy-style csproj targeting `net472`+ (in-process VSSDK; VS 2019 = 16.x, 2022 = 17.x, 2026 = 18.x) using `Microsoft.VisualStudio.SDK` + `Microsoft.VSSDK.BuildTools`; `ProjectTypeGuids` include `{82b43b9b-a64c-4715-b499-d71e9ca2bd60}`. Out-of-process VisualStudio.Extensibility projects target `net8.0`+.
- Key files: `source.extension.vsixmanifest` (`Identity` Id/Version/Publisher; `InstallationTarget` range such as `[17.0,18.0)`, with `ProductArchitecture` `amd64` for 2022+; `Assets` such as `Microsoft.VisualStudio.VsPackage` -> `%CurrentProject%`, MEF parts via `Microsoft.VisualStudio.MefComponent`), `*.vsct` command table (GUIDs/IDs must match the code), an `AsyncPackage` subclass (`[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]`, `[Guid]`, `[ProvideMenuResource]`).
- Output is a `.vsix`; F5 launches the Experimental instance (`/rootsuffix Exp`). Touch VS services/DTE only on the UI thread (`await JoinableTaskFactory.SwitchToMainThreadAsync()`).
