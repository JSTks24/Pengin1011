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
	[Theory]
	[InlineData("{\"MySQL\":{}}")]
	[InlineData("{\"MySQL\":{},\"PostgreSQL\":{}}")]
	public async Task InvalidExternalConfigurationDoesNotInitializeSQLite(string databases) {
		using var runtime = new IsolatedHost();
		File.WriteAllText(runtime.ConfigPath, "{\"Discord\":{\"Token\":\"\"},\"Databases\":" + databases + "}");
		var result = await runtime.RunAsync("db status Absent\nexit\n");
		Assert.Equal(1, result.ExitCode);
		Assert.Contains(Text("DatabaseConfigurationUnavailable"), result.Output);
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

	[Theory]
	[InlineData("MySQL")]
	[InlineData("PostgreSQL")]
	public async Task ExternalMaintenanceUsesTheConfiguredBackendWithoutLocalFiles(string provider) {
		using var runtime = new IsolatedHost();
		var config = new { Discord = new { Token = "" }, Databases = new Dictionary<string, object> {
			[provider] = new { Host = "localhost", Database = "missing", Username = "test", Password = "secret-do-not-log" },
		} };
		File.WriteAllText(runtime.ConfigPath, JsonSerializer.Serialize(config));
		var result = await runtime.RunAsync("db status Absent\ndb backup\nexit\n");
		Assert.Equal(1, result.ExitCode);
		Assert.Contains(provider, result.Output);
		Assert.Contains(Text("ExternalDatabaseBackupUnsupported"), result.Output);
		Assert.DoesNotContain("secret-do-not-log", result.Output);
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

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
		Assert.Contains(string.Format(CultureInfo.InvariantCulture, Text("DbNoBackups"), "Absent"), result.Output);
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

	public static IEnumerable<object[]> UnreadableConfigurations() {
		var configurations = new List<string> { "{ invalid", "null" };
		foreach (var provider in new[] { "MySQL", "PostgreSQL" }) {
			var external = "\"" + provider + "\":{\"Host\":\"localhost\",\"Database\":\"test\",\"Username\":\"test\",\"Password\":\"secret;=\\\"private\"";
			configurations.Add("{\"Databases\":{" + external + ",\"Port\":\"5432\"}}}");
			configurations.Add("{\"Discord\":{\"Token\":123},\"Databases\":{" + external + "}}}");
		}
		foreach (var configuration in configurations) {
			foreach (var mode in new[] { "default", "existing", "create" }) yield return [configuration, mode];
		}
	}

	[Theory]
	[MemberData(nameof(UnreadableConfigurations))]
	public async Task UnreadableConfigRejectsAllDatabaseCommandsWithoutTouchingStorage(string configuration, string mode) {
		using var runtime = new IsolatedHost();
		File.WriteAllText(runtime.ConfigPath, configuration);
		var selected = Path.Combine(runtime.RootDirectory, "explicit data");
		if (mode == "existing") {
			Directory.CreateDirectory(selected);
			SeedDatabase(Path.Combine(selected, "Probe.db"), "preserved");
		}
		var before = mode == "existing" ? File.ReadAllBytes(Path.Combine(selected, "Probe.db")) : [];
		string[] arguments = mode switch {
			"existing" => ["--data-dir", selected],
			"create" => ["--data-dir", selected, "--create-data-dir"],
			_ => [],
		};
		var result = await runtime.RunAsync("db version\ndb status Probe\ndb backups Probe\ndb backup\ndb verify Probe Missing\nmodules\nexit\n", arguments);

		Assert.Equal(1, result.ExitCode);
		Assert.True(result.Output.Split(Text("DatabaseConfigurationUnavailable"), StringSplitOptions.None).Length >= 6, result.Output);
		Assert.Contains(Text("NoModulesLoaded"), result.Output);
		Assert.DoesNotContain("SQLite", result.Output);
		Assert.DoesNotContain(Prefix("DbBackupCompleted"), result.Output);
		var logs = string.Join("\n", Directory.EnumerateFiles(Path.Combine(runtime.AppDirectory, "runtime", "logs"), "*.log").Select(File.ReadAllText));
		Assert.DoesNotContain("secret;=\"private", result.Output + logs);
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
		if (mode == "existing") {
			Assert.Equal(before, File.ReadAllBytes(Path.Combine(selected, "Probe.db")));
			Assert.Equal([Path.Combine(selected, "Probe.db")], Directory.GetFileSystemEntries(selected));
		} else {
			Assert.False(Directory.Exists(selected));
		}
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
	public async Task RemovedInternalCommands_ShowOfflineInstructions(string command) {
		using var runtime = new IsolatedHost();

		var result = await runtime.RunAsync(command + "\nexit\n");

		Assert.Equal(1, result.ExitCode);
		Assert.Contains(Text("DbInternalUsage"), result.Output);
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
	}

	[Fact]
	public async Task InvalidOfflineCommand_IsRejectedBeforeConfigurationIsRead() {
		using var runtime = new IsolatedHost(writeConfig: false);

		var result = await runtime.RunAsync("", ["db", "version"]);

		Assert.Equal(2, result.ExitCode);
		Assert.Contains(Text("DbOfflineUsage"), result.Output);
		Assert.DoesNotContain(Text("MaintenanceConsole"), result.Output);
		Assert.False(File.Exists(runtime.ConfigPath));
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OfflineRestoreRunsWithoutConfigurationModulesOrFinalBackupAndReleasesOwnership(bool malformedConfig) {
		using var runtime = new IsolatedHost(writeConfig: false);
		if (malformedConfig) {
			Directory.CreateDirectory(Path.GetDirectoryName(runtime.ConfigPath)!);
			File.WriteAllText(runtime.ConfigPath, "{ invalid");
		}
		var data = Path.Combine(runtime.RootDirectory, "offline data");
		Directory.CreateDirectory(data);
		await TestChildProcess.RunArgumentsAsync("db-seed", data);
		var backup = File.ReadAllText(Path.Combine(data, "backup-id.txt"));
		var before = DatabaseBackupStore.ListCompleted(data, "Fake", DatabaseBackupStore.RollingCategory).Count;
		var result = await runtime.RunAsync("", ["db", "restore", "Fake", backup, "--data-dir", data, "--confirm"]);
		Assert.True(result.ExitCode == 0, result.Output);
		Assert.Contains(Text("RestoreOfflineCompleted"), result.Output);
		Assert.Equal(malformedConfig, File.Exists(runtime.ConfigPath));
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
		Assert.DoesNotContain(Text("MaintenanceConsole"), result.Output);
		Assert.Equal(before, DatabaseBackupStore.ListCompleted(data, "Fake", DatabaseBackupStore.RollingCategory).Count);
		Assert.True(DataDirectoryOwnership.TryAcquire(data, out var owner, out var failure), failure);
		owner!.Dispose();
		await TestChildProcess.RunArgumentsAsync("db-read", data, "backup-target");
	}

	[Fact]
	public async Task OfflineRestoreRefusesAnotherOwnerAndMissingConfirmation() {
		using var runtime = new IsolatedHost(writeConfig: false);
		var data = Path.Combine(runtime.RootDirectory, "locked data");
		Directory.CreateDirectory(data);
		Assert.True(DataDirectoryOwnership.TryAcquire(data, out var owner, out _));
		using (owner) {
			var blocked = await runtime.RunAsync("", ["db", "restore", "Fake", "missing", "--data-dir", data, "--confirm"]);
			Assert.Equal(1, blocked.ExitCode);
		}
		var unconfirmed = await runtime.RunAsync("", ["db", "restore", "Fake", "missing", "--data-dir", data]);
		Assert.Equal(2, unconfirmed.ExitCode);
		Assert.False(File.Exists(runtime.ConfigPath));
		Assert.False(Directory.Exists(Path.Combine(data, "backup")));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	public async Task OfflineRestoreRejectsBlankExplicitDirectory(string directory) {
		using var runtime = new IsolatedHost(writeConfig: false);
		var result = await runtime.RunAsync("", ["db", "restore", "Fake", "backup", "--data-dir", directory, "--confirm"]);
		Assert.Equal(2, result.ExitCode);
		Assert.False(Directory.Exists(runtime.DefaultDataDirectory));
		Assert.False(File.Exists(runtime.ConfigPath));
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
