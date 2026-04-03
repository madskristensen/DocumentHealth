# 🎯 Fix Performance, Reliability, and Memory Issues

## Understanding
The DocumentHealth extension has several issues affecting performance (repeated Regex compilation, redundant array copies), memory (context menus created per-glyph/per-line), reliability (race conditions, silent exception swallowing), and code quality (missing IDisposable, empty tooltip labels). These need to be addressed to improve the extension's efficiency and stability.

## Assumptions
- The extension targets .NET Framework 4.8
- Regex patterns are static and won't change at runtime
- Context menus can be lazily created and reused
- Tooltip labels were intended to show counts but implementation was incomplete

## Approach
Address issues in order of impact: first the high-frequency performance issues (Regex compilation), then memory leaks (context menu creation), then reliability (race conditions, exception handling), and finally code quality improvements. Each fix will be minimal and targeted to avoid introducing regressions.

## Key Files
- `src/DiagnosticDataProvider.cs` - Regex compilation, exception handling, redundant ToArray
- `src/DiagnosticGlyphFactory.cs` - Context menu per glyph
- `src/InlineDiagnosticsAdornment.cs` - Context menu per line, missing IDisposable
- `src/HealthMargin.cs` - Dispose race condition
- `src/HealthStatusControl.cs` - Empty tooltip labels
- `src/DiagnosticContextMenu.cs` - Process.Start safety

## Risks & Open Questions
- Lazy context menu creation changes timing of menu construction
- Changing exception handling could surface previously hidden issues

**Progress**: 100% [██████████]

**Last Updated**: 2026-04-03 03:22:14

## 📝 Plan Steps
- ✅ **Add compiled static Regex fields in DiagnosticDataProvider**
- ✅ **Remove redundant ToArray call in DiagnosticDataProvider**
- ✅ **Fix tooltip labels not being updated in HealthStatusControl**
- ✅ **Fix race condition in HealthMargin.Dispose**
- ✅ **Add lazy context menu creation in DiagnosticGlyphFactory**
- ✅ **Add lazy context menu creation in InlineDiagnosticsAdornment**
- ✅ **Add explicit ProcessStartInfo in DiagnosticContextMenu**
- ✅ **Add IDisposable to InlineDiagnosticsAdornment**
- ✅ **Improve exception handling with specific catch blocks**
- ✅ **Build and verify changes compile successfully**

