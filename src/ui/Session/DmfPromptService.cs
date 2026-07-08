using System.ComponentModel;
using System.Diagnostics;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Nxm;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.Session;

/// <summary>
/// The DMF (Darktide Mod Framework, Nexus mod 8) install-prompt coordinator.
/// Surfaces a modal prompt on the main window after two triggers, when DMF is
/// not already in the active profile: (1) the first time Nexus auth transitions
/// from <see cref="NexusAuthMethod.None"/> to configured (OAuth or API key),
/// gated by a persisted flag so it fires once ever; and (2) each time a new
/// profile is created + set active (a fresh ask per profile, no persisted flag).
/// Decline is respected; the user can add DMF later via the normal add flow.
/// </summary>
/// <remarks>
/// <para>
/// <b>Trigger signals come from the backend services; the prompt fires from the
/// shell, on the main window.</b> <see cref="IProfileService.ProfileCreated"/>
/// fires from inside the ManageProfiles dialog's create call, and
/// <see cref="INexusAuthService.AuthStateChanged"/> fires from inside the
/// Integrations dialog's auth command. Showing a modal prompt from inside those
/// handlers would be a dialog-on-dialog (manage-profiles / Integrations is still
/// open). This coordinator records each signal as a pending trigger; the shell
/// calls <see cref="ProcessPendingAsync"/> after the triggering dialog closes,
/// so the DMF prompt is the topmost modal at that point.</para>
/// <para>
/// <b>The three DMF cases.</b> On a trigger, the coordinator looks up DMF by
/// source (<c>new NexusSource { ModId = <see cref="DmfModId"/> }</c>) and checks
/// the active profile's mod list. (1) DMF in the repo but not in the profile:
/// a Yes/No confirm, On Yes adds it instantly (no download). (2) DMF not in the
/// repo + Nexus auth configured: a Yes/No confirm, On Yes either downloads +
/// adds it under a spinner (premium users; the Nexus <c>download_link</c>
/// endpoint is premium-only) or opens the DMF files page in the user's browser
/// (non-premium or unknown premium state; the user clicks Download on the page
/// and the existing <c>nxm://</c> handler picks it up). (3) DMF not in the repo
/// + auth NOT configured: an informational OK-only alert (this case applies only
/// to the new-profile trigger; the auth-configured trigger implies auth is set
/// up).</para>
/// <para>
/// <b>Ask-once for the auth trigger.</b> <see cref="NexusConfig.DmfAuthPromptShown"/>
/// is set to <c>true</c> after the auth-triggered prompt fires (accepted or
/// declined), so subsequent auth changes (re-login, sign-out + re-sign-in) do
/// not re-prompt. The new-profile trigger has no such flag: each new profile is
/// a fresh ask.</para>
/// <para>
/// <b>Lives in the UI assembly.</b> Mirrors <see cref="UpdateCheckRunner"/>:
/// the coordinator observes UI-layer singletons (<see cref="IProfileSession"/>,
/// <see cref="IDialogService"/>) and orchestrates Integrations + Profiles +
/// Mods services. Registered as a singleton; the shell resolves it + calls
/// <see cref="ProcessPendingAsync"/> after the ManageProfiles + Integrations
/// dialogs close.</para>
/// </remarks>
public sealed class DmfPromptService
{
    /// <summary>
    /// The Nexus mod id of Darktide Mod Framework. DMF is required for most
    /// Darktide mods; the prompt offers to install it when missing.
    /// </summary>
    public const int DmfModId = 8;

    /// <summary>
    /// The Darktide Nexus game domain. Fixed: Curator supports only Darktide
    /// (mirrors <c>ModListViewModel.GameDomain</c> +
    /// <c>ModAcquisitionService</c>).
    /// </summary>
    private const string GameDomain = "warhammer40kdarktide";

    /// <summary>
    /// The Nexus files page for DMF. Used as the browser-open target for
    /// non-premium users (the Nexus <c>download_link</c> endpoint is
    /// premium-only, so non-premium users must visit the site to generate the
    /// per-file nxm download token). The user clicks Download on the page; the
    /// existing <c>nxm://</c> handler catches the URL and DMF is added to the
    /// active profile via the standard nxm flow.
    /// </summary>
    private const string DmfFilesUrl = "https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files";

