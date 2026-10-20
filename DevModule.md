# Pengin1011 Module Development Guide

This guide covers module implementation and operations. The [project rules](../../Document/Pengin1011/AGENTS.md) define coding and test requirements; the [architecture](../../Document/Pengin1011/架构设计.md) defines the current runtime contracts.

## 1. Scaffold, build and update

Run from the project root:

```bash
dotnet build Pengin1011.csproj -t:NewModule -p:Module=MyBot
dotnet tool restore
dotnet build Pengin1011.csproj -t:AddModuleMigration -p:Module=MyBot -p:MigrationName=Init
dotnet build Pengin1011.csproj -t:BuildModule -p:Module=MyBot
dotnet run --project Pengin1011.csproj
```

Generate migrations only when the module uses a database. Rebuild after generating them so Migration, Designer and ModelSnapshot are included in the module DLL. The EF command-line tool generates schema change source files; production startup applies them through `Databases.PrepareAsync`.

The scaffold creates a csproj, command class, Runtime, config loader, DbContext and localization resources under `Modules/<Name>/`, and adds the project to the solution. Package references are inherited from the host; the scaffold's explicit EF Sqlite/Design references support database runtime and migration generation. Do not add other packages without approval.

The maintained scaffold consists of seven files. `NewModule` substitutes `__MODULE__` and writes these templates into the new module; use these sources as the complete example:

| Template | Generated content |
|---|---|
| [Module.csproj.tpl](BuildTemplates/Module.csproj.tpl) | Module project, host/analyzer references and artifact copy |
| [ModuleClass.tpl](BuildTemplates/ModuleClass.tpl) | Command class with module availability |
| [ModuleRuntime.tpl](BuildTemplates/ModuleRuntime.tpl) | Runtime initialization and cleanup |
| [ModuleConfig.tpl](BuildTemplates/ModuleConfig.tpl) | Config template, loader and options |
| [ModuleDbContext.tpl](BuildTemplates/ModuleDbContext.tpl) | DbContext and design-time factory |
| [ModuleStrings.tpl](BuildTemplates/ModuleStrings.tpl) | Neutral localization resources |
| [ModuleStringsZhCn.tpl](BuildTemplates/ModuleStringsZhCn.tpl) | zh-CN localization resources |

Keep the host project reference and the generator analyzer reference. `EmitCompilerGeneratedFiles` must remain enabled so bridge manifests reach dependent modules through `obj/`. Only add `BridgeDependency` when the module consumes another module's bridge.

Modules load from the executable directory's `module/` once per process. The DLL basename must match its assembly name, ignoring case; a renamed DLL is rejected before Runtime construction or bridge registration. A new DLL takes effect after a full restart: stop the old process, replace the artifact, then start again. `sync` only registers the current command tree; `reload` returns a restart notice. The host contract is version 4; rebuild modules when upgrading the host because the removed APIs are not binary compatible.

## 2. Runtime and command ownership

Each module assembly must contain exactly one implementation:

```csharp
public interface IModuleRuntime {
	Task InitializeAsync(CancellationToken ct);
	Task StopAsync(CancellationToken ct);
}
```

`InitializeAsync` receives business cancellation. Start database-dependent work only after Prepare succeeds. A successful initialization marks the module Ready; a failure disables it and requests cleanup after initialization ends.

`StopAsync` receives an independent cleanup budget token, normally 10 seconds. The Runtime owns subscriptions, component registrations, timers and background tasks: dispose registrations, stop producers and await tasks, including resources created during partial initialization. Repeated stop requests reuse one cleanup task.

The host admits module cleanup only after control operations and tracked work end. Cancellation and timeout do not prove completion. Do not claim a resource stopped until its work has actually ended.

Initialization must observe cancellation and finish its own work. The host does not forcibly terminate an initialization task: an unfinished task prevents safe maintenance and keeps directory ownership. Once an exit report says resources did not settle, further state-changing maintenance requests are rejected instead of being reported as accepted.

Command classes contain parameter, permission and reply logic:

```csharp
using Discord.Interactions;
using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.MyBot;

[ModuleAvailability(typeof(MyBotRuntime))]
public class MyBot : InteractionModuleBase<SocketInteractionContext> {
	[SlashCommand("ping", "Check that the bot is alive")]
	public async Task Ping() {
		await RespondAsync("pong", ephemeral: true);
	}
}
```

