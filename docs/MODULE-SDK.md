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
- External tools are adapted into the same dynamic router and execution boundary, and receive
  stable module-qualified router names so unrelated modules can reuse local tool names. MCP
  modules may set `"discoverTools": true` to load tool schemas from `tools/list` during startup;
  each tool name and schema then comes from its server. The manifest can still declare
  name-to-permission policy entries without duplicating those schemas. Discovery follows MCP
  pagination and caps each module at 128 tools and 16 pages, each schema at 16 KB and description
  at 1,000 characters, with a 10-second total discovery budget per module. If discovery fails or
  the live surfaces do not match the discovered schemas, the module is registered without
  callable discovered tools and the reason is logged. Discovered tools without a matching
  manifest permission policy still pass through the conservative system-critical approval gate;
  they simply have no named scope attached to the prompt yet.
- External modules may declare up to six surfaces (up to five sidebar surfaces and one
  independent overlay), each with at most twenty native elements: plain text, text inputs,
  constrained select controls, and buttons bound to that module's offered tools. Select options
  map to string enum values in the target tool schema. The shell shows a module overlay while its
  tool is running, updates it from real MCP progress, and lets the user hide it for that task. A
  button action is checked against its owning module and then uses the same permission gate
  as a model-selected tool. All inputs in one surface are string fields shared by its buttons
  and must match each target tool's JSON schema. Semantic icon keys use Iris's built-in glyph set;
  a module's icon also appears in the activity card.
- The request router keeps the model-facing list to at most eight tools, chooses no more than two
  relevant external modules, and ranks a large module's tools against the current request rather
  than taking the first entries. It caches a compact module fingerprint when registering the
  module and keeps the full installed tool catalog out of the repeated system prompt.
- External tools use Iris's existing system-critical permission gate. A first call asks for
  approval; a saved "always allow" grant can authorize later calls to that tool without another
  prompt. A tool may name one capability in `permission`, and the manifest must also list that
  scope in `asksPermission`. Iris shows the scope in the permission prompt and audit trail, and
  remembers "always allow" per module and scope; tools with another scope ask separately. Saved
  scope grants can be revoked in Settings. This enforces consent at Iris's tool-call boundary,
  not OS isolation: an external process still runs with the current user's OS access, so the
  user must trust enabled module code and its declared behavior.
- MCP tools may send `notifications/progress` after Iris supplies `_meta.progressToken` with
  `tools/call`. Iris correlates those messages to the plugin, tool, and conversation and
  publishes `ModuleProgressEvent`. Intermediate updates are throttled to avoid UI event floods.
- The activity card shows the current conversation/tool step without taking focus. The compact
  call pill remains a separate surface. Progress bars appear only when the module reports both
  a finite progress value and a finite positive total; otherwise the card shows the step only.
- External tools may opt into bounded background execution with `"background": true` in their
  declared tool policy. Iris checks permission before starting, returns a task ID to the current
  conversation, and keeps the task independent of later chat turns. At most two tasks run at once,
  at most four may be queued or running, and up to 32 recent task records are retained. The shared
  activity card shows current progress and offers stop/hide controls; the background-task tools
  can list status or request cancellation by ID. Command processes are killed on cancellation;
  MCP tasks receive `notifications/cancelled` (the server still has to honor it). Background
  actions have a five-minute host timeout. This is bounded concurrency, not permission to create
  unbounded work.
- A bare executable such as `node` or `python` resolves through `PATH`; a relative command path
  resolves inside the module folder. External code runs only after the module is enabled.
- A command module receives one JSON line on stdin with its module id, local tool name, and
  arguments (plus protocol version 1). It may write ordinary text for its result, or emit
  JSONL `progress` and `result` envelopes for shared live activity; stdout is capped at 256 KiB,
  stderr storage at 16 KiB, and oversized stdout terminates the process. MCP modules receive the
  standard `tools/call` request with the local tool name.

### Command module progress protocol

Existing command modules may keep writing plain text. Version 1 modules can instead report
intermediate work and a structured final result on stdout, one JSON object per line:

```json
{"type":"progress","message":"Finding Alex","progress":1,"total":3}
{"type":"result","ok":true,"text":"Found Alex in contacts."}
```

Progress numbers are optional; omit them when there is no honest total. `result.ok` defaults to
true and `result.text` is returned to Iris. The module process is isolated from Iris's UI process,
and cancellation terminates its process tree. The command runs with the user's OS permissions, so
the existing module enablement and capability approval still matter.

## What the public SDK still needs

The current mechanism is not yet the complete extensibility promise. Controls are static, use
semantic glyphs rather than a module's own branded assets, and currently support only one
activity-attached overlay per module. They cannot yet provide main tabs, persistent state updates,
richer typed form controls, or explicit show/hide component APIs. The next SDK work must define and
implement:

1. A versioned manifest and authoring kit with stronger validation, dynamic discovery for
   command modules and live refresh, lifecycle hooks, installation review,
   enable/disable/update/removal, and actionable in-app diagnostics. MCP discovery currently
   happens at startup only.
2. Stronger permission review and enforcement for module code. Iris now gives scopes separate
   consent grants and revocation at its tool-call boundary. The remaining work is OS-level
   process isolation, publisher review, and stronger safeguards for consequential side effects.
3. A declarative UI contract that grows the current schema into stateful cards, typed forms,
   branded icons, buttons, and multiple independently controlled overlays. Iris owns rendering,
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

### Current schema-v1 module surfaces example

With `discoverTools: true`, the MCP server supplies its tool schemas and the manifest supplies
permission policy; a button names a discovered tool and input names map to its JSON argument
properties. `icon` is a semantic key from Iris's allow-list. The module stays disabled until the
user explicitly enables it.

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
  "discoverTools": true,
  "asksPermission": ["messages.send", "history.read"],
  "tools": [
    {
      "name": "send_message",
      "permission": "messages.send"
    },
    {
      "name": "search_history",
      "permission": "history.read",
      "background": true
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
    },
    {
      "id": "activity",
      "title": "Telegram activity",
      "slot": "overlay",
      "icon": "telegram",
      "elements": [
        { "id": "hint", "type": "text", "label": "Sending a message" }
      ]
    }
  ]
}
```
