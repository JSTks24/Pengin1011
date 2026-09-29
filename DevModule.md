# Pengin1011 Module Development Guide

This guide covers module structure, lifecycle, framework services and build commands.

## 1. How modules work

Bot features are implemented in **modules**: separate class-library projects compiled to DLLs in the host's `module/` directory. At startup, the host scans the directory once, loads assemblies and checks their contracts. Valid modules have their commands published and their runtimes initialized.

Consequences of this model:

- Modules are added or updated by building the DLL and restarting the process. There is no hot reload; a running DLL is locked on Windows, so stop the host first.
- Modules that fail contract checks are excluded from startup.
- Modules must not reference or call each other. Each module owns its configuration and, when needed, its database.

A module project references the host project (`Pengin1011.csproj`), which transitively provides Discord.Net, the AI client, and all framework services. The namespace convention is `Pengin1011.Modules.<Name>`.

## 2. Quick start

Run from the repository root (`Pengin1011.csproj` directory):

```bash
# 1. Scaffold a new module (five files, auto-added to the solution)
dotnet build Pengin1011.csproj -t:NewModule -p:Module=MyBot

# 2. Implement the generated command, runtime and configuration files

# 3. Only for modules with a database: restore tools and generate a migration
dotnet tool restore
dotnet build Pengin1011.csproj -t:AddModuleMigration -p:Module=MyBot -p:MigrationName=Init

# 4. Build after generating migrations so the module DLL includes them
dotnet build Pengin1011.csproj -t:BuildModule -p:Module=MyBot

# 5. Run the host
dotnet run --project Pengin1011.csproj
```

Other build targets:

| Command | Purpose |
|---|---|
| `dotnet build Pengin1011.csproj -t:BuildAllModules` | Build every module |
| `dotnet build Pengin1011.csproj -t:AddAllModuleMigrations -p:MigrationName=<Name>` | Generate migrations for all modules |
| `dotnet build Pengin1011.csproj -t:PublishAll` | Publish host + modules |
| `dotnet build Pengin1011.sln` | Build everything including tests |

## 3. Module anatomy

The scaffold creates five files under `Modules/<Name>/`:

| File | Role |
|---|---|
| `<Name>.csproj` | Project file. References the host project; copies the built DLL into `module/`. Do not add other package references (see §9). |
| `<Name>.cs` | Command class — Discord slash/message/user commands. |
| `<Name>Runtime.cs` | The `IModuleRuntime` implementation — the single business entry point. |
| `<Name>Config.cs` | Config loader for `config/<Name>.json`. |
| `<Name>DbContext.cs` | EF Core `DbContext` wired to the module's own SQLite database. Delete this file if the module needs no persistence. |

## 4. Lifecycle

Every module assembly must contain **exactly one** `IModuleRuntime` implementation (zero or more than one is a load error):

```csharp
public interface IModuleRuntime {
	Task InitializeAsync(CancellationToken ct);
	Task StopAsync(CancellationToken ct);
}
```

A successful startup changes `Starting` to `Ready`. Initialization failure marks the module `Disabled`. Normal cleanup changes `Starting` or `Ready` to `Stopping`, then to `Stopped` when cleanup succeeds. Failed modules can remain `Disabled` after cleanup; cleanup failure also results in `Disabled`. A timeout while waiting does not mean the cleanup task has finished.

The two tokens mean different things:

- `InitializeAsync` receives the module's **business cancellation token**. It is cancelled when the module is being stopped; use it for `WaitAsync`/`ThrowIfCancellationRequested` in long-running setup.
- `StopAsync` receives a **cleanup budget token** (10 seconds by default). It is independent of business cancellation — cleanup that must complete (flushing, saving) should run even if business work was cancelled; await your background tasks yourself inside `StopAsync`.

Rules the host enforces:

