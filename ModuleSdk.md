# Pengin1011.ModuleSdk

用这个包可以在独立的 C# 项目里编写 Pengin1011 模块，再把模块 DLL 装进机器人。安装 .NET 10 SDK，选用与宿主版本相同的开发包。下面以 `1.0.0` 为例。

## 准备开发包

若使用的 NuGet 源已经提供所需版本，可直接创建模块。使用本地包时，先在**框架仓库**根目录打包。

```sh
dotnet pack Pengin1011.csproj -c Release
```

将生成的 `bin/Release/Pengin1011.ModuleSdk.1.0.0.nupkg` 复制到**独立模块仓库**的 `packages/` 目录。在模块仓库根目录创建 `NuGet.Config`，让 NuGet 从本地读取框架包，并从 nuget.org 还原其余依赖。已有配置的项目可以合并这两个源及其映射。

```xml
<configuration>
  <packageSources>
    <add key="PenginLocal" value="./packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="PenginLocal"><package pattern="Pengin1011.ModuleSdk" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

## 创建模块

从模块仓库根目录创建类库。

```sh
dotnet new classlib -n MyModule -f net10.0
```

在 `MyModule/MyModule.csproj` 的 `<Project>` 内加入包引用。

```xml
<ItemGroup>
  <PackageReference Include="Pengin1011.ModuleSdk" Version="1.0.0" />
</ItemGroup>
```

将 `MyModule/Class1.cs` 替换为 `MyModule/MyModuleRuntime.cs`。每个模块提供一个运行对象，下面的最小实现即可装载。

```csharp
using Pengin1011.Core.Modules;

namespace Pengin1011.Modules.MyModule;

public sealed class MyModuleRuntime : IModuleRuntime {
	public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
	public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

## 编译并装载

在模块仓库根目录执行构建。

```sh
dotnet build MyModule/MyModule.csproj -c Release
```

停止机器人，把 `MyModule/bin/Release/net10.0/MyModule.dll` 按原文件名复制到宿主程序旁的 `module/` 目录。重启后，在机器人终端输入 `modules`，确认 `MyModule` 的状态为 `Ready`。

接下来可以给模块添加 Discord 命令，使用共享 AI 预设生成回复，或接入数据库和其他模块。[模块开发示例（英文）](https://github.com/JSTks24/Pengin1011/blob/main/DevModule.md)
