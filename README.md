[marketplace]: https://marketplace.visualstudio.com/items?itemName=MadsKristensen.DocumentHealth
[vsixgallery]: http://vsixgallery.com/extension/DocumentHealth.ebd2f3af-c274-4af6-bc9d-3e929361845d/
[repo]:https://github.com/madskristensen/DocumentHealth

# Editor Info extension for Visual Studio

[![Build](https://github.com/madskristensen/DocumentHealth/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/DocumentHealth/actions/workflows/build.yaml)

Download this extension from the [Visual Studio Marketplace][marketplace]
or get the [CI build][vsixgallery].

----------------------------------------

Shows an icon above the vertical scroll bar indicating if the document has any errors or warnings. Use the existing keyboard shortcuts **Ctrl+Shift+F12** or **Alt+PgDn/PgUp** to navigate between errors and warnings in the open document.

![error](art/error.png)

When there are no errors or warnings, the icon is green and you can relax knowing that your code is in good shape.

![No errors](art/green.png)

The icon replaces the current Document Health Indicator at the bottom left of the editor.

![Indicator](art/indicator.png)

By not having the Document Health Indicator at the bottom left, we free up space for the horizontal scrollbar and reduce noise.

## How can I help?
If you enjoy using the extension, please give it a ★★★★★ rating on the [Visual Studio Marketplace][marketplace].

Should you encounter bugs or if you have feature requests, head on over to the [GitHub repo][repo] to open an issue if one doesn't already exist.

Pull requests are also very welcome, since I can't always get around to fixing all bugs myself. This is a personal passion project, so my time is limited.

Another way to help out is to [sponsor me on GitHub](https://github.com/sponsors/madskristensen).