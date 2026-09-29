# Pengin1011

[简体中文](README_zh.md)

A modular Discord bot framework on .NET 10 and Discord.Net.

Bot features are implemented as separate module projects. Each module compiles to a DLL in the host's `module/` directory and is loaded and validated at startup. Module updates require a process restart.

## Features

- **Module loading:** validates runtime entry points and command contracts before startup.
- **Shutdown:** waits for active work before module cleanup and reports incomplete steps.
- **Databases:** per-module SQLite databases with EF Core migrations, WAL mode, scheduled backups and an attempted final backup on shutdown.
- **Scaffolding:** generates module projects and copies compiled modules into the host's `module/` directory.
- **AI client:** supports OpenAI-compatible endpoints and Gemini, with per-call endpoint, credentials and model overrides, output token limits, concurrency limits, timeouts and streaming. Non-streaming calls retry transient request failures. Sampling parameters are passed through to both backends; support depends on the endpoint and model.
- **Discord replies:** splits long messages and supports rate-limited streaming edits.

## Getting started

Requires the .NET 10 SDK. Run from the repository root:

```bash
dotnet run --project Pengin1011.csproj
```

On first start, the host creates `config/config.json` under its executable directory. Set the Discord bot token and default AI backend, then restart. With the command above, the configuration is normally at `bin/Debug/net10.0/config/config.json`.

Console commands: `help`, `status`, `modules`, `db backup`, `db status`, `sync`, `exit`.

## Developing modules

See [DevModule.md](DevModule.md) for module development instructions.

## Tests

```bash
dotnet test Tests/Pengin1011.Tests/Pengin1011.Tests.csproj
```

## License

Pengin1011 is licensed under the [Apache License 2.0](LICENSE).
