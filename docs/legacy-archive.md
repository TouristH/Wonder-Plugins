# 旧仓库历史留档

Wonder Plugins 延续此仓库的原始历史。`plugins/` 中既有的身份、插件元数据和版本 JSON，以及 `migrations/v2-bootstrap.json`，保留旧主干提交 `260693c055cdc263f96137f2968d97f506171b37` 的原始内容，供追溯使用；policy 禁止修改这些档案。

这些文件中的旧名称、链接和 schema 引用也是历史记录，不表示当前产品名称、有效下载源或 Wonder 兼容性。旧格式及配套 Python 实现可从上述提交查阅。新运行时只读取 `registry/plugins/`、`reviews/` 和 `catalog/`，不会把历史插件自动作为 Wonder 插件发布。

仓库信任配置继续使用 `repository.json` 的 `schemaVersion: 2`，保留 numeric identity 约束；Wonder 的索引、发布清单和权限报告各自使用其独立的 schema 版本。配置版本不等于插件 API 版本。

原插件作者若要发布 Wonder 版本，应按当前投稿规范重新生成安装包和权限报告，通过自动校验后收录。旧审核结果不继承到新包。
