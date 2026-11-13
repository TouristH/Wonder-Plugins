# 机器人部署与维护

## 运行结构

仓库无需常驻服务器。GitHub Actions 运行 .NET 10 工具，Git 保存版本与审核记录，客户端读取静态索引。

| 工作流 | 入口 | 作用 |
| --- | --- | --- |
| validate | PR、main push | 测试、包与报告检查、索引核对 |
| policy | main 的 pull_request_target | 编译精确受信 base，重放数据变更，不执行 PR 候选代码 |
| registry | 每六小时、手动、Issue 新建/重开、评论 | 自动收录或审核命令校验，创建 App PR |
| reconcile | main push、手动 | 只对已合并的 App PR 回复处理结果 |

写工作流共享 `registry-write` 并发组，不取消正在处理的事务。定时收录、Issue 和人工验证使用同一套包检查；作者发布材料不会被执行。

## GitHub 配置

正式默认分支为 `main`。分支保护要求 PR，并要求 GitHub Actions 的 `policy` 和 `validate` 检查通过；允许合并前刷新分支。开启仓库 auto-merge。

专用 GitHub App 仅需安装到中心仓库，授予 Contents 和 Pull requests 写权限。`registry-bot/**` 分支仅允许此 App 写入；App 不应绕过 main 保护。配置：

- Variable：`WONDER_REGISTRY_APP_CLIENT_ID`。
- Secret：`WONDER_REGISTRY_APP_PRIVATE_KEY`。现有安装迁移时暂可沿用 `NYA_REGISTRY_APP_PRIVATE_KEY`，工作流已显式回退读取；勿将私钥写进代码或日志。
- `repository.json`：真实 `registryBotLogin`、`registryBotId`、`trustedReviewers`、`trustedReviewerIds`。

Issue 模板使用 `plugin-submission`、`queued-for-intake` 与 `review-request` 标签。部署时确保这些标签存在。

收录工作流先用 GITHUB_TOKEN 读取与反馈请求，技术校验结束后才签发短期 App token 用于发布数据 PR。PR 内容只允许版本事实、审核事件与生成索引，合并后再通知 Issue。源码修改的 PR 验证没有写令牌；policy 始终来自 base。

## 常用维护

固定共享分析器源码版本在 `.github/actions/setup-registry/action.yml`。更新该版本时重新生成示例报告，按新的版本号发布不同字节的示例 ZIP；不要覆盖已发布 ZIP。具体构建和作者命令见 README、CONTRIBUTING。

自动收录失败后查看 Actions 日志和 Issue 反馈；修复作者清单后重开 Issue 或手动运行 registry。main 前移导致事务发布失败时，以最新 main 重跑收录或评论任务。不要手改已发布版本的哈希来消除错误。

人工验证只对精确版本与 SHA-256 有效。审核员离任前先撤销其仍有效的验证，再移除信任配置；已撤销历史保留。新的审核员必须由当前维护者审查配置变更后加入。

## 迁移验收

工作流文件存在不表示已经部署成功。首次迁移还需确认：固定的 Wonder 源码提交能被 CI 读取、默认分支正确、标签和 App 配置有效、两个必需检查实际运行、定时任务可执行，以及客户端从 main 索引完成下载校验。

旧仓库身份与版本记录原样保留在 `plugins/`，迁移锚点原样保留在 `migrations/v2-bootstrap.json`，由 policy 禁止修改。它们不进入 Wonder 索引；旧 API 的插件或审核标志不能直接复制成 Wonder 版本，详见[历史留档](legacy-archive.md)。当前 Wonder 数据使用第一代发布者身份；旧身份转让、注销与清除工作流尚未迁移，未来需要时以旧代码与测试为行为参考。

合并后反馈扫描最近更新的 200 个已关闭 PR，并核对 App 身份与已经落地的记录。超出窗口的漏报需要维护者手动回补。
