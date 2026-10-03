# Third-party notices

SimpleScraper's own source and original icon artwork use MIT. The application depends on separately licensed platform components; its MIT license does not replace their terms.

| Component | Use | License / authoritative source |
|---|---|---|
| Microsoft Windows App SDK 2.5.1, including WinUI 2.3.9 | WinUI desktop runtime and SDK-managed runtime components | Microsoft Software License Terms from the exact packages: [Windows App SDK](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1/License), [WinUI](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI/2.3.9/License). The source repository's MIT license does not replace these binary distribution terms. |
| Microsoft.Windows.SDK.BuildTools 10.0.28000.1839 | Build-only Windows tooling | Microsoft package license; see the package metadata and license included by Microsoft |
| Microsoft.Windows.SDK.BuildTools.MSIX 1.7.251221100 | Build-only Windows packaging tooling | [Exact package license](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools.MSIX/1.7.251221100/License) and its original notice text |
| .NET runtime 10.0.12 | Self-contained application runtime in the audited release | MIT and component-specific notices: [versioned runtime license](https://github.com/dotnet/runtime/blob/v10.0.12/LICENSE.TXT), [versioned third-party notices](https://github.com/dotnet/runtime/blob/v10.0.12/THIRD-PARTY-NOTICES.TXT) |
| Microsoft.Web.WebView2 1.0.3719.77 | SDK loader and projection components required by WinUI; full browser runtime is not bundled | [Exact package BSD 3-Clause license](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77/License) and its original `NOTICE.txt` |
| Microsoft.Windows.SDK.NET.Ref 10.0.26100.57 | Windows API projections and WinRT runtime support | [Microsoft Windows SDK terms](https://aka.ms/WinSDKLicenseURL), retained as `sdk_license.rtf` in the package |
| System.Numerics.Tensors 9.0.0 | Resolved Windows ML dependency | [MIT](https://www.nuget.org/packages/System.Numerics.Tensors/9.0.0/License) and its original third-party notices |
| Windows system fonts and Fluent glyphs | UI supplied by Windows | Windows/system component terms; no font binaries are copied into this repository |

The complete resolved package/version list, including Windows App SDK transitives, is recorded in [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md). The committed NuGet lock files are restored with an explicit `win-x64` runtime using `scripts/restore.ps1`.

`scripts/collect-runtime-notices.ps1` copies original license and notice texts byte-for-byte into `ThirdPartyLicenses/<package>/<version>/` in every published application, and preserves any notices emitted by the SDK. Its `inventory.json` records exact versions and SHA-256 values. The application's own `LICENSE` remains separate from the .NET Foundation copyright and license text.

The Microsoft platform components retain their applicable license and redistribution conditions. Windows App SDK terms permit package-binplaced files to accompany a functional Windows application subject to their distribution requirements, including protective terms for distributors and end users. Distributing the MIT application source does not relicense those components, and copying notices does not itself establish agreement to their terms. The full bundled license texts govern component use and redistribution.

No CommunityToolkit, TagLibSharp, MKVToolNix or original application assets are bundled by this project.

Metadata and artwork services:
- [TMDB API documentation](https://developer.themoviedb.org/docs): this product uses the TMDB API and is not endorsed or certified by TMDB. Users supply their own keys. Attribution/trademarks and API use follow TMDB's terms.
- [Bangumi API](https://github.com/bangumi/api): metadata/artwork and service access are governed by the respective service and rights holders.
- Google translation, LibreTranslate and configured compatible translation endpoints have their own availability, privacy and usage terms. Translation is initiated through the user's selected provider.

Posters, actor photos, episode thumbnails and externally obtained text are not redistributed as repository example assets. API schema field names and framework interfaces are interoperability contracts. Review the exact resolved package licenses when updating dependencies.
