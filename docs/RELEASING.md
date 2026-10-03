# 独立发布

## 发布前

在 Windows PowerShell 中执行 `./scripts/build.ps1`，默认完成测试并生成自包含 EXE 和 ZIP。已有发布文件时脚本拒绝覆盖；使用新的 `-ReleaseDirectory artifacts/releases-next` 或明确指定 `-ReplaceRelease`。构建不会覆盖 `artifacts/app`，也会检查选定输出目录是否有正在运行的应用。

把单文件 EXE 单独放入新目录，以空解包缓存和隔离数据目录冷启动，并使用最终发布文件名验证中文界面、图标、季集展开、编辑取消与设置恢复。ZIP 需要完整解压后验证。联网验收需使用自己的密钥；实际通过范围记录在 [验证记录](VALIDATION.md)。

检查 [来源记录](PROVENANCE.md) 和 [第三方说明](../THIRD-PARTY-NOTICES.md)。发布源码不应包含个人配置、媒体缓存、照片、密钥、旧历史或旧安装器。

## GitHub

源码仓库为 [v-hiker/simple-scraper](https://github.com/v-hiker/simple-scraper)。完成本地验收后推送源码：

```powershell
git push -u origin main
```

工程的初始提交只包含独立源码、文档和自有素材。它与原改造工程没有 Git 父提交或旧 remote 关联。

## 版本

在 Directory.Build.props 修改 Version，添加 `docs/releases/v版本号.md` 的发布说明并重新测试。源码推送、Actions 验证通过后发布相同版本标签，例如：

```powershell
git tag -a v0.1.1 -m 'SimpleScraper 0.1.1'
git push origin v0.1.1
```

Actions 会在 Windows runner 上重测、构建、检查内嵌清单、XBF 和第三方许可证，然后上传构建产物。标签发布任务下载同一次构建的产物，核对版本与 SHA-256 后创建 Release。发布包包含应用、MIT 许可证、指南和第三方原始许可文件。

## 旧数据迁移

原工程保持原位置以便回退。新工程使用独立 AppData，不自动读取旧密钥或状态。迁移前正常退出两个应用并备份旧数据。仅复制自己拥有的 appsettings、library-state、library-cache 和历史数据；先检查配置中的密钥归属与路径，保留副本验证加载后再使用。不能把迁移数据提交到 GitHub。