    private readonly IProfileService _profiles;
    private readonly IProfileSession _session;
    private readonly IModRepository _repo;
    private readonly IModAcquisitionService _acquisition;
    private readonly INexusAuthService _auth;
    private readonly IConfigLoader _configLoader;
    private readonly IDialogService _dialogs;
    private readonly LocalizationService _localization;
    private readonly INxmHandlerRegistrar? _nxmRegistrar;
    private readonly ILogger<DmfPromptService> _logger;
    private readonly Func<Uri, bool> _launchExternal;

    // Pending triggers, set by the event handlers (which fire from inside the
    // ManageProfiles / Integrations dialogs) and consumed by
    // ProcessPendingAsync (called by the shell after the triggering dialog
    // closes). Single-entry each: the newest trigger of a kind wins, which is
    // correct (a second create during one dialog open is the relevant one).
    // _pendingNewProfileId is read + written on the UI thread only (ProfileCreated
    // fires from ProfileService on the UI thread). _pendingAuthConfigured is
    // volatile: AuthStateChanged fires from background threads (the OAuth and
    // API-key flows await internally), so the write may happen off-thread; the
    // read in ProcessPendingAsync is on the UI thread.
    private Guid? _pendingNewProfileId;
    private volatile bool _pendingAuthConfigured;

    public DmfPromptService(
        IProfileService profiles,
        IProfileSession session,
        IModRepository repo,
        IModAcquisitionService acquisition,
        INexusAuthService auth,
        IConfigLoader configLoader,
        IDialogService dialogs,
        LocalizationService localization,
        ILogger<DmfPromptService> logger,
        INxmHandlerRegistrar? nxmRegistrar = null,
        Func<Uri, bool>? launchExternal = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _nxmRegistrar = nxmRegistrar;
        _launchExternal = launchExternal ?? DefaultLaunchExternal;

        _profiles.ProfileCreated += OnProfileCreated;
        _auth.AuthStateChanged += OnAuthStateChanged;
    }

    /// <summary>
    /// Records a new-profile-created signal. The shell will call
    /// <see cref="ProcessPendingAsync"/> after the ManageProfiles dialog
    /// closes; this method only records the pending trigger so the prompt does
    /// not fire dialog-on-dialog.
    /// </summary>
    /// <remarks>
    /// A second create during one ManageProfiles session overwrites the first
    /// (the newest created id is the relevant one). A profile created + then
    /// deleted in the same dialog session is handled by
    /// <see cref="PromptForNewProfileAsync"/>: it checks the active id, which
    /// no longer points at the deleted profile, so no prompt fires.
    /// </remarks>
    private void OnProfileCreated(object? sender, ProfileSummary e)
    {
        _pendingNewProfileId = e.Id;
        _logger.LogDebug("Recorded pending DMF new-profile trigger for {Id}.", e.Id);
    }

