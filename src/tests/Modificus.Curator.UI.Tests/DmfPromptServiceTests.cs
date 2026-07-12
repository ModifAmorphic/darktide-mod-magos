using Modificus.Curator.Config;
using Modificus.Curator.Integrations;
using Modificus.Curator.Mods;
using Modificus.Curator.Profiles;
using Modificus.Curator.UI.Dialogs;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Behaviors of the <see cref="DmfPromptService"/>: the three DMF cases
/// (add existing / download + add / informational), the two triggers
/// (new-profile creation + first Nexus auth setup), the ask-once flag for the
/// auth trigger, the decline path, and the dialog-on-dialog avoidance (the
/// prompt does not fire from inside the event handler; it waits for the shell
/// to call <see cref="DmfPromptService.ProcessPendingAsync"/>).
/// </summary>
/// <remarks>
/// All against the hand-rolled fakes in <see cref="TestDoubles"/>: the
/// <c>FakeProfileService</c> raises <c>ProfileCreated</c> from
/// <c>CreateProfile</c>; the <c>FakeNexusAuthService</c> exposes
/// <c>RaiseAuthStateChanged</c>; the <c>FakeDialogService</c> records every
/// call + drives the <c>ShowProgressAsync</c> work to completion.
/// </remarks>
public sealed class DmfPromptServiceTests
{
    private static readonly LocalizationService Localization = new();

    /// <summary>
    /// Builds a coordinator + a tuple of its fakes so each test can seed +
    /// assert on the specific dependencies it cares about.
    /// </summary>
    /// <param name="launchExternal">Optional spy for the browser-launcher seam.
    /// When omitted the builder wires <see cref="TestLauncher.NoOp"/>, a harmless
    /// recorder that NEVER shell-opens, so the case-2 non-premium browser path
    /// can never reach the production <c>Process.Start</c> fallback. Tests that
    /// exercise the case-2 non-premium browser-open path pass a recorder so they
    /// can assert on the URL.</param>
    private static (DmfPromptService Service, FakeProfileService Profiles, FakeProfileSession Session,
        FakeModRepository Repo, FakeModAcquisitionService Acquisition, FakeNexusAuthService Auth,
        FakeConfigLoader Config, FakeDialogService Dialogs) Build(
            FakeProfileService? profiles = null,
            FakeProfileSession? session = null,
            FakeModRepository? repo = null,
            FakeModAcquisitionService? acquisition = null,
            FakeNexusAuthService? auth = null,
            FakeConfigLoader? config = null,
            FakeDialogService? dialogs = null,
            FakeNxmHandlerRegistrar? nxmRegistrar = null,
            Func<Uri, bool>? launchExternal = null)
    {
        profiles ??= TestDoubles.Profiles();
        session ??= new FakeProfileSession(() => profiles.ListProfiles());
        repo ??= new FakeModRepository();
        acquisition ??= new FakeModAcquisitionService();
        auth ??= new FakeNexusAuthService();
        config ??= new FakeConfigLoader();
        dialogs ??= new FakeDialogService();
        // SAFETY: an omitted launcher seam defaults to the harmless no-op
        // recorder (never the production Process.Start fallback).
        var service = new DmfPromptService(
            profiles, session, repo, acquisition, auth, config, dialogs,
            Localization, NullLogger<DmfPromptService>.Instance, nxmRegistrar,
            launchExternal ?? TestLauncher.NoOp);
        return (service, profiles, session, repo, acquisition, auth, config, dialogs);
    }

    // ---- case 1: DMF in repo, not in profile -> offer add -----------------

    [Fact]
    public async Task NewProfile_case1_dmf_in_repo_not_in_profile_offers_add()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dmf = repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var dialogs = new FakeDialogService();

