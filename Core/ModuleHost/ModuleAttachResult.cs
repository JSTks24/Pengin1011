namespace Pengin1011.Core;

public sealed record ModuleAttachResult(IReadOnlyList<LoadedModule> Valid, IReadOnlyList<LoadedModule> Invalid, IReadOnlyList<string> Errors);
