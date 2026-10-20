using System.Diagnostics;
using Discord;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Services.AI;
using Pengin1011.Services.Discord;

namespace Pengin1011;

internal static class Program {
	public static async Task<int> Main(string[] args) {
		Console.OutputEncoding = System.Text.Encoding.UTF8;
		HostMaintenance.MaintenanceOnly = true;
		if (!TryParseArguments(args, out var dataDirectory, out var createDirectory)) {
			Logger.Error(typeof(Program), Loc.Get("StartupArgumentsInvalid"));
			return 2;
		}
		using var cancellation = new CancellationTokenSource();
		var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var shutdownCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void RequestStop() {
			try {
				cancellation.Cancel();
			} catch (Exception e) {
				Logger.Error(typeof(Program), e, Loc.Get("CancelActiveWorkFailed"));
			}
			exit.TrySetResult();
		}
		using var signals = new ShutdownSignals(RequestStop, shutdownCompleted.Task);
		var started = false;
		try {
			await using var handoff = await RestartHandoff.ConnectAsync(RequestStop, cancellation.Token);
			try {
				var configValid = AppConfig.TryLoad(null, out _);
				if (!AppConfig.PathConfigurationValid && dataDirectory == null) throw new InvalidDataException(Loc.Get("ConfigPathsUnavailable"));
				if (!Databases.TryResolveDataDirectory(new DatabasePathOptions(dataDirectory, AppConfig.Databases.DataDirectory, createDirectory), out var resolved, out var failure)) {
					throw new DirectoryNotFoundException(Loc.Format("DataDirectoryMissing", failure));
				}
				Databases.ConfigureDataDirectory(resolved);
				Databases.Init();
				Logger.Info(typeof(Program), Loc.Format("DataDirectoryResolved", resolved, Environment.ProcessId));
				if (configValid && AIPresetStore.TryLoad(null, out _)) {
					await StartBusinessAsync(cancellation.Token);
					started = true;
					HostMaintenance.MaintenanceOnly = false;
					Logger.Info(typeof(Program), Loc.Get("StartedHint"));
				}
			} catch (Exception e) {
				Logger.Error(typeof(Program), e, Loc.Get("StartupFailed"));
			}
			if (handoff != null && !await handoff.CompleteStartupAsync(started, cancellation.Token)) return 1;
			if (!started) {
				await HostExit.StopAsync();
				Logger.Info(typeof(Program), Loc.Get("MaintenanceConsole"));
			}
			CliDispatcher.StartedAt = DateTimeOffset.Now;
			var input = CliLoop.RunAsync(RequestStop, cancellation.Token, () => false);
			while (!exit.Task.IsCompleted) {
				var request = HostMaintenance.Pending;
				if (!started || !input.IsCompleted) await Task.WhenAny(exit.Task, request, input);
				else await Task.WhenAny(exit.Task, request);
				if (request.IsCompletedSuccessfully) {
					if (await HostMaintenance.ExecutePendingWithRestartAsync(HostExit.StopAsync, () => RestartHandoff.RunAsync(RestartStartInfo(), cancellation.Token))) return 0;
					started = false;
					Logger.Info(typeof(Program), Loc.Get("MaintenanceConsole"));
				} else if (!started && input.IsCompleted) {
					break;
				}
			}
			var report = await HostExit.StopAsync();
			return HostExit.CodeFor(started, report);
		} catch (Exception e) {
			Logger.Error(typeof(Program), e, Loc.Get("StartupFailed"));
			return 1;
		} finally {
			RequestStop();
			try {
				await HostExit.StopAsync();
			} finally {
				shutdownCompleted.TrySetResult();
			}
		}
	}

	private static async Task StartBusinessAsync(CancellationToken ct) {
		AIClient.Init(AIPresetStore.MaxParallel);
		AIPresetUsageRegistry.Register(typeof(AIClient).Assembly);
		DiscordGateway.Init(AppConfig.Discord.Token);
		Components.Attach();
		LoggerChannelForwarder.Start(AppConfig.Logging.Channels);
		DiscordGateway.SubscribeInteraction(null, InteractionHost.ExecuteAsync);
		ModuleHost.LoadAll();
		if (!AIPresetValidator.TryValidate(out _)) throw new InvalidOperationException(Loc.Get("StartupFailed"));
		await InteractionHost.PublishInitialAsync();
		using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
		startup.CancelAfter(TimeSpan.FromSeconds(45));
		if (!await DiscordGateway.StartAsync(startup.Token) || !await DiscordGateway.WaitReadyAsync(ct: startup.Token)) {
			throw new InvalidOperationException(Loc.Get("StartupFailed"));
		}
	}

	internal static bool TryParseArguments(IReadOnlyList<string> args, out string? directory, out bool create) {
		directory = null;
		create = false;
		for (var index = 0; index < args.Count; index++) {
			if (args[index] == "--data-dir" && directory == null && index + 1 < args.Count && !string.IsNullOrWhiteSpace(args[index + 1]) && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) {
				directory = args[++index];
			} else if (args[index] == "--create-data-dir" && !create) {
				create = true;
			} else {
				return false;
			}
		}
		return true;
	}

	internal static ProcessStartInfo RestartStartInfo() {
		var executable = Environment.ProcessPath ?? throw new InvalidOperationException(Loc.Get("RestartFailed"));
		var info = new ProcessStartInfo(executable) {
			UseShellExecute = false,
			WorkingDirectory = AppContext.BaseDirectory
		};
		if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)) {
			info.ArgumentList.Add(typeof(Program).Assembly.Location);
		}
		info.ArgumentList.Add("--data-dir");
		info.ArgumentList.Add(Databases.DataDirectory);
		return info;
	}

}
