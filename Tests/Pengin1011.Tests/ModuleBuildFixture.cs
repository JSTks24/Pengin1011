using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Pengin1011.Tests;

internal sealed class ModuleBuildFixture : IDisposable {
	public string Root { get; }

	public static string RepositoryRoot {
		get {
			for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent) {
				if (File.Exists(Path.Combine(current.FullName, "Pengin1011.csproj"))) return current.FullName;
			}
			throw new InvalidOperationException("未找到仓库根（Pengin1011.csproj）");
		}
	}

	public ModuleBuildFixture(bool pathWithSpaces = false) {
		var prefix = pathWithSpaces ? "pengin1011 build " : "pengin1011_build_";
		Root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(Root, ".config"));
		File.Copy(Path.Combine(RepositoryRoot, ".config", "dotnet-tools.json"), Path.Combine(Root, ".config", "dotnet-tools.json"));
		foreach (var file in new[] { "global.json", "Program.cs", "Pengin1011.csproj", "Pengin1011.sln", "Directory.Build.targets", "ModuleSdk.md", "LICENSE" }) File.Copy(Path.Combine(RepositoryRoot, file), Path.Combine(Root, file));
		foreach (var directory in new[] { "build", "BuildTemplates", "Cli", "Core", "Generator", "Helper", "Modules", "Resources", "Services", "Tests", "tools" }) CopySources(Path.Combine(RepositoryRoot, directory), Path.Combine(Root, directory));
	}

	public void RestoreTools() {
		var restore = RunDotnet(Root, "tool", "restore");
		Assert.True(restore.ExitCode == 0, restore.Output);
	}

	public void AddMigration(string module, string context, string migration, string provider) {
		var result = RunDotnet(Root, "build", "Pengin1011.csproj", "-c", "Release", "-t:AddModuleMigration",
			$"-p:Module={module}", $"-p:Context={context}", $"-p:MigrationName={migration}", $"-p:Provider={provider}");
		Assert.True(result.ExitCode == 0, result.Output);
	}

	public string CreateDirectory(string name) {
		var directory = Path.Combine(Root, "Tests", "_Build", name);
		Directory.CreateDirectory(directory);
		return directory;
	}

	public (int ExitCode, string Output) RunDotnet(string workingDirectory, params string[] arguments) {
		if (arguments[0] is "build" or "publish" or "msbuild") arguments = [.. arguments, "--nologo", arguments.Contains("-t:NewModule") ? "-v:minimal" : "-v:quiet", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false"];
		return Run("dotnet", workingDirectory, arguments);
	}

	private (int ExitCode, string Output) Run(string executable, string workingDirectory, string[] arguments) {
		var startInfo = new ProcessStartInfo(executable) {
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			UseShellExecute = false,
		};
		startInfo.Environment.Clear();
		foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "HOME", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "DOTNET_ROOT", "DOTNET_ROOT(x86)", "NUGET_PACKAGES", "RestoreFallbackFolders", "NuGetAudit" }) {
			var value = Environment.GetEnvironmentVariable(name);
			if (value != null) startInfo.Environment[name] = value;
		}
		startInfo.Environment["DOTNET_CLI_HOME"] = Path.Combine(Root, ".dotnet-home");
		startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		startInfo.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
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
