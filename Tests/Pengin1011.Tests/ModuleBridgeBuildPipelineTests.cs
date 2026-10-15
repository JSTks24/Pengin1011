namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleBridgeBuildPipelineTests {
	private static string ManifestPath(string directory) {
		return Path.Combine(directory, "obj", "Debug", "net10.0", "generated", "Pengin1011Generator", "Pengin1011Generator.ModuleBridge.ModuleBridgeGenerator", "ModuleBridgeManifest.g.cs");
	}

	private static void WriteProvider(ModuleBuildFixture fixture, string directory, string exports) {
		var prefix = Path.GetRelativePath(directory, fixture.Root).Replace('\\', '/');
		File.WriteAllText(Path.Combine(directory, "ProbeProvider.csproj"), $$"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			    <AssemblyName>ProbeProvider</AssemblyName>
			    <Nullable>enable</Nullable>
			    <ImplicitUsings>enable</ImplicitUsings>
			    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
			    <OutputType>Library</OutputType>
			  </PropertyGroup>
			  <ItemGroup>
			    <ProjectReference Include="{{prefix}}/Pengin1011.csproj" />
			    <ProjectReference Include="{{prefix}}/Generator/Pengin1011Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
			  </ItemGroup>
			</Project>
			""");
		WriteSource(directory, exports);
	}

	private static void WriteSource(string directory, string exports) {
		File.WriteAllText(Path.Combine(directory, "Exports.cs"), $$"""
			using Pengin1011.Core;

			namespace ProbeProvider;

			public static class ProbeExports {
			{{exports}}
			}
			""");
	}

	private static void WriteConsumer(ModuleBuildFixture fixture, string directory, string dependency, string source) {
		var prefix = Path.GetRelativePath(directory, fixture.Root).Replace('\\', '/');
		File.WriteAllText(Path.Combine(directory, "ProbeConsumer.csproj"), $$"""
			<Project Sdk="Microsoft.NET.Sdk">
			  <PropertyGroup>
			    <TargetFramework>net10.0</TargetFramework>
			    <AssemblyName>ProbeConsumer</AssemblyName>
			    <Nullable>enable</Nullable>
			    <ImplicitUsings>enable</ImplicitUsings>
			    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
			    <OutputType>Library</OutputType>
			  </PropertyGroup>
			  <ItemGroup>
			    <ProjectReference Include="{{prefix}}/Pengin1011.csproj" />
			    <ProjectReference Include="{{prefix}}/Generator/Pengin1011Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
			    <BridgeDependency Include="{{dependency}}" />
			  </ItemGroup>
			</Project>
			""");
		File.WriteAllText(Path.Combine(directory, "Consumer.cs"), source);
	}

	[Fact]
	public void ConsumerBuild_RefreshesBridgeDependencyWithoutReferencingProviderTypes() {
		using var fixture = new ModuleBuildFixture();
		var providerDirectory = fixture.CreateDirectory("ProbeProvider");
		var consumerDirectory = fixture.CreateDirectory("ProbeConsumer");
		WriteProvider(fixture, providerDirectory, "\t[ModuleBridgeExport]\n\tpublic static string Before() => \"before\";");
		WriteConsumer(fixture, consumerDirectory, "ProbeProvider", "using Pengin1011.Core;\npublic static class Consumer { public static string Run() => ModuleBridge.ProbeProvider.Before(); }");
		var manifestPath = ManifestPath(providerDirectory);
		var proxyPath = Path.Combine(Path.GetDirectoryName(ManifestPath(consumerDirectory))!, "ModuleBridge.ProbeProvider.g.cs");
		Assert.False(File.Exists(manifestPath));
		Assert.False(File.Exists(proxyPath));
		Assert.False(Directory.Exists(Path.Combine(fixture.Root, "bin")));

		var first = fixture.RunDotnet(consumerDirectory, "build", "ProbeConsumer.csproj");
		Assert.True(first.ExitCode == 0, first.Output);
		Assert.Contains("__ExportCount = 1", File.ReadAllText(manifestPath));
		Assert.Contains("Before|static|", File.ReadAllText(manifestPath));
		Assert.Contains("public static string Before()", File.ReadAllText(proxyPath));

		WriteSource(providerDirectory, "\t[ModuleBridgeExport]\n\tpublic static int After(int value) => value;");
		File.WriteAllText(Path.Combine(consumerDirectory, "Consumer.cs"), "using Pengin1011.Core;\npublic static class Consumer { public static int Run() => ModuleBridge.ProbeProvider.After(7); }");
		var changed = fixture.RunDotnet(consumerDirectory, "build", "ProbeConsumer.csproj", "--no-restore");
		Assert.True(changed.ExitCode == 0, changed.Output);
		var manifest = File.ReadAllText(manifestPath);
		var proxy = File.ReadAllText(proxyPath);
		Assert.Contains("After|static|int|-:value:int", manifest);
		Assert.DoesNotContain("Before", manifest);
		Assert.Contains("public static int After(int value)", proxy);
		Assert.DoesNotContain("Before", proxy);

		WriteSource(providerDirectory, "");
		File.WriteAllText(Path.Combine(consumerDirectory, "Consumer.cs"), "public static class Consumer { }");
		var empty = fixture.RunDotnet(consumerDirectory, "build", "ProbeConsumer.csproj", "--no-restore");
		Assert.True(empty.ExitCode == 0, empty.Output);
		manifest = File.ReadAllText(manifestPath);
		proxy = File.ReadAllText(proxyPath);
		Assert.Contains("__ExportCount = 0", manifest);
		Assert.DoesNotContain("Before", manifest);
		Assert.DoesNotContain("After", manifest);
		Assert.Contains("public static class ProbeProvider", proxy);
		Assert.DoesNotContain("Before", proxy);
		Assert.DoesNotContain("After", proxy);

		File.WriteAllText(Path.Combine(consumerDirectory, "Consumer.cs"), "public static class Consumer { public static global::System.Type ProviderType => typeof(global::ProbeProvider.ProbeExports); }");
		var directReference = fixture.RunDotnet(consumerDirectory, "build", "ProbeConsumer.csproj", "--no-restore");
		Assert.NotEqual(0, directReference.ExitCode);
		Assert.Contains("CS0400", directReference.Output);
	}

	[Fact]
	public void FirstBuild_ProducesTheManifestInTheDocumentedLocation() {
		using var fixture = new ModuleBuildFixture();
		var directory = fixture.CreateDirectory("ProbeProvider");
		WriteProvider(fixture, directory, "\t[ModuleBridgeExport]\n\tpublic static string Ping() => \"pong\";");

		var build = fixture.RunDotnet(directory, "build", "ProbeProvider.csproj");
		Assert.True(build.ExitCode == 0, build.Output);

		var manifest = ManifestPath(directory);
		Assert.True(File.Exists(manifest), $"清单未按约定路径产出：{manifest}");
		var text = File.ReadAllText(manifest);
		Assert.Contains("pengin1011.modulebridge.manifest/3", text);
		Assert.Contains("ProbeProvider", text);
		Assert.Contains("Ping", text);
	}

	[Fact]
	public void ChinesePreferredUiLanguage_ReportsLocalizedGeneratorDiagnostics() {
		using var fixture = new ModuleBuildFixture();
		var directory = fixture.CreateDirectory("ProbeProvider");
		WriteProvider(fixture, directory, "\t[ModuleBridgeExport]\n\tinternal static string Hidden() => \"x\";");

		var chinese = fixture.RunDotnet(directory, "build", "ProbeProvider.csproj", "-p:PreferredUILang=zh-CN");
		Assert.NotEqual(0, chinese.ExitCode);
		Assert.Contains("PBG001", chinese.Output);
		Assert.Contains("必须是 public", chinese.Output);

		var english = fixture.RunDotnet(directory, "build", "ProbeProvider.csproj", "-p:PreferredUILang=en-US", "--no-restore");
		Assert.NotEqual(0, english.ExitCode);
		Assert.Contains("PBG001", english.Output);
		Assert.Contains("must be public", english.Output);
	}

	[Fact]
	public void ConsumerWithDependencyWithoutGenerator_FailsWithAnEnglishMsBuildError() {
		using var fixture = new ModuleBuildFixture();
		var providerDirectory = fixture.CreateDirectory("MissingManifest");
		var consumerDirectory = fixture.CreateDirectory("ProbeConsumer");
		File.WriteAllText(Path.Combine(providerDirectory, "MissingManifest.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
		WriteConsumer(fixture, consumerDirectory, "MissingManifest", "public static class Consumer { }");

		var build = fixture.RunDotnet(consumerDirectory, "build", "ProbeConsumer.csproj");

		Assert.NotEqual(0, build.ExitCode);
		Assert.Contains("Bridge manifest missing", build.Output);
	}
}
