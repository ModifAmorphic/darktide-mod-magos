using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Modificus.Curator.Config;
using Modificus.Curator.General;
using Modificus.Curator.Integrations;
using Modificus.Curator.UI.Localization;
using Modificus.Curator.UI.Session;
using Microsoft.Extensions.Logging;

namespace Modificus.Curator.UI.ViewModels;

/// <summary>
/// The view model behind the Integrations modal
/// (<see cref="Views.IntegrationsWindow"/>). Nexus-only in v1: two clearly
/// alternative, visually separated blocks, "Sign in with Nexus" (OAuth) and
/// "Use an API key", only one of which is active at a time. The active method
/// is shown by a per-block Sign out button + the method-aware status line
/// ("Signed in as X (Premium) via Nexus login" vs "...via API key"). The
/// API-key field is masked by default, persisted across reopens (so the user
/// sees one is configured), revealed on a Show eye toggle, + re-validatable
/// without re-entering. Disabled (with a tooltip) while the game is running,
/// mirroring the profile-switch gate. GitHub stays config-file-only (no UI
/// section). Below the auth blocks, an "Update checks" sub-section holds the
/// periodic update-check toggle + interval, persisted live through
/// <see cref="IConfigLoader"/> (read-modify-save on each change).
/// </summary>
/// <remarks>
/// <para>
/// <b>Auth method is the user's explicit choice.</b> Clicking "Sign in with
/// Nexus" starts the OAuth loopback flow (<c>AuthMethod = OAuth</c>); pasting +
/// validating an API key sets <c>AuthMethod = ApiKey</c>; Sign out resets to
/// <c>None</c>. Switching methods clears the other method's credentials (handled
/// in <see cref="NexusAuthService"/>). One active method at a time, no
/// leftovers.</para>
/// <para>
/// <b>Status line is resolved server-side on open + after each action.</b>
/// When the dialog opens, <see cref="RefreshAsync"/> calls
/// <see cref="NexusAuthService.GetCurrentStateAsync"/> to resolve the current
/// display name + premium state (one network call). A failed verify (network or
/// stale credentials) yields a method-aware "signed in (unverified)" status; the
/// user can still sign out.</para>
/// <para>
/// <b>Masked API-key field.</b> When the configured method is <c>ApiKey</c>,
/// the field shows the persisted key masked (via <see cref="ApiKeyMaskChar"/>);
/// a Show eye toggle flips the mask char to <c>'\0'</c> (plain). The field is
/// bound two-way to <see cref="ApiKey"/>: the user can paste a new key (replacing
/// the displayed one) + click Validate to switch methods. The Validate button
/// always validates whatever <see cref="ApiKey"/> currently holds, so it
/// re-validates the existing masked key when the field has not been touched +
/// validates a freshly typed key otherwise.</para>
/// <para>
/// <b>Localization.</b> Every user-facing string resolves through
/// <see cref="LocalizationService"/>; the dialog's bound properties re-resolve on
/// a culture flip.</para>
/// </remarks>
public partial class IntegrationsViewModel : ObservableObject
{
    private readonly INexusAuthService _auth;
    private readonly LocalizationService _localization;
    private readonly IProfileSession _session;
    private readonly IConfigLoader _configLoader;
    private readonly ILogger<IntegrationsViewModel> _logger;

    public IntegrationsViewModel(
        INexusAuthService auth,
        LocalizationService localization,
        IProfileSession session,
        IConfigLoader configLoader,
        ILogger<IntegrationsViewModel> logger)
    {
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _isGameRunning = _session.IsRunning;
        _session.PropertyChanged += OnSessionPropertyChanged;
        _localization.PropertyChanged += OnCultureChanged;
    }

    // ---- state -----------------------------------------------------------

    /// <summary>
    /// Whether Darktide is currently running, mirrored LIVE from
    /// <see cref="IProfileSession.IsRunning"/>. Gates the auth controls (avoids
    /// credential changes mid-session).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(LoginWithOAuthCommand))]
    [NotifyCanExecuteChangedFor(nameof(ValidateApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleApiKeyRevealCommand))]
    private bool _isGameRunning;

    /// <summary>
    /// Whether the auth controls are interactive (game not running). The Integrations
    /// dialog mirrors the profile-switch gate: no credential changes mid-session.
    /// </summary>
    public bool IsEnabled => !IsGameRunning;