The availability attribute must identify the module's actual Runtime. The host rejects Async RunMode overrides and intercepts commands when their module is not Ready.

For administrative commands using the host's policy, add `[RequireBotAdmin]` from `Pengin1011.Core`. It allows the bot owner or a guild member with a configured `Discord.AdminRoleIds` role. An empty role list permits only the owner; guild administrator permissions add no bypass, and DMs cannot authorize through guild roles.

## 3. Events and components

Subscribe during initialization, pass the module run object, retain each handle and dispose it during cleanup:

```csharp
var run = ModuleRegistry.RunOf(GetType().Assembly);
_messageSubscription = DiscordGateway.SubscribeMessage(run, OnMessage);
```

`OnMessage` accepts `SocketMessage` and `CancellationToken`, returning Task. Gateway subscriptions also cover Ready, guild availability/join, interactions, buttons, select menus and modals. Their exact signatures live in `Services/Discord/DiscordGateway.cs`.

Ready notifications are not replayed. A late subscriber misses an earlier notification, and a module still Starting may be filtered out. Do not make initialization depend on receiving Ready through a module subscription; the host's command synchronization follows its own gateway path.

Do not use `[ComponentInteraction]` or `[ModalInteraction]`: they fail command contract checks, and the module becomes Disabled without initialization. Register handlers with ownership instead:

```csharp
var run = ModuleRegistry.RunOf(GetType().Assembly);
var customId = $"mybot:confirm:{Guid.NewGuid():N}";
Components.Register(run, customId, async (interaction, ct) => {
	await interaction.RespondAsync("confirmed", ephemeral: true);
}, TimeSpan.FromMinutes(10));
```

Use the same custom ID on the Discord component. Separate sessions need separate IDs; replacing an existing ID replaces its handler/state. The default TTL is 180 seconds and refreshes on a valid interaction. State is in memory and does not survive restart.

`Components.Register<TState>` supports shared registration state; `Unregister`, `TryGetState` and `DisableMessageComponentsAsync` support cleanup. Runtime cleanup remains responsible for its own registrations.

Gateway dispatch tracks admitted work and rejects new work during shutdown. Module-owned background tasks must also observe cancellation and be awaited by the Runtime; a fire-and-forget task is not a completed cleanup.

The gateway requests Guilds, GuildMembers, GuildMessages and MessageContent intents. Enable Server Members Intent and Message Content Intent in Discord Developer Portal and satisfy any required platform approval; setting the flags in code does not enable the application settings.

## 4. AI presets and calls

`config/aipresets.json` contains MaxParallel and the preset list; `config.json` contains no AI connection section. Supported providers are openai, gemini and vertex. See architecture §6 for required fields and the canonical file example.

Gemini and Vertex accept only official HTTPS root endpoints on the default port, with no credentials, query, fragment or extra path. Gemini uses `https://generativelanguage.googleapis.com`; Vertex supports the global `https://aiplatform.googleapis.com`, regional `https://<region>-aiplatform.googleapis.com`, and multi-region `https://aiplatform.us.rep.googleapis.com` / `https://aiplatform.eu.rep.googleapis.com` endpoints. An empty BaseUrl selects the official default for the provider and Vertex Location. Google clients do not follow HTTP redirects. Loading, preset edits and backend construction share this validation; OpenAI keeps its configured endpoint policy.

`AIRequest` contains System, required Messages, optional Images and optional Model/Temperature/TopP/MaxOutputTokens overrides. Request values override preset values. Connection credentials and endpoints belong to presets.

```csharp
var reply = await AIClient.CompleteAsync("prod", new AIRequest {
	System = "Answer concisely.",
	Messages = [new AIMessage(AIRole.User, "Hello")],
	MaxOutputTokens = 500,
}, ct);
```

Omitting the preset name uses the first preset. null and an empty name also select the default; an actual preset named `"<default>"` is a named preset. Missing presets throw AIPresetNotFoundException, including when the default list is empty.

Preset names must be string literals or const values. Dynamic names fail compilation with PAP001; referencing CompleteAsync or StreamAsync as method groups/delegates fails with PAP002. nameof does not register a dependency. Runtime selection can branch between separate literal-name calls.

The generator indexes calls per assembly. Before business startup the host reports all missing referenced presets. Referenced presets cannot be renamed/deleted, and default consumers cannot lose their default preset.

