# 简单刮削器 · SimpleScraper

Windows 电影、电视剧与动画媒体库管理工具。支持中文界面、元数据匹配与编辑、NFO 和图片输出，以及带预览和冲突检查的文件重命名。

[下载 Windows 版本](https://github.com/v-hiker/simple-scraper/releases/latest) · [使用指南](docs/USER_GUIDE.md)

## 安装

推荐下载 `SimpleScraper-版本-win-x64-setup.exe`。安装后可以从开始菜单打开“简单刮削器”，也可以在 Windows“设置 → 应用 → 已安装的应用”中卸载。支持 Windows 10 1809 及以上的 x64 系统，无需另外安装 .NET。

升级时运行新版安装包即可。程序安装在 `%LOCALAPPDATA%\Programs\SimpleScraper`，个人设置、媒体库、缓存和重命名历史保存在 `%LOCALAPPDATA%\SimpleScraper`；升级和卸载保留这些数据。

还提供直接运行的单文件 EXE 和便携 ZIP。ZIP 需要完整解压。这两种形式使用相同的个人数据目录。测试时可以用 `SIMPLE_SCRAPER_DATA_HOME` 指定隔离数据目录。

## 功能

- 电影与剧集目录扫描、搜索和筛选，可配置列表列，展开季与集。
- TMDB、Bangumi 与多来源匹配，支持分季来源、单集绑定和缺集检查。
- 编辑作品、季与集的元数据，管理演员、制作信息、海报和背景图。
- 输出 Jellyfin、Emby、Kodi 风格的 NFO 与图片。
- 文件和作品目录重命名预览、冲突检查、同名字幕处理与历史记录。
- 简介翻译、本地缓存、中英文界面以及浅色、深色和系统主题。

第一次使用时添加媒体库。TMDB 需要在设置中填写自己的 [API 密钥](https://www.themoviedb.org/settings/api)，Bangumi 支持公开搜索。

## 开发

使用 Windows、.NET 10 SDK 和 Git。在 PowerShell 中执行：

```powershell
./scripts/test.ps1
./scripts/build.ps1
./scripts/build-installer.ps1
```

应用输出在 `artifacts/app-release`，发布文件和 SHA-256 校验文件在 `artifacts/releases`。安装包构建需要 Inno Setup 6。依赖版本由 NuGet lock 文件固定。

项目按 Core、Infrastructure、App 分离业务、外部服务与 WinUI 界面，见 [架构说明](docs/ARCHITECTURE.md)。GitHub Actions 执行测试、构建和打包，推送版本标签后自动发布同一次构建的产物，见 [发布指南](docs/RELEASING.md)。

## 许可证

项目代码和应用图标采用 [MIT](LICENSE)。第三方运行库与数据服务遵循各自条款，见 [第三方说明](THIRD-PARTY-NOTICES.md)。
