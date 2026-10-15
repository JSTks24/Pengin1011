# Pengin1011

[Read in English](README.md)

基于 .NET 10 与 Discord.Net 的模块化 Discord 机器人。功能模块是独立项目，编译为 DLL 后从宿主的 module 目录装载一次；更新后完整重启进程生效。

## 启动

安装 .NET 10 SDK，在项目根执行：

```bash
dotnet run --project Pengin1011.csproj
```

缺少的配置会在程序目录下生成模板；上述命令通常对应 bin/Debug/net10.0/config。配置不完整时保留本地维护终端。

config/config.json 保存宿主设置：

```json
{
  "Discord": {
    "Token": "YOUR_DISCORD_BOT_TOKEN",
    "AdminRoleIds": ["123456789012345678"]
  },
  "Databases": { "DataDirectory": null },
  "Logger": { "Channels": [] }
}
```

身份组、频道 ID 使用字符串。自带 /preset 和 /db 命令允许机器人所有者或命中任一配置身份组的成员；空列表仅允许所有者，服务器管理员没有额外权限。

AI 连接保存在 config/aipresets.json，包含 MaxParallel 和 Presets。Provider 支持 openai、gemini、vertex，第一项为默认预设。模块按预设名调用 AIClient，可在请求中覆盖模型和采样参数；端点与鉴权信息由预设决定。字段、示例和编译期清点规则见[模块指南](DevModule.md)。

运行自带模块前先构建：

```bash
dotnet build Pengin1011.csproj -t:BuildAllModules
```

## 数据与维护

数据目录优先级为 --data-dir、config.json 的 Databases.DataDirectory、程序目录下的 data。相对路径按程序目录解析；默认目录允许首次创建，显式指定的不存在目录需要 --create-data-dir。环境变量不参与目录选择。

```bash
dotnet Pengin1011.dll --data-dir "D:/Pengin1011/data" --create-data-dir
```

打开程序后，在内部终端输入：

```text
help
db status
db backups <数据库>
db backup
db version
db restore <数据库> <备份> --confirm
db recover <数据库> <操作> --complete --confirm
exit
```

启动时不再接受 db 子命令。现有 AIPresetAdmin 模块提供 /preset 与 /db；本地旧备份导入仅在内部终端提供。校验、回滚、解锁和导入语法见[模块指南](DevModule.md#6-database-preparation-and-maintenance)。

宿主持有数据目录的独占所有权。需要改动现场的维护先等待业务与资源安全停止，再持原所有权执行；恢复、恢复收尾、现场校验或解锁成功后启动新进程。失败时保留诊断与维护终端。

SQLite 使用 EF 迁移和经过验证的快照。中断恢复在证明目标或原现场之前阻断数据库使用。完整日志保留本地；配置的 Discord 日志频道只接收固定摘要，不发送原消息或异常正文。

## 开发与测试

模块使用明确的 Runtime 初始化/清理、生成的跨模块代理和按程序集隔离的本地化资源。升级宿主时重新构建模块 DLL，契约版本不兼容的旧模块会在装载时拒绝。

开发步骤见 [DevModule.md](DevModule.md)，运行契约见[架构设计](../../Document/Pengin1011/架构设计.md)。

```bash
dotnet build Pengin1011.sln
dotnet test Tests/Pengin1011.Tests/Pengin1011.Tests.csproj
```

## 许可证

[Apache License 2.0](LICENSE)。
