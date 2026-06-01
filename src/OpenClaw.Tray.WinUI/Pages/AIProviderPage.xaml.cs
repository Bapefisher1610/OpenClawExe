using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;

namespace OpenClawTray.Pages;

public sealed partial class AIProviderPage : Page
{
    private static App CurrentApp => (App)Application.Current;
    private const int OpenAICodexOAuthCallbackPort = 1455;

    private bool _busy;
    private CancellationTokenSource? _oauthCallbackCts;

    public AIProviderPage()
    {
        InitializeComponent();
        ProviderCombo.SelectionChanged += OnProviderChanged;
    }

    public void Initialize()
    {
        PopulateModelOptions();
        UpdateProviderPathText();
        ConnectionInfoBar.IsOpen = ResolveLocalDistroName() is null;
        _ = RefreshProviderStatusAsync();
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderPathText is null)
            return;

        UpdateProviderPathText();
        PopulateModelOptions();
        UpdateOpenAICodexOAuthVisibility();
    }

    private void OnSave(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnSaveAsync,
            new AppLogger(),
            nameof(OnSave));

    private async Task OnSaveAsync()
    {
        if (_busy) return;

        var distro = ResolveLocalDistroName();
        if (string.IsNullOrWhiteSpace(distro))
        {
            ShowStatus("No local gateway", "Connect to a local OpenClaw WSL gateway before saving provider credentials.", InfoBarSeverity.Warning);
            ConnectionInfoBar.IsOpen = true;
            return;
        }

        var keyName = SelectedKeyName();
        var apiKey = ApiKeyBox.Password?.Trim() ?? string.Empty;
        var agentId = string.IsNullOrWhiteSpace(AgentIdBox.Text) ? "main" : AgentIdBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowStatus("API key required", "Paste a provider API key before saving.", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        ShowStatus("Saving provider", $"Writing {keyName} for agent '{agentId}' and restarting the gateway.", InfoBarSeverity.Informational);

        try
        {
            var result = await SaveProviderKeyAsync(distro, keyName, apiKey, agentId, CancellationToken.None);
            if (result.ExitCode != 0)
            {
                ShowStatus("Save failed", FirstNonEmpty(result.StdErr, result.StdOut, "The gateway rejected the provider update."), InfoBarSeverity.Error);
                return;
            }

            ApiKeyBox.Password = string.Empty;
            await SaveSelectedModelAsync(distro, SelectedProviderId(), SelectedModelId(), CancellationToken.None);
            ShowStatus("Provider saved", "The gateway was restarted. Try sending a chat message again.", InfoBarSeverity.Success);
            await RefreshProviderStatusAsync();
        }
        catch (Exception ex)
        {
            ShowStatus("Save failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnRefreshStatus(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            RefreshProviderStatusAsync,
            new AppLogger(),
            nameof(OnRefreshStatus));

    private void OnOpenConnection(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("connection");

    private void OnOpenUsage(object sender, RoutedEventArgs e)
        => ((IAppCommands)CurrentApp).Navigate("usage");

    private void OnOpenAICodexOAuthLogin(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnOpenAICodexOAuthLoginAsync,
            new AppLogger(),
            nameof(OnOpenAICodexOAuthLogin));

    private async Task OnOpenAICodexOAuthLoginAsync()
    {
        if (_busy) return;

        _oauthCallbackCts?.Cancel();
        _oauthCallbackCts?.Dispose();
        _oauthCallbackCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        SetBusy(true);
        OpenAICodexOAuthStatusText.Text = "Starting OpenAI OAuth in the local gateway.";
        ShowStatus("OpenAI OAuth", "Opening OpenAI sign-in. Complete authentication in your browser.", InfoBarSeverity.Informational);

        try
        {
            var distro = ResolveLocalDistroName();
            if (string.IsNullOrWhiteSpace(distro))
            {
                ShowStatus("No local gateway", "Connect to a local OpenClaw WSL gateway before using OAuth.", InfoBarSeverity.Warning);
                return;
            }

            OpenAICodexOAuthLink.Visibility = Visibility.Collapsed;
            var profileId = await RunOpenAICodexOAuthLoginInWslAsync(distro, _oauthCallbackCts.Token);
            await SetActiveOpenAIProfileAsync(distro, profileId, SelectedModelId(), _oauthCallbackCts.Token);
            OpenAICodexOAuthStatusText.Text = $"OAuth profile saved: {profileId}";
            ShowStatus("OpenAI OAuth saved", "Token was saved to the agent auth store and selected for provider openai.", InfoBarSeverity.Success);
            await RefreshProviderStatusAsync();
        }
        catch (OperationCanceledException)
        {
            OpenAICodexOAuthStatusText.Text = "OpenAI OAuth timed out before the callback returned.";
            ShowStatus("OpenAI OAuth timed out", "No callback was received on localhost:1455 within 5 minutes.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            OpenAICodexOAuthStatusText.Text = $"OpenAI OAuth failed: {ex.Message}";
            ShowStatus("OpenAI OAuth failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnUseSelectedOAuthProfile(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnUseSelectedOAuthProfileAsync,
            new AppLogger(),
            nameof(OnUseSelectedOAuthProfile));

    private async Task OnUseSelectedOAuthProfileAsync()
    {
        if (_busy) return;

        var distro = ResolveLocalDistroName();
        if (string.IsNullOrWhiteSpace(distro))
            return;

        var profileId = (OAuthProfileCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            ShowStatus("No profile selected", "Select an OpenAI OAuth profile first.", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await SetActiveOpenAIProfileAsync(distro, profileId, SelectedModelId(), CancellationToken.None);
            if (result.ExitCode != 0)
            {
                ShowStatus("Profile switch failed", FirstNonEmpty(result.StdErr, result.StdOut, "Could not update openclaw.json."), InfoBarSeverity.Error);
                return;
            }

            ShowStatus("OpenAI profile selected", $"Active profile: {profileId}", InfoBarSeverity.Success);
            await RefreshProviderStatusAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshProviderStatusAsync()
    {
        var distro = ResolveLocalDistroName();
        ConnectionInfoBar.IsOpen = string.IsNullOrWhiteSpace(distro);
        if (string.IsNullOrWhiteSpace(distro))
        {
            ProviderStatusText.Text = "No local WSL gateway record is active.";
            return;
        }

        try
        {
            var result = await RunWslAsync(distro, BuildProviderStatusScript(), null, CancellationToken.None);
            ProviderStatusText.Text = string.IsNullOrWhiteSpace(result.StdOut)
                ? "No provider API keys were found in ~/.openclaw/.env."
                : result.StdOut.Trim();
            PopulateOAuthProfiles(result.StdOut);
        }
        catch (Exception ex)
        {
            ProviderStatusText.Text = $"Could not read provider status: {ex.Message}";
        }
    }

    private static string BuildProviderStatusScript() => """
        set -e
        if [ -f ~/.openclaw/.env ]; then
          grep -E '^(OPENAI_API_KEY|OPENROUTER_API_KEY|ANTHROPIC_API_KEY|GOOGLE_API_KEY)=' ~/.openclaw/.env | sed 's/=.*/=[SET]/' || true
        fi
        node <<'NODE'
        const fs = require('fs');
        const path = require('path');
        const home = process.env.HOME || '/home/openclaw';
        const authPath = path.join(home, '.openclaw/agents/main/agent/auth-profiles.json');
        const configPath = path.join(home, '.openclaw/openclaw.json');
        const read = (p, f) => { try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch { return f; } };
        const store = read(authPath, { profiles: {} });
        const cfg = read(configPath, {});
        const order = Array.isArray(cfg?.auth?.order?.openai) ? cfg.auth.order.openai : [];
        for (const [id, profile] of Object.entries(store.profiles || {})) {
          if (profile?.provider !== 'openai') continue;
          const label = profile.email || profile.displayName || profile.accountId || id;
          const active = order[0] === id ? '1' : '0';
          console.log(`OAUTH_PROFILE\t${id}\t${label}\t${active}`);
        }
        NODE
        """;

    private void PopulateOAuthProfiles(string output)
    {
        OAuthProfileCombo.Items.Clear();
        var activeIndex = -1;
        var count = 0;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("OAUTH_PROFILE\t", StringComparison.Ordinal))
                continue;

            var parts = line.Split('\t');
            if (parts.Length < 4)
                continue;

            var item = new ComboBoxItem
            {
                Content = parts[2],
                Tag = parts[1]
            };
            OAuthProfileCombo.Items.Add(item);
            if (parts[3] == "1")
                activeIndex = count;
            count++;
        }

        if (activeIndex >= 0)
            OAuthProfileCombo.SelectedIndex = activeIndex;
        else if (OAuthProfileCombo.Items.Count > 0)
            OAuthProfileCombo.SelectedIndex = 0;

        OAuthProfileStatusText.Text = count == 0
            ? "No OpenAI OAuth profile exists in ~/.openclaw/agents/main/agent/auth-profiles.json."
            : $"{count} OpenAI OAuth profile(s) found. Active account is first in auth.order.openai.";
    }

    private static async Task<CommandResult> SaveProviderKeyAsync(string distro, string keyName, string apiKey, string agentId, CancellationToken cancellationToken)
    {
        var script = """
            set -euo pipefail
            read -r key_name_b64
            read -r api_key_b64
            read -r agent_id_b64
            key_name="$(printf '%s' "$key_name_b64" | base64 -d)"
            api_key="$(printf '%s' "$api_key_b64" | base64 -d)"
            agent_id="$(printf '%s' "$agent_id_b64" | base64 -d)"

            case "$key_name" in
              OPENAI_API_KEY|OPENROUTER_API_KEY|ANTHROPIC_API_KEY|GOOGLE_API_KEY) ;;
              *) echo "Unsupported provider key: $key_name" >&2; exit 2 ;;
            esac

            case "$agent_id" in
              *[!A-Za-z0-9._-]*|'') echo "Invalid agent id: $agent_id" >&2; exit 2 ;;
            esac

            mkdir -p "$HOME/.openclaw"
            env_file="$HOME/.openclaw/.env"
            tmp_file="$(mktemp)"
            touch "$env_file"
            grep -v -E "^${key_name}=" "$env_file" > "$tmp_file" || true
            printf "%s=%q\n" "$key_name" "$api_key" >> "$tmp_file"
            chmod 600 "$tmp_file"
            mv "$tmp_file" "$env_file"

            if command -v openclaw >/dev/null 2>&1; then
              openclaw gateway restart >/tmp/openclaw-provider-restart.log 2>&1 || openclaw gateway start >/tmp/openclaw-provider-restart.log 2>&1 || true
            fi

            agent_dir="$HOME/.openclaw/agents/$agent_id/agent"
            if [ -d "$agent_dir" ]; then
              echo "Updated $key_name in ~/.openclaw/.env for agent '$agent_id'."
            else
              echo "Updated $key_name in ~/.openclaw/.env. Agent '$agent_id' does not have a local directory yet."
            fi
            """;

        var stdin = string.Join('\n',
            ToBase64(keyName),
            ToBase64(apiKey),
            ToBase64(agentId)) + "\n";

        return await RunWslAsync(distro, script, stdin, cancellationToken);
    }

    private static Task<CommandResult> SetActiveOpenAIProfileAsync(string distro, string profileId, string modelId, CancellationToken cancellationToken)
    {
        var script = """
            set -euo pipefail
            read -r profile_id_b64
            read -r model_id_b64
            profile_id="$(printf '%s' "$profile_id_b64" | base64 -d)"
            model_id="$(printf '%s' "$model_id_b64" | base64 -d)"
            node - "$profile_id" "$model_id" <<'NODE'
            const fs = require('fs');
            const path = require('path');
            const profileId = process.argv[2];
            const modelId = process.argv[3] || 'gpt-5.5';
            const home = process.env.HOME || '/home/openclaw';
            const authPath = path.join(home, '.openclaw/agents/main/agent/auth-profiles.json');
            const configPath = path.join(home, '.openclaw/openclaw.json');
            const read = (p, f) => { try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch { return f; } };
            const store = read(authPath, { profiles: {} });
            const profile = store.profiles?.[profileId];
            if (!profile || profile.provider !== 'openai') {
              console.error(`OpenAI OAuth profile not found: ${profileId}`);
              process.exit(2);
            }
            const cfg = read(configPath, {});
            cfg.auth = cfg.auth && typeof cfg.auth === 'object' ? cfg.auth : {};
            cfg.auth.order = cfg.auth.order && typeof cfg.auth.order === 'object' ? cfg.auth.order : {};
            const existing = Array.isArray(cfg.auth.order.openai) ? cfg.auth.order.openai : [];
            cfg.auth.order.openai = [profileId, ...existing.filter((id) => id !== profileId)];
            cfg.auth.profiles = cfg.auth.profiles && typeof cfg.auth.profiles === 'object' ? cfg.auth.profiles : {};
            cfg.auth.profiles[profileId] = {
              provider: 'openai',
              mode: 'oauth',
              ...(profile.email ? { email: profile.email } : {}),
              ...(profile.displayName ? { displayName: profile.displayName } : {})
            };
            cfg.agents = cfg.agents && typeof cfg.agents === 'object' ? cfg.agents : {};
            cfg.agents.defaults = cfg.agents.defaults && typeof cfg.agents.defaults === 'object' ? cfg.agents.defaults : {};
            cfg.agents.defaults.model = modelId;
            cfg.agents.defaults.models = cfg.agents.defaults.models && typeof cfg.agents.defaults.models === 'object' ? cfg.agents.defaults.models : {};
            for (const key of [modelId, `openai/${modelId}`]) {
              cfg.agents.defaults.models[key] = cfg.agents.defaults.models[key] || {};
              cfg.agents.defaults.models[key].provider = 'openai';
              cfg.agents.defaults.models[key].authProfile = profileId;
            }
            fs.mkdirSync(path.dirname(configPath), { recursive: true });
            fs.writeFileSync(configPath, JSON.stringify(cfg, null, 2) + '\n', { mode: 0o600 });
            console.log(`Active OpenAI OAuth profile: ${profileId}; model: ${modelId}`);
            NODE
            if command -v openclaw >/dev/null 2>&1; then
              openclaw gateway restart >/tmp/openclaw-provider-restart.log 2>&1 || openclaw gateway start >/tmp/openclaw-provider-restart.log 2>&1 || true
            fi
            """;

        var stdin = string.Join('\n', ToBase64(profileId), ToBase64(modelId)) + "\n";
        return RunWslAsync(distro, script, stdin, cancellationToken);
    }

    private static Task<CommandResult> SaveSelectedModelAsync(string distro, string providerId, string modelId, CancellationToken cancellationToken)
    {
        var script = """
            set -euo pipefail
            read -r provider_id_b64
            read -r model_id_b64
            provider_id="$(printf '%s' "$provider_id_b64" | base64 -d)"
            model_id="$(printf '%s' "$model_id_b64" | base64 -d)"
            node - "$provider_id" "$model_id" <<'NODE'
            const fs = require('fs');
            const path = require('path');
            const providerId = process.argv[2] || 'openai';
            const modelId = process.argv[3] || 'gpt-5.5';
            const configPath = path.join(process.env.HOME || '/home/openclaw', '.openclaw/openclaw.json');
            const read = (p, f) => { try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch { return f; } };
            const cfg = read(configPath, {});
            cfg.agents = cfg.agents && typeof cfg.agents === 'object' ? cfg.agents : {};
            cfg.agents.defaults = cfg.agents.defaults && typeof cfg.agents.defaults === 'object' ? cfg.agents.defaults : {};
            cfg.agents.defaults.model = modelId;
            cfg.agents.defaults.models = cfg.agents.defaults.models && typeof cfg.agents.defaults.models === 'object' ? cfg.agents.defaults.models : {};
            for (const key of [modelId, `${providerId}/${modelId}`]) {
              cfg.agents.defaults.models[key] = cfg.agents.defaults.models[key] || {};
              cfg.agents.defaults.models[key].provider = providerId;
            }
            fs.mkdirSync(path.dirname(configPath), { recursive: true });
            fs.writeFileSync(configPath, JSON.stringify(cfg, null, 2) + '\n', { mode: 0o600 });
            console.log(`Selected ${providerId} model: ${modelId}`);
            NODE
            if command -v openclaw >/dev/null 2>&1; then
              openclaw gateway restart >/tmp/openclaw-provider-restart.log 2>&1 || openclaw gateway start >/tmp/openclaw-provider-restart.log 2>&1 || true
            fi
            """;

        var stdin = string.Join('\n', ToBase64(providerId), ToBase64(modelId)) + "\n";
        return RunWslAsync(distro, script, stdin, cancellationToken);
    }

    private static async Task<string> RunOpenAICodexOAuthLoginInWslAsync(string distro, CancellationToken cancellationToken)
    {
        var script = BuildOpenAICodexOAuthWslScript();
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(distro);
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("openclaw");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start WSL OAuth helper.");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        string? savedProfileId = null;

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
                    await LaunchOAuthUriAsync(uri);
            }
            else if (line.StartsWith("OPENCLAW_OAUTH_SAVED ", StringComparison.Ordinal))
            {
                savedProfileId = line["OPENCLAW_OAUTH_SAVED ".Length..].Trim();
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                break;
            }
        }

        try { await process.WaitForExitAsync(cancellationToken); }
        catch (InvalidOperationException) { }
        var stderr = await stderrTask;
        if (string.IsNullOrWhiteSpace(savedProfileId))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "OpenAI OAuth did not save a profile." : stderr.Trim());

        return savedProfileId;
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
        const readJson = (file, fallback) => { try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return fallback; } };
        const writeJson = (file, value, mode) => {
          fs.mkdirSync(path.dirname(file), { recursive: true });
          const tmp = `${file}.tmp-${process.pid}`;
          fs.writeFileSync(tmp, JSON.stringify(value, null, 2) + '\n', { mode });
          fs.renameSync(tmp, file);
          if (mode) fs.chmodSync(file, mode);
        };
        const decodeJwtPayload = (token) => {
          try {
            const payload = token.split('.')[1];
            if (!payload) return {};
            const normalized = payload.replace(/-/g, '+').replace(/_/g, '/');
            const padded = normalized + '='.repeat((4 - normalized.length % 4) % 4);
            return JSON.parse(Buffer.from(padded, 'base64').toString('utf8'));
          } catch { return {}; }
        };
        const creds = await loginOpenAICodex({
          originator: 'openclaw',
          onAuth: async ({ url }) => console.log(`OPENCLAW_OAUTH_URL ${url}`),
          onPrompt: async () => { throw new Error('OpenAI OAuth callback was not received.'); },
          onProgress: () => {}
        });
        if (!creds?.access || !creds?.refresh || !Number.isFinite(creds.expires)) {
          throw new Error('OpenAI OAuth did not return complete credentials.');
        }
        const payload = decodeJwtPayload(creds.access);
        const authClaim = payload['https://api.openai.com/auth'] || {};
        const email = typeof payload.email === 'string' ? payload.email : undefined;
        const displayName = typeof payload.name === 'string' ? payload.name : undefined;
        const accountId = typeof creds.accountId === 'string' ? creds.accountId : typeof authClaim.chatgpt_account_id === 'string' ? authClaim.chatgpt_account_id : undefined;
        const profileSlug = String(email || accountId || 'default').toLowerCase().replace(/[^a-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '') || 'default';
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
        cfg.auth.profiles[profileId] = { provider, mode: 'oauth', ...(email ? { email } : {}), ...(displayName ? { displayName } : {}) };
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

        var jsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(js));
        return $$"""
        set -euo pipefail
        node_bin=/home/openclaw/.openclaw/tools/node/bin/node
        package_dir=/home/openclaw/.openclaw/tools/node-v22.22.0/lib/node_modules/openclaw
        if [ ! -x "$node_bin" ]; then echo "OpenClaw Node not found at $node_bin" >&2; exit 2; fi
        if [ ! -d "$package_dir" ]; then echo "OpenClaw package not found at $package_dir" >&2; exit 2; fi
        helper="$package_dir/.openai-oauth-helper.mjs"
        printf '%s' '{{jsBase64}}' | base64 -d > "$helper"
        cd "$package_dir"
        "$node_bin" "$helper"
        rm -f "$helper"
        """;
    }

    private static async Task<CommandResult> RunWslAsync(string distro, string script, string? stdin, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(distro);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start wsl.exe.");
        if (!string.IsNullOrEmpty(stdin))
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
        }
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private string? ResolveLocalDistroName()
    {
        if (Application.Current is not App app)
            return null;

        var active = app.Registry?.GetActive();
        if (!string.IsNullOrWhiteSpace(active?.SetupManagedDistroName))
            return active.SetupManagedDistroName;

        if (active?.IsLocal == true)
            return "OpenClawGateway";

        return null;
    }

    private string SelectedKeyName() =>
        (ProviderCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OPENAI_API_KEY";

    private string SelectedProviderId() => SelectedKeyName() switch
    {
        "OPENROUTER_API_KEY" => "openrouter",
        "ANTHROPIC_API_KEY" => "anthropic",
        "GOOGLE_API_KEY" => "google",
        _ => "openai"
    };

    private string SelectedModelId()
        => (ModelCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-5.5";

    private void PopulateModelOptions()
    {
        if (ModelCombo is null)
            return;

        var selected = SelectedModelId();
        ModelCombo.Items.Clear();

        var models = SelectedProviderId() switch
        {
            "openrouter" => new[] { ("openrouter/auto", "Auto"), ("anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5"), ("openai/gpt-5.5", "GPT-5.5") },
            "anthropic" => new[] { ("claude-opus-4.7", "Claude Opus 4.7"), ("claude-sonnet-4.5", "Claude Sonnet 4.5"), ("claude-haiku-4.5", "Claude Haiku 4.5") },
            "google" => new[] { ("gemini-3-pro", "Gemini 3 Pro"), ("gemini-2.5-pro", "Gemini 2.5 Pro"), ("gemini-2.5-flash", "Gemini 2.5 Flash") },
            _ => new[] { ("gpt-5.5", "GPT-5.5"), ("gpt-5.4", "GPT-5.4"), ("gpt-5-mini", "GPT-5 Mini") }
        };

        var selectedIndex = 0;
        for (var i = 0; i < models.Length; i++)
        {
            var (id, label) = models[i];
            ModelCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            if (string.Equals(id, selected, StringComparison.OrdinalIgnoreCase))
                selectedIndex = i;
        }

        ModelCombo.SelectedIndex = selectedIndex;
    }

    private void UpdateProviderPathText()
    {
        if (ProviderPathText is null)
            return;

        var keyName = SelectedKeyName();
        var modelId = SelectedModelId();
        var distro = ResolveLocalDistroName() ?? "OpenClawGateway";
        ProviderPathText.Text = $"Target: WSL distro '{distro}', variable {keyName}, model {modelId}. OAuth profiles are stored in ~/.openclaw/agents/main/agent/auth-profiles.json.";
    }

    private void UpdateOpenAICodexOAuthVisibility()
    {
        if (OpenAICodexOAuthPanel is null)
            return;

        OpenAICodexOAuthPanel.Visibility = SelectedKeyName() == "OPENAI_API_KEY"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy;
        ProviderCombo.IsEnabled = !busy;
        ApiKeyBox.IsEnabled = !busy;
        AgentIdBox.IsEnabled = !busy;
        ModelCombo.IsEnabled = !busy;
        OpenAICodexOAuthButton.IsEnabled = !busy;
        OAuthProfileCombo.IsEnabled = !busy;
    }

    private static async Task LaunchOAuthUriAsync(Uri uri)
    {
        if (await Launcher.LaunchUriAsync(uri))
            return;

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private static string ToBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

    private sealed record OpenAICodexOAuthSession(Uri AuthorizationUri, string State, string CodeVerifier);

    private sealed record OpenAICodexOAuthCallbackResult(string Code);

    private static OpenAICodexOAuthSession CreateOpenAICodexOAuthSession()
    {
        var state = CreateBase64UrlRandom(32);
        var codeVerifier = CreateBase64UrlRandom(32);
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
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

    private static async Task<OpenAICodexOAuthCallbackResult> WaitForOpenAICodexOAuthCallbackAsync(string expectedState, CancellationToken cancellationToken)
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
                var result = await HandleOpenAICodexOAuthCallbackClientAsync(client, expectedState, cancellationToken);
                if (result is not null)
                    return result;
            }
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static async Task<OpenAICodexOAuthCallbackResult?> HandleOpenAICodexOAuthCallbackClientAsync(TcpClient client, string expectedState, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
            return null;

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
        }

        var result = ParseOpenAICodexOAuthRequest(requestLine, expectedState, out var responseTitle, out var responseMessage);
        await WriteOAuthCallbackResponseAsync(stream, result is not null, responseTitle, responseMessage, cancellationToken);
        return result;
    }

    private static OpenAICodexOAuthCallbackResult? ParseOpenAICodexOAuthRequest(string requestLine, string expectedState, out string title, out string message)
    {
        title = "OpenAI OAuth failed";
        message = "The callback was not recognized by OpenClaw.";

        var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Uri.TryCreate("http://localhost" + parts[1], UriKind.Absolute, out var uri) ||
            !uri.AbsolutePath.Equals("/auth/callback", StringComparison.Ordinal))
            return null;

        var query = ParseQuery(uri.Query);
        if (query.TryGetValue("error", out var error))
        {
            message = WebUtility.HtmlEncode(error);
            return null;
        }

        if (!query.TryGetValue("state", out var state) ||
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

        title = "OpenAI OAuth authenticated";
        message = "You can return to OpenClaw. The authorization callback was received successfully.";
        return new OpenAICodexOAuthCallbackResult(code);
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
        var body = Encoding.UTF8.GetBytes(html);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}
