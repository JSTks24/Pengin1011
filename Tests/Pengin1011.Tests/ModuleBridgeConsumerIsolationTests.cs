using Microsoft.CodeAnalysis;

namespace Pengin1011.Tests;

public sealed class ModuleBridgeConsumerIsolationTests {
	private const string HostStubs = """
namespace Pengin1011.Core {
	[System.AttributeUsage(System.AttributeTargets.Assembly)]
	public sealed class ModuleHostContractAttribute : System.Attribute { public ModuleHostContractAttribute(int version) { } }
	public sealed class ModuleBridgeRegistrar { public void Add(string name, System.Delegate handler) { } }
	public interface IModuleBridgeProvider { void Register(ModuleBridgeRegistrar registrar, Pengin1011.Core.Modules.IModuleRuntime runtime); }
	public sealed class BridgeLeaseStub : System.IDisposable { public void Dispose() { } }
	public sealed class BridgeCall<TDelegate> where TDelegate : System.Delegate {
		public BridgeCall(TDelegate handler, BridgeLeaseStub lease) { Handler = handler; Lease = lease; }
		public TDelegate Handler { get; }
		public BridgeLeaseStub Lease { get; }
	}
	public static class ModuleBridgeDispatcher {
		public static BridgeCall<TDelegate> Admit<TDelegate>(string module, string name) where TDelegate : System.Delegate => throw new System.InvalidOperationException(module + "." + name);
	}
	public static class ModuleBridgeLeaseScope {
		public static System.Threading.Tasks.Task RunTask<TDelegate>(BridgeCall<TDelegate> call, System.Func<System.Threading.Tasks.Task> body) where TDelegate : System.Delegate => body();
		public static System.Threading.Tasks.Task<TResult> RunTaskOf<TDelegate, TResult>(BridgeCall<TDelegate> call, System.Func<System.Threading.Tasks.Task<TResult>> body) where TDelegate : System.Delegate => body();
		public static System.Threading.Tasks.ValueTask RunValueTask<TDelegate>(BridgeCall<TDelegate> call, System.Func<System.Threading.Tasks.ValueTask> body) where TDelegate : System.Delegate => body();
		public static System.Threading.Tasks.ValueTask<TResult> RunValueTaskOf<TDelegate, TResult>(BridgeCall<TDelegate> call, System.Func<System.Threading.Tasks.ValueTask<TResult>> body) where TDelegate : System.Delegate => body();
		public static System.Collections.Generic.IAsyncEnumerable<TItem> RunAsyncEnumerable<TDelegate, TItem>(System.Func<BridgeCall<TDelegate>> admit, System.Func<TDelegate, System.Collections.Generic.IAsyncEnumerable<TItem>> body, System.Threading.CancellationToken ct = default) where TDelegate : System.Delegate => body(admit().Handler);
		public static System.Collections.Generic.IEnumerable<TItem> RunSyncEnumerable<TDelegate, TItem>(System.Func<BridgeCall<TDelegate>> admit, System.Func<TDelegate, System.Collections.Generic.IEnumerable<TItem>> body) where TDelegate : System.Delegate => body(admit().Handler);
	}
}
namespace Pengin1011.Core.Modules {
	public interface IModuleRuntime {
		System.Threading.Tasks.Task InitializeAsync(System.Threading.CancellationToken ct);
		System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct);
	}
}
""";

	private static string Manifest(string module, params string[] exports) {
		var lines = exports.Select((line, index) => $"\t\tpublic const string __Export_{index} = \"{line}\";");
		return $$"""
namespace Pengin1011.Core {
	internal static class __ModuleBridgeManifest {
		public const string __Format = "pengin1011.modulebridge.manifest/3";
		public const string __Module = "{{module}}";
		public const int __ExportCount = {{exports.Length}};
{{string.Join("\n", lines)}}
	}
}
""";
	}

