using System.Diagnostics;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

internal static class Program
{
    private const string AliyunCredentialTarget = "CodexDictation.Aliyun.ApiKey";
    private const string VolcengineCredentialTarget = "CodexDictation.Volcengine.ApiKey";
    internal const string TitleCredentialTarget = "CodexDictation.Title.ApiKey";
    private const string LocalNetworkOptOut = "--disable-features=LocalNetworkAccessChecks,LocalNetworkAccessChecksWebSockets";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConditionalWeakTable<ClientWebSocket, CdpInbox> CdpInboxes = new();
    private static readonly ConcurrentDictionary<string, byte> VoiceWatchers = new(StringComparer.Ordinal);
    private static readonly Regex TitleCallRegex = new(
        @"await (?<native>(?<services>[$A-Za-z_][$\w]*)\.threadMetadataGeneration\?\.generateTitle\(\{hostId:(?<store>[$A-Za-z_][$\w]*)\.getHostId\(\),prompt:(?<prompt>[$A-Za-z_][$\w]*\([$A-Za-z_][$\w]*\)),cwd:(?<cwd>[$A-Za-z_][$\w]*),readOnlyAppToolAllowlist:(?<allowlist>[$A-Za-z_][$\w]*),\.\.\.(?<conversation>[$A-Za-z_][$\w]*)\.serviceName===void 0\?\{\}:\{serviceName:\k<conversation>\.serviceName\}\}\))",
        RegexOptions.CultureInvariant);

