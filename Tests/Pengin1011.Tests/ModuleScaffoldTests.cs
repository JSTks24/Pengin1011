using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleScaffoldTests {
	[Fact]
	public void B04_ScaffoldedModule_MigratesBuildsAndPublishesInAnIsolatedCopy() {
		using var fixture = new ModuleBuildFixture();
		const string moduleName = "ScaffoldProbe";
		var moduleDirectory = Path.Combine(fixture.Root, "Modules", moduleName);
		Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bin")));
		Assert.False(Directory.Exists(Path.Combine(fixture.Root, "obj")));
		var scaffold = fixture.RunDotnet(fixture.Root, "build", "Pengin1011.csproj", "-c", "Release", "-t:NewModule", $"-p:Module={moduleName}");
		Assert.True(scaffold.ExitCode == 0, scaffold.Output);

		foreach (var expected in new[] { $"{moduleName}.csproj", $"{moduleName}.cs", $"{moduleName}Runtime.cs", $"{moduleName}Config.cs", $"{moduleName}DbContext.cs", "Localization/Strings.resx", "Localization/zh-CN/Strings.resx" }) {
			Assert.True(File.Exists(Path.Combine(moduleDirectory, expected)), $"模板未生成 {expected}");
		}
		Assert.Contains($"Modules\\{moduleName}\\{moduleName}.csproj", File.ReadAllText(Path.Combine(fixture.Root, "Pengin1011.sln")));
		var contextPath = Path.Combine(moduleDirectory, $"{moduleName}DbContext.cs");
		var context = File.ReadAllText(contextPath).Replace($"public class {moduleName}DbContext : DbContext {{", $"public class {moduleName}DbContext : DbContext {{\n\tpublic DbSet<ProbeRecord> Records => Set<ProbeRecord>();");
		File.WriteAllText(contextPath, context + "\npublic sealed class ProbeRecord {\n\tpublic int Id { get; set; }\n\tpublic string Value { get; set; } = \"\";\n}\n");
		var project = Path.Combine(moduleDirectory, $"{moduleName}.csproj");
		var build = fixture.RunDotnet(fixture.Root, "build", project, "-c", "Release");
		Assert.True(build.ExitCode == 0, build.Output);

		var efTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", OperatingSystem.IsWindows() ? "dotnet-ef.exe" : "dotnet-ef");
		Assert.True(File.Exists(efTool), $"EF 迁移验收要求全局工具：{efTool}");
		var migration = fixture.Run(efTool, moduleDirectory, [
			"migrations", "add", "Init", "--configuration", "Release", "--no-build",
			"--project", project, "--startup-project", project, "--context", $"{moduleName}DbContext", "--output-dir", "Migrations",
		]);
		Assert.True(migration.ExitCode == 0, migration.Output);
		var migrations = Directory.GetFiles(Path.Combine(moduleDirectory, "Migrations"), "*.cs");
		Assert.Contains(migrations, file => file.EndsWith("_Init.cs", StringComparison.Ordinal));
		Assert.Contains(migrations, file => file.EndsWith("_Init.Designer.cs", StringComparison.Ordinal));
		Assert.Contains(migrations, file => file.EndsWith("DbContextModelSnapshot.cs", StringComparison.Ordinal));
		var rebuild = fixture.RunDotnet(fixture.Root, "build", project, "-c", "Release", "--no-restore");
		Assert.True(rebuild.ExitCode == 0, rebuild.Output);

		var database = Path.Combine(moduleDirectory, "probe.db");
		var migrate = fixture.Run(efTool, moduleDirectory, [
			"database", "update", "--configuration", "Release", "--no-build",
			"--project", project, "--startup-project", project, "--context", $"{moduleName}DbContext", "--connection", $"Data Source={database}",
		]);
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
		foreach (var expected in new[] { "Pengin1011.dll", "Pengin1011.deps.json", "Pengin1011.runtimeconfig.json", "module/AIPresetAdmin.dll", $"module/{moduleName}.dll" }) {
			Assert.True(File.Exists(Path.Combine(publishDirectory, expected)), $"发布缺少 {expected}");
		}
		var publishedModules = Directory.GetFiles(Path.Combine(publishDirectory, "module"), "*.dll").Select(file => Path.GetFileName(file)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
		Assert.Equal(["AIPresetAdmin.dll", $"{moduleName}.dll"], publishedModules);
		Assert.DoesNotContain(Directory.GetFiles(publishDirectory, "*.dll", SearchOption.AllDirectories), file => Path.GetFileName(file).StartsWith("Fake", StringComparison.Ordinal) || Path.GetFileName(file) is "Pengin1011.Tests.dll" or "testhost.dll" || Path.GetFileName(file).StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
		Assert.False(Directory.Exists(Path.Combine(publishDirectory, "config")));
		Assert.False(Directory.Exists(Path.Combine(publishDirectory, "data")));
	}

	[Fact]
	public void B05_TemplateUsesThePrepareContractNotTheRemovedAutoMigratingOpen() {
		var runtime = File.ReadAllText(Path.Combine(ModuleBuildFixture.RepositoryRoot, "BuildTemplates", "ModuleRuntime.tpl"));
		Assert.Contains("Databases.PrepareAsync<", runtime);
		Assert.DoesNotContain("Databases.Open<", runtime);
		var context = File.ReadAllText(Path.Combine(ModuleBuildFixture.RepositoryRoot, "BuildTemplates", "ModuleDbContext.tpl"));
		Assert.Contains("DbContextOptions<", context);
		Assert.Contains("IDesignTimeDbContextFactory", context);
		Assert.Contains("options.IsConfigured", context);
	}
}

internal sealed class ModuleBuildFixture : IDisposable {
	public string Root { get; } = Path.Combine(Path.GetTempPath(), $"pengin1011_build_{Guid.NewGuid():N}");

	public static string RepositoryRoot {
		get {
			for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent) {
				if (File.Exists(Path.Combine(current.FullName, "Pengin1011.csproj"))) return current.FullName;
			}
			throw new InvalidOperationException("未找到仓库根（Pengin1011.csproj）");
		}
	}

	public ModuleBuildFixture() {
		Directory.CreateDirectory(Root);
		foreach (var file in new[] { "Program.cs", "Pengin1011.csproj", "Pengin1011.sln", "Directory.Build.targets" }) File.Copy(Path.Combine(RepositoryRoot, file), Path.Combine(Root, file));
		foreach (var directory in new[] { "BuildTemplates", "Cli", "Core", "Generator", "Helper", "Modules", "Resources", "Services", "Tests", "tools" }) CopySources(Path.Combine(RepositoryRoot, directory), Path.Combine(Root, directory));
	}

	public string CreateDirectory(string name) {
		var directory = Path.Combine(Root, "Tests", "_Build", name);
		Directory.CreateDirectory(directory);
		return directory;
	}

	public (int ExitCode, string Output) RunDotnet(string workingDirectory, params string[] arguments) {
		if (arguments[0] is "build" or "publish" or "msbuild") arguments = [.. arguments, "--nologo", "-v:quiet", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false"];
		return Run("dotnet", workingDirectory, arguments);
	}

	public (int ExitCode, string Output) Run(string executable, string workingDirectory, string[] arguments) {
		var startInfo = new ProcessStartInfo(executable) {
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			UseShellExecute = false,
		};
		startInfo.Environment.Clear();
		foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "DOTNET_ROOT", "DOTNET_ROOT(x86)", "NUGET_PACKAGES" }) {
			var value = Environment.GetEnvironmentVariable(name);
			if (value != null) startInfo.Environment[name] = value;
		}
		startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(Root, ".dotnet-home");
		startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
		startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
		startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
		startInfo.Environment["UseSharedCompilation"] = "false";
		foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{executable} 启动失败");
		var stdout = process.StandardOutput.ReadToEndAsync();
		var stderr = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(300_000)) {
			process.Kill(true);
			process.WaitForExit();
			return (-1, $"{executable} timed out\n{stdout.GetAwaiter().GetResult()}{stderr.GetAwaiter().GetResult()}");
		}
		return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
	}

	private static void CopySources(string source, string destination) {
		Directory.CreateDirectory(destination);
		foreach (var file in Directory.EnumerateFiles(source)) {
			if (Path.GetExtension(file) is ".cs" or ".csproj" or ".sln" or ".props" or ".targets" or ".tpl" or ".resx" or ".md") File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
		}
		foreach (var directory in Directory.EnumerateDirectories(source)) {
			if (Path.GetFileName(directory) is "bin" or "obj" or ".git" or ".vs" or "config" or "data" or "runtime") continue;
			CopySources(directory, Path.Combine(destination, Path.GetFileName(directory)));
		}
	}

	public void Dispose() {
		SqliteConnection.ClearAllPools();
		Directory.Delete(Root, true);
	}
}