	[Fact]
	public void ConsumerCompilesUsingOnlyTheManifest_WithoutReferencingTheProviderAssembly() {
		var manifest = Manifest("Donor",
			"QueryGreetingAsync|static|global::System.Threading.Tasks.Task<string>|-:userId:ulong",
			"AddValues|static|long|-:a:int;-:b:int");
		var source = HostStubs + """

public static class Consumer {
	public static System.Threading.Tasks.Task<string> Greet(ulong id) => Pengin1011.Core.ModuleBridge.Donor.QueryGreetingAsync(id);
	public static long Sum(int a, int b) => Pengin1011.Core.ModuleBridge.Donor.AddValues(a, b);
}
""";
		var outcome = GeneratorTestHost.Run(source, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
	}

	[Fact]
	public void ProxyParamsModifier_AllowsExpandedCallSites() {
		var manifest = Manifest("Donor", "Count|static|int|p:items:global::System.String[]");
		var source = HostStubs + """

public static class Consumer {
	public static int Sum() => Pengin1011.Core.ModuleBridge.Donor.Count("a", "b", "c");
	public static int Array() => Pengin1011.Core.ModuleBridge.Donor.Count(new string[] { "a" });
}
""";
		var outcome = GeneratorTestHost.Run(source, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		GeneratorTestHost.AssertNoGeneratorDiagnostics(GeneratorTestHost.Run(source, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest)));
	}

	[Fact]
	public void ProviderAssemblyTypes_AreNotReachableWithoutReferencingThatAssembly() {
		var providerSource = """
namespace Donor {
	public static class DonorBridgeExports {
		public static string Ping() => "x";
	}
}
""";
		var providerCompilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
			"Donor",
			[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(providerSource)],
			GeneratorTestHost.ReferenceSet(),
			new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		using var stream = new MemoryStream();
		var emitted = providerCompilation.Emit(stream);
		Assert.True(emitted.Success, string.Join(" | ", emitted.Diagnostics.Select(d => d.GetMessage())));
		var donorReference = MetadataReference.CreateFromImage(stream.ToArray());

		var manifest = Manifest("Donor", "Ping|static|string|");
		var directSource = HostStubs + """

public static class Consumer {
	public static string Call() => Donor.DonorBridgeExports.Ping();
}
""";
		var direct = GeneratorTestHost.Run(directSource, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest));
		Assert.Contains(direct.CompilationDiagnostics, d => d.Id is "CS0103" or "CS0246");

		var proxySource = HostStubs + """

public static class Consumer {
	public static string Call() => Pengin1011.Core.ModuleBridge.Donor.Ping();
}
""";
		var proxy = GeneratorTestHost.Run(proxySource, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest));
		GeneratorTestHost.AssertNoGeneratorDiagnostics(proxy);
		GeneratorTestHost.AssertCompiles(proxy);