        // Build the coordinator FIRST so its ProfileCreated subscription is in
        // place, then drive the create (which fires the signal), then process.
        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // Confirm fired; one AddMod call against the existing DMF container.
        Assert.Equal(1, dialogs.ConfirmCalls);
        var add = Assert.Single(profiles.AddModCalls);
        Assert.Equal(created.Id, add.Id);
        Assert.Equal(dmf.Id, add.ContainerId);
    }

    [Fact]
    public async Task NewProfile_case1_decline_does_not_add()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var dialogs = new FakeDialogService { ConfirmResult = false }; // user says No

        var (service, _, _, _, _, _, _, _) = Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls); // prompt did fire
        Assert.Empty(profiles.AddModCalls); // nothing added
    }

    // ---- case 2: DMF not in repo, auth configured -> offer download -------

    [Fact]
    public async Task NewProfile_case2_dmf_not_in_repo_with_auth_offers_download_then_adds()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository(); // no DMF
        var acquisition = new FakeModAcquisitionService();
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        var dialogs = new FakeDialogService(); // ConfirmResult default = true

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, config: config, dialogs: dialogs);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // Confirm fired (the download confirm).
        Assert.Equal(1, dialogs.ConfirmCalls);
        // Progress dialog drove the acquisition.
        Assert.Single(dialogs.ProgressCalls);
        // Acquisition was called with DMF's mod id + the Darktide domain.
        var acquireCall = Assert.Single(acquisition.LatestNexusCalls);
        Assert.Equal(DmfPromptService.DmfModId, acquireCall.ModId);
        Assert.Equal("warhammer40kdarktide", acquireCall.GameDomain);
        // AddMod was called against the acquisition's returned container id.
        var add = Assert.Single(profiles.AddModCalls);
        Assert.Equal(created.Id, add.Id);
        Assert.Equal(acquisition.NextResult.ContainerId, add.ContainerId);
    }

    [Fact]
    public async Task NewProfile_case2_download_failure_alerts_and_does_not_add()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository(); // no DMF
        var acquisition = new FakeModAcquisitionService
        {
            ThrowNext = new InvalidOperationException("boom"),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, config: config, dialogs: dialogs);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // The download confirm fired + the user accepted.
        Assert.Equal(1, dialogs.ConfirmCalls);
        // The acquisition was attempted.
        Assert.Single(acquisition.LatestNexusCalls);
        // A failure alert was shown.
        Assert.Single(dialogs.AlertCalls);
        // No AddMod: the download did not succeed.
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task NewProfile_case2_decline_does_not_download()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        var dialogs = new FakeDialogService { ConfirmResult = false }; // user says No

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, config: config, dialogs: dialogs);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task NewProfile_case2_non_premium_user_opens_browser_at_dmf_files_url()
    {
        // The Nexus download_link endpoint is premium-only. Non-premium users
        // must visit the site to generate the per-file nxm token, so on a Yes
        // the prompt opens the DMF files page in the browser; the existing
        // nxm:// handler picks up the resulting download. The browser-open path
        // runs only when Curator is registered as the nxm handler, so this test
        // wires a registered registrar.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository(); // no DMF
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        var dialogs = new FakeDialogService(); // ConfirmResult default = true
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };

        var launchedUris = new List<Uri>();
        Func<Uri, bool> spy = uri =>
        {
            launchedUris.Add(uri);
            return true;
        };

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, config, dialogs, registrar, spy);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // The download confirm fired (the user accepted).
        Assert.Equal(1, dialogs.ConfirmCalls);
        // The browser launcher was called exactly once with DMF's files URL.
        var launched = Assert.Single(launchedUris);
        Assert.Equal("https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files", launched.ToString());
        // No in-app API download, no AddMod (that happens later via the nxm
        // handler), no progress spinner, no failure alert (launch succeeded).
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(profiles.AddModCalls);
        Assert.Empty(dialogs.ProgressCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task NewProfile_case2_premium_state_unknown_treats_as_non_premium()
    {
        // When the verify call failed, IsPremium is null. Safer to fall back to
        // the browser-open path (a premium user just visits the site; a
        // non-premium user avoids a 403). Wires a registered registrar so the
        // browser-open path runs.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "name", IsPremium: null),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        var dialogs = new FakeDialogService();
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };

        var launchedUris = new List<Uri>();
        Func<Uri, bool> spy = uri =>
        {
            launchedUris.Add(uri);
            return true;
        };

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, config, dialogs, registrar, spy);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Single(launchedUris);
        Assert.Empty(acquisition.LatestNexusCalls);
    }

    [Fact]
    public async Task NewProfile_case2_non_premium_and_not_registered_shows_enable_nxm_alert()
    {
        // When Curator is NOT the nxm handler, opening the DMF files page would
        // be a dead end (the download click would route to another manager). The
        // prompt instead shows an informational alert that tells the user to
        // enable nxm links in Integrations (or download manually) and carries
        // the DMF URL.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        var dialogs = new FakeDialogService();
        var registrar = new FakeNxmHandlerRegistrar { Registered = false };

        var launchedUris = new List<Uri>();
        Func<Uri, bool> spy = uri =>
        {
            launchedUris.Add(uri);
            return true;
        };

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, config, dialogs, registrar, spy);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // The download confirm fired (the user accepted) but no browser open.
        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Empty(launchedUris);
        // An informational alert carries the DMF URL so the user can navigate
        // manually.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains(
            "https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files",
            alert.Message);
        Assert.Empty(acquisition.LatestNexusCalls);
        Assert.Empty(profiles.AddModCalls);
    }

    [Fact]
    public async Task NewProfile_case2_non_premium_no_registrar_shows_enable_nxm_alert()
    {
        // Same as above but with no registrar at all (unsupported platform): the
        // informational alert runs because there is nothing to probe / open.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, config, dialogs);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains(
            "https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files",
            alert.Message);
    }

    [Fact]
    public async Task NewProfile_case2_browser_launch_failure_alerts_with_url()
    {
        // If the OS shell-open fails (no default browser, headless), surface the
        // URL in an alert so the user can copy it manually instead of a silent
        // no-op. Wires a registered registrar so the browser-open path runs.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.ApiKey, "free", IsPremium: false),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        var dialogs = new FakeDialogService();
        var registrar = new FakeNxmHandlerRegistrar { Registered = true };

        Func<Uri, bool> failingLauncher = _ => false; // shell-open failed

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, config, dialogs, registrar, failingLauncher);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        // One failure alert carrying the DMF URL.
        var alert = Assert.Single(dialogs.AlertCalls);
        Assert.Contains(
            "https://www.nexusmods.com/warhammer40kdarktide/mods/8?tab=files",
            alert.Message);
        // No in-app download.
        Assert.Empty(acquisition.LatestNexusCalls);
    }

    [Fact]
    public async Task NewProfile_case2_premium_user_uses_in_app_download()
    {
        // Regression: the premium path (the API download under a spinner + add)
        // still works and does NOT open the browser.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var acquisition = new FakeModAcquisitionService();
        var auth = new FakeNexusAuthService
        {
            State = new NexusAuthState(NexusAuthMethod.OAuth, "premium", IsPremium: true),
        };
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        var dialogs = new FakeDialogService();

        var launchedUris = new List<Uri>();
        Func<Uri, bool> spy = uri =>
        {
            launchedUris.Add(uri);
            return true;
        };

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, acquisition, auth, config, dialogs, null, spy);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        Assert.Equal(1, dialogs.ConfirmCalls);
        // Premium -> in-app API download (not the browser-open path).
        Assert.Empty(launchedUris);
        var acquireCall = Assert.Single(acquisition.LatestNexusCalls);
        Assert.Equal(DmfPromptService.DmfModId, acquireCall.ModId);
        Assert.Single(dialogs.ProgressCalls);
        var add = Assert.Single(profiles.AddModCalls);
        Assert.Equal(created.Id, add.Id);
        Assert.Equal(acquisition.NextResult.ContainerId, add.ContainerId);
    }

    // ---- case 3: DMF not in repo, auth NOT configured -> informational ----

    [Fact]
    public async Task NewProfile_case3_no_dmf_no_auth_shows_informational_alert()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var config = new FakeConfigLoader(); // AuthMethod defaults to None
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, config: config, dialogs: dialogs);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;

        await service.ProcessPendingAsync();

        // No Yes/No prompt; only the informational alert.
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Single(dialogs.AlertCalls);
        // No acquisition, no AddMod.
        Assert.Empty(profiles.AddModCalls);
    }

    // ---- DMF already in the profile -> no prompt --------------------------

    [Fact]
    public async Task NewProfile_skips_when_dmf_already_in_profile()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dmf = repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        var created = profiles.CreateProfile("New");
        session.ActiveProfileId = created.Id;
        // Seed DMF into the new profile (already added).
        profiles.WithMods(created.Id,
            new ModListEntry { ContainerId = dmf.Id, Enabled = true, Order = 0 });

        await service.ProcessPendingAsync();

        // DMF is already in the profile: no prompt, no add, no alert.
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
        // No new AddMod call (the entry is already there; the test seeded it directly).
        Assert.Empty(profiles.AddModCalls);
    }

    // ---- new-profile trigger is gated on the new profile being active -----

    [Fact]
    public async Task NewProfile_skips_when_the_new_profile_did_not_become_active()
    {
        // A profile created while the game is running does NOT become active
        // (the session gates it); the new-profile trigger should not fire.
        var existing = new ProfileSummary(Guid.NewGuid(), "Existing");
        var profiles = TestDoubles.Profiles(existing);
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = existing.Id,
            IsRunning = true, // gate: RequestActive is a no-op while running
        };
        var repo = new FakeModRepository();
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        // Create a new profile while running; the active id stays on `existing`.
        // session.IsRunning blocks the active change in production; mirror it
        // by NOT flipping ActiveProfileId here.
        profiles.CreateProfile("New");

        await service.ProcessPendingAsync();

        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- auth trigger: ask-once flag --------------------------------------

    [Fact]
    public async Task AuthTrigger_first_time_prompts_and_sets_flag()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var existing = new ProfileSummary(Guid.NewGuid(), "Existing");
        profiles = TestDoubles.Profiles(existing);
        session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = existing.Id,
        };
        var repo = new FakeModRepository();
        var auth = new FakeNexusAuthService();
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.ApiKey;
        // Flag starts false (default).
        Assert.False(config.Config.Integrations.Nexus.DmfAuthPromptShown);
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, configAfter, dialogsAfter) =
            Build(profiles, session, repo, auth: auth, config: config, dialogs: dialogs);

        // Simulate the auth action that fires the signal.
        auth.RaiseAuthStateChanged();
        await service.ProcessPendingAsync();

        // The flag is now set (the prompt fired).
        Assert.True(configAfter.Config.Integrations.Nexus.DmfAuthPromptShown);
        // The download confirm fired (case 2: no DMF + auth configured).
        Assert.Equal(1, dialogsAfter.ConfirmCalls);
    }

    [Fact]
    public async Task AuthTrigger_does_not_re_prompt_after_flag_set()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var existing = new ProfileSummary(Guid.NewGuid(), "Existing");
        profiles = TestDoubles.Profiles(existing);
        session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = existing.Id,
        };
        var repo = new FakeModRepository();
        var auth = new FakeNexusAuthService();
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        config.Config.Integrations.Nexus.DmfAuthPromptShown = true; // already shown
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, auth: auth, config: config, dialogs: dialogs);

        // Sign-out + sign-in: AuthStateChanged fires both times.
        auth.RaiseAuthStateChanged();
        await service.ProcessPendingAsync();

        // No prompt (the flag is set).
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    [Fact]
    public async Task AuthTrigger_skips_when_method_is_None()
    {
        // Sign-out fires AuthStateChanged but lands AuthMethod=None; not the
        // first-time-configured threshold.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var existing = new ProfileSummary(Guid.NewGuid(), "Existing");
        profiles = TestDoubles.Profiles(existing);
        session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = existing.Id,
        };
        var repo = new FakeModRepository();
        var auth = new FakeNexusAuthService();
        var config = new FakeConfigLoader();
        // AuthMethod defaults to None.
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, configAfter, _) =
            Build(profiles, session, repo, auth: auth, config: config, dialogs: dialogs);

        auth.RaiseAuthStateChanged();
        await service.ProcessPendingAsync();

        Assert.Equal(0, dialogs.ConfirmCalls);
        // Flag NOT flipped (no prompt fired).
        Assert.False(configAfter.Config.Integrations.Nexus.DmfAuthPromptShown);
    }

    [Fact]
    public async Task AuthTrigger_skips_when_no_active_profile()
    {
        // Auth configured but no profile active: no surface for the prompt.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = null,
        };
        var repo = new FakeModRepository();
        var auth = new FakeNexusAuthService();
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, configAfter, _) =
            Build(profiles, session, repo, auth: auth, config: config, dialogs: dialogs);

        auth.RaiseAuthStateChanged();
        await service.ProcessPendingAsync();

        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.False(configAfter.Config.Integrations.Nexus.DmfAuthPromptShown);
    }

    [Fact]
    public async Task AuthTrigger_case1_dmf_in_repo_adds_existing()
    {
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var existing = new ProfileSummary(Guid.NewGuid(), "Existing");
        profiles = TestDoubles.Profiles(existing);
        session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = existing.Id,
        };
        var repo = new FakeModRepository();
        var dmf = repo.Seed(new NexusSource { ModId = DmfPromptService.DmfModId }, "DMF", "1.0");
        var auth = new FakeNexusAuthService();
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        var dialogs = new FakeDialogService();

        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, auth: auth, config: config, dialogs: dialogs);

        auth.RaiseAuthStateChanged();
        await service.ProcessPendingAsync();

        // Case 1: add-confirm (not download).
        Assert.Equal(1, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.ProgressCalls);
        var add = Assert.Single(profiles.AddModCalls);
        Assert.Equal(dmf.Id, add.ContainerId);
    }

    // ---- dialog-on-dialog avoidance ---------------------------------------

    [Fact]
    public async Task ProfileCreated_does_not_synchronously_show_a_dialog()
    {
        // The signal fires from inside the ManageProfiles dialog; the prompt
        // must NOT fire synchronously (dialog-on-dialog). It must wait for
        // ProcessPendingAsync.
        var profiles = TestDoubles.Profiles();
        var session = new FakeProfileSession(() => profiles.ListProfiles());
        var repo = new FakeModRepository();
        var dialogs = new FakeDialogService();
        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, dialogs: dialogs);

        // Simulate the create inside the ManageProfiles dialog.
        profiles.CreateProfile("New");

        // No prompt fired yet (signal is pending).
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);

        // After ProcessPendingAsync (shell, post-dialog), the prompt fires.
        // (Active profile is null in this test -> skipped; use a session w/
        // active id to verify the prompt fires after the process call.)
    }

    [Fact]
    public async Task AuthStateChanged_does_not_synchronously_show_a_dialog()
    {
        var profiles = TestDoubles.Profiles();
        var existing = new ProfileSummary(Guid.NewGuid(), "Existing");
        profiles = TestDoubles.Profiles(existing);
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = existing.Id,
        };
        var repo = new FakeModRepository();
        var auth = new FakeNexusAuthService();
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        var dialogs = new FakeDialogService();
        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, auth: auth, config: config, dialogs: dialogs);

        // Simulate the auth action inside the Integrations dialog.
        auth.RaiseAuthStateChanged();

        // No prompt fired yet (signal is pending).
        Assert.Equal(0, dialogs.ConfirmCalls);

        // After ProcessPendingAsync (shell, post-dialog), the prompt fires.
        await service.ProcessPendingAsync();
        Assert.Equal(1, dialogs.ConfirmCalls);
    }

    // ---- nothing pending -> no-op -----------------------------------------

    [Fact]
    public async Task ProcessPending_with_no_signals_is_a_noop()
    {
        var (service, _, _, _, _, _, _, dialogs) = Build();
        await service.ProcessPendingAsync();
        Assert.Equal(0, dialogs.ConfirmCalls);
        Assert.Empty(dialogs.AlertCalls);
    }

    // ---- trigger is consumed after processing -----------------------------

    [Fact]
    public async Task ProcessPending_consumes_signal_so_second_call_does_not_re_prompt()
    {
        var profiles = TestDoubles.Profiles();
        var existing = new ProfileSummary(Guid.NewGuid(), "Existing");
        profiles = TestDoubles.Profiles(existing);
        var session = new FakeProfileSession(() => profiles.ListProfiles())
        {
            ActiveProfileId = existing.Id,
        };
        var repo = new FakeModRepository();
        var auth = new FakeNexusAuthService();
        var config = new FakeConfigLoader();
        config.Config.Integrations.Nexus.AuthMethod = NexusAuthMethod.OAuth;
        var dialogs = new FakeDialogService();
        var (service, _, _, _, _, _, _, _) =
            Build(profiles, session, repo, auth: auth, config: config, dialogs: dialogs);

        auth.RaiseAuthStateChanged();
        await service.ProcessPendingAsync();
        Assert.Equal(1, dialogs.ConfirmCalls);

        // Second call: no new signal, no re-prompt (the flag is also set now,
        // so even a fresh signal would skip; this asserts the consumption
        // separately from the flag).
        await service.ProcessPendingAsync();
        Assert.Equal(1, dialogs.ConfirmCalls);
    }
}
