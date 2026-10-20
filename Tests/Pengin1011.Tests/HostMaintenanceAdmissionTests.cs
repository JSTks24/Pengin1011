using System.Diagnostics;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class HostMaintenanceAdmissionTests : IDisposable {
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pengin_admission_" + Guid.NewGuid().ToString("N"));
	private readonly TimeSpan _stopTimeout = ModuleHost.StopTimeout;

	public HostMaintenanceAdmissionTests() {
		Directory.CreateDirectory(_root);
		HostMaintenance.ResetForTest();
		HostExit.ResetForTest();
		ModuleHost.ResetForTest();
		InteractionHost.ResetForTest();
		DiscordGateway.DetachForTest();
		Databases.SetDataDirectoryForTest(_root);
		Databases.Init();
	}

	[Fact]
	public async Task UnsettledExit_RejectsNewMaintenanceWithoutPretendingToAcceptIt() {
		var runtime = new PendingRuntime();
		var run = new LoadedModule {
			Name = "PendingAdmission",
			FilePath = typeof(HostMaintenanceAdmissionTests).Assembly.Location,
			Assembly = typeof(HostMaintenanceAdmissionTests).Assembly,
			LoadedAt = DateTimeOffset.Now,
			Runtime = runtime,
		};
		ModuleHost.Adopt(run);
		var initialization = run.TryBeginInit(() => runtime.InitializeAsync(run.Lifecycle.Token))!;
		ModuleHost.StopTimeout = TimeSpan.FromMilliseconds(50);
		try {
			await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var report = await HostExit.StopAsync();
			Assert.False(report.ResourcesSettled);
			Assert.Equal(L.Get("MaintenanceUnavailable"), await DatabaseCommands.ExecuteAsync(["db", "unlock", "Absent", "--confirm"]));
			Assert.False(HostMaintenance.Pending.IsCompleted);
			Assert.False(HostMaintenance.Busy);
			Assert.False(DataDirectoryOwnership.TryAcquire(_root, out _, out _));

			runtime.Release.TrySetResult();
			await initialization;
			ModuleHost.StopTimeout = TimeSpan.FromSeconds(5);
			Assert.True((await ModuleHost.StopAsync(run)).Clean);
			Assert.Same(report, await HostExit.StopAsync());
			Assert.Equal(L.Get("MaintenanceUnavailable"), await DatabaseCommands.ExecuteAsync(["db", "unlock", "Absent", "--confirm"]));
		} finally {
			runtime.Release.TrySetResult();
			await initialization;
			ModuleHost.StopTimeout = TimeSpan.FromSeconds(5);
			await ModuleHost.StopAsync(run);
		}
	}

	[Fact]
	public async Task ExecuteBeforeRequest_DoesNotBlockRequestAdmission() {
		using var process = Process.Start(TestChildProcess.StartInfo("maintenance-admission-probe", Path.Combine(_root, "child")))!;
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();
		try {
			await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			Assert.True(process.ExitCode == 0, await output + await error);
		} finally {
			if (!process.HasExited) {
				process.Kill();
				await process.WaitForExitAsync();
			}
			await output;
			await error;
		}
	}

	[Fact]
	public async Task QueuedMaintenance_RejectsPausedAccessBeforeInspectingItsTarget() {
		await Databases.BackupGate.WaitAsync();
		Task<string> pending;
		DataDirectoryOwnership? replacement = null;
		try {
			pending = HostMaintenance.RequestAsync(new DatabaseCommand("restore", "Fake", "missing", true, false, null));
			Assert.False(pending.IsCompleted);
			Databases.ReleaseOwnership();
			Assert.True(DataDirectoryOwnership.TryAcquire(_root, out replacement, out var failure), failure);
			HostMaintenance.DataAccessPaused = true;
		} finally {
			Databases.BackupGate.Release();
		}
		try {
			Assert.Equal(L.Get("MaintenanceBusy"), await pending.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.False(HostMaintenance.Pending.IsCompleted);
			Assert.False(HostMaintenance.Busy);
			Assert.False(DataDirectoryOwnership.TryAcquire(_root, out _, out _));
		} finally {
			replacement?.Dispose();
		}
	}

	[Fact]
	public async Task QueuedBackup_RejectsPausedAccessWithoutReacquiringOwnership() {
		await Databases.BackupGate.WaitAsync();
		Task<BackupResult> pending;
		DataDirectoryOwnership? replacement = null;
		try {
			pending = Databases.BackupNowAsync();
			Assert.False(pending.IsCompleted);
			Databases.ReleaseOwnership();
			Assert.True(DataDirectoryOwnership.TryAcquire(_root, out replacement, out var failure), failure);
			HostMaintenance.DataAccessPaused = true;
		} finally {
			Databases.BackupGate.Release();
		}
		try {
			var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(result.Success);
			Assert.Equal(L.Get("MaintenanceBusy"), Assert.Single(result.Failures).Reason);
			Assert.False(DataDirectoryOwnership.TryAcquire(_root, out _, out _));
		} finally {
			replacement?.Dispose();
		}
	}

	internal static async Task<int> RunProbeAsync(string directory) {
		Directory.CreateDirectory(directory);
		Databases.ConfigureDataDirectory(directory);
		Databases.Init();
		try {
			var execution = HostMaintenance.ExecutePendingAsync(() => Task.FromResult(new ExitCoordinator.ExitReport(true, [], true)), () => false);
			var response = await DatabaseCommands.ExecuteAsync(["db", "unlock", "Absent", "--confirm"]);
			if (response != L.Get("MaintenanceAccepted")) return 3;
			if (await execution) return 4;
			return HostMaintenance.Pending.IsCompleted || HostMaintenance.Busy ? 5 : 0;
		} finally {
			await Databases.ShutdownAsync();
			Databases.ReleaseOwnership();
		}
	}

	public void Dispose() {
		ModuleHost.StopTimeout = _stopTimeout;
		HostExit.ResetForTest();
		HostMaintenance.ResetForTest();
		ModuleHost.ResetForTest();
		InteractionHost.ResetForTest();
		Databases.SetDataDirectoryForTest(null);
		Directory.Delete(_root, true);
	}

	private sealed class PendingRuntime : IModuleRuntime {
		internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task InitializeAsync(CancellationToken ct) {
			Entered.TrySetResult();
			await Release.Task;
		}

		public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
	}
}
