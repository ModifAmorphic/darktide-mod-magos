# UI layer: architecture

The UI layer is the Avalonia 12 front end of Modificus Curator. It is the only
part of the codebase that talks to the user: the shell window, profile
management, the mod list, every dialog (Settings, Preferences, Integrations,
Manage profiles, import, discovery escape-hatch, progress), global
preferences (theme, font scale, language), and the dynamic-language
infrastructure. The UI never touches the filesystem, the network, or any OS
API directly. Every data operation flows through a backend library service;
the UI only presents state and orchestrates calls.

This doc covers how the UI is structured, how the active profile is owned, how
the shell wires its commands, how the mod list and the update UI behave, how
the DMF install prompt fires, and how dialogs, preferences, and i18n fit
together.

> Public surface, exact signatures, and DI registration are documented in the
> [UI reference](../reference/src/ui.md). This doc covers the
> architecture and the why.

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│ MainWindow (shell)                                                       │
│                                                                          │
│  ┌─ Top bar ───────────────────────────────────────────────────────────┐ │
│  │ Title · Profile dropdown · Manage… · Integrations… ·  Preferences · │ │
│  │                                              Settings · Launch     │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│  ┌─ Content area ──────────────────────────────────────────────────────┐ │
│  │ ModListView (the active profile's mod list; drag-and-drop target)   │ │
│  │   header: title · recent-only / rate-limit notices · refresh ·      │ │
│  │           auto-sort · Add split button (zip / folder)               │ │
│  │   rows:   name · source badge + update marker · enabled · policy ·  │ │
│  │           update button / progress · up · down · remove             │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
│  ┌─ Status strip ──────────────────────────────────────────────────────┐ │
│  │ Drawn Ellipse (running / stopped) · GameRunningText · LaunchStatusNote│ │
│  └─────────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────┘
```

The shell holds no data state of its own. Every long-lived concern is owned by
a UI-layer singleton that the shell (and other view models) inject:

```
 IProfileSession ───────────── single authority for the active profile id,
 │                             the can-change gate, and the live running-state
 │                             (a DispatcherTimer polls ISteamService every 3s)
 │
 ├── ShellViewModel ────────── profile dropdown + Launch + the command set
 │   │                         (ManageProfiles, OpenIntegrations, OpenSettings,
 │   │                         OpenPreferences); mirrors session state
 │   │
 │   └── ModListViewModel ──── the active profile's mod list; enable/disable,
 │       │                     reorder, per-mod policy, remove, import, update
 │       │
 │       └── ModItemViewModel  one row; carries state only (no service calls)
 │
 ├── UpdateCheckRunner ─────── fires IUpdateCheckService on profile load,
 │                             active-profile switch, a periodic timer, and
 │                             the manual "check now" affordance
 │
 └── DmfPromptService ──────── records triggers from IProfileService +
                               INexusAuthService; the shell calls
                               ProcessPendingAsync after the triggering dialog
                               closes so the DMF prompt is never dialog-on-dialog

 IDialogService ────────────── the testable dialog seam (every Show* is one method);
                             production DialogService owns the real Window wiring
 LocalizationService ───────── the i18n indexer + dynamic-culture INPC refresh
 IPreferencesService ───────── applies theme / font scale / language + persists
```

The backend libraries (`Profiles`, `Mods`, `Integrations`, `Steam`,
`RelayClient`, `General`, `Nxm`) sit below this layer. The UI injects
their interfaces; it never constructs a backend type directly.

## The profile session (`IProfileSession`)

`IProfileSession` is the single authority for three things: which profile is
active, whether the active profile may change right now, and whether Darktide
is running. Both the shell dropdown switch and the Manage-profiles dialog
create-sets-active route through the same gate (`RequestActive`), so the two
paths can never diverge.

`RequestActive(id)` is applied and persisted only when the game is not
running; otherwise it is a no-op (the active stays put). Delete-of-active is
gated separately by `CanDeleteProfile(id)` (false when the id is the active
one and the game is running); when delete-of-active does happen, the game is
already stopped, so `ReconcileActive` clears the active id to null. The user
then explicitly picks the next profile; Curator never auto-selects a remaining
one on the user's behalf.

The running-state is live. The production `ProfileSession` snapshots
`ISteamService.IsGameRunning()` at construction, then a `DispatcherTimer`
re-checks roughly every 3 seconds (`ProfileSession.PollInterval`). The
session exposes `Refresh()` for callers that just caused a state change (the
shell after a successful launch) so the indicator reacts immediately rather
than waiting for the next poll. The session raises
`INotifyPropertyChanged.PropertyChanged` for `ActiveProfileId` and
`IsRunning`, which the shell and other consumers subscribe to.

The session lives in the UI layer (not a backend library) because the polling
timer is UI-session glue: a `DispatcherTimer` runs on the UI thread and ties
the lifetime of the running-state signal to the desktop lifetime. Backend
services have no UI thread and no business owning one. The timer is injected
as a `startTimer` delegate so unit tests construct the session without a UI
dispatcher and drive `Refresh()` directly for deterministic state changes.

### How session changes cascade

`OnSessionPropertyChanged` on the shell mirrors `IsRunning` into the shell's
own `IsGameRunning` (which cascades through `CanSwitchProfile`,
`LaunchCommand`'s can-execute, the status-strip label, and the dropdown's
enabled state). The mod list's `OnSessionPropertyChanged` filters to
`ActiveProfileId` only: a running-state change does not reload the list (the
list stays put while the game runs; edits land on the profile the user will
launch next). Active-id changes rebuild the list from the new profile.

## The shell (`ShellViewModel` + `MainWindow`)

The shell owns the profile-list snapshot and the dropdown selection binding.
It does not own the active id, the gate, or the running-state; those are the
session's. The shell mirrors the session's authoritative active id back into
the dropdown, so a blocked change snaps the dropdown back to the real active.

### The `_syncing` guard

`_syncing` brackets any operation that re-sets `Profiles` and then re-syncs
`SelectedProfile` to the session. Replacing the dropdown's `ItemsSource`
causes the `ComboBox` to fire spurious `SelectedItem` changes: first null
(the old reference is gone), then a value match against the new collection
for the previously-selected name. Without the guard those events land in
`OnSelectedProfileChanged` with the stale value, which would call
`RequestActive` and revert the session to the pre-dialog selection (undoing
the active change a create just made inside the Manage-profiles dialog).
Bracketing the swap and re-sync under `_syncing = true` makes those spurious
events no-ops. `ManageProfiles`, `OpenIntegrations`, and `OpenSettings` all
use this pattern.

### Launch + the result branches

`LaunchCommand` calls `IRelayLaunchService.Launch(activeProfileId)` and
branches on `LaunchResult.Status`:

- **`Launched`**: a brief localized status note ("Launched 'X'") plus an
  immediate `_session.Refresh()` so the indicator and launch-availability
  react at once, not on the next poll.
- **`DiscoveryIncomplete`**: opens the focused escape-hatch dialog with the
  missing fields. No auto-retry: the user submits the paths, closes the
  dialog, and clicks Launch again. A loop here would trap the user if they
  could not get the paths right.
- **`StagingFailed`**: a localized modal alert. `Message` is null (the raw
  staging exception is for the log only); the user sees localized prose, never
  the exception body.
- **`Error`**: a modal alert with the result's message.

### The post-dialog DMF prompt path

`ManageProfiles` and `OpenIntegrations` both call
`_dmfPrompts.ProcessPendingAsync()` after their dialog closes. The
`DmfPromptService` records triggers from `IProfileService.ProfileCreated`
(fires from inside the Manage-profiles dialog's create) and
`INexusAuthService.AuthStateChanged` (fires from inside the Integrations
dialog's auth command) as pending; processing them after the dialog closes
means the DMF prompt is the topmost modal at that point, never nested on top
of the dialog that triggered it. After the prompt, the shell calls
`ModList.Reload()` so a DMF add shows without a profile switch.

## The mod list (`ModListViewModel` + `ModItemViewModel`)

`ModListViewModel` owns the active profile's mod list (the dominant content
area). It subscribes to `IProfileSession.PropertyChanged` (filtered to
`ActiveProfileId`), `LocalizationService.PropertyChanged` (culture refresh),
and `IUpdateCheckService.CheckCompleted` (badge refresh). The active profile
is the session's; the list never decides the active id, it only reloads when
the id changes.

The command set:

- **Enable / disable** (`ToggleEnabled`): the row's `Enabled` is two-way
  bound to its CheckBox; this persists the toggle through
  `IProfileService.SetModEnabled`.
- **Reorder** (`MoveUp` / `MoveDown`): swaps with the predecessor or
  successor, persists the new container-id order through
  `IProfileService.SetModOrder`, then reloads so the persisted
  `ModListEntry.Order` fields drive the display.
- **Per-mod policy** (`SetPolicyLatest` / `SetPolicyPinned`): routes through
  `IProfileService.SetModPolicy`. The pin is a constrained dropdown of the
  container's actual versions (the dropdown exposes the readable tag, stores
  the opaque folder id, and the parent wraps it as
  `PinnedPolicy(versionId)`).
- **Remove** (`Remove`): a confirm gate, then `IProfileService.RemoveMod`.
  The repository copy survives; the confirm is about the profile edit, not
  data loss.
- **Auto-sort** (`AutoSort`): applies the `IModOrderResolver` and persists.
  The current resolver is the identity stub (a no-op); a real
  dependency-driven resolver is out of v1. The seam is DI-swappable, so the
  UI wires against the abstraction now.
- **Add** (`AddMods`): the Add split button (zip picker + folder picker) and
  drag-and-drop both reduce to this command, which processes paths
  sequentially.

### The import flow

`AddMods` runs one import modal per path. For each path:

1. Peek the base folder name via `IModImportService.GetBaseName`. This
   validates the source structure (exactly one base dir with a matching
   `<base>.mod` descriptor) before any container or version is created. An
   invalid source throws here and aborts the remaining batch.
2. Hard-block a base-name collision via
   `IProfileService.GetBaseNameCollision`. Two mods with the same base
   folder name cannot coexist in one profile (the loader cannot tell them
   apart). The would-be container is excluded (a re-add of a mod already in
   the profile is not a collision). On a hit, the import is refused and the
   batch aborts; nothing is created.
3. `IModImportService.Import` (extract or copy into the repository), then
   `IProfileService.AddMod` (the profile reference). The modal's chosen
   policy drives the new entry: `LatestPolicy` (the default) tracks the
   container's newest release; `PinnedPolicy` freezes the entry to the
   version being imported (constructed from the version id the import just
   minted).

A cancelled modal, a failed peek or import, or a collision cancels the whole
remaining batch. Mods imported earlier in the batch stay imported.

The mod name is derived from each path (folder name or archive stem) and
pre-filled in the modal; the user may rename at import (the edited name
becomes the container's display name and the untracked dedup key).

### Rows carry state only

Each row is a `ModItemViewModel`: container id (immutable, the join key
against `IModRepository`), display name, source, resolved version tag,
enabled, order, policy, and the per-row policy-edit state. The row never
talks to `IProfileService` directly; the parent owns every service call, and
the view routes row interactions (toggle, move, policy, remove, update)
through code-behind handlers calling the parent's commands with the row as
the `CommandParameter`. This mirrors the established
`ManageProfilesWindow` pattern.

## The update UI

The mod list consumes `IUpdateCheckService` for update badges and the
per-mod update button. The check itself lives in Integrations (see
[mod acquisition](mod-acquisition.md) and
[Nexus API rate limiting](nexus-rate-limiting.md)); the UI only fires it,
reads its result, and renders.

### Reading the result

`ModListViewModel.OnUpdateCheckCompleted` re-applies
`IUpdateCheckService.LastResult` to the rows. The handler is marshaled to the
UI thread via an injected `invokeOnUi` seam (`Dispatcher.UIThread.Post` in
production) because `CheckCompleted` fires on the check's completing
threadpool thread and the handler iterates the UI-bound `Mods` collection.

The application is idempotent and safe to call on every completion, including
the no-auth, no-checkable-mods, and failure short circuits. Per-row
`UpdateAvailable` is set by matching `LastResult.Updates` on `ContainerId`
(indexed once into a `HashSet` for an O(1) per-row lookup). Two list-level
flags also derive from the result:

- `IsRateLimited`: the last check was rate-limited. Drives the header
  "check incomplete" notice.
- `IsRecentOnly`: the last check was Month-only and not thorough. Drives the
  header "showing recent updates" notice. Suppressed while `IsRateLimited`
  is set; the rate-limit notice takes precedence via
  `ShowRecentOnlyNotice = !IsRateLimited && IsRecentOnly`.

### The premium gate

`IsPremiumUser` is read once at construction via
`INexusAuthService.GetCurrentStateAsync()`, fire-and-forget. The read hits
the network, so blocking the UI-thread constructor on it would stall startup;
the result lands sub-second and flips the flag. There is no mid-session
refresh (re-checking on Integrations dialog close would burn an API call each
time; a user signing in mid-session needs a restart for the buttons to
appear).

### The per-mod Update command

`Update(row)` is premium-only and one-at-a-time:

- Defense: no-op when there is no active profile, another update is in flight
  (`AnyRowUpdating`), the row is not Nexus plus Latest (`IsNexusLatest`), no
  update is flagged (`UpdateAvailable`), the user is not premium
  (`IsPremiumUser`), or the row has no `NexusModId`.
- Sets `AnyRowUpdating` and `row.IsUpdating`, then calls
  `IModAcquisitionService.AcquireLatestNexusAsync(gameDomain, modId)` (the
  premium / auth-only path). The repository's `AddVersion` extracts into a
  sibling temp and atomically swaps on success, so a mid-update failure
  leaves the existing version intact.
- On success: reload so the new version and the fresh `ImportedAt` show, then
  clear the row's marker immediately (the stale `LastResult` would re-flag
  it), then fire a fresh check so the stale result is replaced.
- On failure: a user-facing alert.
- The finally block clears `row.IsUpdating` and `AnyRowUpdating` (no stuck
  state).

### The manual "check now" affordance

`CheckForUpdatesNow` routes through `UpdateCheckRunner.CheckNowAsync()` so the
runner stays the single owner of "fire a check" logic and uses the thorough
path (`IUpdateCheckService.CheckThoroughAsync`) that also catches mods outside
the Month window. `IsCheckingNow` is set before the await and cleared in the
finally block; it drives the header refresh button's enabled state and an
indeterminate `ProgressBar` that replaces the refresh icon for the duration.
The existing `CheckCompleted` subscription re-applies the result when it
lands.

### View affordances

- The source badge is a `HyperlinkButton` styled as a pill, with
  `NavigateUri` set to the row's `SourceUrl` (the mod's remote page; null for
  untracked, which the `HyperlinkButton` treats as a no-op click).
- The update-available marker is a drawn `<Ellipse>` plus a `HyperlinkButton`
  to the mod's Nexus files tab (`UpdatePageUrl`, the mod page with
  `?tab=files`), so the user's instinct to click the marker lands on the
  files page where the new release lives. Both show only when
  `UpdateAvailable` is set.
- The per-mod Update button is a drawn download-arrow `<Path>` plus an
  indeterminate `ProgressBar` (toggled by `IsUpdating`). The button's
  `IsVisible` is a `MultiBinding` over `BoolAllConverter` that ANDs the row's
  `CanShowUpdateButton` (`IsNexusLatest && UpdateAvailable && !IsUpdating`)
  with the parent's `IsPremiumUser`. The button's `IsEnabled` binds directly
  to the parent's pre-computed `IsUpdateEnabled` (`IsPremiumUser &&
  !AnyRowUpdating`).

## The DMF install prompt (`DmfPromptService`)

The DMF (Darktide Mod Framework, Nexus mod 8) install-prompt coordinator
surfaces a modal on the main window when DMF is not already in the active
profile. There are two triggers:

1. **The first time Nexus auth transitions from `None` to configured**
   (OAuth or API key), gated by the persisted
   `CuratorConfig.Nexus.DmfAuthPromptShown` flag so it fires once ever.
   Subsequent auth changes (re-login, sign-out plus re-sign-in) do not
   re-prompt.
2. **Each time a new profile is created and becomes active** (a fresh ask
   per profile, no persisted flag). A profile created while Darktide runs
   does not become active (the session gates it), so no prompt fires in that
   case.

### The three cases

On a trigger, the coordinator looks up DMF by source
(`new NexusSource { ModId = DmfModId }`) and checks the active profile's mod
list:

1. **DMF in the repo but not in the profile**: a Yes/No confirm. On Yes,
   `IProfileService.AddMod` adds it instantly (no download).
2. **DMF not in the repo plus auth configured**: a Yes/No confirm. On Yes,
   premium users get the in-app API download under a modal spinner (the
   Nexus `download_link` endpoint is premium-only) plus the add. Non-premium
   users (or unknown premium state) get their browser opened at DMF's Nexus
   files page only when Curator is registered as the `nxm://` handler (the user
   clicks Download there and the handler picks up the URL, so DMF is added to
   the active profile via the standard nxm flow); when Curator is not the
   handler, an informational alert tells the user to enable nxm links in
   Integrations (or download the archive manually) and carries the DMF files
   URL.
