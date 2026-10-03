# 独立发布

## 发布前

在 Windows PowerShell 中执行 `./scripts/test.ps1` 与 `./scripts/build.ps1`。打开输出的 SimpleScraper.exe，检查中文界面、主题、设置、扫描、搜索、编辑取消和重命名预览。联网验收需使用你自己的密钥。

检查 [来源记录](PROVENANCE.md) 和 [第三方说明](../THIRD-PARTY-NOTICES.md)。发布源码不应包含个人配置、媒体缓存、照片、密钥、旧历史或旧安装器。

## GitHub

仓库建议名：`simple-scraper`。使用你自己的 GitHub 账号创建空仓库，避免生成另一个 README/许可证提交。然后在新工程目录执行：

```powershell
git remote add origin https://github.com/v-hiker/simple-scraper.git
git push -u origin main
```

工程的初始提交只包含独立源码、文档和自有素材。它与原改造工程没有 Git 父提交或旧 remote 关联。

## 版本

在 Directory.Build.props 修改 Version 后重新测试。源码推送完成后发布相同版本标签：

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Actions 会在 Windows runner 上重测、构建并创建 Release。需要仓库启用 Actions，以及工作流的 contents 写权限。发布包包含应用、MIT 许可证和第三方说明。版本标签创建/推送应在确认该版本可以公开后执行。

## 旧数据迁移

原工程保持原位置以便回退。新工程使用独立 AppData，不自动读取旧密钥或状态。迁移前正常退出两个应用并备份旧数据。仅复制自己拥有的 appsettings、library-state、library-cache 和历史数据；先检查配置中的密钥归属与路径，保留副本验证加载后再使用。不能把迁移数据提交到 GitHub。
