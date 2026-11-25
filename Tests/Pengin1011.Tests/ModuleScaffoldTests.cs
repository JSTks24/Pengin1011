using System.Runtime.Loader;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleScaffoldTests {
	[Fact]
	public void B04_ScaffoldedModule_MigratesBuildsAndPublishesInAnIsolatedCopy() {
		using var fixture = new ModuleBuildFixture(pathWithSpaces: true);
		const string moduleName = "ScaffoldProbe";
		var moduleDirectory = Path.Combine(fixture.Root, "Modules", moduleName);
		Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bin")));
		Assert.False(Directory.Exists(Path.Combine(fixture.Root, "obj")));
		var scaffold = fixture.RunDotnet(fixture.Root, "build", "Pengin1011.csproj", "-c", "Release", "-t:NewModule", $"-p:Module={moduleName}", "-p:WithDatabase=true");
		Assert.True(scaffold.ExitCode == 0, scaffold.Output);
		Assert.Contains("dotnet build Pengin1011.csproj -t:AddModuleMigration", scaffold.Output);

		foreach (var expected in new[] { $"{moduleName}.csproj", $"{moduleName}.cs", $"{moduleName}Runtime.cs", $"{moduleName}Config.cs", $"{moduleName}Options.cs", $"{moduleName}DbContext.cs", "Localization/Strings.resx", "Localization/zh-CN/Strings.resx" }) {
			Assert.True(File.Exists(Path.Combine(moduleDirectory, expected)), $"模板未生成 {expected}");
		}
		Assert.Contains($"Modules\\{moduleName}\\{moduleName}.csproj", File.ReadAllText(Path.Combine(fixture.Root, "Pengin1011.sln")));
		var contextPath = Path.Combine(moduleDirectory, $"{moduleName}DbContext.cs");
		var context = File.ReadAllText(contextPath).Replace($"public partial class {moduleName}DbContext : DbContext {{", $"public partial class {moduleName}DbContext : DbContext {{\n\tpublic DbSet<ProbeRecord> Records => Set<ProbeRecord>();\n\tprotected override void OnModelCreating(ModelBuilder model) {{\n\t\tmodel.Entity<ProbeRecord>().Property(row => row.Quantity).HasColumnName(\"quantity\").HasDefaultValue(0);\n\t\tmodel.Entity<ProbeRecord>().ToTable(\"Records\", table => table.HasCheckConstraint(\"CK_Positive\", \"quantity >= 0\"));\n\t}}");
		File.WriteAllText(contextPath, context + "\npublic sealed class ProbeRecord {\n\tpublic int Id { get; set; }\n\tpublic int Quantity { get; set; }\n\tpublic string Value { get; set; } = \"\";\n}\n");
		var project = Path.Combine(moduleDirectory, $"{moduleName}.csproj");
		var build = fixture.RunDotnet(fixture.Root, "build", project, "-c", "Release");
		Assert.True(build.ExitCode == 0, build.Output);

		fixture.RestoreTools();
		fixture.AddMigration(moduleName, moduleName + "DbContext", "Init", "SQLite");
		var migrations = Directory.GetFiles(Path.Combine(moduleDirectory, "Migrations", $"{moduleName}DbContextMigrations", "SQLite"), "*.cs");
		Assert.Contains(migrations, file => file.EndsWith("_Init.cs", StringComparison.Ordinal));
		Assert.Contains(migrations, file => file.EndsWith("_Init.Designer.cs", StringComparison.Ordinal));
		Assert.Contains(migrations, file => file.EndsWith("DbContextModelSnapshot.cs", StringComparison.Ordinal));
		foreach (var provider in new[] { "MySQL", "PostgreSQL" }) {
			fixture.AddMigration(moduleName, moduleName + "DbContext", "Init", provider);
		}
		var rebuild = fixture.RunDotnet(fixture.Root, "build", project, "-c", "Release", "--no-restore");
		Assert.True(rebuild.ExitCode == 0, rebuild.Output);

		var database = Path.Combine(moduleDirectory, "probe.db");
		var migrate = fixture.RunDotnet(moduleDirectory,
			"tool", "run", "dotnet-ef", "--", "database", "update", "--configuration", "Release", "--no-build",
			"--project", project, "--startup-project", project, "--context", $"{moduleName}DbContext", "--connection", $"Data Source={database}");
		Assert.True(migrate.ExitCode == 0, migrate.Output);
		using (var connection = new SqliteConnection($"Data Source={database};Pooling=False")) {
			connection.Open();
			using var table = connection.CreateCommand();
			table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Records'";
			Assert.Equal(1L, table.ExecuteScalar());
			using var history = connection.CreateCommand();
			history.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory";
			Assert.EndsWith("_Init", Assert.IsType<string>(history.ExecuteScalar()));
		}

		using (var connection = new SqliteConnection($"Data Source={database};Pooling=False")) {
			connection.Open();
			using var insert = connection.CreateCommand();
			insert.CommandText = "INSERT INTO Records (Value) VALUES ('preserved-v1')";
			insert.ExecuteNonQuery();
		}
		File.WriteAllText(contextPath, File.ReadAllText(contextPath).Replace("public int Id { get; set; }", "public int Id { get; set; }\n\tpublic string? Note { get; set; }"));
		var v2Build = fixture.RunDotnet(fixture.Root, "build", project, "-c", "Release");
		Assert.True(v2Build.ExitCode == 0, v2Build.Output);
		fixture.AddMigration(moduleName, moduleName + "DbContext", "AddNote", "SQLite");
		foreach (var provider in new[] { "MySQL", "PostgreSQL" }) {
			fixture.AddMigration(moduleName, moduleName + "DbContext", "AddNote", provider);
		}
		var v2Rebuild = fixture.RunDotnet(fixture.Root, "build", project, "-c", "Release");
		Assert.True(v2Rebuild.ExitCode == 0, v2Rebuild.Output);
		var loadContext = new AssemblyLoadContext("ScaffoldDatabaseProbe", isCollectible: true);
		try {
			using var assemblyStream = File.OpenRead(Path.Combine(moduleDirectory, "bin", "Release", "net10.0", moduleName + ".dll"));
			var assembly = loadContext.LoadFromStream(assemblyStream);
			foreach (var provider in new[] { "SQLite", "MySQL", "PostgreSQL" }) {
				var suffix = provider == "SQLite" ? "" : provider;
				var factoryType = assembly.GetType($"Pengin1011.Modules.{moduleName}.{moduleName}DbContext{suffix}DesignFactory")!;
				using var contextProbe = (DbContext)factoryType.GetMethod("CreateDbContext")!.Invoke(Activator.CreateInstance(factoryType), [Array.Empty<string>()])!;
				Assert.Equal(2, contextProbe.Database.GetMigrations().Count());
				Assert.False(contextProbe.Database.HasPendingModelChanges());
				var script = contextProbe.GetService<IMigrator>().GenerateScript();
				Assert.Contains(provider == "SQLite" ? "Records" : $"PG1011_{moduleName}_Records", script);
				Assert.Contains("Note", script);
				Assert.Contains(provider == "SQLite" ? "CK_Positive" : $"PG1011_{moduleName}_CK_Positive", script);
			}
		} finally {
			loadContext.Unload();
		}
		var v2Update = fixture.RunDotnet(moduleDirectory,
			"tool", "run", "dotnet-ef", "--", "database", "update", "--configuration", "Release", "--no-build",
			"--project", project, "--startup-project", project, "--context", $"{moduleName}DbContext", "--connection", $"Data Source={database}");
		Assert.True(v2Update.ExitCode == 0, v2Update.Output);
		using (var connection = new SqliteConnection($"Data Source={database};Pooling=False")) {
			connection.Open();
			using var row = connection.CreateCommand();
			row.CommandText = "SELECT Value FROM Records WHERE Note IS NULL";
			Assert.Equal("preserved-v1", row.ExecuteScalar());
			using var history = connection.CreateCommand();
			history.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
			Assert.Equal(2L, history.ExecuteScalar());
		}

		var fakeModuleDirectory = Path.Combine(fixture.Root, "bin", "Release", "net10.0", "module");
		var fakeBuild = fixture.RunDotnet(fixture.Root, "build", "Tests/FakeModule/FakeModule.csproj", "-c", "Release", $"-p:ModuleOutputDir={fakeModuleDirectory}");
		Assert.True(fakeBuild.ExitCode == 0, fakeBuild.Output);
		Assert.True(File.Exists(Path.Combine(fixture.Root, "bin", "Release", "net10.0", "module", "FakeModule.dll")));
		var publishDirectory = Path.Combine(fixture.Root, "bin", "Release", "net10.0", "publish");
		var staleModules = Path.Combine(publishDirectory, "module");
		Directory.CreateDirectory(staleModules);
		File.Copy(Path.Combine(fakeModuleDirectory, "FakeModule.dll"), Path.Combine(staleModules, "FakeModule.dll"));
		File.Copy(Path.Combine(fakeModuleDirectory, "FakeModule.dll"), Path.Combine(staleModules, "RemovedModule.dll"));
		var publish = fixture.RunDotnet(fixture.Root, "build", "Pengin1011.csproj", "-c", "Release", "-t:PublishAll");
		Assert.True(publish.ExitCode == 0, publish.Output);
		foreach (var expected in new[] { "Pengin1011.dll", "Pengin1011.deps.json", "Pengin1011.runtimeconfig.json", "MySql.EntityFrameworkCore.dll", "MySql.Data.dll", "Npgsql.EntityFrameworkCore.PostgreSQL.dll", "Npgsql.dll", "module/AIPresetAdmin.dll", $"module/{moduleName}.dll" }) {
			Assert.True(File.Exists(Path.Combine(publishDirectory, expected)), $"发布缺少 {expected}");
		}
		var publishedModules = Directory.GetFiles(Path.Combine(publishDirectory, "module"), "*.dll").Select(file => Path.GetFileName(file)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
		Assert.Equal(["AIPresetAdmin.dll", $"{moduleName}.dll"], publishedModules);
		Assert.DoesNotContain(Directory.GetFiles(publishDirectory, "*.dll", SearchOption.AllDirectories), file => Path.GetFileName(file).StartsWith("Fake", StringComparison.Ordinal) || Path.GetFileName(file) is "Pengin1011.Tests.dll" or "testhost.dll" || Path.GetFileName(file).StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
		Assert.False(Directory.Exists(Path.Combine(publishDirectory, "config")));
		Assert.False(Directory.Exists(Path.Combine(publishDirectory, "data")));
	}

	[Fact]
	public void ScaffoldWithoutDatabaseHasNoContextAndBuilds() {
		using var fixture = new ModuleBuildFixture(pathWithSpaces: true);
		var result = fixture.RunDotnet(fixture.Root, "build", "Pengin1011.csproj", "-t:NewModule", "-p:Module=NoDatabase");
		Assert.True(result.ExitCode == 0, result.Output);
		Assert.Contains("dotnet build Pengin1011.csproj -t:BuildModule", result.Output);
		Assert.DoesNotContain("AddModuleMigration", result.Output);
		Assert.False(File.Exists(Path.Combine(fixture.Root, "Modules", "NoDatabase", "NoDatabaseDbContext.cs")));
		var runtime = File.ReadAllText(Path.Combine(fixture.Root, "Modules", "NoDatabase", "NoDatabaseRuntime.cs"));
		Assert.DoesNotContain("Databases.", runtime);
		var build = fixture.RunDotnet(fixture.Root, "build", "Modules/NoDatabase/NoDatabase.csproj", "-c", "Release");
		Assert.True(build.ExitCode == 0, build.Output);
	}

	[Fact]
	public void FailedScaffoldCanBeRetriedAndPreservesExistingModules() {
		using var fixture = new ModuleBuildFixture(pathWithSpaces: true);
		var directory = Path.Combine(fixture.Root, "Modules", "RetryProbe");
		var solution = Path.Combine(fixture.Root, "Pengin1011.sln");
		var original = File.ReadAllBytes(solution);
		var template = Path.Combine(fixture.Root, "BuildTemplates", "ModuleOptions.tpl");
		var contents = File.ReadAllBytes(template);
		(int ExitCode, string Output) Create() => fixture.RunDotnet(fixture.Root, "build", "Pengin1011.csproj", "-t:NewModule", "-p:Module=RetryProbe");

		File.Delete(template);
		Assert.NotEqual(0, Create().ExitCode);
		Assert.False(Directory.Exists(directory));
		Assert.Equal(original, File.ReadAllBytes(solution));
		File.WriteAllBytes(template, contents);
		File.WriteAllText(solution, "invalid solution");
		Assert.NotEqual(0, Create().ExitCode);
		Assert.False(Directory.Exists(directory));
		Assert.Equal("invalid solution", File.ReadAllText(solution));
		Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(directory)!, "RetryProbe.*.tmp"));

		File.WriteAllBytes(solution, original);
		using (File.Open(solution + ".scaffold.lock", FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
			Assert.NotEqual(0, Create().ExitCode);
			Assert.True(File.Exists(solution + ".scaffold.lock"));
			Assert.False(Directory.Exists(directory));
			Assert.Equal(original, File.ReadAllBytes(solution));
		}
		File.Delete(solution + ".scaffold.lock");
		var retry = Create();
		Assert.True(retry.ExitCode == 0, retry.Output);
		var options = Path.Combine(directory, "RetryProbeOptions.cs");
		File.WriteAllText(options, "existing work");
		Assert.NotEqual(0, Create().ExitCode);
		Assert.Equal("existing work", File.ReadAllText(options));
	}

}
