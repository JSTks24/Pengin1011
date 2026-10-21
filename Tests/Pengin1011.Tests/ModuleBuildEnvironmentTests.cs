using Microsoft.Win32;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleBuildEnvironmentTests {
	[Fact]
	public void FreshDotnetHome_DoesNotChangePath() {
		using var fixture = new ModuleBuildFixture();
		var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
		var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
		var rawUserPath = ReadRawUserPath();
		Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".dotnet-home")));

		var projectDirectory = fixture.CreateDirectory("PathProbe");
		(int ExitCode, string Output) result;
		string? afterUserPath;
		string? afterMachinePath;
		try {
			result = fixture.RunDotnet(fixture.Root, "new", "console", "--no-restore", "--output", projectDirectory);
			afterUserPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
			afterMachinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
		} finally {
			if (userPath != null && Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) != userPath) RestoreRawUserPath(rawUserPath);
		}

		Assert.True(result.ExitCode == 0, result.Output);
		Assert.True(File.Exists(Path.Combine(projectDirectory, "PathProbe.csproj")));
		Assert.Equal(machinePath, afterMachinePath);
		Assert.Equal(userPath, afterUserPath);
		Assert.Equal(userPath, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
		Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, ".dotnet-home"), "*.toolpath.sentinel", SearchOption.AllDirectories));
	}

	private static (string? Value, RegistryValueKind Kind)? ReadRawUserPath() {
		if (!OperatingSystem.IsWindows()) return null;
		using var key = Registry.CurrentUser.OpenSubKey("Environment");
		if (key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value) return null;
		return (value, key.GetValueKind("Path"));
	}

	private static void RestoreRawUserPath((string? Value, RegistryValueKind Kind)? raw) {
		if (!OperatingSystem.IsWindows() || raw is not { Value: { } value } original) return;
		using var key = Registry.CurrentUser.OpenSubKey("Environment", true);
		key?.SetValue("Path", value, original.Kind);
	}
}
