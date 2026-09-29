using System.Runtime.ExceptionServices;
using Discord;
using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;

namespace Pengin1011.Services.Discord;

public static class DiscordGateway {
	public const int DefaultWorkCapacity = 128;

	public static GatewayIntents RequiredIntents { get; } = GatewayIntents.Guilds | GatewayIntents.GuildMembers | GatewayIntents.GuildMessages | GatewayIntents.MessageContent;

	private static DiscordSocketClient? _client;
	private static string _token = "";
	private static WorkTracker? _dispatcher;
	private static int _rejectedMessages;

	private static readonly object WireGate = new();
	private static readonly List<HandlerEntry<SocketMessage>> MessageHandlers = [];
	private static readonly List<HandlerEntry<object?>> ReadyHandlers = [];
	private static readonly List<HandlerEntry<SocketGuild>> GuildAvailableHandlers = [];
	private static readonly List<HandlerEntry<SocketGuild>> GuildJoinedHandlers = [];
	private static readonly List<HandlerEntry<SocketInteraction>> InteractionHandlers = [];
	private static readonly List<HandlerEntry<SocketMessageComponent>> ButtonHandlers = [];
	private static readonly List<HandlerEntry<SocketMessageComponent>> SelectMenuHandlers = [];
	private static readonly List<HandlerEntry<SocketModal>> ModalHandlers = [];

	public static DiscordSocketClient? Client => _client;
	public static ConnectionState ConnectionState => _client?.ConnectionState ?? ConnectionState.Disconnected;
	public static IUser? CurrentUser => _client?.CurrentUser;
	public static WorkTracker? Dispatcher => _dispatcher;

	public static SocketGuild? GetGuild(ulong guildId) {
		return _client?.GetGuild(guildId);
	}

	public static IDisposable SubscribeMessage(LoadedModule? owner, Func<SocketMessage, CancellationToken, Task> handler) {
		return Subscribe(MessageHandlers, owner, handler);
	}

	public static IDisposable SubscribeReady(LoadedModule? owner, Func<CancellationToken, Task> handler) {
		return Subscribe(ReadyHandlers, owner, (object? _, CancellationToken ct) => handler(ct));
	}

	public static IDisposable SubscribeGuildAvailable(LoadedModule? owner, Func<SocketGuild, CancellationToken, Task> handler) {
		return Subscribe(GuildAvailableHandlers, owner, handler);
	}

	public static IDisposable SubscribeGuildJoined(LoadedModule? owner, Func<SocketGuild, CancellationToken, Task> handler) {
		return Subscribe(GuildJoinedHandlers, owner, handler);
	}

	public static IDisposable SubscribeInteraction(LoadedModule? owner, Func<SocketInteraction, CancellationToken, Task> handler) {
		return Subscribe(InteractionHandlers, owner, handler);
	}

	public static IDisposable SubscribeButton(LoadedModule? owner, Func<SocketMessageComponent, CancellationToken, Task> handler) {
		return Subscribe(ButtonHandlers, owner, handler);
	}

	public static IDisposable SubscribeSelectMenu(LoadedModule? owner, Func<SocketMessageComponent, CancellationToken, Task> handler) {
		return Subscribe(SelectMenuHandlers, owner, handler);
	}

	public static IDisposable SubscribeModal(LoadedModule? owner, Func<SocketModal, CancellationToken, Task> handler) {
		return Subscribe(ModalHandlers, owner, handler);
	}

	public static void RemoveModuleHandlers(LoadedModule run) {
		lock (WireGate) {
			MessageHandlers.RemoveAll(entry => entry.Owner == run);
			ReadyHandlers.RemoveAll(entry => entry.Owner == run);
			GuildAvailableHandlers.RemoveAll(entry => entry.Owner == run);
			GuildJoinedHandlers.RemoveAll(entry => entry.Owner == run);
			InteractionHandlers.RemoveAll(entry => entry.Owner == run);
			ButtonHandlers.RemoveAll(entry => entry.Owner == run);
			SelectMenuHandlers.RemoveAll(entry => entry.Owner == run);
			ModalHandlers.RemoveAll(entry => entry.Owner == run);
		}
	}

	public static void OpenAccepting() {
		_dispatcher?.Open();
	}

	public static void RequestExitClose() {
		_dispatcher?.RequestExitClose();
	}

	public static Task<bool> DrainWorkAsync(TimeSpan timeout, CancellationToken ct = default) {
		return _dispatcher == null ? Task.FromResult(true) : _dispatcher.DrainAsync(timeout, ct);
	}

	public static void CancelActiveWork() {
		_dispatcher?.CancelActive();
	}

	public static void Init(string token) {
		_token = token ?? "";
		Attach(new DiscordSocketClient(new DiscordSocketConfig {
			GatewayIntents = RequiredIntents,
		}));
	}