    /// <summary>
    /// The currently configured Nexus auth method (None / OAuth / ApiKey),
    /// mirrored from the auth state on every refresh. Drives the per-block
    /// active indicator (the Sign out button visibility) + the
    /// <see cref="IsOAuthActive"/> / <see cref="IsApiKeyActive"/> helpers the
    /// view binds visibility to.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOAuthActive))]
    [NotifyPropertyChangedFor(nameof(IsApiKeyActive))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    private NexusAuthMethod _activeMethod = NexusAuthMethod.None;

    /// <summary>Whether OAuth is the currently configured method (block-active
    /// indicator for the view).</summary>
    public bool IsOAuthActive => ActiveMethod == NexusAuthMethod.OAuth;

    /// <summary>Whether API key is the currently configured method (block-active
    /// indicator for the view).</summary>
    public bool IsApiKeyActive => ActiveMethod == NexusAuthMethod.ApiKey;

    /// <summary>
    /// The API key as the TextBox sees it. Two-way bound. When the configured
    /// method is <c>ApiKey</c>, <see cref="RefreshAsync"/> populates this with
    /// the persisted key (the field masks it via
    /// <see cref="ApiKeyMaskChar"/>); the user can paste a new key over it +
    /// click Validate to switch. When the method is <c>None</c> or <c>OAuth</c>,
    /// this is <see cref="string.Empty"/> (the field shows the placeholder).
    /// </summary>
    /// <remarks>
    /// Carries the real key in-process only; the masking is purely visual (the
    /// TextBox's PasswordChar). The Show toggle (<see cref="IsApiKeyRevealed"/>)
    /// flips the mask so the user can verify / copy the key.
    /// </remarks>
    [ObservableProperty]
    private string _apiKey = string.Empty;

    /// <summary>
    /// Whether the API-key field is in revealed (plain) mode. The Show eye
    /// toggle flips this; <see cref="ApiKeyMaskChar"/> recomputes on flip so
    /// the TextBox re-paints. Defaults to <c>false</c> (masked).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApiKeyMaskChar))]
    private bool _isApiKeyRevealed;

    /// <summary>
    /// The mask char the API-key TextBox binds to its <c>PasswordChar</c>.
    /// <c>'\0'</c> when revealed (no masking, plain text); <c>'\u2022'</c>
    /// (bullet) when masked. Recomputes on a
    /// <see cref="IsApiKeyRevealed"/> flip.
    /// </summary>
    public char ApiKeyMaskChar => IsApiKeyRevealed ? '\0' : '\u2022';

    /// <summary>
    /// The status line text, resolved through <see cref="LocalizationService"/>
    /// + carrying the active-method indicator ("via Nexus login" /
    /// "via API key"). Re-resolves on a culture change. Updated by
    /// <see cref="RefreshAsync"/>.
    /// </summary>
    [ObservableProperty]
    private string _statusLine = string.Empty;

    /// <summary>
    /// Whether a Nexus auth method is currently configured (OAuth or ApiKey).
    /// Drives the Sign-out button availability (sign-out only enabled when
    /// authenticated).
    /// </summary>
    [ObservableProperty]
    private bool _isAuthenticated;

    /// <summary>
    /// Whether the dialog is mid-flight on an OAuth login or API-key validate
    /// (both are async + hit the network). Disables the buttons + shows a
    /// "working" status while in flight so the user gets feedback that the click
    /// registered.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginWithOAuthCommand))]
    [NotifyCanExecuteChangedFor(nameof(ValidateApiKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleApiKeyRevealCommand))]
    private bool _isBusy;

    // ---- update-check settings -------------------------------------------

    /// <summary>
    /// Set while <see cref="LoadAutoUpdateSettings"/> populates the toggle +
    /// interval from the live config so the resulting property-change handlers
    /// do not write back (which would be a no-op round-trip on every dialog
    /// open). Cleared after the load completes; user-driven changes then persist
    /// through <see cref="OnAutoUpdateCheckEnabledChanged"/> /
    /// <see cref="OnAutoUpdateCheckIntervalMinutesChanged"/>.
    /// </summary>
    private bool _isLoadingAutoUpdate;

    /// <summary>
    /// Whether the periodic background update check runs while a profile is
    /// active. Loaded live from <c>NexusConfig.AutoUpdateCheckEnabled</c> on
    /// dialog open; persisted on each user change via read-modify-save. The
    /// toggle gates ONLY the periodic timer (profile-load + manual checks still
    /// run); the runner reads it live, so a change here takes effect without a
    /// restart.
    /// </summary>
    [ObservableProperty]
    private bool _autoUpdateCheckEnabled;

    /// <summary>
    /// The periodic update-check interval, in minutes, as the
    /// <c>NumericUpDown</c> sees it (decimal to match the control's Value type).
    /// Two-way bound; persisted on each user change via read-modify-save,
    /// clamped to [1, 1440] on write. Loaded from
    /// <c>NexusConfig.AutoUpdateCheckIntervalMinutes</c> on dialog open.
    /// </summary>
    [ObservableProperty]
    private decimal? _autoUpdateCheckIntervalMinutes;

    /// <summary>
    /// Persisted when the user flips <see cref="AutoUpdateCheckEnabled"/>.
    /// Skipped during the dialog-open load (guarded by
    /// <c>_isLoadingAutoUpdate</c>) so populating the field from config does not
    /// trigger a redundant write-back round-trip.
    /// </summary>
    partial void OnAutoUpdateCheckEnabledChanged(bool value) => SaveAutoUpdateSettings();

    /// <summary>
    /// Persisted when the user edits <see cref="AutoUpdateCheckIntervalMinutes"/>.
    /// Skipped during the dialog-open load (guarded by
    /// <c>_isLoadingAutoUpdate</c>).
    /// </summary>
    partial void OnAutoUpdateCheckIntervalMinutesChanged(decimal? value) => SaveAutoUpdateSettings();

    /// <summary>
    /// Read-modify-saves the toggle + interval into the live config so the
    /// runner picks them up on its next tick. Best-effort (the ConfigLoader
    /// swallows write failures); clamps the interval to [1, 1440] minutes + null
    /// defaults to 10. No-op while <c>_isLoadingAutoUpdate</c> is set.
    /// </summary>
    private void SaveAutoUpdateSettings()
    {
        if (_isLoadingAutoUpdate)
        {
            return;
        }

        var config = _configLoader.Load();
        config.Integrations.Nexus.AutoUpdateCheckEnabled = AutoUpdateCheckEnabled;
        config.Integrations.Nexus.AutoUpdateCheckIntervalMinutes =
            (int)Math.Clamp(AutoUpdateCheckIntervalMinutes ?? 10, 1, 1440);
        _configLoader.Save(config);
    }

    /// <summary>
    /// Loads the toggle + interval from the live config into the bound
    /// properties, suppressing the change-triggered save while populating.
    /// Called from <see cref="RefreshAsync"/> so the dialog reflects the
    /// persisted state on every open (a prior session may have changed it).
    /// </summary>
    private void LoadAutoUpdateSettings()
    {
        var nexus = _configLoader.Load().Integrations.Nexus;
        _isLoadingAutoUpdate = true;
        try
        {
            AutoUpdateCheckEnabled = nexus.AutoUpdateCheckEnabled;
            AutoUpdateCheckIntervalMinutes = nexus.AutoUpdateCheckIntervalMinutes;
        }
        finally
        {
            _isLoadingAutoUpdate = false;
        }
    }

    // ---- localized labels -------------------------------------------------

    public string WindowTitle => _localization["Integrations_Title"];
    public string NexusHeader => _localization["Integrations_NexusHeader"];
    public string LoginWithOAuthLabel => _localization["Integrations_LoginWithNexus"];
    public string ApiKeyLabel => _localization["Integrations_ApiKeyLabel"];
    public string ValidateLabel => _localization["Integrations_ValidateButton"];
    public string ApiKeyHelpLink => _localization["Integrations_ApiKeyHelpUrl"];
    public string ApiKeyHelpLabel => _localization["Integrations_ApiKeyHelp"];
    public string SignOutLabel => _localization["Integrations_SignOutButton"];
    public string DoneLabel => _localization["Integrations_DoneButton"];
    public string AutoUpdateHeader => _localization["Integrations_AutoUpdateHeader"];
    public string AutoUpdateEnabledLabel => _localization["Integrations_AutoUpdateEnabled"];
    public string AutoUpdateIntervalLabel => _localization["Integrations_AutoUpdateInterval"];
    // Null when the game isn't running so no tooltip is shown on the enabled
    // panel; the running-message appears only when the panel is actually
    // disabled by IsGameRunning (avoids a misleading "is running" tooltip on
    // hover when the game is not running).
    public string? TooltipRunning => IsGameRunning ? _localization["Integrations_RunningTooltip"] : null;
    public string ShowApiKeyTooltip => _localization["Integrations_ShowApiKeyTooltip"];
    public string HideApiKeyTooltip => _localization["Integrations_HideApiKeyTooltip"];

    // ---- commands --------------------------------------------------------

    /// <summary>
    /// Starts the Nexus OAuth loopback flow: opens the browser, awaits the
    /// callback, exchanges for tokens, fetches the user info. Updates the status
    /// line on success; surfaces a localized error inline on failure. Disabled
    /// while the game runs or another auth op is in flight.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartAuth))]
    private async Task LoginWithOAuth()
    {
        IsBusy = true;
        StatusLine = _localization["Integrations_StartingOAuth"];
        try
        {
            var result = await _auth.LoginWithOAuthAsync();
            await RefreshStatusAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nexus OAuth login threw.");
            StatusLine = _localization.Format("Integrations_ErrorFormat", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Validates the API key currently held in <see cref="ApiKey"/>. On success,
    /// sets <c>AuthMethod = ApiKey</c> + clears any OAuth tokens + updates the
    /// status line. On failure, surfaces the error inline + keeps the entered
    /// key so the user can correct it. The field always validates whatever it
    /// holds, so this works equally for re-validating an existing persisted key
    /// (when the field shows the masked key on dialog reopen) and for validating
    /// a freshly typed key.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartAuth))]
    private async Task ValidateApiKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusLine = _localization["Integrations_ApiKeyEmpty"];
            return;
        }

        IsBusy = true;
        StatusLine = _localization["Integrations_Validating"];
        try
        {
            var result = await _auth.LoginWithApiKeyAsync(ApiKey);
            await RefreshStatusAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nexus API-key validate threw.");
            StatusLine = _localization.Format("Integrations_ErrorFormat", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Flips <see cref="IsApiKeyRevealed"/> so <see cref="ApiKeyMaskChar"/>
    /// swaps between bullet + plain, re-painting the field. Disabled only while
    /// a login is in flight (the field is meaningless to toggle mid-network-op);
    /// the running gate is not enforced here because revealing a masked key
    /// while the game runs is a read-only op that changes no credentials.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleReveal))]
    private void ToggleApiKeyReveal()
    {
        IsApiKeyRevealed = !IsApiKeyRevealed;
        // Re-resolve the tooltip so the eye toggle's accessible name flips with
        // the state ("Show" vs "Hide").
        OnPropertyChanged(nameof(ShowApiKeyTooltip));
        OnPropertyChanged(nameof(HideApiKeyTooltip));
    }

    /// <summary>
    /// Signs out: clears the persisted OAuth tokens + API key + sets
    /// <c>AuthMethod = None</c>. Idempotent. Used by BOTH block Sign-out buttons;
    /// only one is visible at a time (the active block's).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOut()
    {
        IsBusy = true;
        try
        {
            await _auth.SignOutAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nexus sign-out threw.");
            StatusLine = _localization.Format("Integrations_ErrorFormat", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStartAuth() => !IsGameRunning && !IsBusy;
    private bool CanSignOut() => !IsGameRunning && !IsBusy && IsAuthenticated;
    private bool CanToggleReveal() => !IsBusy;

    // ---- live state -------------------------------------------------------

    /// <summary>
    /// Refreshes the status line + active-method indicator + masked-key field
    /// from the persisted auth state, and the update-check toggle + interval
    /// from the persisted config. Called on dialog open (after construction)
    /// + after each auth command. Hits the v1 API to resolve the display name +
    /// premium state.
    /// </summary>
    public async Task RefreshAsync()
    {
        var state = await _auth.GetCurrentStateAsync();
        ApplyState(state);
        LoadAutoUpdateSettings();
    }

    /// <summary>
    /// Refreshes the status line from an explicit auth result (the
    /// just-completed OAuth login or API-key validate), then re-resolves the
    /// server-side state for the verified name + premium flag.
    /// </summary>
    private async Task RefreshStatusAsync(NexusAuthResult result)
    {
        if (!result.IsSuccess)
        {
            // Surface the failure inline; do NOT re-resolve (the network just
            // failed, no point pinging again).
            StatusLine = _localization.Format("Integrations_ErrorFormat", result.ErrorMessage ?? string.Empty);
            return;
        }

        // Success: re-resolve the verified state. If the network fails here we
        // fall back to a method-aware signed-in state.
        var state = await _auth.GetCurrentStateAsync();
        ApplyState(state);
    }

    private void ApplyState(NexusAuthState? state)
    {
        ActiveMethod = state?.Method ?? NexusAuthMethod.None;
        IsAuthenticated = state is not null;
        // The API-key field reflects the persisted key when the method is
        // ApiKey (so the user sees one is configured, masked, + can re-validate
        // without re-entering); empty otherwise (placeholder visible). Clearing
        // the reveal flag on each apply keeps the field masked by default after
        // a method switch / sign-out.
        ApiKey = state is { Method: NexusAuthMethod.ApiKey, ApiKey: { } key }
            ? key
            : string.Empty;
        IsApiKeyRevealed = false;

        StatusLine = state switch
        {
            null => _localization["Integrations_StatusNotSignedIn"],

            { Method: NexusAuthMethod.OAuth, Name: { } name, IsPremium: true } =>
                _localization.Format("Integrations_StatusSignedInOAuthPremium", name),
            { Method: NexusAuthMethod.OAuth, Name: { } name } =>
                _localization.Format("Integrations_StatusSignedInOAuth", name),
            { Method: NexusAuthMethod.OAuth } =>
                _localization["Integrations_StatusSignedInOAuthUnverified"],

            { Method: NexusAuthMethod.ApiKey, Name: { } name, IsPremium: true } =>
                _localization.Format("Integrations_StatusSignedInApiKeyPremium", name),
            { Method: NexusAuthMethod.ApiKey, Name: { } name } =>
                _localization.Format("Integrations_StatusSignedInApiKey", name),
            { Method: NexusAuthMethod.ApiKey } =>
                _localization["Integrations_StatusSignedInApiKeyUnverified"],

            // Defensive: any other method falls back to the generic signed-in
            // line. Not expected in practice (None is null above; OAuth +
            // ApiKey are the only non-None values).
            _ => _localization["Integrations_StatusSignedInGeneric"],
        };
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IProfileSession.IsRunning))
        {
            IsGameRunning = _session.IsRunning;
        }
    }

    /// <summary>
    /// Re-resolves the localized strings (window title, labels, status line)
    /// when the UI culture flips so the dialog refreshes in-step with the rest
    /// of the UI on a language switch.
    /// </summary>
    private void OnCultureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizationService.Culture) or "Item[]"))
        {
            return;
        }

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(NexusHeader));
        OnPropertyChanged(nameof(LoginWithOAuthLabel));
        OnPropertyChanged(nameof(ApiKeyLabel));
        OnPropertyChanged(nameof(ValidateLabel));
        OnPropertyChanged(nameof(ApiKeyHelpLink));
        OnPropertyChanged(nameof(ApiKeyHelpLabel));
        OnPropertyChanged(nameof(SignOutLabel));
        OnPropertyChanged(nameof(DoneLabel));
        OnPropertyChanged(nameof(AutoUpdateHeader));
        OnPropertyChanged(nameof(AutoUpdateEnabledLabel));
        OnPropertyChanged(nameof(AutoUpdateIntervalLabel));
        OnPropertyChanged(nameof(TooltipRunning));
        OnPropertyChanged(nameof(ShowApiKeyTooltip));
        OnPropertyChanged(nameof(HideApiKeyTooltip));
        // The status line embeds a localized format; re-resolve it by re-applying
        // the current state. Fire-and-forget: a culture flip mid-flight is rare,
        // and the next state-resolve will pick up the new culture.
        _ = RefreshAsync();
    }

    /// <summary>
    /// Detaches the VM's subscriptions so the short-lived dialog VM is
    /// collectable after its window closes (the session + localization service
    /// are singletons that outlive the dialog).
    /// </summary>
    public void Detach()
    {
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _localization.PropertyChanged -= OnCultureChanged;
    }
}
