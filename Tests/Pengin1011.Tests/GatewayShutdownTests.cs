using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class GatewayShutdownTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task StartupHangs_CancellationThenExit_CompletesAndBacksUp(bool hangDuringStart) {
		var baseDir = Path.Combine(Path.GetTempPath(), $"pengin1011_gateway_exit_{Guid.NewGuid():N}");
		var logPath = Path.Combine(baseDir, "error.log");
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var logoutFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<ExitCoordinator.ExitReport>? exit = null;
		Reset();
		Databases.SetDataDirectoryForTest(baseDir);
		Logger.SetLogPathForTest(logPath);
		ExitCoordinator.GatewayStopBudget = TimeSpan.FromMilliseconds(50);
		try {
			Databases.Init();
			await using (var db = Databases.Open<FakeDbContext>()) {
				db.Items.Add(new FakeItem { Name = "BeforeStartupTimeout" });
				await db.SaveChangesAsync();
			}
			DiscordGateway.Init("test-token");
			await InteractionHost.PublishInitialAsync();
			DiscordGateway.ClientLoginForTest = _ => hangDuringStart ? Task.CompletedTask : gate.Task;
			DiscordGateway.ClientStartForTest = _ => hangDuringStart ? gate.Task : Task.CompletedTask;
			DiscordGateway.ClientStopForTest = static _ => Task.CompletedTask;
			DiscordGateway.ClientLogoutForTest = async _ => {
				await gate.Task;
				logoutFinished.TrySetResult();
			};
			using var cancellation = new CancellationTokenSource();
			var start = DiscordGateway.StartAsync(cancellation.Token);
			Assert.False(start.IsCompleted);
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(3)));

			exit = HostExit.StopAsync();
			var report = await exit.WaitAsync(TimeSpan.FromSeconds(3));

			Assert.False(gate.Task.IsCompleted);
			Assert.False(report.Success);
			Assert.Equal(1, HostExit.CodeFor(false, report));
			Assert.Contains(report.Failures, failure => failure.Contains(L.Get("ExitStepGatewayStop")));
			Assert.Null(InteractionHost.SnapshotForTest);
			Assert.Same(exit, HostExit.StopAsync());
			var log = File.ReadAllText(logPath);
			Assert.Contains(nameof(TimeoutException), log);
			Assert.Contains(L.Get("ExitStepGatewayStop"), log);
			var backup = Assert.Single(Directory.GetFiles(Path.Combine(baseDir, "data", "backup"), "Fake-*.db"));
			await using var snapshot = new RawSqlContext(backup);
			Assert.Equal("BeforeStartupTimeout", await snapshot.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM \"Items\"").SingleAsync());
		} finally {
			gate.TrySetResult();
			if (exit != null) {
				await exit.WaitAsync(TimeSpan.FromSeconds(3));
				await logoutFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
			}
			await Databases.ShutdownAsync();
			Logger.SetLogPathForTest(null);
			Databases.SetDataDirectoryForTest(null);
			Reset();
			SqliteConnection.ClearAllPools();
			Directory.Delete(baseDir, true);
		}
	}

	[Fact]
	public async Task GatewayStopHangs_ExitReturnsFailedAndRunsLaterSteps() {
		var baseDir = Path.Combine(Path.GetTempPath(), $"pengin1011_gateway_stop_{Guid.NewGuid():N}");
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var logoutFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Task<ExitCoordinator.ExitReport>? exit = null;
		Reset();
		Databases.SetDataDirectoryForTest(baseDir);
		ExitCoordinator.GatewayStopBudget = TimeSpan.FromMilliseconds(50);
		try {
			Databases.Init();
			DiscordGateway.Init("test-token");
			await InteractionHost.PublishInitialAsync();
			DiscordGateway.ClientStopForTest = _ => gate.Task;
			DiscordGateway.ClientLogoutForTest = _ => {
				logoutFinished.TrySetResult();
				return Task.CompletedTask;
			};

			exit = HostExit.StopAsync();
			var report = await exit.WaitAsync(TimeSpan.FromSeconds(3));

			Assert.False(gate.Task.IsCompleted);
			Assert.False(report.Success);
			Assert.Equal(1, HostExit.CodeFor(true, report));
			Assert.Contains(report.Failures, failure => failure.Contains(L.Get("ExitStepGatewayStop")));
			Assert.Null(InteractionHost.SnapshotForTest);
			Assert.True((await Databases.ShutdownAsync()).Success);
		} finally {
			gate.TrySetResult();
			if (exit != null) {
				await exit.WaitAsync(TimeSpan.FromSeconds(3));
				await logoutFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
			}
			await Databases.ShutdownAsync();
			Databases.SetDataDirectoryForTest(null);
			Reset();
			Directory.Delete(baseDir, true);
		}
	}

	private static void Reset() {
		ModuleHost.ResetForTest();
		HostExit.ResetForTest();
		ExitCoordinator.ResetForTest();
		InteractionHost.ResetForTest();
		DiscordGateway.DetachForTest();
		DiscordGateway.ResetClientOverridesForTest();
	}
}
