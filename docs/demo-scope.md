# Demo scope

**Status:** Proposed  
**Last scoped:** 2026-08-14  
**Target API:** `Microsoft.Extensions.AI` 10.9.0 (`MEAI001`)

## Outcome

Build a browser-based, inspectable chat playground that teaches how
`Microsoft.Extensions.AI` selects, composes, and fails over between
`IChatClient` instances.

The demo should make an otherwise invisible routing decision observable:

1. Complete a guided build step for policies, routes, models, and options.
2. Review and create the configured routing pipeline.
3. Chat through that pipeline or trigger a deterministic failure.
4. See the selected route, actual model, option layers, semantic evidence, and
   every failover attempt.

The application is a teaching and API-feedback tool, not a production routing
gateway.

## What "all capabilities" means

The full target covers the public behavior of:

- `RoutingContext`
- `RoutingChatClient`
- `SemanticRoutingChatClient`
- `FailoverChatClient`
- `FailoverChatClientAttempt`
- `OrderedFailoverChatClient`

It also demonstrates that custom policies and configured clients compose
because each layer is an `IChatClient`.

The target does not implement every possible custom policy. It includes enough
representative policies to prove request-scoped state, sticky selection,
per-route option shaping, attempt observation, and health-aware routing.
`RoutingChatClient.Create` remains a code-level extension point rather than a
visual option until the UI has an explicit rule editor.

## Product model

Semantic selection and ordered failover must not be presented as mutually
exclusive choices. The configurator has three independent concepts:

| Layer | Question | Examples |
| --- | --- | --- |
| Selection | Which client should receive this request first? | Semantic match, direct single-family selection, sticky route |
| Resilience | What should happen if that invocation fails? | None, built-in ordered failover, observable custom failover |
| Route | What is this selectable client? | Named simulated model plus instructions and chat options |

A composed pipeline can therefore look like:

```text
OrderedFailoverChatClient
|-- SemanticRoutingChatClient
|   |-- coding -> gpt-* with high reasoning
|   |-- creative -> gpt-* with higher temperature
|   `-- general -> default model
`-- emergency -> fallback model
```

This is presented as a linear two-phase experience:

1. **Build:** configure and create a fresh pipeline.
2. **Chat:** use that pipeline and inspect its behavior.

Structural changes require returning to the build step and creating a new
pipeline. Runtime controls such as kill, timed kill, revive, cancel, and clear
diagnostics remain available during chat.

## Audience and demo story

The primary audience is a .NET developer evaluating the routing APIs. A
successful five-minute walkthrough should answer:

- How do I route by message meaning?
- How do threshold, top-K, and aggregation change a decision?
- How do I assign different models or reasoning levels to routes?
- When does failover retry, and when is a streaming failure terminal?
- What exactly is in `FailoverChatClientAttempt`?
- How do I compose a router with a failover client?
- How do I build a custom stateful or adaptive policy?
- Which routing patterns are intentionally outside this API?

## Proposed experience

### 1. Live composition workspace

The landing page is one persistent visual builder rather than a linear wizard.
The client tree is always visible while the user:

1. Chooses a starting preset.
2. Selects the selector, route-family, resilience, model, or outer-fallback
   node to open its contextual settings.
3. Watches every option immediately mutate the same tree.
4. Reviews readiness notes or the equivalent C# shape and starts chat from the
   same screen.

Starting scenarios are accelerators inside the first step:

| Preset | Purpose |
| --- | --- |
| Semantic route families | Select coding, creative, and general family clients whose targets have independent resilience |
| Semantic over ordered chains | Map every semantic profile to its own `OrderedFailoverChatClient` |
| Ordered failover | Walk a ranked list after deterministic pre-output failures |
| Semantic plus outer fallback | Wrap the semantic router and a global emergency client in ordered failover |
| Observable failover | Expose exact `FailoverChatClientAttempt` records from a custom `FailoverChatClient` |
| Cooldown failover | Temporarily or indefinitely kill routes and let a custom policy skip unhealthy clients |
| Reasoning levels | Semantically select low, medium, or high reasoning wrappers over one model |
| Sticky conversation | Classify once, pin only after a completed response, and reuse the route |
| Adaptive health | Rank routes using observed latency after cooldown behavior is understood |

