; Shipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PBG001 | ModuleBridge | Error | ModuleBridgeExport 方法必须是 public
PBG002 | ModuleBridge | Error | ModuleBridgeExport 方法所在的类型必须是 public
PBG003 | ModuleBridge | Error | 实例方法的 ModuleBridgeExport 必须声明在模块唯一的 IModuleRuntime 实现类上
PBG004 | ModuleBridge | Error | ModuleBridgeExport 方法所在的类型不能是泛型类型
PBG005 | ModuleBridge | Error | 泛型方法不能标记 ModuleBridgeExport
PBG006 | ModuleBridge | Error | 带 ref/out/in 参数或 ref、ref readonly 返回值的方法不能标记 ModuleBridgeExport
PBG007 | ModuleBridge | Error | 带可选参数的方法不能标记 ModuleBridgeExport
PBG008 | ModuleBridge | Error | 桥导出签名含消费方无法构造的类型
PBG009 | ModuleBridge | Error | 桥导出参数数量超过上限 16
PBG010 | ModuleBridge | Error | 桥导出签名含不支持的类型
PBG011 | ModuleBridge | Error | 存在重复的桥导出名
PBG012 | ModuleBridge | Error | 模块名不是合法 C# 标识符
PBG101 | ModuleBridge | Error | 无法解析桥清单条目
PBG102 | ModuleBridge | Error | 桥清单中的模块名不是合法 C# 标识符