Non-streaming calls retry transient failures twice after 1 and 2 seconds, with a 120-second timeout per attempt; handled request failures return null. Parameter errors and caller cancellation propagate. The preset, merged parameters and acquired connection remain fixed across retries.

Streaming uses `AIClient.StreamAsync`, has a stream timeout, does not retry, and propagates errors. Handle those errors in the caller and record them through Logger. For Discord output, append deltas to a `StreamingReply`, drive `FlushAsync` yourself, and call `FinalizeAsync` with the complete result.

Presets are immutable snapshots. Management code uses `AIPresetStore.AddPatch`, `Apply(name, patch, expectedVersion, out result)` and `RemovePatch`; Apply also performs rename. Patch distinguishes absent, set and clear. Save failure leaves the old snapshot in place, and result.Error is already rendered text. Do not restore the removed whole-object Update API or format that error a second time. The computed Connection property is not serialized; old files containing it remain readable and lose that redundant field on the next normal save.

Connections are shared by connection parameters. A preset edit retires only connections no longer referenced. In-flight requests keep their lease until completion; retiring a connection does not dispose it while still in use.

## 5. Discord helpers

| Helper | Purpose |
|---|---|
| Messages | Send, reply, edit, delete and batch; long messages split automatically |
| MessageSplitter / StreamingReply | Manual splitting; caller-driven streaming edits |
| Interactions | Defer, follow up, attach files and edit original responses |
| Channels / Members / Guilds / Threads | Resolve entities, inspect permissions and perform their supported mutations |
| History / Attachments | Paged history and attachment download |
| Mentions / MessageLink | Mention formatting/stripping and message-link parsing |

Messages returns Succeeded, Partial or Failed with planned chunks, sent messages and failed indexes. Inspect these results. Sends/edits default to AllowedMentions.None; Delete treats Discord 404 as already deleted. StreamingReply has no background flush timer, and normally spaces edits by 3000 ms. Call Append, FlushAsync, FinalizeAsync and other operations sequentially, awaiting each asynchronous operation before the next; the object does not coordinate concurrent callers. Its chunk limit defaults to 1900, nonpositive values use that default and larger values are capped at 1900.

Cancellation support is per method. Message sending/editing/deletion, DM user/channel resolution, history REST requests and component disabling pass their accepted token into Discord.Net. History cancellation returns the messages already read; message and component helpers keep their existing status/false results. Thread tag modifications accept a token, while restoring the prior archive state uses its own cleanup budget. Interactions and the other helpers without a token parameter do not gain caller cancellation.

`Attachments.DownloadAsync(attachment)` remains available with a default 25 MiB limit. Use `DownloadAsync(attachment, ct, maxBytes)` to supply cancellation and a different byte budget. Declared attachment size, response Content-Length and actual bytes read are checked, including responses without a known length. Caller cancellation propagates; other download failures return null. The shared HttpClient's 100-second timeout also covers body reading.

Permission checks are thread-aware: sending in a thread uses SendMessagesInThreads. Discord.Net's available permission view may not fully model implicit inheritance; inspect the helper and library behavior when adding permission-sensitive operations.

## 6. Database preparation and maintenance

The database path is `<resolved data directory>/<DatabaseId>.db`. DatabaseId comes from the DbContext class name, removing a trailing `DbContext`, or otherwise `Context`; `MyBotDbContext` therefore uses `MyBot.db`. It does not come from the module DLL name. Distinct context types resolving to the same ID are rejected instead of sharing a file. Resolution is `--data-dir > config.json Databases.DataDirectory > <program directory>/data`; relative paths use the program directory. Environment variables are not an input. The default directory may be created on first start; explicit missing paths require `--create-data-dir`.

The Runtime prepares its context before starting business work:

```csharp
await Databases.PrepareAsync<MyBotDbContext>(ct);
using var db = Databases.Open<MyBotDbContext>();
```

Open creates a fresh context and never migrates. Prepare validates migrations, snapshot/model agreement, history and unfinished maintenance; it refuses untracked business tables and residual EF locks. Concurrent callers share preparation while retaining independent cancellation of their waits. The operation has its own budget and responds to shutdown.

Use the generated DbContext and design-time factory. Its normal OnConfiguring calls `Databases.Configure(GetType(), options)` only when options are not already configured. Generate and build migration source before deployment.

The host owns one operating-system directory lock through business, stopping and maintenance. A second host cannot write the same directory. Do not run external database writers, including `dotnet ef database update`, against the host's live data.

