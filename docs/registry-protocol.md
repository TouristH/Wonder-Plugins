# Wonder 仓库协议

## 作者清单

仓库根 `_manifest.json` 使用 `schemaVersion: 1`，`id` 属于作者名空间，`releases` 保存全部发布版本。每个版本的元数据与包内清单一致；`apiVersion` 可为 1 或 2。包内若有 `configurationSchema`，也须原样写入对应的 release，由索引传给启动器。可选 `permissions` 仅为作者补充，同样必须一致。

```json
{
  "schemaVersion": 1,
  "id": "io.github.example.toolbox",
  "releases": [{
    "name": "工具箱",
    "description": "插件用途。",
    "author": "Example",
    "version": "1.0.0",
    "apiVersion": 1,
    "downloadUrl": "https://github.com/example/toolbox/releases/download/v1.0.0/toolbox.zip",
    "sha256": "填写实际ZIP的64位小写SHA-256",
    "size": 12345
  }]
}
```

机器人从顶层清单和核对过的仓库填入版本 `id`、`sourceUrl`。`review` 只能来自维护者指令，作者不能提供。包内另含 `entryAssembly`、`entryType`，见 [schema](../schemas/plugin-v1.schema.json)。

## 文件报告

`permissions.json` 记录 `schemaVersion`、`analyzerVersion`、`files`、`findings`、`limitations`。`files` 是按路径排序的 `{path, sha256}`，覆盖全部文件，排除根报告自身以避免循环哈希。修改许可证、资源、清单或 DLL 都会使报告失效。

报告使用与客户端相同的 C# 分析器；不同分析器版本不能混用。它是可复算结果，不是沙箱、签名或安全认证。

## 中心与客户端

`registry/plugins/<id>.json` 记录 schemaVersion、ID、generation、repository 的 fullName/numeric id/ownerId 及 releases。身份和既有发布事实不可被普通版本更新替换。当前使用 `generation: 1`；身份转让或复用须另行迁移，不能用同名新仓库冒充。

`reviews/<id>/g1/<version>.json` 绑定 ID、版本、SHA-256，保存按评论 ID 递增的事件。事件含审核员及 numeric ID、Issue、时间、说明和 verified/revoked 状态，撤销不删除历史。

`public/v1/index.json` 使用 `schemaVersion: 1, product: "wonder", plugins: [...]`。同一 ID 可出现不同版本；每条独立包含下载信息和可选 `review: {status,reviewer,reviewedAt,sha256}`。无 review 表示未验证；所有收录版本均保留下载地址。
