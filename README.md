# Pengin1011

[简体中文](README_zh.md)

A modular Discord bot built with .NET 10 and Discord.Net. Modules are separate projects compiled into DLLs and loaded once from the host's module directory. Updates take effect after a full process restart.

## Start

Install the .NET 10 SDK and run from the project root:

```bash
dotnet run --project Pengin1011.csproj
```

The host creates missing configuration templates under its executable directory, normally bin/Debug/net10.0/config when using the command above. Incomplete configuration leaves a local maintenance terminal available.

config/config.json holds host settings:

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

Discord role/channel IDs are strings. The built-in /preset and /db commands allow the bot owner or a member with any configured admin role. An empty list permits only the owner; server administrator permissions do not grant access.

Enable **Server Members Intent** and **Message Content Intent** for the application in Discord Developer Portal, including any required platform approval. The host requests those privileged intents in addition to Guilds and GuildMessages.

AI connections belong in config/aipresets.json, containing MaxParallel and Presets. Supported providers are openai, gemini and vertex; the first preset is the default. Modules call AIClient with a preset name and may override model/sampling values per request. There is no per-call endpoint or credential override. See the [module guide](DevModule.md) for fields, examples and compile-time preset checks.

Gemini/Vertex BaseUrl accepts only the provider's official HTTPS root endpoints; an empty value selects the official default, and Google clients do not follow redirects. Vertex supports global, regional and US/EU multi-region endpoints. OpenAI retains its configured endpoint policy.

Build bundled modules before running them:

```bash
dotnet build Pengin1011.csproj -t:BuildAllModules
```

## Data and maintenance

Data-directory priority is --data-dir, config.json Databases.DataDirectory, then data beside the program. Relative paths use the program directory. The default is created on first start; an explicit missing directory requires --create-data-dir. Environment variables do not select the directory.

```bash
dotnet Pengin1011.dll --data-dir "D:/Pengin1011/data" --create-data-dir
```

Enter commands after the program opens:

```text
help
db status
db backups <database>
db backup
db version
db restore <database> <backup-id> --confirm
db recover <database> <operation-id> --complete --confirm
exit
```

Startup db subcommands are no longer supported. The existing AIPresetAdmin module provides /preset and /db; importing a local legacy backup is available only in the terminal. The [module guide](DevModule.md#6-database-preparation-and-maintenance) lists verification, rollback, unlock and import syntax.

The host keeps exclusive ownership of the data directory. Database filenames derive from DbContext class names by removing a trailing DbContext or Context, rather than directly from module names; different context types resolving to the same name are rejected. State-changing maintenance waits for business and resources to stop safely, then retains that ownership while changing data.

After successful restore, recovery, live verification or unlock, the host uses a handoff restart; database completion and restart failure are reported separately. See the [handoff contract](../../Document/Pengin1011/架构设计.md#73-内部终端与-discord).

The first Ctrl+C and SIGTERM request normal shutdown; a second Ctrl+C permits immediate termination. Internal handoff restart targets standalone Windows/Linux processes. systemd/container restart policies need separate deployment integration.

SQLite databases use EF migrations and verified snapshots. Interrupted restores block database use until recovery proves the target or original files. Local logs retain full details; configured Discord log channels receive fixed summaries without message or exception contents.

## Develop and test

Modules use explicit Runtime initialization/cleanup, generated bridge proxies for cross-module calls, and per-assembly localization resources. Rebuild module DLLs when upgrading the host; incompatible contract versions are rejected during loading.

Keep the DLL basename equal to its assembly name. Bridge manifests use `pengin1011.modulebridge.manifest/3`; normal dependency builds refresh them, while explicitly skipping dependency builds can use stale artifacts. The [seven scaffold templates](DevModule.md#1-scaffold-build-and-update) are the maintained complete example. Behavior tests remain flat under Tests/Pengin1011.Tests.

See [DevModule.md](DevModule.md) for development instructions and [the architecture](../../Document/Pengin1011/架构设计.md) for runtime contracts.

```bash
dotnet build Pengin1011.sln
dotnet test Tests/Pengin1011.Tests/Pengin1011.Tests.csproj
```

## License

[Apache License 2.0](LICENSE).
