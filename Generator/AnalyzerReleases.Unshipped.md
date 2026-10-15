; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PBG013 | ModuleBridge | Error | Bridge export name collides with a generated member
PBG103 | ModuleBridge | Error | Bridge manifest format is outdated
PBG104 | ModuleBridge | Error | Bridge manifest export count mismatch
PBG105 | ModuleBridge | Error | Duplicate bridge manifest for one module
PBG106 | ModuleBridge | Error | Duplicate export name inside a bridge manifest
PAP001 | AIPreset | Error | AI preset name must be a string literal or const
PAP002 | AIPreset | Error | AI preset entry point must not be referenced as a method group
