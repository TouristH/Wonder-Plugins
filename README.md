# Wonder Plugins

[Wonder Launcher](https://github.com/TouristH/Wonder-Launcher) 的在线插件仓库。启动器从
[`dev` 索引](https://raw.githubusercontent.com/TouristH/UNuse/dev/public/wonder/v1/index.json)读取插件和 ZIP 的下载信息。

索引由 [catalog/plugins.json](catalog/plugins.json) 和 [packages/](packages/) 中的 ZIP 生成。
每项记录包含插件的标识、作者、版本、下载地址、字节数、SHA-256 及源码地址。安装前应核对来源，启用后插件在
Wonder 进程中以当前系统用户权限执行。索引校验覆盖文件完整性和格式，使用者应仅启用可信插件。

- [发布插件与构建示例](CONTRIBUTING.md)
- [索引格式](schemas/index-v1.schema.json)
- [包内清单格式](schemas/plugin-v1.schema.json)
- [示例源码](examples/Wonder.Example.Hello)

本分支的 Wonder 索引使用 `product: "wonder"`。旧仓库的发布历史在原分支和 Git 历史中保留。

仓库工具及索引格式使用 [MIT](LICENSE)。Wonder Hello 示例按 [GPL-3.0-only](examples/Wonder.Example.Hello/LICENSE)
及其[附加条款](examples/Wonder.Example.Hello/ADDITIONAL_TERMS.md)发布。其他插件以其包内许可证为准。