- Initialization is admitted at most once per module per process. Modules rejected by contract checks or blocked by shutdown do not start initialization.
- Once cleanup starts, repeated stop requests reuse the same task. `StopAsync` is not called a second time.
- If `InitializeAsync` throws, the module is marked `Disabled` and cleanup is requested after the initialization task ends. Cleanup must tolerate partially initialized resources.
- During shutdown, module cleanup starts only after control operations and tracked work have ended. If they do not finish within the exit budget, cleanup is skipped and the exit report records the incomplete step.
- While a module is not `Ready`, its commands are intercepted and rejected with "module unavailable" — the module does not need to check its own state in command handlers.
- The host does not clean up module resources twice. Disposing subscriptions, timers, and background tasks is the runtime's own responsibility in `StopAsync`.

## 5. Commands

Commands are Discord.Net interaction modules:

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

Contract enforced at load time:

1. Every command class **must** carry `[ModuleAvailability(typeof(<Your>Runtime))]`, and the runtime type must be the one selected in the same assembly. This is how the host intercepts commands of non-Ready modules.
2. Command execution mode is host-unified `Sync`. Do not set `RunMode.Async` — such modules are rejected.
3. Command classes contain no initialization logic. They only parse arguments, check permissions, reply, and call into services/your runtime.

## 6. Buttons, select menus, modals

`[ComponentInteraction]` and `[ModalInteraction]` attributes are **not** dispatched by the host. Components are registered at runtime with an owner, a custom id, and a TTL:

```csharp
var run = ModuleRegistry.RunOf(GetType().Assembly);

Components.Register(run, "mybot:confirm:42", async (interaction, ct) => {
	await interaction.RespondAsync("confirmed", ephemeral: true);
});

// In-memory state shared by callbacks for this registration:
var voteState = new VoteState();
var voteId = $"mybot:vote:{Guid.NewGuid():N}";
Components.Register(run, voteId, voteState, TimeSpan.FromMinutes(10),
	async (VoteState state, SocketInteraction interaction, CancellationToken ct) => {
		var votes = Interlocked.Increment(ref state.Yes);
		await interaction.RespondAsync($"votes: {votes}", ephemeral: true);
	});
```

Reference signatures:

```csharp
public static void Register(LoadedModule? owner, string customId,
	Func<SocketInteraction, CancellationToken, Task> handler, TimeSpan? ttl = null)
public static void Register<TState>(LoadedModule? owner, string customId, TState state, TimeSpan? ttl,
	Func<TState, SocketInteraction, CancellationToken, Task> handler)
public static bool Unregister(string customId)
public static bool TryGetState<TState>(string customId, out TState? state)
public static Task<bool> DisableMessageComponentsAsync(IMessage? message, CancellationToken ct = default)
```

These snippets run inside module instance methods. Import `Discord.WebSocket`, `Pengin1011.Core.Modules` and `Pengin1011.Services.Discord`; `VoteState` is defined in §10. Use the same custom ID when registering a callback and building its button or menu. Separate sessions need separate IDs: registering an existing ID replaces its handler and state.

Default TTL is 180 seconds and is renewed on a valid interaction. Expired handlers stop firing. Registration state is held in memory and does not survive a process restart. Obtain the module's `LoadedModule` owner with `ModuleRegistry.RunOf(GetType().Assembly)`; `LoadedModule` is in `Pengin1011.Core`.

For slash-command helpers (deferring, followups), see §8.4.

## 7. Gateway events

Subscribe from `InitializeAsync`, store the returned `IDisposable`, and dispose it in `StopAsync`. The first argument must be your run object (pass `null` only in tests):

```csharp
using Discord.WebSocket;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

namespace Pengin1011.Modules.MyBot;

public sealed class MyBotRuntime : IModuleRuntime {
	private IDisposable? _messageSubscription;

	public Task InitializeAsync(CancellationToken ct) {
		var run = ModuleRegistry.RunOf(GetType().Assembly);
		_messageSubscription = DiscordGateway.SubscribeMessage(run, OnMessage);
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct) {
		_messageSubscription?.Dispose();
		_messageSubscription = null;
		return Task.CompletedTask;
	}

	private Task OnMessage(SocketMessage message, CancellationToken ct) {
		// handle message
		return Task.CompletedTask;
	}
}
```

