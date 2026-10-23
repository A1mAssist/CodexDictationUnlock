using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class ContextPanel
{
    private const int CharsPerToken = 4;
    private const int BlockOverhead = 4;
    private const int RoleOverhead = 4;
    private const double ImagePxCoeff = 0.00101642;
    private const double ImageBaseTokens = 628.40;
    private const double ImageFallbackTokens = 384;
    private static readonly Regex ThreadIdPattern = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FileThreadIdPattern = new(
        "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\\.jsonl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly (string Key, string Label, string Zh, string Color)[] BandDefs =
    [
        ("system", "System prompt", "系统提示词", "#3b82f6"),
        ("inject", "Instructions", "指令", "#a855f7"),
        ("user", "You", "你的消息", "#22c55e"),
        ("assistant", "Visible replies", "可见回复", "#06b6d4"),
        ("reasoning", "Reasoning", "推理过程", "#ec4899"),
        ("compaction", "Compacted history", "压缩后保留历史", "#f59e0b"),
        ("tool", "Tool output", "工具输出", "#14b8a6"),
        ("untracked", "Tool + request schema", "工具 + 请求 schema", "#64748b"),
    ];
    private static readonly (string Key, string Label, string Zh, string Color) FreeBand = ("free", "Free", "剩余", "#3f3f46");
    private static readonly ConcurrentDictionary<string, ParseState> ParseCache = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record Turn(long? Ordinal, string Band, double Tokens, string? SubBand = null);
    internal sealed record KeptTurn(long? Ordinal, string Body, string Band);
    internal sealed record SessionMeta(string Id, string Cwd, string BaseInstructions);
    internal sealed record Usage(long? InputTokens, long? CachedInputTokens, long? OutputTokens, long? ReasoningOutputTokens, long? ContextWindow, long? TotalTokens);
    internal sealed record Compaction(long? Ordinal, int? WindowNumber, int RetainedUsers, int ReplacementItems, List<KeptTurn> Kept);
    private sealed class ParseState
    {
        internal long Offset;
        internal DateTime CreatedUtc;
        internal SessionMeta? Meta;
        internal readonly List<Turn> Turns = [];
        internal readonly List<Compaction> Compactions = [];
        internal readonly Queue<double> Growth = new();
        internal Usage? Usage;
        internal long? LastTotalTokens;
        internal long? TaskStartTotalTokens;

        internal void Reset()
        {
            Offset = 0;
            CreatedUtc = default;
            Meta = null;
            Turns.Clear();
            Compactions.Clear();
            Growth.Clear();
            Usage = null;
            LastTotalTokens = null;
            TaskStartTotalTokens = null;
        }
    }

    internal sealed record Rollout(SessionMeta? Meta, IReadOnlyList<Turn> Turns, Usage? Usage, IReadOnlyList<Compaction> Compactions,
        double? RecentGrowthTokens);
    internal sealed record ContextBand(string Key, string Label, string Zh, long Tokens, double Pct, string Color);
    internal sealed record ContextProvider(long? InputTokens, long? CachedInputTokens, long? OutputTokens, long? ReasoningOutputTokens,
        long? ContextWindow, long? UsedTokens, double? UsedPct);
    internal sealed record ContextCompaction(long? Ordinal, int? WindowNumber, int RetainedUsers, int ReplacementItems);
    internal sealed record ContextSlice(string Key, string Label, string Zh, long Tokens);
    internal sealed record ContextSnapshot(string ThreadId, string Name, string UpdatedAt, IReadOnlyList<ContextBand> Bands,
        IReadOnlyList<ContextSlice> InjectBands, ContextProvider Provider, IReadOnlyList<ContextCompaction> Compactions,
        int CompactionCount, long UntrackedTokens, double? RecentGrowthTokens, bool HasConversation);

    internal static string SessionsRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    internal static string? FindRollout(string threadId)
    {
        if (!ThreadIdPattern.IsMatch(threadId)) return null;
        try
        {
            // Paginated threads have one rollout per page, all containing the
            // parent id. The newest file is the page currently being written.
            string? newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var path in Directory.EnumerateFiles(SessionsRoot(), "*.jsonl", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(path).Contains(threadId, StringComparison.OrdinalIgnoreCase)) continue;
                var time = File.GetLastWriteTimeUtc(path);
                if (time <= newestTime) continue;
                newest = path;
                newestTime = time;
            }
            return newest;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    internal static ContextSnapshot? Build(string threadId)
    {
        var path = FindRollout(threadId);
        if (path is null) return null;
        var rollout = ReadRolloutCached(path);
        var live = LiveWindow(rollout.Turns, rollout.Compactions);
        var totals = BandTotals(live);
        // Compaction retags retained turns, so ask the raw transcript whether
        // the thread ever had a real exchange. Otherwise a compacted thread
        // would look empty and the panel would disappear.
        var hasConversation = rollout.Turns.Any(turn => turn.Band is "user" or "assistant");
        var provider = rollout.Usage;
        var window = provider?.ContextWindow;
        var used = window is > 0 && provider?.TotalTokens is long total ? Math.Min(total, window.Value) : 0;
        Dictionary<string, long> scaled;
        long untracked;
        if (used > 0)
        {
            (scaled, untracked) = ScaleBands(totals, used);
        }
        else
        {
            scaled = totals.ToDictionary(pair => pair.Key, pair => (long)Math.Round(pair.Value, MidpointRounding.AwayFromZero));
            untracked = 0;
        }
        var bandSum = scaled.Values.Sum();
        var windowTokens = window is > 0 && used > 0 ? window.Value : bandSum;
        var free = window is > 0 && used > 0 && window.Value > used ? window.Value - used : 0;
        var bands = BandDefs.Where(band => scaled.GetValueOrDefault(band.Key) > 0)
            .Select(band => new ContextBand(band.Key, band.Label, band.Zh, scaled[band.Key],
                windowTokens == 0 ? 0 : Math.Round(scaled[band.Key] * 100.0 / windowTokens, 1), band.Color))
            .ToList();
        if (free > 0)
            bands.Add(new ContextBand(FreeBand.Key, FreeBand.Label, FreeBand.Zh, free,
                Math.Round(free * 100.0 / windowTokens, 1), FreeBand.Color));
        var injectBands = InjectBreakdown(live, bands.FirstOrDefault(band => band.Key == "inject"));
        var cwd = rollout.Meta?.Cwd ?? "";
        var name = cwd.Length == 0 ? "" : Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return new ContextSnapshot(
            threadId,
            name,
            LastWrite(path),
            bands,
            injectBands,
            new ContextProvider(provider?.InputTokens, provider?.CachedInputTokens, provider?.OutputTokens,
                provider?.ReasoningOutputTokens, window, used,
                window is > 0 ? Math.Round(used * 100.0 / window.Value, 1) : null),
            rollout.Compactions.Select(item => new ContextCompaction(item.Ordinal, item.WindowNumber, item.RetainedUsers, item.ReplacementItems)).ToArray(),
            rollout.Compactions.Count,
            untracked,
            rollout.RecentGrowthTokens,
            hasConversation);
    }

    internal static double TextTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Ceiling(text.Length / (double)CharsPerToken) + BlockOverhead;

    internal static double SystemTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Ceiling(text.Length / (double)CharsPerToken) + RoleOverhead;

    internal static double? EstimateImageTokens(int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        return ImagePxCoeff * ((long)width * height) + ImageBaseTokens;
    }

    internal static double? ImageTokensFromDataUrl(string url)
    {
        var marker = url.IndexOf("base64,", StringComparison.Ordinal);
        if (marker < 0) return null;
        try
        {
            // A PNG header is 24 bytes, so decode only the start of the payload.
            // Decoding a multi-megabyte data URL just to read width/height is waste.
            var payload = url[(marker + 7)..];
            var headerLength = Math.Min(payload.Length, 32);
            headerLength -= headerLength % 4;
            if (headerLength < 24) return ImageFallbackTokens;
            var raw = Convert.FromBase64String(payload[..headerLength]);
            if (raw.Length < 24 || !raw.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }) ||
                !raw.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                return ImageFallbackTokens;
            return EstimateImageTokens((int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(16, 4)),
                (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(20, 4)));
        }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    internal static double OutputTokens(JsonElement output)
    {
        if (output.ValueKind == JsonValueKind.String)
        {
            var text = output.GetString() ?? "";
            var image = text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ? ImageTokensFromDataUrl(text) : null;
            return image ?? TextTokens(text);
        }
        if (output.ValueKind == JsonValueKind.Array)
        {
            var total = 0.0;
            foreach (var part in output.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    total += TextTokens(part.GetRawText());
                    continue;
                }
                var type = GetString(part, "type");
                if (type == "input_image")
                {
                    var url = GetString(part, "image_url");
                    total += (url is null ? null : ImageTokensFromDataUrl(url)) ?? ImageFallbackTokens;
                }
                else if (type == "input_text")
                {
                    total += TextTokens(GetString(part, "text"));
                }
                else
                {
                    total += TextTokens(part.GetRawText());
                }
            }
            return total;
        }
        return TextTokens(output.GetRawText());
    }

    internal static List<Turn> LiveWindow(IReadOnlyList<Turn> turns, IReadOnlyList<Compaction> compactions)
    {
        var always = turns.Where(turn => turn.Band == "system").ToList();
        if (compactions.Count == 0) return always.Concat(turns.Where(turn => turn.Band != "system")).ToList();
        var last = compactions[^1];
        var cut = last.Ordinal;
        // The replacement history is the compaction window's retained content.
        // Keep it separate so the bar can show the compacted history as its own band.
        var kept = last.Kept.Select(item => new Turn(item.Ordinal, "compaction", TextTokens(item.Body))).ToList();
        var after = turns.Where(turn => turn.Band != "system" && (cut is not long ordinal || turn.Ordinal is not long current || current > ordinal));
        return always.Concat(kept).Concat(after).ToList();
    }

    internal static Dictionary<string, double> BandTotals(IEnumerable<Turn> turns)
    {
        var totals = BandDefs.ToDictionary(band => band.Key, _ => 0.0);
        foreach (var turn in turns) totals[turn.Band] = totals.GetValueOrDefault(turn.Band) + turn.Tokens;
        return totals;
    }

    internal static (Dictionary<string, long> Scaled, long Untracked) ScaleBands(IReadOnlyDictionary<string, double> totals, long total)
    {
        var measured = totals.Where(pair => pair.Key != "untracked").ToDictionary(pair => pair.Key, pair => pair.Value);
        var weight = measured.Values.Sum();
        if (weight <= 0 || total <= 0)
        {
            return (totals.ToDictionary(pair => pair.Key, pair => (long)Math.Round(pair.Value, MidpointRounding.AwayFromZero)), 0);
        }
        var untracked = (long)Math.Max(0, Math.Round(total - weight, MidpointRounding.AwayFromZero));
        var tracked = total - untracked;
        var scaled = totals.Keys.ToDictionary(key => key, _ => 0L);
        foreach (var (key, value) in measured)
            scaled[key] = (long)Math.Round(value / weight * tracked, MidpointRounding.AwayFromZero);
        var drift = scaled.Values.Sum() - tracked;
        if (drift != 0 && measured.Count > 0)
        {
            var largest = measured.Keys.MaxBy(key => scaled[key])!;
            scaled[largest] = Math.Max(0, scaled[largest] - drift);
        }
        scaled["untracked"] = untracked;
        return (scaled, scaled["untracked"]);
    }

    internal static IReadOnlyList<ContextSlice> InjectBreakdown(IReadOnlyList<Turn> live, ContextBand? injectBand)
    {
        if (injectBand is null || injectBand.Tokens <= 0) return [];
        var raw = live.Where(turn => turn.Band == "inject" && turn.SubBand is not null)
            .GroupBy(turn => turn.SubBand!, StringComparer.Ordinal)
            .Select(group => (Key: group.Key, Tokens: group.Sum(turn => turn.Tokens)))
            .Where(item => item.Tokens > 0)
            .ToList();
        var rawTotal = raw.Sum(item => item.Tokens);
        if (rawTotal <= 0) return [];
        var slices = raw
            .Select(item =>
            {
                var label = InjectLabel(item.Key);
                return new ContextSlice(item.Key, label.Label, label.Zh,
                    (long)Math.Round(item.Tokens * injectBand.Tokens / rawTotal));
            })
            .OrderByDescending(slice => slice.Tokens)
            .ToList();
        var drift = slices.Sum(slice => slice.Tokens) - injectBand.Tokens;
        if (drift != 0)
            slices[0] = slices[0] with { Tokens = Math.Max(0, slices[0].Tokens - drift) };
        return slices;
    }

    private static string InjectCategory(string text)
    {
        if (text.StartsWith("<app-context>", StringComparison.Ordinal)) return "app";
        if (text.Contains("<skills_instructions>", StringComparison.Ordinal) ||
            text.Contains("## Skills", StringComparison.Ordinal)) return "skills";
        if (text.StartsWith("<gpt-with-taste-user-steer>", StringComparison.Ordinal)) return "steer";
        if (text.StartsWith("<model_switch>", StringComparison.Ordinal)) return "model";
        if (text.StartsWith("PONYTAIL MODE", StringComparison.Ordinal)) return "ponytail";
        if (text.StartsWith("<codex_apps_client_time_context>", StringComparison.Ordinal)) return "time";
        if (text.StartsWith("<collaboration_mode>", StringComparison.Ordinal) ||
            text.StartsWith("<multi_agent", StringComparison.Ordinal)) return "mode";
        return "other";
    }

    private static (string Label, string Zh) InjectLabel(string key) => key switch
    {
        "app" => ("App context", "应用上下文"),
        "skills" => ("Skills", "技能"),
        "steer" => ("User steer", "每轮 steer"),
        "model" => ("Model switch", "模型切换"),
        "ponytail" => ("Ponytail", "Ponytail"),
        "time" => ("Time", "时间"),
        "mode" => ("Mode", "模式"),
        _ => ("Other", "其他"),
    };

    internal static Rollout ReadRollout(string path)
    {
        var state = new ParseState();
        foreach (var line in File.ReadLines(path))
            ConsumeLine(state, line);
        return Snapshot(state);
    }

    internal static Rollout ReadRolloutCached(string path)
    {
        var state = ParseCache.GetOrAdd(path, _ => new ParseState());
        lock (state)
        {
            long length;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return Snapshot(state);
                var created = info.CreationTimeUtc;
                if (state.CreatedUtc != default && created != state.CreatedUtc) state.Reset();
                state.CreatedUtc = created;
                length = info.Length;
            }
            catch (IOException) { return Snapshot(state); }
            catch (UnauthorizedAccessException) { return Snapshot(state); }
            if (length < state.Offset) state.Reset();
            if (length == state.Offset) return Snapshot(state);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(state.Offset, SeekOrigin.Begin);
                var pending = new MemoryStream();
                var buffer = new byte[4 * 1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    pending.Write(buffer, 0, read);
                    var data = pending.GetBuffer();
                    var count = checked((int)pending.Length);
                    var start = 0;
                    for (var index = 0; index < count; index++)
                    {
                        if (data[index] != (byte)'\n') continue;
                        ConsumeLine(state, data.AsMemory(start, index - start));
                        state.Offset += index - start + 1;
                        start = index + 1;
                    }
                    if (start == 0) continue;
                    var remaining = count - start;
                    var next = new MemoryStream(remaining);
                    if (remaining > 0) next.Write(data, start, remaining);
                    pending.Dispose();
                    pending = next;
                }
                pending.Dispose();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Snapshot(state);
        }
    }

    private static string LastWrite(string path)
    {
        try { return File.GetLastWriteTime(path).ToString("s", CultureInfo.InvariantCulture); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
    private static Rollout Snapshot(ParseState state) =>
        new(state.Meta, state.Turns.ToArray(), state.Usage, state.Compactions.ToArray(),
            state.Growth.Count == 0 ? null : state.Growth.Average());

    private static void ConsumeLine(ParseState state, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            ConsumeRoot(state, document.RootElement);
        }
        catch (JsonException) { }
    }

    private static void ConsumeLine(ParseState state, ReadOnlyMemory<byte> line)
    {
        if (line.IsEmpty) return;
        try
        {
            using var document = JsonDocument.Parse(line);
            ConsumeRoot(state, document.RootElement);
        }
        catch (JsonException) { }
    }

    private static void ConsumeRoot(ParseState state, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var kind = GetString(root, "type");
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
        var ordinal = GetLong(root, "ordinal");
        switch (kind)
        {
            case "session_meta":
            {
                var baseText = "";
                if (payload.TryGetProperty("base_instructions", out var instructions) && instructions.ValueKind == JsonValueKind.Object)
                    baseText = GetString(instructions, "text") ?? "";
                state.Meta = new SessionMeta(GetString(payload, "id") ?? "", GetString(payload, "cwd") ?? "", baseText);
                state.Turns.Add(new Turn(ordinal, "system", SystemTokens(baseText)));
                break;
            }
            case "compacted":
            {
                var kept = new List<KeptTurn>();
                var replacementItems = 0;
                if (payload.TryGetProperty("replacement_history", out var replacement) && replacement.ValueKind == JsonValueKind.Array)
                {
                    replacementItems = replacement.GetArrayLength();
                    foreach (var item in replacement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || GetString(item, "type") != "message") continue;
                        kept.Add(new KeptTurn(ordinal, MessageText(item), RoleBand(GetString(item, "role"))));
                    }
                }
                var retainedUsers = 0;
                if (payload.TryGetProperty("retained_context", out var retained) && retained.ValueKind == JsonValueKind.Object &&
                    retained.TryGetProperty("user_messages", out var users) && users.ValueKind == JsonValueKind.Array)
                    retainedUsers = users.GetArrayLength();
                state.Compactions.Add(new Compaction(ordinal, GetInt(payload, "window_number"), retainedUsers, replacementItems, kept));
                break;
            }
            case "event_msg" when GetString(payload, "type") == "task_started":
            {
                state.TaskStartTotalTokens = state.LastTotalTokens;
                break;
            }
            case "event_msg" when GetString(payload, "type") is "task_complete" or "turn_aborted":
            {
                if (state.TaskStartTotalTokens is long start && state.LastTotalTokens is long end && end > start)
                {
                    state.Growth.Enqueue(end - start);
                    while (state.Growth.Count > 5) state.Growth.Dequeue();
                }
                state.TaskStartTotalTokens = null;
                break;
            }
            case "event_msg" when GetString(payload, "type") == "token_count":
            {
                if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) break;
                var last = info.TryGetProperty("last_token_usage", out var usage) && usage.ValueKind == JsonValueKind.Object ? usage : default;
                var total = GetLong(last, "total_tokens");
                if (total is long current) state.LastTotalTokens = current;
                state.Usage = new Usage(GetLong(last, "input_tokens"), GetLong(last, "cached_input_tokens"),
                    GetLong(last, "output_tokens"), GetLong(last, "reasoning_output_tokens"),
                    GetLong(info, "model_context_window"), total);
                break;
            }
            case "response_item":
            {
                var itemType = GetString(payload, "type");
                if (itemType == "message")
                {
                    var text = MessageText(payload);
                    var band = RoleBand(GetString(payload, "role"));
                    state.Turns.Add(new Turn(ordinal, band, TextTokens(text),
                        band == "inject" ? InjectCategory(text) : null));
                }
                else if (itemType == "reasoning")
                {
                    state.Turns.Add(new Turn(ordinal, "reasoning", TextTokens(ReasoningText(payload))));
                }
                else if (itemType is "function_call" or "custom_tool_call")
                {
                    state.Turns.Add(new Turn(ordinal, "tool", TextTokens(ToolCallText(payload))));
                }
                else if (itemType is "function_call_output" or "custom_tool_call_output")
                {
                    state.Turns.Add(new Turn(ordinal, "tool", payload.TryGetProperty("output", out var output) ? OutputTokens(output) : 0));
                }
                break;
            }
        }
    }

    private static string RoleBand(string? role) => role switch
    {
        "developer" => "inject",
        "user" => "user",
        "assistant" => "assistant",
        _ => "inject",
    };

    private static string MessageText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
            if (part.ValueKind == JsonValueKind.Object && GetString(part, "text") is { } value) text.Append(value);
        return text.ToString();
    }

    private static string ReasoningText(JsonElement item)
    {
        if (!item.TryGetProperty("summary", out var parts) || parts.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            item.TryGetProperty("summary_text", out parts);
        if (parts.ValueKind == JsonValueKind.String) return parts.GetString() ?? "";
        if (parts.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String) text.Append(part.GetString());
            else if (part.ValueKind == JsonValueKind.Object && GetString(part, "text") is { } value) text.Append(value);
            else text.Append(part.GetRawText());
        }
        return text.ToString();
    }

    private static string ToolCallText(JsonElement item)
    {
        if (item.TryGetProperty("arguments", out var arguments) && arguments.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            var value = arguments.ValueKind == JsonValueKind.String ? arguments.GetString() : arguments.GetRawText();
            if (!string.IsNullOrEmpty(value)) return value;
        }
        if (!item.TryGetProperty("input", out var input)) return "";
        return input.ValueKind == JsonValueKind.String ? input.GetString() ?? "" : input.GetRawText();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number : null;

    internal static void SelfTest()
    {
        Check("text tokens", TextTokens(new string('a', 40)), 14.0);
        Check("empty text", TextTokens(""), 0.0);
        Check("system tokens", SystemTokens(new string('a', 40)), 14.0);
        Check("image price", Math.Round(EstimateImageTokens(1200, 2048)!.Value), 3126.0);
        Check("small image is cheaper", EstimateImageTokens(100, 100) < EstimateImageTokens(4000, 4000), true);
        Check("1MB base64 is not text", ImageTokensFromDataUrl("data:image/png;base64," + new string('A', 1_000_000)) < 400, true);
        var png = new byte[24];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(png, 0);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 1200);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 2048);
        Check("png image price", Math.Round(ImageTokensFromDataUrl("data:image/png;base64," + Convert.ToBase64String(png))!.Value), 3126.0);
        using var stringImage = JsonDocument.Parse(JsonSerializer.Serialize("data:image/png;base64," + Convert.ToBase64String(png)));
        Check("string image price", Math.Round(OutputTokens(stringImage.RootElement)), 3126.0);

        Check("empty conversation has no messages", LiveWindow([new Turn(0, "system", 100)], []).Any(turn => turn.Band is "user" or "assistant"), false);
        var turns = new List<Turn> { new(0, "system", 100), new(1, "user", 50), new(2, "tool", 999), new(3, "user", 70) };
        var compactions = new List<Compaction> { new(2, 1, 0, 1, [new KeptTurn(2, "kept body", "user")]) };
        var live = LiveWindow(turns, compactions);
        var bands = BandTotals(live);
        Check("system survives compaction", bands["system"], 100.0);
        Check("pre-compaction tool output is dropped", bands["tool"], 0.0);
        Check("replacement history is counted", live.Any(turn => turn.Ordinal == 2 && turn.Band == "compaction" && turn.Tokens == TextTokens("kept body")), true);
        Check("user band after compaction", bands["user"], 70.0);
        Check("compaction band after compaction", bands["compaction"], TextTokens("kept body"));
        bands = BandTotals(LiveWindow(turns, []));
        Check("no compaction keeps tool output", bands["tool"], 999.0);
        Check("no compaction keeps user text", bands["user"], 120.0);

        var (scaled, untracked) = ScaleBands(new Dictionary<string, double> { ["system"] = 100, ["user"] = 300, ["tool"] = 600 }, 2000);
        Check("scaled bands add up", scaled.Values.Sum(), 2000L);
        Check("untracked is the gap", scaled["untracked"], 1000L);
        Check("scaled system band", scaled["system"], 100L);
        (scaled, _) = ScaleBands(new Dictionary<string, double> { ["system"] = 1000, ["user"] = 1000 }, 2000);
        Check("nothing untracked when complete", scaled["untracked"], 0L);
        Check("complete bands add up", scaled.Values.Sum(), 2000L);
        Check("unused untracked", untracked, 1000L);
        var injectBand = new ContextBand("inject", "Injected", "注入内容", 150, 100, "#a855f7");
        var injectSlices = InjectBreakdown([new Turn(1, "inject", 100, "skills"), new Turn(2, "inject", 50, "steer")], injectBand);
        Check("inject breakdown sums", injectSlices.Sum(slice => slice.Tokens), 150L);
        Check("inject breakdown keeps the largest first", injectSlices[0].Key, "skills");

        var temp = Path.Combine(Path.GetTempPath(), $"codex-context-{Guid.NewGuid():N}.jsonl");
        try
        {
            var meta = JsonSerializer.Serialize(new { type = "session_meta", ordinal = 0, payload = new { id = "01a15d4d-caa0-7921-80b7-14b75081853c", cwd = @"D:\demo", base_instructions = new { text = new string('a', 40) } } });
            var user = JsonSerializer.Serialize(new { type = "response_item", ordinal = 1, payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = new string('b', 40) } } } });
            var usage = JsonSerializer.Serialize(new { type = "event_msg", ordinal = 2, payload = new { type = "token_count", info = new { last_token_usage = new { input_tokens = 10, total_tokens = 20 }, model_context_window = 100 } } });
            var started = JsonSerializer.Serialize(new { type = "event_msg", ordinal = 3, payload = new { type = "task_started" } });
            var usage2 = JsonSerializer.Serialize(new { type = "event_msg", ordinal = 4, payload = new { type = "token_count", info = new { last_token_usage = new { input_tokens = 20, total_tokens = 50 }, model_context_window = 100 } } });
            var complete = JsonSerializer.Serialize(new { type = "event_msg", ordinal = 5, payload = new { type = "task_complete" } });
            File.WriteAllText(temp, meta + "\n" + user + "\n");
            var first = ReadRolloutCached(temp);
            File.AppendAllText(temp, usage + "\n" + started + "\n" + usage2 + "\n" + complete + "\n");
            var cached = ReadRolloutCached(temp);
            var full = ReadRollout(temp);
            Check("incremental meta", cached.Meta?.Id, full.Meta?.Id);
            Check("incremental usage", cached.Usage?.TotalTokens, full.Usage?.TotalTokens);
            Check("incremental growth", cached.RecentGrowthTokens, 30d);
            Check("incremental turns", cached.Turns.Count, full.Turns.Count);
            Check("incremental compaction count", cached.Compactions.Count, full.Compactions.Count);
            Check("initial parse", first.Turns.Count, 2);
        }
        finally { try { File.Delete(temp); } catch { } }

        var checkedRollouts = 0;
        var hasSessions = Directory.Exists(SessionsRoot());
        if (hasSessions)
        {
            foreach (var path in Directory.EnumerateFiles(SessionsRoot(), "*.jsonl", SearchOption.AllDirectories)
                         .Select(path => (Path: path, Time: File.GetLastWriteTimeUtc(path)))
                         .OrderByDescending(item => item.Time)
                         .Take(3))
            {
                var match = FileThreadIdPattern.Match(Path.GetFileName(path.Path));
                if (!match.Success) continue;
                var context = Build(match.Groups[1].Value);
                if (context is null || context.Provider.UsedTokens is not > 0 || context.Provider.ContextWindow is not > 0) continue;
                Check("real rollout has conversation", context.HasConversation, true);
                Check("real rollout used bands add up", context.Bands.Where(band => band.Key != "free").Sum(band => band.Tokens), context.Provider.UsedTokens);
                Check("real rollout full window adds up", context.Bands.Sum(band => band.Tokens), context.Provider.ContextWindow);
                Check("real rollout provider percent", context.Provider.UsedPct,
                    Math.Round(context.Provider.UsedTokens.Value * 100.0 / context.Provider.ContextWindow.Value, 1));
                checkedRollouts++;
            }
        }
        Check("real rollouts checked", !hasSessions || checkedRollouts > 0, true);
    }

    private static void Check<T>(string label, T actual, T expected)
    {
        if (EqualityComparer<T>.Default.Equals(actual, expected)) return;
        throw new Exception($"Context panel self-test failed: {label} ({actual} != {expected})");
    }
}
