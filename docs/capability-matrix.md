# Routing capability matrix

**Target:** `Microsoft.Extensions.AI` 10.9.0  
**Priority:** P0 is required for the core demo, P1 is advanced scope, and P2 is
verified outside the main interaction.

## Routing context and base router

| Capability | Demo treatment | Evidence | Priority |
| --- | --- | --- | --- |
| One `RoutingContext` per request | Assign and display a request ID around context creation | Event timeline | P0 |
| Request messages available to selection | Focused callback API scenario displays the messages inspected by code | Code sample or automated scenario | P1 |
| Caller `ChatOptions` are cloned | Mutate the caller object after starting a controlled request and show no effect | Scenario test and options diff | P1 |
| Context options can carry request-level policy changes | Custom callback modifies the request-local snapshot | Options diff | P0 |
| Custom `RoutingContext` subclass carries request-scoped state | Sticky or observable policy stores state on a derived context | Policy state inspector | P1 |
| `RoutingChatClient.Create` callback selection | Keep outside the visual builder until an explicit rule editor exists | Code sample or automated scenario | P1 |
| Derived `RoutingChatClient` policy | Sticky policy or explicit stateful policy | Route graph and policy events | P0 |
| Streaming forwarding | Stream a normal response through the selected client | Transcript and update events | P0 |
| Non-streaming forwarding | Toggle invocation mode | Response event | P0 |
| Selection exceptions propagate | Fault the policy itself | Terminal error scenario | P2 |
| Callback-selected clients remain caller-owned | Verify disposal behavior outside the UI | Automated test | P2 |

## Semantic routing

| Capability | Demo treatment | Evidence | Priority |
| --- | --- | --- | --- |
| Route by the last user message | Multi-turn transcript with an intentionally different earlier topic | Semantic evidence | P0 |
| App-provided example utterances | Editable profiles per named route | Configurator | P0 |
| Lazy, batched profile embedding | First request shows one profile batch | Embedding events | P0 |
| Cached profile index | Second request shows no profile re-embedding | Embedding events | P0 |
| Query embedding per request | Show one query embedding invocation | Embedding events | P0 |
| Highest score above threshold wins | Adjust threshold around a known score | Score inspector | P0 |
| Default client below threshold | High-threshold scenario | Default-selection reason | P0 |
| Default client with no usable user message | System/assistant-only controlled request | Default-selection reason | P1 |
| `topK` selection | Compare top-1 and top-5 with the same profiles | Score inspector | P0 |
| `Mean` aggregation | Toggle and display aggregate calculation | Score inspector | P0 |
| `Sum` aggregation and threshold range | Toggle and validate threshold against `topK` | Config validation | P0 |
| Stable client identity by reference | Two wrappers over one base model appear as distinct routes | Route graph | P0 |
| Client and embedding-generator ownership via `leaveOpen` | Verify disposal behavior outside the UI | Automated test | P2 |
| Invalid profiles, dimensions, and embedding counts fail clearly | Use focused construction and fake-generator tests | Automated tests | P2 |

## Failover base behavior

| Capability | Demo treatment | Evidence | Priority |
| --- | --- | --- | --- |
| Reselect after an uncanceled pre-output failure | Fail primary before first update | Attempt timeline | P0 |
| Retry a failed non-streaming invocation | Non-streaming fault preset | Attempt timeline | P0 |
| Do not retry after output is committed | Fail after update N | Attempt timeline | P0 |
| Do not retry after cancellation | Cancel an in-flight request | Terminal reason | P0 |
| `MaximumAttemptsPerRequest` | Set the cap below the candidate count | Attempt timeline | P0 |
| Limit is captured when the request starts | Change the configured limit during a controlled request | Automated scenario | P2 |
| Shared context across selections and updates | Display a request-scoped attempt counter/state object | Policy state inspector | P1 |
| Fresh invocation options clone per attempt | Mutate one attempt's options and show no leakage to the next | Automated scenario and options diff | P1 |
| Selection failures are not reported as attempts | Fault `SelectClientAsync` | Terminal error scenario | P2 |
| Update-hook exceptions stop routing | Fault `OnRoutingUpdateAsync` | Automated scenario | P2 |
| Abandoned stream is reported incomplete | Stop consuming without a provider exception | Attempt timeline | P0 |
| Attempt observations can update later selection | Cool down a failed route before reselection | Attempt and policy timelines | P0 |

## `FailoverChatClientAttempt`

