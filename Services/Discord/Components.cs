using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Pengin1011.Core;
using Pengin1011.Core.Localization;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;

namespace Pengin1011.Services.Discord;

public static class Components {
	public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(180);
	internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

	private static readonly object RegistryGate = new();
	private static readonly Dictionary<string, Entry> Entries = [];
	private static readonly List<IDisposable> _handles = [];
	private static bool _attached;
	private static TimeProvider _time = TimeProvider.System;
	private static CancellationTokenSource? _sweepCts;
	private static Task? _sweepTask;

	public static void Attach() {
		if (!_attached) {
			_attached = true;
			_handles.Add(DiscordGateway.SubscribeButton(null, (component, ct) => RouteAsync(component, ct)));
			_handles.Add(DiscordGateway.SubscribeSelectMenu(null, (component, ct) => RouteAsync(component, ct)));
			_handles.Add(DiscordGateway.SubscribeModal(null, (modal, ct) => RouteAsync(modal, ct)));
		}
		EnsureSweeper();
	}

	public static void Detach() {
		if (!_attached) return;
		_attached = false;
		foreach (var handle in _handles) {
			handle.Dispose();
		}
		_handles.Clear();
	}

	public static async Task ShutdownAsync() {
		Task? task;
		CancellationTokenSource? cts;
		lock (RegistryGate) {
			task = _sweepTask;
			cts = _sweepCts;
			_sweepTask = null;
			_sweepCts = null;
		}
		if (cts == null || task == null) return;
		cts.Cancel();
		try {
			await task;
		} catch (OperationCanceledException) {
		}
	}

	public static void Register(string customId, Func<SocketInteraction, CancellationToken, Task> handler, TimeSpan? ttl = null) {
		Register(null, customId, handler, ttl);
	}

	public static void Register(LoadedModule? owner, string customId, Func<SocketInteraction, CancellationToken, Task> handler, TimeSpan? ttl = null) {
		if (string.IsNullOrEmpty(customId)) throw new ArgumentException(Localizer.Get("CustomIdRequired"), nameof(customId));
		ArgumentNullException.ThrowIfNull(handler);
		RegisterEntry(owner, customId, null, handler, ttl);
	}

	public static void Register<TState>(string customId, TState state, TimeSpan? ttl, Func<TState, SocketInteraction, CancellationToken, Task> handler) {
		Register(null, customId, state, ttl, handler);
	}

	public static void Register<TState>(LoadedModule? owner, string customId, TState state, TimeSpan? ttl, Func<TState, SocketInteraction, CancellationToken, Task> handler) {
		if (string.IsNullOrEmpty(customId)) throw new ArgumentException(Localizer.Get("CustomIdRequired"), nameof(customId));
		ArgumentNullException.ThrowIfNull(handler);
		RegisterEntry(owner, customId, state, (interaction, ct) => handler(state, interaction, ct), ttl);
	}

	public static bool Unregister(string customId) {
		lock (RegistryGate) {
			return Entries.Remove(customId);
		}
	}

	public static void Clear() {
		lock (RegistryGate) {
			Entries.Clear();
		}
	}

	public static void RemoveModule(LoadedModule run) {
		lock (RegistryGate) {
			foreach (var key in Entries.Where(pair => pair.Value.Owner == run).Select(pair => pair.Key).ToList()) {
				Entries.Remove(key);
			}
		}
	}

	public static bool TryGetState<TState>(string customId, out TState? state) {
		state = default;
		lock (RegistryGate) {
			if (!Entries.TryGetValue(customId, out var entry)) return false;
			if (IsExpired(entry)) {
				Entries.Remove(customId);
				return false;
			}
			if (entry.State is not TState matched) return false;
			state = matched;
			return true;
		}
	}

	public static Task RouteAsync(SocketInteraction? interaction, CancellationToken ct = default) {
		return RouteByIdAsync(CustomIdOf(interaction), interaction, ct);
	}

