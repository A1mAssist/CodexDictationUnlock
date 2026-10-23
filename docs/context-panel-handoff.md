> 实现状态（2026-10-21）：已合并进本仓库。C# 解析与 `/context` 接口在 `ContextPanel.cs`，面板脚本在 `context-panel.js`。数据由页面每 5 秒调用本地 HTTP `/context` 获取，复用现有 helper 的 token/CORS；没有新增 WebSocket；没有 user/assistant 消息的空对话不挂载面板。下文关于 Python 进程、SQLite 和独立 watcher 的描述是迁移前的设计记录。
# Handoff — 把上下文面板合并进 CodexDictationUnlock

日期：2026-10-21
状态：**已实现。** 实现位置见文首注记；下文保留迁移前的分析结论。
目标仓库：本仓库（`CodexDictationUnlock`，C#/.NET 8，HEAD `d8952f3`，工作区干净）

---

## 一句话目标

把已经跑通的「Codex 右栏上下文面板」从独立 Python 项目并进本仓库，让用户只启动 CodexDictation 就同时拿到听写和上下文面板，不再单独起进程。

---

## 已验证的前提（接手时不必重查）

**面板和听写挂的是同一个 target。** `IsCodexTarget`（Program.cs:426-434）筛的正是 `app://-/index.html`，面板就挂在这个页面上。CDP 筛选、握手、`Runtime.evaluate` 通道全部可直接复用，无需新增机制。

**三个 Python 侧的负担会直接消失。**

| Python 侧现状 | 合并后 |
|---|---|
| `discover_port()` 读进程命令行找 `--remote-debugging-port` | 删。`debugPort` 在 `ActivateCodexAsync`（Program.cs:878）手里，本就是局部变量 |
| 常驻 watcher 进程 | 删。复用 `InjectLoopAsync`（Program.cs:264）已有的循环 |
| 手写的 WebSocket 握手 | 删。用现成的 `SendCdpAsync` / `WaitForCdpResponseAsync` |

**SQLite 可以整个砍掉，这是最关键的一条。** 本仓库现在是**零 NuGet 依赖**（纯 BCL + P/Invoke，`obj/project.assets.json` 只有 `ILLink.Tasks`）。加 SQLite 会引入第一个外部包。

查证过 rollout JSONL 本身自带所需字段：

```
session_meta keys: ['base_instructions', 'cli_version', 'context_window', 'cwd',
                    'history_mode', 'id', 'model_provider', 'originator', ...]
meta.id = 01a15d4d-caa0-7921-80b7-14b75081853c
meta.cwd = D:\Workspaces\...
```

面板需要 SQLite 的唯一原因是取线程**显示标题**。而标题页面上本来就有，实测：

```json
{"attrs":["data-app-action-sidebar-thread-selected","data-app-action-sidebar-thread-title",
          "data-app-action-sidebar-thread-id","aria-label", ...],
 "title":"OpenSynapse 后端"}
```

**结论：标题从 DOM 的 `data-app-action-sidebar-thread-title` 读，SQLite 不移植，保持零依赖。**
（只有当用户在侧栏完全收起、拿不到该属性时才需要退化方案 —— 那时用 `meta.cwd` 的目录名兜底即可。）

---

## 必须遵守的架构约束（踩过坑）

### 1. 面板绝不能混进 bundle patch 路径

`InjectRuntimeAsync`（Program.cs:439-453）里有 `Page.addScriptToEvaluateOnNewDocument` + `Page.reload`。听写**需要** reload 才生效，面板**不需要** —— 它是纯 DOM 注入，reload 后重新挂一次就行。

如果面板也去抢 reload，会撞上 Program.cs:455-457 注释里写的那个坑：window-manager 还在 `await loadURL("app://-/index.html")` 时发 `Page.reload`，那个 loadURL 会以 `ERR_ABORTED` 结束，bootstrap 判失败，弹 **"ChatGPT failed to start."**。

**面板只走 `injectedTargets` 那条路（Program.cs:299-308，纯 `Runtime.evaluate`）。**

### 2. 数据推送复用已有 helper WebSocket，不要新开轮询

本仓库已有本地 HTTP + WebSocket helper（`HasProtocol`，Program.cs:261）。注入脚本通过 `__CONNECT_INFO__` / `__HELPER_CONFIG__` 占位符拿到端口和 token（`LoadInjectionScript`，Program.cs:847-857）。