	public static void Init(DiscordSocketClient client) {
		_token = "";
		Attach(client);
	}

	private static void Attach(DiscordSocketClient client) {
		Detach();
		_client = client;
		_dispatcher = new WorkTracker(DefaultWorkCapacity);
		_client.Log += OnClientLog;
		_client.MessageReceived += OnMessageReceived;
		_client.Ready += OnHostReadyDirect;
		_client.Ready += OnReady;
		_client.GuildAvailable += OnGuildAvailable;
		_client.JoinedGuild += OnGuildJoined;
		_client.InteractionCreated += OnInteractionCreated;
		_client.ButtonExecuted += OnButtonExecuted;
		_client.SelectMenuExecuted += OnSelectMenuExecuted;
		_client.ModalSubmitted += OnModalSubmitted;
	}

	private static void Detach() {
		var client = _client;
		if (client != null) {
			client.Log -= OnClientLog;
			client.MessageReceived -= OnMessageReceived;
			client.Ready -= OnHostReadyDirect;
			client.Ready -= OnReady;
			client.GuildAvailable -= OnGuildAvailable;
			client.JoinedGuild -= OnGuildJoined;
			client.InteractionCreated -= OnInteractionCreated;
			client.ButtonExecuted -= OnButtonExecuted;
			client.SelectMenuExecuted -= OnSelectMenuExecuted;
			client.ModalSubmitted -= OnModalSubmitted;
		}
		_client = null;
		_dispatcher = null;
		lock (WireGate) {
			MessageHandlers.Clear();
			ReadyHandlers.Clear();
			GuildAvailableHandlers.Clear();
			GuildJoinedHandlers.Clear();
			InteractionHandlers.Clear();
			ButtonHandlers.Clear();
			SelectMenuHandlers.Clear();
			ModalHandlers.Clear();
		}
	}

