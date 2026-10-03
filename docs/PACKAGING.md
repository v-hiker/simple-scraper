# 发布形式与体积

## 为什么 0.1.0 的包较大

0.1.0 的 Windows x64 ZIP 为 111,786,705 字节，解压后约 293 MB。自包含发布带有 .NET 10、Windows App SDK 和 WinUI 运行库，可以在未预装这些运行库的电脑上运行。

本地发布目录中较大的文件如下，表中 MB 按 1,000,000 字节计算：

| 文件 | 体积 |
|---|---:|
| Microsoft.Windows.SDK.NET.dll | 58.56 MB |
| onnxruntime.dll | 21.66 MB |
| DirectML.dll | 18.70 MB |
| Microsoft.WinUI.dll | 16.42 MB |
| System.Private.CoreLib.dll | 16.03 MB |
| Microsoft.ui.xaml.dll | 15.32 MB |

ONNX 和 DirectML 随 SDK 的原生运行时载荷进入发布目录。应用自身不提供机器学习功能，但不能只删除 DLL 而保留引用它们的运行时清单。

## 自包含单文件

0.1.1 本地发布构建的单文件 EXE 为 95,614,447 字节（约 95.6 MB），比 0.1.0 的 ZIP 下载量减少约 14.5%；新版 ZIP 为 96,721,699 字节（约 96.7 MB），减少约 13.5%。不同构建的归档元数据可能造成少量字节差异，实际下载文件以 Release 及其 SHA-256 为准。

单文件版使用 Windows App SDK 支持的 `PublishSingleFile`、`IncludeAllContentForSelfExtract` 和压缩选项，将运行库、PRI/XAML、图标、指南及许可证装入一个 EXE。用户下载后直接运行，首次启动由 .NET 自动解包；Windows 默认缓存位置在 `%TEMP%/.net` 下。设置、匹配和历史仍在 `%LOCALAPPDATA%/SimpleScraper`。

编译后的四个 XBF 同时保留在应用 PRI 的 EmbeddedData 中，并在 bundle 生成前作为独立项加入单文件载荷，供 WinUI 从解包目录加载。构建验证核对两个位置的 XBF 字节一致，法律文件和指南也在 bundle 生成前加入。

单文件与目录发布使用独立的 WinRT 注册清单中间目录，并在切换形式时刷新编译输入。单文件清单中的运行库路径指向自动解包目录；发布脚本分别检查两种 EXE 的内嵌清单，避免编译缓存复用错误路径。

应用 PRI 使用固定文件名 `resources.pri`。这是[微软提供的资源查找修复方式](https://github.com/microsoft/WindowsAppSDK/issues/6248#issuecomment-5211778390)，避免可执行文件带版本号或被浏览器改名后，WinUI 从 EXE 名推导出错误的 PRI 路径。正式发布文件名仍需实际冷启动验证。

为了减少体积，发布关闭额外的 ReadyToRun 编译。当前配置与元数据使用反射式 JSON，因此保持 `PublishTrimmed=false`，保留序列化契约和全部运行时原生依赖。

## 目录 ZIP

ZIP 版同样自包含，需要完整解压后运行 `SimpleScraper.exe`。它保留直接可见的运行库、XAML、指南和许可证，适合查看文件或排查问题。

## 共享运行库实验

本地框架依赖发布实验的 ZIP 约 29.36 MB，需要预先安装匹配的 .NET 10 x64 与 Windows App SDK 运行库。该形式尚未完成实际窗口启动验收，不作为本次正式下载包。

## 参考

- [Microsoft：非打包 WinUI 3 与单文件 EXE](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)
- [Microsoft：.NET 单文件部署](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [Microsoft：ReadyToRun 的体积与启动权衡](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run)
