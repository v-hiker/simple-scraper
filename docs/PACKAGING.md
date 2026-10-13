# Windows 发行形式

| 文件 | 用途 | Windows 注册 |
|---|---|---|
| `SimpleScraper-版本-win-x64-setup.exe` | 用户级安装与升级 | 开始菜单、已安装应用及卸载入口 |
| `SimpleScraper-版本-win-x64.exe` | 单文件直接运行 | 直接运行 |
| `SimpleScraper-版本-win-x64.zip` | 完整解压后运行 | 便携使用 |

三种形式均包含 .NET、Windows App SDK、WinUI、界面资源、指南和许可证，不需要另外安装 .NET。安装包使用固定 AppId 和 `%LOCALAPPDATA%\Programs\SimpleScraper` 安装目录。

个人配置、媒体库、缓存和历史统一保存在 `%LOCALAPPDATA%\SimpleScraper`，不依赖当前工作目录、程序文件名或版本号。安装和卸载只处理程序文件，保留个人数据。

## 构建

`scripts/build.ps1` 生成自包含目录、ZIP 和单文件 EXE。`scripts/build-installer.ps1` 从自包含目录生成 Inno Setup 安装包。GitHub Actions 为全部文件生成 SHA-256 校验文件，并将同一次构建的产物用于 Release。

单文件版将运行库、PRI、XAML 和文档装入 EXE，首次启动会解包运行。构建检查独立 XBF 与 PRI 中的资源一致性，以及两种发布形式的 WinRT 注册清单。应用 PRI 的文件名固定为 `resources.pri`。

反射式 JSON 配置与元数据保持 `PublishTrimmed=false`，发布关闭 ReadyToRun 编译，并保留运行库依赖。
