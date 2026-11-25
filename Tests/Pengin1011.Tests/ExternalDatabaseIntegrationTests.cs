using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Pengin1011.Core;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ExternalDatabaseIntegrationTests {
	[ExternalDatabaseFact("PENGIN1011_MYSQL_TEST_CONFIG")]
	public Task MySqlCreatesUpgradesAndIsolatesTables() => RunAsync("PENGIN1011_MYSQL_TEST_CONFIG", DatabaseProvider.MySQL);

	[ExternalDatabaseFact("PENGIN1011_POSTGRESQL_TEST_CONFIG")]
	public Task PostgreSqlCreatesUpgradesAndIsolatesTables() => RunAsync("PENGIN1011_POSTGRESQL_TEST_CONFIG", DatabaseProvider.PostgreSQL);

	private static async Task RunAsync(string variable, DatabaseProvider provider) {
		var config = JsonSerializer.Deserialize<DatabaseConfig>(File.ReadAllText(Environment.GetEnvironmentVariable(variable)!))!;
		Assert.True(DatabaseOptions.TryValidate(config, out _));
		Assert.Equal(provider, DatabaseOptions.ProviderOf(config));
		using var fixture = new ModuleBuildFixture();
		var module = "Db" + Guid.NewGuid().ToString("N")[..8];
		var generated = fixture.RunDotnet(fixture.Root, "build", "Pengin1011.csproj", "-t:NewModule", "-p:Module=" + module, "-p:WithDatabase=true");
		Assert.True(generated.ExitCode == 0, generated.Output);
		var directory = Path.Combine(fixture.Root, "Modules", module);
		var project = Path.Combine(directory, module + ".csproj");
		var contextPath = Path.Combine(directory, module + "DbContext.cs");
		var source = $$"""
			using Microsoft.EntityFrameworkCore;
			using Pengin1011.Core;
			namespace Pengin1011.Modules.{{module}};
			[ModuleDatabase("{{module}}")]
			public partial class {{module}}DbContext : DbContext {
				public DbSet<Row> Items => Set<Row>();
				protected override void OnModelCreating(ModelBuilder model) {
					model.Entity<Row>().Property(row => row.Quantity).HasColumnName("quantity");
					model.Entity<Row>().ToTable("Items", table => table.HasCheckConstraint("CK_Positive", "quantity >= 0"));
				}
			}
			[ModuleDatabase("{{module}}B")]
			public partial class OtherDbContext : DbContext {
				public DbSet<Row> Items => Set<Row>();
				protected override void OnModelCreating(ModelBuilder model) {
					model.Entity<Row>().Property(row => row.Quantity).HasColumnName("quantity");
					model.Entity<Row>().ToTable("Items", table => table.HasCheckConstraint("CK_Positive", "quantity >= 0"));
				}
			}
			public class Row {
				public long Id { get; set; }
				public int Quantity { get; set; }
				public string Name { get; set; } = "";
			}
			""";
		File.WriteAllText(contextPath, source.ReplaceLineEndings("\n"));
		fixture.RestoreTools();
		void Build() {
			var result = fixture.RunDotnet(fixture.Root, "build", project, "-c", "Release");
			Assert.True(result.ExitCode == 0, result.Output);
		}
		void AddMigration(string name) {
			foreach (var context in new[] { module + "DbContext", "OtherDbContext" }) {
				fixture.AddMigration(module, context, name, provider.ToString());
			}
		}
		Build();
		AddMigration("Init");
		Build();
		var tableNames = new[] { DatabaseOptions.Prefix(module) + "Items", DatabaseOptions.HistoryTable(module), DatabaseOptions.Prefix(module + "B") + "Items", DatabaseOptions.HistoryTable(module + "B") };
		try {
			await PrepareAndCheckAsync(directory, module, config, upgrade: false);
			File.WriteAllText(contextPath, source.Replace("public long Id", "public string? Note { get; set; }\n\tpublic long Id").ReplaceLineEndings("\n"));
			Build();
			AddMigration("AddNote");
			Build();
			await PrepareAndCheckAsync(directory, module, config, upgrade: true);
		} finally {
			using var context = new FakeDbContext(DatabaseOptions.Create<FakeDbContext>(provider, DatabaseOptions.ConnectionString(config), module));
			var sql = context.GetService<ISqlGenerationHelper>();
			await context.Database.OpenConnectionAsync();
			foreach (var table in tableNames) {
				await using var command = context.Database.GetDbConnection().CreateCommand();
				command.CommandText = "DROP TABLE IF EXISTS " + sql.DelimitIdentifier(table, provider == DatabaseProvider.PostgreSQL ? "public" : null);
				await command.ExecuteNonQueryAsync();
			}
		}
	}

	private static async Task PrepareAndCheckAsync(string directory, string module, DatabaseConfig config, bool upgrade) {
		var loader = new AssemblyLoadContext(module + Guid.NewGuid().ToString("N"), isCollectible: true);
		var service = new DatabaseService();
		try {
			service.ConfigureProvider(config);
			using var assemblyStream = File.OpenRead(Path.Combine(directory, "bin", "Release", "net10.0", module + ".dll"));
			var assembly = loader.LoadFromStream(assemblyStream);
			var registrar = new DatabaseRegistrar(service.Provider);
			((IModuleDatabaseProvider)Activator.CreateInstance(assembly.GetCustomAttribute<ModuleDatabaseProviderAttribute>()!.ProviderType)!).Register(registrar);
			service.Register(assembly, registrar.Definitions);
			await service.PrepareModuleAsync(assembly, CancellationToken.None);
			foreach (var definition in registrar.Definitions) {
				Assert.Equal(DatabaseState.Prepared, service.StateOf(definition.Id));
				await using var context = definition.Create(DatabaseOptions.ConnectionString(config));
				var type = assembly.GetType($"Pengin1011.Modules.{module}.Row")!;
				if (!upgrade) {
					var row = Activator.CreateInstance(type)!;
					type.GetProperty("Name")!.SetValue(row, definition.Id);
					context.Add(row);
					await context.SaveChangesAsync();
				} else {
					var row = await context.FindAsync(type, 1L);
					Assert.NotNull(row);
					Assert.Equal(definition.Id, type.GetProperty("Name")!.GetValue(row));
					Assert.Null(type.GetProperty("Note")!.GetValue(row));
					type.GetProperty("Note")!.SetValue(row, "upgraded");
					await context.SaveChangesAsync();
					context.ChangeTracker.Clear();
					row = await context.FindAsync(type, 1L);
					Assert.Equal("upgraded", type.GetProperty("Note")!.GetValue(row));
					context.Remove(row!);
					await context.SaveChangesAsync();
					Assert.Null(await context.FindAsync(type, 1L));
				}
				Assert.Equal(upgrade ? 2 : 1, (await context.Database.GetAppliedMigrationsAsync()).Count());
			}
		} finally {
			await service.StopPreparingAsync();
			loader.Unload();
		}
	}
}
