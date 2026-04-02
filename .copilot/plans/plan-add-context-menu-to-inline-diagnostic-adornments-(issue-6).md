# 🎯 Add context menu to inline diagnostic adornments (issue #6)

## Understanding
Issue #6 requests right-click context menu on inline diagnostic adornments with: "Copy diagnostic message", "Copy diagnostic code", and "Search online". The adornment's `TextBlock` currently has `IsHitTestVisible = false`, so it doesn't receive mouse events. The context menu must use the same `ThemedContextMenuHelper` for VS-themed appearance.

## Assumptions
- The context menu should appear on right-click of the inline text adornment (the `TextBlock` rendered by `RenderInlineMessage`)
- "Search online" opens the default browser with a Bing search for the diagnostic code + message
- The `LineDiagnostic` data needs to be accessible from the context menu event handlers
- CrispImage with KnownMonikers should be used for menu item icons

## Approach
The `TextBlock` in `RenderInlineMessage` needs `IsHitTestVisible = true` and a themed `ContextMenu` attached. The `LineDiagnostic` data will be stored on the `TextBlock.Tag` so the menu item click handlers can access the message and code. The context menu will be created once per adornment instance and reused, with items updating their enabled state based on the diagnostic (e.g., "Copy diagnostic code" disabled when no code is present). We'll use `System.Diagnostics.Process.Start` for the browser search and `System.Windows.Clipboard` for copy.

## Key Files
- `src/InlineDiagnosticsAdornment.cs` — add context menu creation and wire it to the TextBlock
- `src/ThemedContextMenuHelper.cs` — already exists, reused for theming

## Risks & Open Questions
- Search URL format: using Bing search as default

**Progress**: 100% [██████████]

**Last Updated**: 2026-04-02 23:03:42

## 📝 Plan Steps
- ✅ **Add context menu creation method to InlineDiagnosticsAdornment with Copy Message, Copy Code, and Search Online items using CrispImage icons**
- ✅ **Modify RenderInlineMessage to set IsHitTestVisible=true, store LineDiagnostic in Tag, and attach the context menu**
- ✅ **Implement click handlers for clipboard copy and browser search**
- ✅ **Build and verify compilation**

