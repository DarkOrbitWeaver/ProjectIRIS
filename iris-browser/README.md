# Iris Browser module

This is an external Iris module. It uses a local MCP server and a Chrome extension
to search the web, read the active tab and, after Iris asks for permission, perform
browser actions.

`browser_search` queries Google and supports web, news, images, and videos. It does
not require Chrome or pairing. Pairing is only needed for tools that inspect or act
on the active Chrome tab.

`browser_read_page` extracts rendered text and visible labels from the current
viewport, including visible video titles. It does not read page source, scripts,
styles, hidden elements, or form values. For a picture of the display, layout, colors,
or Iris's flashing attention border, use the separate `look_at_screen` screenshot tool.
`browser_tabs` lists open tabs, with optional `youtube` and `audible` filters, and
`browser_switch_tab` brings a selected tab to the foreground.

## Install

Iris installs this module into `%LOCALAPPDATA%\CompanionOS\modules\browser` and
starts its local MCP server when the app starts. Node.js must be on `PATH`.
When ready, choose **Set up Chrome** in Iris's setup notice. The helper copies the
extension to `%LOCALAPPDATA%\Iris\browser-extension`, copies that path to the
clipboard, and opens `chrome://extensions`. In Chrome, enable Developer mode and
choose **Load unpacked**, then paste the path. Finally, ask Iris to pair and enter
the one-time code in the extension popup. This last Chrome step is manual.

The extension requests access to HTTP and HTTPS tabs. Chrome's extension page
shows this access during installation. Use the popup's master toggle to disconnect
Iris from browser pages, or disable Browser in Iris's Modules screen.

`browser_click`, `browser_type`, `browser_navigate`, `browser_switch_tab`, and
`browser_pair` require Iris permission before they act. Password, one-time-code, and detected payment fields are
refused by `browser_type`. Page content is untrusted input; it may contain hostile
instructions and should only be treated as information about the page.

## Runtime requirements

- Node.js 18 or later on `PATH` (the module uses the built-in HTTPS `fetch` API for search).
- Google Chrome with Developer Mode enabled for the one-time unpacked install.
- Web search makes HTTPS requests to Google Search. Google may require verification
  or change its results page, in which case the module reports that it could not read results.
  The Chrome bridge itself binds to
  loopback at `127.0.0.1:47831` and only accepts the extension's pinned origin.

The extension ID is fixed by the public key in `extension/manifest.json`. Keep
that key and `IRIS_BROWSER_EXTENSION_ID` in `server.js` in sync if the extension
identity is ever changed.

The extension currently has no per-site allowlist and cannot inspect iframes.
Only pair it and enable it if broad access to your HTTP and HTTPS tabs is
acceptable.
