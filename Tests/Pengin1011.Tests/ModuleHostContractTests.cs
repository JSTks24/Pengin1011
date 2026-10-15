using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Pengin1011.Tests;

[Collection("BaseDirSerial")]
public sealed class ModuleHostContractTests {
	[Fact]
	public async Task LegacyContract_IsRejectedBeforeRuntimeConstructionOrBridgeRegistration() {
		var directory = Path.Combine(Path.GetTempPath(), "pengin_contract_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			var marker = Path.Combine(directory, "runtime-created.txt");
			var source = $$"""
using System.Threading;
using System.Threading.Tasks;
using Pengin1011.Core;
using Pengin1011.Core.Modules;
[assembly: ModuleHostContract(3)]
[assembly: ModuleBridgeProvider(typeof(LegacyProvider))]
public sealed class LegacyRuntime : IModuleRuntime {
	public LegacyRuntime() {
		System.IO.File.WriteAllText({{SymbolDisplay.FormatLiteral(marker, true)}}, "created");
	}
	public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
	public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
public sealed class LegacyProvider : IModuleBridgeProvider {
	public void Register(ModuleBridgeRegistrar registrar, IModuleRuntime runtime) {
		registrar.Add("LegacyValue", (System.Func<string>)(() => "legacy"));
	}
}
""";
			var compilation = CSharpCompilation.Create(
				"LegacyContract_" + Guid.NewGuid().ToString("N"),
				[CSharpSyntaxTree.ParseText(source)],
				GeneratorTestHost.ReferenceSet(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			var path = Path.Combine(directory, "LegacyContract.dll");
			using (var output = File.Create(path)) {
				var result = compilation.Emit(output);
				Assert.True(result.Success, string.Join(" | ", result.Diagnostics));
			}

			await TestChildProcess.RunArgumentsAsync("module-contract-reject", path);

			Assert.False(File.Exists(marker));
		} finally {
			Directory.Delete(directory, true);
		}
	}
}