	public static async Task RouteByIdAsync(string? customId, SocketInteraction? interaction, CancellationToken ct = default) {
		if (string.IsNullOrEmpty(customId)) return;
		Entry? entry;
		lock (RegistryGate) {
			if (!Entries.TryGetValue(customId, out var found)) {
				entry = null;
			} else if (IsExpired(found)) {
				Entries.Remove(customId);
				entry = null;
			} else {
				entry = found;
			}
		}
		if (entry == null) {
			await RespondExpiredAsync(interaction);
			return;
		}
		var owner = entry.Owner;
		if (owner != null && owner.State != ModuleState.Ready) {
			_ = RespondUnavailableAsync(interaction);
			return;
		}
		lock (RegistryGate) {
			if (Entries.TryGetValue(customId, out var current) && ReferenceEquals(current, entry) && current.ExpiresAt != null) {
				current.ExpiresAt = _time.GetUtcNow() + current.Ttl;
			}
		}
		using var linked = owner == null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, owner.Lifecycle.Token);
		await entry.Handler(interaction!, linked?.Token ?? ct);
	}

	public static async Task<bool> DisableMessageComponentsAsync(IMessage? message, CancellationToken ct = default) {
		if (message is not IUserMessage userMessage || message.Components.Count == 0) return false;
		try {
			var builder = ComponentBuilder.FromMessage(message);
			foreach (var row in builder.ActionRows) {
				foreach (var component in row.Components) {
					switch (component) {
						case ButtonBuilder button:
							button.IsDisabled = true;
							break;
						case SelectMenuBuilder select:
							select.IsDisabled = true;
							break;
					}
				}
			}
			await userMessage.ModifyAsync(properties => properties.Components = builder.Build());
			return true;
		} catch (Exception e) {
			Logger.Error(typeof(Components), e, Localizer.Format("DisableComponentsFailed", message.Id));
			return false;
		}
	}

	internal static IReadOnlyList<string> SweepExpired() {
		lock (RegistryGate) {
			var now = _time.GetUtcNow();
			var removed = new List<string>();
			foreach (var pair in Entries) {
				if (pair.Value.ExpiresAt == null || now < pair.Value.ExpiresAt) continue;
				removed.Add(pair.Key);
			}
			foreach (var key in removed) {
				Entries.Remove(key);
			}
			return removed;
		}
	}

	internal static void SetTimeProviderForTest(TimeProvider? provider) {
		lock (RegistryGate) {
			_time = provider ?? TimeProvider.System;
		}
	}

	internal static bool SweeperRunningForTest {
		get {
			lock (RegistryGate) {
				return _sweepTask != null;
			}
		}
	}

	private static void RegisterEntry(LoadedModule? owner, string customId, object? state, Func<SocketInteraction, CancellationToken, Task> handler, TimeSpan? ttl) {
		var lifetime = ttl ?? DefaultTtl;
		Entry entry;
		if (lifetime <= TimeSpan.Zero) {
			entry = new Entry(owner, state, handler, null, null);
		} else {
			entry = new Entry(owner, state, handler, lifetime, _time.GetUtcNow() + lifetime);
		}
		lock (RegistryGate) {
			Entries[customId] = entry;
		}
	}

	private static void EnsureSweeper() {
		lock (RegistryGate) {
			if (_sweepTask != null) return;
			var cts = new CancellationTokenSource();
			_sweepCts = cts;
			_sweepTask = Task.Run(() => SweepLoopAsync(cts.Token));
		}
	}

	private static async Task SweepLoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			try {
				await Task.Delay(SweepInterval, ct);
			} catch (OperationCanceledException) {
				break;
			}
			SweepExpired();
		}
	}

	private static async Task RespondExpiredAsync(SocketInteraction? interaction) {
		if (interaction == null || interaction.HasResponded) return;
		try {
			await interaction.RespondAsync(Localizer.Get("InteractionExpired"), ephemeral: true);
		} catch (Exception e) {
			Logger.Error(typeof(Components), e, Localizer.Get("ExpiredNoticeSendFailed"));
		}
	}

	internal static async Task RespondUnavailableAsync(IDiscordInteraction? interaction) {
		if (interaction == null || interaction.HasResponded) return;
		try {
			await interaction.RespondAsync(Localizer.Get("ModuleUnavailable"), ephemeral: true);
		} catch (Exception e) {
			Logger.Error(typeof(Components), e, Localizer.Get("ModuleUnavailableNoticeSendFailed"));
		}
	}

	private static bool IsExpired(Entry entry) {
		return entry.ExpiresAt != null && _time.GetUtcNow() >= entry.ExpiresAt;
	}

	private static string? CustomIdOf(SocketInteraction? interaction) {
		return interaction switch {
			SocketMessageComponent component => component.Data.CustomId,
			SocketModal modal => modal.Data.CustomId,
			_ => null,
		};
	}

	private sealed class Entry(LoadedModule? owner, object? state, Func<SocketInteraction, CancellationToken, Task> handler, TimeSpan? ttl, DateTimeOffset? expiresAt) {
		public LoadedModule? Owner { get; } = owner;
		public object? State { get; } = state;
		public Func<SocketInteraction, CancellationToken, Task> Handler { get; } = handler;
		public TimeSpan? Ttl { get; } = ttl;
		public DateTimeOffset? ExpiresAt { get; set; } = expiresAt;
	}
}
