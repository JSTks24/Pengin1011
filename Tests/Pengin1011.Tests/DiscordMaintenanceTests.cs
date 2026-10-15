using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.Net.Rest;
using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.AIPresetAdmin;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DiscordMaintenanceTests {
	[Fact]
	public async Task ActualRestoreCommand_AcknowledgesAndLeavesItsOwnTrackedWorkBeforeRecovery() {
		var directory = Path.Combine(Path.GetTempPath(), "pengin_discord_" + Guid.NewGuid().ToString("N"));
		var rest = new ControlledRestClient();
		using var client = new DiscordSocketClient(new DiscordSocketConfig { RestClientProvider = _ => rest });
		Task<bool>? execution = null;
		try {
			await TestChildProcess.RunArgumentsAsync("db-seed", directory);
			var backupId = File.ReadAllText(Path.Combine(directory, "backup-id.txt"));
			Databases.SetDataDirectoryForTest(directory);
			Databases.Init();
			HostMaintenance.ResetForTest();
			HostExit.ResetForTest();
			ExitCoordinator.ResetForTest();
			InteractionHost.ResetForTest();
			ModuleHost.ResetForTest();
			DiscordGateway.Init(client);
			await client.LoginAsync(TokenType.Bot, "fictional-token", validateToken: false);
			var run = ModuleHost.LoadOne("AIPresetAdmin", typeof(DatabaseAdmin).Assembly.Location);
			ModuleHost.Adopt(run);
			await InteractionHost.PublishInitialAsync();
			await run.InitTask!.WaitAsync(TimeSpan.FromSeconds(5));
			await WaitForAsync(() => run.State == ModuleState.Ready);
			using var subscription = DiscordGateway.SubscribeInteraction(null, InteractionHost.ExecuteAsync);
			var interaction = CreateRestoreInteraction(client, backupId);
			var dispatch = typeof(DiscordGateway).GetMethod("OnInteractionCreated", BindingFlags.Static | BindingFlags.NonPublic)!;
			await (Task)dispatch.Invoke(null, [interaction])!;
			await HostMaintenance.Pending.WaitAsync(TimeSpan.FromSeconds(5));
			var acknowledgement = await rest.FollowupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(L.Get("MaintenanceAccepted"), acknowledgement);
			Assert.True(rest.Deferred);
			Assert.Equal(1, DiscordGateway.Dispatcher!.ActiveCount);
			var original = DatabaseBackupStore.HashFile(Path.Combine(directory, "Fake.db"));
			var restarted = false;

			execution = HostMaintenance.ExecutePendingAsync(HostExit.StopAsync, () => restarted = true);
			await WaitForAsync(() => InteractionHost.Exiting);
			Assert.False(execution.IsCompleted);
			Assert.Equal(ModuleState.Ready, run.State);
			Assert.Equal(original, DatabaseBackupStore.HashFile(Path.Combine(directory, "Fake.db")));
			rest.AllowFollowup.TrySetResult();

			Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.True(restarted);
			Assert.True((await HostExit.StopAsync()).ResourcesSettled);
			Assert.Equal(0, DiscordGateway.Dispatcher.ActiveCount);
			Assert.Equal(ModuleState.Stopped, run.State);
			using var connection = SqliteConnectionPolicy.OpenReadOnly(Path.Combine(directory, "Fake.db"));
			using var query = connection.CreateCommand();
			query.CommandText = "SELECT Name FROM Items";
			Assert.Equal("backup-target", query.ExecuteScalar());
		} finally {
			rest.AllowFollowup.TrySetResult();
			if (execution != null) await execution.WaitAsync(TimeSpan.FromSeconds(5));
			await HostExit.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
			DiscordGateway.DetachForTest();
			InteractionHost.ResetForTest();
			ModuleHost.ResetForTest();
			HostExit.ResetForTest();
			ExitCoordinator.ResetForTest();
			HostMaintenance.ResetForTest();
			Databases.SetDataDirectoryForTest(null);
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			Directory.Delete(directory, true);
		}
	}

	private static async Task WaitForAsync(Func<bool> condition) {
		using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!condition()) await Task.Delay(10, budget.Token);
	}

	private static SocketSlashCommand CreateRestoreInteraction(DiscordSocketClient client, string backupId) {
		var constructor = typeof(SocketSlashCommand).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
		var modelType = constructor.GetParameters()[1].ParameterType;
		var models = modelType.Assembly;
		var model = Activator.CreateInstance(modelType)!;
		var data = Activator.CreateInstance(models.GetType("Discord.API.ApplicationCommandInteractionData")!)!;
		var optionType = models.GetType("Discord.API.ApplicationCommandInteractionDataOption")!;
		var options = Array.CreateInstance(optionType, 3);
		options.SetValue(Option(optionType, "database", ApplicationCommandOptionType.String, "Fake"), 0);
		options.SetValue(Option(optionType, "backup", ApplicationCommandOptionType.String, backupId), 1);
		options.SetValue(Option(optionType, "confirm", ApplicationCommandOptionType.Boolean, true), 2);
		var subcommand = Option(optionType, "restore", ApplicationCommandOptionType.SubCommand, null);
		Set(subcommand, "Options", options);
		var commands = Array.CreateInstance(optionType, 1);
		commands.SetValue(subcommand, 0);
		Set(data, "Id", 1UL);
		Set(data, "Name", "db");
		Set(data, "Type", ApplicationCommandType.Slash);
		Set(data, "Options", commands);
		Set(model, "Id", SnowflakeUtils.ToSnowflake(DateTimeOffset.UtcNow));
		Set(model, "ApplicationId", 42UL);
		Set(model, "Type", InteractionType.ApplicationCommand);
		Set(model, "Data", data);
		Set(model, "Token", "fictional-interaction-token");
		Set(model, "Version", 1);
		Set(model, "Entitlements", Array.CreateInstance(modelType.GetProperty("Entitlements")!.PropertyType.GetElementType()!, 0));
		Set(model, "IntegrationOwners", new Dictionary<ApplicationIntegrationType, ulong>());
		var userType = typeof(SocketUser).Assembly.GetType("Discord.WebSocket.SocketGlobalUser")!;
		var user = Activator.CreateInstance(userType, BindingFlags.Instance | BindingFlags.NonPublic, null, [client, 9000UL], null);
		Set(user!, "Username", "owner");
		var interaction = (SocketSlashCommand)constructor.Invoke([client, model, null, user]);
		typeof(SocketSlashCommand).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(interaction, [model]);
		return interaction;
	}

	private static object Option(Type type, string name, ApplicationCommandOptionType kind, object? value) {
		var option = Activator.CreateInstance(type)!;
		Set(option, "Name", name);
		Set(option, "Type", kind);
		if (value != null) Set(option, "Value", value);
		return option;
	}

	private static void Set(object model, string name, object value) {
		var property = model.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
		if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(Optional<>)) {
			value = Activator.CreateInstance(property.PropertyType, [value])!;
		}
		property.SetValue(model, value);
	}

	private sealed class ControlledRestClient : IRestClient {
		private readonly Dictionary<string, string> _headers = [];
		private CancellationToken _cancellation;

		internal TaskCompletionSource<string> FollowupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource AllowFollowup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal bool Deferred { get; private set; }

		public void SetHeader(string key, string value) => _headers[key] = value;
		public void SetCancelToken(CancellationToken token) => _cancellation = token;
		public void Dispose() => AllowFollowup.TrySetResult();

		public Task<RestResponse> SendAsync(string method, string endpoint, CancellationToken token, bool headerOnly, string reason, IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders) {
			return SendAsync(method, endpoint, "", token, headerOnly, reason, requestHeaders);
		}

		public Task<RestResponse> SendAsync(string method, string endpoint, IReadOnlyDictionary<string, object> parameters, CancellationToken token, bool headerOnly, string reason, IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders) {
			return SendAsync(method, endpoint, JsonSerializer.Serialize(parameters), token, headerOnly, reason, requestHeaders);
		}

		public async Task<RestResponse> SendAsync(string method, string endpoint, string payload, CancellationToken token, bool headerOnly, string reason, IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders) {
			_cancellation.ThrowIfCancellationRequested();
			token.ThrowIfCancellationRequested();
			if (endpoint.EndsWith("users/@me", StringComparison.Ordinal)) {
				return Response("""{"id":"42","username":"test-bot","discriminator":"0000","bot":true,"avatar":null}""");
			}
			if (endpoint.EndsWith("oauth2/applications/@me", StringComparison.Ordinal)) {
				return Response("""{"id":"42","name":"test-bot","description":"","icon":null,"owner":{"id":"9000","username":"owner","discriminator":"0001","avatar":null},"bot_public":false,"bot_require_code_grant":false,"verify_key":"fictional","flags":0}""");
			}
			if (endpoint.Contains("interactions/", StringComparison.Ordinal) && endpoint.Contains("/callback", StringComparison.Ordinal)) {
				Deferred = true;
				return Response("", HttpStatusCode.NoContent);
			}
			if (method == "POST" && endpoint.Contains("webhooks/", StringComparison.Ordinal)) {
				using var json = JsonDocument.Parse(payload);
				var content = json.RootElement.GetProperty("content").GetString()!;
				FollowupStarted.TrySetResult(content);
				await AllowFollowup.Task.WaitAsync(token);
				return Response(JsonSerializer.Serialize(new {
					id = "2", channel_id = "1", webhook_id = "42", content,
					author = new { id = "42", username = "test-bot", discriminator = "0000", bot = true },
					timestamp = DateTimeOffset.UtcNow.ToString("O"), tts = false, mention_everyone = false,
					mentions = Array.Empty<object>(), mention_roles = Array.Empty<string>(), attachments = Array.Empty<object>(),
					embeds = Array.Empty<object>(), pinned = false, type = 0, flags = 64
				}));
			}
			throw new InvalidOperationException($"Unexpected fake Discord request: {method} {endpoint}");
		}

		private static RestResponse Response(string json, HttpStatusCode status = HttpStatusCode.OK) {
			return new RestResponse(status, [], new MemoryStream(Encoding.UTF8.GetBytes(json)));
		}
	}
}