    private sealed class CdpInbox
    {
        internal Queue<JsonNode> Messages { get; set; } = new();
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--self-test"]) return await SelfTestAsync();
            if (args.Length != 0) throw new ArgumentException("Usage: CodexDictation [--self-test]");
            HideConsole();
            await RunAsync();
            return 0;
        }
        catch (Exception error)
        {
            Log(error.ToString());
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Codex Dictation requires Windows.");
        var existingHelpers = GetHelperProcesses();
        var existingCodex = GetCodexProcesses();
        if ((existingHelpers.Length > 0 || existingCodex.Length > 0) && !ConfirmRestartCodex(existingCodex.Length, existingHelpers.Length))
        {
            Log("User declined the restart confirmation; helper startup canceled.");
            return;
        }
        if (existingCodex.Length > 0) await CloseExistingCodexAsync(existingCodex);
        if (existingHelpers.Length > 0) await CloseExistingHelpersAsync(existingHelpers);
        using var instance = new Mutex(true, @"Local\CodexDictation", out var ownsMutex);
        if (!ownsMutex) throw new InvalidOperationException("Codex Dictation is already running.");
        var token = "codex-dictation." + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapMethods("/config", ["OPTIONS"], context =>
        {
            AddCorsHeaders(context.Response);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        app.MapGet("/config", async context =>
        {
            AddCorsHeaders(context.Response);
            if (!IsConfigRequestAuthorized(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var config = LoadConfigOrNull();
            var provider = config?.Provider ?? "aliyun";
            var hasApiKey = CredentialStore.Read(CredentialTarget(provider)) is not null;
            await context.Response.WriteAsJsonAsync(new
            {
                provider,
                workspaceId = config?.WorkspaceId ?? "",
                volcResourceId = config?.VolcResourceId ?? "volc.seedasr.sauc.duration",
                model = provider == "volcengine" ? "豆包大模型双向流式" : "qwen3-asr-flash-realtime",
                language = config?.Language ?? "zh",
                dictionary = config?.Dictionary ?? Array.Empty<string>(),
                hasApiKey,
                ready = config is not null && hasApiKey,
                title = new
                {
                    mode = config?.TitleMode ?? "off",
                    baseUrl = config?.TitleBaseUrl ?? "",
                    model = config?.TitleModel ?? "",
                    wireApi = config?.TitleWireApi ?? "chat",
                    available = TitleGenerator.IsAvailable(config),
                    hasApiKey = CredentialStore.Read(TitleCredentialTarget) is not null
                }
            });
        });
        app.MapPost("/config", async context =>
        {
            AddCorsHeaders(context.Response);
            if (!IsConfigRequestAuthorized(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (context.Request.ContentLength is > 65_536)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                if (Encoding.UTF8.GetByteCount(body) > 65_536)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
                var request = JsonSerializer.Deserialize<ConfigRequest>(body, JsonOptions)
                    ?? throw new InvalidDataException("Invalid configuration request.");
                var current = LoadConfigOrNull();
                var provider = NormalizeProvider(request.Provider ?? current?.Provider);
                var workspaceId = request.WorkspaceId?.Trim() ?? current?.WorkspaceId ?? "";
                var volcResourceId = request.VolcResourceId?.Trim();
                var asrRequest = request.Provider is not null || request.ApiKey is not null || request.WorkspaceId is not null || request.VolcResourceId is not null || request.Dictionary is not null;
                if (asrRequest)
                {
                    if (provider == "aliyun") ValidateWorkspaceId(workspaceId);
                    else ValidateVolcResourceId(volcResourceId ?? current?.VolcResourceId ?? "volc.seedasr.sauc.duration");
                }
                var apiKey = request.ApiKey?.Trim() ?? "";
                var credentialTarget = CredentialTarget(provider);
                if (apiKey.Length > 0)
                {
                    if (apiKey.Length is < 8 or > 1024) throw new InvalidDataException("API key length is invalid.");
                    CredentialStore.Write(credentialTarget, apiKey);
                }
                else if (asrRequest && CredentialStore.Read(credentialTarget) is null) throw new InvalidDataException("API key is required.");
                var dictionary = (request.Dictionary ?? current?.Dictionary ?? Array.Empty<string>())
                    .Select(item => item.Trim())
                    .Where(item => item.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Take(200)
                    .ToArray();
                var titleMode = NormalizeTitleMode(request.TitleMode ?? current?.TitleMode);
                var titleWireApi = NormalizeTitleWireApi(request.TitleWireApi ?? current?.TitleWireApi);
                var titleBaseUrl = (request.TitleBaseUrl ?? current?.TitleBaseUrl ?? "").Trim();
                var titleModel = (request.TitleModel ?? current?.TitleModel ?? "").Trim();
                if (titleBaseUrl.Length > 0 && (!Uri.TryCreate(titleBaseUrl, UriKind.Absolute, out var titleEndpoint) || titleEndpoint.Scheme is not ("http" or "https")))
                    throw new InvalidDataException("Title endpoint must be an absolute http(s) URL.");
                if (titleMode == "custom" && (titleBaseUrl.Length == 0 || titleModel.Length == 0))
                    throw new InvalidDataException("Custom title mode requires an endpoint and a model.");
                var titleApiKey = request.TitleApiKey?.Trim() ?? "";
                if (titleApiKey.Length > 0)
                {
                    if (titleApiKey.Length is < 8 or > 1024) throw new InvalidDataException("Title API key length is invalid.");
                    CredentialStore.Write(TitleCredentialTarget, titleApiKey);
                }
                SaveConfig(new Config(workspaceId, current?.Language ?? "zh", dictionary, provider,
                    volcResourceId ?? current?.VolcResourceId ?? "volc.seedasr.sauc.duration",
                    titleMode, titleBaseUrl, titleModel, titleWireApi));
                await context.Response.WriteAsJsonAsync(new { ready = true });
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = error.Message });
            }
        });
        app.MapMethods("/title", ["OPTIONS"], context =>
        {
            AddCorsHeaders(context.Response);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        app.MapPost("/title", async context =>
        {
            AddCorsHeaders(context.Response);
            if (!IsConfigRequestAuthorized(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            try
            {
                var request = JsonSerializer.Deserialize<TitleRequest>(await ReadBodyAsync(context, 65_536), JsonOptions)
                    ?? throw new InvalidDataException("Invalid title request.");
                var prompt = request.Prompt?.Trim() ?? "";
                if (prompt.Length is < 1 or > 32_768) throw new InvalidDataException("Title prompt is invalid.");
                var config = LoadConfigOrNull();
                if (config is null || config.TitleMode == "off") throw new InvalidDataException("Title routing is disabled.");
                var result = await TitleGenerator.GenerateAsync(config, prompt, request.Model, request.Provider, context.RequestAborted);
                if (result is null)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "Title provider is unavailable." });
                    return;
                }
                await context.Response.WriteAsJsonAsync(result);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException or InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                Log($"Title generation failed: {error.Message}");
                context.Response.StatusCode = error is InvalidDataException ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(new { error = error.Message });
            }
        });
        app.Map("/dictation", async context =>
        {
            Log($"Dictation WebSocket request: ws={context.WebSockets.IsWebSocketRequest}, protocols={context.Request.Headers.SecWebSocketProtocol}");
            if (!context.WebSockets.IsWebSocketRequest || !HasProtocol(context, "chatgpt-dictation") || !HasProtocol(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var config = LoadConfigOrNull();
            var apiKey = config is null ? null : CredentialStore.Read(CredentialTarget(config.Provider));
            if (config is null || apiKey is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync("chatgpt-dictation");
            Log($"Dictation WebSocket accepted; provider={config.Provider}.");
            await new DictationSession(socket, config, apiKey).RunAsync(context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
            ?? throw new InvalidOperationException("Local dictation listener did not start.");
        var localPort = new Uri(address).Port;
        Log($"Helper listening on http://127.0.0.1:{localPort}.");
        var debugPort = FreeLoopbackPort();
        ActivateCodex(debugPort);
        var injectionTask = InjectLoopAsync(debugPort, localPort, token, app.Lifetime.ApplicationStopping);
        await WaitForCodexExitAsync(app.Lifetime.ApplicationStopping);
        app.Lifetime.StopApplication();
        try { await injectionTask; } catch (OperationCanceledException) { }
        await app.StopAsync();
    }

    private static bool HasProtocol(HttpContext context, string expected) =>
        context.Request.Headers.SecWebSocketProtocol.ToString().Split(',').Any(item => item.Trim() == expected);

    private static async Task InjectLoopAsync(int debugPort, int helperPort, string token, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        var script = LoadInjectionScript(helperPort, token);
        var sawCodexTarget = false;
        var injectedTargets = new HashSet<string>(StringComparer.Ordinal);
        var bundlePatchedTargets = new HashSet<string>(StringComparer.Ordinal);
        var misses = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var targets = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/list", cancellationToken))?.AsArray();
                var codexTargets = (targets ?? []).Where(IsCodexTarget).ToArray();
                var target = codexTargets.FirstOrDefault(item => !IsAvatarOverlay(item)) ?? codexTargets.FirstOrDefault();
                var wsUrl = target?["webSocketDebuggerUrl"]?.GetValue<string>();
                if (wsUrl is not null)
                {
                    foreach (var codexTarget in codexTargets)
                    {
                        var targetWs = codexTarget?["webSocketDebuggerUrl"]?.GetValue<string>();
                        if (targetWs is null || bundlePatchedTargets.Contains(targetWs)) continue;
                        try
                        {
                            if (IsAvatarOverlay(codexTarget)) await PatchGlobalDictationBundleAndReloadAsync(targetWs, cancellationToken);
                            else await PatchDictationBundleAndReloadAsync(targetWs, cancellationToken);
                            bundlePatchedTargets.Add(targetWs);
                            injectedTargets.Remove(targetWs);
                            continue;
                        }
                        catch (Exception error)
                        {
                            Log($"Bundle patch skipped; keeping Codex running: {error.Message}");
                            bundlePatchedTargets.Add(targetWs);
                        }
                    }
                    foreach (var codexTarget in codexTargets)
                    {
                        var targetWs = codexTarget?["webSocketDebuggerUrl"]?.GetValue<string>();
                        if (targetWs is null || !injectedTargets.Add(targetWs)) continue;
                        await InjectRuntimeAsync(targetWs, script, cancellationToken);
                        Log($"CDP injection attached to {codexTarget?["url"]?.GetValue<string>() ?? "Codex page"}.");
                    }
                    sawCodexTarget = true;
                    misses = 0;
                }
                else misses++;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                misses++;
                if (injectedTargets.Count == 0)
                {
                    Log($"CDP injection skipped; Codex will continue unmodified: {error.Message}");
                }
                Log($"CDP retry: {error.Message}");
            }
            if (sawCodexTarget && misses >= 5)
            {
                misses = 0;
                Log("CDP target temporarily unavailable; keeping helper alive.");
            }
            if (!sawCodexTarget && misses >= 15)
            {
                misses = 0;
                Log("Codex CDP target is not ready; continuing to retry.");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private static async Task WaitForCodexExitAsync(CancellationToken cancellationToken)
    {
        var seenCodex = false;
        var launchDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        DateTime? missingSince = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var running = CodexIsRunning();
            if (running)
            {
                seenCodex = true;
                missingSince = null;
            }
            else if (missingSince is null)
            {
                missingSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - missingSince.Value >= TimeSpan.FromSeconds(60) && (seenCodex || DateTime.UtcNow >= launchDeadline))
            {
                Log("Codex processes absent for 60 seconds; stopping helper.");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static bool CodexIsRunning() => GetCodexProcesses().Length > 0;

    private static Process[] GetCodexProcesses() => Process.GetProcessesByName("ChatGPT");

    private static Process[] GetHelperProcesses() =>
        Process.GetProcesses().Where(process => process.Id != Environment.ProcessId && process.ProcessName.StartsWith("CodexDictation", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static bool ConfirmRestartCodex(int processCount, int helperCount)
    {
        const uint MbYesNo = 0x00000004;
        const uint MbIconQuestion = 0x00000020;
        const uint MbDefaultButton2 = 0x00000100;
        var isChinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        var title = isChinese ? "CodexDictation" : "CodexDictation";
        var message = isChinese
            ? $"检测到 {processCount} 个 ChatGPT 进程和 {helperCount} 个听写助手进程。\n\n是否关闭现有进程并重新启动，以注入听写补丁？\n\n选择“否”将退出，不会修改现有进程。"
            : $"Detected {processCount} ChatGPT process(es) and {helperCount} dictation helper process(es).\n\nClose them and restart with the dictation patch?\n\nChoose No to exit without changing existing processes.";
        return MessageBox(IntPtr.Zero, message, title, MbYesNo | MbIconQuestion | MbDefaultButton2) == 6;
    }

    private static async Task CloseExistingHelpersAsync(Process[] processes)
    {
        foreach (var process in processes)
        {
            try { if (!process.HasExited) process.CloseMainWindow(); } catch { }
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline && processes.Any(IsRunning)) await Task.Delay(100);
        foreach (var process in processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        while (processes.Any(IsRunning)) await Task.Delay(100);
    }

    private static async Task CloseExistingCodexAsync(Process[] processes)
    {
        if (processes.Length == 0) return;
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) process.CloseMainWindow();
            }
            catch { }
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && processes.Any(IsRunning)) await Task.Delay(250);
        if (!processes.Any(IsRunning)) return;
        foreach (var process in processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        }
        while (processes.Any(IsRunning)) await Task.Delay(100);
    }

    private static bool IsRunning(Process process)
    {
        try { return !process.HasExited; }
        catch { return false; }
    }

    private static bool IsCodexTarget(JsonNode? target)
    {
        if (target?["type"]?.GetValue<string>() != "page" || target["webSocketDebuggerUrl"] is null) return false;
        var url = target["url"]?.GetValue<string>() ?? "";
        if (url.Equals("app://-/index.html", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("app://-/index.html?", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("initialRoute=%2Fchatgpt%2Fquick-chat", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsAvatarOverlay(JsonNode? target) =>
        (target?["url"]?.GetValue<string>() ?? "").Contains("initialRoute=%2Favatar-overlay", StringComparison.OrdinalIgnoreCase);

    private static async Task InjectRuntimeAsync(string websocketUrl, string script, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(websocketUrl), cancellationToken);
        await SendCdpAsync(socket, 1, "Runtime.enable", new { }, cancellationToken);
        await WaitForCdpResponseAsync(socket, 1, cancellationToken);
        await SendCdpAsync(socket, 2, "Page.setBypassCSP", new { enabled = true }, cancellationToken);
        await WaitForCdpResponseAsync(socket, 2, cancellationToken);
        await SendCdpAsync(socket, 3, "Page.addScriptToEvaluateOnNewDocument", new { source = script }, cancellationToken);
        await WaitForCdpResponseAsync(socket, 3, cancellationToken);
        await SendCdpAsync(socket, 4, "Runtime.evaluate", new { expression = script, awaitPromise = true, returnByValue = true }, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await WaitForCdpResponseAsync(socket, 4, timeout.Token);
    }

    private static async Task PatchDictationBundleAndReloadAsync(string websocketUrl, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(websocketUrl), token);
        await SendCdpAsync(socket, 9, "Network.enable", new { }, token);
        await WaitForCdpResponseAsync(socket, 9, token);
        await SendCdpAsync(socket, 10, "Network.setCacheDisabled", new { cacheDisabled = true }, token);
        await WaitForCdpResponseAsync(socket, 10, token);
        await SendCdpAsync(socket, 8, "Page.setBypassCSP", new { enabled = true }, token);
        await WaitForCdpResponseAsync(socket, 8, token);
        await SendCdpAsync(socket, 7, "Runtime.enable", new { }, token);
        await WaitForCdpResponseAsync(socket, 7, token);
        await SendCdpAsync(socket, 1, "Fetch.enable", new { patterns = new[]
        {
            new { urlPattern = "*app-initial-*.js*", requestStage = "Response" },
            new { urlPattern = "*voice-settings*.js*", requestStage = "Response" }
        } }, token);
        await WaitForCdpResponseAsync(socket, 1, token);
        await SendCdpAsync(socket, 2, "Page.reload", new { ignoreCache = true }, token);
        string? requestId = null;
        JsonNode? paused = null;
        var patchedApp = false;
        try
        {
            while (!patchedApp)
            {
                paused = await ReceiveCdpMessageAsync(socket, 8 * 1024 * 1024, token);
                if (paused?["method"]?.GetValue<string>() != "Fetch.requestPaused") continue;
                requestId = paused["params"]?["requestId"]?.GetValue<string>();
                var url = paused["params"]?["request"]?["url"]?.GetValue<string>() ?? "";
                if (requestId is null) continue;
                var isApp = url.Contains("app-initial-", StringComparison.OrdinalIgnoreCase);
                var isVoice = url.Contains("voice-settings", StringComparison.OrdinalIgnoreCase);
                if (!isApp && !isVoice) { await SendCdpAsync(socket, 4, "Fetch.continueRequest", new { requestId }, token); requestId = null; continue; }
                await SendCdpAsync(socket, 3, "Fetch.getResponseBody", new { requestId }, token);
                var response = await WaitForCdpResponseAsync(socket, 3, token);
                var body = response["result"]?["body"]?.GetValue<string>() ?? throw new InvalidDataException("Codex bundle body missing.");
                var source = response["result"]?["base64Encoded"]?.GetValue<bool>() == true ? Encoding.UTF8.GetString(Convert.FromBase64String(body)) : body;
                var patched = PatchDictationSource(source);
                if (isApp) ValidateAppBundlePatch(source, patched);
                if (isApp && !patched.Contains("__CODEX_TITLE_ROUTER__", StringComparison.Ordinal)) Log("Title router injection point not found; title routing stays disabled.");
                var headers = (paused?["params"]?["responseHeaders"]?.AsArray() ?? [])
                    .Where(header => !new[] { "content-length", "content-encoding", "transfer-encoding", "connection" }.Contains(header?["name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase))
                    .Select(header => new { name = header!["name"]!.GetValue<string>(), value = header["value"]!.GetValue<string>() }).ToArray();
                await SendCdpAsync(socket, 5, "Fetch.fulfillRequest", new { requestId, responseCode = paused?["params"]?["responseStatusCode"]?.GetValue<int>() ?? 200, responseHeaders = headers, body = Convert.ToBase64String(Encoding.UTF8.GetBytes(patched)) }, token);
                await WaitForCdpResponseAsync(socket, 5, token);
                if (isApp)
                {
                    patchedApp = true;
                    Log("Bundle patched: app-initial");
                }
                requestId = null;
            }
            _ = WatchVoiceBundleAsync(websocketUrl, cancellationToken);
        }
        finally
        {
            if (requestId is not null)
            {
                try
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendCdpAsync(socket, 7, "Fetch.continueRequest", new { requestId }, recovery.Token);
                }
                catch { }
            }
            try
            {
                using var disable = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendCdpAsync(socket, 6, "Fetch.disable", new { }, disable.Token);
            }
            catch { }
        }
    }

    private static async Task WatchVoiceBundleAsync(string websocketUrl, CancellationToken cancellationToken)
    {
        if (!VoiceWatchers.TryAdd(websocketUrl, 0)) return;
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(websocketUrl), cancellationToken);
            await SendCdpAsync(socket, 1, "Fetch.enable", new { patterns = new[]
            {
                new { urlPattern = "*voice-settings*.js*", requestStage = "Response" },
                new { urlPattern = "*voice*.js*", requestStage = "Response" },
                new { urlPattern = "*settings*.js*", requestStage = "Response" }
            } }, cancellationToken);
            await WaitForCdpResponseAsync(socket, 1, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var paused = await ReceiveCdpMessageAsync(socket, 8 * 1024 * 1024, cancellationToken);
                if (paused?["method"]?.GetValue<string>() != "Fetch.requestPaused") continue;
                var requestId = paused["params"]?["requestId"]?.GetValue<string>();
                var url = paused["params"]?["request"]?["url"]?.GetValue<string>() ?? "";
                if (requestId is null) continue;
                if (!url.Contains("voice-settings", StringComparison.OrdinalIgnoreCase))
                {
                    await SendCdpAsync(socket, 3, "Fetch.continueRequest", new { requestId }, cancellationToken);
                    continue;
                }
                await SendCdpAsync(socket, 4, "Fetch.getResponseBody", new { requestId }, cancellationToken);
                var response = await WaitForCdpResponseAsync(socket, 4, cancellationToken);
                var body = response["result"]?["body"]?.GetValue<string>() ?? throw new InvalidDataException("Voice settings bundle body missing.");
                var source = response["result"]?["base64Encoded"]?.GetValue<bool>() == true ? Encoding.UTF8.GetString(Convert.FromBase64String(body)) : body;
                var headers = (paused["params"]?["responseHeaders"]?.AsArray() ?? [])
                    .Where(header => !new[] { "content-length", "content-encoding", "transfer-encoding", "connection" }.Contains(header?["name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase))
                    .Select(header => new { name = header!["name"]!.GetValue<string>(), value = header["value"]!.GetValue<string>() }).ToArray();
                await SendCdpAsync(socket, 5, "Fetch.fulfillRequest", new { requestId, responseCode = paused["params"]?["responseStatusCode"]?.GetValue<int>() ?? 200, responseHeaders = headers, body = Convert.ToBase64String(Encoding.UTF8.GetBytes(PatchDictationSource(source))) }, cancellationToken);
                await WaitForCdpResponseAsync(socket, 5, cancellationToken);
                Log("Bundle patched: voice-settings");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { Log($"Voice bundle watcher stopped: {error.Message}"); }
        finally { VoiceWatchers.TryRemove(websocketUrl, out _); }
    }

    private static string PatchDictationSource(string source)
    {
        source = Regex.Replace(source,
            @"function (?<function>[$A-Za-z_][$\w]*)\((?<scope>[$A-Za-z_][$\w]*),(?<host>[$A-Za-z_][$\w]*)\)\{let (?<manager>[$A-Za-z_][$\w]*)=\k<scope>\.get\((?<managerAtom>[$A-Za-z_][$\w]*)\);if\(\k<manager>==null\)throw Error\(`AppServerManager RPC is not connected`\);return \k<manager>\.forHost\(\k<host>\)\}",
            "function ${function}(${scope},${host}){let ${manager}=${scope}.get(${managerAtom});if(${manager}==null)throw Error(`AppServerManager RPC is not connected`);let codexDictationClient=${manager}.forHost(${host});(globalThis.__CODEX_DICTATION_APP_SERVERS__??=new Map).set(${host},codexDictationClient);globalThis.__CODEX_DICTATION_APP_SERVER__=codexDictationClient;return codexDictationClient}",
            RegexOptions.CultureInvariant);
        const string connectInfoCall = "async function Zfo(){return(await Ax.getInstance().post(`/codex/dictation-stream-connect-info`,void 0)).body}";
        source = source.Replace(connectInfoCall,
            "async function Zfo(){return globalThis.__CODEX_DICTATION_CONNECT_INFO__??(await Ax.getInstance().post(`/codex/dictation-stream-connect-info`,void 0)).body}",
            StringComparison.Ordinal);
        source = Regex.Replace(source,
            @"async function (?<name>[$A-Za-z_][$\w]*)\(\)\{return\(await (?<client>[$A-Za-z_][$\w]*)\.getInstance\(\)\.post\(`/codex/dictation-stream-connect-info`,void 0\)\)\.body\}",
            "async function ${name}(){return globalThis.__CODEX_DICTATION_CONNECT_INFO__??(await ${client}.getInstance().post(`/codex/dictation-stream-connect-info`,void 0)).body}",
            RegexOptions.CultureInvariant);
        var gate = "return{isLoading:a,isError:!1,isCapable:!a&&n&&i===`chatgpt`}";
        var index = source.IndexOf(gate, StringComparison.Ordinal);
        if (index >= 0) source = source.Remove(index, gate.Length).Insert(index, "return{isLoading:a,isError:!1,isCapable:!a}");
        var streaming = "streamingEnabled:n";
        index = source.IndexOf(streaming, StringComparison.Ordinal);
        if (index >= 0) source = source.Remove(index, streaming.Length).Insert(index, "streamingEnabled:!0");
        var global = "return{isLoading:t,isError:!1,isCapable:!t&&(n!=null||i===!1)&&(n!==`chatgpt`||r!==!1)}";
        index = source.IndexOf(global, StringComparison.Ordinal);
        if (index >= 0) source = source.Remove(index, global.Length).Insert(index, "return{isLoading:t,isError:!1,isCapable:!t}");
        var keepVisible = "n==null||n.configuredHotkey==null&&n.configuredToggleHotkey==null||s.isPending";
        source = source.Replace(keepVisible, "s.isPending", StringComparison.Ordinal);
        source = Regex.Replace(source, @"n\s*==\s*null\s*\|\|\s*n(?:\?\.)?configuredHotkey\s*==\s*null\s*&&\s*n(?:\?\.)?configuredToggleHotkey\s*==\s*null\s*\|\|\s*s\.isPending", "s.isPending", RegexOptions.CultureInvariant);
        var overlayStatus = "m(e.configuredHotkey!=null||e.configuredToggleHotkey!=null?`idle`:`initializing`)";
        source = source.Replace(overlayStatus, "m(`idle`)", StringComparison.Ordinal);
        source = Regex.Replace(source,
            @"(?<setter>[$A-Za-z_][$\w]*)\(e\.configuredHotkey!=null\|\|e\.configuredToggleHotkey!=null\?`idle`:`initializing`\)",
            "${setter}(`idle`)", RegexOptions.CultureInvariant);
        const string keepVisibleMutation = "m=e=>{s.mutate({keepVisible:e})}";
        const string keepVisibleFallback = "m=e=>{let t=G(`global-dictation-hotkey-state`);i.setQueryData(t,{...(n??{}),keepVisible:e});s.mutate({keepVisible:e})}";
        source = source.Replace(keepVisibleMutation, keepVisibleFallback, StringComparison.Ordinal);
        const string composerPattern = @"(?<callback>[$A-Za-z_][$\w]*)=e=>\{if\((?<controller>[$A-Za-z_][$\w]*)\.view\.dom\.isConnected\)\{\k<controller>\.insertDictationText\(e\);return\}(?<fallback>[$A-Za-z_][$\w]*)\((?<scope>[$A-Za-z_][$\w]*),t=>(?<insert>[$A-Za-z_][$\w]*)\(t,e\)\)\},";
        const string composerReplacement = "${callback}=(window.__CODEX_DICTATION_REGISTER_COMPOSER__?.(${controller}),e=>{if(${controller}.view.dom.isConnected){${controller}.insertDictationText(e);return}${fallback}(${scope},t=>${insert}(t,e))}),";
        source = new Regex(composerPattern, RegexOptions.CultureInvariant).Replace(source, composerReplacement);
        source = TitleCallRegex.Replace(source, "await (globalThis.__CODEX_TITLE_ROUTER__?.title?.(${conversation},{prompt:${prompt}})??${native})");
        return source;
    }

    private static void ValidateAppBundlePatch(string source, string patched)
    {
        if (string.Equals(source, patched, StringComparison.Ordinal))
            throw new InvalidDataException("Codex dictation bundle did not match any known injection point.");
        if (!patched.Contains("__CODEX_DICTATION_CONNECT_INFO__", StringComparison.Ordinal))
            throw new InvalidDataException("Codex dictation connect-info injection point was not patched.");
        if (source.Contains("streamingEnabled:", StringComparison.Ordinal) && !patched.Contains("streamingEnabled:!0", StringComparison.Ordinal))
            throw new InvalidDataException("Codex streaming dictation capability was not enabled.");
    }

    private static async Task PatchGlobalDictationBundleAndReloadAsync(string websocketUrl, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var token = timeout.Token;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(websocketUrl), token);
        await SendCdpAsync(socket, 8, "Runtime.enable", new { }, token);
        await WaitForCdpResponseAsync(socket, 8, token);
        await SendCdpAsync(socket, 1, "Fetch.enable", new { patterns = new[] { new { urlPattern = "*global-dictation-page-*.js*", requestStage = "Response" } } }, token);
        await WaitForCdpResponseAsync(socket, 1, token);
        await SendCdpAsync(socket, 2, "Page.reload", new { ignoreCache = true }, token);
        string? requestId = null;
        try
        {
            while (true)
            {
                var paused = await ReceiveCdpMessageAsync(socket, 8 * 1024 * 1024, token);
                if (paused?["method"]?.GetValue<string>() != "Fetch.requestPaused") continue;
                requestId = paused["params"]?["requestId"]?.GetValue<string>();
                var url = paused["params"]?["request"]?["url"]?.GetValue<string>() ?? "";
                if (requestId is null) continue;
                if (!url.Contains("global-dictation-page-", StringComparison.OrdinalIgnoreCase))
                {
                    await SendCdpAsync(socket, 3, "Fetch.continueRequest", new { requestId }, token);
                    requestId = null;
                    continue;
                }
                await SendCdpAsync(socket, 4, "Fetch.getResponseBody", new { requestId }, token);
                var response = await WaitForCdpResponseAsync(socket, 4, token);
                var body = response["result"]?["body"]?.GetValue<string>() ?? throw new InvalidDataException("Global dictation bundle body missing.");
                var source = response["result"]?["base64Encoded"]?.GetValue<bool>() == true ? Encoding.UTF8.GetString(Convert.FromBase64String(body)) : body;
                var patched = PatchDictationSource(source);
                var headers = (paused["params"]?["responseHeaders"]?.AsArray() ?? [])
                    .Where(header => !new[] { "content-length", "content-encoding", "transfer-encoding", "connection" }.Contains(header?["name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase))
                    .Select(header => new { name = header!["name"]!.GetValue<string>(), value = header["value"]!.GetValue<string>() }).ToArray();
                await SendCdpAsync(socket, 5, "Fetch.fulfillRequest", new { requestId, responseCode = paused["params"]?["responseStatusCode"]?.GetValue<int>() ?? 200, responseHeaders = headers, body = Convert.ToBase64String(Encoding.UTF8.GetBytes(patched)) }, token);
                await WaitForCdpResponseAsync(socket, 5, token);
                requestId = null;
                break;
            }
        }
        finally
        {
            if (requestId is not null) try { await SendCdpAsync(socket, 6, "Fetch.continueRequest", new { requestId }, CancellationTokenSource.CreateLinkedTokenSource(token).Token); } catch { }
            try { await SendCdpAsync(socket, 7, "Fetch.disable", new { }, CancellationTokenSource.CreateLinkedTokenSource(token).Token); } catch { }
        }
    }

    private static Task SendCdpAsync(ClientWebSocket socket, int id, string method, object parameters, CancellationToken cancellationToken) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters }), WebSocketMessageType.Text, true, cancellationToken);


    private static async Task<JsonNode> WaitForCdpResponseAsync(ClientWebSocket socket, int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var inbox = CdpInboxes.GetOrCreateValue(socket);
            JsonNode? message = null;
            lock (inbox.Messages)
            {
                var queued = inbox.Messages.FirstOrDefault(item => item["id"]?.GetValue<int>() == id);
                if (queued is not null)
                {
                    inbox.Messages = new Queue<JsonNode>(inbox.Messages.Where(item => !ReferenceEquals(item, queued)));
                    message = queued;
                }
            }
            message ??= JsonNode.Parse(await ReceiveTextAsync(socket, 32 * 1024 * 1024, cancellationToken) ?? throw new WebSocketException("CDP closed unexpectedly."));
            if (message!["id"]?.GetValue<int>() != id)
            {
                lock (inbox.Messages) inbox.Messages.Enqueue(message);
                continue;
            }
            if (message["error"] is not null) throw new InvalidOperationException($"CDP command failed: {message["error"]}");
            return message;
        }
    }

    private static async Task<JsonNode> ReceiveCdpMessageAsync(ClientWebSocket socket, int maxBytes, CancellationToken cancellationToken)
    {
        var inbox = CdpInboxes.GetOrCreateValue(socket);
        lock (inbox.Messages)
        {
            if (inbox.Messages.Count > 0) return inbox.Messages.Dequeue();
        }
        return JsonNode.Parse(await ReceiveTextAsync(socket, maxBytes, cancellationToken) ?? throw new WebSocketException("CDP closed unexpectedly."))!;
    }

    private static string LoadInjectionScript(int port, string token)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexDictation.injection.js")
            ?? throw new InvalidOperationException("Embedded injection script is missing.");
        using var reader = new StreamReader(stream);
        var info = JsonSerializer.Serialize(new
        {
            websocketUrl = $"ws://127.0.0.1:{port}/dictation",
            protocols = new[] { "chatgpt-dictation", token, "codex-desktop" }
        });
        var helper = JsonSerializer.Serialize(new { url = $"http://127.0.0.1:{port}/config?token={Uri.EscapeDataString(token)}" });
        return reader.ReadToEnd()
            .Replace("__CONNECT_INFO__", info, StringComparison.Ordinal)
            .Replace("__HELPER_CONFIG__", helper, StringComparison.Ordinal);
    }

    private static void ActivateCodex(int debugPort)
    {
        var args = $"--remote-debugging-port={debugPort} --remote-allow-origins=http://127.0.0.1:{debugPort} {LocalNetworkOptOut}";
        var manager = (IApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))!)!;
        try
        {
            var aumid = ResolvePackageAumid();
            var result = manager.ActivateApplication(aumid, args, 0, out var processId);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            Log($"Activated {aumid} with CDP port {debugPort} (pid {processId}).");
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    private static string ResolvePackageAumid()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Where-Object Name -in @('OpenAI.Codex','OpenAI.CodexBeta','OpenAI.ChatGPT-Desktop') | Select-Object -First 1 -ExpandProperty InstallLocation\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to query the Codex Store package.");
        var location = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(5000);
        if (string.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("Codex Store package was not found.");
        return ResolveAumidFromFolder(Path.GetFileName(location.TrimEnd(Path.DirectorySeparatorChar)));
    }

    private static string ResolveAumidFromFolder(string folder)
    {
        var marker = folder.IndexOf("__", StringComparison.Ordinal);
        var identityEnd = folder.IndexOf('_');
        if (marker < 0 || identityEnd < 0) throw new InvalidDataException("Codex package identity is invalid.");
        return $"{folder[..identityEnd]}_{folder[(marker + 2)..]}!App";
    }

    private static int FreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Config? LoadConfigOrNull()
    {
        var path = ConfigPath();
        if (!File.Exists(path)) return null;
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Invalid config.json.");
        config = config with
        {
            Dictionary = config.Dictionary ?? Array.Empty<string>(),
            Provider = NormalizeProvider(config.Provider),
            VolcResourceId = string.IsNullOrWhiteSpace(config.VolcResourceId) ? "volc.seedasr.sauc.duration" : config.VolcResourceId,
            TitleMode = NormalizeTitleMode(config.TitleMode),
            TitleWireApi = NormalizeTitleWireApi(config.TitleWireApi),
            TitleBaseUrl = (config.TitleBaseUrl ?? "").Trim(),
            TitleModel = (config.TitleModel ?? "").Trim()
        };
        if (config.Provider == "aliyun")
        {
            if (config.WorkspaceId.Length > 0) ValidateWorkspaceId(config.WorkspaceId);
        }
        else ValidateVolcResourceId(config.VolcResourceId);
        if (config.Language.Length is < 2 or > 8) throw new InvalidDataException("Invalid ASR language.");
        return config;
    }

    private static void SaveConfig(Config config)
    {
        var path = ConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
    }

    private static string ConfigPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexDictation", "config.json");
    private static bool IsConfigRequestAuthorized(HttpContext context, string token) =>
        string.Equals(context.Request.Query["token"], token, StringComparison.Ordinal);
    private static async Task<string> ReadBodyAsync(HttpContext context, int maxBytes)
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes) throw new InvalidDataException("Request body is too large.");
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        if (Encoding.UTF8.GetByteCount(body) > maxBytes) throw new InvalidDataException("Request body is too large.");
        return body;
    }
    private static void AddCorsHeaders(HttpResponse response)
    {
        var origin = response.HttpContext.Request.Headers.Origin.ToString();
        response.Headers.AccessControlAllowOrigin = string.IsNullOrWhiteSpace(origin) ? "*" : origin;
        response.Headers.Vary = "Origin";
        response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
        response.Headers.AccessControlAllowHeaders = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
        response.Headers["Access-Control-Max-Age"] = "600";
        response.Headers.CacheControl = "no-store";
    }
    private static string NormalizeProvider(string? value) =>
        string.Equals(value, "volcengine", StringComparison.OrdinalIgnoreCase) ? "volcengine" : "aliyun";
    private static string NormalizeTitleMode(string? value) => value?.ToLowerInvariant() switch
    {
        "custom" => "custom",
        "current" => "current",
        _ => "off"
    };
    private static string NormalizeTitleWireApi(string? value) =>
        string.Equals(value, "responses", StringComparison.OrdinalIgnoreCase) ? "responses" : "chat";

    private static string CredentialTarget(string provider) =>
        provider == "volcengine" ? VolcengineCredentialTarget : AliyunCredentialTarget;

    private static void ValidateWorkspaceId(string? value)
    {
        if (value is null || value.Length is < 8 or > 128 || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-')))
            throw new ArgumentException("Invalid Aliyun WorkspaceId.");
    }

    private static void ValidateVolcResourceId(string? value)
    {
        if (value is null || value.Length is < 4 or > 128 || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')))
            throw new ArgumentException("Invalid Volcengine Resource ID.");
    }

    private static void HideConsole()
    {
        if (OperatingSystem.IsWindows()) ShowWindow(GetConsoleWindow(), 0);
    }

    private static async Task<string> ReadStubRequestAsync(Stream stream)
    {
        var buffer = new byte[65_536];
        var read = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read));
            if (count <= 0) break;
            read += count;
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) continue;
            var length = 0;
            foreach (var line in text[..headerEnd].Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line["Content-Length:".Length..].Trim());
            if (read >= headerEnd + 4 + length) return text;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    internal static void Log(string message)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexDictation");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "helper.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }

    private static async Task<int> SelfTestAsync()
    {
        var input = Enumerable.Range(0, 4800).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 16000)).ToArray();
        var bytes = new byte[input.Length * 2];
        Buffer.BlockCopy(input, 0, bytes, 0, bytes.Length);
        var resampler = new Pcm16Resampler(48000, 16000);
        var output = resampler.Process(bytes[..3000]).Concat(resampler.Process(bytes[3000..])).ToArray();
        if (output.Length is < 3190 or > 3210) throw new Exception($"Resampler output length is wrong: {output.Length}");
        using var document = JsonDocument.Parse(DictationSession.ClosedEvent("session", 3));
        if (document.RootElement.GetProperty("session").GetProperty("status").GetString() != "closed") throw new Exception("Closed event is invalid.");
        if (ResolveAumidFromFolder("OpenAI.Codex_26.820.9563.0_x64__2p2nqsd0c76g0") != "OpenAI.Codex_2p2nqsd0c76g0!App") throw new Exception("AUMID parsing is invalid.");
        if (!LocalNetworkOptOut.Contains("LocalNetworkAccessChecksWebSockets", StringComparison.Ordinal)) throw new Exception("Local network launch flags are invalid.");
        var gateSource = "async function Zfo(){return(await Ax.getInstance().post(`/codex/dictation-stream-connect-info`,void 0)).body} return{isLoading:a,isError:!1,isCapable:!a&&n&&i===`chatgpt`} streamingEnabled:n return{isLoading:t,isError:!1,isCapable:!t&&(n!=null||i===!1)&&(n!==`chatgpt`||r!==!1)} n==null||n.configuredHotkey==null&&n.configuredToggleHotkey==null||s.isPending m=e=>{s.mutate({keepVisible:e})} ii=e=>{if(lt.view.dom.isConnected){lt.insertDictationText(e);return}pq(W,t=>j0o(t,e))},ai=e=>{}";
        var patchedGate = PatchDictationSource(gateSource);
        if (!patchedGate.Contains("__CODEX_DICTATION_CONNECT_INFO__", StringComparison.Ordinal) || !patchedGate.Contains("isCapable:!a", StringComparison.Ordinal) || !patchedGate.Contains("streamingEnabled:!0", StringComparison.Ordinal) || !patchedGate.Contains("isCapable:!t}", StringComparison.Ordinal) || patchedGate.Contains("configuredHotkey==null", StringComparison.Ordinal) || !patchedGate.Contains("s.isPending", StringComparison.Ordinal) || !patchedGate.Contains("setQueryData(t", StringComparison.Ordinal) || !patchedGate.Contains("__CODEX_DICTATION_REGISTER_COMPOSER__", StringComparison.Ordinal)) throw new Exception("Dictation gate patch is invalid.");
        var currentBundleSource = "async function lTs(){return(await sS.getInstance().post(`/codex/dictation-stream-connect-info`,void 0)).body} return{isLoading:a,isError:!1,isCapable:!a&&n&&i===`chatgpt`} streamingEnabled:n return{isLoading:t,isError:!1,isCapable:!t&&(n!=null||i===!1)&&(n!==`chatgpt`||r!==!1)} S(e.configuredHotkey!=null||e.configuredToggleHotkey!=null?`idle`:`initializing`)";
        var patchedCurrentBundle = PatchDictationSource(currentBundleSource);
        if (!patchedCurrentBundle.Contains("__CODEX_DICTATION_CONNECT_INFO__", StringComparison.Ordinal) || !patchedCurrentBundle.Contains("streamingEnabled:!0", StringComparison.Ordinal) || !patchedCurrentBundle.Contains("S(`idle`)", StringComparison.Ordinal))
            throw new Exception("Current Codex dictation bundle patch is invalid.");
        ValidateAppBundlePatch(currentBundleSource, patchedCurrentBundle);
        var configBridgeSource = "function Dm(e,t){let n=e.get(Om);if(n==null)throw Error(`AppServerManager RPC is not connected`);return n.forHost(t)}";
        var patchedConfigBridge = PatchDictationSource(configBridgeSource);
        if (!patchedConfigBridge.Contains("__CODEX_DICTATION_APP_SERVERS__", StringComparison.Ordinal) || !patchedConfigBridge.Contains(".set(t,codexDictationClient)", StringComparison.Ordinal) || !patchedConfigBridge.Contains("__CODEX_DICTATION_APP_SERVER__", StringComparison.Ordinal))
            throw new Exception("Codex config client bridge patch is invalid.");
        if (!DictationSession.StartedEvent("session", 1).Contains("transcript_delivery_mode\":\"segment", StringComparison.Ordinal)) throw new Exception("Streaming transcript mode is invalid.");
        var activationManager = (IApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))!)!;
        Marshal.ReleaseComObject(activationManager);
        var injection = LoadInjectionScript(12345, "codex-dictation.test");
        if (injection.Contains("__CONNECT_INFO__", StringComparison.Ordinal) || injection.Contains("__HELPER_CONFIG__", StringComparison.Ordinal) ||
            !injection.Contains("ws://127.0.0.1:12345/dictation", StringComparison.Ordinal) || !injection.Contains("http://127.0.0.1:12345/config", StringComparison.Ordinal))
            throw new Exception("Injection script substitution failed.");
        if (!injection.Contains("insertText(next", StringComparison.Ordinal) || !injection.Contains("clearPreview", StringComparison.Ordinal) || !injection.Contains("lastFocusedComposer", StringComparison.Ordinal) || !injection.Contains("aria-modal", StringComparison.Ordinal))
            throw new Exception("Native dictation preview bridge is missing.");
        var volcJson = DictationSession.BuildVolcJsonFrame(Encoding.UTF8.GetBytes("{}"), 1);
        if (!volcJson.AsSpan(0, 4).SequenceEqual(new byte[] { 0x11, 0x11, 0x11, 0x00 }) || !Encoding.UTF8.GetString(DictationSession.Gunzip(volcJson[12..])).Equals("{}", StringComparison.Ordinal))
            throw new Exception("Volcengine JSON frame is invalid.");
        var volcAudio = DictationSession.BuildVolcAudioFrame(new byte[] { 1, 2, 3, 4 }, -1, true);
        if (!volcAudio.AsSpan(0, 4).SequenceEqual(new byte[] { 0x11, 0x23, 0x01, 0x00 }) || BinaryPrimitives.ReadInt32BigEndian(volcAudio.AsSpan(4, 4)) != -1 || !DictationSession.Gunzip(volcAudio[12..]).SequenceEqual(new byte[] { 1, 2, 3, 4 }))
            throw new Exception("Volcengine audio frame is invalid.");
        var titleBundleSource = "async function dXn(){let o=t.getConversation(n);let a=await yU.threadMetadataGeneration?.generateTitle({hostId:t.getHostId(),prompt:yYn(u),cwd:i,readOnlyAppToolAllowlist:r,...o.serviceName===void 0?{}:{serviceName:o.serviceName}}),s=a?.title.trim();return s}";
        var patchedTitleBundle = PatchDictationSource(titleBundleSource);
        if (!patchedTitleBundle.Contains("__CODEX_TITLE_ROUTER__", StringComparison.Ordinal) ||
            !patchedTitleBundle.Contains("prompt:yYn(u)", StringComparison.Ordinal) ||
            !patchedTitleBundle.Contains("threadMetadataGeneration?.generateTitle(", StringComparison.Ordinal))
            throw new Exception("Title router bundle patch is invalid.");
        var extractedTitle = TitleGenerator.Extract("```json\n{\"title\":\"  Add  title  router \",\"description\":\"route titles\"}\n```");
        if (extractedTitle?.Title != "Add title router" || extractedTitle.Description != "route titles")
            throw new Exception("Title response parsing is invalid.");
        if (TitleGenerator.Extract(new string('x', 40)) is not { Title.Length: 36 }) throw new Exception("Title truncation is invalid.");
        if (TitleGenerator.Endpoint("https://example.com/v1", "chat") != "https://example.com/v1/chat/completions" ||
            TitleGenerator.Endpoint("https://example.com", "responses") != "https://example.com/responses" ||
            TitleGenerator.Endpoint("https://generativelanguage.googleapis.com/v1beta/openai", "chat") != "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")
            throw new Exception("Title endpoint building is invalid.");
        var codexConfig = CodexConfigFile.Parse("model = \"deepseek-v4-flash\"\nmodel_provider = \"Relay\"\n[model_providers.Relay]\nname = \"Relay\"\nbase_url = \"https://example.com/v1\"\nwire_api = \"responses\"\nexperimental_bearer_token = \"sk-test\"\n");
        var relayProvider = codexConfig.FindProvider(codexConfig.ModelProvider);
        if (codexConfig.Model != "deepseek-v4-flash" || relayProvider?.BaseUrl != "https://example.com/v1" || relayProvider.Token != "sk-test" || relayProvider.WireApi != "responses")
            throw new Exception("Codex config parsing is invalid.");
        using var stub = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        stub.Start();
        var stubPort = ((IPEndPoint)stub.LocalEndpoint).Port;
        var stubRequests = new List<string>();
        var stubServer = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var client = await stub.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var request = await ReadStubRequestAsync(stream);
                stubRequests.Add(request);
                var stripped = !request.Contains("response_format", StringComparison.Ordinal) && !request.Contains("reasoning_effort", StringComparison.Ordinal);
                var success = attempt > 0 && stripped;
                var payload = success
                    ? "{\"choices\":[{\"message\":{\"content\":\"{\\\"title\\\":\\\"Route titles\\\",\\\"description\\\":\\\"through the stub\\\"}\"}}]}"
                    : "{\"error\":{\"message\":\"invalid schema: reasoning_effort and response_format\"}}";
                var body = Encoding.UTF8.GetBytes(payload);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? 200 : 400)} {(success ? "OK" : "Bad Request")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
            }
        });
        var stubConfig = new Config("ws-12345678", "zh", Array.Empty<string>(), "aliyun", "volc.seedasr.sauc.duration",
            "custom", $"http://127.0.0.1:{stubPort}", "gpt-4o-mini", "chat");
        var generatedTitle = await TitleGenerator.GenerateAsync(stubConfig, "generate a title", null, null, CancellationToken.None);
        await stubServer.WaitAsync(TimeSpan.FromSeconds(15));
        stub.Stop();
        if (generatedTitle?.Title != "Route titles" || generatedTitle.Description != "through the stub")
            throw new Exception("Title round-trip is invalid.");
        if (stubRequests.Count != 2 || !stubRequests[0].Contains("response_format", StringComparison.Ordinal) || !stubRequests[0].Contains("reasoning_effort", StringComparison.Ordinal) ||
            stubRequests[1].Contains("response_format", StringComparison.Ordinal) || stubRequests[1].Contains("reasoning_effort", StringComparison.Ordinal) || !stubRequests[1].Contains("gpt-4o-mini", StringComparison.Ordinal))
            throw new Exception("Title retry payload is invalid.");
        Console.WriteLine("Self-test passed.");
        return 0;
    }

    internal sealed record Config(string WorkspaceId, string Language, string[] Dictionary, string Provider = "aliyun", string VolcResourceId = "volc.seedasr.sauc.duration",
        string TitleMode = "off", string TitleBaseUrl = "", string TitleModel = "", string TitleWireApi = "chat");
    private sealed record ConfigRequest(string? WorkspaceId, string? ApiKey, string[]? Dictionary, string? Provider, string? VolcResourceId,
        string? TitleMode = null, string? TitleBaseUrl = null, string? TitleModel = null, string? TitleWireApi = null, string? TitleApiKey = null);
    private sealed record TitleRequest(string? Prompt, string? Model, string? Provider);

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, IntPtr verb, out uint processId);
        [PreserveSig] int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
    }

    internal static async Task<string?> ReceiveTextAsync(WebSocket socket, int maxBytes, CancellationToken cancellationToken)
    {
        using var data = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Only text WebSocket messages are supported.");
            data.Write(buffer, 0, result.Count);
            if (data.Length > maxBytes) throw new InvalidDataException("WebSocket message is too large.");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(data.GetBuffer(), 0, checked((int)data.Length));
        }
    }
}

internal sealed class DictationSession(WebSocket client, Program.Config config, string apiKey)
{
    private const int MaxClientMessageBytes = 6 * 1024 * 1024;
    private readonly SemaphoreSlim _clientSendLock = new(1, 1);
    private int _sequence;
    private int _transcriptRevision;
    private string _lastPreview = "";
    private string _sessionId = Guid.NewGuid().ToString("N");
    private int _volcSequence;
    private int _volcUtterance;
    private string? _volcUtteranceId;
    private string _volcLastText = "";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var startText = await Program.ReceiveTextAsync(client, MaxClientMessageBytes, linked.Token) ?? throw new InvalidDataException("Missing session.start.");
            using var start = JsonDocument.Parse(startText);
            var root = start.RootElement;
            if (root.GetProperty("type").GetString() != "session.start") throw new InvalidDataException("First event must be session.start.");
            var inputRate = root.GetProperty("config").GetProperty("sample_rate_hz").GetInt32();
            if (inputRate is < 8000 or > 192000) throw new InvalidDataException("Unsupported input sample rate.");
            if (root.GetProperty("config").GetProperty("input_audio_format").GetString() != "pcm16") throw new InvalidDataException("Only PCM16 audio is supported.");
            if (root.GetProperty("config").GetProperty("num_channels").GetInt32() != 1) throw new InvalidDataException("Only mono audio is supported.");

            Program.Log($"Dictation session.start received; rate={inputRate}, provider={config.Provider}.");

            if (config.Provider == "volcengine") await RunVolcengineAsync(inputRate, linked, apiKey);
            else await RunAliyunAsync(inputRate, linked, apiKey);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Program.Log($"Dictation session error: {error.Message}");
            await TrySendClientErrorAsync(error.Message, cancellationToken);
        }
        finally
        {
            linked.Cancel();
            if (client.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None);
        }
    }

    private async Task RunAliyunAsync(int inputRate, CancellationTokenSource linked, string apiKey)
    {
        using var upstream = new ClientWebSocket();
        upstream.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        upstream.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        var uri = new Uri($"wss://{config.WorkspaceId}.cn-beijing.maas.aliyuncs.com/api-ws/v1/realtime?model=qwen3-asr-flash-realtime");
        await upstream.ConnectAsync(uri, linked.Token);
        Program.Log("Aliyun upstream connected.");
        await SendJsonAsync(upstream, new { event_id = EventId(), type = "session.update", session = new
        {
            modalities = new[] { "text" }, input_audio_format = "pcm", sample_rate = 16000,
            input_audio_transcription = new { language = config.Language, corpus = config.Dictionary.Length == 0 ? null : new { text = string.Join("、", config.Dictionary) } },
            turn_detection = new { type = "server_vad", threshold = 0.0, silence_duration_ms = 500 }
        } }, linked.Token);
        await WaitForUpstreamReadyAsync(upstream, linked.Token);
        await SendClientAsync(StartedEvent(_sessionId, NextSequence()), linked.Token);
        var resampler = new Pcm16Resampler(inputRate, 16000);
        var audioChunks = 0;
        var upstreamEvents = RelayUpstreamAsync(upstream, linked.Token);
        while (true)
        {
            var text = await ReceiveClientMessageAsync(linked.Token);
            if (text is null) break;
            using var message = JsonDocument.Parse(text);
            var type = message.RootElement.GetProperty("type").GetString();
            if (type == "audio.append")
            {
                var audio = Convert.FromBase64String(message.RootElement.GetProperty("audio").GetString() ?? "");
                if (audio.Length > 4 * 1024 * 1024 || (audio.Length & 1) != 0) throw new InvalidDataException("Invalid PCM16 audio chunk.");
                var converted = resampler.Process(audio);
                if (converted.Length > 0) { audioChunks++; await SendJsonAsync(upstream, new { event_id = EventId(), type = "input_audio_buffer.append", audio = Convert.ToBase64String(converted) }, linked.Token); }
            }
            else if (type == "session.close")
            {
                await SendJsonAsync(upstream, new { event_id = EventId(), type = "session.finish" }, linked.Token);
                using var finishTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token); finishTimeout.CancelAfter(TimeSpan.FromSeconds(7));
                await upstreamEvents.WaitAsync(finishTimeout.Token); return;
            }
            else throw new InvalidDataException($"Unsupported client event: {type}");
        }
        linked.Cancel(); await IgnoreCancellation(upstreamEvents);
        Program.Log($"Aliyun session ended; audioChunks={audioChunks}.");
    }

    private async Task RunVolcengineAsync(int inputRate, CancellationTokenSource linked, string apiKey)
    {
        using var upstream = new ClientWebSocket();
        var requestId = Guid.NewGuid().ToString();
        upstream.Options.SetRequestHeader("X-Api-Key", apiKey);
        upstream.Options.SetRequestHeader("X-Api-Resource-Id", config.VolcResourceId);
        upstream.Options.SetRequestHeader("X-Api-Request-Id", requestId);
        upstream.Options.SetRequestHeader("X-Api-Connect-Id", Guid.NewGuid().ToString());
        upstream.Options.SetRequestHeader("X-Api-Sequence", "-1");
        await upstream.ConnectAsync(new Uri("wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async"), linked.Token);
        Program.Log("Volcengine upstream connected.");
        await SendVolcFullRequestAsync(upstream, linked.Token);
        await SendClientAsync(StartedEvent(_sessionId, NextSequence()), linked.Token);
        var resampler = new Pcm16Resampler(inputRate, 16000);
        var upstreamEvents = RelayVolcengineAsync(upstream, linked.Token);
        while (true)
        {
            var text = await ReceiveClientMessageAsync(linked.Token);
            if (text is null) break;
            using var message = JsonDocument.Parse(text);
            var type = message.RootElement.GetProperty("type").GetString();
            if (type == "audio.append")
            {
                var audio = Convert.FromBase64String(message.RootElement.GetProperty("audio").GetString() ?? "");
                if (audio.Length > 4 * 1024 * 1024 || (audio.Length & 1) != 0) throw new InvalidDataException("Invalid PCM16 audio chunk.");
                var converted = resampler.Process(audio);
                if (converted.Length > 0) await SendVolcAudioAsync(upstream, converted, false, linked.Token);
            }
            else if (type == "session.close")
            {
                await SendVolcAudioAsync(upstream, Array.Empty<byte>(), true, linked.Token);
                using var finishTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token); finishTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                await upstreamEvents.WaitAsync(finishTimeout.Token); return;
            }
            else throw new InvalidDataException($"Unsupported client event: {type}");
        }
        linked.Cancel(); await IgnoreCancellation(upstreamEvents);
    }

    private async Task SendVolcFullRequestAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var language = config.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : config.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : config.Language;
        var request = new
        {
            user = new { uid = "codex-dictation" },
            audio = new { format = "pcm", codec = "raw", rate = 16000, bits = 16, channel = 1, language },
            request = new
            {
                model_name = "bigmodel", enable_nonstream = true, enable_itn = true, enable_punc = true,
                enable_ddc = true, result_type = "full", show_utterances = true, enable_accelerate_text = false,
                accelerate_score = 0, end_window_size = 800,
                corpus = config.Dictionary.Length == 0 ? null : new { context = JsonSerializer.Serialize(new { hotwords = config.Dictionary.Select(word => new { word }).ToArray() }) }
            }
        };
        await socket.SendAsync(BuildVolcJsonFrame(JsonSerializer.SerializeToUtf8Bytes(request), 1), WebSocketMessageType.Binary, true, cancellationToken);
    }

    private async Task SendVolcAudioAsync(ClientWebSocket socket, byte[] audio, bool last, CancellationToken cancellationToken)
    {
        var sequence = ++_volcSequence;
        if (last) sequence = -sequence;
        await socket.SendAsync(BuildVolcAudioFrame(audio, sequence, last), WebSocketMessageType.Binary, true, cancellationToken);
    }

    private async Task RelayVolcengineAsync(ClientWebSocket upstream, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReceiveBinaryAsync(upstream, 8 * 1024 * 1024, cancellationToken);
            if (frame is null) throw new WebSocketException("Volcengine closed unexpectedly.");
            if (frame.Value.Type == 15) throw new InvalidOperationException(VolcError(frame.Value.Payload));
            if (frame.Value.Type != 9) continue;
            using var document = JsonDocument.Parse(frame.Value.Payload);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0) throw new InvalidOperationException(VolcError(root));
            if (!root.TryGetProperty("result", out var result))
            {
                if (frame.Value.Sequence < 0)
                {
                    await SendClientAsync(ClosedEvent(_sessionId, NextSequence()), cancellationToken);
                    return;
                }
                continue;
            }
            var text = result.TryGetProperty("text", out var textValue) ? textValue.GetString() ?? "" : "";
            var definite = false;
            if (result.TryGetProperty("utterances", out var utterances) && utterances.ValueKind == JsonValueKind.Array)
            {
                var lastUtteranceText = "";
                foreach (var utterance in utterances.EnumerateArray())
                {
                    if (utterance.TryGetProperty("text", out var utteranceText) && utteranceText.GetString() is { } value) lastUtteranceText = value;
                    definite |= utterance.TryGetProperty("definite", out var finalValue) && finalValue.GetBoolean();
                }
                if (text.Length == 0 && lastUtteranceText.Length > 0) text = lastUtteranceText;
            }
            if (text.Length > 0)
            {
                _volcUtteranceId ??= $"volc-utterance-{++_volcUtterance}";
                if (_volcLastText.Length == 0) await SendClientAsync(JsonSerializer.Serialize(new { type = "speech.started", sequence_no = NextSequence(), utterance_id = _volcUtteranceId }), cancellationToken);
                if (!string.Equals(text, _volcLastText, StringComparison.Ordinal))
                {
                    _volcLastText = text;
                    await SendClientAsync(JsonSerializer.Serialize(new { type = "transcript.segment", sequence_no = NextSequence(), utterance_id = _volcUtteranceId, revision = ++_transcriptRevision, text }), cancellationToken);
                }
                if (definite || frame.Value.Sequence < 0)
                {
                    await SendClientAsync(JsonSerializer.Serialize(new { type = "transcript.final", sequence_no = NextSequence(), utterance_id = _volcUtteranceId, revision = ++_transcriptRevision, text }), cancellationToken);
                    await SendClientAsync(JsonSerializer.Serialize(new { type = "speech.stopped", sequence_no = NextSequence(), utterance_id = _volcUtteranceId }), cancellationToken);
                    _volcUtteranceId = null; _volcLastText = ""; _transcriptRevision = 0;
                }
            }
            if (frame.Value.Sequence < 0)
            {
                await SendClientAsync(ClosedEvent(_sessionId, NextSequence()), cancellationToken);
                return;
            }
        }
    }

    private readonly record struct VolcFrame(int Type, int Sequence, byte[] Payload);

    internal static byte[] BuildVolcJsonFrame(byte[] json, int sequence) => BuildVolcFrame(0x11, sequence, Gzip(json));
    internal static byte[] BuildVolcAudioFrame(byte[] pcm, int sequence, bool last) => BuildVolcFrame(last ? (byte)0x23 : (byte)0x21, sequence, Gzip(pcm), false);
    private static byte[] BuildVolcFrame(byte flags, int sequence, byte[] payload, bool json = true)
    {
        var frame = new byte[12 + payload.Length];
        frame[0] = 0x11; frame[1] = flags; frame[2] = (byte)((json ? 1 : 0) << 4 | 1); frame[3] = 0;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), (uint)payload.Length);
        payload.CopyTo(frame, 12);
        return frame;
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }

    private static async Task<VolcFrame?> ReceiveBinaryAsync(WebSocket socket, int maxBytes, CancellationToken cancellationToken)
    {
        using var data = new MemoryStream(); var buffer = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Volcengine returned a non-binary frame.");
            data.Write(buffer, 0, result.Count);
            if (data.Length > maxBytes) throw new InvalidDataException("Volcengine frame is too large.");
            if (!result.EndOfMessage) continue;
            var bytes = data.ToArray();
            if (bytes.Length < 12 || bytes[0] != 0x11) throw new InvalidDataException("Invalid Volcengine frame header.");
            var type = bytes[1] >> 4; var sequence = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
            if (length > bytes.Length - 12) throw new InvalidDataException("Invalid Volcengine payload length.");
            var payload = bytes.AsSpan(12, (int)length).ToArray();
            if ((bytes[2] & 0x0F) == 1) payload = Gunzip(payload);
            if (((bytes[2] >> 4) & 0x0F) == 1 && payload.Length > 0) return new VolcFrame(type, sequence, payload);
            return new VolcFrame(type, sequence, payload);
        }
    }

    internal static byte[] Gunzip(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var gzip = new GZipStream(input, CompressionMode.Decompress); using var output = new MemoryStream();
        gzip.CopyTo(output); return output.ToArray();
    }

    private static string VolcError(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return VolcError(document.RootElement);
        }
        catch { return "Volcengine ASR error."; }
    }

    private static string VolcError(JsonElement root) => root.TryGetProperty("message", out var message) ? message.GetString() ?? "Volcengine ASR error." : root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var nested) ? nested.GetString() ?? "Volcengine ASR error." : "Volcengine ASR error.";

    private async Task WaitForUpstreamReadyAsync(ClientWebSocket upstream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            var text = await Program.ReceiveTextAsync(upstream, 1_000_000, timeout.Token) ?? throw new WebSocketException("Aliyun closed before session.updated.");
            using var message = JsonDocument.Parse(text);
            var type = message.RootElement.GetProperty("type").GetString();
            if (type == "session.updated")
            {
                _sessionId = message.RootElement.GetProperty("session").GetProperty("id").GetString() ?? _sessionId;
                return;
            }
            if (type == "error") throw new InvalidOperationException(UpstreamError(message.RootElement));
        }
    }

    private async Task RelayUpstreamAsync(ClientWebSocket upstream, CancellationToken cancellationToken)
    {
        while (true)
        {
            var text = await Program.ReceiveTextAsync(upstream, 1_000_000, cancellationToken);
            if (text is null) throw new WebSocketException("Aliyun closed unexpectedly.");
            using var message = JsonDocument.Parse(text);
            var root = message.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "input_audio_buffer.speech_started" || type == "input_audio_buffer.speech_stopped")
            {
                var utteranceId = root.GetProperty("item_id").GetString() ?? EventId();
                if (type.EndsWith("started", StringComparison.Ordinal))
                {
                    _transcriptRevision = 0;
                    _lastPreview = "";
                }
                var mapped = type.EndsWith("started", StringComparison.Ordinal) ? "speech.started" : "speech.stopped";
                await SendClientAsync(JsonSerializer.Serialize(new { type = mapped, sequence_no = NextSequence(), utterance_id = utteranceId }), cancellationToken);
            }
            else if (type == "conversation.item.input_audio_transcription.text")
            {
                var utteranceId = root.GetProperty("item_id").GetString() ?? EventId();
                var preview = (root.GetProperty("text").GetString() ?? "") + (root.GetProperty("stash").GetString() ?? "");
                if (preview.Length == 0 || preview == _lastPreview) continue;
                _lastPreview = preview;
                await SendClientAsync(JsonSerializer.Serialize(new
                {
                    type = "transcript.segment",
                    sequence_no = NextSequence(),
                    utterance_id = utteranceId,
                    revision = ++_transcriptRevision,
                    text = preview
                }), cancellationToken);
            }
            else if (type == "conversation.item.input_audio_transcription.completed")
            {
                var utteranceId = root.GetProperty("item_id").GetString() ?? EventId();
                var finalText = root.GetProperty("transcript").GetString() ?? "";
                await SendClientAsync(JsonSerializer.Serialize(new { type = "transcript.final", sequence_no = NextSequence(), utterance_id = utteranceId, revision = ++_transcriptRevision, text = finalText }), cancellationToken);
            }
            else if (type == "conversation.item.input_audio_transcription.failed")
            {
                await SendClientAsync(JsonSerializer.Serialize(new { type = "transcript.failed", sequence_no = NextSequence(), utterance_id = root.GetProperty("item_id").GetString(), error = ErrorObject(UpstreamError(root)) }), cancellationToken);
            }
            else if (type == "error") throw new InvalidOperationException(UpstreamError(root));
            else if (type == "session.finished")
            {
                await SendClientAsync(ClosedEvent(_sessionId, NextSequence()), cancellationToken);
                if (upstream.State == WebSocketState.Open) await upstream.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "finished", cancellationToken);
                return;
            }
        }
    }

    private async Task SendClientAsync(string json, CancellationToken cancellationToken)
    {
        await _clientSendLock.WaitAsync(cancellationToken);
        try { await client.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken); }
        finally { _clientSendLock.Release(); }
    }

    private async Task<string?> ReceiveClientMessageAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await Program.ReceiveTextAsync(client, MaxClientMessageBytes, timeout.Token);
    }

    private async Task TrySendClientErrorAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            if (client.State == WebSocketState.Open)
                await SendClientAsync(JsonSerializer.Serialize(new { type = "session.error", sequence_no = NextSequence(), fatal = true, error = ErrorObject(message) }), cancellationToken);
        }
        catch { }
    }

    private static Task SendJsonAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, cancellationToken);

    private static object ErrorObject(string message) => new { code = "asr_error", message, retryable = false };
    private static string EventId() => "event_" + Guid.NewGuid().ToString("N");
    private int NextSequence() => Interlocked.Increment(ref _sequence);
    private static string UpstreamError(JsonElement root) => root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message) ? message.GetString() ?? "Aliyun ASR error." : "Aliyun ASR error.";
    private static async Task IgnoreCancellation(Task task) { try { await task; } catch (OperationCanceledException) { } }

    internal static string StartedEvent(string sessionId, int sequence) => JsonSerializer.Serialize(new
    {
        type = "session.started",
        sequence_no = sequence,
        session = new { session_id = sessionId, status = "active", config = new { provider_mode = "streaming_sse", transcript_delivery_mode = "segment" } }
    });

    internal static string ClosedEvent(string sessionId, int sequence) => JsonSerializer.Serialize(new
    {
        type = "session.updated",
        sequence_no = sequence,
        session = new { session_id = sessionId, status = "closed", config = new { provider_mode = "streaming_sse", transcript_delivery_mode = "segment" } }
    });
}