3. **DMF not in the repo plus auth not configured**: an informational
   OK-only alert. This case is only reachable from the new-profile trigger
   (the auth-configured trigger implies auth is set up).

Decline is respected. DMF can be added later via the normal add flow.

### Why the prompt fires from the shell

The trigger signals (`IProfileService.ProfileCreated`,
`INexusAuthService.AuthStateChanged`) fire from inside the Manage-profiles
and Integrations dialogs. Showing a modal from inside those handlers would
be dialog-on-dialog (the triggering dialog is still open). The coordinator
records each signal as pending; the shell calls `ProcessPendingAsync` after
the triggering dialog closes, so the DMF prompt is the topmost modal at that
point.

`ProcessPendingAsync` snapshots and clears the pending triggers before
processing each one, so an exception in one prompt does not leave it stuck
pending for the next call. Each prompt is wrapped in a try/catch that logs
and swallows non-cancellation exceptions, so a wiring failure never blocks
the shell's post-dialog return.

## Dialogs, preferences, and i18n

### `IDialogService`

The testable dialog seam. View models depend on this interface, not on
Avalonia `Window` construction, so their logic stays unit-testable: a test
injects a recording fake instead of a real window. The production
`DialogService` owns every real `Window` and `ShowDialog` wiring.

```csharp
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);
    Task ShowManageProfilesAsync();
    Task ShowPreferencesAsync();
    Task<ImportModResult?> ShowImportModAsync(ImportModRequest request);
    Task ShowSettingsAsync();
    Task ShowIntegrationsAsync();
    Task<bool> ShowDiscoveryEscapeHatchAsync(IReadOnlyList<string> missingFields);
    Task ShowAlertAsync(string title, string message);
    Task<T> ShowProgressAsync<T>(string title, string message, Func<Task<T>> work);
}
```

