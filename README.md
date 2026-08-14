# routing-ai-chat-demo-final

> Status: scope and design only. This repository intentionally contains no application scaffold yet.

An interactive .NET demo for the extensible routing and failover APIs in
`Microsoft.Extensions.AI` 10.9.0. The target experience lets a user compose a
routing pipeline, configure OpenAI-backed routes, inject failures, chat through
the pipeline, and inspect why each request selected or failed over to a model.
Routes can be taken down for one request, for a timed window, or until manually
revived so static ordered failover can be compared with a stateful custom
cooldown policy.

The central product decision is that semantic routing and failover are not
exclusive modes:

- A selection policy decides which client should handle a request.
- A resilience policy decides what to try next if that invocation fails.
- Because every router is an `IChatClient`, those policies can be composed.

The proposed UI therefore separates **selection**, **resilience**, and
**per-route model configuration** rather than offering only a semantic-versus-
ordered-failover switch.

## Scope documents

- [Demo scope](docs/demo-scope.md): proposed experience, scenarios, architecture,
  diagnostics, phases, and non-goals.
- [Capability matrix](docs/capability-matrix.md): every routing API behavior and
  how the demo will prove it.
- [Open decisions](docs/open-decisions.md): choices to settle before scaffolding
  the application.

## Build gate

Implementation should begin only after the P0 decisions in
[open decisions](docs/open-decisions.md) are accepted or revised. Until then,
changes in this repository should remain focused on product and technical scope.
Scope and design for an extensible Microsoft.Extensions.AI routing and failover demo.
