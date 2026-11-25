using System.Data.Common;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;
using Npgsql;
using Pengin1011.Core;
using Pengin1011.Core.Logging;
using Pengin1011.Core.Modules;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ExternalDatabaseFailureIntegrationTests {
	[Theory]
	[InlineData(DatabaseProvider.MySQL, 3306)]
	[InlineData(DatabaseProvider.PostgreSQL, 5432)]
	public void ReadOnlyTargetComparisonUsesEffectiveConnectionSettings(DatabaseProvider provider, int defaultPort) {
		var config = ExternalDatabaseTests.Configuration(provider);
		foreach (var difference in new[] { "none", "port", "host", "database" }) {
			var external = new ExternalDatabaseConfig {
				Port = difference == "port" ? defaultPort + 1 : defaultPort,
				Host = difference == "host" ? "another-host" : " localhost ",
				Database = difference == "database" ? "another-database" : " test ",
				Username = "readOnly", Password = "different;=\"password",
			};
			var readOnly = new DatabaseConfig { MySQL = provider == DatabaseProvider.MySQL ? external : null, PostgreSQL = provider == DatabaseProvider.PostgreSQL ? external : null };
			Assert.Equal(difference == "none", Target(config) == Target(readOnly));
		}
	}

	private static (string Host, int Port, string? Database) Target(DatabaseConfig config) {
		var connection = DatabaseOptions.ConnectionString(config);
		if (DatabaseOptions.ProviderOf(config) == DatabaseProvider.MySQL) {
			var mysql = new MySqlConnectionStringBuilder(connection);
			return (mysql.Server, (int)mysql.Port, mysql.Database);
		}
		var postgres = new NpgsqlConnectionStringBuilder(connection);
		return (postgres.Host!, postgres.Port, postgres.Database);
	}

	[Theory]
	[InlineData(DatabaseProvider.MySQL)]
	[InlineData(DatabaseProvider.PostgreSQL)]
	public void FailureMigrationsCompileAndMatchTheirProviderWithoutConnecting(DatabaseProvider provider) {
		using var fixture = new FailureModule(provider);
		fixture.FailureSql = "SELECT 12345";
		var factoryType = fixture.Assembly.GetType(fixture.ContextType.FullName + provider + "DesignFactory")!;
		using var context = (DbContext)factoryType.GetMethod("CreateDbContext")!.Invoke(Activator.CreateInstance(factoryType), [Array.Empty<string>()])!;
		Assert.False(context.Database.HasPendingModelChanges());
		Assert.Contains("SELECT 12345", context.GetService<IMigrator>().GenerateScript());
		Assert.Contains(DatabaseOptions.Prefix(fixture.Id) + "Items", context.Database.GenerateCreateScript());
	}

	[ExternalDatabaseFact("PENGIN1011_MYSQL_TEST_CONFIG")]
	public Task MySqlFailuresBlockRuntimeWithoutFallback() => RunAsync("PENGIN1011_MYSQL_TEST_CONFIG", DatabaseProvider.MySQL);

	[ExternalDatabaseFact("PENGIN1011_POSTGRESQL_TEST_CONFIG")]
	public Task PostgreSqlFailuresBlockRuntimeWithoutFallback() => RunAsync("PENGIN1011_POSTGRESQL_TEST_CONFIG", DatabaseProvider.PostgreSQL);

	[ExternalDatabaseFact("PENGIN1011_MYSQL_TEST_CONFIG", "PENGIN1011_MYSQL_READONLY_TEST_CONFIG")]
	public Task MySqlReadOnlyAccountCannotPrepareTables() => RunAsync("PENGIN1011_MYSQL_TEST_CONFIG", DatabaseProvider.MySQL, "PENGIN1011_MYSQL_READONLY_TEST_CONFIG");

	[ExternalDatabaseFact("PENGIN1011_POSTGRESQL_TEST_CONFIG", "PENGIN1011_POSTGRESQL_READONLY_TEST_CONFIG")]
	public Task PostgreSqlReadOnlyAccountCannotPrepareTables() => RunAsync("PENGIN1011_POSTGRESQL_TEST_CONFIG", DatabaseProvider.PostgreSQL, "PENGIN1011_POSTGRESQL_READONLY_TEST_CONFIG");

	private static async Task RunAsync(string variable, DatabaseProvider provider, string? readOnlyVariable = null) {
		var config = ReadConfiguration(variable, provider);
		using var fixture = new FailureModule(provider);
		using var observer = new FakeDbContext(DatabaseOptions.Create<FakeDbContext>(provider, DatabaseOptions.ConnectionString(config), fixture.Id));
		await observer.Database.OpenConnectionAsync();
		var sql = observer.GetService<ISqlGenerationHelper>();
		var source = (config.MySQL ?? config.PostgreSQL)!;
		var scenarios = readOnlyVariable == null ? new[] { "missing", "password", "migration", "cancel", "timeout", "shutdown" } : ["permission"];
		foreach (var scenario in scenarios) {
			var attempt = config;
			var password = "wrong;\"=" + Guid.NewGuid().ToString("N");
			if (scenario is "missing" or "password") {
				var external = new ExternalDatabaseConfig {
					Host = source.Host, Port = source.Port, Username = source.Username,
					Database = scenario == "missing" ? "Missing" + Guid.NewGuid().ToString("N") : source.Database,
					Password = scenario == "password" ? password : source.Password,
				};
				attempt = new DatabaseConfig { MySQL = provider == DatabaseProvider.MySQL ? external : null, PostgreSQL = provider == DatabaseProvider.PostgreSQL ? external : null };
			} else if (scenario == "permission") {
				attempt = ReadConfiguration(readOnlyVariable!, provider);
				Assert.Equal(Target(config), Target(attempt));
				using var connectionCheck = new FakeDbContext(DatabaseOptions.Create<FakeDbContext>(provider, DatabaseOptions.ConnectionString(attempt), fixture.Id));
				await connectionCheck.Database.OpenConnectionAsync();
				await connectionCheck.Database.ExecuteSqlRawAsync("SELECT 1");
			}
			var waiting = scenario is "cancel" or "timeout" or "shutdown";
			var marker = "wait" + fixture.Id;
			fixture.FailureSql = scenario == "migration" ? "SELECT * FROM " + sql.DelimitIdentifier("Missing" + fixture.Id)
				: waiting ? (provider == DatabaseProvider.MySQL ? "SELECT SLEEP(60)" : "SELECT pg_sleep(60)") + " /* " + marker + " */" : null;
			var directory = Path.Combine(Path.GetTempPath(), "pengin_failure_" + Guid.NewGuid().ToString("N"));
			Databases.SetDataDirectoryForTest(directory);
			Databases.ConfigureProvider(attempt);
			Databases.RegisterModule(fixture.Assembly);
			InteractionHost.ResetForTest();
			var runtime = new ProbeRuntime();
			var run = new LoadedModule { Name = fixture.Id, FilePath = "", Assembly = fixture.Assembly, LoadedAt = DateTimeOffset.Now, Runtime = runtime };
			var messages = new List<string>();
			using var listener = Logger.Subscribe(entry => messages.Add(entry.Message + entry.Exception));
			var budget = DatabaseService.PrepareBudget;
			try {
				if (scenario == "timeout") DatabaseService.PrepareBudget = TimeSpan.FromSeconds(5);
				Assert.True(InteractionHost.TryBeginModuleInitialization(run));
				if (waiting) {
					await WaitForQueryAsync(observer.Database.GetDbConnection(), provider, marker);
					if (scenario == "cancel") run.Lifecycle.Cancel();
					if (scenario == "shutdown") await Databases.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
				}
				var failure = await Record.ExceptionAsync(() => run.InitTask!.WaitAsync(TimeSpan.FromSeconds(15)));
				Assert.NotNull(failure);
				Assert.IsNotType<TimeoutException>(failure);
				Assert.Equal(0, runtime.InitCount);
				Assert.Equal(DatabaseState.Failed, Databases.GetStatus(fixture.Id).State);
				Assert.False(Directory.Exists(directory));
				var expected = scenario switch {
					"password" => "ExternalDatabaseAuthentication",
					"permission" => "ExternalDatabasePermission",
					"cancel" or "shutdown" => "ExternalDatabaseCancelled",
					"timeout" => "ExternalDatabaseTimeout",
					_ => null,
				};
				var note = Assert.Single(Databases.GetStatus(fixture.Id).Notes);
				if (expected != null) Assert.Contains(L.Get(expected), note);
				var count = messages.Count;
				var cached = await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareModuleAsync(fixture.Assembly, CancellationToken.None));
				Assert.Equal(note, cached.Message);
				Assert.Equal(count, messages.Count);
				var open = typeof(Databases).GetMethod(nameof(Databases.Open))!.MakeGenericMethod(fixture.ContextType);
				Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => open.Invoke(null, null)).InnerException);
				var output = string.Join("\n", messages) + note + cached.Message;
				foreach (var secret in new[] { source.Password, password, DatabaseOptions.ConnectionString(attempt) }.Where(value => !string.IsNullOrEmpty(value))) {
					Assert.False(output.Contains(secret, StringComparison.Ordinal), "Database diagnostics exposed a connection secret.");
				}
			} finally {
				run.Lifecycle.Cancel();
				try {
					await Databases.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
				} finally {
					DatabaseService.PrepareBudget = budget;
				}
				Databases.SetDataDirectoryForTest(null);
				InteractionHost.ResetForTest();
				run.Lifecycle.Dispose();
				foreach (var name in new[] { DatabaseOptions.Prefix(fixture.Id) + "Items", DatabaseOptions.HistoryTable(fixture.Id) }) {
					await using var command = observer.Database.GetDbConnection().CreateCommand();
					command.CommandText = "DROP TABLE IF EXISTS " + sql.DelimitIdentifier(name, provider == DatabaseProvider.PostgreSQL ? "public" : null);
					await command.ExecuteNonQueryAsync();
				}
			}
		}
	}

	private static DatabaseConfig ReadConfiguration(string variable, DatabaseProvider provider) {
		var config = JsonSerializer.Deserialize<DatabaseConfig>(File.ReadAllText(Environment.GetEnvironmentVariable(variable)!))!;
		Assert.True(DatabaseOptions.TryValidate(config, out _));
		Assert.Equal(provider, DatabaseOptions.ProviderOf(config));
		return config;
	}

	private static async Task WaitForQueryAsync(DbConnection connection, DatabaseProvider provider, string marker) {
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true) {
			await using var command = connection.CreateCommand();
			command.CommandText = provider == DatabaseProvider.MySQL
				? "SELECT COUNT(*) FROM information_schema.PROCESSLIST WHERE ID <> CONNECTION_ID() AND INFO LIKE @pattern"
				: "SELECT COUNT(*) FROM pg_catalog.pg_stat_activity WHERE pid <> pg_backend_pid() AND state = 'active' AND query LIKE @pattern";
			var pattern = command.CreateParameter();
			pattern.ParameterName = "pattern";
			pattern.Value = "%" + marker + "%";
			command.Parameters.Add(pattern);
			if (Convert.ToInt64(await command.ExecuteScalarAsync(timeout.Token)) > 0) return;
			await Task.Delay(50, timeout.Token);
		}
	}

	private sealed class ProbeRuntime : IModuleRuntime {
		public int InitCount { get; private set; }
		public Task InitializeAsync(CancellationToken ct) {
			InitCount++;
			return Task.CompletedTask;
		}
		public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
	}

	private sealed class FailureModule : IDisposable {
		private readonly ModuleBuildFixture _fixture = new();
		private readonly AssemblyLoadContext _loader = new("ExternalFailure" + Guid.NewGuid().ToString("N"), isCollectible: true);
		public string Id { get; } = "Failure" + Guid.NewGuid().ToString("N")[..8];
		public Assembly Assembly { get; }
		public Type ContextType => Assembly.GetType($"Pengin1011.Modules.{Id}.{Id}DbContext")!;
		public string? FailureSql {
			set => Assembly.GetType($"Pengin1011.Modules.{Id}.FailureProbe")!.GetField("Sql")!.SetValue(null, value);
		}

		public FailureModule(DatabaseProvider provider) {
			try {
				var created = _fixture.RunDotnet(_fixture.Root, "build", "Pengin1011.csproj", "-t:NewModule", "-p:Module=" + Id, "-p:WithDatabase=true");
				Assert.True(created.ExitCode == 0, created.Output);
				var directory = Path.Combine(_fixture.Root, "Modules", Id);
				var project = Path.Combine(directory, Id + ".csproj");
				File.WriteAllText(Path.Combine(directory, Id + "DbContext.cs"), $$"""
					using Microsoft.EntityFrameworkCore;
					using Pengin1011.Core;
					namespace Pengin1011.Modules.{{Id}};
					[ModuleDatabase("{{Id}}")]
					public partial class {{Id}}DbContext : DbContext {
						public DbSet<Row> Items => Set<Row>();
					}
					public class Row { public int Id { get; set; } }
					public static class FailureProbe { public static string? Sql; }
					""".ReplaceLineEndings("\n"));
				var built = _fixture.RunDotnet(_fixture.Root, "build", project, "-c", "Release");
				Assert.True(built.ExitCode == 0, built.Output);
				_fixture.RestoreTools();
				_fixture.AddMigration(Id, Id + "DbContext", "Init", provider.ToString());
				var migration = Directory.GetFiles(Path.Combine(directory, "Migrations", Id + "DbContextMigrations", provider.ToString()), "*_Init.cs").Single();
				var source = File.ReadAllText(migration).ReplaceLineEndings("\n");
				var end = source.IndexOf("\n        }", source.IndexOf("protected override void Up", StringComparison.Ordinal), StringComparison.Ordinal);
				Assert.True(end > 0);
				source = source.Insert(end, "\n            if (global::Pengin1011.Modules." + Id + ".FailureProbe.Sql is { } sql) migrationBuilder.Sql(sql);");
				File.WriteAllText(migration, source);
				built = _fixture.RunDotnet(_fixture.Root, "build", project, "-c", "Release", "--no-restore");
				Assert.True(built.ExitCode == 0, built.Output);
				using var stream = File.OpenRead(Path.Combine(directory, "bin", "Release", "net10.0", Id + ".dll"));
				Assembly = _loader.LoadFromStream(stream);
			} catch {
				Dispose();
				throw;
			}
		}

		public void Dispose() {
			_loader.Unload();
			_fixture.Dispose();
		}
	}
}
