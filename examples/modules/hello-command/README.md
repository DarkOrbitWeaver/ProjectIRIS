# Hello command module

This is a small Windows PowerShell 5.1 example of Iris's folder-based module API. It demonstrates
startup tool discovery, a native sidebar input/button, and truthful live progress. It has no
third-party packages and does not access accounts or make network requests.

To try it, copy this folder into Iris's module directory (`%LOCALAPPDATA%\CompanionOS\modules`,
or the `modules` directory under the configured `COMPANIONOS_HOME`). In Iris, open Modules, choose
**Refresh modules**, review the card, then choose **Enable now**. Iris discovers its tools and adds
the module immediately. Its code runs as your Windows user, so only enable code you trust.

Use this as a protocol example, not a privileged sandbox. For a service that needs a persistent
connection or login session, use MCP transport; command modules start a fresh process for each
discovery or tool call.
