# CodexDictationUnlock

中文 | [English](#english)

## 中文

为 Windows 版 Codex Desktop 的 API Key 登录解锁原生 Dictation，并把音频转发到兼容的实时 ASR 服务。

### 当前版本

首个公开版本：`v1.0.0`；当前修复版本：`v1.0.11`

适配的 ChatGPT Desktop（Codex）客户端版本：`26.911.7940.0`（Microsoft Store 包 `OpenAI.Codex_26.911.7940.0_x64__2p2nqsd0c76g0`）。客户端更新后，注入点可能需要重新验证。

### 支持的 ASR 服务

| 服务商 | 模型 | 设置页需要填写 |
| --- | --- | --- |
| 阿里云 DashScope | `qwen3-asr-flash-realtime` | Workspace ID、API Key |
| 火山引擎 | 豆包大模型双向流式 | Resource ID、API Key |

火山引擎使用官方双向流式接口 `wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async`。默认 Resource ID 是 `volc.seedasr.sauc.duration`，也可以在 Voice 设置中填写账号实际分配的 Resource ID。

### 功能

- 使用 Codex 原生 Voice 设置和听写输入框，不提供额外的前台窗口。
- 兼容主对话输入框与 Annotation 评论输入框，听写结果写入当前焦点编辑器。
- 实时发送 16 kHz、16-bit、单声道 PCM 音频。
- 转发实时增量识别结果和分句最终结果。
- 将 Codex Voice 页面中的听写词典作为 ASR 热词上下文提交。
- API Key 使用 Windows Credential Manager 保存，不写入 `config.json`。
- 设置卡片跟随 Codex 当前语言显示中文或英文。
- 在 Voice 设置中显示当前生效的 `model_provider`，允许自定义 Provider 修改模型响应流重连次数；内置 `openai`、`ollama`、`lmstudio` 保持 Codex 默认值 5。
- 可在 Voice 设置中用自定义接口或当前对话模型接管新会话标题生成，绕开中转 API 对 `gpt-5.6-luna` 的屏蔽。

### 使用发布版

1. 从 [Releases](https://github.com/A1mAssist/CodexDictationUnlock/releases) 下载 `CodexDictation.exe`。
2. 如果 Codex 已在运行，启动时 Helper 会弹窗询问是否关闭并重新注入；无需提前手动结束。
3. 双击 `CodexDictation.exe`。Helper 会启动带调试端口的 Codex，并自动注入补丁。
4. 打开 Codex 的 `Settings > Voice`，找到 `Dictation ASR` 配置卡片。
5. 选择服务商：
   - 阿里云：填写 Workspace ID 和 DashScope API Key。
   - 火山引擎：填写 Resource ID 和火山引擎 App Key。
6. 保存配置，并在 Voice 设置中设置 Dictation hotkey。
7. 使用 Codex 原生听写入口开始录音。
8. 如需调整模型响应流重连次数，在同一 Voice 页面修改“模型响应流重连”；保存后从下一次请求生效。
9. 需要接管新会话标题时，在同一页面底部展开“会话标题生成”，选择“自定义接口”或“当前对话模型”并保存。

Helper 没有独立的前台 UI。运行日志位于 `%APPDATA%\CodexDictation\helper.log`。
当 Codex 退出后，Helper 会自动停止；下次可以直接再次双击 exe 启动。
如果启动时检测到已有 ChatGPT 进程，会先询问是否关闭并重新启动注入；选择“否”不会修改现有客户端。
注入不会在 Codex 已完成加载后强制刷新页面；能在首次加载阶段拦截时直接完成补丁。

### 配置和凭据

配置文件：`%APPDATA%\CodexDictation\config.json`

- 阿里云 API Key：Windows Credential Manager 目标 `CodexDictation.Aliyun.ApiKey`
- 火山引擎 API Key：Windows Credential Manager 目标 `CodexDictation.Volcengine.ApiKey`
- 标题生成 API Key（可选）：Windows Credential Manager 目标 `CodexDictation.Title.ApiKey`

API Key 只在本机 Helper 与对应 ASR 服务建立连接时使用，不会通过 Codex 页面发送。

### 会话标题生成

Codex 用固定的小模型 `gpt-5.6-luna` 生成新会话标题，部分中转 API 会禁用该模型，标题因此停留在临时标题。Voice 设置底部的“会话标题生成”卡片提供两种接管方式：

- `自定义接口`：标题请求只发往填写的接口地址与模型，支持 `chat`（Chat Completions）和 `responses` 两种协议。接口拒绝结构化输出或思考强度参数时，去掉这两个参数重试一次。API Key 可选，留空时不带 `Authorization` 头。
- `当前对话模型`：使用当前对话的模型，从 `%USERPROFILE%\.codex\config.toml` 读取当前 Provider 的 `base_url` 与凭据，以最低思考强度发起请求。只对配置了 `base_url` 的自定义 Provider 生效；内置 `openai`、ChatGPT 登录或 `ollama`/`lmstudio` 会自动保留 Codex 原生行为。

标题生成失败不会回退到 `gpt-5.6-luna`，会话保留临时标题，错误写入 `helper.log`。选择 `关闭` 完全保留 Codex 原生行为。

### 常见问题

**配置卡片显示 Helper unavailable**

确认 Helper 正在运行，并且没有被防火墙拦截 `127.0.0.1`。查看 `%APPDATA%\CodexDictation\helper.log` 获取具体错误。

**火山引擎连接失败**

确认使用的是火山引擎 App Key，并且 Resource ID 与账号开通的服务一致。ASR 1.0 和 ASR 2.0 的 Resource ID 不能混用。

**听写按钮仍不可用**

在 `Settings > Voice` 中先设置一个 Dictation hotkey。Codex 原生逻辑要求存在 hotkey 后才启用保持听写栏显示等相关功能。

### 从源码构建

需要 Windows 11 和 .NET 8 SDK：

```powershell
dotnet build -c Release
dotnet run -c Release --no-build -- --self-test
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\artifacts\publish-framework
```

只支持 Windows x64。构建产物是依赖系统 .NET 8 Runtime 的单文件 exe。

## English

Unlocks native Dictation for API-key sessions in Codex Desktop for Windows and forwards audio to compatible real-time ASR providers.

### Current release

First public release: `v1.0.0`; current fix release: `v1.0.11`

Validated against ChatGPT Desktop (Codex) client version `26.911.7940.0` (Microsoft Store package `OpenAI.Codex_26.911.7940.0_x64__2p2nqsd0c76g0`). Injection points may need to be revalidated after a client update.

### Supported ASR providers

| Provider | Model | Settings required |
| --- | --- | --- |
| Aliyun DashScope | `qwen3-asr-flash-realtime` | Workspace ID and API key |
| Volcengine | Doubao BigModel bidirectional streaming | Resource ID and API key |

Volcengine uses the official bidirectional streaming endpoint `wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async`. The default Resource ID is `volc.seedasr.sauc.duration`; replace it with the Resource ID provisioned for your account when necessary.

### Features

- Uses Codex's native Voice settings and dictation composer; there is no separate foreground UI.
- Supports both the main conversation composer and Annotation comment editors, using the focused editor.
- Streams 16 kHz, 16-bit, mono PCM audio.
- Relays interim transcript deltas and final utterance results.
- Sends the dictation dictionary from Codex Voice settings as ASR hotword context.
- Stores API keys in Windows Credential Manager instead of `config.json`.
- Follows Codex's current language for the injected settings card.
- Shows the active `model_provider` in Voice settings and lets custom providers change model-response stream retries; built-in `openai`, `ollama`, and `lmstudio` keep Codex's default of 5.
- Can route new-conversation title generation to a custom endpoint or the current conversation model, bypassing relays that block `gpt-5.6-luna`.

### Use the release build

1. Download `CodexDictation.exe` from [Releases](https://github.com/A1mAssist/CodexDictationUnlock/releases).
2. If Codex is already running, the Helper asks whether to close and restart it for injection; you do not need to close it manually first.
3. Double-click `CodexDictation.exe`. The Helper starts Codex with a debug port and injects the patch.
4. Open `Settings > Voice` and find the `Dictation ASR` card.
5. Choose a provider:
   - Aliyun: enter the Workspace ID and DashScope API key.
   - Volcengine: enter the Resource ID and Volcengine App Key.
6. Save the settings and configure a Dictation hotkey in Voice settings.
7. Use Codex's native dictation entry point.
8. To adjust model-response stream retries, use the new Voice setting; changes apply from the next request.
9. To take over new-conversation titles, expand `Thread title generation` at the bottom of the same page, pick `Custom endpoint` or `Current conversation model`, and save.

The Helper has no separate foreground UI. Logs are written to `%APPDATA%\CodexDictation\helper.log`.
When Codex exits, the Helper stops automatically, so the executable can be launched again for the next session.
If ChatGPT is already running at startup, the Helper asks whether to close and restart it for injection; choosing No leaves the existing client unchanged.
The Helper does not force a page reload after Codex has loaded; it patches the initial load when interception is available.

### Configuration and credentials

Configuration file: `%APPDATA%\CodexDictation\config.json`

- Aliyun API key: Windows Credential Manager target `CodexDictation.Aliyun.ApiKey`
- Volcengine API key: Windows Credential Manager target `CodexDictation.Volcengine.ApiKey`
- Title generation API key (optional): Windows Credential Manager target `CodexDictation.Title.ApiKey`

API keys are used locally by the Helper to connect to the selected ASR service and are not sent through the Codex page.

### Thread title generation

Codex generates new-conversation titles with the fixed small model `gpt-5.6-luna`; some relay providers block it, leaving threads on their provisional title. The `Thread title generation` card at the bottom of Voice settings takes over in two ways:

- `Custom endpoint`: title requests go only to the configured endpoint and model, over either `chat` (Chat Completions) or `responses`. When the provider rejects the structured-output or reasoning-effort parameters, one stripped retry is made. The API key is optional; when it is empty no `Authorization` header is sent.
- `Current conversation model`: uses the current conversation model with the endpoint and credentials read from the active provider in `%USERPROFILE%\.codex\config.toml`, at the lowest reasoning effort. Only custom providers with a `base_url` qualify; built-in `openai`, ChatGPT sign-in, `ollama`, and `lmstudio` keep Codex's native behavior.

Title generation never falls back to `gpt-5.6-luna`: on failure the thread keeps its provisional title and the error is written to `helper.log`. `Off` keeps Codex's native behavior.

### Troubleshooting

**The card says Helper unavailable**

Make sure the Helper is running and that the firewall allows its `127.0.0.1` listener. Check `%APPDATA%\CodexDictation\helper.log` for the exact error.

**Volcengine connection fails**

Use a Volcengine App Key and a Resource ID provisioned for the same account. ASR 1.0 and ASR 2.0 Resource IDs are not interchangeable.

**Dictation is still disabled**

Configure a Dictation hotkey in `Settings > Voice` first. Codex's native logic requires a hotkey for related dictation controls.

### Build from source

Windows 11 and the .NET 8 SDK are required:

```powershell
dotnet build -c Release
dotnet run -c Release --no-build -- --self-test
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\artifacts\publish-framework
```

Windows x64 is currently supported. The published single-file executable requires the system .NET 8 Runtime.
