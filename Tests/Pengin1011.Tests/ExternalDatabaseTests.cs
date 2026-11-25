using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.Data.MySqlClient;
using Npgsql;
using Pengin1011.Core;
using Pengin1011.Core.Logging;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ExternalDatabaseTests {
	[Theory]
	[InlineData(DatabaseProvider.SQLite)]
	[InlineData(DatabaseProvider.MySQL)]
	[InlineData(DatabaseProvider.PostgreSQL)]
	public void ConfigurationSelectsBackendAndPreservesPassword(DatabaseProvider provider) {
		var config = Configuration(provider);
		WithConfig(config, () => {
			Assert.True(AppConfig.DatabaseConfigurationValid);
			Assert.Equal(provider, DatabaseOptions.ProviderOf(AppConfig.Databases));
			if (provider == DatabaseProvider.SQLite) return;
			var connection = DatabaseOptions.ConnectionString(AppConfig.Databases);
			if (provider == DatabaseProvider.MySQL) {
				var parsed = new MySqlConnectionStringBuilder(connection);
				Assert.Equal(3306u, parsed.Port);
				Assert.Equal("secret;=\" spaced ", parsed.Password);
			} else {
				var parsed = new NpgsqlConnectionStringBuilder(connection);
				Assert.Equal(5432, parsed.Port);
				Assert.Equal("secret;=\" spaced ", parsed.Password);
			}
		});
	}

	[Theory]
	[InlineData("{\"MySQL\":{},\"PostgreSQL\":{}}")]
	[InlineData("{\"MySQL\":{}}")]
	[InlineData("{\"PostgreSQL\":{\"Host\":\"localhost\",\"Username\":\"u\"}}")]
	[InlineData("{\"MySQL\":{\"Host\":\"localhost\",\"Database\":\"d\",\"Username\":\"u\",\"Port\":0}}")]
	[InlineData("{\"PostgreSQL\":{\"Host\":\"localhost\",\"Database\":\"d\",\"Username\":\"u\",\"Port\":65536}}")]
	public void InvalidConfigurationFailsWithoutInitializingSQLite(string json) {
		var config = JsonSerializer.Deserialize<DatabaseConfig>(json)!;
		Assert.False(DatabaseOptions.TryValidate(config, out var failure));
		Assert.NotNull(failure);
		Assert.Throws<ArgumentException>(() => new DatabaseService().ConfigureProvider(config));
		WithConfig(config, () => Assert.False(AppConfig.DatabaseConfigurationValid), valid: false);
	}

	[Theory]
	[InlineData(DatabaseProvider.MySQL)]
	[InlineData(DatabaseProvider.PostgreSQL)]
	public async Task ExternalModeDoesNotTouchSQLiteFilesOrReportSuccessfulBackup(DatabaseProvider provider) {
		var directory = Path.Combine(Path.GetTempPath(), "pengin_external_" + Guid.NewGuid().ToString("N"));
		Databases.SetDataDirectoryForTest(directory);
		try {
			Databases.ConfigureProvider(Configuration(provider));
			Databases.Init();
			Assert.Empty(Databases.DatabaseIds());
			Assert.False((await Databases.BackupNowAsync()).Success);
			Assert.Throws<NotSupportedException>(() => Databases.ListBackups("Test", "rolling"));
			foreach (var command in new[] { new[] { "db", "backup" }, ["db", "backups", "Test"], ["db", "verify", "Test", "snapshot"] }) {
				Assert.Equal(L.Get("ExternalDatabaseBackupUnsupported"), await DatabaseCommands.ExecuteAsync(command));
			}
			var status = Databases.GetStatus("Test");
			Assert.Equal(provider, status.Provider);
			Assert.Null(status.FilePath);
			Assert.Null(status.SizeBytes);
			Assert.Null(status.WalSizeBytes);
			await Databases.ShutdownAsync();
			Assert.False(Directory.Exists(directory));
		} finally {
			Databases.SetDataDirectoryForTest(null);
		}
	}

	[Theory]
	[InlineData(DatabaseProvider.SQLite)]
	[InlineData(DatabaseProvider.MySQL)]
	[InlineData(DatabaseProvider.PostgreSQL)]
	public void GeneratedContextsShareBusinessModelButHaveIndependentMappings(DatabaseProvider provider) {
		var assembly = GenerateModule();
		var registrar = new DatabaseRegistrar(provider);
		((IModuleDatabaseProvider)Activator.CreateInstance(assembly.GetCustomAttribute<ModuleDatabaseProviderAttribute>()!.ProviderType)!).Register(registrar);
		foreach (var definition in registrar.Definitions) {
			var connection = provider == DatabaseProvider.SQLite ? "Data Source=:memory:" : DatabaseOptions.ConnectionString(Configuration(provider));
			using var context = definition.Create(connection);
			var suffix = provider == DatabaseProvider.SQLite ? "" : provider.ToString();
			Assert.Equal(definition.ContextType.FullName + suffix, context.GetType().FullName);
			Assert.True(definition.ContextType.IsInstanceOfType(context));
			var prefix = provider == DatabaseProvider.SQLite ? "" : DatabaseOptions.Prefix(definition.Id);
			var entity = context.Model.FindEntityType(assembly.GetType("Probe.Item")!)!;
			Assert.Equal(prefix + "Items", entity.GetTableName());
			Assert.Equal(prefix + "CustomIndex", entity.GetIndexes().Single().GetDatabaseName());
			Assert.Equal(prefix + "CustomKey", entity.FindPrimaryKey()!.GetName());
			var factory = Activator.CreateInstance(assembly.GetType(context.GetType().FullName + "DesignFactory")!)!;
			using var design = (DbContext)factory.GetType().GetMethod("CreateDbContext")!.Invoke(factory, [Array.Empty<string>()])!;
			Assert.Equal(context.Database.GenerateCreateScript(), design.Database.GenerateCreateScript());
			Assert.Contains(provider == DatabaseProvider.SQLite ? "__EFMigrationsHistory" : DatabaseOptions.HistoryTable(definition.Id), context.GetService<IHistoryRepository>().GetCreateScript());
		}
	}

	[Fact]
	public async Task FailedPreparationIsCachedAndCannotLeakConnectionSecrets() {
		var config = Configuration(DatabaseProvider.PostgreSQL);
		var service = new DatabaseService();
		service.ConfigureProvider(config);
		var calls = 0;
		var registrar = new DatabaseRegistrar(service.Provider);
		registrar.Add<FakeDbContext>("Secret", (_, _) => {
			calls++;
			throw new InvalidOperationException(DatabaseOptions.ConnectionString(config));
		});
		service.Register(typeof(FakeDbContext).Assembly, registrar.Definitions);
		var messages = new List<string>();
		using var listener = Logger.Subscribe(entry => messages.Add(entry.Message + entry.Exception));
		var first = await service.PrepareAsync<FakeDbContext>(CancellationToken.None);
		Assert.False(first.Success);
		Assert.Same(first, await service.PrepareAsync<FakeDbContext>(CancellationToken.None));
		Assert.Equal(1, calls);
		Assert.DoesNotContain("secret", string.Join("\n", messages), StringComparison.Ordinal);
		Assert.DoesNotContain("secret", first.Failure!, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("caller")]
	[InlineData("shutdown")]
	[InlineData("timeout")]
	public async Task CancellationWhileWaitingForMaintenanceNeverCreatesAContext(string reason) {
		var service = new DatabaseService();
		service.ConfigureProvider(Configuration(DatabaseProvider.PostgreSQL));
		var registrar = new DatabaseRegistrar(service.Provider);
		var calls = 0;
		registrar.Add<FakeDbContext>("Cancelled", (_, _) => {
			calls++;
			throw new InvalidOperationException("unexpected context creation");
		});
		service.Register(typeof(FakeDbContext).Assembly, registrar.Definitions);
		using var cancellation = new CancellationTokenSource();
		var originalBudget = DatabaseService.PrepareBudget;
		await service.MaintenanceGate.WaitAsync();
		try {
			if (reason == "timeout") DatabaseService.PrepareBudget = TimeSpan.FromMilliseconds(30);
			var task = service.PrepareAsync<FakeDbContext>(cancellation.Token);
			if (reason == "caller") cancellation.Cancel();
			if (reason == "shutdown") await service.StopPreparingAsync();
			var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(result.Success);
			Assert.Equal(DatabaseState.Failed, result.State);
			Assert.Contains(L.Get(reason == "timeout" ? "ExternalDatabaseTimeout" : "ExternalDatabaseCancelled"), result.Failure!);
			Assert.Equal(0, calls);
			Assert.Equal(0, service.MaintenanceGate.CurrentCount);
		} finally {
			DatabaseService.PrepareBudget = originalBudget;
			service.MaintenanceGate.Release();
			await service.StopPreparingAsync();
		}
	}

	internal static Assembly GenerateModule(string? source = null) {
		var result = DatabaseGeneratorTests.Generate(source ?? """
			using Microsoft.EntityFrameworkCore;
			using Pengin1011.Core;
			namespace Probe;
			[ModuleDatabase("First")]
			public partial class FirstContext : DbContext {
				public DbSet<Item> Items => Set<Item>();
				protected override void OnModelCreating(ModelBuilder model) {
					model.Entity<Item>().HasKey(x => x.Id).HasName("CustomKey");
					model.Entity<Item>().HasIndex(x => x.Name).HasDatabaseName("CustomIndex");
				}
			}
			[ModuleDatabase("Second")]
			public partial class SecondContext : DbContext {
				public DbSet<Item> Items => Set<Item>();
				protected override void OnModelCreating(ModelBuilder model) {
					model.Entity<Item>().HasKey(x => x.Id).HasName("CustomKey");
					model.Entity<Item>().HasIndex(x => x.Name).HasDatabaseName("CustomIndex");
				}
			}
			public class Item {
				public long Id { get; set; }
				public string Name { get; set; } = "";
			}
			""");
		using var stream = new MemoryStream();
		var emitted = result.Compilation.Emit(stream);
		Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
		return Assembly.Load(stream.ToArray());
	}

	internal static DatabaseConfig Configuration(DatabaseProvider provider) {
		var external = new ExternalDatabaseConfig { Host = "localhost", Database = "test", Username = "test", Password = "secret;=\" spaced " };
		return new DatabaseConfig { MySQL = provider == DatabaseProvider.MySQL ? external : null, PostgreSQL = provider == DatabaseProvider.PostgreSQL ? external : null };
	}

	private static void WithConfig(DatabaseConfig config, Action check, bool valid = true) {
		var path = Path.Combine(Path.GetTempPath(), "pengin_config_" + Guid.NewGuid().ToString("N") + ".json");
		try {
			File.WriteAllText(path, JsonSerializer.Serialize(new { Discord = new { Token = "test" }, Databases = config }));
			Assert.Equal(valid, AppConfig.TryLoad(path, out _));
			check();
		} finally {
			File.Delete(path);
		}
	}
}
