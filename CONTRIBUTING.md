# 向 Wonder Plugins 发布插件

## 必需文件

| ZIP 根部文件 | 内容 |
| --- | --- |
| `plugin.json` | Wonder 插件清单，描述 ID、API 版本和入口 |
| 入口 DLL | .NET 10 插件类库；入口实现 IWonderPlugin |
| `LICENSE` | 插件许可证；保留依赖的独立许可与通知 |
| `RISK.md` | 告知插件以系统用户权限运行，分析与人工验证不保证安全 |
| `permissions.json` | 工具自动生成的文件指纹与行为分析报告 |

附带实际所需依赖，不打包宿主共享的 `Wonder.Extensibility.Contracts.dll`。根文件名区分大小写，ZIP 不带多余顶层目录。示例见 [plugin.json](examples/Wonder.Example.Hello/plugin.json) 与[风险声明](examples/Wonder.Example.Hello/RISK.md)。

仓库接受 `apiVersion` 为 1 或 2 的插件。API 2 插件需使用支持 API 2 的启动器。

公共 ID 使用 `io.github.<GitHub用户名小写>.<插件名>`，最长 80 字符。版本为三个无前导零的非负整数，例如 `1.2.0`。ZIP 不超过 64 MiB，展开后不超过 128 MiB、2048 条目。清单和声明文件不超过 1 MiB。

## 报告与发布

按 README 构建 C# 工具，将插件及全部发布文件放到独立目录。最后生成报告，再打包；此后修改文件必须重新生成报告。

```powershell
dotnet src/Wonder.PluginRegistry/bin/Release/net10.0/Wonder.PluginRegistry.dll report --generate ./plugin-output
dotnet src/Wonder.PluginRegistry/bin/Release/net10.0/Wonder.PluginRegistry.dll report --verify ./plugin-output
# 将 plugin-output 内容打包到 ZIP 根部
dotnet src/Wonder.PluginRegistry/bin/Release/net10.0/Wonder.PluginRegistry.dll check-package ./plugin.zip
```

报告读取静态元数据，不执行插件；包含文件 SHA-256、检测结果和分析限制。作者不能用自述权限替代报告。仓库用固定版本分析器重新生成并核对，缺报告、文件变化或结果不一致不能收录。发现联网、文件操作或动态加载等行为本身不意味着拒绝收录，这些结果供用户判断。

将 ZIP 上传到固定 GitHub Release 地址，不使用 `latest`。按[协议](docs/registry-protocol.md)创建根 `_manifest.json`，保留全部版本；新版本追加，不修改已发布版本及字节。

使用 **收录 Wonder 插件**表单提交作者仓库地址。模板自动添加 `plugin-submission`（插件投稿）和 `queued-for-intake`（等待收录），两项标签同时存在才进入队列。技术校验失败时显示 `validation-failed`。修正后重新填表提交，或请维护者给原 Issue 重新加上等待收录标签。已收录插件的后续版本由周期任务同步。

收录 PR 提交后显示 `pending-merge`。合并后标记 `listed`（已收录）和 `pending-review`（等待验证），Issue 保持打开。

## 人工验证

收录后默认未验证。单版本的原投稿 Issue 会收到机器人发布的插件 ID、精确版本和 SHA-256。涉及多个版本时，用 **请求人工验证** 表单分别填写目标版本和 SHA-256。等机器人公布唯一验证目标后，维护者完成检查并单独回复 `/ap`。

撤销时仍需指定版本、哈希和理由。

```text
/revoke-review io.github.example.toolbox@g1:1.0.0 sha256:<64位小写SHA-256> 撤销原因
```

审核员登录名与 numeric ID 必须同时匹配配置。编辑已有评论不能变成新指令。审核 PR 等待合并时显示 `review-pending-merge`。验证合并后标记 `review-verified` 并关闭发出指令的 Issue。撤销合并后改为 `review-revoked`，保留审核历史和下载。每条记录都只对应指令中的具体版本。

## 构建示例

```powershell
pwsh -File tools/package-example.ps1 -WonderExtensibilityProject "../Wonder Minecraft Launcher/src/Wonder.Extensibility/Wonder.Extensibility.csproj"
dotnet src/Wonder.PluginRegistry/bin/Release/net10.0/Wonder.PluginRegistry.dll build-index
```

示例读取自己的 `greeting` 配置并记录日志。脚本不会覆盖字节不同的同版本 ZIP；示例源码和 GPL 许可材料随包提供。
