using Pengin1011.Helper;

namespace Pengin1011.Tests;

public sealed class JsonHelperTests {
	private sealed class SampleData {
		public string Name { get; set; } = "";
		public int Count { get; set; }
		public List<string> Items { get; set; } = [];
		public SampleNested? Nested { get; set; }
	}

	private sealed class SampleNested {
		public string Value { get; set; } = "";
	}

	private static string TempDir() {
		return Path.Combine(Path.GetTempPath(), $"qq1011_{Guid.NewGuid():N}");
	}

	[Fact]
	public void SaveThenLoad_RoundTrips() {
		var dir = TempDir();
		var path = Path.Combine(dir, "data.json");
		var data = new SampleData {
			Name = "测试",
			Count = 42,
			Items = ["a", "b", "c"],
			Nested = new SampleNested { Value = "nested" },
		};
		try {
			Assert.True(JsonHelper.Save(path, data));
			var loaded = JsonHelper.Load<SampleData>(path);
			Assert.NotNull(loaded);
			Assert.Equal("测试", loaded!.Name);
			Assert.Equal(42, loaded.Count);
			Assert.Equal(["a", "b", "c"], loaded.Items);
			Assert.Equal("nested", loaded.Nested!.Value);
		} finally {
			try { Directory.Delete(dir, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Load_MissingFile_Throws() {
		var dir = TempDir();
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, "missing.json");
		Assert.Throws<FileNotFoundException>(() => JsonHelper.Load<SampleData>(path));
	}

	[Fact]
	public void Load_BadJson_Throws() {
		var dir = TempDir();
		var path = Path.Combine(dir, "bad.json");
		try {
			Directory.CreateDirectory(dir);
			File.WriteAllText(path, "{ broken");
			Assert.Throws<System.Text.Json.JsonException>(() => JsonHelper.Load<SampleData>(path));
		} finally {
			try { Directory.Delete(dir, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Load_AllowsCommentsAndTrailingCommas() {
		var dir = TempDir();
		var path = Path.Combine(dir, "comments.json");
		try {
			Directory.CreateDirectory(dir);
			File.WriteAllText(path, """
				{
				  // 行注释
				  "Name": "n",
				  "Count": 1,
				}
				""");
			var loaded = JsonHelper.Load<SampleData>(path);
			Assert.NotNull(loaded);
			Assert.Equal("n", loaded!.Name);
			Assert.Equal(1, loaded.Count);
		} finally {
			try { Directory.Delete(dir, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Save_OverwritesExisting() {
		var dir = TempDir();
		var path = Path.Combine(dir, "data.json");
		try {
			Assert.True(JsonHelper.Save(path, new SampleData { Name = "first" }));
			Assert.True(JsonHelper.Save(path, new SampleData { Name = "second" }));
			var loaded = JsonHelper.Load<SampleData>(path);
			Assert.Equal("second", loaded!.Name);
		} finally {
			try { Directory.Delete(dir, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Save_CreatesMissingDirectories() {
		var dir = TempDir();
		var path = Path.Combine(dir, "deep", "nested", "data.json");
		try {
			Assert.True(JsonHelper.Save(path, new SampleData { Name = "x" }));
			Assert.True(File.Exists(path));
		} finally {
			try { Directory.Delete(dir, true); } catch (IOException) { }
		}
	}

	[Fact]
	public void Save_LeavesNoTempFiles() {
		var dir = TempDir();
		var path = Path.Combine(dir, "data.json");
		try {
			JsonHelper.Save(path, new SampleData { Name = "a" });
			JsonHelper.Save(path, new SampleData { Name = "b" });
			JsonHelper.Save(path, new SampleData { Name = "c" });
			Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
			Assert.Single(Directory.GetFiles(dir));
		} finally {
			try { Directory.Delete(dir, true); } catch (IOException) { }
		}
	}
}
