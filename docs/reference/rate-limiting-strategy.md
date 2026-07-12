# Nexus API rate-limiting strategy

Modificus Curator proactively limits its own Nexus API calls via three
mechanisms: a manual sliding-window throttle on the "check now" refresh, an
auto-check interval floor, and a persisted last-check interval gate that covers
every automatic trigger. These run alongside the reactive handling that responds
to rate-limit signals from the server after a call has been made; see
[Nexus API rate limiting](../architecture/nexus-rate-limiting.md).

## The manual sliding-window throttle

The manual "check now" refresh (the mod-list header refresh button) carries its
own throttle, independent of every other gate. The first 10 manual refreshes in
a rolling 1-hour window fire freely; once spent, the path throttles to one per
2 minutes until timestamps age out of the window and free mode resumes. A
blocked attempt consumes nothing: no API call, no timestamp stamp. The window
persists across restarts via `app-state.json`
(`IAppStateStore.ManualRefreshTimestamps`), seeded at `UpdateCheckRunner.Start()`
and written back on every successful fire, so closing and reopening the app
does not reset the free-refresh budget.

Owned by `UpdateCheckRunner`. Constants on that class:

- `FreeManualRefreshLimit = 10`
- `ManualRefreshWindow = 1 hour`
- `ManualRefreshThrottleInterval = 2 minutes`

When throttled, the refresh button disables and shows a live `m:ss` countdown
tooltip ("Rate limiting protection enabled. Manual refresh will be available
again in {time}."). The countdown reads `UpdateCheckRunner.NextManualRefreshAllowedAt`,
the single source of truth for the button's enable state and the tooltip.

## The auto-check interval floor

The minimum user-configurable periodic-check interval is 5 minutes. This is a
named policy bound, `NexusConfig.MinAutoUpdateCheckIntervalMinutes = 5`; the
default is 10, the maximum 1440 (`MaxAutoUpdateCheckIntervalMinutes`). The floor
is enforced on save (the Integrations dialog) and at tick time (the runner, via
`Math.Max`), so a value below the floor is raised before it can drive an API
call. At the 5-minute floor the periodic check tops out at 288 calls/day.

Owned by `NexusConfig` (the bounds) and applied by the Integrations dialog and
`UpdateCheckRunner`.

## The persisted last-check interval gate

Every automatic trigger (startup, active-profile switch, and the periodic timer)
passes through one shared interval check: a check fires only when the configured
interval has elapsed since the last check of any kind. The last-check timestamp
is persisted to `app-state.json` (`IAppStateStore.LastUpdateCheckUtc`) and seeded
at `UpdateCheckRunner.Start()`, so the gate survives a close/reopen: a rapid
open/close loop does not fire a call per launch.

The `AutoUpdateCheckEnabled` toggle gates only the periodic timer; startup and
switch fire regardless of the toggle when the interval has elapsed.

`UpdateCheckRunner.CheckNowAsync` (the manual path) bypasses the interval gate,
since it carries its own sliding window instead, but a successful manual fire
still re-stamps the shared last-check timestamp so the periodic clock backs off
after it.

Owned by `IAppStateStore` (the persisted timestamp) and `UpdateCheckRunner` (the
gate).

## Budget

Worst case for a determined user: 10 free plus 30 throttled manual refreshes is
40 manual calls per hour, plus roughly 12 automatic calls at the 5-minute floor
is roughly 52 per hour, about 10.4% of the 500/hour Nexus budget.

The Nexus daily budget is 20,000/day (resets 00:00 GMT) and the hourly budget is
500/hour (resets each hour), per API key or OAuth token. The budget is the
user's, shared across all their Nexus tools (Vortex, MO2, the Nexus Mod App,
browser sessions, and Curator). The API does not break the budget down per
client, so Curator cannot know how much of the reported remaining is its own;
these proactive limits bound only Curator's own contribution.

## See also

- [Nexus API rate limiting](../architecture/nexus-rate-limiting.md): the reactive
  handling (the `x-rl-*` observation, the 429/403 hard wall, the update-check
  `RateLimited` flag).
- [The update check runner](src/ui.md): the `UpdateCheckRunner` surface.