Available subscriptions (namespace `Pengin1011.Services.Discord`):

```csharp
IDisposable SubscribeMessage(LoadedModule? owner, Func<SocketMessage, CancellationToken, Task> handler)
IDisposable SubscribeReady(LoadedModule? owner, Func<CancellationToken, Task> handler)
IDisposable SubscribeGuildAvailable(LoadedModule? owner, Func<SocketGuild, CancellationToken, Task> handler)
IDisposable SubscribeGuildJoined(LoadedModule? owner, Func<SocketGuild, CancellationToken, Task> handler)
IDisposable SubscribeInteraction(LoadedModule? owner, Func<SocketInteraction, CancellationToken, Task> handler)
IDisposable SubscribeButton(LoadedModule? owner, Func<SocketMessageComponent, CancellationToken, Task> handler)
IDisposable SubscribeSelectMenu(LoadedModule? owner, Func<SocketMessageComponent, CancellationToken, Task> handler)
IDisposable SubscribeModal(LoadedModule? owner, Func<SocketModal, CancellationToken, Task> handler)
```

Event dispatch has a default capacity of 128 tracked tasks. During shutdown, the host stops accepting new work and waits for admitted tasks within its exit budget. When dispatch rejects an interaction, the host attempts a busy response. Module-owned background tasks must be cancelled and awaited by the runtime.

## 8. Framework services

The service facades below are static except for `StreamingReply`, which is instantiated per reply.

### 8.1 AI — `AIClient` (namespace `Pengin1011.Services.AI`)

The framework provides OpenAI-compatible and Gemini backends (Gemini API key or Vertex credentials). Defaults come from `config/config.json` (`AI.Provider`, `AI.OpenAI`, `AI.Gemini`). Requests can override the endpoint, credentials, model and supported generation parameters. `AIProvider` selects the protocol; setting `Endpoint` does not change it.

```csharp
public static Task<AIResult?> CompleteAsync(AIRequest request, CancellationToken ct = default)
public static Task<AIResult?> CompleteAsync(AIProvider stack, AIRequest request, CancellationToken ct = default)
public static IAsyncEnumerable<AIStreamDelta> StreamAsync(AIRequest request, CancellationToken ct = default)
public static IAsyncEnumerable<AIStreamDelta> StreamAsync(AIProvider stack, AIRequest request, CancellationToken ct = default)
```

```csharp
public sealed class AIRequest {
	public string System { get; init; } = "";                                    // system prompt
	public required IReadOnlyList<AIMessage> Messages { get; init; }             // conversation
	public IReadOnlyList<AIImage> Images { get; init; } = [];                    // image data; attachment behavior described below
	public string? Model { get; init; }                                          // null = default model from config
	public AIEndpoint? Endpoint { get; init; }                                   // null = default endpoint from config
	public float? Temperature { get; init; }                                     // sampling parameter, both backends
	public float? TopP { get; init; }                                            // sampling parameter, both backends
	public int? MaxOutputTokens { get; init; }                                   // output token limit, both backends
}

public sealed class AIEndpoint {
	public string ApiKey { get; init; } = "";
	public Uri? BaseUrl { get; init; }        // required for OpenAI-compatible; optional for Gemini
	public string? Project { get; init; }     // Gemini Vertex auth (mutually exclusive with ApiKey)
	public string? Location { get; init; }    // Gemini Vertex auth
}

public sealed record AIMessage(AIRole Role, string Text);   // AIRole.User | AIRole.Assistant
public sealed record AIImage(byte[] Data, string MimeType);
public sealed record AIUsage(int InputTokens, int OutputTokens);
public sealed record AIResult(string Text, AIUsage Usage);
public sealed record AIStreamDelta(string Text, AIUsage? Usage = null);
public enum AIProvider { OpenAI, Gemini }
```

