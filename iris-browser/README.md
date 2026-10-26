# Iris Browser module

This is an optional external Iris module. The manifest is disabled by default. It
uses a local MCP server and a Chrome extension to read the active tab and, after
Iris asks for permission, perform browser actions.

## Install

1. Copy this whole folder into `%LOCALAPPDATA%\CompanionOS\modules\iris-browser`.
   If `COMPANIONOS_HOME` is set, use `<that folder>\modules\iris-browser` instead.
2. Open a terminal in that folder and run `npm install`.
3. Run `install-extension.ps1`. In Chrome, enable Developer mode and choose **Load
   unpacked**. Paste the extension folder path copied by the script.
4. In Iris, refresh Modules, enable Browser, then ask Iris to pair. Enter the
   one-time code in the extension popup.

The module requests access to HTTP and HTTPS tabs. Chrome's Developer Mode
extension page shows this access during installation. Use the popup's master
toggle to disconnect Iris from browser pages. The module remains disabled until
you explicitly enable it in Iris.

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
