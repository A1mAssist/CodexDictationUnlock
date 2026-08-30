# CodexDictationUnlock

中文 | [English](#english)

## 中文

为 Windows 版 Codex Desktop 的 API Key 登录解锁原生 Dictation，并把音频转发到兼容的实时 ASR 服务。

### 当前版本

首个公开版本：`v1.0.0`

适配的 ChatGPT Desktop（Codex）客户端版本：`26.825.5331.0`（Microsoft Store 包 `OpenAI.Codex_26.825.5331.0_x64__2p2nqsd0c76g0`）。客户端更新后，注入点可能需要重新验证。

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

### 使用发布版

1. 从 [Releases](https://github.com/A1mAssist/CodexDictationUnlock/releases) 下载 `CodexDictation.exe`。
2. 关闭正在运行的 Codex 和旧版 CodexDictation Helper。
3. 双击 `CodexDictation.exe`。Helper 会启动带调试端口的 Codex，并自动注入补丁。
4. 打开 Codex 的 `Settings > Voice`，找到 `Dictation ASR` 配置卡片。
5. 选择服务商：
   - 阿里云：填写 Workspace ID 和 DashScope API Key。
   - 火山引擎：填写 Resource ID 和火山引擎 App Key。
6. 保存配置，并在 Voice 设置中设置 Dictation hotkey。
7. 使用 Codex 原生听写入口开始录音。

Helper 没有独立的前台 UI。运行日志位于 `%APPDATA%\CodexDictation\helper.log`。
当 Codex 退出后，Helper 会自动停止；下次可以直接再次双击 exe 启动。

### 配置和凭据

配置文件：`%APPDATA%\CodexDictation\config.json`

- 阿里云 API Key：Windows Credential Manager 目标 `CodexDictation.Aliyun.ApiKey`
- 火山引擎 API Key：Windows Credential Manager 目标 `CodexDictation.Volcengine.ApiKey`

API Key 只在本机 Helper 与对应 ASR 服务建立连接时使用，不会通过 Codex 页面发送。

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

First public release: `v1.0.0`

Validated against ChatGPT Desktop (Codex) client version `26.825.5331.0` (Microsoft Store package `OpenAI.Codex_26.825.5331.0_x64__2p2nqsd0c76g0`). Injection points may need to be revalidated after a client update.

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

### Use the release build

1. Download `CodexDictation.exe` from [Releases](https://github.com/A1mAssist/CodexDictationUnlock/releases).
2. Close Codex and any older CodexDictation Helper instance.
3. Double-click `CodexDictation.exe`. The Helper starts Codex with a debug port and injects the patch.
4. Open `Settings > Voice` and find the `Dictation ASR` card.
5. Choose a provider:
   - Aliyun: enter the Workspace ID and DashScope API key.
   - Volcengine: enter the Resource ID and Volcengine App Key.
6. Save the settings and configure a Dictation hotkey in Voice settings.
7. Use Codex's native dictation entry point.

The Helper has no separate foreground UI. Logs are written to `%APPDATA%\CodexDictation\helper.log`.
When Codex exits, the Helper stops automatically, so the executable can be launched again for the next session.

### Configuration and credentials

Configuration file: `%APPDATA%\CodexDictation\config.json`

- Aliyun API key: Windows Credential Manager target `CodexDictation.Aliyun.ApiKey`
- Volcengine API key: Windows Credential Manager target `CodexDictation.Volcengine.ApiKey`

API keys are used locally by the Helper to connect to the selected ASR service and are not sent through the Codex page.

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