internal sealed class Pcm16Resampler(int inputRate, int outputRate)
{
    private readonly double _step = (double)inputRate / outputRate;
    private long _inputIndex = -1;
    private double _nextOutputPosition;
    private short _previous;

    public byte[] Process(byte[] pcm16)
    {
        if ((pcm16.Length & 1) != 0) throw new ArgumentException("PCM16 data must contain complete samples.");
        if (inputRate == outputRate) return pcm16;
        var output = new List<short>((int)Math.Ceiling(pcm16.Length / 2.0 * outputRate / inputRate) + 2);
        for (var offset = 0; offset < pcm16.Length; offset += 2)
        {
            var current = (short)(pcm16[offset] | pcm16[offset + 1] << 8);
            _inputIndex++;
            if (_inputIndex == 0)
            {
                output.Add(current);
                _nextOutputPosition = _step;
            }
            else
            {
                while (_nextOutputPosition <= _inputIndex)
                {
                    var fraction = _nextOutputPosition - (_inputIndex - 1);
                    output.Add((short)Math.Clamp(Math.Round(_previous + fraction * (current - _previous)), short.MinValue, short.MaxValue));
                    _nextOutputPosition += _step;
                }
            }
            _previous = current;
        }
        var bytes = new byte[output.Count * 2];
        for (var i = 0; i < output.Count; i++)
        {
            bytes[i * 2] = (byte)output[i];
            bytes[i * 2 + 1] = (byte)(output[i] >> 8);
        }
        return bytes;
    }
}