面板照抄这个模式加一个 `__CONTEXT_CONFIG__` 占位符即可，**不要另起一条轮询**。

当前 Python 版每 6 秒新建一条完整 WebSocket 握手推一次数据（`codex_context_inject.py`）。挂在已有通道上可以省掉这个开销。

### 3. 注入脚本的版本守卫照抄

`injection.js` 开头就是这套模式：

```js
const version = "68";
if (window.__CODEX_DICTATION_ASR_VERSION__ === version) return;
if (window.__codexDictationAsrTimer) window.clearInterval(window.__codexDictationAsrTimer);
```

面板脚本同样需要 `window.__CODEX_CONTEXT_PANEL_VERSION__` 守卫，否则重复注入会产生第二个面板。
面板脚本里**已经**有一个更细的守卫：reload 会清掉面板但留下版本标记，所以判断条件是"版本相同 **且** `document.getElementById(PANEL_ID)` 存在"。

---

## 两个数字的语义（务必原样保留，别在移植时"修正"）

面板显示两个来源完全不同的数字，措辞和位置是刻意分开的：

**大字百分比 —— 准确。** 来自 provider 自己上报的 `event_msg/token_count`：

```python
used = min(last["total_tokens"], window)
percent = round(used / window * 100, 1)
```

这就是原生 "Context usage" 圆环的算法，所以面板大字与原生环**逐位一致**（实测多次比对全等：82%/83%/28%/95%）。

**下面彩色 band —— 估算。** provider 用自己的 tokenizer 加重���缓存计费，跟转录字符数基本无关。

已穷尽 7 种反推方案（固定 4 chars/token、CJK/ASCII 分档、像素线性、512-tile、相邻请求差分回归、多项式拟合、reasoning 字段替代），**R² 全部 < 0.1 甚至为负**。实测佐证：差值与 reasoning band 相关系数 **r = +0.96** —— rollout 只存 reasoning *摘要*，provider 收到的是完整推理，二者不等价。

移植时必须保持的做法：

1. 按转录里各部分实际字节数算权重；
2. 缩放到 provider 的精确总量；
3. 差额进 `untracked` band（`Tool + request schema` / `工具 + 请求 schema`），代表转录里不存在的东西：工具 schema（每次请求重发，从不写进 rollout）+ 请求脚手架。

**图片计价已标定，不要改：**

```
tokens = 0.00101642 * pixels + 628.40
```

11 个真实样本，中位误差 0.46%，最大 1.85%。
**不能用 384 封顶** —— 那是 DeepSeek 视觉模型的价，本 provider 一张 1200×2048 的图约 3100 token。
**也不能按 base64 长度算** —— 会把 tool band 撑大两个数量级。

---

## 增量解析（移植时最容易搞坏的部分）

`codex_context_helper.py` 里有两份等价实现，自检要求两者结果**完全一致**：

- `read_rollout_cached(path)` —— 按字节 offset 增量，只解析新增尾巴，用���二进制读
- `read_rollout(path)` —— 全量解析

`_ParseState` 保存 `offset / meta / turns / usage / compactions`。关键细节：只消费完整行（`data.rfind(b"\n")`），半行留给下次。文件变小或被替换时 `reset()` 重来。

移植后性能必须保持 —— 这是 Python 版从 0.5s/30s 优化到 0.19s/30s 的原因（全量解析大 rollout 每次都要重读几百 MB）。

---

## 压缩窗口的语义（`live_window`，helper 里最绕的一段）

压缩不是"截断"，是"替换"。Codex 把 `replacement_history` 里的条目作为压缩后的上下文重发。

**`replacement_history` 的条目不在转录的 ordinal 线上**，它替换掉被切掉的那批条目，所以必须**无条件计数**，不能按 ordinal 过滤。

系统提示词每次请求都重发，所以即使 ordinal 在压缩点之前，也**始终计入**。

自检里这三条都有断言，移植时保留：

```python
turns = [(0, "system", 100), (1, "user", 50), (2, "tool", 999), (3, "user", 70)]
comps = [{"ordinal": 2, "kept": [(2, "kept body", "user")]}]
# 断言：system 存活、压缩点前的 tool output 被丢弃、replacement 被计入
```

---

