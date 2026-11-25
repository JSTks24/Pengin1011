using Microsoft.CodeAnalysis;

namespace Pengin1011.Tests;

public sealed class BuildToolchainTests {
	[Fact]
	public void GeneratorUsesNet10ReferenceAssemblies() {
		var runtime = Assert.Single(GeneratorTestHost.ReferenceSet().OfType<PortableExecutableReference>(), reference => Path.GetFileName(reference.FilePath) == "System.Runtime.dll");
		Assert.Equal("net10.0", Path.GetFileName(Path.GetDirectoryName(runtime.FilePath)));
	}

	[Fact]
	public void ScaffoldUsesTheNet10SdkFromAnIsolatedCopy() {
		using var fixture = new ModuleBuildFixture();
		var result = fixture.RunDotnet(fixture.CreateDirectory("SdkProbe"), "--version");
		Assert.True(result.ExitCode == 0, result.Output);
		Assert.StartsWith("10.0.", result.Output.Trim());
	}
}