Behavior:

- Non-streaming calls return `null` for an unconfigured backend or a handled request failure. Failures recognized as transient are retried up to twice, with 1 s and 2 s delays; each request attempt has a 120 s timeout. OpenAI HTTP 408/429/5xx and Gemini server errors are classified as transient, as are network failures and attempt timeouts.
- Argument errors and caller cancellation propagate as exceptions. For non-streaming calls, override backend construction is inside the request error boundary: construction failures are logged, transient failures are retried with the same backoff, and other failures return `null`. Streaming construction failures propagate to the caller, which is responsible for logging them. Failed construction entries are removed from the cache so later calls can attempt construction again.
- Streaming calls have a 120 s timeout for the stream, do not retry and propagate failures to the caller.
- All calls share a concurrency gate (`AI.MaxParallel`, default 5).
- Sampling parameters (`Temperature`, `TopP`, `MaxOutputTokens`) are passed through on both stacks; parameters left unset are omitted from the request. Whether the service accepts a supplied parameter depends on the endpoint and model.
- When `Endpoint` is set, its connection settings replace those of the selected stack. `Model` is required because the override has no default model. OpenAI requires `ApiKey` + `BaseUrl`; Gemini requires `ApiKey` or `Project` + `Location` (mutually exclusive), with optional `BaseUrl`. Vertex uses Application Default Credentials in the process environment.
- OpenAI attaches images to the last user message, or adds a user message if there is none. Gemini attaches images to the final message if it is a user message; otherwise it appends a new user message.

The following examples run inside async module methods. Import `Pengin1011.Core` and `Pengin1011.Services.AI`. Replace model names, credentials and endpoint URLs with those supported by the chosen service.

```csharp
// Default endpoint and model from config/config.json
var defaultReply = await AIClient.CompleteAsync(new AIRequest {
	System = "You are a concise assistant.",
	Messages = [new AIMessage(AIRole.User, "Summarize: ...")],
});
```

```csharp
// Per-call override: custom OpenAI-compatible endpoint + key + model
var overrideReply = await AIClient.CompleteAsync(AIProvider.OpenAI, new AIRequest {
	System = "You are a concise assistant.",
	Messages = [new AIMessage(AIRole.User, "Hello")],
	Model = "your-openai-model",
	Endpoint = new AIEndpoint {
		ApiKey = "sk-...",
		BaseUrl = new Uri("https://your-gateway.example.com/v1"),
	},
	MaxOutputTokens = 500,
});
```

For streaming, also import `System.Text` and `Pengin1011.Services.Discord`. `userMessage` is the incoming `Discord.IMessage`, and `ct` is the handler's cancellation token. The caller handles propagated exceptions and records errors through `Logger`.

```csharp
// Streaming with Gemini override and sampling parameters
var streaming = new StreamingReply(ReplySinks.FromMessage(userMessage));
var streamingText = new StringBuilder();
await streaming.StartAsync("generating...", ct);
await foreach (var delta in AIClient.StreamAsync(AIProvider.Gemini, new AIRequest {
	Messages = [new AIMessage(AIRole.User, "Tell me a story")],
	Model = "your-gemini-model",
	Endpoint = new AIEndpoint { ApiKey = "..." },
	Temperature = 0.7f,
	TopP = 0.9f,
	MaxOutputTokens = 2048,
}, ct)) {
	streamingText.Append(delta.Text);
	streaming.Append(delta.Text);
	await streaming.FlushAsync(ct);
}
if (streamingText.Length == 0) {
	await streaming.SetStatusAsync("No text returned.", ct);
} else {
	await streaming.FinalizeAsync(streamingText.ToString(), ct);
}
```

### 8.2 Messages — `Messages` / `MessageSplitter`

