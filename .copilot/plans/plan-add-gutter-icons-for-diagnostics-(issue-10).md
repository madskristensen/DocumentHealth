# 🎯 Add Gutter Icons for Diagnostics (Issue #10)

## Understanding
The user wants to implement issue #10: gutter icons (glyphs in the editor indicator margin) that show severity-colored icons for lines with diagnostics. They want KnownMonikers for the icons, a setting to disable the feature, and the same themed context menu that inline diagnostics adornments use (copy message, copy code, search online).

## Assumptions
- This is a .NET Framework 4.8 VSIX project using VSSDK/Community Toolkit patterns
- Use `IGlyphFactoryProvider` + `IGlyphFactory` with a custom `IGlyphTag` and `ITaggerProvider` + `ITagger<T>`
- Use `CrispImage` with `KnownMonikers.StatusError`, `KnownMonikers.StatusWarning`, `KnownMonikers.StatusInformation` for glyphs
- Reuse the same context menu pattern from `InlineDiagnosticsAdornment.CreateDiagnosticContextMenu`
- The tagger needs to produce tags on lines that have error tags, reusing the same error tag aggregation approach
- Add a `ShowGutterIcons` boolean setting to `General.cs` under "Inline Diagnostics" category

## Approach
The implementation requires three new files and modifications to the settings:

1. **DiagnosticGlyphTag** - A custom `IGlyphTag` that carries severity and diagnostic info so the glyph factory can render the right icon and attach the context menu.
2. **DiagnosticGlyphTagger** + **DiagnosticGlyphTaggerProvider** - An `ITaggerProvider` that creates taggers producing `DiagnosticGlyphTag` for lines with diagnostics. It will aggregate `IErrorTag` instances (same as `InlineDiagnosticsAdornment`) and map them to glyph tags.
3. **DiagnosticGlyphFactory** + **DiagnosticGlyphFactoryProvider** - An `IGlyphFactoryProvider` that creates `CrispImage` controls with KnownMonikers based on severity, with a right-click context menu.
4. **Settings** - Add `ShowGutterIcons` to [General.cs](src/Options/General.cs).

The tagger will check `General.Instance.ShowGutterIcons` and return no tags when disabled. The context menu will be the same as in `InlineDiagnosticsAdornment` — we'll extract the static method `CreateDiagnosticContextMenu` so both can share it.

## Key Files
- src/Options/General.cs - Add new setting
- src/DiagnosticGlyphTag.cs - New: custom IGlyphTag carrying severity/diagnostic data
- src/DiagnosticGlyphTagger.cs - New: ITaggerProvider + ITagger producing glyph tags from error tags
- src/DiagnosticGlyphFactory.cs - New: IGlyphFactoryProvider + IGlyphFactory rendering KnownMoniker icons
- src/InlineDiagnosticsAdornment.cs - Extract shared context menu builder
- src/DocumentHealth.csproj - Register new files

## Risks & Open Questions
- The tagger needs debounced updates similar to inline diagnostics to avoid lag
- Need to make the `LineDiagnostic` class and `DiagnosticSeverity` enum accessible from glyph factory (currently internal to InlineDiagnosticsAdornment)

**Progress**: 100% [██████████]

**Last Updated**: 2026-04-02 23:15:17

## 📝 Plan Steps
- ✅ **Extract LineDiagnostic and DiagnosticSeverity from InlineDiagnosticsAdornment into a shared scope**
- ✅ **Extract CreateDiagnosticContextMenu to a shared static helper accessible from both adornments and the glyph factory**
- ✅ **Add ShowGutterIcons setting to General.cs**
- ✅ **Create DiagnosticGlyphTag.cs with custom IGlyphTag carrying diagnostic data**
- ✅ **Create DiagnosticGlyphTagger.cs with ITaggerProvider and ITagger implementation**
- ✅ **Create DiagnosticGlyphFactory.cs with IGlyphFactoryProvider and IGlyphFactory**
- ✅ **Add new files to DocumentHealth.csproj**
- ✅ **Build and verify compilation**

