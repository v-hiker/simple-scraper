# Resolved dependencies

Audited on 2026-10-03 against the committed NuGet lock file, restored package metadata, and the actual Windows x64 self-contained publish manifest.

The project uses the current stable Windows App SDK 2.5.1, including WinUI 2.3.9. Microsoft platform binaries retain their package distribution terms; the project's MIT license applies to its own source and original artwork. [Microsoft release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels).

Original license and component notice files are included under `ThirdPartyLicenses/<package>/<version>/` in the published application. `ThirdPartyLicenses/inventory.json` records the exact files and SHA-256 values. The collection script does not accept licenses on anyone's behalf.

| Resolved package / pack | Version | Publish evidence / role | License source |
|---|---|---|---|
| Microsoft.Web.WebView2 | 1.0.3719.77 | Runtime manifest: `WebView2Loader.dll` | [BSD-3-Clause with component notices](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77/License) |
| Microsoft.Windows.AI.MachineLearning | 2.1.74 | Runtime manifest: `DirectML.dll`, `Microsoft.ML.OnnxRuntime.dll` and 3 more files | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.Windows.AI.MachineLearning/2.1.74/License) |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.1839 | Build/targeting download; no entry in runtime manifest | [Microsoft Windows SDK terms](https://aka.ms/WinSDKLicenseURL) |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 | Build/targeting download; no entry in runtime manifest | [Microsoft Windows SDK terms](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools.MSIX/1.7.251221100/License) |
| Microsoft.WindowsAppSDK | 2.5.1 | Runtime dependency / SDK-managed content | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1/License) |
| Microsoft.WindowsAppSDK.AI | 2.5.5 | Runtime manifest: `Microsoft.Graphics.Imaging.Projection.dll`, `Microsoft.Windows.AI.ContentSafety.Projection.dll` and 5 more files | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.AI/2.5.5/License) |
| Microsoft.WindowsAppSDK.Base | 2.0.4 | SDK dependency / build-managed runtime content | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Base/2.0.4/License) |
| Microsoft.WindowsAppSDK.DWrite | 2.1.0 | SDK dependency / build-managed runtime content | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.DWrite/2.1.0/License) |
| Microsoft.WindowsAppSDK.Foundation | 2.3.12 | Runtime manifest: `Microsoft.Security.Authentication.OAuth.Projection.dll`, `Microsoft.Windows.AppLifecycle.Projection.dll` and 19 more files | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Foundation/2.3.12/License) |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.9 | Runtime manifest: `Microsoft.InteractiveExperiences.Projection.dll` | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.InteractiveExperiences/2.1.9/License) |
| Microsoft.WindowsAppSDK.ML | 2.1.94 | Runtime dependency / SDK-managed content | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.ML/2.1.94/License) |
| Microsoft.WindowsAppSDK.Runtime | 2.5.1 | SDK dependency / build-managed runtime content | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Runtime/2.5.1/License) |
| Microsoft.WindowsAppSDK.Search | 2.5.5 | Runtime manifest: `Microsoft.Windows.Search.Projection.dll` | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Search/2.5.5/License) |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | Runtime manifest: `Microsoft.Windows.Widgets.Projection.dll` | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.Widgets/2.0.5/License) |
| Microsoft.WindowsAppSDK.WinUI | 2.3.9 | Runtime manifest: `Microsoft.WinUI.dll` | [Microsoft Windows App SDK / Windows ML terms](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.9/License) |
| System.Numerics.Tensors | 9.0.0 | Runtime manifest: `System.Numerics.Tensors.dll` | [MIT with component notices](https://licenses.nuget.org/MIT) |
| Microsoft.AspNetCore.App.Runtime.win-x64 | 10.0.12 | Build/targeting download; no entry in runtime manifest | [MIT with component notices](https://licenses.nuget.org/MIT) |
| Microsoft.NETCore.App.Crossgen2.win-x64 | 10.0.12 | Build/targeting download; no entry in runtime manifest | [MIT with component notices](https://licenses.nuget.org/MIT) |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.12 | Runtime manifest: `Microsoft.CSharp.dll`, `Microsoft.DiaSymReader.Native.amd64.dll` and 185 more files | [MIT with component notices](https://licenses.nuget.org/MIT) |
| Microsoft.Windows.SDK.NET.Ref | 10.0.26100.57 | Runtime manifest: `Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll` | [Microsoft Windows SDK terms](https://aka.ms/WinSDKLicenseURL) |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.12 | Build/targeting download; no entry in runtime manifest | [MIT with component notices](https://licenses.nuget.org/MIT) |

The NuGet lock file records package content hashes for the application graph. SDK-downloaded runtime and targeting pack versions are pinned in the resolved assets and publish manifest, with exact legal texts retained by the collector. `scripts/collect-runtime-notices.ps1` verifies manifest/package consistency and rejects the engineering-preview license before release.

Windows App SDK package terms permit the files placed beside a functional Windows application to be redistributed under their distribution conditions. Those conditions include retaining notices and providing protective terms to distributors and end users. The bundled texts are authoritative; the application MIT license does not substitute for them.

WebView2 SDK files use the package's BSD 3-Clause license and its notice text. This package does not bundle the full Microsoft Edge WebView2 browser runtime. Windows system fonts and Fluent glyphs are supplied by Windows; no font binary is copied into the source or package.

TagLibSharp, MKVToolNix, CommunityToolkit and assets from the original application are not resolved or bundled by this project.