Snapshots live under `backup/<DatabaseId>/rolling/<backupId>/` and `pre-migration/<backupId>/`, each with snapshot.db and metadata.json. Incomplete .partial directories are not recovery points. Rolling retains the latest five completed snapshots plus the target of a pending restore, temporarily allowing six. Protection ends with the request; the next ordinary backup resumes normal retention. Pre-migration snapshots are not automatically pruned. Prepare creates an upgrade snapshot only when needed, not an extra rolling snapshot on every restart.

Use the host's internal terminal after opening the program:

```text
db status [database]
db backups <database>
db backup
db version
db verify <database> <backup-id>
db verify <database> --confirm --accept-interrupted
db restore <database> <backup-id> --confirm
db recover <database> <operation-id> --complete --confirm
db recover <database> <operation-id> --rollback --confirm
db unlock <database> --confirm
db import-backup <database> "legacy file.db" --confirm
```

Startup accepts only the data-directory options; `dotnet Pengin1011.dll db ...` is no longer supported. The existing AIPresetAdmin module also exposes /db status, backups, backup, verify, restore, recover and unlock. Local file import is terminal-only. Both /db and /preset use the admin policy in §2.

A state-changing maintenance request first returns an accepted response, then the main flow stops business. It proceeds only when the exit report confirms resources settled, keeping the same directory lock. Requests are rejected if an existing exit report already confirms resources did not settle. Import shares the maintenance semaphore; import and restore-target prechecks reject business tables with no migration history but allow an empty database. Import success does not prove compatibility with every version of a module's DbContext.