```csharp
Task<MessageSendResult> ReplyAsync(IMessage? source, string? text, AllowedMentions? allowedMentions = null, CancellationToken ct = default)
Task<MessageSendResult> SendAsync(IMessageChannel? channel, string? text, AllowedMentions? allowedMentions = null, CancellationToken ct = default)
Task<IUserMessage?> SendEmbedAsync(IMessageChannel? channel, Embed? embed, string? text = null, AllowedMentions? allowedMentions = null)
Task<IUserMessage?> SendFileAsync(IMessageChannel? channel, byte[]? data, string filename, string? text = null, AllowedMentions? allowedMentions = null)
Task<bool> EditAsync(IUserMessage? message, string? text, CancellationToken ct = default)
Task<bool> DeleteAsync(IMessage? message, CancellationToken ct = default)
Task<bool> ForwardAsync(IMessage? message, IMessageChannel? destination)
Task<DirectMessageResult> SendDirectAsync(ulong userId, string? text, CancellationToken ct = default)
Task<DirectMessageResult> SendDirectAsync(IUser? user, string? text, CancellationToken ct = default)
Task<IUserMessage?> SendDirectFileAsync(IUser? user, byte[]? data, string filename, string? text = null)
Task<IMessage?> FetchAsync(ulong channelId, ulong messageId)
Task<IReadOnlyList<BulkSendResult>> SendBulkAsync(IReadOnlyCollection<IMessageChannel>? channels, string? text, int delayMs = SendBulkDelayMs, CancellationToken ct = default)
```

Long texts are automatically split into multiple messages (`MessageSendResult.PlannedChunks` / `Sent` / `FailedChunkIndexes`; status `Succeeded | Partial | Failed`). To split text yourself:

```csharp
IReadOnlyList<string> MessageSplitter.Split(string? text, int limit = MessageSplitter.DefaultLimit)
```

### 8.3 Streaming replies — `StreamingReply`

`StreamingReply` buffers text and updates one or more Discord messages. The caller drives updates with `FlushAsync`; there is no background flush timer. See §8.1 for a streaming example.

```csharp
// Methods on a StreamingReply instance
void Append(string delta)
Task<bool> StartAsync(string statusText, CancellationToken ct = default)
Task<bool> SetStatusAsync(string statusText, CancellationToken ct = default)
Task<StreamSyncStatus> FlushAsync(CancellationToken ct = default)
Task<StreamSyncStatus> FinalizeAsync(string fullText, CancellationToken ct = default)
Task<bool> PublishErrorAsync(string errorText, CancellationToken ct = default)
```

The default edit interval is 3000 ms, configurable through `StreamingReplyOptions.EditIntervalMs`. `FinalizeAsync` takes the complete final text and removes excess posts when synchronization succeeds. Check return values for failed or incomplete Discord operations; publishing an error message belongs in the caller's failure path.

### 8.4 Interaction helpers — `Interactions`

```csharp
Task<bool> DeferAsync(IDiscordInteraction? interaction, bool ephemeral = true)
Task<IUserMessage?> FollowupAsync(IDiscordInteraction? interaction, string? text, bool ephemeral = true)
Task<IUserMessage?> FollowupFileAsync(IDiscordInteraction? interaction, byte[]? data, string filename, string? text = null, bool ephemeral = true)
Task<bool> EditOriginalAsync(IDiscordInteraction? interaction, string? text)
```

### 8.5 Channels / members / guilds

