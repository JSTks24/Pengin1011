using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.Interactions;
using Discord.Net.Rest;
using Discord.WebSocket;
using Pengin1011.Core.Localization;
using Pengin1011.Modules.AIPresetAdmin;
using Pengin1011.Services.AI;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DiscordReplyTests {
	[Fact]
	public async Task PresetShow_LongOutput_DeferredPrivateChunksWithoutMentions() {
		using var rest = new RecordingRestClient();
		using var client = new DiscordSocketClient(new DiscordSocketConfig { RestClientProvider = _ => rest });
		AIPresetStore.ResetForTest();
		Localizer.RegisterModule(typeof(AIPresetAdminLogic).Assembly);
		await client.LoginAsync(TokenType.Bot, "fictional-token", validateToken: false);
		var interaction = CreateInteraction(client);
		var module = new Pengin1011.Modules.AIPresetAdmin.AIPresetAdmin();
		((IInteractionModuleBase)module).SetContext(new SocketInteractionContext(client, interaction));
		var name = new string('x', 5000) + "<@1>";
		var expected = MessageSplitter.Split(AIPresetAdminLogic.Show(name));

		await module.Show(name);

		Assert.True(rest.Deferred);
		Assert.True(rest.DeferredPrivate);
		Assert.Equal(expected, rest.Replies.Select(reply => reply.Content));
		Assert.All(rest.Replies, reply => {
			Assert.True(reply.Private);
			Assert.True(reply.MentionsDisabled);
			Assert.InRange(reply.Content.Length, 1, MessageSplitter.DefaultLimit);
		});
	}

	[Fact]
	public async Task SendDirectAsync_UserResolution_ReceivesCallerCancellation() {
		using var rest = new RecordingRestClient();
		using var client = new DiscordSocketClient(new DiscordSocketConfig { RestClientProvider = _ => rest });
		DiscordGateway.Init(client);
		try {
			await client.LoginAsync(TokenType.Bot, "fictional-token", validateToken: false);
			using var source = new CancellationTokenSource();
			var send = Messages.SendDirectAsync(123UL, "text", source.Token);
			await rest.ResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

			source.Cancel();

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
		} finally {
			DiscordGateway.DetachForTest();
		}
	}

	private static SocketSlashCommand CreateInteraction(DiscordSocketClient client) {
		var constructor = typeof(SocketSlashCommand).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
		var modelType = constructor.GetParameters()[1].ParameterType;
		var model = Activator.CreateInstance(modelType)!;
		var data = Activator.CreateInstance(modelType.Assembly.GetType("Discord.API.ApplicationCommandInteractionData")!)!;
		Set(data, "Id", 1UL);
		Set(data, "Name", "preset");
		Set(data, "Type", ApplicationCommandType.Slash);
		Set(data, "Options", Array.CreateInstance(modelType.Assembly.GetType("Discord.API.ApplicationCommandInteractionDataOption")!, 0));
		Set(model, "Id", SnowflakeUtils.ToSnowflake(DateTimeOffset.UtcNow));
		Set(model, "ApplicationId", 42UL);
		Set(model, "Type", InteractionType.ApplicationCommand);
		Set(model, "Data", data);
		Set(model, "Token", "fictional-interaction-token");
		Set(model, "Version", 1);
		Set(model, "Entitlements", Array.CreateInstance(modelType.GetProperty("Entitlements")!.PropertyType.GetElementType()!, 0));
		Set(model, "IntegrationOwners", new Dictionary<ApplicationIntegrationType, ulong>());
		var userType = typeof(SocketUser).Assembly.GetType("Discord.WebSocket.SocketGlobalUser")!;
		var user = Activator.CreateInstance(userType, BindingFlags.Instance | BindingFlags.NonPublic, null, [client, 9000UL], null)!;
		Set(user, "Username", "owner");
		var interaction = (SocketSlashCommand)constructor.Invoke([client, model, null, user]);
		typeof(SocketSlashCommand).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(interaction, [model]);
		return interaction;
	}

	private static void Set(object model, string name, object value) {
		var property = model.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
		if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(Optional<>)) {
			value = Activator.CreateInstance(property.PropertyType, [value])!;
		}
		property.SetValue(model, value);
	}

	private sealed record Reply(string Content, bool Private, bool MentionsDisabled);

	private sealed class RecordingRestClient : IRestClient {
		private CancellationToken _cancellation;

		public bool Deferred { get; private set; }
		public bool DeferredPrivate { get; private set; }
		public List<Reply> Replies { get; } = [];
		public TaskCompletionSource ResolveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void SetHeader(string key, string value) { }
		public void SetCancelToken(CancellationToken token) => _cancellation = token;
		public void Dispose() { }

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
			if (endpoint.EndsWith("users/123", StringComparison.Ordinal)) {
				ResolveStarted.TrySetResult();
				using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _cancellation);
				await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
			}
			if (endpoint.Contains("interactions/", StringComparison.Ordinal) && endpoint.Contains("/callback", StringComparison.Ordinal)) {
				using var json = JsonDocument.Parse(payload);
				Deferred = json.RootElement.GetProperty("type").GetInt32() == 5;
				DeferredPrivate = json.RootElement.GetProperty("data").GetProperty("flags").GetInt32() == 64;
				return Response("", HttpStatusCode.NoContent);
			}
			if (method == "POST" && endpoint.Contains("webhooks/", StringComparison.Ordinal)) {
				Assert.True(Deferred);
				using var json = JsonDocument.Parse(payload);
				var root = json.RootElement;
				var content = root.GetProperty("content").GetString()!;
				var mentions = root.GetProperty("allowed_mentions");
				var mentionsDisabled = !HasMentionValues(mentions, "parse") && !HasMentionValues(mentions, "users") && !HasMentionValues(mentions, "roles");
				Replies.Add(new Reply(content, root.GetProperty("flags").GetInt32() == 64, mentionsDisabled));
				return Response(JsonSerializer.Serialize(new {
					id = Replies.Count.ToString(), channel_id = "1", webhook_id = "42", content,
					author = new { id = "42", username = "test-bot", discriminator = "0000", bot = true },
					timestamp = DateTimeOffset.UtcNow.ToString("O"), tts = false, mention_everyone = false,
					mentions = Array.Empty<object>(), mention_roles = Array.Empty<string>(), attachments = Array.Empty<object>(),
					embeds = Array.Empty<object>(), pinned = false, type = 0, flags = 64,
				}));
			}
			throw new InvalidOperationException($"Unexpected fake Discord request: {method} {endpoint}");
		}

		private static RestResponse Response(string json, HttpStatusCode status = HttpStatusCode.OK) {
			return new RestResponse(status, [], new MemoryStream(Encoding.UTF8.GetBytes(json)));
		}

		private static bool HasMentionValues(JsonElement mentions, string field) {
			if (!mentions.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return false;
			return value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 0;
		}
	}
}