internal static class CredentialStore
{
    private const uint TypeGeneric = 1;
    private const uint PersistLocalMachine = 2;

    public static void Write(string target, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = TypeGeneric, TargetName = target, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = PersistLocalMachine, UserName = "apikey" };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeCoTaskMem(blob); }
    }

    public static string? Read(string target)
    {
        if (!CredRead(target, TypeGeneric, 0, out var pointer)) return Marshal.GetLastWin32Error() == 1168 ? null : throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally { CredFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}

internal static class TitleGenerator
{
    internal sealed record Result(string Title, string? Description);

    private const int MaxTitleLength = 36;
    private const int MaxDescriptionLength = 100;
    private const string TitleSchemaJson = """
        {"type":"object","properties":{"title":{"type":"string","description":"Concise task title, at most 36 characters."},"description":{"type":"string","description":"Compact search-oriented summary, at most 100 characters."}},"required":["title","description"],"additionalProperties":false}
        """;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };

    internal static async Task<Result?> GenerateAsync(Program.Config config, string prompt, string? modelHint, string? providerHint, CancellationToken cancellationToken)
    {
        string? baseUrl, apiKey, model, wireApi;
        if (config.TitleMode == "current")
        {
            var codex = CodexConfigFile.Read();
            var providerId = string.IsNullOrWhiteSpace(providerHint) ? codex?.ModelProvider : providerHint;
            var provider = codex?.FindProvider(providerId);
            baseUrl = provider?.BaseUrl;
            apiKey = provider?.Token;
            wireApi = provider?.WireApi;
            model = string.IsNullOrWhiteSpace(modelHint) ? codex?.Model : modelHint;
        }
        else
        {
            baseUrl = config.TitleBaseUrl;
            apiKey = CredentialStore.Read(Program.TitleCredentialTarget);
            wireApi = config.TitleWireApi;
            model = config.TitleModel;
        }
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)) return null;
        wireApi = string.Equals(wireApi, "responses", StringComparison.OrdinalIgnoreCase) ? "responses" : "chat";
        var plans = new (bool Schema, string? Effort)[] { (true, "minimal"), (false, null) };
        for (var index = 0; index < plans.Length; index++)
        {
            var payload = BuildPayload(wireApi, model, prompt, plans[index].Schema, plans[index].Effort);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(baseUrl, wireApi))
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            using var response = await Client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
                return Extract(ReadContent(wireApi, body)) ?? throw new InvalidOperationException("Title API response did not contain a title.");
            if (index == plans.Length - 1 || response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity))
                throw new InvalidOperationException($"Title API returned HTTP {(int)response.StatusCode}: {Snippet(body)}");
            Program.Log($"Title API rejected the structured payload ({(int)response.StatusCode}); retrying with a reduced request.");
        }
        return null;
    }

    internal static string Endpoint(string baseUrl, string wireApi) =>
        baseUrl.Trim().TrimEnd('/') + (wireApi == "responses" ? "/responses" : "/chat/completions");

    internal static bool IsAvailable(Program.Config? config)
    {
        if (config is null) return false;
        return config.TitleMode switch
        {
            "custom" => config.TitleBaseUrl.Length > 0 && config.TitleModel.Length > 0,
            "current" => CodexConfigFile.Read()?.FindProvider(null)?.BaseUrl is { Length: > 0 },
            _ => false
        };
    }

    internal static Result? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                var root = JsonNode.Parse(trimmed[start..(end + 1)]);
                var title = Clean(root?["title"]?.GetValue<string>(), MaxTitleLength);
                if (title.Length > 0) return new Result(title, NullIfEmpty(Clean(root?["description"]?.GetValue<string>(), MaxDescriptionLength)));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { }
        }
        var fallback = Clean(trimmed, MaxTitleLength);
        return fallback.Length == 0 ? null : new Result(fallback, null);
    }

    private static JsonObject BuildPayload(string wireApi, string model, string prompt, bool schema, string? effort)
    {
        if (wireApi == "responses")
        {
            var responses = new JsonObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["input"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = prompt } }
                    }
                }
            };
            if (effort is not null) responses["reasoning"] = new JsonObject { ["effort"] = effort };
            if (schema) responses["text"] = new JsonObject { ["format"] = StructuredFormat() };
            return responses;
        }
        var chat = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } }
        };
        if (effort is not null) chat["reasoning_effort"] = effort;
        if (schema) chat["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = SchemaDefinition() };
        return chat;
    }

    private static JsonObject StructuredFormat() => new()
    {
        ["type"] = "json_schema",
        ["name"] = "thread_title",
        ["strict"] = true,
        ["schema"] = JsonNode.Parse(TitleSchemaJson)
    };

    private static JsonObject SchemaDefinition() => new()
    {
        ["name"] = "thread_title",
        ["strict"] = true,
        ["schema"] = JsonNode.Parse(TitleSchemaJson)
    };

    private static string? ReadContent(string wireApi, string text)
    {
        try
        {
            var root = JsonNode.Parse(text);
            if (wireApi == "responses")
            {
                foreach (var item in root?["output"]?.AsArray() ?? new JsonArray())
                    foreach (var part in item?["content"]?.AsArray() ?? new JsonArray())
                    {
                        var value = part?["text"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                return null;
            }
            var choices = root?["choices"]?.AsArray();
            var content = choices is { Count: > 0 } ? choices[0]?["message"]?["content"] : null;
            if (content is JsonValue single) return single.GetValue<string>();
            if (content is not JsonArray parts) return null;
            var builder = new StringBuilder();
            foreach (var part in parts) builder.Append(part?["text"]?.GetValue<string>() ?? "");
            return builder.ToString();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    private static string Clean(string? value, int maxLength)
    {
        var text = (value ?? "").Trim().Trim('\u0060', '"', '\'', '“', '”', '「', '」', '\r', '\n', ' ');
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string Snippet(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "…";
}

internal sealed record CodexProviderInfo(string? BaseUrl, string? Token, string WireApi);

internal sealed class CodexConfigFile(string? model, string? modelProvider, Dictionary<string, CodexProviderInfo> providers)
{
    internal string? Model { get; } = model;
    internal string? ModelProvider { get; } = modelProvider;

    internal CodexProviderInfo? FindProvider(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && providers.TryGetValue(id, out var direct)) return direct;
        return string.IsNullOrWhiteSpace(ModelProvider) ? null : providers.GetValueOrDefault(ModelProvider);
    }

    internal static CodexConfigFile? Read()
    {
        try
        {
            var path = Path.Combine(CodexHome(), "config.toml");
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception error)
        {
            Program.Log($"Codex config read failed: {error.Message}");
            return null;
        }
    }

    internal static string CodexHome()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(home) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : home;
    }

    internal static CodexConfigFile Parse(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                section = line.Trim('[', ']', ' ').Trim();
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..]);
            if (key.Length == 0 || value.Length == 0) continue;
            if (!sections.TryGetValue(section, out var bucket)) sections[section] = bucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bucket[key] = value;
        }
        var root = sections.GetValueOrDefault("");
        var providers = new Dictionary<string, CodexProviderInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bucket) in sections)
        {
            if (!name.StartsWith("model_providers.", StringComparison.OrdinalIgnoreCase)) continue;
            var id = name["model_providers.".Length..].Trim('"', ' ');
            var token = bucket.GetValueOrDefault("experimental_bearer_token");
            if (string.IsNullOrWhiteSpace(token) && bucket.TryGetValue("env_key", out var envKey) && !string.IsNullOrWhiteSpace(envKey))
                token = Environment.GetEnvironmentVariable(envKey);
            var wire = bucket.GetValueOrDefault("wire_api") ?? "responses";
            providers[id] = new CodexProviderInfo(
                bucket.GetValueOrDefault("base_url"),
                string.IsNullOrWhiteSpace(token) ? null : token,
                string.Equals(wire, "chat", StringComparison.OrdinalIgnoreCase) ? "chat" : "responses");
        }
        return new CodexConfigFile(root?.GetValueOrDefault("model"), root?.GetValueOrDefault("model_provider"), providers);
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.LastIndexOf('"');
            return end > 0 ? trimmed[1..end] : trimmed[1..];
        }
        var comment = trimmed.IndexOf('#');
        return (comment >= 0 ? trimmed[..comment] : trimmed).Trim();
    }
}
