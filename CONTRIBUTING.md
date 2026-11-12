# 向 Wonder Plugins 发布插件

## 编译与打包

1. 使用 .NET 10 编译面向 Wonder 插件 API v1 的类库，引用启动器提供的 `Wonder.Extensibility.Contracts.dll`。
   入口类是公开、可无参构造的 `IWonderPlugin` 实现。宿主通过 `Activate(IPluginContext)` 传入配置、
   插件数据目录和日志入口，并在释放时调用 `Dispose()`。
2. 创建 ZIP，在根目录放 [plugin.json](schemas/plugin-v1.schema.json)。入口 DLL 可放在根目录或包内子目录，
   `entryAssembly` 使用以 `/` 分隔的包内相对路径，`entryType` 使用带命名空间的完整类名。插件自己的依赖
   和许可可随包分发，宿主提供的 Contracts DLL 由 Wonder 共享。
3. 插件 ID 使用小写字母、数字、点、下划线和连字符，以字母或数字开头，最长 80 字符。
   版本号使用三个无前导零的非负整数，例如 `1.0.0`。ZIP 最多 64 MiB，展开后最多 128 MiB，
   最多 2048 个条目，包内清单最多 1 MiB。文件路径需能在 Windows、Linux 与 macOS 使用。

## 收录与更新

1. 将 `packages/<id>-<version>.zip` 提交到仓库，并在 [catalog/plugins.json](catalog/plugins.json) 中登记
   包路径和 HTTPS 源码地址。每个插件 ID 对应一项。
2. 更新时添加使用新版本号的 ZIP，把目录项指向新文件。已发布版本的 ZIP 应保持原始字节。
3. 从仓库根运行以下命令，将 ZIP、目录和生成的索引一起提交到 `dev` 的 Pull Request。

```sh
python tools/validate.py --write
python tools/validate.py --check
python -m unittest discover -s tests -v
```

## 构建 Wonder Hello 示例

先编译 [Wonder Launcher](https://github.com/TouristH/Wonder-Launcher) 的 `Wonder.Extensibility.Contracts` 项目。
使用构建出的 DLL 打包示例，然后重新生成索引。Windows 如只提供 Python Launcher，可用 `py -3.12` 替代 `python`。

```sh
python tools/package_example.py --contracts "/path/to/Wonder.Extensibility.Contracts.dll"
python tools/validate.py --write
```

安装并启用 Wonder Hello 时，插件会将配置中的 `greeting` 字符串写入日志。示例配置为
`{"greeting":"你好，Wonder！"}`。没有提供有效字符串时使用 `Hello from Wonder Plugins!`。
