using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Connection;
using OpenClaw.Shared;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class WizardPage : Page
{
    private const int MaxWizardSteps = 50;
    private const int MaxSameStepVisits = 3;
    private const int OpenAICodexOAuthCallbackPort = 1455;
    private SetupConfig? _config;
    private OpenClawGatewayClient? _client;
    private string _sessionId = "";
    private string _stepId = "";
    private string _stepType = "";
    private bool _sensitive;
    private bool _errorState;
    private int _wizardStepCount;
    private readonly Dictionary<string, int> _stepVisits = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WizardOption> _options = [];
    private readonly HashSet<string> _autoLaunchedLoginUrls = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _oauthCallbackCts;

    public WizardPage()
    {
        InitializeComponent();
        TextInput.TextChanged += (_, _) => UpdateContinueState();
        SecretInput.PasswordChanged += (_, _) => UpdateContinueState();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        _ = StartWizardAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _oauthCallbackCts?.Cancel();
        _ = DisconnectAsync();
    }

    private async Task StartWizardAsync()
    {
        try
        {
            _errorState = false;
            await DisconnectAsync();
            _sessionId = "";
            _wizardStepCount = 0;
            _stepVisits.Clear();
            SetBusy("Connecting to gateway...");
            _client = await ConnectClientAsync();
            _client.StatusChanged += OnWizardClientStatusChanged;
            SetBusy("Starting wizard...");
            var payload = await _client.SendWizardRequestAsync("wizard.start", timeoutMs: 30_000);
            await ApplyPayloadAsync(payload);
        }
        catch (Exception ex)
        {
            await EnterWizardErrorAsync($"Gateway wizard failed: {ex.Message}");
        }
    }

    private async Task<OpenClawGatewayClient> ConnectClientAsync()
    {
        var config = _config!;
        var dataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var record = registry.GetActive() ?? throw new InvalidOperationException("No active gateway record found.");
        var identityPath = registry.GetIdentityDirectory(record.Id);
        var token = DeviceIdentity.TryReadStoredDeviceToken(identityPath)
            ?? record.SharedGatewayToken
            ?? record.BootstrapToken
            ?? throw new InvalidOperationException("No gateway credential found.");

        var client = new OpenClawGatewayClient(config.EffectiveGatewayUrl, token, logger: new UiGatewayLogger(), identityPath: identityPath)
        {
            UseV2Signature = true
        };

        var outcome = await WaitForConnectAsync(client, TimeSpan.FromSeconds(20));
        if (!outcome)
        {
            client.Dispose();
            throw new InvalidOperationException("Could not connect to the gateway.");
        }

        return client;
    }

    private static async Task<bool> WaitForConnectAsync(OpenClawGatewayClient client, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStatusChanged(object? sender, ConnectionStatus status)
        {
            if (status == ConnectionStatus.Connected)
                tcs.TrySetResult(true);
            else if (status is ConnectionStatus.Error or ConnectionStatus.Disconnected)
                tcs.TrySetResult(false);
        }

        client.StatusChanged += OnStatusChanged;
        try
        {
            await client.ConnectAsync();
            using var cts = new CancellationTokenSource(timeout);
            await using var _ = cts.Token.Register(() => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        finally
        {
            client.StatusChanged -= OnStatusChanged;
        }
    }

    private void OnWizardClientStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status is not (ConnectionStatus.Disconnected or ConnectionStatus.Error))
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_errorState
                || _client == null
                || !ReferenceEquals(sender, _client)
                || string.IsNullOrWhiteSpace(_sessionId))
            {
                return;
            }

            _ = EnterWizardErrorAsync("Gateway connection was lost while the wizard was running.");
        });
    }

    private async Task ApplyPayloadAsync(JsonElement payload)
    {
        if (payload.TryGetProperty("sessionId", out var sid))
            _sessionId = sid.GetString() ?? _sessionId;

        if (payload.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
        {
            var error = payload.TryGetProperty("error", out var err) ? err.ToString() : "";
            if (!string.IsNullOrWhiteSpace(error) && !error.Contains("this.prompt is not a function", StringComparison.OrdinalIgnoreCase))
            {
                ShowError(error);
                return;
            }

            await DisconnectAsync();
            if (_config!.SkipPermissions)
                App.MainWindow?.NavigateToComplete(true, TimeSpan.Zero, _config.LogPath, autoLaunchTray: true);
            else
                App.MainWindow?.NavigateToPermissions();
            return;
        }

        if (!payload.TryGetProperty("step", out var step))
        {
            ShowError("Gateway wizard returned an invalid response.");
            return;
        }

        _stepId = step.TryGetProperty("id", out var id) ? id.ToString() : "";
        _stepType = step.TryGetProperty("type", out var type) ? type.ToString() : "note";
        var stepIndex = payload.TryGetProperty("stepIndex", out var indexProperty) && indexProperty.TryGetInt32(out var index) ? index : 0;
        _sensitive = step.TryGetProperty("sensitive", out var sensitive) && sensitive.ValueKind == JsonValueKind.True;
        var title = step.TryGetProperty("title", out var titleProp) ? titleProp.ToString() : "";
        var message = step.TryGetProperty("message", out var msgProp) ? msgProp.ToString() : "";
        var initial = step.TryGetProperty("initialValue", out var initialProp) ? initialProp : default;

        if (string.IsNullOrWhiteSpace(_stepId))
        {
            ShowError("Gateway wizard step is missing an id.");
            return;
        }

        _wizardStepCount++;
        if (_wizardStepCount > MaxWizardSteps)
        {
            ShowError($"Gateway wizard exceeded {MaxWizardSteps} steps.");
            return;
        }

        var visitKey = $"{_stepId}:{stepIndex}";
        _stepVisits.TryGetValue(visitKey, out var visits);
        _stepVisits[visitKey] = visits + 1;
        if (_stepVisits[visitKey] > MaxSameStepVisits)
        {
            ShowError($"Gateway wizard repeated step '{_stepId}' too many times.");
            return;
        }

        ResetInputs();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? DisplayTitleFor(_stepType) : title;
        RenderMessage(message);
        StepCard.MinHeight = _stepType == "note" && string.IsNullOrWhiteSpace(message) ? 140 : 260;
        ErrorText.Visibility = Visibility.Collapsed;
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        StatusText.Text = "Answer the gateway setup question";
        PrimaryButton.IsEnabled = !WizardSelection.RequiresAnswer(_stepType);
        SecondaryButton.IsEnabled = true;
        PrimaryButton.Content = _stepType == "confirm" ? "Yes" : "Continue";
        SecondaryButton.Content = "No";
        SecondaryButton.Visibility = _stepType == "confirm" ? Visibility.Visible : Visibility.Collapsed;

        if (!BuildOptions(step, initial))
            return;

        if (_stepType == "text")
        {
            if (_sensitive)
            {
                SecretInput.Visibility = Visibility.Visible;
                SecretInput.Password = initial.ValueKind == JsonValueKind.String ? initial.GetString() ?? "" : "";
            }
            else
            {
                TextInput.Visibility = Visibility.Visible;
                TextInput.Text = initial.ValueKind == JsonValueKind.String ? initial.GetString() ?? "" : "";
            }

            UpdateContinueState();
        }

        if (_stepType == "note")
        {
            SecondaryButton.IsEnabled = false;
            SecondaryButton.Visibility = Visibility.Collapsed;
        }

        AutoLaunchLoginUrlIfPresent(payload, title, message, initial);
    }

    private bool BuildOptions(JsonElement step, JsonElement initial)
    {
        if (_stepType is not ("select" or "multiselect"))
            return true;

        _options.Clear();
        if (step.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                var value = option.ValueKind == JsonValueKind.Object && option.TryGetProperty("value", out var valueProp)
                    ? valueProp.ToString()
                    : option.ToString();
                var label = option.ValueKind == JsonValueKind.Object && option.TryGetProperty("label", out var labelProp)
                    ? labelProp.ToString()
                    : value;
                var hint = option.ValueKind == JsonValueKind.Object && option.TryGetProperty("hint", out var hintProp)
                    ? hintProp.ToString()
                    : "";
                _options.Add(new(value, label, hint));
            }
        }

        if (!WizardSelection.HasSelectableOptions(_stepType, _options.Select(o => o.Value).ToArray()))
        {
            ShowError("Gateway wizard returned a choice step without any selectable options.");
            return false;
        }

        if (_stepType == "select")
        {
            SelectOptions.Visibility = Visibility.Visible;
            foreach (var option in _options)
            {
                SelectOptions.Children.Add(new RadioButton
                {
                    Content = BuildOptionContent(option),
                    Tag = option.Value,
                    GroupName = $"wizard-step-{_stepId}",
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                });
            }

            var initialValue = initial.ValueKind == JsonValueKind.String ? initial.GetString() : null;
            var index = WizardSelection.SelectedIndex(initialValue, _options.Select(o => o.Value).ToArray());
            if (index >= 0 && index < SelectOptions.Children.Count && SelectOptions.Children[index] is RadioButton radio)
                radio.IsChecked = true;

            foreach (var optionRadio in SelectOptions.Children.OfType<RadioButton>())
                optionRadio.Checked += (_, _) => UpdateContinueState();

            UpdateContinueState();
        }
        else
        {
            MultiOptions.Visibility = Visibility.Visible;
            var initialValues = initial.ValueKind == JsonValueKind.Array
                ? initial.EnumerateArray().Select(v => v.ToString()).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var option in _options)
            {
                var checkBox = new CheckBox
                {
                    Content = BuildOptionContent(option),
                    Tag = option.Value,
                    IsChecked = initialValues.Contains(option.Value),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                checkBox.Checked += (_, _) => UpdateContinueState();
                checkBox.Unchecked += (_, _) => UpdateContinueState();
                MultiOptions.Children.Add(checkBox);
            }

            UpdateContinueState();
        }

        return true;
    }

    private static FrameworkElement BuildOptionContent(WizardOption option)
    {
        var panel = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(2, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        panel.Children.Add(new TextBlock
        {
            Text = option.Label,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(option.Hint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = option.Hint,
                FontSize = 12,
                Foreground = ResourceBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None
            });
        }

        return panel;
    }

    private static Brush ResourceBrush(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out var brush)
            && brush is Brush typedBrush
            ? typedBrush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private void Primary_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            PrimaryClickAsync,
            NullLogger.Instance,
            nameof(Primary_Click));

    private async Task PrimaryClickAsync()
    {
        if (_errorState)
        {
            await StartWizardAsync();
            return;
        }

        await SendCurrentAnswerAsync(skip: false);
    }

    private void Secondary_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            SecondaryClickAsync,
            NullLogger.Instance,
            nameof(Secondary_Click));

    private async Task SecondaryClickAsync()
    {
        if (_errorState)
        {
            await SkipWizardAsync();
            return;
        }

        await SendCurrentAnswerAsync(skip: true);
    }

    private async Task SendCurrentAnswerAsync(bool skip)
    {
        if (_client == null) return;

        try
        {
            object? answerValue = null;
            if (!skip && !TryBuildAnswerValue(out answerValue))
            {
                ErrorText.Text = _stepType == "multiselect"
                    ? "Choose at least one valid option."
                    : _stepType == "text"
                    ? "Enter a value to continue."
                    : "Choose a valid option.";
                ErrorText.Visibility = Visibility.Visible;
                UpdateContinueState();
                return;
            }

            await SendAnswerValueAsync(answerValue, skip, skip ? "Skipping..." : "Submitting...");
        }
        catch (Exception ex)
        {
            await EnterWizardErrorAsync(ex.Message);
        }
    }

    private async Task SendAnswerValueAsync(object? answerValue, bool skip, string busyText)
    {
        if (_client == null) return;

        SetBusy(busyText);
        object parameters;
        if (skip)
        {
            parameters = _stepType == "confirm"
                ? new { sessionId = _sessionId, answer = new { stepId = _stepId, value = false } }
                : new { sessionId = _sessionId };
        }
        else
        {
            parameters = new { sessionId = _sessionId, answer = new { stepId = _stepId, value = answerValue } };
        }

        var payload = await _client.SendWizardRequestAsync("wizard.next", parameters, timeoutMs: TimeoutForCurrentStep());
        await ApplyPayloadAsync(payload);
    }

    private bool TryBuildAnswerValue(out object value)
    {
        value = _stepType switch
        {
            "confirm" => true,
            "select" => SelectOptions.Children.OfType<RadioButton>()
                .FirstOrDefault(r => r.IsChecked == true)
                ?.Tag?.ToString() ?? "",
            "multiselect" => MultiOptions.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag?.ToString() ?? "")
                .Where(v => v.Length > 0)
                .ToArray(),
            "text" => _sensitive ? SecretInput.Password : TextInput.Text,
            _ => "true"
        };

        if (!WizardSelection.RequiresAnswer(_stepType))
            return true;

        if (_stepType == "text")
            return !WizardSelection.ShouldDisableContinue(_stepType, value?.ToString());

        return !WizardSelection.ShouldDisableContinue(_stepType, GetSelectedOptionValues(), _options.Select(o => o.Value).ToArray());
    }

    private string[] GetSelectedOptionValues()
    {
        return _stepType switch
        {
            "select" => SelectOptions.Children.OfType<RadioButton>()
                .Where(r => r.IsChecked == true)
                .Select(r => r.Tag?.ToString() ?? "")
                .Where(v => v.Length > 0)
                .ToArray(),
            "multiselect" => MultiOptions.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag?.ToString() ?? "")
                .Where(v => v.Length > 0)
                .ToArray(),
            _ => []
        };
    }

    private void UpdateContinueState()
    {
        if (_errorState || !WizardSelection.RequiresAnswer(_stepType))
            return;

        PrimaryButton.IsEnabled = _stepType == "text"
            ? !WizardSelection.ShouldDisableContinue(_stepType, _sensitive ? SecretInput.Password : TextInput.Text)
            : !WizardSelection.ShouldDisableContinue(
                _stepType,
                GetSelectedOptionValues(),
                _options.Select(o => o.Value).ToArray());

        if (PrimaryButton.IsEnabled)
            ErrorText.Visibility = Visibility.Collapsed;
    }

    private int TimeoutForCurrentStep()
    {
        var text = $"{TitleText.Text} {string.Join(' ', MessagePanel.Children.OfType<TextBlock>().Select(t => t.Text))}";
        return text.Contains("device", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authorize", StringComparison.OrdinalIgnoreCase)
            || text.Contains("login", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("oauth", StringComparison.OrdinalIgnoreCase)
            ? 300_000
            : 30_000;
    }

    private void ResetInputs()
    {
        _oauthCallbackCts?.Cancel();
        _oauthCallbackCts?.Dispose();
        _oauthCallbackCts = null;
        SelectOptions.Children.Clear();
        SelectOptions.Visibility = Visibility.Collapsed;
        MultiOptions.Children.Clear();
        MultiOptions.Visibility = Visibility.Collapsed;
        TextInput.Visibility = Visibility.Collapsed;
        SecretInput.Visibility = Visibility.Collapsed;
        MessagePanel.Children.Clear();
    }

    private void AutoLaunchLoginUrlIfPresent(JsonElement payload, string title, string message, JsonElement initial)
    {
        var promptText = $"{_stepId} {_stepType} {title} {message} {JsonElementString(initial)}";
        var urls = ExtractHttpUrls(payload).DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToArray();

        var hasOpenAIAuthUrl = urls.Any(IsOpenAIAuthUri);
        if (urls.Length == 0 && LooksLikeOpenAICodexOAuthPrompt(promptText))
        {
            StartOpenAICodexOAuthWslLoginOnce();
            return;
        }

        if (urls.Length == 0 || !LooksLikeLoginPrompt(promptText, urls))
            return;

        var uri = urls.FirstOrDefault(IsOpenAIAuthUri) ?? urls.FirstOrDefault(IsLoginUrl) ?? urls[0];
        OpenAICodexOAuthSession? oauthSession = null;
        if (hasOpenAIAuthUrl || IsOpenAIAuthUri(uri))
        {
            oauthSession = CreateOpenAICodexOAuthSessionFromAuthorizationUri(uri);
            uri = oauthSession.AuthorizationUri;
        }

        AddLoginLinkIfMissing(message, uri);
        if (oauthSession is not null)
            LaunchLoginUriOnce(oauthSession.AuthorizationUri);
        else
            LaunchLoginUriOnce(uri);
    }

    private void AddLoginLinkIfMissing(string message, Uri uri)
    {
        if (MessageContainsUrl(message, uri.AbsoluteUri))
            return;

        var linkLabel = IsOpenAIAuthUri(uri)
            ? "Open OpenAI OAuth: "
            : "Open sign-in page: ";
        MessagePanel.Children.Add(BuildLinkLine(linkLabel + uri.AbsoluteUri, uri.AbsoluteUri, uri));
    }

    private void StartOpenAICodexOAuthFlowOnce(OpenAICodexOAuthSession session)
    {
        var launchKey = $"{_sessionId}:{_stepId}:{session.AuthorizationUri.AbsoluteUri}";
        if (!_autoLaunchedLoginUrls.Add(launchKey))
            return;

        TextInput.Visibility = Visibility.Collapsed;
        SecretInput.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = false;
        SecondaryButton.Visibility = Visibility.Collapsed;
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        TitleText.Text = "OpenAI OAuth";
        StatusText.Text = "Opening OpenAI OAuth...";

        _oauthCallbackCts?.Cancel();
        _oauthCallbackCts?.Dispose();
        _oauthCallbackCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _ = CompleteOpenAICodexOAuthAsync(session, _oauthCallbackCts.Token);
    }

    private void StartOpenAICodexOAuthWslLoginOnce()
    {
        var launchKey = $"{_sessionId}:{_stepId}:openai-codex-wsl-oauth";
        if (!_autoLaunchedLoginUrls.Add(launchKey))
            return;

        TextInput.Visibility = Visibility.Collapsed;
        SecretInput.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = false;
        SecondaryButton.Visibility = Visibility.Collapsed;
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        TitleText.Text = "OpenAI OAuth";
        StatusText.Text = "Starting OpenAI OAuth in WSL...";

        _oauthCallbackCts?.Cancel();
        _oauthCallbackCts?.Dispose();
        _oauthCallbackCts = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        _ = CompleteOpenAICodexOAuthViaWslAsync(_oauthCallbackCts.Token);
    }

    private async Task CompleteOpenAICodexOAuthViaWslAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CancelCurrentWizardSessionAsync();
            var distro = ResolveLocalDistroName();
            if (string.IsNullOrWhiteSpace(distro))
                throw new InvalidOperationException("No local OpenClaw WSL gateway distro was found.");

            StatusText.Text = "Opening OpenAI OAuth...";
            await RunOpenAICodexOAuthLoginInWslAsync(distro, cancellationToken);

            StatusText.Text = "OpenAI OAuth saved. Opening OpenClaw...";
            await DisconnectAsync();
            App.MainWindow?.NavigateToComplete(true, TimeSpan.Zero, _config?.LogPath, autoLaunchTray: true);
        }
        catch (OperationCanceledException)
        {
            ShowError("OpenAI OAuth timed out. Retry setup to open the OAuth page again.");
        }
        catch (Exception ex)
        {
            ShowError($"OpenAI OAuth failed: {ex.Message}");
        }
    }

    private async Task CompleteOpenAICodexOAuthAsync(OpenAICodexOAuthSession session, CancellationToken cancellationToken)
    {
        try
        {
            var callbackTask = WaitForOpenAICodexOAuthCallbackAsync(session.ExpectedState, session.CodeVerifier, cancellationToken);
            LaunchExternalUri(session.AuthorizationUri);
            StatusText.Text = "Waiting for OpenAI OAuth callback...";

            var result = await callbackTask;
            StatusText.Text = "OpenAI OAuth successful";
            await SendAnswerValueAsync(result.RedirectUrl, skip: false, busyText: "Recording OpenAI token...");
        }
        catch (OperationCanceledException)
        {
            ShowError("OpenAI OAuth timed out. Retry setup to open the OAuth page again.");
        }
        catch (Exception ex)
        {
            ShowError($"OpenAI OAuth failed: {ex.Message}");
        }
    }

    private void LaunchLoginUriOnce(Uri uri)
    {
        if (IsOpenAIAuthUri(uri))
        {
            StartOpenAICodexOAuthFlowOnce(CreateOpenAICodexOAuthSessionFromAuthorizationUri(uri));
            return;
        }

        var launchKey = $"{_sessionId}:{_stepId}:{uri.AbsoluteUri}";
        if (!_autoLaunchedLoginUrls.Add(launchKey))
            return;

        try
        {
            LaunchExternalUri(uri);
            StatusText.Text = IsOpenAIAuthUri(uri)
                ? "Opened official OpenAI login"
                : "Opened browser for gateway login";
        }
        catch
        {
            StatusText.Text = "Open the sign-in link to continue";
        }
    }

    private static bool LooksLikeLoginPrompt(string text, IEnumerable<Uri> urls)
    {
        if (text.Contains("oauth", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authorize", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authorization code", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("login", StringComparison.OrdinalIgnoreCase))
            return true;

        return urls.Any(IsLoginUrl);
    }

    private static bool LooksLikeOpenAICodexOAuthPrompt(string text)
    {
        return text.Contains("openai", StringComparison.OrdinalIgnoreCase)
            && text.Contains("codex", StringComparison.OrdinalIgnoreCase)
            && text.Contains("oauth", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoginUrl(Uri uri)
    {
        var value = uri.AbsoluteUri;
        return value.Contains("oauth", StringComparison.OrdinalIgnoreCase)
            || value.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || value.Contains("authorize", StringComparison.OrdinalIgnoreCase)
            || value.Contains("login", StringComparison.OrdinalIgnoreCase)
            || value.Contains("signin", StringComparison.OrdinalIgnoreCase)
            || value.Contains("sign-in", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenAICodexLoginUri(Uri uri)
        => string.Equals(uri.Host, "auth.openai.com", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.Equals("/oauth/authorize", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenAIAuthUri(Uri uri)
        => string.Equals(uri.Host, "auth.openai.com", StringComparison.OrdinalIgnoreCase);

    private static void LaunchExternalUri(Uri uri)
        => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    private static OpenAICodexOAuthSession CreateOpenAICodexOAuthSession()
    {
        var state = CreateBase64UrlRandom(32);
        var codeVerifier = CreateBase64UrlRandom(32);
        var codeChallenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(codeVerifier)));
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = "app_EMoamEEZ73f0CkXaXp7hrann",
            ["redirect_uri"] = $"http://localhost:{OpenAICodexOAuthCallbackPort}/auth/callback",
            ["scope"] = "openid profile email offline_access api.connectors.read api.connectors.invoke",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = "codex_vscode"
        };

        var builder = new UriBuilder("https://auth.openai.com/oauth/authorize")
        {
            Query = string.Join('&', query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        };

        return new OpenAICodexOAuthSession(builder.Uri, state, codeVerifier);
    }

    private static OpenAICodexOAuthSession CreateOpenAICodexOAuthSessionFromAuthorizationUri(Uri authorizationUri)
    {
        var query = ParseQuery(authorizationUri.Query);
        query.TryGetValue("state", out var state);
        return new OpenAICodexOAuthSession(authorizationUri, state, null);
    }

    private static string CreateBase64UrlRandom(int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool MessageContainsUrl(string message, string url) =>
        !string.IsNullOrWhiteSpace(message) && message.Contains(url, StringComparison.OrdinalIgnoreCase);

    private static string JsonElementString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";

    private static IEnumerable<Uri> ExtractHttpUrls(JsonElement element)
    {
        foreach (var value in EnumerateStringValues(element))
        {
            foreach (Match match in Regex.Matches(value, @"https?://[^\s\)\""<>'`]+", RegexOptions.IgnoreCase))
            {
                var candidate = match.Value.TrimEnd('.', ',', ';', ':');
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    yield return uri;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateStringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nested in EnumerateStringValues(property.Value))
                        yield return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in EnumerateStringValues(item))
                        yield return nested;
                }
                break;
        }
    }

    private void RenderMessage(string message)
    {
        MessagePanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(message))
            return;

        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var codeMatch = Regex.Match(trimmed, @"^((?:Code|code|user_code|USER_CODE)\s*[:=]\s*)([A-Z0-9]{2,8}(?:-[A-Z0-9]{2,8})+|[A-Z0-9]{4,12})\b");
            if (codeMatch.Success)
            {
                MessagePanel.Children.Add(BuildCodeRow(codeMatch.Groups[1].Value, codeMatch.Groups[2].Value));
                continue;
            }

            var urlMatch = Regex.Match(trimmed, @"https?://[^\s\)\""]+", RegexOptions.IgnoreCase);
            if (urlMatch.Success && Uri.TryCreate(urlMatch.Value.TrimEnd('.', ','), UriKind.Absolute, out var uri))
            {
                if (IsOpenAIAuthUri(uri))
                {
                    MessagePanel.Children.Add(new TextBlock
                    {
                        Text = trimmed.Replace(urlMatch.Value, "OpenAI OAuth opens automatically in your browser.", StringComparison.OrdinalIgnoreCase),
                        FontSize = 14,
                        Opacity = 0.82,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    });
                    continue;
                }

                MessagePanel.Children.Add(BuildLinkLine(trimmed, urlMatch.Value, uri));
                continue;
            }

            MessagePanel.Children.Add(new TextBlock
            {
                Text = trimmed,
                FontSize = 14,
                Opacity = 0.82,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
        }
    }

    private static FrameworkElement BuildLinkLine(string line, string urlText, Uri uri)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var prefix = line[..line.IndexOf(urlText, StringComparison.Ordinal)];
        if (!string.IsNullOrEmpty(prefix))
            panel.Children.Add(new TextBlock { Text = prefix, FontSize = 14, Opacity = 0.82, VerticalAlignment = VerticalAlignment.Center });

        var button = new HyperlinkButton
        {
            Content = urlText,
            NavigateUri = uri,
            Padding = new Thickness(0),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(button);

        var suffix = line[(line.IndexOf(urlText, StringComparison.Ordinal) + urlText.Length)..];
        if (!string.IsNullOrEmpty(suffix))
            panel.Children.Add(new TextBlock { Text = suffix, FontSize = 14, Opacity = 0.82, VerticalAlignment = VerticalAlignment.Center });

        return panel;
    }

    private static FrameworkElement BuildCodeRow(string prefix, string code)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            ColumnSpacing = 10
        };

        var label = new TextBlock { Text = prefix, FontSize = 14, Opacity = 0.82, VerticalAlignment = VerticalAlignment.Center };
        var codeText = new TextBlock
        {
            Text = code,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        var copy = new Button { Content = "Copy", Padding = new Thickness(8, 4, 8, 4) };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(code);
            Clipboard.SetContent(package);
        };

        Grid.SetColumn(label, 0);
        Grid.SetColumn(codeText, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(label);
        grid.Children.Add(codeText);
        grid.Children.Add(copy);
        return grid;
    }

    private static async Task<OpenAICodexOAuthCallbackResult> WaitForOpenAICodexOAuthCallbackAsync(string? expectedState, string? codeVerifier, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.IPv6Any, OpenAICodexOAuthCallbackPort);
        listener.Server.DualMode = true;
        listener.Start(1);

        using var registration = cancellationToken.Register(() => listener.Stop());
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                var result = await HandleOpenAICodexOAuthCallbackClientAsync(client, expectedState, codeVerifier, cancellationToken);
                if (result is not null)
                    return result;
            }
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static async Task<OpenAICodexOAuthCallbackResult?> HandleOpenAICodexOAuthCallbackClientAsync(TcpClient client, string? expectedState, string? codeVerifier, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
            return null;

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
        }

        var result = ParseOpenAICodexOAuthRequest(requestLine, expectedState, codeVerifier, out var responseTitle, out var responseMessage);
        if (result is not null)
            await WriteOAuthSuccessResponseAsync(stream, result.SuccessUrl, cancellationToken);
        else
            await WriteOAuthCallbackResponseAsync(stream, success: false, responseTitle, responseMessage, cancellationToken);
        return result;
    }

    private static OpenAICodexOAuthCallbackResult? ParseOpenAICodexOAuthRequest(string requestLine, string? expectedState, string? codeVerifier, out string title, out string message)
    {
        title = "OpenAI OAuth failed";
        message = "The callback was not recognized by OpenClaw.";

        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Uri.TryCreate("http://localhost" + parts[1], UriKind.Absolute, out var uri))
            return null;

        var query = ParseQuery(uri.Query);
        if (uri.AbsolutePath.Equals("/success", StringComparison.Ordinal) &&
            query.TryGetValue("id_token", out var idToken) &&
            !string.IsNullOrWhiteSpace(idToken))
        {
            title = "OpenAI OAuth successful";
            message = "OAuth successful. You can return to OpenClaw Setup.";
            var successRedirectUrl = $"http://localhost:{OpenAICodexOAuthCallbackPort}{uri.PathAndQuery}";
            return new OpenAICodexOAuthCallbackResult(idToken, successRedirectUrl, successRedirectUrl);
        }

        if (uri.AbsolutePath.Equals("/auth/success", StringComparison.Ordinal))
        {
            title = "OpenAI OAuth successful";
            message = "OAuth successful. You can return to OpenClaw Setup.";
            return null;
        }

        if (!uri.AbsolutePath.Equals("/auth/callback", StringComparison.Ordinal))
            return null;

        if (query.TryGetValue("error", out var error))
        {
            message = error;
            return null;
        }

        if (!query.TryGetValue("state", out var state) || string.IsNullOrWhiteSpace(state))
        {
            message = "OpenAI did not return an OAuth state.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(expectedState) &&
            !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            message = "The OAuth state did not match the login request.";
            return null;
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            message = "OpenAI did not return an authorization code.";
            return null;
        }

        title = "OpenAI OAuth successful";
        message = "OAuth successful. You can return to OpenClaw Setup.";
        var redirectUrl = $"http://localhost:{OpenAICodexOAuthCallbackPort}{uri.PathAndQuery}";
        if (!string.IsNullOrWhiteSpace(codeVerifier))
            redirectUrl = AppendQueryValue(redirectUrl, "code_verifier", codeVerifier);
        var successUrl = BuildOpenAICodexOAuthSuccessUrl(code, state, codeVerifier, redirectUrl);
        return new OpenAICodexOAuthCallbackResult(code, redirectUrl, successUrl);
    }

    private static string BuildOpenAICodexOAuthSuccessUrl(string code, string state, string? codeVerifier, string redirectUrl)
    {
        var values = new List<string>
        {
            "token=" + Uri.EscapeDataString(code),
            "code=" + Uri.EscapeDataString(code),
            "state=" + Uri.EscapeDataString(state),
            "redirect_uri=" + Uri.EscapeDataString(redirectUrl)
        };
        if (!string.IsNullOrWhiteSpace(codeVerifier))
            values.Insert(2, "code_verifier=" + Uri.EscapeDataString(codeVerifier));
        var query = string.Join('&', values);
        return $"http://localhost:{OpenAICodexOAuthCallbackPort}/auth/success?{query}";
    }

    private static string AppendQueryValue(string url, string key, string value)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            var rawKey = index >= 0 ? pair[..index] : pair;
            var rawValue = index >= 0 ? pair[(index + 1)..] : string.Empty;
            values[WebUtility.UrlDecode(rawKey)] = WebUtility.UrlDecode(rawValue);
        }

        return values;
    }

    private static async Task WriteOAuthCallbackResponseAsync(Stream stream, bool success, string title, string message, CancellationToken cancellationToken)
    {
        var status = success ? "200 OK" : "400 Bad Request";
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>{{WebUtility.HtmlEncode(title)}}</title>
              <style>
                body { font-family: Segoe UI, system-ui, sans-serif; margin: 48px; color: #1f1f1f; }
                main { max-width: 640px; }
                h1 { font-size: 24px; font-weight: 600; }
                p { font-size: 15px; line-height: 1.5; }
              </style>
            </head>
            <body>
              <main>
                <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                <p>{{WebUtility.HtmlEncode(message)}}</p>
              </main>
            </body>
            </html>
            """;
        var body = System.Text.Encoding.UTF8.GetBytes(html);
        var headers = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static async Task WriteOAuthSuccessResponseAsync(Stream stream, string successUrl, CancellationToken cancellationToken)
    {
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <title>OpenAI OAuth successful</title>
              <script>
                history.replaceState(null, "", "{{successUrl}}");
              </script>
              <style>
                body { font-family: Segoe UI, system-ui, sans-serif; margin: 48px; color: #1f1f1f; }
                main { max-width: 640px; }
                h1 { font-size: 24px; font-weight: 600; }
                p { font-size: 15px; line-height: 1.5; }
                code { word-break: break-all; }
              </style>
            </head>
            <body>
              <main>
                <h1>OpenAI OAuth successful</h1>
                <p>Sign in successful. You can return to OpenClaw Setup.</p>
                <p><code>{{WebUtility.HtmlEncode(successUrl)}}</code></p>
              </main>
            </body>
            </html>
            """;
        var body = System.Text.Encoding.UTF8.GetBytes(html);
        var headers = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private void SetBusy(string status)
    {
        StatusText.Text = status;
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = false;
    }

    private void ShowError(string message)
    {
        _errorState = true;
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        StatusText.Text = "Wizard needs attention";
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        PrimaryButton.Content = "Start wizard again";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Content = "Skip wizard";
        SecondaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Visible;
    }

    private async Task EnterWizardErrorAsync(string detail)
    {
        if (_errorState)
            return;

        _errorState = true;
        await DisconnectAsync();
        ShowError(detail);
    }

    private async Task SkipWizardAsync()
    {
        if (_client != null && !string.IsNullOrWhiteSpace(_sessionId))
        {
            try { await _client.SendWizardRequestAsync("wizard.cancel", new { sessionId = _sessionId }, timeoutMs: 10_000); }
            catch { }
        }

        await DisconnectAsync();
        if (_config!.SkipPermissions)
            App.MainWindow?.NavigateToComplete(true, TimeSpan.Zero, _config.LogPath, autoLaunchTray: true);
        else
            App.MainWindow?.NavigateToPermissions();
    }

    private async Task CancelCurrentWizardSessionAsync()
    {
        if (_client != null && !string.IsNullOrWhiteSpace(_sessionId))
        {
            try { await _client.SendWizardRequestAsync("wizard.cancel", new { sessionId = _sessionId }, timeoutMs: 10_000); }
            catch { }
        }

        await DisconnectAsync();
        _sessionId = "";
    }

    private async Task DisconnectAsync()
    {
        var client = _client;
        if (client == null) return;
        _client = null;
        client.StatusChanged -= OnWizardClientStatusChanged;
        try { await client.DisconnectAsync(); } catch { }
        client.Dispose();
    }

    private static string? ResolveLocalDistroName()
    {
        var dataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenClawTray");
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var active = registry.GetActive();
        if (!string.IsNullOrWhiteSpace(active?.SetupManagedDistroName))
            return active.SetupManagedDistroName;
        if (active?.IsLocal == true)
            return "OpenClawGateway";
        return null;
    }

    private async Task RunOpenAICodexOAuthLoginInWslAsync(string distro, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(distro);
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("openclaw");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(BuildOpenAICodexOAuthWslScript());

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start WSL OAuth helper.");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var openedUrl = false;
        var tokenSaved = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.StartsWith("OPENCLAW_OAUTH_URL ", StringComparison.Ordinal))
            {
                var rawUrl = line["OPENCLAW_OAUTH_URL ".Length..].Trim();
                if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
                {
                    openedUrl = true;
                    LaunchExternalUri(uri);
                    StatusText.Text = "Waiting for OpenAI OAuth callback...";
                }
            }
            else if (line.StartsWith("OPENCLAW_OAUTH_SAVED ", StringComparison.Ordinal))
            {
                tokenSaved = true;
                StatusText.Text = "OpenAI OAuth token saved.";
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                break;
            }
        }

        try { await process.WaitForExitAsync(cancellationToken); }
        catch (InvalidOperationException) { }
        var stderr = await stderrTask;
        if (!tokenSaved && process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? "WSL OAuth helper failed." : stderr.Trim();
            throw new InvalidOperationException(detail);
        }
        if (!openedUrl && !tokenSaved)
            throw new InvalidOperationException("WSL OAuth helper did not return an OpenAI login URL.");
    }

    private static string BuildOpenAICodexOAuthWslScript()
    {
        var js = """
        import fs from 'node:fs';
        import path from 'node:path';
        import { loginOpenAICodex } from '@earendil-works/pi-ai/oauth';

        const stateDir = process.env.OPENCLAW_HOME || '/home/openclaw/.openclaw';
        const agentDir = path.join(stateDir, 'agents', 'main', 'agent');
        const authPath = path.join(agentDir, 'auth-profiles.json');
        const configPath = path.join(stateDir, 'openclaw.json');
        const provider = 'openai';

        function decodeJwtPayload(token) {
          try {
            const payload = token.split('.')[1];
            if (!payload) return {};
            const normalized = payload.replace(/-/g, '+').replace(/_/g, '/');
            const padded = normalized + '='.repeat((4 - normalized.length % 4) % 4);
            return JSON.parse(Buffer.from(padded, 'base64').toString('utf8'));
          } catch {
            return {};
          }
        }

        function readJson(file, fallback) {
          try {
            return JSON.parse(fs.readFileSync(file, 'utf8'));
          } catch {
            return fallback;
          }
        }

        function writeJson(file, value, mode) {
          fs.mkdirSync(path.dirname(file), { recursive: true });
          const tmp = `${file}.tmp-${process.pid}`;
          fs.writeFileSync(tmp, JSON.stringify(value, null, 2) + '\n', { mode });
          fs.renameSync(tmp, file);
          if (mode) fs.chmodSync(file, mode);
        }

        const creds = await loginOpenAICodex({
          originator: 'openclaw',
          onAuth: async ({ url }) => {
            console.log(`OPENCLAW_OAUTH_URL ${url}`);
          },
          onPrompt: async () => {
            throw new Error('OpenAI OAuth callback was not received.');
          },
          onProgress: () => {}
        });

        if (!creds?.access || !creds?.refresh || !Number.isFinite(creds.expires)) {
          throw new Error('OpenAI OAuth did not return complete credentials.');
        }

        const payload = decodeJwtPayload(creds.access);
        const authClaim = payload['https://api.openai.com/auth'] || {};
        const email = typeof payload.email === 'string' ? payload.email : undefined;
        const displayName = typeof payload.name === 'string' ? payload.name : undefined;
        const accountId = typeof creds.accountId === 'string'
          ? creds.accountId
          : typeof authClaim.chatgpt_account_id === 'string'
            ? authClaim.chatgpt_account_id
            : undefined;
        const profileSlug = String(email || accountId || 'default')
          .toLowerCase()
          .replace(/[^a-z0-9._-]+/g, '-')
          .replace(/^-+|-+$/g, '') || 'default';
        const profileId = `openai:${profileSlug}`;

        const store = readJson(authPath, { version: 1, profiles: {} });
        store.version = 1;
        store.profiles = store.profiles && typeof store.profiles === 'object' ? store.profiles : {};
        store.profiles[profileId] = {
          type: 'oauth',
          provider,
          access: creds.access,
          refresh: creds.refresh,
          expires: creds.expires,
          ...(accountId ? { accountId } : {}),
          ...(email ? { email } : {}),
          ...(displayName ? { displayName } : {})
        };
        writeJson(authPath, store, 0o600);

        const cfg = readJson(configPath, {});
        cfg.auth = cfg.auth && typeof cfg.auth === 'object' ? cfg.auth : {};
        cfg.auth.profiles = cfg.auth.profiles && typeof cfg.auth.profiles === 'object' ? cfg.auth.profiles : {};
        cfg.auth.profiles[profileId] = {
          provider,
          mode: 'oauth',
          ...(email ? { email } : {}),
          ...(displayName ? { displayName } : {})
        };
        cfg.auth.order = cfg.auth.order && typeof cfg.auth.order === 'object' ? cfg.auth.order : {};
        const existingOrder = Array.isArray(cfg.auth.order[provider]) ? cfg.auth.order[provider] : [];
        cfg.auth.order[provider] = [profileId, ...existingOrder.filter((id) => id !== profileId)];
        cfg.agents = cfg.agents && typeof cfg.agents === 'object' ? cfg.agents : {};
        cfg.agents.defaults = cfg.agents.defaults && typeof cfg.agents.defaults === 'object' ? cfg.agents.defaults : {};
        cfg.agents.defaults.models = cfg.agents.defaults.models && typeof cfg.agents.defaults.models === 'object' ? cfg.agents.defaults.models : {};
        cfg.agents.defaults.models['openai/gpt-5.5'] = cfg.agents.defaults.models['openai/gpt-5.5'] || {};
        cfg.agents.defaults.models['openai/gpt-5.5'].authProfile = profileId;
        writeJson(configPath, cfg, 0o600);

        console.log(`OPENCLAW_OAUTH_SAVED ${profileId}`);
        process.exit(0);
        """;

        var jsBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(js));
        return $$"""
        set -euo pipefail
        if [ ! -x /home/openclaw/.openclaw/tools/node/bin/node ]; then
          echo "OpenClaw Node not found at /home/openclaw/.openclaw/tools/node/bin/node in distro ${WSL_DISTRO_NAME:-unknown}" >&2
          exit 2
        fi
        if [ ! -d /home/openclaw/.openclaw/tools/node-v22.22.0/lib/node_modules/openclaw ]; then
          echo "OpenClaw package not found at /home/openclaw/.openclaw/tools/node-v22.22.0/lib/node_modules/openclaw in distro ${WSL_DISTRO_NAME:-unknown}" >&2
          exit 2
        fi
        rm -f /home/openclaw/.openclaw/tools/node-v22.22.0/lib/node_modules/openclaw/.openai-codex-oauth-helper.mjs
        printf '%s' '{{jsBase64}}' | base64 -d > /home/openclaw/.openclaw/tools/node-v22.22.0/lib/node_modules/openclaw/.openai-codex-oauth-helper.mjs
        cd /home/openclaw/.openclaw/tools/node-v22.22.0/lib/node_modules/openclaw
        /home/openclaw/.openclaw/tools/node/bin/node /home/openclaw/.openclaw/tools/node-v22.22.0/lib/node_modules/openclaw/.openai-codex-oauth-helper.mjs
        rm -f /home/openclaw/.openclaw/tools/node-v22.22.0/lib/node_modules/openclaw/.openai-codex-oauth-helper.mjs
        """;
    }

    private static string DisplayTitleFor(string stepType) => stepType switch
    {
        "confirm" => "Confirm",
        "select" => "Choose an option",
        "multiselect" => "Choose options",
        "text" => "Enter value",
        _ => "Setup"
    };

    private sealed record WizardOption(string Value, string Label, string Hint);

    private sealed record OpenAICodexOAuthSession(Uri AuthorizationUri, string? ExpectedState, string? CodeVerifier);

    private sealed record OpenAICodexOAuthCallbackResult(string Code, string RedirectUrl, string SuccessUrl);

    private sealed class UiGatewayLogger : IOpenClawLogger
    {
        public void Info(string message) { }
        public void Debug(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
