# Build a Pengin1011 module

This guide starts with a C# module in the framework repository. For a module that lives in its own repository, follow the [standalone SDK guide](ModuleSdk.md).

## Run your first command

Complete the bot configuration in the quick start and stop the bot. [Quick start](README.md#quick-start)

From the framework repository root, create a module called `MyBot` and run it.

```sh
dotnet build Pengin1011.csproj -t:NewModule -p:Module=MyBot
dotnet build Pengin1011.csproj -t:BuildModule -p:Module=MyBot
dotnet run --project Pengin1011.csproj
```

The scaffold creates `Modules/MyBot/` and adds it to the solution. The build copies `MyBot.dll` to the bot's `module/` directory. Type `modules` in the bot console and look for `MyBot` with state `Ready`. Once the bot connects, run `/example` in Discord. It replies with a greeting visible only to you.

To change the command to `/ping`, replace the existing command method in `Modules/MyBot/MyBot.cs` with the following method. Edit `SampleGreeting` in the module's localization files to change the reply.

```csharp
[SlashCommand("ping", "Reply with a greeting")]
public async Task Ping() {
	await RespondAsync(Loc.Get("SampleGreeting"), ephemeral: true);
}
```

After each change, stop the bot, run the `BuildModule` command again and restart it. [Command template](BuildTemplates/ModuleClass.tpl)

## Use an AI model

Set up the model once in the host's `config/aipresets.json`. Any module can use the same preset by name. Here is a Gemini example. Replace the key and model ID with the values for your account.

```json
{
  "MaxParallel": 5,
  "Presets": [
    {
      "Name": "assistant",
      "Provider": "gemini",
      "ApiKey": "your-api-key",
      "Model": "your-model-id"
    }
  ]
}
```

For OpenAI, set `Provider` to `openai` and supply `ApiKey`, `BaseUrl` and `Model`. For Vertex AI, use `vertex` with `Project`, `Location` and `Model`, and configure Application Default Credentials. Restart the bot after editing the file. The bundled `/preset` commands can manage presets while the bot is running.

Import `Pengin1011.Services.AI` in the module. This method sends a question to the preset above and returns the answer text. A handled request failure returns `null`.

```csharp
private static async Task<string?> AnswerAsync(string question, CancellationToken ct) {
	var request = new AIRequest { Messages = [new AIMessage(AIRole.User, question)] };
	var result = await AIClient.CompleteAsync("assistant", request, ct);
	return result?.Text;
}
```

Use a string literal or a `const string` for a named preset. Calling `CompleteAsync(request, ct)` or `StreamAsync(request, ct)` uses the first preset. Configure every preset the module uses before starting the bot.

To show an answer as it arrives, pass the incoming Discord message and the request to this method. It updates the reply as text arrives and flushes the final text when the stream ends. Import `Discord`, `System.Text` and `Pengin1011.Services.Discord` as well.

```csharp
private static async Task<StreamSyncStatus> ReplyAsync(IMessage message, AIRequest request, CancellationToken ct) {
	var reply = new StreamingReply(ReplySinks.FromMessage(message));
	var text = new StringBuilder();
	await foreach (var delta in AIClient.StreamAsync("assistant", request, ct)) {
		text.Append(delta.Text);
		reply.Append(delta.Text);
		await reply.FlushAsync(ct);
	}
	return await reply.FinalizeAsync(text.ToString(), ct);
}
```

A final `Incomplete` status means at least one Discord message update failed. Streaming errors and cancellation propagate to the caller.

## Commands, events and configuration

Each module has one `IModuleRuntime`. The scaffold's `MyBotRuntime.InitializeAsync` loads the module configuration. Use it to subscribe to events or start background work. In `StopAsync`, dispose subscriptions, remove component handlers and await that work. [Runtime template](BuildTemplates/ModuleRuntime.tpl)

- Keep `[ModuleAvailability(typeof(MyBotRuntime))]` on the slash command class and use the default command run mode. Add `[RequireBotAdmin]` to restrict a command to the bot owner or a member with a role listed in `Discord.AdminRoleIds`.
- For events and buttons, obtain the module's owner with `ModuleRegistry.RunOf(GetType().Assembly)` during initialization. Pass that owner to `DiscordGateway.SubscribeMessage(owner, handler)` or `Components.Register(owner, customId, handler)`. Keep the subscription handle and component ID for cleanup. Use a unique ID for each active button or modal. Start any work that must always run in `InitializeAsync` because a late subscriber can miss the Ready event.
- Put module settings in `config/MyBot.json` beside the bot. Add fields to the scaffold's options class and read them from the runtime. For translated replies, add matching keys to `Localization/Strings.resx` and `Localization/zh-CN/Strings.resx` and call `Loc.Get` or `Loc.Format`. [Configuration template](BuildTemplates/ModuleConfig.tpl)

For sending, editing or splitting long messages, use `Messages`. Inspect the returned result for partial delivery. `Interactions` handles deferred replies and follow-ups, `History` reads earlier messages, and `Attachments.DownloadAsync` downloads attachments with a default limit of 25 MiB. [Discord helpers](Services/Discord)

## Store module data

For a new module that needs persistent data, enable the database option when creating it. This example uses a separate module called `Notes`.

```sh
dotnet build Pengin1011.csproj -t:NewModule -p:Module=Notes -p:WithDatabase=true
```

Add entities and their `DbSet` properties to the generated `NotesDbContext`. Keep its `[ModuleDatabase("Notes")]` identifier stable. From the repository root, restore the EF tool and generate a migration for each supported backend.

```sh
dotnet tool restore
dotnet build Pengin1011.csproj -t:AddModuleMigration -p:Module=Notes -p:Context=NotesDbContext -p:MigrationName=Init -p:Provider=SQLite
dotnet build Pengin1011.csproj -t:AddModuleMigration -p:Module=Notes -p:Context=NotesDbContext -p:MigrationName=Init -p:Provider=MySQL
dotnet build Pengin1011.csproj -t:AddModuleMigration -p:Module=Notes -p:Context=NotesDbContext -p:MigrationName=Init -p:Provider=PostgreSQL
dotnet build Pengin1011.csproj -t:BuildModule -p:Module=Notes
```

Keep the generated migrations, designer files and snapshots with the module. When the model changes, repeat the migration commands with a new name. The host prepares the configured database before it calls `InitializeAsync`. Open it from module code with `using var db = Databases.Open<NotesDbContext>();`. [Database template](BuildTemplates/ModuleDbContext.tpl)

### Migrations in a standalone SDK project

In an independent project, declare the same kind of context. For SDK `1.0.0`, add this development dependency inside an `<ItemGroup>` in the module's project file.

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" PrivateAssets="all" />
```

From the module repository root, create a tool manifest with `dotnet new tool-manifest` if needed, then install `dotnet-ef` with `dotnet tool install dotnet-ef --version 10.0.12`. Once the manifest includes that tool, other developers can run `dotnet tool restore`. For a context named `MyModuleDbContext`, run the following commands from the directory containing the module's `.csproj`, then rebuild the module.

```sh
dotnet tool run dotnet-ef -- migrations add Init --context MyModuleDbContext --output-dir Migrations/MyModuleDbContextMigrations/SQLite
dotnet tool run dotnet-ef -- migrations add Init --context MyModuleDbContextMySQL --output-dir Migrations/MyModuleDbContextMigrations/MySQL
dotnet tool run dotnet-ef -- migrations add Init --context MyModuleDbContextPostgreSQL --output-dir Migrations/MyModuleDbContextMigrations/PostgreSQL
```

## Call another module

In the providing module, mark a public static method, or a public method on its runtime, with `[ModuleBridgeExport]` from `Pengin1011.Core`. For example, a module named `Sample` can export a greeting.

```csharp
public static class Greetings {
	[ModuleBridgeExport]
	public static string Greet(string name) => $"Hello, {name}!";
}
```

In the framework repository, declare the sibling module in the consumer's project file.

```xml
<ItemGroup>
  <BridgeDependency Include="Sample" />
</ItemGroup>
```

In an independent SDK project, use a project reference with the path to the provider.

```xml
<ItemGroup>
  <ProjectReference Include="../Sample/Sample.csproj" ReferenceOutputAssembly="false" BridgeDependency="true" />
</ItemGroup>
```

Build the consumer, import `Pengin1011.Core` and call `ModuleBridge.Sample.Greet("Ada")`. Deploy both DLLs together. Keep bridge dependencies acyclic and use BCL or host types in the exported signature.

## Deploy the bot

To build a directory containing the host and its in-repository modules, run this from the framework repository root.

```sh
dotnet build Pengin1011.csproj -c Release -t:PublishAll
```

Copy the contents of `bin/Release/net10.0/publish/` to the installation directory. Keep the instance's `config/`, `data/` and any separately installed module DLLs when upgrading. Start the bot there with `dotnet Pengin1011.dll`.