## 需要移植的文件

源码在 `D:\Workspaces\PROJECTLESS_CODEX_TASKS\2026-10-21\https-github-com-bowenliang123-dsh-context\outputs\`：

| 文件 | 移植成 | 说明 |
|---|---|---|
| `codex_context_helper.py` (20663 B) | C# 约 500-600 行 | **主要工作量。** 解析、band 拆解、缩放、图片计价、增量解析 |
| `codex_context_injection.js` (17130 B) | EmbeddedResource | **可原样移植**，只需把数据接收函数名对齐。已是原生 DOM，克隆原生摘要面板的盒子，复用 Codex CSS 变量 |
| `codex_context_selftest.py` (6090 B) | C# 自检 | 见下方"自检清单" |
| `codex_context_inject.py` / `codex_context_panel.py` / `start-context-panel.cmd` | **丢弃** | 独立进程的启停逻辑，合并后不需要 |
| `codex_context_inspect.py` | **丢弃** | 早期调查脚本，已被取代 |
| `docs/panel.png` (91286 B) | README 用图 | 已裁剪过，只含右栏，不含对话内容 |

**不要移植的三个 Python 侧机制**（合并后自然消失）：`discover_port()`、手写 WebSocket 握手、`SystemExit` bug（见下）。

---

## 自检清单（移植后必须全绿）

Python 版 `codex_context_selftest.py` 当前 **ALL CHECKS PASSED**，覆盖：

1. 4 chars/token 启发式、空文本、system 偏移
2. **图片计价命中标定值**（`estimate_image_tokens(1200, 2048) == 3126`）、小图 < 大图、**1MB base64 不得被当文本计**（< 400 token）
3. 压缩窗口：system 存活、压缩点前内容丢弃、`replacement_history` 计入
4. 无压缩时全部计入
5. provider 百分比公式 `min(total, window) / window`
6. **band 加总精确等于 provider 总量**；untracked 是差额；转录已完整时 untracked 为 0
7. 12 个真实 rollout：provider 百分比与公式一致、bands 精确加总
8. **增量解析与全量解析结果一致**
9. **CDP 断连不得让 watcher 退出**（这条在 C# 里天然成立，断言可省）

第 9 条的由来值得记一笔：`ws_send` 曾用 `raise SystemExit`，而 `SystemExit` 继承 `BaseException`，`except Exception` 接不住 —— watcher 跟着 Codex 重启一起死了。C# 的 `catch (Exception)` 没有这个陷阱。

---

## 已完成的实机验收（合并后需要重新确认这几条）

在 Codex `26.1013.4894.0` + provider `http://127.0.0.1:8788/v1` 上验过：

- ✅ 面板在右栏，与原生摘要面板**同列、同宽、同色、无重叠**
- ✅ 大字百分比与原生环**逐位一致**
- ✅ 切换会话面板跟随
- ✅ React 重渲染后面板存活，`panelCount == 1`（无重复挂载）
- ✅ 两个 watcher 并跑仍只有 1 个面板（版本守卫生效）
- ✅ 改面板代码无需重启 watcher（每 tick 重读脚本）
- ✅ Codex reload 后面板自动恢复
- ✅ **Codex 重启后面板自动恢复**（端口变了也能跟上）
- ✅ watcher 空闲 CPU 0.19s/30s

截图证据在 `work/cdp/shot_v12.png`（整窗，含对话内容，**不要提交**）和 `outputs/docs/panel.png`（已裁剪版）。

---

## 仍未验证 / 已知风险

- **band 构成比例仍可能偏。** 30 个会话里实测差值 −14% ~ +28%，与 reasoning 强相关（r=+0.96）。总百分比可信，构成仅供参考。移植时不要试图"修好"它。
- **Codex 升级可能让面板静默消失。** 面板克隆的是原生摘要面板的 DOM，Codex 改了结构就失效。当前 Python 版每次 tick 会确认面板是否真的挂上，失效时日志写 `panel did not mount: ...`。**合并后必须保留这个确认**，否则会退化成静默失败 —— 面板脚本里已有 `window.__CODEX_CONTEXT_PANEL_MOUNT_ERROR__` 可读。
- **未在 CodexDictation 之外的路径验证。**
- **窄窗口 / 移动端布局未验收。**
- **未做 Codex 大版本升级兼容矩阵。**