`ShowProgressAsync<T>` runs the supplied work under a buttonless, non-closeable
spinner (the `DialogTitleBar.ShowClose` styled property is set to false on
the progress dialog, so the user cannot dismiss an in-flight operation whose
partial result would be useless). The spinner is closed in either case; the
work's exception (if any) propagates to the caller.

### `IPreferencesService`

The single authority for applying user-facing preferences (theme, font scale,
language) to the running app and persisting them to `CuratorConfig`. The
composition root applies the loaded config at startup (before the main window
shows, so the first paint already reflects the user's choices); the
Preferences dialog calls `ApplyAndPersist` on each change. All three concerns
(theme variant, global font scale, UI culture) live behind one method so the
values stay consistent: nothing else in the UI touches
`RequestedThemeVariant`, the `AppFontSize` / `AppStatusFontSize` resources,
or `LocalizationService.Culture` directly.

The `PreferencesService` publishes the scaled font sizes as application
resources: `AppFontSize` (base 14px, bound by the Window style in
`App.axaml`) and `AppStatusFontSize` (base 12px, bound by the status-strip
`TextBlock`). Both scale by the user's font scale, so the status strip grows
with the body.

### `LocalizationService`

The single authority for resolving localized strings at runtime. A singleton
registered in DI, it holds the current UI culture, exposes a string indexer
used by every XAML binding, and raises `PropertyChanged` so bindings refresh
live when the culture changes.

The indexer property name is `Item[]`; raising `PropertyChanged` for
`"Item[]"` tells every Avalonia indexer binding (`{Binding [Key],
Source={StaticResource Loc}}`) to re-evaluate, so the whole UI updates the
moment the culture flips (no restart). The XAML uses `{ReflectionBinding
[Key], Source={StaticResource Loc}}` rather than a compiled binding because
the indexer-based dynamic-language path is not expressible as a compiled
binding.

A `ResourceManager` over the neutral `Strings.resx` resolves by culture. A
missing key returns the key itself (visible, never throws). The service
holds its own culture and resolves strings with it directly; it does not
mutate the thread's `CurrentUICulture`, so only the UI text follows the
chosen language.

`App.OnFrameworkInitializationCompleted` swaps the XAML resource placeholder
for the real DI singleton, so every view's `{Binding [Key],
Source={StaticResource Loc}}` resolves through the live service.

### The custom title bar

`DialogTitleBar.axaml` is the shared custom title bar reused by the modal
dialogs. The outer `Border` carries
`WindowDecorationProperties.ElementRole="TitleBar"`, so the OS handles native
drag and double-click-to-maximize over this region (the Avalonia 12.x
custom-chrome pattern). The dialog's own `Window.Title` is mirrored as the
visible header. The close button is a drawn X `<Path>` (no Unicode glyph)
whose `Click` closes the owning window. The button carries
`WindowDecorationProperties.ElementRole="User"` so it receives pointer input
even though it overlaps the chrome; on Windows without that role the whole
title bar is claimed for non-client drag handling (`HTCAPTION`) during
`WM_NCHITTEST`, and the button would never receive events.

`DialogTitleBar.ShowClose` (a styled property, default true) hides the close
button. The progress-spinner dialog sets it to false so the user cannot
dismiss an in-flight operation.

## See also

- [UI reference](../reference/src/ui.md): public surface, exact
  signatures, and DI registration for the UI layer.
- [Modificus Curator architecture](MODIFICUS-CURATOR.md): the high-level
  tie-together (component model, the Relay contract, profiles, launch).
- [mod acquisition](mod-acquisition.md): the `NxmModDownloadHandler` (in the
  UI assembly) that coordinates the nxm download flow, and the
  `IModAcquisitionService` the per-mod Update button calls.
- [Nexus authentication](nexus-authentication.md): the auth factory and
  orchestrator the Integrations dialog drives, and the
  `AuthStateChanged` event the DMF prompt coordinator subscribes to.
- [Nexus API rate limiting](nexus-rate-limiting.md): how the update check's
  rate-limit signal becomes the mod-list "check incomplete" notice.
