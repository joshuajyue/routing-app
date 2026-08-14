# routing-ai-chat-demo-final

> Status: functional UI prototype with simulated routing behavior.

A minimalist Blazor UI for exploring the extensible routing and failover APIs
in `Microsoft.Extensions.AI`. The experience starts with a guided build step
for selecting policies, routes, models, and options, then segues into an
interactive chat with live routing diagnostics.

The central product decision is that semantic routing and failover are not
exclusive modes:

- A selection policy decides which client should handle a request.
- A resilience policy decides what to try next if that invocation fails.
- Because every router is an `IChatClient`, those policies can be composed.

The build step separates **selection**, **resilience**, and **per-route model
configuration** rather than offering only a semantic-versus-ordered-failover
switch. Structural configuration is reviewed once and built into a pipeline.
The chat screen keeps only runtime health controls, conversation, and debug
information visible.

## Run

```powershell
dotnet run --project .\src\RoutingDemo.Web\RoutingDemo.Web.csproj
```

The current prototype is deterministic and does not require an API key.

## Included

- Guided scenario, policy, route, and review steps.
- Semantic, callback, ordered failover, and cooldown policy shapes.
- Per-route OpenAI model, reasoning, temperature, instruction, and token
  controls.
- Simulated streaming and non-streaming chat.
- Fail-next, timed kill, kill-until-revived, and revive controls.
- Semantic score evidence, event timeline, effective options, and
  `FailoverChatClientAttempt`-shaped diagnostics.
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