	public static async Task<bool> StartAsync() {
		var client = _client;
		if (client == null || _token.Length == 0) return false;
		try {
			await client.LoginAsync(TokenType.Bot, _token);
			await client.StartAsync();
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(DiscordGateway), e, "Gateway 连接失败");
			return false;
		}
	}

	public static async Task<bool> WaitReadyAsync(int timeoutMs = 30_000, CancellationToken ct = default) {
		var client = _client;
		if (client == null) return false;
		if (client.CurrentUser != null && client.ConnectionState == ConnectionState.Connected) return true;

		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		Task OnClientReady() {
			completion.TrySetResult(true);
			return Task.CompletedTask;
		}

		client.Ready += OnClientReady;
		try {
			var delay = Task.Delay(timeoutMs, ct);
			var finished = await Task.WhenAny(completion.Task, delay);
			return finished == completion.Task;
		} finally {
			client.Ready -= OnClientReady;
		}
	}

	public static async Task StopAsync() {
		var client = _client;
		if (client == null) return;
		var stop = ClientStopForTest ?? (target => target.StopAsync());
		var logout = ClientLogoutForTest ?? (target => target.LogoutAsync());
		Exception? stopFailure = null;
		try {
			await stop(client);
		} catch (Exception e) {
			stopFailure = e;
		}
		try {
			await logout(client);
		} catch (Exception logoutFailure) {
			if (stopFailure == null) throw;
			throw new AggregateException("网关停止与退出登录均失败", stopFailure, logoutFailure);
		}
		if (stopFailure != null) {
			ExceptionDispatchInfo.Capture(stopFailure).Throw();
		}
	}

	private static Task OnHostReadyDirect() {
		InteractionHost.OnGatewayReady();
		return Task.CompletedTask;
	}

	private static Task OnClientLog(LogMessage message) {
		var text = $"Discord 客户端日志 {message.Severity}: {message.Message ?? "(无消息)"}";
		if (message.Exception != null) {
			Logger.Error(typeof(DiscordGateway), message.Exception, text);
		} else if (message.Severity is LogSeverity.Error or LogSeverity.Critical) {
			Logger.Error(typeof(DiscordGateway), text);
		} else {
			Logger.Info(typeof(DiscordGateway), text);
		}
		return Task.CompletedTask;
	}

	internal static Func<DiscordSocketClient, Task>? ClientStopForTest;
	internal static Func<DiscordSocketClient, Task>? ClientLogoutForTest;

	internal static void ResetClientOverridesForTest() {
		ClientStopForTest = null;
		ClientLogoutForTest = null;
	}

	internal static void DetachForTest() {
		Detach();
	}

	internal static Task DispatchReadyForTest() {
		return DispatchReady(ReadyHandlers);
	}

	internal static Task DispatchMessageForTest(SocketMessage? message) {
		return Dispatch(MessageHandlers, message!, null, "消息");
	}

	internal static Task DispatchInteractionForTest(SocketInteraction? interaction) {
		return Dispatch(InteractionHandlers, interaction!, interaction, "交互");
	}

	internal static void DispatchInteractionRejectionForTest(WorkRejectReason reason, IDiscordInteraction interaction) {
		HandleRejection("交互", interaction, reason);
	}

	internal static int RejectedMessageCountForTest => _rejectedMessages;

	private static Task OnMessageReceived(SocketMessage message) {
		return Dispatch(MessageHandlers, message, null, "消息");
	}

	private static Task OnReady() {
		return DispatchReady(ReadyHandlers);
	}

	private static Task OnGuildAvailable(SocketGuild guild) {
		return Dispatch(GuildAvailableHandlers, guild, null, "服务器可用");
	}

	private static Task OnGuildJoined(SocketGuild guild) {
		return Dispatch(GuildJoinedHandlers, guild, null, "加入服务器");
	}

	private static Task OnInteractionCreated(SocketInteraction interaction) {
		return Dispatch(InteractionHandlers, interaction, interaction, "交互");
	}

	private static Task OnButtonExecuted(SocketMessageComponent component) {
		return Dispatch(ButtonHandlers, component, component, "按钮");
	}

	private static Task OnSelectMenuExecuted(SocketMessageComponent component) {
		return Dispatch(SelectMenuHandlers, component, component, "下拉");
	}

	private static Task OnModalSubmitted(SocketModal modal) {
		return Dispatch(ModalHandlers, modal, modal, "Modal");
	}

	private static Task Dispatch<T>(List<HandlerEntry<T>> handlers, T argument, IDiscordInteraction? interaction, string kind) {
		TrackedHandler[] batch;
		lock (WireGate) {
			if (handlers.Count == 0) return Task.CompletedTask;
			batch = new TrackedHandler[handlers.Count];
			for (var i = 0; i < handlers.Count; i++) {
				var entry = handlers[i];
				batch[i] = new TrackedHandler(entry.Owner, ct => entry.Handler(argument, ct));
			}
		}
		return SubmitBatch(batch, interaction, kind);
	}

	private static Task DispatchReady(List<HandlerEntry<object?>> handlers) {
		TrackedHandler[] batch;
		lock (WireGate) {
			if (handlers.Count == 0) return Task.CompletedTask;
			batch = new TrackedHandler[handlers.Count];
			for (var i = 0; i < handlers.Count; i++) {
				var entry = handlers[i];
				batch[i] = new TrackedHandler(entry.Owner, ct => entry.Handler(null, ct));
			}
		}
		return SubmitBatch(batch, null, "Ready");
	}

	private static Task SubmitBatch(TrackedHandler[] batch, IDiscordInteraction? interaction, string kind) {
		var dispatcher = _dispatcher;
		if (dispatcher == null) return Task.CompletedTask;
		var item = dispatcher.TryBegin(batch, out var reason);
		if (item == null) {
			HandleRejection(kind, interaction, reason);
		}
		return Task.CompletedTask;
	}

	private static void HandleRejection(string kind, IDiscordInteraction? interaction, WorkRejectReason reason) {
		if (interaction != null) {
			_ = RespondRejectionAsync(interaction, reason);
			return;
		}
		var count = Interlocked.Increment(ref _rejectedMessages);
		Logger.Info(typeof(DiscordGateway), $"接单拒绝（{reason}）：{kind}，累计 {count}");
	}

	private static async Task RespondRejectionAsync(IDiscordInteraction interaction, WorkRejectReason reason) {
		var text = reason switch {
			WorkRejectReason.Paused => "服务尚未就绪，请稍后重试",
			WorkRejectReason.Exiting => "框架正在退出，请稍后",
			_ => "服务忙碌，请稍后重试",
		};
		try {
			if (!interaction.HasResponded) {
				await interaction.RespondAsync(text, ephemeral: true);
			}
		} catch (Exception e) {
			Logger.Error(typeof(DiscordGateway), e, "拒绝接单提示发送失败");
		}
	}

	private static IDisposable Subscribe<T>(List<HandlerEntry<T>> handlers, LoadedModule? owner, Func<T, CancellationToken, Task> handler) {
		var entry = new HandlerEntry<T>(owner, handler);
		lock (WireGate) {
			handlers.Add(entry);
		}
		return new Subscription(() => {
			lock (WireGate) {
				handlers.Remove(entry);
			}
		});
	}

	private sealed class Subscription(Action dispose) : IDisposable {
		private Action? _dispose = dispose;

		public void Dispose() {
			var action = _dispose;
			_dispose = null;
			action?.Invoke();
		}
	}

	private sealed class HandlerEntry<T>(LoadedModule? owner, Func<T, CancellationToken, Task> handler) {
		public LoadedModule? Owner { get; } = owner;
		public Func<T, CancellationToken, Task> Handler { get; } = handler;
	}
}