Successful restore/recover/live verify/unlock transfers directory ownership to a child; the old process exits after the child confirms business startup, and the child then reads terminal input. Failed startup leaves the old maintenance terminal available, but data access resumes only after the child exits and ownership is reacquired. If data commands keep returning MaintenanceBusy after a failed handoff, follow the [handoff contract and paused-access recovery steps](../../Document/Pengin1011/架构设计.md#73-内部终端与-discord).

Restore records use FormatVersion 2 and one authoritative State. Before cutover, the host preserves the original database and sidecar existence/hashes in quarantine and stages a verified target. complete must produce the selected snapshot's data; rollback must prove and restore the original files. Corrupt or unsupported old unfinished records block startup; never delete or edit their JSON merely to make a test or startup pass. Backup, checkpoint, live verify and unlock do not open an unfinished restore site; use recover first.

If a restore record is already Completed but clearing its migration intent fails, follow the [restore-record procedure](../../Document/Pengin1011/架构设计.md#74-恢复记录) before retrying `db recover <database> <restore-operation-id> --complete --confirm` for that same restore operation.

The maintenance terminal remains available when business configuration fails. Missing Discord/AI credentials do not prevent maintenance of a resolved directory. If the config cannot be parsed, fix it before data operations; db version still works. Maintenance-only shutdown has a nonzero startup outcome even when an individual terminal command succeeded.

## 7. Logging and module configuration

Use the single Logger from `Pengin1011.Core.Logging`, always passing a source type:

```csharp
Logger.Error(typeof(MyBotRuntime), exception, "Operation failed");
```

Errors must reach both the console and runtime/logs/error.log. Warn writes the same destinations; Info writes stdout and info.log. Every catch handles the failure explicitly. Discord forwarding sends only time, level, source, exception type and a fixed hint; full message and exception details remain local.

Module config uses `config/<ModuleName>.json` under the executable directory. Follow the scaffold's template/create/load pattern with JsonHelper. Its options allow comments and trailing commas; missing or unreadable data is reported, rather than silently replaced. Serialize Discord IDs as strings and parse them into ulong options.

## 8. Localization

Call generated `Loc.Get`/`Loc.Format` with `using Pengin1011.Core;`. The generated type belongs to each calling assembly and routes to its own resources before host fallback; do not declare a hand-written Pengin1011.Core.Loc.

```text
Localization/Strings.resx
Localization/zh-CN/Strings.resx
```

For modules the culture is the directory, not a filename suffix, so resources remain embedded in the module DLL. Lookup follows current/parent culture, module neutral, host resources, then key. No resource directory is valid; corrupted module resources are logged and fall back to the host. Keep placeholders identical across languages.

Generator diagnostics follow a different convention: feature Resources directories contain *.resx and *.zh-CN.resx, used through LocalizableResourceString. The generator must not depend on runtime Loc or read the compiler process's current UI culture.

## 9. Module bridge

Mark an exported public static method, or a public instance method on the sole Runtime:

```csharp
public static class SampleLogic {
	[ModuleBridgeExport]
	public static Task<string> GreetingAsync(ulong userId) {
		return Task.FromResult($"hello-{userId}");
	}
}
```

The consumer declares:

```xml
<ItemGroup>
	<BridgeDependency Include="Sample" />
</ItemGroup>
```

Then calls `await ModuleBridge.Sample.GreetingAsync(userId)`. Sample is the provider's assembly/DLL name. No provider type reference is added. Dependencies resolve as sibling projects through Directory.Build.targets and must be acyclic.

Exports must be public on public non-generic enclosing types. They cannot be generic methods or use optional/by-ref parameters, by-ref returns or more than 16 parameters. Types must come from the BCL or host, with no tuples, multidimensional arrays, pointers or module-private types. params arrays keep their expansion behavior. Duplicate names and generated-name collisions are rejected.

The generator distinguishes ordinary values, Task/ValueTask and generic enumerable return shapes exactly. Deferred shapes are supported only at the outermost return type: `Task<IEnumerable<int>>`, `IEnumerable<Task<int>>`, `IEnumerable<int>[]` and `List<Task<int>>` are rejected, while `List<int>`, `Task<List<int>>` and `ValueTask<int[]>` remain supported. Unsupported enumerators, queries, ordered enumerables and observable returns are also rejected when nested. Export declarations report PBG010; consumer manifests report PBG101. Each enumeration acquires a fresh lease when it starts; no enumeration means no active lease. Task leases last through completion; iterator leases last until completion, exception, cancellation or disposal. Do not bypass the generated proxy with a raw dispatcher lookup.

The target must be Ready with admission open. Missing exports throw KeyNotFoundException; unavailable targets or stale signatures throw InvalidOperationException. Module names ignore case while export names preserve it. A broken provider's partial registrations are revoked without disabling unrelated modules.

Ref-like parameters such as Span and ReadOnlySpan remain supported by synchronous value/void exports. They are rejected for Task/ValueTask and generic enumerable returns because those proxies capture arguments in a delegate. Export declarations report PBG010; an old or handwritten consumer manifest with the same unsupported combination reports PBG101.

Each provider build emits registration and a `pengin1011.modulebridge.manifest/3` bridge manifest, including an empty manifest when it has no exports. Consumers read manifests from obj, so a fresh clone needs its first build before proxy IntelliSense appears. Normal builds rebuild dependencies first; explicitly skipping project-reference or bridge dependency builds can consume stale provider artifacts and manifests. After changing exports or the generator, rebuild the provider and consumer with dependencies enabled. Changing the generator namespace requires updating the generated manifest path in Directory.Build.targets and pipeline tests. Detailed diagnostic numbers and manifest rules are in architecture §5.

## 10. Runtime layout and verification

```text
<program directory>/
├── Pengin1011.dll
├── module/
├── config/
│   ├── config.json
│   ├── aipresets.json
│   └── <ModuleName>.json
├── data/
│   ├── <DatabaseId>.db
│   └── backup/
└── runtime/logs/
```

config.json holds the Discord token, admin role IDs, data-directory setting and optional logger channel IDs. AI credentials belong in aipresets.json. Failed configuration creates templates where possible and keeps a maintenance terminal rather than connecting with missing credentials.

Normal console commands include help, status, modules, sync, preset list/show/add/set/remove, db, reload and exit. quit is an alias for exit; reload only displays a notice that a full restart is required. The first Ctrl+C and SIGTERM use the shutdown coordinator; a second Ctrl+C permits immediate termination. Windows console-close cleanup is bounded best effort within the operating system's deadline. Preset and sync actions are unavailable in maintenance-only mode. See the [deployment boundaries](../../Document/Pengin1011/架构设计.md#8-文档与部署边界) for standalone and managed deployments.

Use the linked BuildTemplates as the maintained complete module example instead of copying a second implementation into this guide. Add behavior tests in the confirmed flat Tests/Pengin1011.Tests directory. Template/migration/publish and crash tests use temporary directories; restart tests use distinct processes. Run focused tests first, then Debug and Release full builds and test runs, using the [project's common commands](../../Document/Pengin1011/AGENTS.md#常用命令).

Record actual results and explicitly identify platforms or external services that were not exercised.