---

## 建议的实现顺序

1. 先只做**注入脚本**：把 `codex_context_injection.js` 加为第二个 EmbeddedResource，用 `__CONNECT_INFO__` 同样的占位符机制挂进去，面板能挂上但先显示占位数据。**这一步风险最低，先验证不破坏现有听写链路。**
2. 再移植 helper 的解析逻辑，接上真实数据。
3. 最后移植自检，接进本仓库既有的自检入口（Program.cs:1135-1143 那组）。

**每步之后跑一遍本仓库现有的听写回归**，别等全部写完再测。

---

## 相关坐标

- 本仓库 Program.cs 共 2183 行，`InjectLoopAsync` 在 264-333 行
- 注入脚本占位符：`injection.js:4-5`
- 注入方法：`Program.cs:439` `InjectRuntimeAsync`
- target 筛选：`Program.cs:426` `IsCodexTarget`
- helper 占位符替换：`Program.cs:847-857` `LoadInjectionScript`
- 启动/端口：`Program.cs:878` `ActivateCodexAsync`
- 现有自检：`Program.cs:1135` 附近
- `~/.codex/sessions/**/*.jsonl` —— rollout
- `~/.codex/state_5.sqlite` —— **不需要**，见上文

CDP 调试工具留在原项目：`work/cdp/cdp.mjs`（求值）、`work/cdp/shot.mjs`（截图）、`work/cdp/verify_panel.js`（验收面板状态）、`work/cdp/test_geom.js`（几何/重叠检查）。

---

## 合并后的实现（2026-10-21）

- 文件已迁移：`ContextPanel.cs`、`context-panel.js`、`tests/context-panel-ui.mjs`；原 `HANDOFF-context-panel.md` 归档为本文档。
- `/context` 复用 `/config` 的 token 与 CORS；注入时把 `injection.js` 和 `context-panel.js` 一起下发。
- 进度条改为表示**整个 context window**：已用内容、压缩后保留历史、剩余空间；分母是 provider 的 `contextWindow`，所有 band 之和等于窗口总量。
- 最后一次压缩的 `replacement_history` 单独计入橙色的 `Compacted history` band；`hasConversation` 仍检查原始 rollout，避免压缩后把有内容的会话误判为空对话。
- 分页会话按文件修改时间读取最新一页 rollout。之前取枚举到的第一个文件，会把父页的旧 token_count 当成本页数据。
- `Instructions` 按 developer 消息前缀细分为 app context、skills、user steer、model switch、Ponytail、mode、time、other；细分数据保留在 snapshot 里，当前面板只显示汇总 band。
- `Tool + request schema` 是 provider 总量减去已解析内容的差额。rollout 不保存工具 schema 和完整请求，无法拆出两者的比例；面板把它们合并显示，并明确说明不能继续细分。
- 收起只隐藏 `.ccp-details`；标题栏、大字百分比、进度条继续显示。状态保存在 `localStorage["codex-context-panel-collapsed"]`。
- 新建空对话不挂载；发送第一条消息后自动出现。
- 会话切换后标题在 1 秒内跟随侧栏选中项更新，不等待 5 秒数据轮询；窗口条仍按 5 秒轮询重绘。
- 开关：`config.json` 的 `ContextPanelEnabled`（默认 `true`），`/config` GET 返回 `contextPanel`，POST 接受 `{ "contextPanel": false }`。关闭时 `/context` 返回 404；`Settings > Voice` 底部的“上下文面板”卡片负责读写，保存后调用 `window.__codexContextPanelRefresh()` 立即刷新。
- v1.2.1：面板标题在 1 秒内跟随侧栏选中会话；5 秒轮询会重绘窗口条；`/context` 受 `ContextPanelEnabled` 开关门控。本地 `D:\CodexDictation.exe` 已替换，需重启 CodexDictation 才加载新 helper。
- 验收时注入的静态预览面板已移除；重启后由新 helper 注入真实 rollout 数据。`token_count` 只在新模型响应到达时更新，长工具调用期间窗口条保持不动是正常现象。
- 验收入口：`dotnet build -c Release`、`dotnet run -c Release --no-build -- --self-test`、`node tests/context-panel-ui.mjs <cdp-port> artifacts/context-sample.json`、`node tests/context-panel-toggle.mjs artifacts`。
