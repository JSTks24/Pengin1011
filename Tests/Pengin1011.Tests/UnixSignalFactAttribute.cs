namespace Pengin1011.Tests;

internal sealed class UnixSignalFactAttribute : FactAttribute {
	public UnixSignalFactAttribute() {
		if (OperatingSystem.IsWindows()) Skip = "The real SIGTERM process test requires Unix; Windows console close uses a different operating system signal.";
	}
}
