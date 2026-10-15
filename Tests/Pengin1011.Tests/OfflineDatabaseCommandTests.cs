using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pengin1011.Core;
using Pengin1011.Core.Localization;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class OfflineDatabaseCommandTests {
	[Fact]
	public async Task D33_InternalVersion_ReportsRuntimePragmasWithoutCredentials() {
		using var runtime = new IsolatedHost();

		var result = await runtime.RunAsync("db version\nexit\n");

		Assert.Equal(1, result.ExitCode);
		Assert.Contains(Text("MaintenanceConsole"), result.Output);
		Assert.Contains("SQLite", result.Output);
		Assert.Contains("journal_mode=", result.Output);
		Assert.Contains("synchronous=", result.Output);
		Assert.Contains("foreign_keys=", result.Output);
		Assert.False(File.Exists(Path.Combine(runtime.AppDirectory, "config", "aipresets.json")));
	}

	[Fact]
	public async Task D22_InternalStatusAndBackups_WorkWithoutDiscordOrAiConfiguration() {
		using var runtime = new IsolatedHost();

		var result = await runtime.RunAsync("db status Absent\ndb backups Absent\nexit\n");

		Assert.Equal(1, result.ExitCode);
		Assert.Contains("Absent", result.Output);
		Assert.Contains(nameof(DatabaseState.NotPresent), result.Output);
		Assert.DoesNotContain(Text("DbInternalUsage"), result.Output);
		Assert.False(File.Exists(Path.Combine(runtime.AppDirectory, "config", "aipresets.json")));
	}

	[Fact]
	public async Task DefaultFirstStartup_CreatesDefaultDataAndKeepsMaintenanceTerminalAvailable() {
		using var runtime = new IsolatedHost(writeConfig: false);

		var result = await runtime.RunAsync("db status Absent\nexit\n");

		Assert.Equal(1, result.ExitCode);
		Assert.True(File.Exists(runtime.ConfigPath));
		Assert.True(Directory.Exists(runtime.DefaultDataDirectory));
		Assert.Contains(runtime.DefaultDataDirectory, result.Output, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Absent", result.Output);
		Assert.Contains(Text("MaintenanceConsole"), result.Output);
	}

	[Fact]
	public async Task MalformedConfig_ExposesVersionButDoesNotGuessDataDirectory() {
		using var runtime = new IsolatedHost();
		File.WriteAllText(runtime.ConfigPath, "{ invalid");

		var result = await runtime.RunAsync("db version\ndb status Absent\nexit\n");

		Assert.Equal(1, result.ExitCode);
		Assert.Contains("SQLite", result.Output);
		Assert.Contains(Text("ConfigPathsUnavailable"), result.Output);
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

	[Fact]
	public async Task MalformedConfig_WithExplicitDirectory_AllowsLocalMaintenance() {
		using var runtime = new IsolatedHost();
		File.WriteAllText(runtime.ConfigPath, "{ invalid");
		var selected = Path.Combine(runtime.RootDirectory, "explicit data");
		Directory.CreateDirectory(selected);

		var result = await runtime.RunAsync("db status ExplicitProbe\nexit\n", ["--data-dir", selected]);

		Assert.Equal(1, result.ExitCode);
		Assert.Contains(selected, result.Output, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("ExplicitProbe", result.Output);
		Assert.DoesNotContain(Text("ConfigPathsUnavailable"), result.Output);
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	public async Task BlankExplicitDirectory_IsRejectedBeforeConfigurationIsRead(string directory) {
		using var runtime = new IsolatedHost(writeConfig: false);

		var result = await runtime.RunAsync("", ["--data-dir", directory]);

		Assert.Equal(2, result.ExitCode);
		Assert.Contains(Text("StartupArgumentsInvalid"), result.Output);
		Assert.False(File.Exists(runtime.ConfigPath));
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

	[Fact]
	public async Task D22_ExplicitMissingDirectory_IsRejectedWithoutDefaultFallback() {
		using var runtime = new IsolatedHost();
		var missing = Path.Combine(runtime.RootDirectory, "missing explicit data");

		var result = await runtime.RunAsync("db status Absent\ndb version\nexit\n", ["--data-dir", missing]);

		Assert.Equal(1, result.ExitCode);
		Assert.Contains(missing, result.Output, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(Text("ConfigPathsUnavailable"), result.Output);
		Assert.Contains("SQLite", result.Output);
		Assert.False(Directory.Exists(missing));
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

	[Fact]
	public async Task D22_ExplicitCreateDirectory_AllowsFirstTimeMaintenance() {
		using var runtime = new IsolatedHost();
		var fresh = Path.Combine(runtime.RootDirectory, "fresh explicit data");

		var result = await runtime.RunAsync("db status Absent\nexit\n", ["--data-dir", fresh, "--create-data-dir"]);

		Assert.Equal(1, result.ExitCode);
		Assert.True(Directory.Exists(fresh));
		Assert.Contains(fresh, result.Output, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Absent", result.Output);
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

	[Fact]
	public async Task ConfiguredRelativeDirectory_IsResolvedFromProgramDirectoryEvenWithoutToken() {
		using var runtime = new IsolatedHost(configuredDirectory: "configured storage");
		var configured = Path.Combine(runtime.AppDirectory, "configured storage");
		Directory.CreateDirectory(configured);

		var result = await runtime.RunAsync("db status ConfigProbe\nexit\n");

		Assert.Equal(1, result.ExitCode);
		Assert.Contains(configured, result.Output, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("ConfigProbe", result.Output);
		Assert.False(Directory.Exists(Path.Combine(runtime.WorkingDirectory, "configured storage")));
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

	[Fact]
	public async Task RemovedEnvironmentVariable_DoesNotOverrideDefaultDirectory() {
		using var runtime = new IsolatedHost();
		var environmentDirectory = Path.Combine(runtime.RootDirectory, "ignored environment data");
		Directory.CreateDirectory(environmentDirectory);
		var environmentDatabase = Path.Combine(environmentDirectory, "EnvironmentOnly.db");
		SeedDatabase(environmentDatabase, "environment-marker");
		var original = File.ReadAllBytes(environmentDatabase);

		var result = await runtime.RunAsync("db status\nexit\n", environmentDirectory: environmentDirectory);

		Assert.Equal(1, result.ExitCode);
		Assert.True(Directory.Exists(runtime.DefaultDataDirectory));
		Assert.Contains(runtime.DefaultDataDirectory, result.Output, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(Text("NoDatabases"), result.Output);
		Assert.DoesNotContain("EnvironmentOnly", result.Output);
		Assert.Equal(original, File.ReadAllBytes(environmentDatabase));
		Assert.False(Directory.Exists(Path.Combine(environmentDirectory, "backup")));
	}

	[Fact]
	public async Task CommandLineDirectory_OverridesConfigurationAndEnvironment() {
		using var runtime = new IsolatedHost(configuredDirectory: "configured storage");
		var configured = Path.Combine(runtime.AppDirectory, "configured storage");
		var selected = Path.Combine(runtime.RootDirectory, "selected data");
		var environmentDirectory = Path.Combine(runtime.RootDirectory, "ignored environment data");
		Directory.CreateDirectory(configured);
		Directory.CreateDirectory(selected);
		Directory.CreateDirectory(environmentDirectory);
		SeedDatabase(Path.Combine(configured, "ConfiguredOnly.db"), "configured-marker");
		SeedDatabase(Path.Combine(selected, "SelectedOnly.db"), "selected-marker");
		SeedDatabase(Path.Combine(environmentDirectory, "EnvironmentOnly.db"), "environment-marker");

		var result = await runtime.RunAsync("db status\nexit\n", ["--data-dir", selected], environmentDirectory);

		Assert.Equal(1, result.ExitCode);
		Assert.Contains(selected, result.Output, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("SelectedOnly", result.Output);
		Assert.DoesNotContain("ConfiguredOnly", result.Output);
		Assert.DoesNotContain("EnvironmentOnly", result.Output);
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
		Assert.False(Directory.Exists(Path.Combine(configured, "backup")));
		Assert.False(Directory.Exists(Path.Combine(environmentDirectory, "backup")));
	}

	[Theory]
	[InlineData("db restore Absent missing-backup")]
	[InlineData("db unlock Absent")]
	[InlineData("db recover Absent missing-operation --complete")]
	[InlineData("db recover Absent missing-operation --rollback")]
	[InlineData("db import-backup Absent \"missing backup.db\"")]
	[InlineData("db verify Absent")]
	public async Task MutatingInternalCommands_RequireExplicitConfirmation(string command) {
		using var runtime = new IsolatedHost();

		var result = await runtime.RunAsync(command + "\nexit\n");

		Assert.Equal(1, result.ExitCode);
		Assert.Contains(Text("DbCommandConfirmationRequired"), result.Output);
		Assert.DoesNotContain(Text("MaintenanceAccepted"), result.Output);
		Assert.DoesNotContain(Text("Restarting"), result.Output);
		Assert.False(Directory.Exists(Path.Combine(runtime.DefaultDataDirectory, "backup")));
	}

	[Theory]
	[InlineData("db")]
	[InlineData("db nonsense Absent")]
	[InlineData("db recover Absent operation")]
	[InlineData("db recover Absent operation --complete --rollback --confirm")]
	public async Task InvalidInternalCommand_ShowsCurrentTerminalUsage(string command) {
		using var runtime = new IsolatedHost();

		var result = await runtime.RunAsync(command + "\nexit\n");

		Assert.Equal(1, result.ExitCode);
		Assert.Contains(Text("DbInternalUsage"), result.Output);
		Assert.DoesNotContain(Text("Restarting"), result.Output);
	}

	[Fact]
	public async Task RemovedStartupDbSubcommand_IsRejectedBeforeConfigurationIsRead() {
		using var runtime = new IsolatedHost(writeConfig: false);

		var result = await runtime.RunAsync("", ["db", "version"]);

		Assert.Equal(2, result.ExitCode);
		Assert.Contains(Text("StartupArgumentsInvalid"), result.Output);
		Assert.DoesNotContain(Text("MaintenanceConsole"), result.Output);
		Assert.False(File.Exists(runtime.ConfigPath));
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

	[Fact]
	public async Task ImportBackup_UsesDatabaseAndQuotedSourceArgumentsAndListsRealImportedData() {
		using var runtime = new IsolatedHost();
		var legacyPath = Path.Combine(runtime.RootDirectory, "legacy source with spaces.db");
		SeedDatabase(legacyPath, "imported-target");

		var result = await runtime.RunAsync($"db import-backup Target \"{legacyPath}\" --confirm\ndb backups Target\nexit\n");

		Assert.Equal(1, result.ExitCode);
		var imported = Assert.Single(DatabaseBackupStore.ListCompleted(runtime.DefaultDataDirectory, "Target", DatabaseBackupStore.RollingCategory));
		Assert.Contains(imported.BackupId, result.Output);
		Assert.Contains(DatabaseBackupStore.RollingCategory, result.Output);
		Assert.Contains(Prefix("DbCommandCompleted"), result.Output);
		Assert.DoesNotContain(Text("DbInternalUsage"), result.Output);
		Assert.False(File.Exists(Path.Combine(runtime.DefaultDataDirectory, "Target.db")));
		Assert.True(DatabaseBackupStore.TryVerify(runtime.DefaultDataDirectory, "Target", DatabaseBackupStore.RollingCategory, imported.BackupId, out _, out var failure), failure);
		using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
			DataSource = imported.SnapshotPath,
			Mode = SqliteOpenMode.ReadOnly,
			Pooling = false,
		}.ToString());
		connection.Open();
		using var query = connection.CreateCommand();
		query.CommandText = "SELECT Name FROM ProbeItems";
		Assert.Equal("imported-target", query.ExecuteScalar());
	}

	private static string Text(string key) {
		return Localizer.Get(typeof(Databases).Assembly, key, CultureInfo.InvariantCulture);
	}

	private static string Prefix(string key) {
		return Text(key).Split('{')[0].TrimEnd();
	}

	private static void SeedDatabase(string path, string value) {
		using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "CREATE TABLE ProbeItems (Name TEXT NOT NULL); INSERT INTO ProbeItems VALUES ($value)";
		command.Parameters.AddWithValue("$value", value);
		command.ExecuteNonQuery();
	}

	private sealed record HostResult(int ExitCode, string Output);

	private sealed class IsolatedHost : IDisposable {
		public string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), $"pengin1011 terminal {Guid.NewGuid():N}");
		public string AppDirectory => Path.Combine(RootDirectory, "app");
		public string WorkingDirectory => Path.Combine(RootDirectory, "working");
		public string DefaultDataDirectory => Path.Combine(AppDirectory, "data");
		public string ConfigPath => Path.Combine(AppDirectory, "config", "config.json");

		public IsolatedHost(string? configuredDirectory = null, bool writeConfig = true) {
			Directory.CreateDirectory(AppDirectory);
			Directory.CreateDirectory(WorkingDirectory);
			foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")) {
				File.Copy(source, Path.Combine(AppDirectory, Path.GetFileName(source)));
			}
			foreach (var name in new[] { "Pengin1011.deps.json", "Pengin1011.runtimeconfig.json" }) {
				File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(AppDirectory, name));
			}
			var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
			foreach (var runtime in new[] { RuntimeInformation.RuntimeIdentifier, platform }.Distinct()) {
				var nativeRoot = Path.Combine(AppContext.BaseDirectory, "runtimes", runtime);
				if (!Directory.Exists(nativeRoot)) continue;
				foreach (var source in Directory.EnumerateFiles(nativeRoot, "*", SearchOption.AllDirectories)) {
					var target = Path.Combine(AppDirectory, "runtimes", runtime, Path.GetRelativePath(nativeRoot, source));
					Directory.CreateDirectory(Path.GetDirectoryName(target)!);
					File.Copy(source, target);
				}
			}
			if (!writeConfig) return;
			Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
			File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new {
				Discord = new { Token = "" },
				Databases = new { DataDirectory = configuredDirectory },
			}));
		}

		public async Task<HostResult> RunAsync(string input, IReadOnlyList<string>? arguments = null, string? environmentDirectory = null) {
			var startInfo = new ProcessStartInfo("dotnet") {
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = WorkingDirectory,
			};
			startInfo.ArgumentList.Add(Path.Combine(AppDirectory, "Pengin1011.dll"));
			foreach (var argument in arguments ?? []) startInfo.ArgumentList.Add(argument);
			if (environmentDirectory == null) startInfo.Environment.Remove("PENGIN1011_DATA_DIRECTORY");
			else startInfo.Environment["PENGIN1011_DATA_DIRECTORY"] = environmentDirectory;
			using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("宿主启动失败");
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			if (input.Length > 0) await process.StandardInput.WriteAsync(input);
			process.StandardInput.Close();
			using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			try {
				await process.WaitForExitAsync(budget.Token);
			} catch (OperationCanceledException e) {
				process.Kill(true);
				await process.WaitForExitAsync();
				throw new TimeoutException("隔离宿主未在 30 秒内结束", e);
			}
			return new HostResult(process.ExitCode, await stdout + await stderr);
		}

		public void Dispose() {
			SqliteConnection.ClearAllPools();
			Directory.Delete(RootDirectory, true);
		}
	}
}
