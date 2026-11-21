# 0001: The scheduler stays in the bot process, and Ember runs as a single instance

Status: accepted, 2026-11-20 (audit decision D10).

## Context

Every scheduled job runs on Quartz inside the bot process (`src/Ember.Bot/AppHost.cs`): reminder dispatch, the Discord ask, weekly summaries, missed-day reflections, Fireside notices, health-data deletion and account cleanup. Quartz uses its in-memory store, and every job is `[DisallowConcurrentExecution]`.

The API keeps some state in process memory: session revocations, native sign-in codes and Discord link grants (`AuthSessions`, `DiscordEndpoints`, `IMemoryCache`). Reminder retries also live in memory.

The API also does two jobs of its own in process. Account emails are handed to a background task (`EmberEmailSender.Send`), so every mail-sending endpoint answers at the same speed and an SMTP failure never reveals whether an account exists; a transient failure is retried once after 30 seconds. The daily feedback digest runs as a hosted service in the API (`FeedbackDigestService`), with no lock shared between processes.

A separate worker host or a second instance would need a second deploy unit, a shared Quartz store and a shared cache.

## Decision

- Quartz stays in the bot process with the in-memory store.
- Ember runs one bot and one API instance. In-process caches and retries are allowed on that basis.

## Consequences

- One deploy unit runs the jobs, with nothing extra to operate.
- A job only runs while the bot runs. A failed Discord login must not stop the jobs.
- A second API instance would break session revocation and sign-in codes, and a second bot would send every reminder twice. Neither may be started.
- An account deleted through the bot reaches the API's session cache only after up to 60 seconds.
- An email still waiting in the background (or for its retry) when the API stops is lost. The user asks for a new code or link; nothing else depends on the mail arriving.
- A second API instance would also send the feedback digest once per instance.

## Revisit when

- The API becomes the always-on service and the bot an optional add-on, or
- a second instance of the bot or the API is needed (load, availability or a rolling deploy).

Then move the jobs to a worker or the API with a persistent Quartz store, and move the in-memory state to the database or a shared cache. Outgoing email then goes through a persisted outbox, and the feedback digest becomes one of those jobs.
