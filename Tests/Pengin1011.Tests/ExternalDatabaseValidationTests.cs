using System.Net.Sockets;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MySql.Data.MySqlClient;
using Npgsql;
using Pengin1011.Core;
using Pengin1011.Core.Logging;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ExternalDatabaseValidationTests {
	[Theory]
	[InlineData(DatabaseProvider.MySQL)]
	[InlineData(DatabaseProvider.PostgreSQL)]
	public async Task PreparationAfterShutdownReportsOneSafeCancellationInStatusAndLog(DatabaseProvider provider) {
		var directory = Path.Combine(Path.GetTempPath(), "pengin_stopped_" + Guid.NewGuid().ToString("N"));
		Databases.SetDataDirectoryForTest(directory);
		var messages = new List<LogEntry>();
		using var listener = Logger.Subscribe(entry => { if (entry.Source == typeof(DatabaseService)) messages.Add(entry); });
		try {
			var config = ExternalDatabaseTests.Configuration(provider);
			Databases.ConfigureProvider(config);
			var registrar = new DatabaseRegistrar(provider);
			var calls = 0;
			registrar.Add<FakeDbContext>("Stopped", (_, _) => {
				calls++;
				throw new InvalidOperationException("The context must not be created after shutdown.");
			});
			Databases.RegisterForTest(registrar);
			await Databases.ShutdownAsync();
			for (var attempt = 0; attempt < 2; attempt++) {
				var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareModuleAsync(typeof(Databases).Assembly, CancellationToken.None));
				var status = Databases.GetStatus("Stopped");
				Assert.Equal(DatabaseState.Failed, status.State);
				Assert.Equal(error.Message, Assert.Single(status.Notes));
				Assert.Contains(L.Get("ExternalDatabaseCancelled"), error.Message);
				Assert.Contains(error.Message, await DatabaseCommands.ExecuteAsync(["db", "status", "Stopped"]));
				var logged = Assert.Single(messages);
				Assert.Contains(error.Message, logged.Message);
				Assert.Null(logged.Exception);
				Assert.DoesNotContain((config.MySQL ?? config.PostgreSQL)!.Password!, logged.Message);
				Assert.DoesNotContain(DatabaseOptions.ConnectionString(config), logged.Message);
			}
			Assert.Equal(0, calls);
			Assert.False(Directory.Exists(directory));
		} finally {
			Databases.SetDataDirectoryForTest(null);
		}
	}

	[Theory]
	[InlineData(DatabaseProvider.SQLite)]
	[InlineData(DatabaseProvider.MySQL)]
	[InlineData(DatabaseProvider.PostgreSQL)]
	public void DeclarationUnderscoresAreRejectedOnlyForExternalBackends(DatabaseProvider provider) {
		var assembly = GenerateModel("", "Foo_Bar");
		var registrar = new DatabaseRegistrar(provider);
		var generated = (IModuleDatabaseProvider)Activator.CreateInstance(assembly.GetCustomAttribute<ModuleDatabaseProviderAttribute>()!.ProviderType)!;
		if (provider == DatabaseProvider.SQLite) {
			generated.Register(registrar);
			using var design = DesignContext(assembly, "FirstContext", provider);
			Assert.Contains("Item_Rows", design.Database.GenerateCreateScript());
			Assert.Equal("Foo_Bar", registrar.Definitions[0].Id);
		} else {
			Assert.Equal(L.Get("ExternalDatabaseIdentifierInvalid"), Assert.Throws<InvalidOperationException>(() => generated.Register(registrar)).Message);
			var error = Assert.Throws<TargetInvocationException>(() => DesignContext(assembly, "FirstContext", provider));
			Assert.Equal(L.Get("ExternalDatabaseIdentifierInvalid"), error.InnerException!.Message);
			Assert.Empty(registrar.Definitions);
		}
	}

	[Theory]
	[InlineData(DatabaseProvider.MySQL)]
	[InlineData(DatabaseProvider.PostgreSQL)]
	public void CheckConstraintsAndTphModelsAreIsolatedAndMatchDesignTime(DatabaseProvider provider) {
		var assembly = GenerateModel("""
			model.Entity<Item>().ToTable("Item_Rows", table => table.HasCheckConstraint("CK_Positive", "quantity >= 0"));
			model.Entity<SpecialItem>();
			""");
		var registrar = new DatabaseRegistrar(provider);
		((IModuleDatabaseProvider)Activator.CreateInstance(assembly.GetCustomAttribute<ModuleDatabaseProviderAttribute>()!.ProviderType)!).Register(registrar);
		foreach (var definition in registrar.Definitions) {
			using var runtime = definition.Create(DatabaseOptions.ConnectionString(ExternalDatabaseTests.Configuration(provider)));
			using var design = DesignContext(assembly, definition.ContextType.Name, provider);
			var model = runtime.GetService<IDesignTimeModel>().Model;
			var constraint = Assert.Single(model.GetEntityTypes().SelectMany(entity => entity.GetDeclaredCheckConstraints()));
			Assert.Equal(DatabaseOptions.Prefix(definition.Id) + "CK_Positive", constraint.Name);
			Assert.Equal("quantity >= 0", constraint.Sql);
			Assert.Single(model.GetRelationalModel().Tables);
			Assert.All(model.GetEntityTypes(), entity => Assert.Equal(DatabaseOptions.Prefix(definition.Id) + "Item_Rows", entity.GetTableName()));
			Assert.Equal(design.Database.GenerateCreateScript(), runtime.Database.GenerateCreateScript());
			Assert.Contains(constraint.Name!, runtime.Database.GenerateCreateScript());
		}
	}

	[Theory]
	[InlineData(DatabaseProvider.MySQL, "Split")]
	[InlineData(DatabaseProvider.PostgreSQL, "Split")]
	[InlineData(DatabaseProvider.MySQL, "Tpt")]
	[InlineData(DatabaseProvider.PostgreSQL, "Tpt")]
	[InlineData(DatabaseProvider.MySQL, "ImplicitTpt")]
	[InlineData(DatabaseProvider.PostgreSQL, "ImplicitTpt")]
	[InlineData(DatabaseProvider.MySQL, "Tpc")]
	[InlineData(DatabaseProvider.PostgreSQL, "Tpc")]
	public async Task UnsupportedMappingsFailBeforeConnectingAndKeepSafeStatus(DatabaseProvider provider, string mapping) {
		var configuration = mapping switch {
			"Split" => "model.Entity<Item>().SplitToTable(\"Details\", table => { table.Property(item => item.Id); table.Property(item => item.Detail); });",
			"Tpt" => "model.Entity<Item>().UseTptMappingStrategy(); model.Entity<SpecialItem>().ToTable(\"Details\");",
			"ImplicitTpt" => "model.Entity<SpecialItem>().ToTable(\"Details\");",
			_ => "model.Entity<Item>().UseTpcMappingStrategy(); model.Entity<SpecialItem>().ToTable(\"Details\");",
		};
		var assembly = GenerateModel(configuration);
		using var design = DesignContext(assembly, "FirstContext", provider);
		var expected = L.Get("ExternalDatabase" + (mapping == "ImplicitTpt" ? "Tpt" : mapping) + "Unsupported");
		Assert.Equal(expected, Assert.Throws<InvalidOperationException>(() => design.Database.GenerateCreateScript()).Message);
		using var sqlite = DesignContext(assembly, "FirstContext", DatabaseProvider.SQLite);
		Assert.Contains("Details", sqlite.Database.GenerateCreateScript());
		var directory = Path.Combine(Path.GetTempPath(), "pengin_mapping_" + Guid.NewGuid().ToString("N"));
		Databases.SetDataDirectoryForTest(directory);
		var messages = new List<string>();
		using var listener = Logger.Subscribe(entry => messages.Add(entry.Message + entry.Exception));
		try {
			Databases.ConfigureProvider(ExternalDatabaseTests.Configuration(provider));
			Databases.RegisterModule(assembly);
			var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Databases.PrepareModuleAsync(assembly, CancellationToken.None));
			Assert.Contains(expected, error.Message);
			var status = Databases.GetStatus("First");
			Assert.Equal(DatabaseState.Failed, status.State);
			Assert.Equal(error.Message, Assert.Single(status.Notes));
			Assert.Contains(messages, message => message.Contains(error.Message, StringComparison.Ordinal));
			Assert.DoesNotContain("secret", string.Join("\n", messages), StringComparison.Ordinal);
			Assert.False(Directory.Exists(directory));
		} finally {
			await Databases.ShutdownAsync();
			Databases.SetDataDirectoryForTest(null);
		}
	}

	[Theory]
	[InlineData(1049, "ExternalDatabaseMissing")]
	[InlineData(1045, "ExternalDatabaseAuthentication")]
	[InlineData(1044, "ExternalDatabasePermission")]
	[InlineData(1142, "ExternalDatabasePermission")]
	[InlineData(1143, "ExternalDatabasePermission")]
	[InlineData(1227, "ExternalDatabasePermission")]
	[InlineData(1042, "ExternalDatabaseConnection")]
	[InlineData(1205, "ExternalDatabaseTimeout")]
	[InlineData(1317, "ExternalDatabaseCancelled")]
	[InlineData(1064, "ExternalDatabaseOperation")]
	public void MySqlErrorsKeepCodesWithoutMessages(int code, string resource) {
		var error = (MySqlException)Activator.CreateInstance(typeof(MySqlException), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			null, ["secret;Password=\"private\"", code], null)!;
		Assert.Equal(string.Format(L.Get("ExternalDatabaseFailed"), L.Get(resource), "MySQL " + code), DatabaseOptions.Failure(new InvalidOperationException("secret", error)));
		var wrapper = (MySqlException)Activator.CreateInstance(typeof(MySqlException), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			null, ["secret", true, error], null)!;
		Assert.Equal(0, wrapper.Number);
		Assert.Equal(DatabaseOptions.Failure(error), DatabaseOptions.Failure(wrapper));
	}

	[Theory]
	[InlineData("3D000", "ExternalDatabaseMissing")]
	[InlineData("28000", "ExternalDatabaseAuthentication")]
	[InlineData("28P01", "ExternalDatabaseAuthentication")]
	[InlineData("42501", "ExternalDatabasePermission")]
	[InlineData("08006", "ExternalDatabaseConnection")]
	[InlineData("57014", "ExternalDatabaseCancelled")]
	[InlineData("42601", "ExternalDatabaseOperation")]
	public void PostgreSqlErrorsKeepCodesWithoutMessages(string code, string resource) {
		var error = new PostgresException("secret;Password=\"private\"", "ERROR", "ERROR", code, detail: "secret");
		Assert.Equal(string.Format(L.Get("ExternalDatabaseFailed"), L.Get(resource), "SQLSTATE " + code), DatabaseOptions.Failure(new InvalidOperationException("secret", error)));
	}

	[Fact]
	public void LocalValidationAndTransportErrorsRemainSafeAndSpecific() {
		Assert.Contains(L.Get("ExternalDatabaseConnection"), DatabaseOptions.Failure(new NpgsqlException("secret", new SocketException())));
		Assert.Contains(L.Get("ExternalDatabaseTimeout"), DatabaseOptions.Failure(new NpgsqlException("secret", new TimeoutException("secret"))));
		Assert.Contains(L.Get("ExternalDatabaseCancelled"), DatabaseOptions.Failure(new OperationCanceledException("secret")));
		Assert.Contains(L.Get("ExternalDatabaseTimeout"), DatabaseOptions.Failure(new OperationCanceledException("secret"), timedOut: true));
		Assert.Contains(L.Get("ExternalDatabaseCancelled"), DatabaseOptions.Failure(new TimeoutException("secret"), cancelled: true, timedOut: true));
		Assert.Equal(string.Format(L.Get("ExternalDatabaseFailed"), L.Get("ExternalDatabaseOperation"), "InvalidOperationException"), DatabaseOptions.Failure(new InvalidOperationException("secret")));
		foreach (var key in new[] { "ExternalDatabaseIdentifierInvalid", "DatabaseIdentifierTooLong", "ExternalDatabaseSplitUnsupported", "ExternalDatabaseTptUnsupported", "ExternalDatabaseTpcUnsupported" }) {
			Assert.Contains(L.Get(key), DatabaseOptions.Failure(new TargetInvocationException(new InvalidOperationException(L.Get(key)))));
		}
		Assert.DoesNotContain("secret", DatabaseOptions.Failure(new PostgresException("secret", "ERROR", "ERROR", "secret")), StringComparison.Ordinal);
	}

	internal static Assembly GenerateModel(string configuration, string firstId = "First") {
		return ExternalDatabaseTests.GenerateModule($$"""
			using Microsoft.EntityFrameworkCore;
			using Pengin1011.Core;
			namespace Probe;
			[ModuleDatabase("{{firstId}}")]
			public partial class FirstContext : DbContext {
				public DbSet<Item> Item_Rows => Set<Item>();
				protected override void OnModelCreating(ModelBuilder model) {
					model.Entity<Item>().Property(item => item.Quantity).HasColumnName("quantity");
					{{configuration}}
				}
			}
			[ModuleDatabase("FirstB")]
			public partial class SecondContext : DbContext {
				public DbSet<Item> Item_Rows => Set<Item>();
				protected override void OnModelCreating(ModelBuilder model) {
					model.Entity<Item>().Property(item => item.Quantity).HasColumnName("quantity");
					{{configuration}}
				}
			}
			public class Item {
				public int Id { get; set; }
				public int Quantity { get; set; }
				public string Detail { get; set; } = "";
			}
			public class SpecialItem : Item {
				public int Rating { get; set; }
			}
			""");
	}

	private static DbContext DesignContext(Assembly assembly, string context, DatabaseProvider provider) {
		var type = assembly.GetType("Probe." + context + (provider == DatabaseProvider.SQLite ? "" : provider.ToString()) + "DesignFactory")!;
		return (DbContext)type.GetMethod("CreateDbContext")!.Invoke(Activator.CreateInstance(type), [Array.Empty<string>()])!;
	}
}
