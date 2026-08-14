# routing-ai-chat-demo-final

> Status: functional UI prototype with simulated routing behavior.

A minimalist Blazor UI for exploring the extensible routing and failover APIs
in `Microsoft.Extensions.AI`. The experience starts with one live composition
workspace: presets and node settings mutate an always-visible client tree,
which then segues into an interactive chat with routing diagnostics.

The central product decision is that semantic routing and failover are not
exclusive modes:

- A selection policy chooses a stable **route-family client**.
- Each route family can be a single configured model, an
  `OrderedFailoverChatClient`, or a custom cooldown client.
- An optional outer failover can catch an exhausted route family with a global
  emergency model.
- Because every router is an `IChatClient`, those policies can be composed.

The build step uses an interactive tree instead of a semantic-versus-failover
switch:

```text
OrderedFailoverChatClient
|-- SemanticRoutingChatClient
|   |-- coding -> OrderedFailover[coding-primary, coding-backup]
|   |-- creative -> CooldownFailover[creative-primary, creative-backup]
|   `-- general -> general
`-- global-emergency
```

Selecting any tree node opens its contextual settings, and every change is
immediately reflected in the tree. Build readiness, review notes, and the C#
shape stay in the same workspace; the chat screen keeps runtime health
controls, conversation, and debug information visible.

## Run

```powershell
dotnet run --project .\src\RoutingDemo.Web\RoutingDemo.Web.csproj
```

The current prototype is deterministic and does not require an API key.

## Included

- Live preset selection, interactive composition, readiness, and build controls
  on one screen.
- Semantic profiles that target single clients or independent ordered/cooldown
  failover chains.
- Single-client families are constrained to exactly one model; adding another
  model automatically creates an ordered chain.
- Direct selection is constrained to exactly one route family; adding another
  family promotes the selector to semantic routing.
- The reasoning preset semantically selects low, medium, or high reasoning
  wrappers over the same model, with no failover between reasoning levels.
- Optional whole-pipeline emergency fallback.
- Per-route OpenAI model, reasoning, temperature, instruction, and token
  controls.
- Simulated streaming and non-streaming chat.
- Fail-next, timed kill, kill-until-revived, and revive controls.
- Interactive runtime tree with health controls for the selected model client.
- Independently collapsible pipeline and debug sidebars.
- Semantic score evidence, event timeline, effective options, and
  nested `FailoverChatClientAttempt`-shaped diagnostics.
- Responsive, sans-serif visual system with no external UI dependency.

The next implementation layer is replacing the simulator with real
`Microsoft.Extensions.AI` clients while preserving the same UI contracts.

## Scope documents

- [Demo scope](docs/demo-scope.md): proposed experience, scenarios, architecture,
  diagnostics, phases, and non-goals.
- [Capability matrix](docs/capability-matrix.md): every routing API behavior and
  how the demo will prove it.
- [Open decisions](docs/open-decisions.md): choices to settle before scaffolding
  the application.
