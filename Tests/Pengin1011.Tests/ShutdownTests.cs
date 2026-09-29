using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;
using Pengin1011.Modules.FakeModule;
using Pengin1011.Services.Discord;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ShutdownTests {
	private static string TempBase() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_shutdown_{Guid.NewGuid():N}");
	}

	private static void Cleanup(string baseDir) {
		try {
			Directory.Delete(baseDir, true);
		} catch (IOException) {
			Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
			Directory.Delete(baseDir, true);
		}
	}

	private class ShutdownItem {
		public long Id { get; set; }

		public string Name { get; set; } = "";
	}

	private class ShutdownDbContext : DbContext {
		protected override void OnConfiguring(DbContextOptionsBuilder options) {
			Databases.Configure(GetType(), options);
		}

		public DbSet<ShutdownItem> Items => Set<ShutdownItem>();
	}

	[DbContext(typeof(ShutdownDbContext))]
	[Migration("20260926000001_Init")]
	private sealed class ShutdownInitMigration : Migration {
		protected override void Up(MigrationBuilder migrationBuilder) {
			migrationBuilder.CreateTable(
				name: "Items",
				columns: table => new {
					Id = table.Column<long>(type: "INTEGER", nullable: false)
						.Annotation("Sqlite:Autoincrement", true),
					Name = table.Column<string>(type: "TEXT", nullable: false)
				},
				constraints: table => {
					table.PrimaryKey("PK_Items", x => x.Id);
				});
		}

		protected override void Down(MigrationBuilder migrationBuilder) {
			migrationBuilder.DropTable(name: "Items");
		}
	}

	private sealed class WritingRuntime(TaskCompletionSource writtenSignal, Task releaseSignal) : IModuleRuntime {
		public Task InitializeAsync(CancellationToken ct) {
			return Task.CompletedTask;
		}

		public async Task StopAsync(CancellationToken ct) {
			await using (var db = Databases.Open<ShutdownDbContext>()) {
				db.Items.Add(new ShutdownItem { Name = "清理期最后写入" });
				await db.SaveChangesAsync(ct);
			}
			writtenSignal.TrySetResult();
			await releaseSignal;
		}
	}

	private static LoadedModule MakeRun(string name, IModuleRuntime runtime) {
		var assembly = typeof(ShutdownTests).Assembly;
		return new LoadedModule {
			Name = name,
			FilePath = "",
			Assembly = assembly,
			LoadedAt = DateTimeOffset.Now,
			Runtime = runtime,
		};
	}

	private sealed class ResourceOwningRuntime : IModuleRuntime {
		public static MemoryStream? Resource;
		public static int StopCount;
		public static List<string> Events = [];

		public static void Reset() {
			Resource = null;
			StopCount = 0;
			Events.Clear();
		}

		public Task InitializeAsync(CancellationToken ct) {
			Resource = new MemoryStream();
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken ct) {
			Interlocked.Increment(ref StopCount);
			Events.Add("module-dispose");
			Resource?.Dispose();
			Resource = null;
			return Task.CompletedTask;
		}
	}

	private static void ResetGlobalState() {
		ModuleHostTests.ResetModules();
		HostExit.ResetForTest();
		ExitCoordinator.ResetForTest();
		DiscordGateway.ResetClientOverridesForTest();
		InteractionHost.ResetForTest();
		ModuleHost.StopTimeout = TimeSpan.FromSeconds(10);
		ModuleHost.CleanupBudget = TimeSpan.FromSeconds(10);
	}

	private static async Task<InteractionService> BootAsync() {
		ResetGlobalState();
		DiscordGateway.Init(new DiscordSocketClient());
		ModuleHost.LoadAll();
		await InteractionHost.PublishInitialAsync();
		foreach (var run in ModuleHost.Modules) {
			if (run.InitTask == null) continue;
			try {
				await run.InitTask.WaitAsync(TimeSpan.FromSeconds(2));
			} catch (TimeoutException) {
			} catch (Exception) {
			}
		}
		return InteractionHost.Current.Service;
	}

	private static async Task TeardownAsync() {
		foreach (var run in ModuleHost.Modules) {
			await ModuleHost.StopAsync(run);
		}
		ResetGlobalState();
		DiscordGateway.DetachForTest();
	}

	private static async Task<LoadedModule> BootResourceModuleAsync() {
		ResetGlobalState();
		ResourceOwningRuntime.Reset();
		DiscordGateway.Init(new DiscordSocketClient());
		DiscordGateway.OpenAccepting();
		var assembly = typeof(ShutdownTests).Assembly;
		var run = new LoadedModule {
			Name = "ResourceMod",
			FilePath = "",
			Assembly = assembly,
			LoadedAt = DateTimeOffset.Now,
			Runtime = new ResourceOwningRuntime(),
		};
		ModuleHost.Adopt(run);
		ModuleHost.StartRuntime(run);
		await run.InitTask!.WaitAsync(TimeSpan.FromSeconds(5));
		await ModuleHostTests.WaitStateAsync(run, ModuleState.Ready);
		return run;
	}

	private sealed record ConcurrentStopOutcome(IReadOnlyList<Task<ExitCoordinator.ExitReport>> Callers, IReadOnlyList<ExitCoordinator.ExitReport> Reports, int PhaseHits);

	private static async Task<ConcurrentStopOutcome> ConcurrentStopAsync(int workers) {
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var phaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var phaseRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var phaseHits = 0;
		var arrivedCount = 0;
		ExitCoordinator.PhaseBarrierForTest = () => {
			Interlocked.Increment(ref phaseHits);
			phaseEntered.TrySetResult();
			return phaseRelease.Task;
		};
		var seen = new Task<ExitCoordinator.ExitReport>[workers];
		var callers = new Task[workers];
		for (var index = 0; index < workers; index++) {
			var slot = index;
			callers[slot] = Task.Run(async () => {
				await start.Task;
				seen[slot] = HostExit.StopAsync();
				if (Interlocked.Increment(ref arrivedCount) == workers) {
					arrived.TrySetResult();
				}
				await seen[slot];
			});
		}
		start.SetResult();
		await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await phaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.Delay(100);
		phaseRelease.SetResult();
		await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));
		ExitCoordinator.PhaseBarrierForTest = null;
		return new ConcurrentStopOutcome(seen, [.. seen.Select(task => task.Result)], Volatile.Read(ref phaseHits));
	}

	[Fact]
	public async Task BackupNow_WaitsForPreviousRound() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		try {
			Databases.Init();
			await using (var db = Databases.Open<ShutdownDbContext>()) {
				db.Items.Add(new ShutdownItem { Name = "备份等待" });
				await db.SaveChangesAsync();
			}

			await Databases.BackupGate.WaitAsync();
			var task = Databases.BackupNowAsync();
			await Task.Delay(200);
			Assert.False(task.IsCompleted);
			Databases.BackupGate.Release();
			var result = await task;
			Assert.True(result.Success, $"failures: {string.Join(";", result.Failures.Select(f => f.File))}");
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task ShutdownAsync_FinalBackupContainsLastWrite_Idempotent() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		try {
			Databases.Init();
			await using (var db = Databases.Open<ShutdownDbContext>()) {
				db.Items.Add(new ShutdownItem { Name = "最后一笔写入" });
				await db.SaveChangesAsync();
			}

			var first = await Databases.ShutdownAsync();
			Assert.True(first.Success);
			Assert.Equal(1, first.Total);
			Assert.Equal(1, first.Succeeded);

			var backupDir = Path.Combine(baseDir, "data", "backup");
			var snapshots = Directory.GetFiles(backupDir, "Shutdown-*.db");
			Assert.NotEmpty(snapshots);
			var options = new DbContextOptionsBuilder().UseSqlite($"Data Source={snapshots[^1]}").Options;
			await using (var read = new DbContext(options)) {
				var names = await read.Database.SqlQuery<string>($"SELECT \"Name\" AS \"Value\" FROM \"Items\"").ToListAsync();
				Assert.Equal("最后一笔写入", names.Single());
			}

			var second = await Databases.ShutdownAsync();
			Assert.Same(first, second);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task BackupNow_BrokenDbFile_ReportsFailure() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		try {
			Databases.Init();
			var dataDir = Path.Combine(baseDir, "data");
			Directory.CreateDirectory(dataDir);
			File.WriteAllText(Path.Combine(dataDir, "Broken.db"), "不是 SQLite 文件");
			Databases.Init();

			var result = await Databases.BackupNowAsync();
			Assert.False(result.Success);
			Assert.Contains(result.Failures, failure => failure.File == "Broken.db");
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
		}
	}

	[Fact]
	public async Task S01_ModuleStopFails_ExitReportedFailedWithModuleName() {
		await BootAsync();
		try {
			var run = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
			ModuleProbe.SetRuntimeStatic(run, "StopGate", new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
			ModuleProbe.SetRuntimeStatic(run, "OnStopFailure", (Func<Exception>)(() => new InvalidOperationException("模块清理失败")));
			ModuleHost.StopTimeout = TimeSpan.FromMilliseconds(400);

			var report = await new ExitCoordinator().RunAsync();

			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains("FakeModule"));

			var gate = (TaskCompletionSource)ModuleProbe.RuntimeType(run).GetField("StopGate")!.GetValue(null)!;
			gate.SetResult();
			await ModuleHost.StopAsync(run);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S02_StopTimeout_KeepsTaskReference_DoesNotClaimComplete() {
		await BootAsync();
		try {
			var run = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
			var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			ModuleProbe.SetRuntimeStatic(run, "StopGate", stopGate);
			ModuleHost.StopTimeout = TimeSpan.FromMilliseconds(300);

			var stop = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.PendingTimeout, stop.Outcome);
			Assert.Null(run.FinalStopResult);

			var report = await new ExitCoordinator().RunAsync();
			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains("FakeModule") && failure.Contains("PendingTimeout"));

			stopGate.SetResult();
			var final = await ModuleHost.StopAsync(run);
			Assert.Equal(ModuleStopOutcome.Clean, final.Outcome);
			Assert.NotNull(run.FinalStopResult);
			Assert.True(run.FinalStopResult!.Clean);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S03_LastWriteDuringCleanup_EntersFinalBackup() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		ResetGlobalState();
		DiscordGateway.Init(new DiscordSocketClient());
		try {
			Databases.Init();
			var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var run = MakeRun("WriteMod", new WritingRuntime(written, release.Task));
			ModuleHost.Adopt(run);
			ModuleHost.StartRuntime(run);
			await run.InitTask!;

			var coordinator = new ExitCoordinator();
			var reportTask = coordinator.RunAsync();
			await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(reportTask.IsCompleted, "清理挂住期间退出不得完成");

			release.SetResult();
			var report = await reportTask;
			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");

			var backupDir = Path.Combine(baseDir, "data", "backup");
			var snapshots = Directory.GetFiles(backupDir, "Shutdown-*.db");
			Assert.NotEmpty(snapshots);
			var options = new DbContextOptionsBuilder().UseSqlite($"Data Source={snapshots[^1]}").Options;
			await using (var read = new DbContext(options)) {
				var names = await read.Database.SqlQuery<string>($"SELECT \"Name\" AS \"Value\" FROM \"Items\"").ToListAsync();
				Assert.Contains("清理期最后写入", names);
			}

			await ModuleHost.StopAsync(run);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S04_FirstDrainTimeout_CancelThenComplete_ExitSucceeds() {
		var drainedFlags = new List<bool>();
		var coordinator = new ExitCoordinator {
			BusinessDrainOverrideForTest = () => {
				drainedFlags.Add(true);
				return Task.FromResult(drainedFlags.Count >= 2);
			},
		};
		try {
			var report = await coordinator.RunAsync();
			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			Assert.Equal(2, drainedFlags.Count);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S05_BothDrainsFail_ReportedIncomplete_IndependentStepsStillRun() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		try {
			Databases.Init();
			await using (var db = Databases.Open<ShutdownDbContext>()) {
				db.Items.Add(new ShutdownItem { Name = "S05" });
				await db.SaveChangesAsync();
			}
			var coordinator = new ExitCoordinator {
				BusinessDrainOverrideForTest = () => Task.FromResult(false),
			};

			var report = await coordinator.RunAsync();

			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains("业务任务"));
			var backupDir = Path.Combine(baseDir, "data", "backup");
			Assert.True(Directory.Exists(backupDir), "独立收尾（数据库终备份）仍应执行");
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S06_ExitWithoutReload_SingleCleanupNoNewInitAndNoLateDispatch() {
		await BootAsync();
		try {
			var run = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));

			var report = await new ExitCoordinator().RunAsync();
			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));

			var received = 0;
			DiscordGateway.SubscribeMessage(null, (_, _) => {
				Interlocked.Increment(ref received);
				return Task.CompletedTask;
			});
			await DiscordGateway.DispatchMessageForTest(null);
			await Task.Delay(100);
			Assert.Equal(0, received);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S07_RealGatewayStopFailure_ReportedAndLaterStepsStillRun() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		Databases.Init();
		var logPath = Path.Combine(Path.GetTempPath(), $"qq1011_s07_{Guid.NewGuid():N}.log");
		Logger.SetLogPathForTest(logPath);
		await BootAsync();
		try {
			await using (var db = Databases.Open<ShutdownDbContext>()) {
				db.Items.Add(new ShutdownItem { Name = "S07" });
				await db.SaveChangesAsync();
			}
			DiscordGateway.ClientStopForTest = _ => throw new InvalidOperationException("客户端停止失败");

			var report = await new ExitCoordinator().RunAsync();

			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains("网关停止"));
			Assert.True(Directory.Exists(Path.Combine(baseDir, "data", "backup")), "网关停止失败后数据库收尾仍执行");

			Logger.SetLogPathForTest(null);
			var log = File.ReadAllText(logPath);
			Assert.Contains("网关停止", log);
			Assert.Contains("客户端停止失败", log);
		} finally {
			Logger.SetLogPathForTest(null);
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
			try { File.Delete(logPath); } catch (IOException) { }
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task S09_UnstartedHost_RunAsyncSafeWithoutNullReference() {
		ResetGlobalState();
		Databases.SetDataDirectoryForTest(null);
		DiscordGateway.DetachForTest();
		try {
			var coordinator = new ExitCoordinator();
			var report = await coordinator.RunAsync();
			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task EXIT_01_ControlTaskOverBudget_NoReleaseNoSnapshotDamage() {
		await BootAsync();
		var snapshot = InteractionHost.Current;
		var retiredBefore = InteractionHost.RetiredServiceCountForTest;
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		InteractionHost.SyncBodyForTest = ct => {
			entered.SetResult();
			return hold.Task;
		};
		ExitCoordinator.ControlSectionBudget = TimeSpan.FromMilliseconds(300);
		try {
			InteractionHost.OnGatewayReady();
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var report = await new ExitCoordinator().RunAsync();

			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains("控制操作"));
			Assert.Same(snapshot, InteractionHost.SnapshotForTest);
			Assert.Equal(retiredBefore, InteractionHost.RetiredServiceCountForTest);
			Assert.Same(snapshot.Service, InteractionHost.Current.Service);

			hold.SetResult();
			await InteractionHost.SyncTaskForTest!.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(1, InteractionHost.ControlGateCountForTest);
			Assert.Equal("框架正在退出", await InteractionHost.SyncAsync(CancellationToken.None));
		} finally {
			hold.TrySetResult();
			InteractionHost.SyncBodyForTest = null;
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task EXIT_02_BusinessIgnoresCancel_DependenciesStayAlive() {
		await BootAsync();
		var snapshot = InteractionHost.Current;
		var retiredBefore = InteractionHost.RetiredServiceCountForTest;
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handle = DiscordGateway.SubscribeMessage(null, async (_, ct) => {
			entered.SetResult();
			await release.Task;
		});
		ExitCoordinator.BusinessDrainBudget = TimeSpan.FromMilliseconds(300);
		ExitCoordinator.BusinessCancelDrainBudget = TimeSpan.FromMilliseconds(300);
		try {
			await DiscordGateway.DispatchMessageForTest(null);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var report = await new ExitCoordinator().RunAsync();

			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains("业务任务"));
			Assert.Contains(report.Failures, failure => failure.Contains("跳过"));
			Assert.Same(snapshot, InteractionHost.SnapshotForTest);
			Assert.Equal(retiredBefore, InteractionHost.RetiredServiceCountForTest);
			Assert.Same(snapshot.Service, InteractionHost.Current.Service);
			Assert.Contains(report.Failures, failure => failure.Contains("模块清理未开始"));
			Assert.All(ModuleHost.Modules, module => Assert.Equal(0, ModuleProbe.RuntimeStaticInt(module, "StopCount")));
			Assert.All(ModuleHost.Modules, module => Assert.Equal(ModuleState.Ready, module.State));

			release.SetResult();
			await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
		} finally {
			release.TrySetResult();
			await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task EXIT_03_ModuleCleanupTimeout_SingleCleanupAndFailureNotRewritten() {
		await BootAsync();
		var run = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
		var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		ModuleProbe.SetRuntimeStatic(run, "StopGate", stopGate);
		ModuleHost.StopTimeout = TimeSpan.FromMilliseconds(300);
		try {
			var first = await HostExit.StopAsync();
			Assert.False(first.Success);
			Assert.Contains(first.Failures, failure => failure.Contains("FakeModule"));

			stopGate.SetResult();
			await ModuleHost.StopAsync(run);
			Assert.True(run.FinalStopResult!.Clean);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));

			var again = await HostExit.StopAsync();
			Assert.Same(first, again);
			Assert.False(again.Success);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
		} finally {
			stopGate.TrySetResult();
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task EXIT_04_NormalCompletion_ServiceReleasedOnceAndFinalBackupVerifiable() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		Databases.Init();
		await BootAsync();
		var retiredBefore = InteractionHost.RetiredServiceCountForTest;
		try {
			await using (var db = Databases.Open<ShutdownDbContext>()) {
				db.Items.Add(new ShutdownItem { Name = "EXIT-04 最后一笔写入" });
				await db.SaveChangesAsync();
			}

			var report = await HostExit.StopAsync();

			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			Assert.Null(InteractionHost.SnapshotForTest);
			Assert.Equal(retiredBefore + 1, InteractionHost.RetiredServiceCountForTest);

			Assert.Same(report, await HostExit.StopAsync());
			Assert.Equal(retiredBefore + 1, InteractionHost.RetiredServiceCountForTest);

			var snapshots = Directory.GetFiles(Path.Combine(baseDir, "data", "backup"), "Shutdown-*.db");
			Assert.NotEmpty(snapshots);
			var options = new DbContextOptionsBuilder().UseSqlite($"Data Source={snapshots[^1]}").Options;
			await using (var read = new DbContext(options)) {
				var names = await read.Database.SqlQuery<string>($"SELECT \"Name\" AS \"Value\" FROM \"Items\"").ToListAsync();
				Assert.Contains("EXIT-04 最后一笔写入", names);
			}
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task EXIT_05_FirstDrainTimeout_CancelThenComplete_ExitSucceeds() {
		await BootAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handle = DiscordGateway.SubscribeMessage(null, async (_, ct) => {
			entered.SetResult();
			await Task.Delay(Timeout.Infinite, ct);
		});
		ExitCoordinator.BusinessDrainBudget = TimeSpan.FromMilliseconds(300);
		ExitCoordinator.BusinessCancelDrainBudget = TimeSpan.FromSeconds(5);
		try {
			await DiscordGateway.DispatchMessageForTest(null);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var report = await HostExit.StopAsync();

			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			Assert.Null(InteractionHost.SnapshotForTest);
			Assert.All(ModuleHost.Modules, module => Assert.Equal(1, ModuleProbe.RuntimeStaticInt(module, "StopCount")));
			Assert.All(ModuleHost.Modules, module => Assert.NotNull(module.FinalStopResult));
		} finally {
			await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task EXIT_06_ConcurrentProductionEntry_SingleCoordinatorTaskAndSingleBackup() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		Databases.Init();
		await BootAsync();
		var run = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
		var retiredBefore = InteractionHost.RetiredServiceCountForTest;
		try {
			await using (var db = Databases.Open<ShutdownDbContext>()) {
				db.Items.Add(new ShutdownItem { Name = "EXIT-06" });
				await db.SaveChangesAsync();
			}

			var success = await ConcurrentStopAsync(8);
			Assert.All(success.Callers, caller => Assert.Same(success.Callers[0], caller));
			Assert.All(success.Reports, report => Assert.Same(success.Reports[0], report));
			Assert.True(success.Reports[0].Success, $"failures: {string.Join(";", success.Reports[0].Failures)}");
			Assert.Equal(1, success.PhaseHits);
			Assert.Equal(retiredBefore + 1, InteractionHost.RetiredServiceCountForTest);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Single(Directory.GetFiles(Path.Combine(baseDir, "data", "backup"), "Shutdown-*.db"));

			HostExit.ResetForTest();
			DiscordGateway.ClientStopForTest = _ => throw new InvalidOperationException("EXIT-06 网关停止失败");
			var failure = await ConcurrentStopAsync(8);
			Assert.All(failure.Callers, caller => Assert.Same(failure.Callers[0], caller));
			Assert.All(failure.Reports, report => Assert.Same(failure.Reports[0], report));
			Assert.False(failure.Reports[0].Success);
			Assert.Equal(1, failure.PhaseHits);
			Assert.Equal(retiredBefore + 1, InteractionHost.RetiredServiceCountForTest);
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.Single(Directory.GetFiles(Path.Combine(baseDir, "data", "backup"), "Shutdown-*.db"));
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task EXIT_07_FailureSummaryReachesLoggerAndNonZeroExitCode() {
		var logPath = Path.Combine(Path.GetTempPath(), $"qq1011_exit07_{Guid.NewGuid():N}.log");
		Logger.SetLogPathForTest(logPath);
		await BootAsync();
		try {
			DiscordGateway.ClientStopForTest = _ => throw new InvalidOperationException("EXIT-07 网关停止失败");

			var report = await HostExit.StopAsync();

			Assert.False(report.Success);
			Assert.Equal(1, HostExit.CodeFor(true, report));
			Assert.Equal(1, HostExit.CodeFor(false, report));
			Assert.Equal(1, HostExit.CodeFor(true, new ExitCoordinator.ExitReport(false, ["人为失败"])));
			Assert.Equal(0, HostExit.CodeFor(true, new ExitCoordinator.ExitReport(true, [])));

			Logger.SetLogPathForTest(null);
			var log = File.ReadAllText(logPath);
			Assert.Contains("退出失败", log);
			Assert.Contains("网关停止", log);
			Assert.Contains("EXIT-07 网关停止失败", log);
		} finally {
			Logger.SetLogPathForTest(null);
			try { File.Delete(logPath); } catch (IOException) { }
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task EXIT_08_ExitDuringReadySync_NoReopenNoAlternativePublish() {
		await BootAsync();
		var run = ModuleHost.Modules.Single(module => module.Name == "FakeModule");
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		InteractionHost.SyncBodyForTest = ct => {
			entered.SetResult();
			return hold.Task;
		};
		try {
			InteractionHost.OnGatewayReady();
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var exitTask = HostExit.StopAsync();
			Assert.False(DiscordGateway.Dispatcher!.Accepting, "退出登记后不得重新接单");
			Assert.True(DiscordGateway.Dispatcher!.ExitClosedForTest);
			Assert.NotNull(InteractionHost.SnapshotForTest);

			hold.SetResult();
			var report = await exitTask;
			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "InitCount"));
			Assert.Equal(1, ModuleProbe.RuntimeStaticInt(run, "StopCount"));
			Assert.False(DiscordGateway.Dispatcher!.Accepting);
			Assert.True(DiscordGateway.Dispatcher!.ExitClosedForTest);
			Assert.Null(InteractionHost.SnapshotForTest);
			Assert.Throws<InvalidOperationException>(() => InteractionHost.Current);
		} finally {
			hold.TrySetResult();
			InteractionHost.SyncBodyForTest = null;
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task STOP_01_BusinessStillRunning_ModuleResourcesNotReleased() {
		var run = await BootResourceModuleAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var access = "";
		using var handle = DiscordGateway.SubscribeMessage(run, async (_, ct) => {
			entered.SetResult();
			await release.Task;
			try {
				ResourceOwningRuntime.Resource!.WriteByte(1);
				access = "写入成功";
			} catch (Exception e) {
				access = $"{e.GetType().Name}: {e.Message}";
			}
		});
		ExitCoordinator.BusinessDrainBudget = TimeSpan.FromMilliseconds(300);
		ExitCoordinator.BusinessCancelDrainBudget = TimeSpan.FromMilliseconds(300);
		try {
			await DiscordGateway.DispatchMessageForTest(null);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var report = await HostExit.StopAsync();

			Assert.False(report.Success);
			Assert.Equal(1, HostExit.CodeFor(true, report));
			Assert.Contains(report.Failures, failure => failure.Contains("模块清理未开始") && failure.Contains("业务任务未确认结束"));
			Assert.DoesNotContain(report.Failures, failure => failure.Contains("模块 ResourceMod 停止结果"));

			Assert.Equal(0, ResourceOwningRuntime.StopCount);
			Assert.NotNull(ResourceOwningRuntime.Resource);
			ResourceOwningRuntime.Resource!.WriteByte(1);
			Assert.Equal(ModuleState.Ready, run.State);
			Assert.Contains(run, ModuleHost.Modules);

			release.SetResult();
			await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
			Assert.Equal("写入成功", access);

			var again = await HostExit.StopAsync();
			Assert.Same(report, again);
			Assert.False(again.Success);
			Assert.Equal(0, ResourceOwningRuntime.StopCount);
		} finally {
			release.TrySetResult();
			await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task STOP_02_BusinessFinishesFirst_ModuleCleanupRunsAfterwardsExactlyOnce() {
		var run = await BootResourceModuleAsync();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handle = DiscordGateway.SubscribeMessage(run, async (_, ct) => {
			entered.SetResult();
			await release.Task;
			ResourceOwningRuntime.Resource!.WriteByte(1);
			ResourceOwningRuntime.Events.Add("business-access");
		});
		ExitCoordinator.BusinessDrainBudget = TimeSpan.FromSeconds(10);
		try {
			await DiscordGateway.DispatchMessageForTest(null);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var exitTask = HostExit.StopAsync();
			await Task.Delay(300);
			Assert.False(exitTask.IsCompleted, "业务未放行时排空不得结束");
			Assert.Equal(0, ResourceOwningRuntime.StopCount);
			Assert.Equal(ModuleState.Ready, run.State);

			release.SetResult();
			var report = await exitTask;

			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			Assert.Equal(1, ResourceOwningRuntime.StopCount);
			Assert.Equal(["business-access", "module-dispose"], ResourceOwningRuntime.Events);
			Assert.Null(ResourceOwningRuntime.Resource);
			Assert.Contains(run, ModuleHost.Modules);
		} finally {
			release.TrySetResult();
			await DiscordGateway.DrainWorkAsync(TimeSpan.FromSeconds(5));
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task GW_01_ClientStopFails_RealGatewayPropagatesToReport() {
		await BootAsync();
		var stopCalls = 0;
		var logoutCalls = 0;
		DiscordGateway.ClientStopForTest = _ => {
			stopCalls++;
			throw new InvalidOperationException("GW-01 客户端停止失败");
		};
		DiscordGateway.ClientLogoutForTest = _ => {
			logoutCalls++;
			return Task.CompletedTask;
		};
		try {
			var report = await HostExit.StopAsync();

			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains("网关停止") && failure.Contains("GW-01 客户端停止失败"));
			Assert.Equal(1, stopCalls);
			Assert.Equal(1, logoutCalls);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task GW_02_LogoutFails_ExitNotSuccessful() {
		await BootAsync();
		var stopCalls = 0;
		var logoutCalls = 0;
		DiscordGateway.ClientStopForTest = _ => {
			stopCalls++;
			return Task.CompletedTask;
		};
		DiscordGateway.ClientLogoutForTest = _ => {
			logoutCalls++;
			throw new InvalidOperationException("GW-02 退出登录失败");
		};
		try {
			var report = await HostExit.StopAsync();

			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains("网关停止") && failure.Contains("GW-02 退出登录失败"));
			Assert.Equal(1, stopCalls);
			Assert.Equal(1, logoutCalls);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task GW_03_BothOperationsSucceed_GatewayCalledOnceEach() {
		await BootAsync();
		var stopCalls = 0;
		var logoutCalls = 0;
		DiscordGateway.ClientStopForTest = _ => {
			stopCalls++;
			return Task.CompletedTask;
		};
		DiscordGateway.ClientLogoutForTest = _ => {
			logoutCalls++;
			return Task.CompletedTask;
		};
		try {
			var report = await HostExit.StopAsync();

			Assert.True(report.Success, $"failures: {string.Join(";", report.Failures)}");
			Assert.DoesNotContain(report.Failures, failure => failure.Contains("网关"));
			Assert.Equal(1, stopCalls);
			Assert.Equal(1, logoutCalls);
		} finally {
			await TeardownAsync();
		}
	}

	[Fact]
	public async Task GW_04_GatewayFailure_IndependentStepsStillRun_FailureNotOverwritten() {
		var baseDir = TempBase();
		Databases.SetDataDirectoryForTest(baseDir);
		Databases.Init();
		await BootAsync();
		await using (var db = Databases.Open<ShutdownDbContext>()) {
			db.Items.Add(new ShutdownItem { Name = "GW-04" });
			await db.SaveChangesAsync();
		}
		DiscordGateway.ClientStopForTest = _ => throw new InvalidOperationException("GW-04 网关停止失败");
		try {
			var report = await HostExit.StopAsync();

			Assert.False(report.Success);
			Assert.Contains(report.Failures, failure => failure.Contains("网关停止"));
			Assert.True(Directory.Exists(Path.Combine(baseDir, "data", "backup")), "网关失败后数据库收尾仍执行");
			Assert.Equal(1, HostExit.CodeFor(true, report));

			var again = await HostExit.StopAsync();
			Assert.Same(report, again);
			Assert.False(again.Success);
		} finally {
			Databases.SetDataDirectoryForTest(null);
			Cleanup(baseDir);
			await TeardownAsync();
		}
	}
}
