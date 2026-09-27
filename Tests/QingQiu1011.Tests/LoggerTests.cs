using System.Text;
using QingQiu1011.Core.Logging;

namespace QingQiu1011.Tests;

public sealed class LoggerTests {
	private sealed class FakeSource;

	private static string TempLog(string fileName) {
		return Path.Combine(Path.GetTempPath(), $"qq1011_{Guid.NewGuid():N}", "logs", fileName);
	}

	private static void Cleanup(string path) {
		Logger.SetLogPathForTest(null);
		Logger.SetInfoPathForTest(null);
		try { Directory.Delete(Path.GetDirectoryName(Path.GetFullPath(path))!, true); } catch (IOException) { }
	}

	[Fact]
	public void Error_WritesMessageToFile() {
		var path = TempLog("error.log");
		Logger.SetLogPathForTest(path);
		try {
			Logger.Error(typeof(FakeSource), "测试错误");
			Assert.True(File.Exists(path));
			var content = File.ReadAllText(path);
			Assert.Contains("[FakeSource] 测试错误", content);
			Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", content);
		} finally {
			Cleanup(path);
		}
	}

	[Fact]
	public void Error_WritesExceptionDetails() {
		var path = TempLog("error.log");
		Logger.SetLogPathForTest(path);
		try {
			Logger.Error(typeof(FakeSource), new InvalidOperationException("炸了"), "操作失败");
			var content = File.ReadAllText(path);
			Assert.Contains("[FakeSource] 操作失败", content);
			Assert.Contains("System.InvalidOperationException: 炸了", content);
		} finally {
			Cleanup(path);
		}
	}

	[Fact]
	public void Error_ExceptionOnly_WritesTypeAndMessage() {
		var path = TempLog("error.log");
		Logger.SetLogPathForTest(path);
		try {
			Logger.Error(typeof(FakeSource), new ArgumentException("参数错误"));
			var content = File.ReadAllText(path);
			Assert.Contains("[FakeSource] System.ArgumentException: 参数错误", content);
		} finally {
			Cleanup(path);
		}
	}

	[Fact]
	public void Error_WritesToConsoleError() {
		var path = TempLog("error.log");
		Logger.SetLogPathForTest(path);
		var original = Console.Error;
		var writer = new StringBuilder();
		var stringWriter = new StringWriter(writer);
		Console.SetError(stringWriter);
		try {
			Logger.Error(typeof(FakeSource), "控制台测试");
		} finally {
			Console.SetError(original);
			stringWriter.Dispose();
			Cleanup(path);
		}
		Assert.Contains("[FakeSource] 控制台测试", writer.ToString());
	}

	[Fact]
	public void Info_WritesMessageToInfoFile() {
		var errorPath = TempLog("error.log");
		var infoPath = TempLog("info.log");
		Logger.SetLogPathForTest(errorPath);
		Logger.SetInfoPathForTest(infoPath);
		try {
			Logger.Info(typeof(FakeSource), "提示信息");
			Assert.True(File.Exists(infoPath));
			Assert.False(File.Exists(errorPath));
			var content = File.ReadAllText(infoPath);
			Assert.Contains("[FakeSource] 提示信息", content);
			Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", content);
		} finally {
			Cleanup(infoPath);
		}
	}

	[Fact]
	public void Info_WritesToConsoleOut() {
		var infoPath = TempLog("info.log");
		Logger.SetInfoPathForTest(infoPath);
		var original = Console.Out;
		var writer = new StringBuilder();
		var stringWriter = new StringWriter(writer);
		Console.SetOut(stringWriter);
		try {
			Logger.Info(typeof(FakeSource), "控制台提示");
		} finally {
			Console.SetOut(original);
			stringWriter.Dispose();
			Cleanup(infoPath);
		}
		Assert.Contains("[FakeSource] 控制台提示", writer.ToString());
	}
}
