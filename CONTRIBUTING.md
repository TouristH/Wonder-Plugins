# 向 Wonder Plugins 发布插件

## 必需文件

| ZIP 根部文件 | 内容 |
| --- | --- |
| `plugin.json` | Wonder API v1 清单，描述 ID、版本和入口 |
| 入口 DLL | .NET 10 插件类库；入口实现 IWonderPlugin |
| `LICENSE` | 插件许可证；保留依赖的独立许可与通知 |
| `RISK.md` | 告知插件以系统用户权限运行，分析与人工验证不保证安全 |
| `permissions.json` | 工具自动生成的文件指纹与行为分析报告 |

附带实际所需依赖，不打包宿主共享的 `Wonder.Extensibility.Contracts.dll`。根文件名区分大小写，ZIP 不带多余顶层目录。示例见 [plugin.json](examples/Wonder.Example.Hello/plugin.json) 与[风险声明](examples/Wonder.Example.Hello/RISK.md)。

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

提交 **Plugin submission** Issue，填写作者仓库地址，无需管理员批准才进入自动检查。公开、未归档且非 Fork 的作者仓库也可添加 `wonder-plugin` Topic。修复投稿后可重开 Issue；后续版本由周期任务同步。

## 人工验证

收录后默认未验证。维护者检查后，在原投稿 Issue 或 Review Issue 新发带精确哈希的命令：

```text
/verify io.github.example.toolbox@g1:1.0.0 sha256:<64位小写SHA-256> 检查说明
/revoke-review io.github.example.toolbox@g1:1.0.0 sha256:<64位小写SHA-256> 撤销原因
```

审核员登录名与 numeric ID 必须同时匹配配置。编辑已有评论不能变成新指令。机器人重新校验包，经独立 PR 合并后更新状态。撤销保留历史，旧指令不能覆盖后来的操作。未验证和撤销版本仍可下载。

## 构建示例

```powershell
pwsh -File tools/package-example.ps1 -WonderExtensibilityProject "../Wonder Minecraft Launcher/src/Wonder.Extensibility/Wonder.Extensibility.csproj"
dotnet src/Wonder.PluginRegistry/bin/Release/net10.0/Wonder.PluginRegistry.dll build-index
```

示例读取自己的 `greeting` 配置并记录日志。脚本不会覆盖字节不同的同版本 ZIP；示例源码和 GPL 许可材料随包提供。