```csharp
// Channels — permission checks and resolution
IChannel? Channels.Resolve(ulong channelId)
Task<IChannel?> Channels.ResolveAsync(ulong channelId)
bool Channels.CanView(IGuildChannel? channel, IGuildUser? user)
bool Channels.CanSend(IGuildChannel? channel, IGuildUser? user)
bool Channels.CanReadHistory(IGuildChannel? channel, IGuildUser? user)
bool Channels.IsAdministrator(IGuildUser? user)
bool Channels.CanBotView(IGuildChannel? channel)      // the bot's own permissions
bool Channels.CanBotSend(IGuildChannel? channel)
bool Channels.CanBotReadHistory(IGuildChannel? channel)

// Members
IGuildUser? Members.Resolve(ulong guildId, ulong userId)
Task<IGuildUser?> Members.ResolveAsync(ulong guildId, ulong userId)
bool Members.HasAnyRole(IGuildUser? member, IReadOnlyCollection<ulong> roleIds)
Task<RoleOpResult> Members.AddRolesAsync(ulong guildId, ulong userId, IReadOnlyCollection<ulong> roleIds)
Task<RoleOpResult> Members.RemoveRolesAsync(ulong guildId, ulong userId, IReadOnlyCollection<ulong> roleIds)
Task<bool> Members.DownloadMembersAsync(ulong guildId)
IReadOnlyList<ulong> Members.GetRoleMemberIds(ulong guildId, ulong roleId)

// Guilds
SocketGuild? Guilds.Get(ulong guildId)
IRole? Guilds.GetRole(ulong guildId, ulong roleId)
string? Guilds.GetName(ulong guildId)
Task<bool> Guilds.LeaveAsync(ulong guildId)
```

### 8.6 Threads / history / attachments / mentions

```csharp
// Forum threads
Task<bool> Threads.JoinAsync(ulong threadId)
Task<bool> Threads.SetArchivedAsync(ulong threadId, bool archived)
Task<bool> Threads.SetAppliedTagsAsync(ulong threadId, IReadOnlyCollection<ulong> tagIds, CancellationToken ct = default)
Task<IReadOnlyList<IThreadChannel>> Threads.GetThreadsAsync(ulong forumChannelId, bool includeArchived = false, int archivedLimit = 50)
IReadOnlyList<ForumTag> Threads.GetAvailableTags(ulong forumChannelId)

// Channel history, batched with pause to respect rate limits
Task<IReadOnlyList<IMessage>> History.ReadAsync(IMessageChannel? channel, int limit,
	ulong? beforeId = null, ulong? afterId = null, bool oldestFirst = false,
	int batchPauseMs = History.BatchPauseMs, CancellationToken ct = default)

// Attachments
Task<AttachmentData?> Attachments.DownloadAsync(IAttachment? attachment)   // AttachmentData(byte[] Data, string MimeType, string Filename)
bool Attachments.IsImage(string? contentType, string? filename)

// Mention formatting / stripping
string Mentions.User(ulong userId)
string Mentions.Role(ulong roleId)
string Mentions.Channel(ulong channelId)
string Mentions.Timestamp(DateTimeOffset time, char style = 'F')
string Mentions.StripUserMentions(string? text)

// Discord message links
bool MessageLink.TryParse(string? input, out MessageLinkParts? parts)     // (GuildId, ChannelId, MessageId)
```

### 8.7 Databases — `Databases`

Each module uses its own SQLite database at `data/<ModuleName>.db`. `Databases.Open` applies migrations, enables WAL and registers the database for backup. The host schedules backups and attempts a final backup on exit; writes from unfinished tasks are not guaranteed to be included.

```csharp
TContext Databases.Open<TContext>() where TContext : DbContext   // opens + migrates
void Databases.Configure(Type contextType, DbContextOptionsBuilder options)
```

Your `DbContext` wires itself through `Configure` (the scaffold already does this). `VoteItem` below represents an entity defined by the module:

```csharp
public class MyBotDbContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		Databases.Configure(GetType(), options);
	}

	public DbSet<VoteItem> VoteItems => Set<VoteItem>();
}
```

Open contexts with `Databases.Open<YourDbContext>()`, not a direct constructor. `Configure` only configures the connection; it does not create tables or apply migrations. `Open<TContext>` rejects contexts without migrations and rejects a different context type attempting to claim the same database name. Generate migration source files with the build target (§2), rebuild the module to include them, then let `Open` apply them at runtime.

### 8.8 Logging, JSON, module config