Each scenario replaces the current tree with an editable starting shape. There
is no separate policy, routes, or review page.

The first version uses a constrained interactive tree rather than a free-form
graph editor. A user selects a selector, route-family, resilience, model, or
outer-fallback node and edits only that node's valid settings.

The persistent builder footer contains:

- Validation warnings for unsupported model/option combinations.
- Optional equivalent C# code for the configured API composition.
- Build readiness, family/client counts, and **Build and start chat**.

Import/export can be added after the core build-to-chat flow is complete.

### 2. Route-family configuration during build

Each semantic profile maps to one stable route-family `IChatClient`. That
family target can be:

- Exactly one configured model client when the family has no resilience policy.
- An `OrderedFailoverChatClient` containing compatible model clients.
- A custom `CooldownFailoverChatClient` containing compatible model clients.

The semantic router therefore keeps stable profile references while resilience
runs inside the selected family.

Each model leaf has:

- Stable route ID and display name.
- Simulated model ID, with a curated list plus custom model ID entry.
- Route instructions/persona.
- Reasoning effort.
- Temperature.
- Maximum output tokens.
- Optional advanced chat options.
- Semantic example utterances when the route participates in semantic routing.
- Simulated availability state.
- Router-observed health and cooldown state.

Distinct option configurations should be represented by distinct configured
`IChatClient` wrappers. Changing only `ChatOptions.ModelId` per request would
hide the API's client-instance routing identity and would not demonstrate the
recommended route-level option pattern.

### 3. Chat demo

The main workspace uses a three-panel layout:

- Left, approximately one quarter of the viewport: an interactive runtime tree.
  Selecting a model leaf opens that model's availability and policy-health
  controls directly below the tree.
- Center: conversation, streaming output, and send/cancel controls.
- Right: live debug inspector for the selected request.

The pipeline and debug sidebars can be hidden independently so the conversation
can use the reclaimed space.

All requests use deterministic simulated clients so the demo works without
credentials and failure timelines remain reproducible.

Structural policy, model, route-order, and option changes are not edited in the
chat workspace. Tree interaction selects runtime clients for inspection and
fault injection only. A **Rebuild pipeline** action returns to the live
composition workspace, prefilled with the current configuration.

Live chat controls remain available without rebuilding:

- Select a model client from the runtime tree.
- Fail next invocation.
- Kill for a selected duration.
- Kill until revived.
- Revive.
- Cancel current request.
- Clear transcript or debug events.
- Clear a sticky route pin.

### 4. Debug inspector

The inspector has four views.

#### Summary

- Request ID and application session ID.
- Selection policy and resilience policy.
- Selected route name.
- Configured model ID.
- Actual response/update `ModelId`.
- Finish reason and usage.
- Final outcome: completed, failed, canceled, or abandoned.

#### Routing evidence

- Last user message used by semantic routing.
- Threshold, top-K, and `Mean` or `Sum` aggregation.
- Winning route and aggregate score.
- Top matching profile utterances and cosine scores.
- Whether the default client won.
- Whether the profile index was built or reused.
- Sticky route lookup, pending selection, and committed pin.

#### Attempt timeline

For each failover attempt, display:

- Attempt number and client/route.
- Start and end timestamps.
- `Duration`.
- `TimeToFirstUpdate`.
- `Exception` type and message.
- `ResponseCompleted`.
- `OutputCommitted`.
- `isTerminal`.
- Why another selection did or did not occur.

#### Options and raw events

- Caller request options.
- Request-context option snapshot.
- Route-level overrides.
- Intended effective `ChatOptions`.
- A JSON event stream tagged by origin: framework-derived, provider response,
  or demo instrumentation.

The UI must distinguish configured model from actual provider-reported model.
It must also avoid implying that intended `ChatOptions` are the provider's raw
wire payload.

The inspector and route-status controls are the primary secondary experience
during chat. The full build form should not compete with the transcript.

## Required scenarios

### Callback routing API

