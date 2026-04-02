[marketplace]: https://marketplace.visualstudio.com/items?itemName=MadsKristensen.DocumentHealth
[vsixgallery]: http://vsixgallery.com/extension/DocumentHealth.ebd2f3af-c274-4af6-bc9d-3e929361845d/
[repo]: https://github.com/madskristensen/DocumentHealth

# Document Health for Visual Studio

[![Build](https://github.com/madskristensen/DocumentHealth/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/DocumentHealth/actions/workflows/build.yaml)
[![Visual Studio Marketplace](https://img.shields.io/visual-studio-marketplace/v/MadsKristensen.DocumentHealth?label=VS%20Marketplace)](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.DocumentHealth)
[![Visual Studio Marketplace Downloads](https://img.shields.io/visual-studio-marketplace/d/MadsKristensen.DocumentHealth)](https://marketplace.visualstudio.com/items?itemName=MadsKristensen.DocumentHealth)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/madskristensen)](https://github.com/sponsors/madskristensen)

Download this extension from the [Visual Studio Marketplace][marketplace] or get the [CI build][vsixgallery].

---

A lightweight Visual Studio extension that displays document health status (errors, warnings, and messages) as an icon above the vertical scroll bar.

## Features

### At-a-Glance Status Indicator

An icon at the top of the scroll bar shows your document's health:

| Icon | Status |
|------|--------|
| 🟢 Green | No errors or warnings |
| 🟡 Yellow | Warnings present |
| 🔴 Red | Errors present |

![Tooltip showing error and warning counts](art/tooltip.gif)

### Hover for Details

Hover over the icon to see the exact count of errors, warnings, and messages.

![No errors indicator](art/green.png)

### Cleaner Editor Layout

By default, this extension replaces the built-in *Document Health Indicator* at the bottom left of the editor, freeing up space for the horizontal scrollbar.

You can keep the built-in indicator if you prefer (see **Options** below).

![Built-in indicator comparison](art/indicator.png)

## Keyboard Shortcuts

Use Visual Studio's built-in shortcuts to navigate between issues:

| Shortcut | Action |
|----------|--------|
| `Ctrl+Shift+F12` | Go to next error |
| `Alt+PgDn` | Go to next error/warning |
| `Alt+PgUp` | Go to previous error/warning |

You can also **click the icon** to jump to the next error, or **right-click** to access additional options:

- Go to Next/Previous Error
- Open Error List
- Settings

### Inline Diagnostics

See diagnostic messages directly in the editor, right at the end of the line that caused them — no need to check the Error List.

### Line Highlighting

Lines with errors or warnings are highlighted with a subtle background tint colored by severity:

- **Red** tint for errors
- **Yellow** tint for warnings

Both features can be toggled independently in the options.

## Options

Configure the extension under **Tools → Options → Environment → Document Health**:

| Option | Description | Default |
|--------|-------------|---------|
| Update delay (ms) | Delay before updating the indicator after changes. Higher values improve performance during rapid typing. | 250 |
| Show messages count | Include suggestions and informational messages in the tooltip count. | true |
| Replace built-in indicator | Disable Visual Studio's built-in file health indicator and use this extension's indicator instead. | true |
| Show inline messages | Display diagnostic messages inline at the end of lines containing errors or warnings. | true |
| Highlight lines | Highlight the background of lines containing errors or warnings with a severity-colored tint. | true |

### Notes on behavior

- The indicator now performs an initial health calculation when a document opens.
- Updates use debounce behavior, so rapid typing delays refresh until activity settles.

## How Can I Help?

If you find this extension useful:

- ⭐ [Rate it on the Visual Studio Marketplace][marketplace]
- 🐛 [Report bugs or request features][repo]
- 🔧 Submit a pull request
- 💖 [Sponsor me on GitHub](https://github.com/sponsors/madskristensen)