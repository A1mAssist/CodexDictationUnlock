# CodexDictationUnlock

中文 | [English](#english)

## 中文

为 Codex Desktop API Key 登录解锁原生 Dictation，并将实时音频转发到阿里云 DashScope ASR。

### 特性

- 保留 Codex 原生听写输入框和 Voice 设置界面。
- 支持流式 PCM 音频和实时增量文字转发到 `qwen3-asr-flash-realtime`。
- ASR 配置直接显示在 Codex 的 `Settings > Voice` 中。
- 配置卡片跟随 Codex 当前语言自动显示中文或英文。
- API Key 存储在 Windows Credential Manager，不写入配置文件。

### 使用

1. 关闭 Codex。
2. 双击 `artifacts\publish-framework\CodexDictation.exe`。
3. 在 Codex 中打开 `Settings > Voice`，填写 Aliyun Workspace ID 和 API Key。
4. 在 Voice 设置中配置一个 Dictation hotkey，然后使用原生听写入口。

程序没有独立前台窗口，配置和听写操作都在 Codex 内完成。

### 构建

需要 Windows 11 和 .NET 8 SDK：

```powershell
dotnet build -c Release
dotnet run -c Release --no-build -- --self-test
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\artifacts\publish-framework
```

运行日志位于 `%APPDATA%\CodexDictation\helper.log`。Workspace ID 保存在 `%APPDATA%\CodexDictation\config.json`，API Key 保存在 Windows Credential Manager。

## English

Unlocks native Dictation for Codex Desktop API-key sessions and forwards streaming audio to Aliyun DashScope ASR.

### Features

- Keeps Codex's native Dictation composer and Voice settings UI.
- Forwards streaming PCM audio and incremental transcript updates to `qwen3-asr-flash-realtime`.
- Shows ASR configuration inside `Settings > Voice`.
- Automatically follows Codex's current language and switches the card between Chinese and English.
- Stores the API key in Windows Credential Manager instead of the config file.

### Usage

1. Close Codex.
2. Double-click `artifacts\publish-framework\CodexDictation.exe`.
3. Open `Settings > Voice` in Codex and enter your Aliyun Workspace ID and API key.
4. Configure a Dictation hotkey in Voice settings, then use the native Dictation entry point.

There is no separate foreground UI. Configuration and dictation stay inside Codex.

### Build

Windows 11 and the .NET 8 SDK are required:

```powershell
dotnet build -c Release
dotnet run -c Release --no-build -- --self-test
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\artifacts\publish-framework
```

Runtime logs are written to `%APPDATA%\CodexDictation\helper.log`. The Workspace ID is stored in `%APPDATA%\CodexDictation\config.json`; the API key is stored in Windows Credential Manager.