Do not expose `RoutingChatClient.Create` as an unexplained visual policy. Cover
the callback API in a focused code sample or automated scenario until the demo
has a rule editor that can define and explain the callback's behavior.

### Semantic routing

Allow live editing of:

- Profile utterances.
- Default client.
- Score threshold.
- `topK`.
- `Mean` versus `Sum`.

The first request should visibly build profile embeddings in one batch. Later
requests should show that the cached profile index is reused while only the
latest user message is embedded.

Include cases for:

- A profiled route winning.
- No score clearing the threshold.
- No usable user message, causing the default route.
- A threshold or aggregation change altering the winner.

### Built-in ordered failover

Use `OrderedFailoverChatClient` with a ranked list. Demonstrate:

- First client succeeds.
- First client fails before output and the next client succeeds.
- Every client fails and the final exception is rethrown.
- `MaximumAttemptsPerRequest` stops the list early.
- The same client can occupy more than one position.

This preset proves the concrete built-in type. Named decorators can show which
inner clients were invoked, but the concrete type does not expose its protected
attempt hook publicly.

### Observable custom failover

Implement an ordered policy derived from `FailoverChatClient` and publish the
exact `FailoverChatClientAttempt` plus `isTerminal` into the debug event stream.
This is the authoritative attempt-inspection preset and demonstrates the base
class extension points.

### Streaming commitment boundary

Use deterministic faults to contrast:

1. Failure before the first update: retry is allowed.
2. Failure after at least one update: output is committed and the failure is
   terminal.
3. Caller stops reading without a provider failure: the attempt is incomplete,
   with `ResponseCompleted == false` and no exception.
4. Cancellation: no reselection occurs.

Also include a non-streaming request, where a failed invocation has no partial
output commitment and can be retried.

### Semantic plus failover composition

Use both composition directions:

1. Map each semantic profile to a single client, ordered failover chain, or
   cooldown chain. Semantic selection chooses the route family, then compatible
   models fail over inside it.
2. Optionally place the complete `SemanticRoutingChatClient` in an outer
   ordered failover list with an emergency client after it.

The route graph and timeline must make the nested identities clear:

- Semantic selection: coding, creative, or general family.
- Inner attempts: model clients belonging to that selected family.
- Inner terminal failure: the family target propagates failure.
- Outer attempt: semantic router as one client in the global chain.
- Outer fallback: emergency route.

### Request-level versus route-level options

Show:

- Caller options are cloned for routing and invocation.
- Request-level changes persist across selections in a custom policy.
- Route-level wrappers apply stable instructions or generation options.
- Low-, medium-, and high-reasoning wrappers over the same base model are
  distinct single-client semantic families. They are selection alternatives,
  not failover candidates.

The inspector should render an options diff rather than only the final values.

### Sticky selection

Use an application-owned session ID, not a provider conversation ID. The demo
should:

- Classify only an unpinned session.
- Pin a route only after a completed response.
- Reuse the pinned route on later turns.
- Avoid pinning a failed or abandoned first response.
- Allow clearing the pin independently of clearing chat history.

### Health-aware extension

Implement `CooldownFailoverChatClient : FailoverChatClient` as a core custom
policy. It keeps an ordered candidate list plus state for each route:

- Eligible.
- Cooling down until a timestamp.
- Disabled until manually revived.
- Consecutive failure count and last failure.

On a transient pre-output failure, `OnRoutingUpdateAsync` places the attempted
route into cooldown before the next `SelectClientAsync` call. Selection skips
routes that are cooling down or manually disabled. When a cooldown expires, the
next request acts as a half-open probe; a success clears the failure state and a
failure starts another cooldown.

This creates a deliberate comparison:

- `OrderedFailoverChatClient` always starts from the configured first entry, so
  a killed primary is attempted and fails on every request before fallback.
- `CooldownFailoverChatClient` remembers the failure and skips the primary on
  later requests until it becomes eligible again.

A route killed "permanently" is permanent only for the current demo session:
it remains disabled until the user clicks **Revive**. No route or configuration
is deleted.

