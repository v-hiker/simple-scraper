# 构建与发布

## 本地构建

在 Windows PowerShell 中执行：

```powershell
./scripts/build.ps1
./scripts/build-installer.ps1
```

构建包含锁定依赖还原、回归测试、自包含应用发布、WinRT 清单与单文件资源检查。安装包需要 Inno Setup 6，默认使用 `artifacts/app-release`。发布目录中有已存在文件时，使用新目录或显式传入 `-ReplaceRelease`。

## GitHub Actions

`main` 分支提交、Pull Request 和手动运行都会执行 Windows 构建。工作流生成安装包、单文件 EXE、便携 ZIP 和对应 SHA-256 文件，并上传 Actions artifact。

发布版本时修改 `Directory.Build.props` 中的 `Version`，添加 `docs/releases/v版本号.md`，然后推送对应标签：

```powershell
git tag -a v0.2.0 -m 'SimpleScraper 0.2.0'
git push origin v0.2.0
```

标签必须与工程版本一致。Release 任务下载同一次构建的 artifact，验证全部校验文件后发布到 [GitHub Releases](https://github.com/v-hiker/simple-scraper/releases)。

## 安装与升级验收

运行安装包后，检查开始菜单快捷方式和 Windows 已安装应用入口。升级前后核对 `%LOCALAPPDATA%\SimpleScraper` 的数据保留情况，再从开始菜单启动应用，检查媒体库、设置及历史记录。

单文件 EXE 和 ZIP 使用相同的个人数据目录。自动化验证使用 `SIMPLE_SCRAPER_DATA_HOME` 指向隔离目录。用户配置、缓存、媒体文件和密钥不进入源码或发布包。
