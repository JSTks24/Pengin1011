# Pengin1011

[简体中文](README_zh.md)

Pengin1011 is a Discord bot framework with built-in AI support. Load existing modules or use C# to add your own commands and features. Configure a model from OpenAI, Gemini or Vertex AI once and share it across modules. A Q&A module can update its Discord reply as the model generates an answer.

## Quick start

Install the .NET 10 SDK and have your Discord bot token ready. In the Discord Developer Portal, open the application's **Bot** settings and enable **Server Members Intent** and **Message Content Intent**. Add the bot to the server where you want to use it.

From the framework repository root, build the bundled modules and start the bot.

```sh
dotnet build Pengin1011.csproj -t:BuildAllModules
dotnet run --project Pengin1011.csproj
```

The bot keeps its settings beside the running program. With the commands above, the configuration directory is `bin/Debug/net10.0/config/`.

1. The first launch creates `config.json`. Type `exit` in the bot console, then enter your bot token in the `Token` field under `Discord`.
2. Run the same `dotnet run` command again. It creates `aipresets.json` and waits at the console. Type `exit`. If a module uses AI, add the preset it needs. Otherwise leave the preset list empty. [AI preset example](DevModule.md#use-an-ai-model)
3. Start the bot with the same command. Once it connects, the bot owner can use `/preset list` in Discord to see the configured presets.

## Use modules

To install or update a module, stop the bot, copy the module DLL into the `module/` directory beside the program, and keep its filename. Restart the bot and type `modules` in its console to check that the module is `Ready`.

To write your own module, choose where its source will live.

- [In the framework repository](DevModule.md)
- [In a separate project with the module SDK](ModuleSdk.md)

## License

[Apache License 2.0](LICENSE)