After the cooldown behavior is established, a P1 extension can use `Duration`
and `TimeToFirstUpdate` to rank eligible routes. Seed new clients with explicit
estimates so an untried route is not unfairly preferred or ignored.

### Tool-calling placement

As a P1 advanced scenario, include one deterministic tool and compare:

- Router outside `FunctionInvokingChatClient`: one selection for the whole tool
  loop.
- Router inside it: selection can occur for each inner model call.

This explains pipeline placement without claiming that tool calling is a
routing feature.

## Chat options

### P0 controls

- Model ID.
- Instructions.
- Reasoning effort supported by the selected model.
- Temperature when supported.
- Maximum output tokens.
- Streaming versus non-streaming invocation.

### P1 controls

- Top-P.
- Top-K.
- Frequency and presence penalties.
- Seed.
- Stop sequences.
- Text versus JSON response format.
- Tool mode and multiple-tool-call setting for the tool-placement scenario.

The model catalog needs capability metadata so incompatible controls can be
disabled with an explanation. The metadata is part of the simulator and should
not imply live-provider capability discovery.

Provider-specific raw options and background-response continuation are out of
scope.

## Fault injection

Every named route can be wrapped with deterministic demo faults:

| Fault | Behavior demonstrated |
| --- | --- |
| Healthy | Normal selection and completion |
| Fail next invocation | One-shot ordered failover without changing later health |
| Kill for duration | Route remains unavailable until a visible countdown expires |
| Kill until revived | Route remains unavailable for the demo session until manually revived |
| Fail before first update | Retryable streaming failure |
| Fail after update N | Terminal post-commit failure |
| Fail non-streaming invocation | Retryable non-streaming failure |
| Delay first update | `TimeToFirstUpdate` |
| Delay completion | Attempt `Duration` |
| Empty completed stream | Completed response with no committed output |
| Manual cancellation | Cancellation is terminal |

Faults run entirely within the simulator and never require a real provider
outage.

### Availability versus policy health

The debug UI must keep these concepts separate:

- **Simulated availability** is controlled by the user. A route can be up, down
  for a selected duration, or down until revived.
- **Policy health** is maintained by a custom router. A route can be eligible,
  cooling down because of an observed failure, half-open for its next probe, or
  disabled.

The route list should show both states and a countdown for every timed state.
Controls should include **Fail next**, **Kill for...**, **Kill until revived**,
and **Revive**. These controls live on the chat screen because they change
runtime state rather than pipeline structure.

Recommended initial cooldown behavior:

- Fixed configurable duration, default 30 seconds.
- Only uncanceled pre-output invocation failures trigger automatic cooldown.
- Success clears consecutive failures and cooldown.
- Cancellation and post-output failure are terminal for the request but do not
  automatically mark the route unhealthy.
- Authentication or configuration failures may be classified as disabled
  until manual revive rather than transient cooldown.
- If no route is eligible, fail immediately with a clear policy error and show
  the next cooldown expiry. Do not silently invoke an ineligible route.

Exponential backoff, exception-specific duration rules, and `Retry-After`
support are P1 refinements.

## Diagnostics design

The public APIs do not expose every piece of desired UI data directly. The
implementation should use explicit instrumentation rather than reflection.

### Named client decorator

Wrap every route to record stable route ID, display name, configured model,
invocation, response metadata, and injected faults.

### Recording embedding generator

`SemanticRoutingChatClient` is sealed and does not publish its selected score.
Wrap the `IEmbeddingGenerator` to capture the exact profile and query vectors
used by the built-in client, then reproduce its documented cosine/top-K
aggregation for display. This avoids a second embedding request and keeps the
actual routing decision on the built-in type.

The first version should permit one active request per browser demo session so
embedding diagnostics can be correlated safely. Concurrency can be added after
the event model has an explicit request context.

### Observable failover implementation

`OrderedFailoverChatClient` is sealed and does not expose
`OnRoutingUpdateAsync`. Use it in the built-in preset, and separately derive an
observable ordered policy from `FailoverChatClient` for exact
`FailoverChatClientAttempt` reporting.

Do not infer exact attempt fields from generic logging when the framework hook
can provide them.