| Field or signal | Demo treatment | Evidence | Priority |
| --- | --- | --- | --- |
| `Client` | Resolve to route name and configured model | Attempt row | P0 |
| `Duration` | Add controlled provider delay | Attempt row and bar | P0 |
| `Exception` | Inject a typed failure | Expandable exception details | P0 |
| `ResponseCompleted` | Compare success, failure, and abandoned stream | Attempt status | P0 |
| `OutputCommitted` | Compare failure before and after first update | Attempt status | P0 |
| `TimeToFirstUpdate` | Add controlled first-update delay | Attempt row and bar | P0 |
| `isTerminal` | Publish from custom `OnRoutingUpdateAsync` | Attempt status and explanation | P0 |
| Caller processing time excluded from streaming duration | Delay UI consumption in a controlled test | Automated scenario | P2 |
| Disposal exception can replace invocation exception | Fake enumerator throws during disposal | Automated test | P2 |

## Ordered failover

| Capability | Demo treatment | Evidence | Priority |
| --- | --- | --- | --- |
| Try clients in configured order | Reorder routes, fail the first two | Invocation trace | P0 |
| Same client can appear more than once | Add one route twice | Invocation trace | P1 |
| Final failure is rethrown after exhaustion | Fail every entry | Terminal exception | P0 |
| Attempt limit can stop the list early | Configure list of three and cap at two | Invocation trace | P0 |
| Built-in `OrderedFailoverChatClient` concrete type | Dedicated built-in preset | Pipeline graph | P0 |
| Configured clients are snapshotted | Mutate the source list after construction | Automated test | P2 |
| Ownership via `leaveOpen` | Verify disposal behavior outside the UI | Automated test | P2 |

## Composition and extension patterns

| Capability | Demo treatment | Evidence | Priority |
| --- | --- | --- | --- |
| Router is itself an `IChatClient` | Put semantic router inside ordered failover | Nested pipeline graph | P0 |
| Direct selector invariant | Permit Direct only when exactly one route family exists | Selector validation and live tree | P0 |
| Semantic profile targets a router | Map coding and creative profiles to independent ordered or cooldown family clients | Interactive composition tree | P0 |
| Per-family resilience | Configure exactly one client for Single, or multiple clients under ordered/cooldown behavior | Family-node inspector and invariant validation | P0 |
| Inner versus outer failure scope | Exhaust a selected family, then invoke a global emergency client outside the semantic router | Nested attempt and event timelines | P0 |
| Route-level configured wrappers | Different instructions/options per route | Options diff | P0 |
| One model at multiple reasoning levels | Semantic low/medium/high single-client families over one OpenAI model | Distinct routes and effective options | P0 |
| Sticky application-session routing | Pin only after successful completion | Cache/pin events | P1 |
| One-shot manual failure | Select a model leaf in the runtime tree and fail its next invocation | Selected-model health and invocation trace | P0 |
| Timed manual kill | Select a model leaf and keep it down with a visible expiry countdown | Tree status, selected-model health, and attempt trace | P0 |
| Until-revived manual kill | Select a model leaf and keep it down for the demo session | Tree status and selected-model health | P0 |
| Custom `CooldownFailoverChatClient` | Derive from `FailoverChatClient`, skip cooling or disabled routes, and publish attempts | Pipeline graph and policy events | P0 |
| Cooldown begins from attempt observation | A pre-output failure changes route state before the next selection | Attempt and health timelines | P0 |
| Half-open recovery | Re-admit a route after expiry and use the next request as a probe | Health state events | P0 |
| Built-in versus stateful failover | Repeat a request while primary is down and compare whether it is attempted again | Side-by-side invocation traces | P0 |
| Latency-aware policy | Rank eligible routes from duration and TTFU | Policy score inspector | P1 |
| Advanced circuit-breaker rules | Use exponential cooldown or exception-specific duration | Health state events | P1 |
| Cost-aware selection | Document as an extension of the policy model | Design note, optional preset | P2 |
| Capability-aware selection | Model capability metadata filters candidates | Validation and optional preset | P1 |
| Region-aware selection | Document route metadata and policy hook | Design note | P2 |
| Router placement around tool-calling loop | Compare router outside and inside function invocation | Advanced scenario | P1 |

## Explicit limitations

| Pattern | Demo treatment | Priority |
| --- | --- | --- |
| Quality-based model cascading | Label as unsupported by these primitives because success does not trigger reselection | P0 |
| Ensemble fan-out and merging | Label as requiring a different multi-client implementation | P0 |
| Hedged/racing requests | Label as requiring concurrent fan-out | P0 |
| Mid-stream recovery | Show terminal post-commit failure rather than simulating recovery | P0 |
| Provider conversation portability | Warn that sticky routing uses an application session ID | P0 |
