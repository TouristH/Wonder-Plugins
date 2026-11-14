# Wonder Plugins

[Wonder Launcher](https://github.com/TouristH/Wonder-Launcher) 的在线插件仓库。作者在自己的 GitHub 仓库发布插件，再通过收录表单提交。维护者可在 Issue 中验证具体版本。

启动器读取 [main/public/v1/index.json](https://raw.githubusercontent.com/TouristH/Wonder-Plugins/main/public/v1/index.json)。同一插件的每个已收录版本都保留下载地址、SHA-256 和独立验证状态。未验证、撤销验证不会阻止下载；客户端首次启用前自行分析插件，用户同意后才运行。

## 发布与验证

1. ZIP 根部包含 `plugin.json`、入口 DLL、`LICENSE`、`RISK.md`、工具生成的 `permissions.json`，并附带所需依赖。作者的访问行为说明可选，不能代替自动分析。
2. 在作者仓库创建固定版本 GitHub Release，发布 ZIP，并在根 `_manifest.json` 登记完整版本列表。
3. 使用 **收录 Wonder 插件**表单。它会自动添加类型和等待收录标签。机器人检查清单、发布者身份、包路径、大小和 SHA-256，重新生成并核对权限报告，通过后经 PR 收录。Issue 保持打开并显示等待验证。
4. 维护者检查源码、构建方式和行为后，在对应 Issue 输入 `/verify <id>@g1:<version> sha256:<hash>`。验证记录合并后，机器人标记已验证并关闭 Issue。撤销用 `/revoke-review`，保留历史；原包无法下载时仍能撤销。

自动收录始终不授予人工验证状态。验证绑定具体安装包，不跨版本继承，也不构成安全保证。

- [作者投稿与包规范](CONTRIBUTING.md)
- [发布清单与数据协议](docs/registry-protocol.md)
- [机器人部署与维护](docs/operations.md)
- [旧仓库历史留档](docs/legacy-archive.md)
- [安全边界](SECURITY.md)
- [问候示例](examples/Wonder.Example.Hello)

## 开发

仓库工具使用 C# / .NET 10，复用 Wonder 的 ZIP 校验与静态分析。GitHub Actions 固定引用启动器提交；本地用 `WonderExtensibilityProject` 指定对应工程，无需运行启动器。

```powershell
dotnet build Wonder.PluginRegistry.slnx -c Release -p:WonderExtensibilityProject="../Wonder Minecraft Launcher/src/Wonder.Extensibility/Wonder.Extensibility.csproj"
dotnet test Wonder.PluginRegistry.slnx -c Release -p:WonderExtensibilityProject="../Wonder Minecraft Launcher/src/Wonder.Extensibility/Wonder.Extensibility.csproj"
dotnet src/Wonder.PluginRegistry/bin/Release/net10.0/Wonder.PluginRegistry.dll validate
```

`registry/plugins/` 保存发布者身份及版本事实，`reviews/` 保存人工验证事件；`public/v1/index.json` 是生成结果。`catalog/` 和 `packages/` 保存维护者提供的示例包，第三方插件留在作者自己的 Release 中。

仓库原创工具与格式规范沿用 [MIT](LICENSE)，保留原版权。引用的 Wonder 程序集及示例采用 GPL-3.0-only 和其允许的附加条款；分发组合程序须满足 GPL 的对应源码和通知义务，详见[共享代码说明](THIRD-PARTY-NOTICES.md)。各插件保留自己的许可证。
