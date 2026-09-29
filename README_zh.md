# Pengin1011

[Read in English](README.md)

基于 .NET 10 与 Discord.Net 的模块化 Discord 机器人框架。

机器人功能通过独立模块项目实现。模块编译为 DLL，放入宿主的 `module/` 目录，在启动时装载并校验。更新模块后需要重启进程。

## 特性

- **模块装载：** 启动前检查运行时入口和命令契约。
- **退出流程：** 等待在途任务结束后清理模块，并报告未完成的步骤。
- **数据库：** 每模块独立使用 SQLite，支持 EF Core 迁移、WAL 模式和定时备份，退出时尝试执行最终备份。
- **脚手架：** 生成模块项目，并将编译产物复制到宿主的 `module/` 目录。
- **AI 客户端：** 支持 OpenAI 兼容端点与 Gemini，提供调用级端点、鉴权信息和模型覆盖，以及输出 token 限制、并发限制、超时和流式输出。非流式调用会重试瞬时请求错误。两种后端均传递采样参数，具体支持情况取决于端点和模型。
- **Discord 回复：** 长消息自动分段，支持限频编辑的流式回复。

## 快速开始

需要 .NET 10 SDK。在项目根目录执行：

```bash
dotnet run --project Pengin1011.csproj
```

首次启动会在程序所在目录下生成 `config/config.json`。填写 Discord bot token 和默认 AI 后端后重新启动。使用上述命令时，配置文件通常位于 `bin/Debug/net10.0/config/config.json`。

控制台命令：`help`、`status`、`modules`、`db backup`、`db status`、`sync`、`exit`。

## 开发模块

模块开发说明见 [DevModule.md](DevModule.md)。

## 测试

```bash
dotnet test Tests/Pengin1011.Tests/Pengin1011.Tests.csproj
```

## 许可证

Pengin1011 采用 [Apache License 2.0](LICENSE)。
