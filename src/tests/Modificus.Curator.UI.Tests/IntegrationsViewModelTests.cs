using System.ComponentModel;
using System.Net;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Modificus.Curator.UI.Tests;

/// <summary>
/// Tests for <see cref="IntegrationsViewModel"/>: the redesigned two-block
/// Integrations dialog with the active-method indicator, the masked + persisted
/// API-key field with Show eye toggle, the validate-on-existing path, the
/// status-line wording that names the active method, the OAuth-login + API-key
/// commands routing through the service, the disabled-while-running gate, and
/// the Sign-out command clearing state.
/// </summary>
public sealed class IntegrationsViewModelTests
{
    private static readonly LocalizationService Localization = new();
    private static readonly ILogger<IntegrationsViewModel> Logger = NullLogger<IntegrationsViewModel>.Instance;

    // ---- status line on open ----------------------------------------------

    [Fact]
    public async Task RefreshAsync_shows_not_signed_in_when_None()
    {
        var (vm, _) = await BuildAndRefresh(state: null);

        Assert.Equal(Localization["Integrations_StatusNotSignedIn"], vm.StatusLine);
        Assert.False(vm.IsAuthenticated);
        Assert.Equal(NexusAuthMethod.None, vm.ActiveMethod);
        Assert.False(vm.IsOAuthActive);
        Assert.False(vm.IsApiKeyActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_signed_in_via_oauth_when_OAuth_premium_user()
    {
        var (vm, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.OAuth, "OAuthUser", IsPremium: true));

        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInOAuthPremium", "OAuthUser"),
            vm.StatusLine);
        Assert.True(vm.IsAuthenticated);
        Assert.Equal(NexusAuthMethod.OAuth, vm.ActiveMethod);
        Assert.True(vm.IsOAuthActive);
        Assert.False(vm.IsApiKeyActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_signed_in_via_oauth_when_non_premium()
    {
        var (vm, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.OAuth, "OAuthUser", IsPremium: false));

        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInOAuth", "OAuthUser"),
            vm.StatusLine);
        Assert.True(vm.IsOAuthActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_signed_in_via_apikey_when_ApiKey_user()
    {
        var (vm, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "ApiUser", IsPremium: false, ApiKey: "the-key"));

        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInApiKey", "ApiUser"),
            vm.StatusLine);
        Assert.True(vm.IsApiKeyActive);
        Assert.False(vm.IsOAuthActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_signed_in_via_apikey_when_premium_ApiKey_user()
    {
        var (vm, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "ApiUser", IsPremium: true, ApiKey: "the-key"));

        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInApiKeyPremium", "ApiUser"),
            vm.StatusLine);
        Assert.True(vm.IsApiKeyActive);
    }

    [Fact]
    public async Task RefreshAsync_shows_method_aware_unverified_when_verify_returns_null_name()
    {
        // When the verify call fails, the service returns a state with a null
        // name. The VM must show the method-aware unverified status (so the
        // user knows WHICH method is configured-but-unverifiable, not a generic
        // "signed in").
        var oauthState = new NexusAuthState(NexusAuthMethod.OAuth, Name: null, IsPremium: null);
        var (oauthVm, _) = await BuildAndRefresh(state: oauthState);
        Assert.Equal(Localization["Integrations_StatusSignedInOAuthUnverified"], oauthVm.StatusLine);
        Assert.True(oauthVm.IsOAuthActive);

        var apiKeyState = new NexusAuthState(
            NexusAuthMethod.ApiKey, Name: null, IsPremium: null, ApiKey: "the-key");
        var (apiKeyVm, _) = await BuildAndRefresh(state: apiKeyState);
        Assert.Equal(Localization["Integrations_StatusSignedInApiKeyUnverified"], apiKeyVm.StatusLine);
        Assert.True(apiKeyVm.IsApiKeyActive);
    }

    // ---- masked + persisted API-key field --------------------------------

    [Fact]
    public async Task RefreshAsync_populates_ApiKey_from_state_when_method_is_ApiKey()
    {
        // The masked field shows the persisted key (so the user sees one is
        // configured, without re-entering). The value is real (the masking is
        // purely visual via PasswordChar).
        var (vm, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "U", false, ApiKey: "persisted-key"));

