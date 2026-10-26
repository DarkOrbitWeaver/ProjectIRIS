# Iris Module SDK: current contract and target

Iris is intended to grow through modules, not through a permanently hardcoded list of
integrations. A module may represent a local capability or an external service such as
Telegram, a browser search provider, or a music library. Modules use Iris's routing,
permission, activity, and shell contracts so each integration feels like part of one app.

## What works now

- Built-in C# modules implement `IAssistantModule` and register with `ModuleHost`.
- External folders may declare a `plugin.json` using `command` or MCP stdio transport.
- External modules default to disabled unless their manifest explicitly sets `"disabled": false`;
  the Modules screen can scan folders and enable or disable them immediately. Refresh validates
  changed manifests, re-discovers tools, replaces registrations, and closes active MCP sessions.
  There is no background folder watcher. Existing manifests that omitted `disabled` require an
  explicit enable action.
- External tools are adapted into the same dynamic router and execution boundary, and receive
  stable module-qualified router names so unrelated modules can reuse local tool names. MCP and
  command modules may set `"discoverTools": true` to load tool schemas during startup. MCP tools
  come from `tools/list`; a command module receives an `action: "tools/list"` handshake and
  returns a `tools` JSON envelope. Each tool name and schema then comes from the module. The
  manifest can still declare
  name-to-permission policy entries without duplicating those schemas. Discovery follows MCP
  pagination and caps each module at 128 tools (and MCP at 16 pages), each schema at 16 KB and
  description
  at 1,000 characters, with a 10-second total discovery budget per module. If discovery fails or
  the live surfaces do not match the discovered schemas, the module is registered without
  callable discovered tools and the reason is logged. Discovered tools without a matching
  manifest permission policy still pass through the conservative system-critical approval gate;
  they simply have no named scope attached to the prompt yet.
- Every tool call, including calls from native module surfaces, is checked against its declared
  argument schema at the shared host boundary before a permission prompt or module execution.
  The supported JSON Schema contract covers object properties and required fields, nested object
  and array schemas, primitive types (including integers), enums and constants, additional fields,
  common string/number/collection bounds, patterns, and `allOf`/`anyOf`/`oneOf`. Unsupported
  keywords or invalid schemas fail closed with a visible tool error. The full JSON Schema standard
  is not implemented; modules should use this documented subset until broader support is added.
- External modules may declare up to eight surfaces (up to five sidebar surfaces and three
  independent overlays), each with at most twenty native elements: plain text, text inputs,
  constrained select controls, and buttons bound to that module's offered tools. Select options
  map to string enum values in the target tool schema; a `dynamic: true` select can start empty
  and receive safe option updates from its module. Command modules can update their declared
  surfaces through `ui` JSONL messages; MCP modules can use `notifications/iris/ui`. The shell can show up to three declared
  module overlays together while its tool is running, updates them from real progress, and lets the
  user hide each one for that task. A button action is checked against its owning module and then
  uses the same permission gate as a model-selected tool. All inputs in one surface are string
  fields shared by its buttons and must match each target tool's JSON schema. Icons can use Iris's
  semantic glyph keys or a module-local PNG (maximum 128×128 and 128 KiB, with path traversal and
  links rejected); module branding appears in its surfaces and activity cards.
- The request router keeps the model-facing list to at most eight tools, chooses no more than two
  relevant external modules, and ranks a large module's tools against the current request rather
  than taking the first entries. It caches a compact module fingerprint when registering the
  module and keeps the full installed tool catalog out of the repeated system prompt.
- External tools use Iris's existing system-critical permission gate. A first call asks for
  approval; a saved "always allow" grant can authorize later calls to that tool without another
  prompt. A manifest tool policy can set `"alwaysConfirm": true` to require fresh approval for
  every call; Iris hides the persistent-grant choice and enforces this even if an old client
  submits it. Use this for consequential actions such as sending a message or changing account
  settings. A tool may name one capability in `permission`, and the manifest must also list that
  scope in `asksPermission`. Iris shows the scope in the permission prompt and audit trail, and
  remembers "always allow" per module and scope; tools with another scope ask separately. Saved
  scope grants can be revoked in Settings. This enforces consent at Iris's tool-call boundary,
  not OS isolation: an external process still runs with the current user's OS access, so the
  user must trust enabled module code and its declared behavior.
- MCP tools may send `notifications/progress` after Iris supplies `_meta.progressToken` with
  `tools/call`. Iris correlates those messages to the plugin, tool, and turn or background task and
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
- Modules that need account credentials list secret names in the manifest. Iris's Modules screen
  accepts those values as masked input and stores them in a current-user DPAPI encrypted file;
  values are not written to the manifest, settings, prompts, arguments, or audit records. A command
  module receives declared credentials as uppercase `IRIS_SECRET_<NAME>` environment variables
  (dots and hyphens become underscores) only for a `call` action. An MCP process receives them
  when it starts; changing or removing a credential closes that process so its in-memory copy is
  released, and it reconnects on the next call. Iris removes inherited `IRIS_SECRET_*` variables
  and redacts exact stored values from module output, errors, progress, and discovered descriptions
  before displaying them. Enabled module code still runs as your Windows user, so only assign
  credentials to code you trust.