		Assert.NotNull(donorReference);
	}

	[Fact]
	public void ConsumerProxy_IsGeneratedEvenWhenTheModuleHasNoLocalExports() {
		var manifest = Manifest("Donor", "Value|static|string|");
		var source = HostStubs + """

public static class Consumer {
	public static string Read() => Pengin1011.Core.ModuleBridge.Donor.Value();
}
""";
		var outcome = GeneratorTestHost.Run(source, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains("class Donor", outcome.SourceByHint("ModuleBridge.Donor.g.cs"));
	}

	[Fact]
	public void VoidReturnInManifest_IsAcceptedByTheConsumer() {
		var manifest = Manifest("Donor", "Fire|static|void|-:payload:global::System.String");
		var source = HostStubs + """

public static class Consumer {
	public static void Call() => Pengin1011.Core.ModuleBridge.Donor.Fire("x");
}
""";
		var outcome = GeneratorTestHost.Run(source, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(outcome);
		GeneratorTestHost.AssertCompiles(outcome);
		Assert.Contains("public static void Fire(", outcome.SourceByHint("ModuleBridge.Donor.g.cs"));
	}

	[Fact]
	public void VoidReturnSurvivesAProviderToConsumerRoundTrip() {
		var providerSource = """
namespace Pengin1011.Core { [System.AttributeUsage(System.AttributeTargets.Method)] public sealed class ModuleBridgeExportAttribute : System.Attribute { } }
namespace Pengin1011.Core.Modules { public interface IModuleRuntime { System.Threading.Tasks.Task InitializeAsync(System.Threading.CancellationToken ct); System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct); } }
namespace Pengin1011.Core {
	public sealed class ModuleBridgeRegistrar { public void Add(string name, System.Delegate handler) { } }
	public interface IModuleBridgeProvider { void Register(ModuleBridgeRegistrar registrar, Pengin1011.Core.Modules.IModuleRuntime runtime); }
	[System.AttributeUsage(System.AttributeTargets.Assembly)] public sealed class ModuleBridgeProviderAttribute : System.Attribute { public ModuleBridgeProviderAttribute(System.Type providerType) { } }
}
public static class DonorExports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static void Fire(string payload) { }
	[Pengin1011.Core.ModuleBridgeExport]
	public static int Sum(params int[] values) => values.Length;
}
""";
		var providerRun = GeneratorTestHost.Run(providerSource, "Donor");
		GeneratorTestHost.AssertNoGeneratorDiagnostics(providerRun);
		var manifest = providerRun.SourceContaining("__ModuleBridgeManifest");

		var consumerSource = HostStubs + """

public static class Consumer {
	public static void Fire() => Pengin1011.Core.ModuleBridge.Donor.Fire("x");
	public static int Sum() => Pengin1011.Core.ModuleBridge.Donor.Sum(1, 2);
}
""";
		var consumerRun = GeneratorTestHost.Run(consumerSource, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(consumerRun);
		GeneratorTestHost.AssertCompiles(consumerRun);
	}

	[Fact]
	public void ConsumerProxy_MatchesTheRealManifestProducedByTheProviderSide() {
		var providerSource = """
namespace Pengin1011.Core { [System.AttributeUsage(System.AttributeTargets.Method)] public sealed class ModuleBridgeExportAttribute : System.Attribute { } }
namespace Pengin1011.Core.Modules { public interface IModuleRuntime { System.Threading.Tasks.Task InitializeAsync(System.Threading.CancellationToken ct); System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct); } }
namespace Pengin1011.Core {
	public sealed class ModuleBridgeRegistrar { public void Add(string name, System.Delegate handler) { } }
	public interface IModuleBridgeProvider { void Register(ModuleBridgeRegistrar registrar, Pengin1011.Core.Modules.IModuleRuntime runtime); }
	[System.AttributeUsage(System.AttributeTargets.Assembly)] public sealed class ModuleBridgeProviderAttribute : System.Attribute { public ModuleBridgeProviderAttribute(System.Type providerType) { } }
}
public static class DonorExports {
	[Pengin1011.Core.ModuleBridgeExport]
	public static System.Threading.Tasks.Task<string> QueryGreetingAsync(ulong userId) => System.Threading.Tasks.Task.FromResult(userId.ToString());
	[Pengin1011.Core.ModuleBridgeExport]
	public static int Count(params string[] items) => items.Length;
}
""";
		var providerRun = GeneratorTestHost.Run(providerSource, "Donor");
		GeneratorTestHost.AssertNoGeneratorDiagnostics(providerRun);
		var manifest = providerRun.SourceContaining("__ModuleBridgeManifest");

		var consumerSource = HostStubs + """

public static class Consumer {
	public static System.Threading.Tasks.Task<string> Greet(ulong id) => Pengin1011.Core.ModuleBridge.Donor.QueryGreetingAsync(id);
	public static int Count() => Pengin1011.Core.ModuleBridge.Donor.Count("a", "b");
}
""";
		var consumerRun = GeneratorTestHost.Run(consumerSource, "ConsumerModule", ("ModuleBridgeManifest.g.cs", manifest));

		GeneratorTestHost.AssertNoGeneratorDiagnostics(consumerRun);
		GeneratorTestHost.AssertCompiles(consumerRun);
	}
}
