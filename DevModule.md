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

The scaffold creates a csproj, command class, Runtime, config loader and DbContext under `Modules/<Name>/`, and adds the project to the solution. Package references are inherited from the host; the scaffold's explicit EF Sqlite/Design references support database runtime and migration generation. Do not add other packages without approval.

Keep the host project reference and the generator analyzer reference. `EmitCompilerGeneratedFiles` must remain enabled so bridge manifests reach dependent modules through `obj/`. Only add `BridgeDependency` when the module consumes another module's bridge.

Modules load from the executable directory's `module/` once per process. A new DLL takes effect after a full restart: stop the old process, replace the artifact, then start again. `sync` only registers the current command tree; `reload` returns a restart notice. The host contract is version 4; rebuild modules when upgrading the host because the removed APIs are not binary compatible.

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

Do not use `[ComponentInteraction]` or `[ModalInteraction]`: the host does not dispatch those attributes. Register handlers with ownership instead:

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

## 4. AI presets and calls

`config/aipresets.json` contains MaxParallel and the preset list; `config.json` contains no AI connection section. Supported providers are openai, gemini and vertex. See architecture §6 for required fields and the canonical file example.

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

Presets are immutable snapshots. Management code uses `AIPresetStore.AddPatch`, `Apply(name, patch, expectedVersion, out result)` and `RemovePatch`; Apply also performs rename. Patch distinguishes absent, set and clear. Save failure leaves the old snapshot in place, and result.Error is already rendered text. Do not restore the removed whole-object Update API or format that error a second time.

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

Messages returns Succeeded, Partial or Failed with planned chunks, sent messages and failed indexes. Inspect these results. Sends/edits default to AllowedMentions.None; Delete treats Discord 404 as already deleted. StreamingReply has no background flush timer, and normally spaces edits by 3000 ms.

Permission checks are thread-aware: sending in a thread uses SendMessagesInThreads. Discord.Net's available permission view may not fully model implicit inheritance; inspect the helper and library behavior when adding permission-sensitive operations.

## 6. Database preparation and maintenance

The database path is `<resolved data directory>/<ModuleName>.db`. Resolution is `--data-dir > config.json Databases.DataDirectory > <program directory>/data`; relative paths use the program directory. Environment variables are not an input. The default directory may be created on first start; explicit missing paths require `--create-data-dir`.

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

A state-changing maintenance request first returns an accepted response, then the main flow stops business. It proceeds only when the exit report confirms resources settled, keeping the same directory lock. Successful restore/recover/live verify/unlock starts a new process with the resolved directory. Failure keeps diagnostics and the maintenance terminal available.

Restore records use FormatVersion 2 and one authoritative State. Before cutover, the host preserves the original database and sidecar existence/hashes in quarantine and stages a verified target. complete must produce the selected snapshot's data; rollback must prove and restore the original files. Corrupt or unsupported old unfinished records block startup; never delete or edit their JSON merely to make a test or startup pass. Backup, checkpoint, live verify and unlock do not open an unfinished restore site; use recover first.

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

Each provider build emits registration and a version-2 bridge manifest, including an empty manifest when it has no exports. Consumers read manifests from obj, so a fresh clone needs its first build before proxy IntelliSense appears. Changing the generator namespace requires updating the generated manifest path in Directory.Build.targets and pipeline tests. Detailed diagnostic numbers and manifest rules are in architecture §5.

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
│   ├── <ModuleName>.db
│   └── backup/
└── runtime/logs/
```

config.json holds the Discord token, admin role IDs, data-directory setting and optional logger channel IDs. AI credentials belong in aipresets.json. Failed configuration creates templates where possible and keeps a maintenance terminal rather than connecting with missing credentials.

Normal console commands include help, status, modules, sync, preset list/show/add/set/remove, db and exit. Ctrl+C follows the same shutdown coordinator. Preset and sync actions are unavailable in maintenance-only mode.

Use BuildTemplates as the maintained complete module example instead of copying a second implementation into this guide. Add behavior tests in the flat Tests/Pengin1011.Tests directory. Template/migration/publish and crash tests use temporary directories; restart tests use distinct processes. Record actual results and explicitly identify platforms or external services that were not exercised.
