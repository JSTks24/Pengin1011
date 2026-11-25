# Pengin1011

[English](README.md)

Pengin1011 是一个内置 AI 支持的 Discord 机器人框架。可以加载现成的模块，也可以用 C# 编写自己的命令和功能。OpenAI、Gemini 或 Vertex AI 的模型只需配置一次，多个模块就能共用。问答模块可以随着模型生成回答，逐步更新 Discord 里的回复。

## 快速启动

安装 .NET 10 SDK，准备好 Discord 机器人的 Token。在 Discord Developer Portal 中打开应用的 **Bot** 设置，启用 **Server Members Intent** 和 **Message Content Intent**，并将机器人加入要使用它的服务器。

在框架仓库根目录运行以下命令，构建随附模块并启动机器人。

```sh
dotnet build Pengin1011.csproj -t:BuildAllModules
dotnet run --project Pengin1011.csproj
```

配置保存在程序旁的 `config/` 目录。按上述命令运行时，实际位置是 `bin/Debug/net10.0/config/`。

1. 首次启动会生成 `config.json`。在机器人终端输入 `exit`，然后把机器人 Token 填入文件中 `Discord` 下的 `Token` 字段。
2. 再次执行上面的 `dotnet run` 命令。程序会生成 `aipresets.json` 并等待输入。输入 `exit`。若模块需要 AI，填入它使用的模型预设。其余情况保留空列表即可。[AI 预设示例](DevModule.md#use-an-ai-model)
3. 用同一条命令启动机器人。连接成功后，机器人所有者可以在 Discord 使用 `/preset list` 查看已配置的预设。

## 使用模块

安装或更新模块时，先停止机器人，将模块 DLL 按原文件名复制到程序旁的 `module/` 目录。重新启动后，在机器人终端输入 `modules`，确认模块状态为 `Ready`。

编写自己的模块时，按源码存放位置选择指南。

- [在框架仓库中开发模块](DevModule.md)
- [使用模块 SDK 在独立项目中开发](ModuleSdk.md)

## 许可证

[Apache License 2.0](LICENSE)