Logging goes through the single global logger (`runtime/logs/error.log`, `info.log`):

```csharp
Logger.Error(typeof(MyBotRuntime), exception, "what happened");
Logger.Error(typeof(MyBotRuntime), "plain message");
Logger.Info(typeof(MyBotRuntime), "info message");
```

Always pass `typeof(<your class>)` as the source. Do not create other logging mechanisms.

Config loading uses strict JSON (missing/corrupt files throw, they do not silently return null):

```csharp
T JsonHelper.Load<T>(string path) where T : class
bool JsonHelper.EnsureTemplateFile(string path, string template)   // true when the template was just created
bool JsonHelper.Save<T>(string path, T value)
```

The scaffolded config pattern reads `config/<ModuleName>.json`, writes the template on first start, and lets the host disable the module if loading fails:

```csharp
public static MyBotOptions Load() {
	if (JsonHelper.EnsureTemplateFile(FilePath, Template)) {
		Logger.Info(typeof(MyBotConfig), "config template created: config/MyBot.json");
	}
	return JsonHelper.Load<MyBotOptions>(FilePath);
}
```

Serialize Discord IDs as JSON strings and convert to `ulong` in your options classes.

## 9. Rules

1. **No cross-module references.** Modules never reference or call each other.
2. **No extra packages.** Modules reference the host project only. The scaffold's EF Core Sqlite/Design references exist for the migration toolchain — beyond that, do not add package references.
3. **Namespace** `Pengin1011.Modules.<Name>`.
4. **Ownership is mandatory.** Every event subscription and component registration carries your run object (`ModuleRegistry.RunOf(GetType().Assembly)`). Dispatch checks the owner's readiness. The runtime releases its registrations and resources during cleanup.
5. **Cleanup belongs to the runtime.** Once admitted, cleanup runs at most once. Dispose subscriptions, unregister components, stop timers and await background tasks yourself. Account for partial initialization and the cleanup budget (§4).
6. **Updates require a restart.** Building a module updates the DLL used by the next start. `reload` prints a restart notice and makes no changes.

## 10. Complete example

A module demonstrating a command, an event subscription, in-memory button state, configuration and a database. Button clicks are counted in memory; the database example records a shutdown marker, not persistent vote totals.

```csharp
// Modules/VoteBot/VoteBot.cs
using Discord.Interactions;
using Discord.WebSocket;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

namespace Pengin1011.Modules.VoteBot;

[ModuleAvailability(typeof(VoteBotRuntime))]
public class VoteBot : InteractionModuleBase<SocketInteractionContext> {
	[SlashCommand("votestart", "Start a vote")]
	public async Task VoteStart([Summary("topic", "what to vote on")] string topic) {
		var run = ModuleRegistry.RunOf(GetType().Assembly);
		var state = new VoteState { Topic = topic };
		var customId = $"vote:yes:{Context.Interaction.Id}";
		Components.Register(run, customId, state, TimeSpan.FromMinutes(10),
			async (VoteState s, SocketInteraction interaction, CancellationToken ct) => {
				var votes = Interlocked.Increment(ref s.Yes);
				await interaction.RespondAsync($"yes: {votes}", ephemeral: true);
			});
		var builder = new Discord.ComponentBuilder()
			.WithButton("yes", customId, Discord.ButtonStyle.Success);
		await RespondAsync($"Vote: {topic}", components: builder.Build());
	}
}

public sealed class VoteState {
	public string Topic { get; set; } = "";
	public int Yes;
}
```

