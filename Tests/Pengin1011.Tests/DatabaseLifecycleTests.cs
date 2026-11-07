using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class DatabaseLifecycleTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "pengin_database_lifecycle_" + Guid.NewGuid().ToString("N"));

	public DatabaseLifecycleTests() {
		Directory.CreateDirectory(_directory);
		DatabaseTestSetup.SetDirectory(_directory);
	}

	public void Dispose() {
		DatabasePrepareEngine.IntentWrittenForTest = null;
		DatabaseTestSetup.SetDirectory(null);
		Directory.Delete(_directory, true);
	}

	[Fact]
	public async Task UncreatedDeclarationsAppearInStatusWithoutBecomingBackupFailures() {
		Databases.Init();
		var statuses = Databases.GetStatus();
		Assert.Equal(6, statuses.Count);
		Assert.All(statuses, status => Assert.Equal(DatabaseState.NotPresent, status.State));
		Assert.Equal(statuses.Select(status => status.Database).OrderBy(id => id, StringComparer.Ordinal), Databases.DatabaseIds());
		var backup = await Databases.BackupNowAsync();
		Assert.True(backup.Success);
		Assert.Equal(0, backup.Total);
		Assert.Empty(Directory.GetFiles(_directory, "*.db"));
	}

	[Fact]
	public async Task MissingMigrationsRemainVisibleWithoutCreatingADatabase() {
		Databases.Init();
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<BareDbContext>());
		var status = Assert.Single(Databases.GetStatus(), status => status.Database == "Bare");
		Assert.Equal(DatabaseState.Failed, status.State);
		Assert.Null(status.FilePath);
		Assert.Equal(0, (await Databases.BackupNowAsync()).Total);
	}

	[Fact]
	public async Task FreshMigrationFailureRemainsVisibleAndBlocksBackup() {
		Databases.Init();
		DatabasePrepareEngine.IntentWrittenForTest = _ => throw new IOException("migration interrupted");
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareAsync<FakeDbContext>());
		var status = Assert.Single(Databases.GetStatus(), status => status.Database == "Fake");
		Assert.Equal(DatabaseState.MaintenanceRequired, status.State);
		Assert.True(status.PendingMaintenance);
		var backup = await Databases.BackupNowAsync();
		Assert.False(backup.Success);
		Assert.Equal("Fake.db", Assert.Single(backup.Failures).File);
	}

	[Fact]
	public async Task PartialModulePreparationKeepsEveryDeclarationAndBacksUpTheSuccessfulDatabase() {
		Databases.SetDataDirectoryForTest(_directory);
		var registrar = new DatabaseRegistrar();
		registrar.Add<FakeDbContext>("AReady", options => new FakeDbContext(options));
		registrar.Add<BareDbContext>("ZMissing", options => new BareDbContext(options));
		Databases.RegisterForTest(registrar);
		Databases.Init();
		await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareModuleAsync(typeof(Databases).Assembly, CancellationToken.None));
		Assert.Equal(["AReady", "ZMissing"], Databases.DatabaseIds());
		Assert.Equal(DatabaseState.Prepared, Databases.GetStatus("AReady").State);
		Assert.Equal(DatabaseState.Failed, Databases.GetStatus("ZMissing").State);
		var backup = await Databases.BackupNowAsync();
		Assert.True(backup.Success);
		Assert.Equal(1, backup.Total);
	}

	[Fact]
	public void FailedRegistrationAndRollbackLeaveNoPhantomDeclarations() {
		Databases.SetDataDirectoryForTest(_directory);
		var first = new DatabaseRegistrar();
		first.Add<FakeDbContext>("Shared", options => new FakeDbContext(options));
		Databases.RegisterForTest(first);
		var second = new DatabaseRegistrar();
		second.Add<OtherDbContext>("Unused", options => new OtherDbContext(options));
		second.Add<ClashContext>("shared", options => new ClashContext(options));
		Assert.Throws<InvalidOperationException>(() => Databases.RegisterForTest(second));
		Assert.Equal(["Shared"], Databases.DatabaseIds());
		Databases.UnregisterModule(typeof(Databases).Assembly);
		Assert.Empty(Databases.GetStatus());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PendingDatabaseIsDiscoveredAndNeverOpenedDuringShutdown(bool hasFile) {
		var path = Path.Combine(_directory, "Orphan.db");
		if (hasFile) File.WriteAllText(path, "must not be opened");
		DatabaseMaintenance.Write(_directory, "Orphan", "unfinished");
		Databases.Init();
		var status = Assert.Single(Databases.GetStatus(), status => status.Database == "Orphan");
		Assert.Equal(DatabaseState.MaintenanceRequired, status.State);
		var result = await Databases.ShutdownAsync();
		Assert.False(result.Success);
		Assert.Equal("Orphan.db", Assert.Single(result.Failures).File);
		Assert.Equal(hasFile, File.Exists(path));
		if (hasFile) Assert.Equal("must not be opened", File.ReadAllText(path));
		Assert.Equal("unfinished", File.ReadAllText(DatabaseMaintenance.MarkerPath(_directory, "Orphan")));
	}

	[Fact]
	public async Task MissingPreparedFileStillFailsBackupWithoutBeingRecreated() {
		Databases.Init();
		await Databases.PrepareAsync<FakeDbContext>();
		var path = Path.Combine(_directory, "Fake.db");
		File.Delete(path);
		var backup = await Databases.BackupNowAsync();
		Assert.False(backup.Success);
		Assert.Equal("Fake.db", Assert.Single(backup.Failures).File);
		Assert.False(File.Exists(path));
	}

	[Fact]
	public async Task OwnerCancellationWaitsForTheActualPreparationBeforeReturning() {
		Databases.Init();
		using var cancellation = new CancellationTokenSource();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		DatabasePrepareEngine.IntentWrittenForTest = async ct => {
			using var registration = ct.Register(() => cancelled.TrySetResult());
			entered.TrySetResult();
			await release.Task;
			ct.ThrowIfCancellationRequested();
		};
		var prepare = Databases.PrepareAsync<FakeDbContext>(cancellation.Token);
		try {
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			cancellation.Cancel();
			Assert.False(prepare.IsCompleted);
			await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Throws<InvalidOperationException>(Databases.ReleaseOwnership);
			Assert.False(DataDirectoryOwnership.TryAcquire(_directory, out var competing, out _));
			competing?.Dispose();
			release.TrySetResult();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.True(DatabaseMaintenance.Exists(_directory, "Fake"));
		} finally {
			release.TrySetResult();
			await Record.ExceptionAsync(() => prepare);
		}
	}

	[Fact]
	public async Task PreparationAfterShutdownKeepsItsRejectedResultWithoutCreatingFiles() {
		var service = new DatabaseService();
		service.Configure(_directory);
		var registrar = new DatabaseRegistrar();
		registrar.Add<FakeDbContext>("Fake", options => new FakeDbContext(options));
		service.Register(typeof(FakeDbContext).Assembly, registrar.Definitions);
		await service.StopPreparingAsync();
		var first = service.PrepareAsync<FakeDbContext>(CancellationToken.None);
		var second = service.PrepareAsync<FakeDbContext>(CancellationToken.None);
		Assert.Same(first, second);
		Assert.False((await first).Success);
		Assert.Equal(DatabaseState.Failed, service.StateOf<FakeDbContext>());
		Assert.False(File.Exists(Path.Combine(_directory, "Fake.db")));
		Assert.False(DatabaseMaintenance.Exists(_directory, "Fake"));
	}

	[Fact]
	public async Task LaterCallerCannotCancelTheOwnersWorkOrReturnBeforeItFinishes() {
		using var owner = new CancellationTokenSource();
		using var later = new CancellationTokenSource();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var preparations = 0;
		DatabasePrepareEngine.IntentWrittenForTest = async ct => {
			Interlocked.Increment(ref preparations);
			entered.TrySetResult();
			await release.Task;
			ct.ThrowIfCancellationRequested();
		};
		var first = Databases.PrepareAsync<FakeDbContext>(owner.Token);
		try {
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var second = Databases.PrepareAsync<FakeDbContext>(later.Token);
			later.Cancel();
			Assert.False(second.IsCompleted);
			release.TrySetResult();
			await first.WaitAsync(TimeSpan.FromSeconds(5));
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
			owner.Cancel();
			await Databases.PrepareAsync<FakeDbContext>();
			Assert.Equal(1, preparations);
			Assert.Equal(DatabaseState.Prepared, Databases.StateOf<FakeDbContext>());
			Assert.False(DatabaseMaintenance.Exists(_directory, "Fake"));
		} finally {
			release.TrySetResult();
			await Record.ExceptionAsync(() => first);
		}
	}

	[Fact]
	public async Task CancellationWhileWaitingForMaintenanceEndsTheWorkWithoutWritingAMarker() {
		Databases.Init();
		await Databases.BackupGate.WaitAsync();
		using var cancellation = new CancellationTokenSource();
		try {
			var prepare = Databases.PrepareAsync<FakeDbContext>(cancellation.Token);
			Assert.True(SpinWait.SpinUntil(() => Directory.Exists(Path.Combine(_directory, "backup", "Fake")), TimeSpan.FromSeconds(5)));
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.Equal(DatabaseState.Failed, Databases.StateOf<FakeDbContext>());
			Assert.False(DatabaseMaintenance.Exists(_directory, "Fake"));
			Assert.False(File.Exists(Path.Combine(_directory, "Fake.db")));
			Databases.ReleaseOwnership();
		} finally {
			Databases.BackupGate.Release();
		}
	}
}
