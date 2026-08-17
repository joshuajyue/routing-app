# routing-app

> Status: functional MEAI routing demo with a mock Responses API.

A minimalist Blazor UI backed by real routing and failover clients from
`Microsoft.Extensions.AI`. The leaf model calls use a deterministic mock
Responses API, so the routing pipeline is genuine while generated content
remains local and reproducible.

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

The demo is intentionally deterministic and does not require an API key.

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
- The sticky model-and-reasoning preset pins a broad `gpt-5.4-mini`, `gpt-5.4`,
  or `gpt-5.5` model family by application session ID. That family’s own
  semantic router still selects low, medium, or high reasoning on every turn.
- Semantic profile utterances are edited from the selector that owns them,
  including both the sticky model profiles and each model’s reasoning profiles.
- In the sticky preset, `simple`, `balanced`, and `complex` explicitly select
  the outer model route; `low`, `medium`, and `high` explicitly select the
  inner reasoning route. Configured profile order resolves keyword ties.
- Optional whole-pipeline emergency fallback.
- Per-route model ID, reasoning, temperature, instruction, and token controls.
- Real `IChatClient` streaming and non-streaming execution over mock responses.
- Fail-next, timed kill, kill-until-revived, and revive controls.
- Interactive runtime tree with health controls for the selected model client.
- Independently collapsible pipeline and debug sidebars.
- Semantic score evidence, event timeline, effective options, and
  nested `FailoverChatClientAttempt`-shaped diagnostics.
- Responsive, sans-serif visual system with no external UI dependency.

Live provider integration is intentionally out of scope. Replacing
`MockResponsesChatClient` is the only missing provider step.

## Backend implementation

`DemoPipelineFactory` builds the UI configuration into real MEAI clients:

- Built-in `SemanticRoutingChatClient` and `OrderedFailoverChatClient`.
- Sample `StickySemanticRoutingChatClient : FailoverChatClient`.
- Sample `CooldownFailoverChatClient : FailoverChatClient`.
- `MockResponsesChatClient` leaf clients backed by `MockResponsesApi`.

The backend is intentionally split into two folders:

| Folder | Purpose |
| --- | --- |
| `Demo\Backend\Samples` | Standalone MEAI extensions. These depend only on MEAI and standard .NET abstractions. |
| `Demo\Backend\Infrastructure` | Demo-only configuration mapping, diagnostics, deterministic embeddings, mock responses, and UI runtime state. |

`CooldownFailoverChatClient` and `StickySemanticRoutingChatClient` do not
reference `RouteDefinition`, `DemoDiagnostics`, or any Blazor/UI type. The
infrastructure factory adapts those reusable samples to this demo.

## Scope documents

- [Demo scope](docs/demo-scope.md): proposed experience, scenarios, architecture,
  diagnostics, phases, and non-goals.
- [Capability matrix](docs/capability-matrix.md): every routing API behavior and
  how the demo will prove it.
- [Open decisions](docs/open-decisions.md): choices to settle before scaffolding
  the application.
