using System.Diagnostics;
using System.Collections.Concurrent;
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
    private const string CredentialTarget = "CodexDictation.Aliyun.ApiKey";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConditionalWeakTable<ClientWebSocket, CdpInbox> CdpInboxes = new();
    private static readonly ConcurrentDictionary<string, byte> VoiceWatchers = new(StringComparer.Ordinal);

    private sealed class CdpInbox
    {
        internal Queue<JsonNode> Messages { get; set; } = new();
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--self-test"]) return SelfTest();
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
        using var instance = new Mutex(true, @"Local\CodexDictation", out var ownsMutex);
        if (!ownsMutex) throw new InvalidOperationException("Codex Dictation is already running.");
        await CloseExistingCodexAsync();
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
            var hasApiKey = CredentialStore.Read(CredentialTarget) is not null;
            await context.Response.WriteAsJsonAsync(new
            {
                workspaceId = config?.WorkspaceId ?? "",
                language = config?.Language ?? "zh",
                dictionary = config?.Dictionary ?? Array.Empty<string>(),
                hasApiKey,
                ready = config is not null && hasApiKey
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
                ValidateWorkspaceId(request.WorkspaceId);
                var apiKey = request.ApiKey?.Trim() ?? "";
                if (apiKey.Length > 0)
                {
                    if (apiKey.Length is < 8 or > 1024) throw new InvalidDataException("API key length is invalid.");
                    CredentialStore.Write(CredentialTarget, apiKey);
                }
                else if (CredentialStore.Read(CredentialTarget) is null) throw new InvalidDataException("API key is required.");
                var dictionary = (request.Dictionary ?? Array.Empty<string>())
                    .Select(item => item.Trim())
                    .Where(item => item.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Take(200)
                    .ToArray();
                SaveConfig(new Config(request.WorkspaceId, "zh", dictionary));
                await context.Response.WriteAsJsonAsync(new { ready = true });
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = error.Message });
            }
        });
        app.Map("/dictation", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest || !HasProtocol(context, "chatgpt-dictation") || !HasProtocol(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var config = LoadConfigOrNull();
            var apiKey = CredentialStore.Read(CredentialTarget);
            if (config is null || apiKey is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync("chatgpt-dictation");
            await new DictationSession(socket, config, apiKey).RunAsync(context.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single()
            ?? throw new InvalidOperationException("Local dictation listener did not start.");
        var localPort = new Uri(address).Port;
        Log($"Helper listening on http://127.0.0.1:{localPort}.");
        var debugPort = FreeLoopbackPort();
        ActivateCodex(debugPort);
        await InjectLoopAsync(debugPort, localPort, token, app.Lifetime.ApplicationStopping);
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
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static async Task WaitForCodexExitAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && CodexIsRunning())
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
    }

    private static bool CodexIsRunning() => GetCodexProcesses().Length > 0;

    private static Process[] GetCodexProcesses() =>
        Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("Codex")).ToArray();

    private static async Task CloseExistingCodexAsync()
    {
        var processes = GetCodexProcesses();
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) process.CloseMainWindow();
            }
            catch { }
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && CodexIsRunning()) await Task.Delay(250);
        if (!CodexIsRunning()) return;
        foreach (var process in GetCodexProcesses())
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        }
        while (CodexIsRunning()) await Task.Delay(100);
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
        await SendCdpAsync(socket, 7, "Runtime.enable", new { }, token);
        await WaitForCdpResponseAsync(socket, 7, token);
        await SendCdpAsync(socket, 6, "Runtime.evaluate", new { expression = "document.readyState", returnByValue = true }, token);
        var readyState = (await WaitForCdpResponseAsync(socket, 6, token))["result"]?["result"]?["value"]?.GetValue<string>();
        if (!string.Equals(readyState, "loading", StringComparison.OrdinalIgnoreCase)) return;
        await SendCdpAsync(socket, 9, "Network.enable", new { }, token);
        await WaitForCdpResponseAsync(socket, 9, token);
        await SendCdpAsync(socket, 10, "Network.setCacheDisabled", new { cacheDisabled = true }, token);
        await WaitForCdpResponseAsync(socket, 10, token);
        await SendCdpAsync(socket, 8, "Page.setBypassCSP", new { enabled = true }, token);
        await WaitForCdpResponseAsync(socket, 8, token);
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
        const string keepVisibleMutation = "m=e=>{s.mutate({keepVisible:e})}";
        const string keepVisibleFallback = "m=e=>{let t=G(`global-dictation-hotkey-state`);i.setQueryData(t,{...(n??{}),keepVisible:e});s.mutate({keepVisible:e})}";
        return source.Replace(keepVisibleMutation, keepVisibleFallback, StringComparison.Ordinal);
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
        await SendCdpAsync(socket, 9, "Runtime.evaluate", new { expression = "document.readyState", returnByValue = true }, token);
        var readyState = (await WaitForCdpResponseAsync(socket, 9, token))["result"]?["result"]?["value"]?.GetValue<string>();
        if (!string.Equals(readyState, "loading", StringComparison.OrdinalIgnoreCase)) return;
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
        var args = $"--remote-debugging-port={debugPort} --remote-allow-origins=http://127.0.0.1:{debugPort}";
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
        config = config with { Dictionary = config.Dictionary ?? Array.Empty<string>() };
        ValidateWorkspaceId(config.WorkspaceId);
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
    private static void ValidateWorkspaceId(string? value)
    {
        if (value is null || value.Length is < 8 or > 128 || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-')))
            throw new ArgumentException("Invalid Aliyun WorkspaceId.");
    }

    private static void HideConsole()
    {
        if (OperatingSystem.IsWindows()) ShowWindow(GetConsoleWindow(), 0);
    }

    private static void Log(string message)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexDictation");
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "helper.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }

    private static int SelfTest()
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
        var gateSource = "return{isLoading:a,isError:!1,isCapable:!a&&n&&i===`chatgpt`} streamingEnabled:n return{isLoading:t,isError:!1,isCapable:!t&&(n!=null||i===!1)&&(n!==`chatgpt`||r!==!1)} n==null||n.configuredHotkey==null&&n.configuredToggleHotkey==null||s.isPending m=e=>{s.mutate({keepVisible:e})}";
        var patchedGate = PatchDictationSource(gateSource);
        if (!patchedGate.Contains("isCapable:!a", StringComparison.Ordinal) || !patchedGate.Contains("streamingEnabled:!0", StringComparison.Ordinal) || !patchedGate.Contains("isCapable:!t}", StringComparison.Ordinal) || patchedGate.Contains("configuredHotkey==null", StringComparison.Ordinal) || !patchedGate.Contains("s.isPending", StringComparison.Ordinal) || !patchedGate.Contains("setQueryData(t", StringComparison.Ordinal)) throw new Exception("Dictation gate patch is invalid.");
        if (!DictationSession.StartedEvent("session", 1).Contains("transcript_delivery_mode\":\"delta", StringComparison.Ordinal)) throw new Exception("Streaming transcript mode is invalid.");
        var activationManager = (IApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))!)!;
        Marshal.ReleaseComObject(activationManager);
        var injection = LoadInjectionScript(12345, "codex-dictation.test");
        if (injection.Contains("__CONNECT_INFO__", StringComparison.Ordinal) || injection.Contains("__HELPER_CONFIG__", StringComparison.Ordinal) ||
            !injection.Contains("ws://127.0.0.1:12345/dictation", StringComparison.Ordinal) || !injection.Contains("http://127.0.0.1:12345/config", StringComparison.Ordinal))
            throw new Exception("Injection script substitution failed.");
        if (!injection.Contains("insertText(next", StringComparison.Ordinal) || !injection.Contains("clearPreview", StringComparison.Ordinal))
            throw new Exception("Native dictation preview bridge is missing.");
        Console.WriteLine("Self-test passed.");
        return 0;
    }

    internal sealed record Config(string WorkspaceId, string Language, string[] Dictionary);
    private sealed record ConfigRequest(string WorkspaceId, string? ApiKey, string[]? Dictionary);

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);

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

            using var upstream = new ClientWebSocket();
            upstream.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            upstream.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
            var uri = new Uri($"wss://{config.WorkspaceId}.cn-beijing.maas.aliyuncs.com/api-ws/v1/realtime?model=qwen3-asr-flash-realtime");
            await upstream.ConnectAsync(uri, linked.Token);
            await SendJsonAsync(upstream, new
            {
                event_id = EventId(),
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text" },
                    input_audio_format = "pcm",
                    sample_rate = 16000,
                    input_audio_transcription = new
                    {
                        language = config.Language,
                        corpus = config.Dictionary.Length == 0 ? null : new { text = string.Join("、", config.Dictionary) }
                    },
                    turn_detection = new { type = "server_vad", threshold = 0.0, silence_duration_ms = 500 }
                }
            }, linked.Token);
            await WaitForUpstreamReadyAsync(upstream, linked.Token);
            await SendClientAsync(StartedEvent(_sessionId, NextSequence()), linked.Token);

            var resampler = new Pcm16Resampler(inputRate, 16000);
            var upstreamEvents = RelayUpstreamAsync(upstream, linked.Token);
            while (true)
            {
                var text = await Program.ReceiveTextAsync(client, MaxClientMessageBytes, linked.Token);
                if (text is null) break;
                using var message = JsonDocument.Parse(text);
                var type = message.RootElement.GetProperty("type").GetString();
                if (type == "audio.append")
                {
                    var audio = Convert.FromBase64String(message.RootElement.GetProperty("audio").GetString() ?? "");
                    if (audio.Length > 4 * 1024 * 1024 || (audio.Length & 1) != 0) throw new InvalidDataException("Invalid PCM16 audio chunk.");
                    var converted = resampler.Process(audio);
                    if (converted.Length > 0) await SendJsonAsync(upstream, new { event_id = EventId(), type = "input_audio_buffer.append", audio = Convert.ToBase64String(converted) }, linked.Token);
                }
                else if (type == "session.close")
                {
                    await SendJsonAsync(upstream, new { event_id = EventId(), type = "session.finish" }, linked.Token);
                    using var finishTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    finishTimeout.CancelAfter(TimeSpan.FromSeconds(7));
                    await upstreamEvents.WaitAsync(finishTimeout.Token);
                    return;
                }
                else throw new InvalidDataException($"Unsupported client event: {type}");
            }
            linked.Cancel();
            await IgnoreCancellation(upstreamEvents);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await TrySendClientErrorAsync(error.Message, cancellationToken);
        }
        finally
        {
            linked.Cancel();
            if (client.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None);
        }
    }

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
                    type = "transcript.delta",
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
        session = new { session_id = sessionId, status = "active", config = new { provider_mode = "streaming_sse", transcript_delivery_mode = "delta" } }
    });

    internal static string ClosedEvent(string sessionId, int sequence) => JsonSerializer.Serialize(new
    {
        type = "session.updated",
        sequence_no = sequence,
        session = new { session_id = sessionId, status = "closed", config = new { provider_mode = "streaming_sse", transcript_delivery_mode = "delta" } }
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
