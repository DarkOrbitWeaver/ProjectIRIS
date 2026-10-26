# Iris Module SDK: current contract and target

Iris is intended to grow through modules, not through a permanently hardcoded list of
integrations. A module may represent a local capability or an external service such as
Telegram, a browser search provider, or a music library. Modules use Iris's routing,
permission, activity, and shell contracts so each integration feels like part of one app.

## What works now

- Built-in C# modules implement `IAssistantModule` and register with `ModuleHost`.
- External folders may declare a `plugin.json` using `command` or MCP stdio transport.
- External modules default to disabled unless their manifest explicitly sets `"disabled": false`;
  enabling them is a user action and takes effect after Iris restarts. Existing manifests that
  omitted the field now require that explicit enable step.
- External tools are adapted into the same dynamic router and execution boundary. MCP tools
  are currently repeated in the manifest rather than discovered from `tools/list`, and receive
  stable module-qualified router names so unrelated modules can reuse local tool names.
- External modules may declare up to six sidebar surfaces, each with at most twenty native
  elements: plain text, text inputs, and buttons bound to that module's declared tools. A
  button action is checked against its owning module and then uses the same permission gate
  as a model-selected tool. All inputs in one surface are string fields shared by its buttons
  and must match each target tool's JSON schema. Semantic icon keys use Iris's built-in glyph set;
  a module's icon also appears in the activity card.
- External tools use Iris's existing system-critical permission gate. A first call asks for
  approval; a saved "always allow" grant can authorize later calls to that tool without another
  prompt. A tool may name one capability in `permission`, and the manifest must also list that
  scope in `asksPermission`. Iris shows the scope in the permission prompt and audit trail. The
  scope is currently descriptive metadata: it does not enforce OS isolation or independently
  grant/revoke access across tools. External processes still run with the current user's OS access.
- MCP tools may send `notifications/progress` after Iris supplies `_meta.progressToken` with
  `tools/call`. Iris correlates those messages to the plugin, tool, and conversation and
  publishes `ModuleProgressEvent`. Intermediate updates are throttled to avoid UI event floods.
- The activity card shows the current conversation/tool step without taking focus. The compact
  call pill remains a separate surface. Progress bars appear only when the module reports both
  a finite progress value and a finite positive total; otherwise the card shows the step only.
- A bare executable such as `node` or `python` resolves through `PATH`; a relative command path
  resolves inside the module folder. External code runs only after the module is enabled.
- A command module receives one JSON line on stdin with its module id, local tool name, and
  arguments. MCP modules receive the standard `tools/call` request with the local tool name.

## What the public SDK still needs

The current mechanism is not yet the complete extensibility promise. Sidebar controls are
static, use semantic glyphs rather than a module's own branded assets, and cannot yet provide
independent overlays, main tabs, dynamic state updates, or typed form controls. The next SDK
work must define and implement:

1. A versioned manifest and authoring kit with validation, dynamic tool discovery, lifecycle
   hooks, installation review, enable/disable/update/removal, and actionable diagnostics.
2. Permission scopes that are explicit, reviewable, revocable, and enforced by Iris. Read
   access to contacts must be distinct from sending messages; consequential actions must still
   have user control. Current `permission` labels document and display intent; they are not yet
   independent grants or OS-enforced capabilities.
3. A declarative UI contract that grows the current sidebar schema into stateful cards, typed
   forms, branded icons, buttons, and independently controlled overlays. Iris owns rendering,
   focus, accessibility, placement, and visibility policy; modules publish state and named
   actions instead of loading arbitrary UI code into the shell.
4. A shared activity lifecycle for queued, running, waiting, completed, partial, failed, and
   cancelled work. Events must carry stable task and module IDs, current step, optional truthful
   progress, wait reason, and available controls. Chat, overlays, notices, and diagnostics must
   present the same state.
5. Resource limits, cancellation, bounded I/O and output, version compatibility, crash isolation,
   and audit records that scale as many modules are installed while the model sees only relevant
   tools.

## Security and trust boundary

External integrations run as separate processes through declared transports. Iris must not load
arbitrary plugin DLLs into its process or silently execute newly discovered code. Install and
update flows must identify the publisher/source, show requested permissions, and require the user
to review them. Tokens and account credentials must be handled through a dedicated secret store,
never copied into prompts, logs, tool descriptions, or ordinary module files.

## Example author experience

A messaging module should be able to declare `contacts.read` and `messages.send` separately,
provide a recognizable icon, and publish steps such as “Finding Alex,” “Draft ready,” and
“Message sent.” Iris should show the module's activity and controls in the shared activity card;
she must not claim progress the service did not report. A sent message is an external side
effect, so the user must be able to review and authorize that action according to the configured
permission policy.

### Current schema-v1 sidebar example

The module's tool schema is declared once in `plugin.json`; a button names that local tool and
the input names map to JSON argument properties. `icon` is a semantic key from Iris's
allow-list. The module stays disabled until the user explicitly enables it.

```json
{
  "schema": 1,
  "id": "telegram",
  "name": "Telegram",
  "version": "0.1.0",
  "description": "Find contacts and send messages.",
  "icon": "telegram",
  "transport": "mcp",
  "command": ["node", "server.js"],
  "disabled": true,
  "asksPermission": ["messages.send"],
  "tools": [
    {
      "name": "send_message",
      "description": "Send a message to a contact.",
      "permission": "messages.send",
      "schema": {
        "type": "object",
        "properties": {
          "recipient": { "type": "string" },
          "message": { "type": "string" }
        },
        "required": ["recipient", "message"]
      }
    }
  ],
  "surfaces": [
    {
      "id": "compose",
      "title": "New message",
      "slot": "sidebar",
      "icon": "telegram",
      "elements": [
        { "id": "recipient", "type": "input", "name": "recipient", "label": "To", "placeholder": "Contact" },
        { "id": "message", "type": "input", "name": "message", "label": "Message" },
        { "id": "send", "type": "button", "label": "Send", "tool": "send_message" }
      ]
    }
  ]
}
```