- A command module receives one JSON line on stdin with its module id, local tool name, arguments,
  and action (plus protocol version 1). `action` is `call` for execution and `tools/list` for
  startup discovery. For discovery, return one JSON object with `type: "tools"` and a `tools`
  array;
  each entry has a `name`, optional `description`, and object `schema` (or `inputSchema`). The
  manifest still owns permission and background policy. It may write ordinary text for a normal
  result, or emit JSONL `progress` and `result` envelopes for shared live activity; stdout is
  capped at 256 KiB,
  stderr storage at 16 KiB, and oversized stdout terminates the process. MCP modules receive the
  standard `tools/call` request with the local tool name.

### Command module progress protocol

Existing command modules may keep writing plain text. Version 1 modules can instead report
intermediate work and a structured final result on stdout, one JSON object per line:

```json
{"type":"progress","message":"Finding Alex","progress":1,"total":3}
{"type":"result","ok":true,"text":"Found Alex in contacts."}
```

They may also update a declared surface element or control the current task's overlay visibility,
using only the host's native controls:

```json
{"type":"ui","surface":"compose","element":"contacts","options":[{"value":"user-42","label":"Alex"}],"value":"user-42"}
{"type":"ui","surface":"activity","visible":false}
```

The host scopes this update to the calling plugin and active turn/task, validates both IDs and the
element type, and caps successful updates at 128 per tool call. The shell ignores a delayed overlay
patch if a newer task has already bound that surface. With no `element`, `visible` shows or hides
the declared overlay for the current task. A user's explicit hide remains in force for that task.
With an `element`, `label`, `value`, `visible`, and `enabled` can update the declared control;
`options` is accepted only for a select declared with `dynamic: true`. Modules cannot supply markup
or executable UI code. A button action still goes through the ordinary tool router and permission
gate.

Progress numbers are optional; omit them when there is no honest total. `result.ok` defaults to
true and `result.text` is returned to Iris. The module process is isolated from Iris's UI process,
and cancellation terminates its process tree. The command runs with the user's OS permissions, so
the existing module enablement and capability approval still matter.

### MCP module surface updates

An MCP server can use the same declared-surface contract during `tools/call` by sending the
task's `_meta.progressToken` back in a `notifications/iris/ui` notification. Iris accepts the
notification only while that call is active, applies the same manifest validation and 128-update
limit, redacts configured secrets, and attaches the host's turn/task ID. It can update a control:

```json
{"jsonrpc":"2.0","method":"notifications/iris/ui","params":{"progressToken":"TOKEN_FROM_TOOLS_CALL","surface":"activity","element":"status","label":"Message sent"}}
```

Or it can show or hide its overlay for that task by omitting `element` and setting `visible` to a
boolean. A stale notification from a completed call is ignored. This is an Iris UI extension over
MCP notifications; standard `notifications/progress` remains available for the shared activity
step and percentage.

## What the public SDK still needs

The current mechanism is not yet the complete extensibility promise. Controls are native and
state can update during a command call, and module-local PNG branding is supported. Overlay
visibility can be controlled for its current task, but surfaces are still activity-attached and
modules cannot persist UI state across tasks or restarts. They cannot yet provide main tabs or rich
typed forms. The next SDK work must define and implement:

1. A versioned manifest and authoring kit with stronger validation, lifecycle hooks, installation
   review, guided update/removal, publisher details, and actionable in-app diagnostics. Explicit
   folder refresh and immediate enable/disable already work; MCP discovery still requires a
   refresh after the module changes.
2. Stronger permission review and enforcement for module code. Iris now gives scopes separate
   consent grants and revocation at its tool-call boundary. The remaining work is OS-level
   process isolation, publisher review, and stronger safeguards for consequential side effects.
3. A declarative UI contract that grows the current schema into stateful cards, typed forms,
   branded icons, buttons, and multiple independently controlled overlays. Iris owns rendering,
   focus, accessibility, placement, and visibility policy; modules publish state and named
   actions instead of loading arbitrary UI code into the shell. Task-scoped overlay visibility is
   implemented; persistent visibility and state across tasks remain open.
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

With `discoverTools: true`, the MCP server or command supplies its tool schemas and the manifest
supplies permission policy; a button names a discovered tool and input names map to its JSON argument
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
  "secrets": ["bot_token"],
  "tools": [
    {
      "name": "send_message",
      "permission": "messages.send",
      "alwaysConfirm": true
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
