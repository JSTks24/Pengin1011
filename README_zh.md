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

在 Discord Developer Portal 的 Bot 设置启用 **Server Members Intent** 与 **Message Content Intent**，并满足平台对该应用的准入要求。宿主除 Guilds、GuildMessages 外还会请求这两项特权 intents。

AI 连接保存在 config/aipresets.json，包含 MaxParallel 和 Presets。Provider 支持 openai、gemini、vertex，第一项为默认预设。模块按预设名调用 AIClient，可在请求中覆盖模型和采样参数；端点与鉴权信息由预设决定。字段、示例和编译期清点规则见[模块指南](DevModule.md)。

Gemini/Vertex 的 BaseUrl 只接受对应官方 HTTPS 根端点；空值选择官方默认地址，Google 客户端不跟随重定向。Vertex 支持全球、区域与美欧多区域端点。OpenAI 保留现有配置端点策略。

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
db verify <数据库> <备份>
exit
```

AIPresetAdmin 模块提供 /preset 和 /db status、backups、backup、verify。恢复需要先停止机器人，再执行独立命令：

```bash
dotnet Pengin1011.dll db restore <数据库> <备份> --data-dir "D:/Pengin1011/data" --confirm
```

恢复命令独占数据目录，验证备份、保留原文件并替换数据库；不启动业务、备份定时器或替代进程。成功后手动启动机器人。中断留下的未完成标记会阻断该库业务、普通备份和检查点；排除失败原因后重新执行完整离线恢复。首次迁移失败且没有备份的人工处理步骤见[数据库指南](DevModule.md#6-database-preparation-and-maintenance)。

SQLite 保留 EF 迁移、经过验证的快照，以及每 30 分钟、手动和正常退出备份。数据库文件名取显式 ModuleDatabase 标识；源生成器补齐强类型接线，宿主先完成数据库准备再初始化模块业务。在线恢复、多阶段恢复、解锁和旧备份导入入口已移除。

首次 Ctrl+C 和 SIGTERM 请求正常退出，第二次 Ctrl+C 允许立即终止。完整日志保留本地；配置的 Discord 日志频道只接收固定摘要，不发送原消息或异常正文。

## 开发与测试

模块使用明确的 Runtime 初始化/清理、生成的跨模块代理和按程序集隔离的本地化资源。当前宿主契约版本为 5。升级宿主时重新构建模块 DLL，契约版本不兼容的旧模块会在装载时拒绝。

DLL 基名保持与程序集名一致。桥清单格式为 `pengin1011.modulebridge.manifest/3`，正常依赖构建会刷新；显式跳过依赖构建可能读取旧产物。[七份脚手架模板](DevModule.md#1-scaffold-build-and-update)是维护中的完整示例，行为测试继续平铺在 Tests/Pengin1011.Tests。

开发步骤见 [DevModule.md](DevModule.md)，运行契约见[架构设计](../../Document/Pengin1011/架构设计.md)。

```bash
dotnet build Pengin1011.sln
dotnet test Tests/Pengin1011.Tests/Pengin1011.Tests.csproj
```

## 许可证

[Apache License 2.0](LICENSE)。
