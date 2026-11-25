using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Pengin1011.Core;
using Pengin1011.Core.Modules;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleSdkPackageTests {
	private const string PackageId = "Pengin1011.ModuleSdk";

	[Fact]
	public async Task LocalPackage_BuildsAndRunsIndependentModulesFromACleanRestore() {
		using var fixture = new ModuleBuildFixture(pathWithSpaces: true);
		var version = typeof(ModuleHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
		var pack = fixture.RunDotnet(fixture.Root, "pack", "Pengin1011.csproj", "-c", "Release");
		Assert.True(pack.ExitCode == 0, pack.Output);
		var repack = fixture.RunDotnet(fixture.Root, "pack", "Pengin1011.csproj", "-c", "Release", "--no-build");
		Assert.True(repack.ExitCode == 0, repack.Output);
		var feed = Environment.GetEnvironmentVariable("PENGIN1011_TEST_PACKAGE_SOURCE") ?? Path.Combine(fixture.Root, "bin", "Release");
		var packagePath = Path.Combine(feed, $"{PackageId}.{version}.nupkg");
		using (var package = ZipFile.OpenRead(packagePath)) {
			Assert.NotNull(package.GetEntry("lib/net10.0/Pengin1011.dll"));
			Assert.NotNull(package.GetEntry("lib/net10.0/zh-CN/Pengin1011.resources.dll"));
			Assert.NotNull(package.GetEntry("ModuleSdk.md"));
			Assert.Null(package.GetEntry("lib/net10.0/Pengin1011.runtimeconfig.json"));
		}

		var workspace = Directory.CreateTempSubdirectory("pengin1011 external modules ");
		try {
			WritePackageSources(fixture.Root, feed, workspace.FullName);
			var provider = WriteModule(workspace.FullName, version, "SdkProvider", "", "Loc.Get(\"SampleGreeting\", System.Globalization.CultureInfo.InvariantCulture)");
			var resources = Directory.CreateDirectory(Path.Combine(provider, "Localization"));
			var template = File.ReadAllText(Path.Combine(fixture.Root, "BuildTemplates", "ModuleStrings.tpl"));
			File.WriteAllText(Path.Combine(resources.FullName, "Strings.resx"), template.Replace("__MODULE__", "SdkProvider"));
			var consumer = WriteModule(workspace.FullName, version, "SdkConsumer",
				"""<ProjectReference Include="../SdkProvider/SdkProvider.csproj" ReferenceOutputAssembly="false" BridgeDependency="true" />""",
				"ModuleBridge.SdkProvider.Probe()");

			var packages = Path.Combine(workspace.FullName, "packages");
			var build = fixture.RunDotnet(consumer, "build", "SdkConsumer.csproj", "-c", "Release",
				$"-p:RestorePackagesPath={packages}", "-p:RestoreFallbackFolders=", "-p:NuGetAudit=false");
			Assert.True(build.ExitCode == 0, build.Output);
			var restoredPackage = Path.Combine(packages, PackageId.ToLowerInvariant(), version.ToLowerInvariant(), $"{PackageId}.{version}.nupkg".ToLowerInvariant());
			Assert.Equal(File.ReadAllBytes(packagePath), File.ReadAllBytes(restoredPackage));
			var modules = Directory.CreateDirectory(Path.Combine(workspace.FullName, "module"));
			foreach (var project in new[] { provider, consumer }) {
				var dll = Path.GetFileName(project) + ".dll";
				File.Copy(Path.Combine(project, "bin", "Release", "net10.0", dll), Path.Combine(modules.FullName, dll));
			}
			await TestChildProcess.RunArgumentsAsync("module-sdk-load", modules.FullName);

			File.WriteAllText(Path.Combine(provider, "InvalidExport.cs"), """
				using Pengin1011.Core;
				public static class InvalidExport {
					[ModuleBridgeExport]
					internal static int Hidden() => 1;
				}
				""");
			var diagnostic = fixture.RunDotnet(provider, "build", "--no-restore", "-c", "Release", "-p:PreferredUILang=zh-CN");
			Assert.NotEqual(0, diagnostic.ExitCode);
			Assert.Contains("PBG001", diagnostic.Output);
			Assert.Contains("必须是 public", diagnostic.Output);
		} finally {
			workspace.Delete(true);
		}
	}

	private static void WritePackageSources(string framework, string feed, string workspace) {
		var sources = new XElement("packageSources", new XElement("clear"));
		var mapping = new XElement("packageSourceMapping", new XElement("clear"));
		void Add(string key, string path, string pattern) {
			sources.Add(new XElement("add", new XAttribute("key", key), new XAttribute("value", path)));
			mapping.Add(new XElement("packageSource", new XAttribute("key", key), new XElement("package", new XAttribute("pattern", pattern))));
		}
		Add("framework", feed, PackageId);
		using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(framework, "obj", "project.assets.json")));
		foreach (var folder in assets.RootElement.GetProperty("packageFolders").EnumerateObject()) {
			Add("cache" + sources.Elements("add").Count(), folder.Name, "*");
		}
		new XElement("configuration", sources, mapping).Save(Path.Combine(workspace, "NuGet.Config"));
	}

	private static string WriteModule(string workspace, string version, string name, string reference, string probe) {
		var directory = Directory.CreateDirectory(Path.Combine(workspace, name)).FullName;
		File.WriteAllText(Path.Combine(directory, name + ".csproj"), $$"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			    <ImplicitUsings>enable</ImplicitUsings>
			  </PropertyGroup>
			  <ItemGroup>
			    <PackageReference Include="{{PackageId}}" Version="{{version}}" />
			    {{reference}}
			  </ItemGroup>
			</Project>
			""");
		File.WriteAllText(Path.Combine(directory, name + "Runtime.cs"), $$"""
			using Pengin1011.Core;
			using Pengin1011.Core.Modules;

			namespace Pengin1011.Modules.{{name}};

			public sealed class {{name}}Runtime : IModuleRuntime {
				public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
				public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

				[ModuleBridgeExport]
				public static string Probe() => {{probe}};
			}
			""");
		return directory;
	}

	internal static async Task<int> RunModulesAsync(string directory) {
		ModuleHost.ModuleDirectoryOverrideForTest = directory;
		ModuleHost.LoadAll();
		var modules = ModuleHost.Modules;
		Assert.Equal(new[] { "SdkConsumer", "SdkProvider" }, modules.Select(module => module.Name));
		Assert.DoesNotContain(modules[0].Assembly.GetReferencedAssemblies(), assembly => assembly.Name == "SdkProvider");
		foreach (var module in modules) ModuleHost.StartRuntime(module);
		await Task.WhenAll(modules.Select(module => module.InitTask!)).WaitAsync(TimeSpan.FromSeconds(10));
		Assert.True(SpinWait.SpinUntil(() => modules.All(module => module.State == ModuleState.Ready), TimeSpan.FromSeconds(10)));
		var call = ModuleBridgeDispatcher.Admit<Func<string>>("SdkConsumer", "Probe");
		using (call.Lease) Assert.Equal("Hello from SdkProvider", call.Handler());
		Assert.All(await ModuleHost.StopAllAsync(), result => Assert.True(result.Clean, result.Reason));
		return 0;
	}
}
