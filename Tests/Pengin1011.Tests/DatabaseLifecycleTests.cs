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
		registrar.Add<FakeDbContext>("AReady", (provider, connection) => new FakeDbContext(DatabaseOptions.Create<FakeDbContext>(provider, connection, "AReady")));
		registrar.Add<BareDbContext>("ZMissing", (provider, connection) => new BareDbContext(DatabaseOptions.Create<BareDbContext>(provider, connection, "ZMissing")));
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
		first.Add<FakeDbContext>("Shared", (provider, connection) => new FakeDbContext(DatabaseOptions.Create<FakeDbContext>(provider, connection, "Shared")));
		Databases.RegisterForTest(first);
		var second = new DatabaseRegistrar();
		second.Add<OtherDbContext>("Unused", (provider, connection) => new OtherDbContext(DatabaseOptions.Create<OtherDbContext>(provider, connection, "Unused")));
		second.Add<ClashContext>("shared", (provider, connection) => new ClashContext(DatabaseOptions.Create<ClashContext>(provider, connection, "shared")));
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

	[Theory]
	[InlineData(DatabaseProvider.SQLite)]
	[InlineData(DatabaseProvider.MySQL)]
	[InlineData(DatabaseProvider.PostgreSQL)]
	public async Task PreparationAfterShutdownKeepsItsRejectedResultWithoutCreatingFiles(DatabaseProvider provider) {
		var service = new DatabaseService();
		service.Configure(_directory);
		service.ConfigureProvider(ExternalDatabaseTests.Configuration(provider));
		var calls = 0;
		var registrar = new DatabaseRegistrar(provider);
		registrar.Add<FakeDbContext>("Fake", (_, _) => {
			Interlocked.Increment(ref calls);
			throw new InvalidOperationException("The context must not be created after shutdown.");
		});
		service.Register(typeof(FakeDbContext).Assembly, registrar.Definitions);
		await service.StopPreparingAsync();
		var callers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() => service.PrepareAsync<FakeDbContext>(CancellationToken.None))));
		var first = callers[0];
		Assert.All(callers, task => Assert.Same(first, task));
		Assert.True(first.IsCompletedSuccessfully);
		var result = await first;
		Assert.False(result.Success);
		Assert.Equal(result.Failure, service.EntryOf(typeof(FakeDbContext)).Failure);
		Assert.NotNull(result.Failure);
		if (provider != DatabaseProvider.SQLite) Assert.Contains(L.Get("ExternalDatabaseCancelled"), result.Failure);
		Assert.Equal(DatabaseState.Failed, service.StateOf<FakeDbContext>());
		Assert.Equal(0, calls);
		Assert.False(File.Exists(Path.Combine(_directory, "Fake.db")));
		Assert.False(DatabaseMaintenance.Exists(_directory, "Fake"));
		await service.StopPreparingAsync();
		service.ReleaseOwnership();
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
