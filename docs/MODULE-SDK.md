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
  are currently repeated in the manifest rather than discovered from `tools/list`.
- Every external tool asks for approval on each call. Manifest permission labels are shown as
  information only; they do not grant or enforce individual capabilities yet.
- MCP tools may send `notifications/progress` after Iris supplies `_meta.progressToken` with
  `tools/call`. Iris correlates those messages to the plugin, tool, and conversation and
  publishes `ModuleProgressEvent`. Intermediate updates are throttled to avoid UI event floods.
- The activity card shows the current conversation/tool step without taking focus. The compact
  call pill remains a separate surface. Progress bars appear only when the module reports both
  a finite progress value and a finite positive total; otherwise the card shows the step only.

## What the public SDK still needs

The current mechanism is not yet the complete extensibility promise. External modules still
cannot contribute their own icons, panels, inputs, or independently controlled overlays. The
next SDK work must define and implement:

1. A versioned manifest and authoring kit with validation, dynamic tool discovery, lifecycle
   hooks, installation review, enable/disable/update/removal, and actionable diagnostics.
2. Permission scopes that are explicit, reviewable, revocable, and enforced by Iris. Read
   access to contacts must be distinct from sending messages; consequential actions must still
   have user control.
3. A declarative UI contract for cards, forms, buttons, icons, and overlay surfaces. Iris owns
   rendering, focus, accessibility, placement, and visibility policy; modules publish state and
   named actions instead of loading arbitrary UI code into the shell.
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
