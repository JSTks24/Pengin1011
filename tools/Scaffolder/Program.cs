using System.Diagnostics;

var moduleName = args[0];
var templateDir = args[1];
var moduleDir = Path.GetFullPath(args[2]);
if (moduleName != Path.GetFileName(moduleDir) || moduleName is "." or "..") return 1;
var withDatabase = args.Length > 3 && bool.TryParse(args[3], out var requested) && requested;

(string Template, string Output)[] mappings =
[
	("Module.csproj", $"{moduleName}.csproj"),
	("ModuleClass", $"{moduleName}.cs"),
	("ModuleRuntime", $"{moduleName}Runtime.cs"),
	("ModuleOptions", $"{moduleName}Options.cs"),
	("ModuleConfig", $"{moduleName}Config.cs"),
	("ModuleDbContext", $"{moduleName}DbContext.cs"),
	("ModuleStrings", "Localization/Strings.resx"),
	("ModuleStringsZhCn", "Localization/zh-CN/Strings.resx"),
];

var files = mappings.Where(file => withDatabase || file.Template != "ModuleDbContext").ToArray();
var stagingRoot = moduleDir + "." + Guid.NewGuid().ToString("N") + ".tmp";
var staging = Path.Combine(stagingRoot, moduleName);
Directory.CreateDirectory(staging);
var rollback = false;
try {
	foreach (var (template, output) in files) {
		var content = File.ReadAllText(Path.Combine(templateDir, $"{template}.tpl"));
		var target = Path.Combine(staging, output);
		Directory.CreateDirectory(Path.GetDirectoryName(target)!);
		File.WriteAllText(target, content.Replace("__MODULE__", moduleName).ReplaceLineEndings("\n"));
	}
	Directory.Move(staging, moduleDir);
	rollback = true;
	if (args.Length > 4) {
		using var solutionLock = new FileStream(args[4] + ".scaffold.lock", FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
		var start = new ProcessStartInfo("dotnet") {
			ArgumentList = { "sln", args[4], "add", Path.Combine(moduleDir, $"{moduleName}.csproj") },
		};
		using var process = Process.Start(start)!;
		process.WaitForExit();
		if (process.ExitCode != 0) return process.ExitCode;
	} else {
		Console.WriteLine($"模块骨架已生成：{moduleDir}");
	}
	rollback = false;
	return 0;
} finally {
	Directory.Delete(stagingRoot, true);
	if (rollback) {
		foreach (var (_, output) in files) {
			File.Delete(Path.Combine(moduleDir, output));
		}
		foreach (var directory in new[] { Path.Combine(moduleDir, "Localization", "zh-CN"), Path.Combine(moduleDir, "Localization"), moduleDir }) {
			if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
		}
	}
}
