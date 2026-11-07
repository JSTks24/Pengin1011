using Pengin1011.Core;

namespace Pengin1011.Tests;

internal static class DatabaseTestSetup {
	internal static void SetDirectory(string? directory) {
		Databases.SetDataDirectoryForTest(directory);
		if (directory == null) return;
		var registrar = new DatabaseRegistrar();
		registrar.Add<FakeDbContext>("Fake", options => new FakeDbContext(options));
		registrar.Add<OtherDbContext>("Other", options => new OtherDbContext(options));
		registrar.Add<BareDbContext>("Bare", options => new BareDbContext(options));
		registrar.Add<ClashDbContext>("Clash", options => new ClashDbContext(options));
		registrar.Add<ShutdownDbContext>("Shutdown", options => new ShutdownDbContext(options));
		registrar.Add<DriftedDbContext>("Drifted", options => new DriftedDbContext(options));
		Databases.RegisterForTest(registrar);
	}
}
