using System.IO;
using Microsoft.CodeAnalysis;

namespace Pengin1011Generator.ModuleBridge;

internal sealed class BridgeTypeScope {
	private string? _frameworkDirectory;
	private bool _frameworkResolved;

	internal BridgeTypeScope(Compilation compilation, string moduleAssemblyName) {
		Compilation = compilation;
		ModuleAssemblyName = moduleAssemblyName;
	}

	internal Compilation Compilation { get; }

	internal string ModuleAssemblyName { get; }

	internal string? FrameworkDirectory {
		get {
			if (_frameworkResolved) return _frameworkDirectory;
			_frameworkResolved = true;
			var core = Compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly;
			if (core != null && Compilation.GetMetadataReference(core) is PortableExecutableReference { FilePath: { Length: > 0 } path }) {
				_frameworkDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
			}
			return _frameworkDirectory;
		}
	}
}
