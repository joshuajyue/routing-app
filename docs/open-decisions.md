# Open decisions

The current UI prototype implements the accepted P0 composition model with
simulated clients. Remaining decisions apply to wiring the real routing APIs.

## P0 decisions

| Decision | Recommended default | Reason |
| --- | --- | --- |
| UX model | One live composition workspace with an always-visible client tree, followed by a focused chat/debug screen | Removes wizard overhead while keeping nested `IChatClient` composition explicit |
| Initial host | .NET 10 Blazor Web App with Interactive Server rendering | Keeps `IChatClient`, streaming, and credentials server-side in a mostly C# sample |
| Default execution | Deterministic simulated models and embeddings | Makes the demo reliable, free to run, and able to reproduce exact failure boundaries |
| Live provider scope | OpenAI only, with a curated model catalog plus custom ID | Matches the initial concept without pretending model discovery or capability metadata is universal |
| Core presets | Semantic route families, semantic over ordered chains, direct cooldown failover, outer emergency fallback, and semantic reasoning-level routing | Covers both composition directions without exposing an opaque callback policy |
| Exact attempt telemetry | Add a custom observable ordered policy derived from `FailoverChatClient` | `OrderedFailoverChatClient` is sealed and does not publish its protected attempt hook |
| Model availability controls | Support fail-next, timed kill, down-until-revived, and revive | Enables repeatable comparison of static ordered failover and stateful cooldown behavior |
| Initial cooldown policy | Fixed 30-second cooldown, half-open on the next request, and immediate error when every route is ineligible | Easy to explain and deterministic before adding production-style backoff rules |
| Semantic score telemetry | Capture vectors with a recording embedding decorator and reproduce the built-in scoring calculation for display | Preserves use of the built-in sealed semantic client and avoids a second embedding call |
| Concurrent requests | One active request per browser demo session initially | Simplifies deterministic streaming, fault state, and semantic-diagnostics correlation |
| API key handling | Environment variable and .NET user secrets only | Prevents credentials from entering browser storage or exported presets |
| Repository visibility | Keep private during scope and implementation; make public only when demo content and secret handling are reviewed | Safest default for unfinished work |

## P1 decisions

| Decision | Recommended default | Reason |
| --- | --- | --- |
| Sticky state store | In-memory implementation behind an `IDistributedCache`-compatible abstraction | Demonstrates the correct application-session model without requiring Redis |
| Advanced custom policy | Extend cooldown selection with latency and TTFU ranking | Builds on the core cooldown policy instead of creating a disconnected preset |
| Tool-calling scenario | Include one deterministic tool and compare router placement | Makes the pipeline-placement limitation concrete |
| Advanced options | Add only options supported by model capability metadata | Avoids presenting invalid combinations such as temperature on an incompatible model |
| Shareability | Export configuration and diagnostics separately; strip secrets and chat content by default | Keeps presets useful without leaking data |
| Raw provider payloads | Hidden and redacted by default | They are provider-specific, may contain sensitive data, and are not needed to explain routing |

## Questions to resolve before implementation

1. Is the demo primarily for a live presentation, a blog companion, or a
   self-guided public sample? This changes how much onboarding and narration the
   UI needs.
2. Should the full target include every P1 preset, or should the first public
   version stop after exact attempt telemetry, semantic scoring, and sticky
   routing?
3. Which OpenAI model IDs and capability metadata should ship as curated
   defaults?
4. Should users be able to enter an API key into a server-handled form for a
   temporary session, or should live mode require server configuration only?
5. Is the built-in `OrderedFailoverChatClient` preset required to show exact
   attempt details, or is it acceptable that exact framework attempt objects
   appear in the separate custom observable preset?
6. Should the semantic score inspector be positioned as demo-derived
   diagnostics, given that the built-in API does not expose those scores?
7. Is an offline keyword embedding generator sufficient for simulated mode, or
   should the repository include deterministic numeric fixtures that make score
   changes easier to teach?
8. Should sticky routing survive a browser refresh, or only the active server
   circuit?
9. Does the first version need mobile layout support, or only a desktop
   presentation layout?
10. Should the app target the NuGet package directly or support a local
    `dotnet/extensions` package feed for API-development demos?
11. Should a temporary manual kill and an automatic router cooldown share the
    same duration control, or remain independently configurable?
12. Which exception classes should trigger temporary cooldown versus
    down-until-revived classification?
13. When every candidate is cooling down, should the policy fail immediately,
    wait for the earliest expiry, or allow an explicitly configured last-resort
    route to bypass cooldown?

## Scope completion gate

Scope is ready for implementation when:

- The P0 rows above are accepted or replaced.
- The P0 capability matrix is considered complete.
- The live composition and chat/debug workspaces have low-fidelity wireframes.
- The OpenAI model/capability source is chosen.
- The distinction between built-in telemetry and demo-derived telemetry is
  approved.
- The first public milestone and its excluded P1 items are explicit.
