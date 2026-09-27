var moduleName = args[0];
var templateDir = args[1];
var moduleDir = args[2];

(string Template, string Output)[] mappings =
[
	("Module.csproj", $"{moduleName}.csproj"),
	("ModuleClass", $"{moduleName}.cs"),
	("ModuleRuntime", $"{moduleName}Runtime.cs"),
	("ModuleConfig", $"{moduleName}Config.cs"),
	("ModuleDbContext", $"{moduleName}DbContext.cs"),
];

Directory.CreateDirectory(moduleDir);
foreach (var (template, output) in mappings) {
	var content = File.ReadAllText(Path.Combine(templateDir, $"{template}.tpl"));
	File.WriteAllText(Path.Combine(moduleDir, output), content.Replace("__MODULE__", moduleName));
}
Console.WriteLine($"模块骨架已生成：{moduleDir}");
