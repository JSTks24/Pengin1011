using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Pengin1011.Core;
using Pengin1011Generator.Database;

namespace Pengin1011.Tests;

public sealed class DatabaseGeneratorTests {
	internal static (Compilation Compilation, GeneratorDriverRunResult Result) Generate(string source) {
		var references = GeneratorTestHost.ReferenceSet().AddRange(Directory.EnumerateFiles(AppContext.BaseDirectory, "Microsoft.EntityFrameworkCore*.dll")
			.Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "Microsoft.Extensions*.dll"))
			.Select(path => MetadataReference.CreateFromFile(path)));
		var compilation = CSharpCompilation.Create("DatabaseProbe" + Guid.NewGuid().ToString("N"),
			[CSharpSyntaxTree.ParseText(source)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		GeneratorDriver driver = CSharpGeneratorDriver.Create(new DatabaseGenerator().AsSourceGenerator());
		driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
		return (output, driver.GetRunResult());
	}

	[Theory]
	[InlineData("FirstContext")]
	[InlineData("RenamedContext")]
	public void StableIdentifierAndTypedFactoriesSurviveClassRenames(string name) {
		var generated = Generate($$"""
			using Microsoft.EntityFrameworkCore;
			using Pengin1011.Core;
			namespace Probe;
			[ModuleDatabase("Stable")]
			public partial class {{name}} : DbContext {
				public DbSet<Item> Items => Set<Item>();
			}
			public class Item { public int Id { get; set; } }
			""");
		Assert.Empty(generated.Result.Diagnostics);
		Assert.Empty(generated.Compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
		using var stream = new MemoryStream();
		Assert.True(generated.Compilation.Emit(stream).Success);
		var assembly = Assembly.Load(stream.ToArray());
		var attribute = assembly.GetCustomAttribute<ModuleDatabaseProviderAttribute>()!;
		var provider = (IModuleDatabaseProvider)Activator.CreateInstance(attribute.ProviderType)!;
		var registrar = new DatabaseRegistrar();
		provider.Register(registrar);
		var definition = Assert.Single(registrar.Definitions);
		Assert.Equal("Stable", definition.Id);
		using var database = definition.Create("Data Source=:memory:");
		Assert.Equal(name, database.GetType().Name);
		Assert.Equal("Data Source=:memory:", database.Database.GetConnectionString());
		Assert.Contains(database.Model.GetEntityTypes(), entity => entity.ClrType.Name == "Item");
		var source = string.Join("\n", generated.Result.GeneratedTrees.Select(tree => tree.ToString()));
		Assert.Contains("IDesignTimeDbContextFactory", source);
		Assert.DoesNotContain("Activator", source);
		Assert.DoesNotContain("OnConfiguring", source);
	}

	[Theory]
	[InlineData("[ModuleDatabase(\"A\")] public class Sample : DbContext {}", "PDB001")]
	[InlineData("[ModuleDatabase(\"A\")] public abstract partial class Sample : DbContext {}", "PDB001")]
	[InlineData("[ModuleDatabase(\"A\")] public sealed partial class Sample : DbContext {}", "PDB001")]
	[InlineData("[ModuleDatabase(\"A\")] public partial class Sample : DbContext {} public class SampleMySQL {}", "PDB004")]
	[InlineData("[ModuleDatabase(\"../A\")] public partial class Sample : DbContext {}", "PDB002")]
	[InlineData("[ModuleDatabase(\"CON\")] public partial class Sample : DbContext {}", "PDB002")]
	[InlineData("[ModuleDatabase(\"A\")] public partial class Sample : DbContext { public Sample() {} }", "PDB004")]
	[InlineData("[ModuleDatabase(\"A\")] public partial class Sample : DbContext {} public class SampleDesignFactory {}", "PDB004")]
	[InlineData("[ModuleDatabase(\"A\")] public partial class Sample : DbContext {} [ModuleDatabase(\"a\")] public partial class Other : DbContext {}", "PDB003")]
	public void InvalidDeclarationsFailAtCompileTime(string declaration, string diagnosticId) {
		var result = Generate("using Microsoft.EntityFrameworkCore; using Pengin1011.Core; " + declaration);
		var diagnostic = Assert.Single(result.Result.Diagnostics);
		Assert.Equal(diagnosticId, diagnostic.Id);
		Assert.NotEqual(diagnostic.GetMessage(CultureInfo.InvariantCulture), diagnostic.GetMessage(CultureInfo.GetCultureInfo("zh-CN")));
	}

	[Fact]
	public void NoDatabaseDeclarationGeneratesNothing() {
		var result = Generate("public class Module {}");
		Assert.Empty(result.Result.GeneratedTrees);
		Assert.Empty(result.Result.Diagnostics);
	}

	[Fact]
	public void CrossModuleCollisionDoesNotLeaveEarlierEntriesFromTheBatch() {
		var service = new DatabaseService();
		var first = new DatabaseRegistrar();
		first.Add<FakeDbContext>("Shared", (provider, connection) => new FakeDbContext(DatabaseOptions.Create<FakeDbContext>(provider, connection, "Shared")));
		service.Register(typeof(FakeDbContext).Assembly, first.Definitions);
		var second = new DatabaseRegistrar();
		second.Add<OtherDbContext>("Unused", (provider, connection) => new OtherDbContext(DatabaseOptions.Create<OtherDbContext>(provider, connection, "Unused")));
		second.Add<ClashContext>("shared", (provider, connection) => new ClashContext(DatabaseOptions.Create<ClashContext>(provider, connection, "shared")));
		Assert.Throws<InvalidOperationException>(() => service.Register(typeof(Databases).Assembly, second.Definitions));
		Assert.Throws<InvalidOperationException>(() => service.EntryOf(typeof(OtherDbContext)));
		Assert.Equal("Shared", service.EntryOf(typeof(FakeDbContext)).Id);
	}
}