    /// <summary>
    /// Records an auth-state-changed signal. The shell will call
    /// <see cref="ProcessPendingAsync"/> after the Integrations dialog closes;
    /// this method only records the pending trigger so the prompt does not fire
    /// dialog-on-dialog.
    /// </summary>
    /// <remarks>
    /// The actual gate (was auth just configured for the first time? is the
    /// flag still false? is DMF missing?) lives in
    /// <see cref="PromptForAuthConfiguredAsync"/>, which re-reads the live
    /// config at process time. This keeps the trigger cheap + the gate
    /// evaluation against the freshest state.
    /// </remarks>
    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        _pendingAuthConfigured = true;
        _logger.LogDebug("Recorded pending DMF auth-trigger.");
    }

    /// <summary>
    /// Processes any pending triggers, in the order they would naturally
    /// surface (new-profile before auth). Called by the shell after the
    /// ManageProfiles + Integrations dialogs close so the DMF prompt is the
    /// topmost modal at that point. Safe to call when nothing is pending (a
    /// no-op).
    /// </summary>
    /// <remarks>
    /// Each trigger is consumed (cleared) before it is processed so a thrown
    /// exception in one prompt does not leave it stuck pending for the next
    /// call. A failure inside a prompt is caught + logged so a wiring issue
    /// never blocks the shell's post-dialog return.</remarks>
    public async Task ProcessPendingAsync()
    {
        // Snapshot + clear before processing so an exception in one prompt
        // doesn't leave the trigger stuck for the next call.
        var newProfileId = _pendingNewProfileId;
        var authConfigured = _pendingAuthConfigured;
        _pendingNewProfileId = null;
        _pendingAuthConfigured = false;

        if (newProfileId is Guid id)
        {
            await RunPromptSafelyAsync(() => PromptForNewProfileAsync(id));
        }

        if (authConfigured)
        {
            await RunPromptSafelyAsync(PromptForAuthConfiguredAsync);
        }
    }

    /// <summary>
    /// Runs a prompt, catching any non-cancellation exception so a wiring
    /// failure or service throw does not crash the app or block the shell's
    /// post-dialog return. <see cref="OperationCanceledException"/> is also
    /// swallowed (no cancellation token is wired today; defensive only).
    /// </summary>
    private async Task RunPromptSafelyAsync(Func<Task> prompt)
    {
        try
        {
            await prompt();
        }
        catch (OperationCanceledException)
        {
            // Defensive only; no cancellation token is wired today.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMF prompt failed unexpectedly.");
        }
    }

    // ---- new-profile trigger ----------------------------------------------

    /// <summary>
    /// The new-profile trigger: prompts for DMF if the just-created profile
    /// became active + DMF is not in it. A profile created while Darktide runs
    /// does NOT become active (the session gates it), so no prompt fires in
    /// that case (correct: the user is still on their previous profile).
    /// </summary>
    private Task PromptForNewProfileAsync(Guid createdProfileId)
    {
        if (_session.ActiveProfileId != createdProfileId)
        {
            // Created but not active (game was running). The active profile did
            // not change, so there is no "new profile" surface to prompt for.
            _logger.LogDebug(
                "Skipping DMF new-profile prompt: created profile {Id} is not the active {Active}.",
                createdProfileId,
                _session.ActiveProfileId);
            return Task.CompletedTask;
        }

        return PromptIfMissingAsync(createdProfileId, isAuthTrigger: false);
    }

    // ---- auth-configured trigger ------------------------------------------

    /// <summary>
    /// The auth-trigger: prompts for DMF the first time Nexus auth transitions
    /// from <c>None</c> to configured (gated by
    /// <see cref="NexusConfig.DmfAuthPromptShown"/>), if DMF is not already in
    /// the active profile. After the prompt fires (accepted, declined, or
    /// informational), the flag is flipped so subsequent auth changes do not
    /// re-prompt.
    /// </summary>
    private Task PromptForAuthConfiguredAsync()
    {
        var nexus = _configLoader.Load().Integrations.Nexus;

        // Ask-once: if the flag is already set, never re-prompt on auth changes
        // (re-login, sign-out + re-sign-in). The flag is the durable signal
        // that the first-time prompt already happened.
        if (nexus.DmfAuthPromptShown)
        {
            _logger.LogDebug("Skipping DMF auth-trigger prompt: already shown (flag is set).");
            return Task.CompletedTask;
        }

        // The trigger fires on every auth-state change (login, sign-out,
        // re-login). Only a None -> configured transition crosses the
        // first-time threshold; a sign-out lands here with AuthMethod=None and
        // is skipped. A subsequent sign-in (None -> configured) lands here
        // with the flag still false + triggers the prompt.
        if (nexus.AuthMethod == NexusAuthMethod.None)
        {
            _logger.LogDebug("Skipping DMF auth-trigger prompt: auth method is None.");
            return Task.CompletedTask;
        }

        // No active profile means nowhere to add DMF. The user can configure
        // auth without a profile (the Integrations dialog opens from the shell
        // with no profile selected). The auth-trigger flag is NOT flipped in
        // this case: the prompt did not fire, so the first time the user has
        // both auth + an active profile we still want to ask. The next auth
        // change after they have a profile will fire the prompt + flip the
        // flag. (Acceptable trade-off: most users create a profile before
        // configuring auth, so this branch is rare.)
        if (_session.ActiveProfileId is not Guid profileId)
        {
            _logger.LogDebug("Skipping DMF auth-trigger prompt: no active profile.");
            return Task.CompletedTask;
        }

        return PromptIfMissingAsync(profileId, isAuthTrigger: true);
    }

    // ---- shared prompt body -----------------------------------------------

    /// <summary>
    /// The shared prompt body. Looks up DMF in the repo + checks the active
    /// profile's mod list, then surfaces the appropriate case (1: add, 2:
    /// download + add, 3: informational). No-op if DMF is already in the
    /// profile. After showing (when <paramref name="isAuthTrigger"/> is true),
    /// flips <see cref="NexusConfig.DmfAuthPromptShown"/> so subsequent auth
    /// changes do not re-prompt.
    /// </summary>
    private async Task PromptIfMissingAsync(Guid profileId, bool isAuthTrigger)
    {
        var dmf = _repo.FindBySource(new NexusSource { ModId = DmfModId });

        // DMF already in the profile: nothing to prompt about. The auth-trigger
        // flag is NOT flipped in this case (no prompt was shown); the next
        // auth change re-evaluates against the live state.
        var mods = _profiles.GetModList(profileId);
        if (dmf is not null && mods.Any(m => m.ContainerId == dmf.Id))
        {
            _logger.LogDebug("Skipping DMF prompt: DMF (container {Container}) is already in profile {Profile}.",
                dmf.Id, profileId);
            return;
        }

        var nexus = _configLoader.Load().Integrations.Nexus;
        var authConfigured = nexus.AuthMethod != NexusAuthMethod.None;

        var shown = false;
        try
        {
            if (dmf is not null)
            {
                // Case 1: DMF in the repo but not in this profile.
                shown = true;
                var confirmed = await _dialogs.ConfirmAsync(
                    _localization["Dmf_AddTitle"],
                    _localization["Dmf_AddMessage"]);

                if (confirmed)
                {
                    _profiles.AddMod(profileId, dmf.Id, ModVersionPolicy.Latest);
                    _logger.LogInformation(
                        "Added existing DMF container {Container} to profile {Profile} via the DMF prompt.",
                        dmf.Id, profileId);
                }
            }
            else if (authConfigured)
            {
                // Case 2: DMF not in the repo + Nexus auth configured.
                shown = true;
                var confirmed = await _dialogs.ConfirmAsync(
                    _localization["Dmf_DownloadTitle"],
                    _localization["Dmf_DownloadMessage"]);

                if (confirmed)
                {
                    // Premium users get the in-app API download; non-premium (or
                    // unknown) get the browser opened at DMF's files page. The
                    // Nexus download_link endpoint is premium-only; non-premium
                    // users must visit the site to generate the per-file nxm
                    // token. The user clicks Download on the page -> the
                    // existing nxm:// handler picks it up -> DMF is added to the
                    // active profile via the standard nxm flow. Opening the
                    // browser only helps when Curator owns the nxm handler, so
                    // the non-premium path checks the registrar first.
                    var state = await _auth.GetCurrentStateAsync();
                    if (state?.IsPremium == true)
                    {
                        await DownloadAndAddAsync(profileId);
                    }
                    else
                    {
                        await OfferDmfDownloadForNonPremiumAsync();
                    }
                }
            }
            else
            {
                // Case 3: DMF not in the repo + auth NOT configured. Applies
                // only to the new-profile trigger (the auth-configured trigger
                // implies auth is set up).
                shown = true;
                await _dialogs.ShowAlertAsync(
                    _localization["Dmf_InfoTitle"],
                    _localization["Dmf_InfoMessage"]);
            }
        }
        finally
        {
            // Flip the auth-trigger flag once the prompt has been shown
            // (whether accepted, declined, or informational). The new-profile
            // trigger has no flag (each new profile is a fresh ask).
            if (shown && isAuthTrigger)
            {
                SetAuthPromptShown();
            }
        }
    }

    /// <summary>
    /// The non-premium (or unknown-premium) DMF download path. Opening the DMF
    /// files page only helps when Curator owns the <c>nxm://</c> handler (the
    /// user clicks Download on the page and the handler picks up the URL); if
    /// Curator is not registered (or there is no registrar for this platform),
    /// the browser-open would be a dead end, so an informational alert tells
    /// the user to enable nxm links in Integrations (or download the archive
    /// manually) and carries the DMF URL.
    /// </summary>
    private async Task OfferDmfDownloadForNonPremiumAsync()
    {
        var registered = false;
        try
        {
            registered = _nxmRegistrar?.IsRegistered() ?? false;
        }
        catch (Exception ex)
        {
            // Defensive: the platform registrars catch their own probe
            // exceptions. Treat a throw as "not registered" so the informational
            // path runs (which carries the URL either way).
            _logger.LogWarning(ex, "IsRegistered probe threw during the DMF prompt; treating as not registered.");
            registered = false;
        }

        if (registered)
        {
            await OpenDmfFilesPageInBrowser();
            return;
        }

        _logger.LogInformation(
            "DMF non-premium path: Curator is not the nxm handler; showing the enable-nxm informational alert.");
        await _dialogs.ShowAlertAsync(
            _localization["Dmf_NotConfiguredTitle"],
            _localization.Format("Dmf_NotConfiguredMessage", DmfFilesUrl));
    }

    /// <summary>
    /// Opens DMF's Nexus files page in the user's default browser. Used when the
    /// user is non-premium (or premium state is unknown): the Nexus
    /// <c>download_link.json</c> endpoint is premium-only, so non-premium users
    /// must visit the site to generate the per-file nxm download token. The user
    /// clicks Download on the page; the existing nxm:// handler catches the URL
    /// and DMF is added to the active profile via the standard nxm flow. No
    /// additional confirm before opening (the user already confirmed "Download
    /// DMF?"); on a launcher failure, falls back to an alert with the URL so the
    /// user can copy it manually (better than a silent no-op).
    /// </summary>
    private async Task OpenDmfFilesPageInBrowser()
    {
        var uri = new Uri(DmfFilesUrl);
        if (_launchExternal(uri))
        {
            _logger.LogInformation(
                "Opened DMF files page in browser (non-premium user); the nxm handler will pick up the download.");
            return;
        }

        // Launcher failed (no default browser, headless, etc.). Surface the URL
        // so the user can copy it; this is a failure alert, not a "safety" confirm.
        _logger.LogWarning("Failed to open the DMF files page in the browser.");
        await _dialogs.ShowAlertAsync(
            _localization["Dmf_DownloadFailedTitle"],
            _localization.Format("Dmf_OpenBrowserFailedMessage", DmfFilesUrl));
    }

    /// <summary>
    /// Opens <paramref name="uri"/> in the user's default browser via the OS
    /// shell-open (<c>UseShellExecute = true</c>), the same pattern the OAuth
    /// browser launcher + the Integrations help link use. Returns <c>false</c>
    /// on a shell-open failure (no default browser, headless test env, missing
    /// browser binary, unusual runtime) so the caller can surface a fallback
    /// alert with the URL; programming errors still throw. The exception filter
    /// is intentionally narrow so a real wiring bug is not silently swallowed.
    /// </summary>
    private static bool DefaultLaunchExternal(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads the latest DMF MAIN release via the acquisition service, then
    /// adds it to the profile. The download runs under a modal spinner
    /// (<see cref="IDialogService.ShowProgressAsync{T}"/>) so the user sees the
    /// operation is in flight (the acquisition takes a few seconds). Errors are
    /// surfaced as an alert (the user can retry via the normal add flow).
    /// </summary>
    private async Task DownloadAndAddAsync(Guid profileId)
    {
        Guid containerId;
        try
        {
            var (id, _) = await _dialogs.ShowProgressAsync(
                _localization["Dmf_Downloading"],
                _localization["Dmf_DownloadingMessage"],
                () => _acquisition.AcquireLatestNexusAsync(GameDomain, DmfModId));
            containerId = id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMF download failed.");
            await _dialogs.ShowAlertAsync(
                _localization["Dmf_DownloadFailedTitle"],
                _localization.Format("Dmf_DownloadFailedMessage", ex.Message));
            return;
        }

        try
        {
            _profiles.AddMod(profileId, containerId, ModVersionPolicy.Latest);
            _logger.LogInformation(
                "Downloaded + added DMF container {Container} to profile {Profile} via the DMF prompt.",
                containerId, profileId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DMF AddMod failed after a successful download.");
            await _dialogs.ShowAlertAsync(
                _localization["Dmf_DownloadFailedTitle"],
                _localization.Format("Dmf_DownloadFailedMessage", ex.Message));
        }
    }

    /// <summary>
    /// Persists <see cref="NexusConfig.DmfAuthPromptShown"/> = <c>true</c> via a
    /// read-modify-save so the auth-trigger does not fire again on subsequent
    /// auth changes. Best-effort (the ConfigLoader swallows write failures).
    /// </summary>
    private void SetAuthPromptShown()
    {
        var config = _configLoader.Load();
        if (config.Integrations.Nexus.DmfAuthPromptShown)
        {
            return; // already set; avoid a redundant write
        }
        config.Integrations.Nexus.DmfAuthPromptShown = true;
        _configLoader.Save(config);
        _logger.LogInformation("DMF auth-trigger flag persisted (prompt will not re-fire on subsequent auth changes).");
    }
}