```csharp
// Modules/VoteBot/VoteBotRuntime.cs
using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

namespace Pengin1011.Modules.VoteBot;

public sealed class VoteBotRuntime : IModuleRuntime {
	public VoteBotOptions Options { get; private set; } = new();
	private IDisposable? _subscription;

	public Task InitializeAsync(CancellationToken ct) {
		Options = VoteBotConfig.Load();
		using var db = Databases.Open<VoteBotDbContext>();
		var run = ModuleRegistry.RunOf(GetType().Assembly);
		_subscription = DiscordGateway.SubscribeMessage(run, OnMessage);
		return Task.CompletedTask;
	}

	public async Task StopAsync(CancellationToken ct) {
		_subscription?.Dispose();
		_subscription = null;
		var run = ModuleRegistry.RunOf(GetType().Assembly);
		if (run != null) Components.RemoveModule(run);
		if (run?.State != ModuleState.Stopping) return;
		using var db = Databases.Open<VoteBotDbContext>();
		db.VoteRecords.Add(new VoteRecord { Topic = "shutdown-snapshot", CreatedAt = DateTimeOffset.UtcNow });
		await db.SaveChangesAsync(ct);
	}

	private Task OnMessage(SocketMessage message, CancellationToken ct) {
		if (Options.LogMentions && message.MentionedUsers.Count > 0) {
			Logger.Info(typeof(VoteBotRuntime), $"mentioned in #{message.Channel.Name}");
		}
		return Task.CompletedTask;
	}
}
```

```csharp
// Modules/VoteBot/VoteBotConfig.cs
using Pengin1011.Core.Logging;
using Pengin1011.Helper;

namespace Pengin1011.Modules.VoteBot;

public static class VoteBotConfig {
	private static string FilePath => Path.Combine(AppContext.BaseDirectory, "config", "VoteBot.json");

	private const string Template = """
		{
		  "LogMentions": false
		}
		""";

	public static VoteBotOptions Load() {
		if (JsonHelper.EnsureTemplateFile(FilePath, Template)) {
			Logger.Info(typeof(VoteBotConfig), "config template created: config/VoteBot.json");
		}
		return JsonHelper.Load<VoteBotOptions>(FilePath);
	}
}

public class VoteBotOptions {
	public bool LogMentions { get; set; }
}
```

```csharp
// Modules/VoteBot/VoteBotDbContext.cs
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;

namespace Pengin1011.Modules.VoteBot;

public class VoteRecord {
	public long Id { get; set; }
	public string Topic { get; set; } = "";
	public DateTimeOffset CreatedAt { get; set; }
}

public class VoteBotDbContext : DbContext {
	protected override void OnConfiguring(DbContextOptionsBuilder options) {
		Databases.Configure(GetType(), options);
	}

	public DbSet<VoteRecord> VoteRecords => Set<VoteRecord>();
}
```

Scaffold VoteBot, keep its generated project file and replace the four C# files with the examples above. Then run:

```bash
dotnet tool restore
dotnet build Pengin1011.csproj -t:AddModuleMigration -p:Module=VoteBot -p:MigrationName=Init
dotnet build Pengin1011.csproj -t:BuildModule -p:Module=VoteBot
dotnet run --project Pengin1011.csproj
```

Initialization opens the database to apply migrations. Cleanup always releases registrations; the shutdown marker is written only on the normal `Stopping` path. Initialization failures marked `Disabled` skip that write. Each vote has an ID derived from its interaction, so one user's separate votes do not share state. The example counts clicks and does not enforce one vote per user.

## 11. Runtime layout and console

```
<host exe directory>/
├── Pengin1011.dll            # host + framework
├── module/                    # module DLLs — the only load source at startup
├── config/
│   ├── config.json            # framework config (Discord token, AI defaults)
│   └── <ModuleName>.json      # per-module config
├── data/
│   ├── <ModuleName>.db        # one SQLite database per module
│   └── backup/                # scheduled + exit backups
└── runtime/logs/              # error.log, info.log
```

Paths above are relative to the executable directory. `config/config.json` is created from a template on first start and must provide the Discord bot token and default AI backend. Console commands: `help`, `status`, `modules`, `db backup`, `db status`, `sync` (re-register the current command tree), `exit`. `Ctrl+C` uses the same shutdown path as `exit`; an incomplete shutdown returns a nonzero exit code.
