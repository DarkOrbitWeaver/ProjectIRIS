# Iris Browser module

This is an external Iris module. It uses a local MCP server and a Chrome extension
to read the active tab and, after Iris asks for permission, perform browser actions.

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

`browser_click`, `browser_type`, `browser_navigate`, and `browser_pair` require a
fresh Iris confirmation. Password, one-time-code, and detected payment fields are
refused by `browser_type`. Page content is untrusted input; it may contain hostile
instructions and should only be treated as information about the page.

## Runtime requirements

- Node.js on `PATH`.
- Google Chrome with Developer Mode enabled for the one-time unpacked install.
- Network access is not used by the bridge; the MCP server binds to loopback at
  `127.0.0.1:47831` and only accepts the extension's pinned origin.

The extension ID is fixed by the public key in `extension/manifest.json`. Keep
that key and `IRIS_BROWSER_EXTENSION_ID` in `server.js` in sync if the extension
identity is ever changed.

The extension currently has no per-site allowlist and cannot inspect iframes.
Only pair it and enable it if broad access to your HTTP and HTTPS tabs is
acceptable.
