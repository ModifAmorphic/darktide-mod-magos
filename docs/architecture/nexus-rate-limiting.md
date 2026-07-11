# Nexus API rate limiting

Modificus Curator talks to the Nexus Mods v1 REST API under a per-user rate
budget. This doc explains how Nexus's quota works, what Curator does to observe
and react to it today, what it deliberately does not do, and the call patterns
that consume the budget.

## The quota model

Nexus enforces a per-user (per API key / per OAuth token) rate budget on its v1
REST API at `api.nexusmods.com`. Every authenticated request consumes one unit
from two rolling windows:

- an **hourly** window, and
- a **daily** window.

The remaining counts and reset times for both windows come back on every
response in the `x-rl-*` headers (`x-rl-daily-limit`, `x-rl-daily-remaining`,
`x-rl-daily-reset`, and the matching `x-rl-hourly-*` trio). The standard
free-tier limits are 2000/hour and 20000/day (per Nexus's
[rate-limit help article](https://help.nexusmods.com/article/105-i-have-reached-a-daily-or-hourly-limit-api-requests-have-been-consumed-rate-limit-exceeded-what-does-this-mean));
premium accounts get higher limits. Curator does not hardcode these numbers. It
reads them from the headers.

**The budget is the user's, not Curator's.** The same daily and hourly quota is
shared across everything the user has hitting the Nexus API on their key:
Vortex, MO2, the Nexus Mod App, browser sessions, and Curator. The API does not
break the budget down per client, so Curator cannot know how much of the reported
"remaining" is theoretically its own. A rate-limit hit reported to Curator may
reflect consumption by another tool the user is running, not by Curator.

## How Curator observes

Every Nexus API call goes through `NexusClient.SendAsync`, which:

1. **Parses** the `x-rl-*` headers into a `NexusRateLimits` record
   (`NexusRateLimitsParser.Parse`): six fields, daily limit/remaining/reset and
   hourly limit/remaining/reset. Missing or unparseable headers yield zeros and
   nulls (`NexusRateLimits.Unknown`).
2. **Carries** the parsed limits on the returned `Response<T>.RateLimits`, so
   callers can inspect them.
3. **Logs** the remaining counts at Information level on every successful call
   (`Nexus API call to {uri} ok; remaining: daily=X, hourly=Y`), so the rate
   window draining is visible in the log.

There is no persistent record of the limits across calls. Each response's limits
are used by the immediate caller, or discarded once the response is consumed.

## How Curator reacts

Two reactive paths. Both run after the call has already been made and consumed a
unit.

### The hard wall

`NexusClient.EnsureSuccessAsync` runs on every non-success response. The
rate-limit signal is: HTTP **429** always, or HTTP **403** when the limit
headers are present (`x-rl-*-limit > 0`) and at least one remaining counter is
zero. A 403 with no rate-limit headers, or with a non-zero remaining, is treated
as a permissions error, not rate-limiting (this mirrors the GitHub client's
two-condition rule). On a rate-limit signal, `NexusClient` throws
`NexusRateLimitException` carrying the parsed `NexusRateLimits`, so a caller
could in principle advise when to retry. The exception propagates to the caller,
which surfaces it as an error.

### The update-check post-call flag

`UpdateCheckService.CheckAsync` (the update check that fires on profile load)
makes one `ModUpdatesAsync` call, then inspects `response.RateLimits`: if a
limit was reported and its remaining is zero (`(DailyLimit > 0 &&
DailyRemaining <= 0) || (HourlyLimit > 0 && HourlyRemaining <= 0)`), it returns
an `UpdateCheckResult` with `RateLimited = true` and skips the per-mod
comparison. The `> 0`-on-the-limit guard prevents a false positive when the
headers were absent (`NexusRateLimits.Unknown`, all zeros). The UI consumes this
flag to show "check incomplete."

Both paths react only after the call has consumed a unit or hit the wall.
Nothing anticipates the wall.

## What Curator does not do

Stated plainly, because the gaps matter as much as the handling:

- **No proactive back-off.** No operation checks the last-known remaining before
  making a call. The update check fires `ModUpdatesAsync` even if the previous
  response showed remaining at 5.
- **No low-remaining reaction.** Curator reacts at zero (the update-check flag)
  and at the hard wall (the exception). "Low but not zero" gets no throttle, no
  skip, no warning.
- **No cross-call budget tracking.** Remaining is observed per-response and
  discarded once the caller consumes it. There is no running "what is our
  remaining right now" state across operations, so nothing can reason about the
  budget between calls.
- **No shared-quota awareness.** Curator cannot tell how much of the reported
  remaining is theoretically its own (the API does not break it down per
  client), and it does not surface the shared-budget framing to the user. A
  rate-limit hit reads to the user as "Curator failed," not "the user's overall
  Nexus budget is exhausted across tools."
- **No retry/backoff on the hard wall.** A `NexusRateLimitException` propagates
  as a terminal error for the operation; Curator does not wait for the reset
  window and retry.

Net: Curator observes the rate window on every call and reacts to the wall, but it
does not actively manage the budget or avoid the wall.

## What consumes the budget

Only authenticated calls to `api.nexusmods.com/v1/*` count. Per operation:

- **Update check:** 1 `ModUpdatesAsync` call per profile load (app start with
  the restored profile, plus each profile switch), plus a bounded number of
  `ListModFilesAsync` calls (one per flagged mod that exceeds the tolerance +
  hasn't been pinned). The reconciliation pin suppresses repeat calls: once a
  mod is reconciled, it is skipped until its Month `latest_file_update` changes
  or a new version is imported, so a steady-state check is typically just the
  1 Month call. A rate-limited or failed reconciliation leaves the mod unpinned,
  so the next check retries it.
- **Mod acquisition (download):** about 3 calls per download
  (`DownloadLinksAsync` + `GetModInfoAsync` + `ListModFilesAsync`). This is
  parity with Vortex, which Nexus's help article cites at 3 calls per download.
- **API-key validate:** 1 call (`ValidateAsync`), only when the user validates
  an API key in the Integrations dialog.
- **OAuth userinfo:** on `users.nexusmods.com` (a separate host, the OIDC
  issuer), not `api.nexusmods.com`. The `x-rl-*` quota is the API host's; the
  OAuth userinfo endpoint does not carry it.
- **The archive CDN download** (the actual file bytes): served from a CDN URL
  returned by `DownloadLinksAsync`, on a separate CDN host. Not an API call, not
  counted.

So a typical session is a handful of check calls plus a few calls per download,
essentially all user-initiated. The update check is the only automatic
(non-user-initiated) call, and it fires once per profile load, not periodically.

## See also

- [integrations reference](../reference/src/integrations.md): the
  `INexusClient` surface, the `Response<T>` and `NexusRateLimits` types, and the
  typed `NexusRateLimitException`.
- [Nexus authentication](nexus-authentication.md): the API-key and OAuth auth
  paths the rate-limited calls ride on.
- [Mod acquisition](mod-acquisition.md): the download flow whose ~3 calls per
  acquisition are the main user-initiated budget consumer.