`CooldownFailoverChatClient` should use the same event publisher and attempt
view. Its request-scoped candidate index belongs on a custom `RoutingContext`;
cross-request route health belongs in policy state. Policy-generated events
must explain why each candidate was selected, skipped for cooldown, or skipped
because it was manually disabled.

### Response metadata

Capture `ChatResponse.ModelId`, streaming update `ModelId`, finish reason,
usage content/details, response IDs, and provider exceptions. Raw provider
objects should remain hidden by default and be redacted before any optional
display.

## Proposed technical shape

The recommended host is a .NET 10 Blazor Web App using Interactive Server
rendering:

- `IChatClient` instances and demo state stay server-side.
- Async streaming can update the debug timeline and transcript without a
  separate JavaScript backend.
- The sample remains primarily C# and directly demonstrates the .NET APIs.

Proposed future projects:

| Project | Responsibility |
| --- | --- |
| `RoutingDemo.Web` | Blazor UI and per-browser demo session |
| `RoutingDemo.Core` | Pipeline definitions, validation, and factories |
| `RoutingDemo.Diagnostics` | Event model and instrumented decorators |
| `RoutingDemo.Simulated` | Deterministic models, embeddings, delays, and faults |
| `RoutingDemo.Tests` | Routing scenarios and UI-independent acceptance tests |

This is a proposed structure, not approval to scaffold it yet.

## State and privacy

- Scope pipeline state, chat history, fault state, and diagnostics to one
  browser session.
- Use an in-memory sticky-route store first, behind an interface compatible
  with `IDistributedCache`.
- Do not persist prompts or responses by default.
- Keep exported configurations free of chat content by default.

## Delivery phases

### Phase 0: scope

- Agree on the P0 decisions.
- Lock the capability matrix and acceptance criteria.
- Produce low-fidelity wireframes for the live composition and chat workspaces.

### Phase 1: built-in routing playground

- Deterministic simulated route factories.
- Direct, semantic, built-in ordered failover, and composition presets.
- Core route options.
- Named-client diagnostics and deterministic faults.
- Streaming and non-streaming chat.

### Phase 2: extensibility and exact telemetry

- Observable custom failover and attempt timeline.
- Timed and until-revived model kills.
- `CooldownFailoverChatClient` with fixed cooldown and half-open recovery.
- Semantic score explanation.
- Sticky and reasoning-level routing.
- Advanced failure-boundary scenarios.

### Phase 3: advanced composition and polish

- Latency-aware ranking and advanced cooldown rules.
- Tool-loop placement scenario.
- Import/export, shareable presets without secrets, accessibility, and demo
  script.

## Acceptance criteria

- Every P0 row in the capability matrix has an interactive scenario or an
  explicit automated verification.
- Every scenario runs without credentials or network access.
- The UI always distinguishes route name, configured model, and actual
  simulated response model.
- Pre-output and post-output failures visibly produce different failover
  behavior.
- Every exact `FailoverChatClientAttempt` property and `isTerminal` can be
  inspected in the observable preset.
- A route can be failed once, killed for a timed window, killed until revived,
  and manually restored.
- Repeated requests visibly contrast built-in ordered failover with the custom
  cooldown policy's cross-request health memory.
- Semantic threshold, top-K, aggregation, default selection, and index caching
  are visible.
- A composed semantic-plus-failover pipeline remains understandable from the
  interactive runtime tree.
- Request-level and route-level options are displayed as separate layers.
- Structural configuration happens in the live composition workspace, while the chat
  screen stays focused on conversation, route status, failure controls, and
  debug evidence.
- Unsupported patterns are labeled rather than approximated.

## Non-goals

- Production multi-tenant routing infrastructure.
- Live OpenAI or other provider integration.
- Provider billing reconciliation or guaranteed cost estimates.
- Automatic discovery of provider models and capabilities.
- Real outage detection as a prerequisite for the demo.
- Model cascading after a low-quality successful response.
- Ensemble fan-out and response voting.
- Hedged/racing requests.
- A general-purpose visual workflow editor.
- Editing structural pipeline configuration inline while chatting.
- Persisting prompts or responses.
- Hiding experimental API status or compatibility limitations.