        Assert.Equal("persisted-key", vm.ApiKey);
        // Masked by default (no reveal-on-open).
        Assert.False(vm.IsApiKeyRevealed);
        Assert.Equal('\u2022', vm.ApiKeyMaskChar);
    }

    [Fact]
    public async Task RefreshAsync_clears_ApiKey_when_method_is_None()
    {
        var (vm, _) = await BuildAndRefresh(state: null);

        Assert.Equal(string.Empty, vm.ApiKey);
    }

    [Fact]
    public async Task RefreshAsync_clears_ApiKey_when_method_is_OAuth()
    {
        // When OAuth is active, the API-key field is empty (the placeholder
        // shows; the field is the inactive alternative).
        var (vm, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.OAuth, "U", false));

        Assert.Equal(string.Empty, vm.ApiKey);
    }

    [Fact]
    public async Task RefreshAsync_resets_reveal_on_each_apply()
    {
        // After a refresh (e.g. post-action), the field is masked again even if
        // the user had revealed it (no surprise plaintext after a state change).
        var (vm, _) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "U", false, ApiKey: "k"));
        vm.ToggleApiKeyRevealCommand.Execute(null);
        Assert.True(vm.IsApiKeyRevealed);

        await vm.RefreshAsync();

        Assert.False(vm.IsApiKeyRevealed);
        Assert.Equal('\u2022', vm.ApiKeyMaskChar);
    }

    // ---- Show eye toggle --------------------------------------------------

    [Fact]
    public async Task ToggleApiKeyReveal_flips_mask_char()
    {
        var (vm, _) = await BuildAndRefresh(state: null);

        Assert.False(vm.IsApiKeyRevealed);
        Assert.Equal('\u2022', vm.ApiKeyMaskChar);

        vm.ToggleApiKeyRevealCommand.Execute(null);

        Assert.True(vm.IsApiKeyRevealed);
        Assert.Equal('\0', vm.ApiKeyMaskChar);

        vm.ToggleApiKeyRevealCommand.Execute(null);

        Assert.False(vm.IsApiKeyRevealed);
        Assert.Equal('\u2022', vm.ApiKeyMaskChar);
    }

    [Fact]
    public async Task ToggleApiKeyReveal_disabled_while_login_in_flight()
    {
        // Block the OAuth login on a TCS so we can observe the IsBusy state.
        var (vm, auth) = await BuildAndRefresh(state: null);
        var tcs = new TaskCompletionSource<NexusAuthResult>();
        auth.NextOAuthTask = tcs.Task;

        var task = vm.LoginWithOAuthCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.ToggleApiKeyRevealCommand.CanExecute(null));

        tcs.SetResult(NexusAuthResult.Success("U", false));
        await task;

        Assert.False(vm.IsBusy);
        Assert.True(vm.ToggleApiKeyRevealCommand.CanExecute(null));
    }

    // ---- API-key Validate -------------------------------------------------

    [Fact]
    public async Task ValidateApiKey_invokes_service_and_updates_status_on_success()
    {
        var (vm, auth) = await BuildAndRefresh(state: null);
        auth.NextApiKeyResult = NexusAuthResult.Success("ApiUser", isPremium: false);
        auth.NextStateAfterApiKey = new NexusAuthState(
            NexusAuthMethod.ApiKey, "ApiUser", false, ApiKey: "the-key");

        vm.ApiKey = "the-key";
        await vm.ValidateApiKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.ApiKeyLoginCalls);
        Assert.Equal("the-key", auth.LastApiKey);
        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInApiKey", "ApiUser"),
            vm.StatusLine);
        Assert.True(vm.IsAuthenticated);
        Assert.True(vm.IsApiKeyActive);
        // The field still shows the (now-persisted) key, masked, so the user
        // can re-validate without re-entering (NOT cleared on success like the
        // legacy flow).
        Assert.Equal("the-key", vm.ApiKey);
        Assert.False(vm.IsApiKeyRevealed);
    }

    [Fact]
    public async Task ValidateApiKey_with_empty_key_shows_message_without_calling_service()
    {
        var (vm, auth) = await BuildAndRefresh(state: null);

        vm.ApiKey = "   ";
        await vm.ValidateApiKeyCommand.ExecuteAsync(null);

        Assert.Equal(0, auth.ApiKeyLoginCalls);
        Assert.Equal(Localization["Integrations_ApiKeyEmpty"], vm.StatusLine);
    }

    [Fact]
    public async Task ValidateApiKey_surfaces_failure_inline_without_clearing_key()
    {
        var (vm, auth) = await BuildAndRefresh(state: null);
        auth.NextApiKeyResult = NexusAuthResult.Failed("HTTP 401: invalid");

        vm.ApiKey = "bad-key";
        await vm.ValidateApiKeyCommand.ExecuteAsync(null);

        Assert.Contains("HTTP 401", vm.StatusLine, StringComparison.Ordinal);
        Assert.False(vm.IsAuthenticated);
        Assert.Equal("bad-key", vm.ApiKey); // kept so the user can correct it
    }

    [Fact]
    public async Task ValidateApiKey_revalidates_persisted_masked_key_without_reentry()
    {
        // The "validate the existing masked key" path: the dialog opens with an
        // ApiKey method, the field shows the persisted key (masked), and the
        // user clicks Validate without typing anything new. The VM passes the
        // existing key (held in ApiKey from the state) back into the service.
        var (vm, auth) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "U", false, ApiKey: "existing-key"));
        auth.NextApiKeyResult = NexusAuthResult.Success("U2", isPremium: true);
        auth.NextStateAfterApiKey = new NexusAuthState(
            NexusAuthMethod.ApiKey, "U2", true, ApiKey: "existing-key");

        // No re-entry: ApiKey still holds the persisted value from refresh.
        Assert.Equal("existing-key", vm.ApiKey);
        await vm.ValidateApiKeyCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.ApiKeyLoginCalls);
        Assert.Equal("existing-key", auth.LastApiKey);
        Assert.True(vm.IsApiKeyActive);
    }

    // ---- OAuth login ------------------------------------------------------

    [Fact]
    public async Task LoginWithOAuth_invokes_service_and_updates_status()
    {
        var (vm, auth) = await BuildAndRefresh(state: null);
        auth.NextOAuthResult = NexusAuthResult.Success("OAuthUser", isPremium: false);
        auth.NextStateAfterOAuth = new NexusAuthState(NexusAuthMethod.OAuth, "OAuthUser", false);

        await vm.LoginWithOAuthCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.OAuthLoginCalls);
        Assert.Equal(
            Localization.Format("Integrations_StatusSignedInOAuth", "OAuthUser"),
            vm.StatusLine);
        Assert.True(vm.IsAuthenticated);
        Assert.True(vm.IsOAuthActive);
        // Switching to OAuth clears the API-key field (the field is the
        // inactive alternative now).
        Assert.Equal(string.Empty, vm.ApiKey);
    }

    [Fact]
    public async Task LoginWithOAuth_surfaces_failure_inline()
    {
        var (vm, auth) = await BuildAndRefresh(state: null);
        auth.NextOAuthResult = NexusAuthResult.Failed("User cancelled.");

        await vm.LoginWithOAuthCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.OAuthLoginCalls);
        Assert.Contains("User cancelled", vm.StatusLine, StringComparison.Ordinal);
        Assert.False(vm.IsAuthenticated);
    }

    // ---- Sign out ---------------------------------------------------------

    [Fact]
    public async Task SignOut_clears_state_and_resets_status()
    {
        var (vm, auth) = await BuildAndRefresh(state: new NexusAuthState(
            NexusAuthMethod.ApiKey, "U", false, ApiKey: "k"));
        Assert.True(vm.IsAuthenticated); // signed in to start
        Assert.True(vm.IsApiKeyActive);

        await vm.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.SignOutCalls);
        Assert.False(vm.IsAuthenticated);
        Assert.Equal(NexusAuthMethod.None, vm.ActiveMethod);
        Assert.False(vm.IsOAuthActive);
        Assert.False(vm.IsApiKeyActive);
        Assert.Equal(Localization["Integrations_StatusNotSignedIn"], vm.StatusLine);
        Assert.Equal(string.Empty, vm.ApiKey); // cleared on sign-out
    }

    // ---- disabled-while-running gate -------------------------------------

    [Fact]
    public async Task Auth_commands_disable_when_game_running()
    {
        var session = new FakeProfileSession { IsRunning = true };
        var (vm, _) = await BuildAndRefresh(state: null, session: session);

        Assert.False(vm.IsEnabled);
        Assert.False(vm.LoginWithOAuthCommand.CanExecute(null));
        Assert.False(vm.ValidateApiKeyCommand.CanExecute(null));
        // Sign-out also gated, plus requires IsAuthenticated.
        Assert.False(vm.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public async Task Auth_commands_live_track_running_state()
    {
        // The session raises PropertyChanged on IsRunning. The VM mirrors it via
        // IsGameRunning, which the CanExecute of the commands depends on. So a
        // running-state change flips the buttons live (mirrors the shell's
        // running-state tracking).
        var session = new FakeProfileSession { IsRunning = true };
        var (vm, _) = await BuildAndRefresh(state: null, session: session);
        Assert.False(vm.LoginWithOAuthCommand.CanExecute(null));

        session.IsRunning = false;

        Assert.True(vm.LoginWithOAuthCommand.CanExecute(null));
        Assert.True(vm.ValidateApiKeyCommand.CanExecute(null));
    }

    [Fact]
    public async Task SignOut_only_enabled_when_authenticated()
    {
        var (vm, _) = await BuildAndRefresh(state: null);
        Assert.False(vm.SignOutCommand.CanExecute(null)); // not authenticated
    }

    // ---- IsBusy gate ------------------------------------------------------

    [Fact]
    public async Task IsBusy_disables_commands_during_flight()
    {
        // Block the OAuth login on a TCS so we can observe the IsBusy state.
        var (vm, auth) = await BuildAndRefresh(state: null);
        var tcs = new TaskCompletionSource<NexusAuthResult>();
        auth.NextOAuthTask = tcs.Task;

        var task = vm.LoginWithOAuthCommand.ExecuteAsync(null);
        // The command is in flight; IsBusy must be true + commands disabled.
        Assert.True(vm.IsBusy);
        Assert.False(vm.LoginWithOAuthCommand.CanExecute(null));
        Assert.False(vm.ValidateApiKeyCommand.CanExecute(null));

        tcs.SetResult(NexusAuthResult.Success("U", false));
        await task;

        Assert.False(vm.IsBusy);
        Assert.True(vm.LoginWithOAuthCommand.CanExecute(null));
    }

    // ---- Detach -----------------------------------------------------------

    [Fact]
    public async Task Detach_unsubscribes_so_session_no_longer_drives_IsGameRunning()
    {
        var session = new FakeProfileSession { IsRunning = false };
        var (vm, _) = await BuildAndRefresh(state: null, session: session);
        Assert.False(vm.IsGameRunning);

        vm.Detach();

        // A session IsRunning change after Detach must NOT propagate to the VM.
        session.IsRunning = true;
        Assert.False(vm.IsGameRunning);
    }

    // ---- auto-update settings (toggle + interval persistence) ------------

    [Fact]
    public async Task RefreshAsync_loads_toggle_and_interval_from_config()
    {
        // The dialog open reflects the persisted auto-update settings: the
        // toggle + the interval are read live so a prior session's change shows.
        var configLoader = new FakeConfigLoader();
        configLoader.Config.Integrations.Nexus.AutoUpdateCheckEnabled = false;
        configLoader.Config.Integrations.Nexus.AutoUpdateCheckIntervalMinutes = 25;

        var (vm, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        Assert.False(vm.AutoUpdateCheckEnabled);
        Assert.Equal(25m, vm.AutoUpdateCheckIntervalMinutes);
    }

    [Fact]
    public async Task RefreshAsync_does_not_persist_when_loading_from_config()
    {
        // Populating the fields from config on open must NOT trigger the change
        // handlers' write-back (a redundant round-trip on every open). The
        // loader records zero saves from a pure RefreshAsync.
        var configLoader = new FakeConfigLoader();

        var (_, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        Assert.Equal(0, configLoader.SaveCalls);
    }

    [Fact]
    public async Task Toggling_auto_check_persists_through_config_save()
    {
        var configLoader = new FakeConfigLoader();
        var (vm, _) = await BuildAndRefresh(state: null, configLoader: configLoader);
        Assert.True(vm.AutoUpdateCheckEnabled); // default

        vm.AutoUpdateCheckEnabled = false;

        Assert.Equal(1, configLoader.SaveCalls);
        Assert.False(configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckEnabled);
    }

    [Fact]
    public async Task Changing_interval_persists_clamped_to_int()
    {
        // The NumericUpDown bound value is decimal?; the save clamps + casts to
        // int so the config (an int field) stays consistent.
        var configLoader = new FakeConfigLoader();
        var (vm, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        vm.AutoUpdateCheckIntervalMinutes = 30m;

        Assert.Equal(1, configLoader.SaveCalls);
        Assert.Equal(30, configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckIntervalMinutes);
    }

    [Fact]
    public async Task Empty_interval_persists_as_default_and_clamps_above_max()
    {
        // A cleared NumericUpDown (null) defaults to 10 on save; values above
        // 1440 clamp down so the runner never gets an unreasonable interval.
        var configLoader = new FakeConfigLoader();
        var (vm, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        vm.AutoUpdateCheckIntervalMinutes = null;
        Assert.Equal(10, configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckIntervalMinutes);

        vm.AutoUpdateCheckIntervalMinutes = 5000m;
        Assert.Equal(1440, configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckIntervalMinutes);
    }

    [Fact]
    public async Task Interval_below_one_clamps_to_one()
    {
        var configLoader = new FakeConfigLoader();
        var (vm, _) = await BuildAndRefresh(state: null, configLoader: configLoader);

        vm.AutoUpdateCheckIntervalMinutes = 0m;

        Assert.Equal(1, configLoader.LastSaved!.Integrations.Nexus.AutoUpdateCheckIntervalMinutes);
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<(IntegrationsViewModel vm, FakeNexusAuthService auth)> BuildAndRefresh(
        NexusAuthState? state = null,
        FakeProfileSession? session = null,
        FakeConfigLoader? configLoader = null)
    {
        var auth = new FakeNexusAuthService { CurrentState = state };
        session ??= new FakeProfileSession();
        configLoader ??= new FakeConfigLoader();

        var vm = new IntegrationsViewModel(auth, Localization, session, configLoader, Logger);
        await vm.RefreshAsync(); // resolve the initial status line
        return (vm, auth);
    }

    /// <summary>
    /// A recording <see cref="INexusAuthService"/>: returns preset results for
    /// each call + records what the VM invoked. No real network.
    /// </summary>
    private sealed class FakeNexusAuthService : INexusAuthService
    {
        // Required by the interface; unused by the Integrations VM tests.
        public event EventHandler? AuthStateChanged
        {
            add { }
            remove { }
        }

        public NexusAuthState? CurrentState { get; set; }

        public NexusAuthResult NextOAuthResult { get; set; } =
            NexusAuthResult.Success("OAuthUser", isPremium: false);
        public NexusAuthResult NextApiKeyResult { get; set; } =
            NexusAuthResult.Success("ApiUser", isPremium: false);
        public Task<NexusAuthResult>? NextOAuthTask { get; set; }

        // After a successful OAuth/API-key login, the VM calls GetCurrentStateAsync
        // to resolve the verified name. Tests drive the resulting status line by
        // setting these.
        public NexusAuthState? NextStateAfterOAuth { get; set; }
        public NexusAuthState? NextStateAfterApiKey { get; set; }

        public int OAuthLoginCalls { get; private set; }
        public int ApiKeyLoginCalls { get; private set; }
        public int SignOutCalls { get; private set; }
        public int GetCurrentStateCalls { get; private set; }
        public string? LastApiKey { get; private set; }

        public Task<NexusAuthResult> LoginWithOAuthAsync(CancellationToken ct = default)
        {
            OAuthLoginCalls++;
            if (NextOAuthTask is not null)
            {
                // Chain: after the awaited task completes, the VM will call
                // GetCurrentStateAsync. Make that return the NextStateAfterOAuth.
                var capturedState = NextStateAfterOAuth;
                return NextOAuthTask.ContinueWith(t =>
                {
                    CurrentState = capturedState;
                    return t.Result;
                }, TaskScheduler.Default);
            }
            CurrentState = NextStateAfterOAuth;
            return Task.FromResult(NextOAuthResult);
        }

        public Task<NexusAuthResult> LoginWithApiKeyAsync(string apiKey, CancellationToken ct = default)
        {
            ApiKeyLoginCalls++;
            LastApiKey = apiKey;
            if (NextApiKeyResult.IsSuccess)
            {
                CurrentState = NextStateAfterApiKey;
            }
            return Task.FromResult(NextApiKeyResult);
        }

        public Task SignOutAsync(CancellationToken ct = default)
        {
            SignOutCalls++;
            CurrentState = null;
            return Task.CompletedTask;
        }

        public Task<NexusAuthState?> GetCurrentStateAsync(CancellationToken ct = default)
        {
            GetCurrentStateCalls++;
            return Task.FromResult(CurrentState);
        }
    }
}
