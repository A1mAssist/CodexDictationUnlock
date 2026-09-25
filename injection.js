(() => {
  "use strict";
  const version = "65";
  const connectInfo = __CONNECT_INFO__;
  const helperConfig = __HELPER_CONFIG__;
  window.__CODEX_DICTATION_CONNECT_INFO__ = connectInfo;
  if (window.__CODEX_DICTATION_ASR_VERSION__ === version) return;
  if (window.__codexDictationAsrTimer) window.clearInterval(window.__codexDictationAsrTimer);
  if (window.__codexDictationControlsTimer) window.clearInterval(window.__codexDictationControlsTimer);

  const assetUrls = () => Array.from(new Set([
    ...Array.from(document.scripts || []).map((item) => item.src),
    ...Array.from(document.querySelectorAll("link[href]")).map((item) => item.href),
    ...performance.getEntriesByType("resource").map((item) => item.name),
  ].filter((url) => typeof url === "string" && url.includes("/assets/") && url.split("?")[0].endsWith(".js"))));

  const visible = (element) => element?.getClientRects().length > 0;
  const locale = () => (document.documentElement.lang || navigator.language || "en").toLowerCase().startsWith("zh") ? "zh" : "en";
  const copy = {
    en: {
      title: "Dictation ASR",
      description: "Choose the cloud ASR service used for real-time dictation.",
      provider: "Service provider",
      aliyun: "Aliyun DashScope · qwen3-asr-flash-realtime",
      volcengine: "Volcengine · Doubao BigModel Streaming",
      endpoint: "Workspace ID",
      resource: "Resource ID",
      apiKey: "API key",
      save: "Save",
      connecting: "Connecting to helper...",
      configured: "Configured",
      notConfigured: "Not configured",
      unavailable: "Helper unavailable",
      saving: "Saving",
      saved: "Saved",
      placeholderWorkspace: "ws-xxxxxxxx",
      placeholderResource: "volc.seedasr.sauc.duration",
      placeholderKey: "Paste API key",
      saveError: "Unable to save ASR settings",
      loadError: "Unable to load ASR settings",
      reconnectTitle: "Model response stream reconnects",
      reconnectDescription: "Controls interrupted model-response SSE streams. Changes apply to the next request.",
      currentProvider: "Current provider",
      retryCount: "Retry count",
      retryDefault: "Uses Codex's default value of 5",
      retrySave: "Save retries",
      retrySaved: "Saved · applies to the next request",
      retryLoading: "Loading Codex configuration…",
      retryUnavailable: "Codex configuration is unavailable",
      retryUnsupported: "Built-in providers use Codex's default value of 5",
      retryInvalid: "Enter a non-negative whole number",
      retrySaveError: "Unable to save retry settings",
      recordingsShow: "Show {count} recordings",
      recordingsHide: "Hide recordings",
      titleTitle: "Thread title generation",
      titleMode: "Mode",
      titleOff: "Off (Codex default)",
      titleCustom: "Custom endpoint",
      titleCurrent: "Current conversation model",
      titleBaseUrl: "Endpoint",
      titleModel: "Model",
      titleWireApi: "API",
      titleApiKey: "API key",
      titleOffHint: "Codex uses its built-in model for titles. Relay providers that block gpt-5.6-luna fail here.",
      titleCustomHint: "Title requests go only to this endpoint. Leave the API key empty to send no Authorization header.",
      titleCurrentHint: "Uses the current conversation model with the endpoint and credentials from ~/.codex/config.toml at the lowest reasoning effort.",
      titleUnavailable: "Current config cannot take over; Codex native titles stay in effect",
      titleSaveError: "Unable to save title settings",
    },
    zh: {
      title: "听写 ASR",
      description: "选择用于实时听写的云端 ASR 服务。",
      provider: "服务商",
      aliyun: "阿里云 DashScope · qwen3-asr-flash-realtime",
      volcengine: "火山引擎 · 豆包大模型双向流式",
      endpoint: "Workspace ID",
      resource: "Resource ID",
      apiKey: "API Key",
      save: "保存",
      connecting: "正在连接助手…",
      configured: "已配置",
      notConfigured: "未配置",
      unavailable: "助手不可用",
      saving: "正在保存",
      saved: "已保存",
      placeholderWorkspace: "ws-xxxxxxxx",
      placeholderResource: "volc.seedasr.sauc.duration",
      placeholderKey: "填写 API Key",
      saveError: "无法保存 ASR 设置",
      loadError: "无法读取 ASR 设置",
      reconnectTitle: "模型响应流重连",
      reconnectDescription: "用于模型响应 SSE 流中断后的重连；修改从下一次请求开始生效。",
      currentProvider: "当前 Provider",
      retryCount: "重连次数",
      retryDefault: "使用 Codex 默认值 5",
      retrySave: "保存重连次数",
      retrySaved: "已保存 · 从下一次请求生效",
      retryLoading: "正在读取 Codex 配置…",
      retryUnavailable: "Codex 配置接口不可用",
      retryUnsupported: "内置 Provider 使用 Codex 默认值 5",
      retryInvalid: "请输入非负整数",
      retrySaveError: "无法保存重连设置",
      recordingsShow: "展开 {count} 条录音",
      recordingsHide: "收起录音",
      titleTitle: "会话标题生成",
      titleMode: "模式",
      titleOff: "关闭（使用 Codex 默认）",
      titleCustom: "自定义接口",
      titleCurrent: "当前对话模型",
      titleBaseUrl: "接口地址",
      titleModel: "模型",
      titleWireApi: "协议",
      titleApiKey: "API Key",
      titleOffHint: "由 Codex 内置模型生成标题；中转 API 屏蔽 gpt-5.6-luna 时会失败。",
      titleCustomHint: "标题请求只发往该接口；API Key 留空时不带 Authorization 头。",
      titleCurrentHint: "使用当前对话模型，读取 ~/.codex/config.toml 的接口与凭据，以最低思考强度生成标题。",
      titleUnavailable: "当前配置无法接管，保留 Codex 原生标题",
      titleSaveError: "无法保存标题设置",
    },
  };
  // Native-looking dropdowns: Codex draws its own pickers, so a plain <select> popup
  // (square, OS-highlighted) never matches. Chrome 153 supports customizable select,
  // which lets the app's own tokens style the popup, including the checkmark.
  const selectPopupCss = `
    [data-codex-title-settings] select{appearance:base-select !important;white-space:nowrap !important;overflow:hidden !important;text-overflow:ellipsis !important}
    [data-codex-title-settings] select::picker-icon{content:url("data:image/svg+xml,%3Csvg xmlns=%27http://www.w3.org/2000/svg%27 width=%2716%27 height=%2716%27 viewBox=%270 0 24 24%27 fill=%27none%27 stroke=%27%23ffffffa6%27 stroke-width=%272%27 stroke-linecap=%27round%27 stroke-linejoin=%27round%27%3E%3Cpath d=%27m6 9 6 6 6-6%27/%3E%3C/svg%3E") !important;transition:rotate .15s}
    [data-codex-title-settings] select:open::picker-icon{rotate:180deg}
    [data-codex-title-settings] select::picker(select){appearance:base-select !important;margin-top:6px !important;padding:6px !important;border:1px solid var(--color-border-secondary,rgba(255,255,255,.12)) !important;border-radius:14px !important;background:var(--color-surface-elevated-secondary,#202124) !important;box-shadow:0 12px 32px rgba(0,0,0,.5) !important;color:var(--color-text-primary,currentColor) !important}
    [data-codex-title-settings] option{display:flex !important;align-items:center !important;padding:8px 10px !important;border-radius:10px !important;background-color:transparent !important;color:var(--color-text-primary,currentColor) !important;font-size:14px !important}
    [data-codex-title-settings] option:hover{background-color:var(--color-surface-secondary,rgba(255,255,255,.06)) !important}
    [data-codex-title-settings] option:checked{background-color:var(--color-surface-secondary,rgba(255,255,255,.08)) !important}
    [data-codex-title-settings] option::checkmark{color:var(--color-text-primary,currentColor) !important;font-size:14px !important;order:2 !important;margin-left:auto !important}
  `;
  // cloneNode keeps the native card's inline style, including any fixed height or
  // positioning, which makes an injected card overlap the next section.
  const detachFlowConstraints = (element) => {
    for (const property of ["height", "min-height", "max-height", "position", "top", "bottom", "inset", "overflow"]) element.style.removeProperty(property);
  };
  const cardFor = (element) => {
    let card = element?.parentElement;
    while (card && card !== document.body) {
      const rect = card.getBoundingClientRect();
      if (visible(card) && rect.width >= 500 && rect.height >= 100 && parseFloat(getComputedStyle(card).borderRadius) > 0) return card;
      if (visible(card) && card.querySelectorAll("button").length >= 2) return card;
      card = card.parentElement;
    }
    return null;
  };
  const dictationSettings = () => {
    const input = Array.from(document.querySelectorAll("[data-dictation-dictionary-entry-index]"))
      .find(visible);
    const dictionaryCard = cardFor(input);
    const container = dictionaryCard?.parentElement;
    if (!input || !dictionaryCard || !container) return null;
    const anchor = container.parentElement?.tagName === "SECTION" ? container.parentElement : dictionaryCard;
    return { input, dictionaryCard, container, anchor };
  };

  const recordingsStateKey = "codex-dictation-recordings-collapsed";
  let recordingsCollapsed = true;
  try { recordingsCollapsed = window.localStorage.getItem(recordingsStateKey) !== "0"; } catch {}
  const applyRecordingsCollapse = (native) => {
    const stamp = document.querySelector("time[datetime]");
    const section = stamp?.closest("section");
    const content = section && Array.from(section.children).find((child) => child.contains(stamp));
    const group = content && Array.from(content.children).find((child) => child.contains(stamp));
    const header = group?.firstElementChild;
    if (!group || !header || header.querySelector("time")) return;
    const rows = Array.from(group.children).filter((row) => row.querySelector("time"));
    if (rows.length < 2) {
      header.querySelector("[data-codex-dictation-recordings-toggle]")?.remove();
      return;
    }
    let toggle = header.querySelector("[data-codex-dictation-recordings-toggle]");
    if (!toggle) {
      const sample = Array.from(native.dictionaryCard.querySelectorAll("button")).find(visible);
      toggle = sample ? sample.cloneNode(true) : document.createElement("button");
      toggle.type = "button";
      toggle.dataset.codexDictationRecordingsToggle = "";
      toggle.disabled = false;
      toggle.removeAttribute("disabled");
      toggle.removeAttribute("aria-disabled");
      toggle.style.setProperty("flex", "none", "important");
      toggle.style.setProperty("white-space", "nowrap", "important");
      toggle.addEventListener("click", () => {
        recordingsCollapsed = !recordingsCollapsed;
        try { window.localStorage.setItem(recordingsStateKey, recordingsCollapsed ? "1" : "0"); } catch {}
        applyRecordingsCollapse(native);
      });
      header.append(toggle);
    }
    for (const row of rows) {
      if (recordingsCollapsed) row.style.setProperty("display", "none", "important");
      else row.style.removeProperty("display");
    }
    const text = copy[locale()];
    toggle.textContent = (recordingsCollapsed ? text.recordingsShow : text.recordingsHide).replace("{count}", String(rows.length));
    toggle.setAttribute("aria-expanded", String(!recordingsCollapsed));
  };

  const reservedModelProviders = new Set(["openai", "ollama", "lmstudio"]);
  const readModelRetryConfig = async () => {
    const client = window.__CODEX_DICTATION_APP_SERVERS__?.get?.("local") || window.__CODEX_DICTATION_APP_SERVER__;
    if (!client?.sendRequest) throw new Error("Codex config client unavailable");
    const response = await client.sendRequest("config/read", { includeLayers: true, cwd: null }, { priority: "critical" });
    const raw = response?.config && typeof response.config === "object" ? response.config : {};
    const profile = typeof raw.profile === "string" && raw.profiles?.[raw.profile] && typeof raw.profiles[raw.profile] === "object" ? raw.profiles[raw.profile] : null;
    const config = profile ? { ...raw, ...Object.fromEntries(Object.entries(profile).filter(([, value]) => value != null)) } : raw;
    const providerId = typeof config.model_provider === "string" && config.model_provider ? config.model_provider : "openai";
    const providers = config.model_providers && typeof config.model_providers === "object" ? config.model_providers : {};
    const provider = providers[providerId] && typeof providers[providerId] === "object" ? providers[providerId] : null;
    const editable = !reservedModelProviders.has(providerId) && provider != null && /^[A-Za-z0-9_-]{1,128}$/.test(providerId);
    const retries = Number.isSafeInteger(provider?.stream_max_retries) && provider.stream_max_retries >= 0 ? provider.stream_max_retries : 5;
    const userLayer = [...(Array.isArray(response?.layers) ? response.layers : [])].reverse().find((layer) => layer?.name?.type === "user");
    return { client, providerId, providerName: typeof provider?.name === "string" && provider.name ? provider.name : providerId, editable, retries, filePath: userLayer?.name?.file ?? null, expectedVersion: userLayer?.version ?? null };
  };

  let lastFailureReason = "";
  const failureTitle = () => locale() === "zh"
    ? (lastFailureReason === "helper" ? "听写失败 · Helper 连接失败" : lastFailureReason === "api" ? "听写失败 · ASR API 连接失败" : `听写失败 · ${lastFailureReason || "未知错误"}`)
    : (lastFailureReason === "helper" ? "Unable to transcribe audio · Helper connection failed" : lastFailureReason === "api" ? "Unable to transcribe audio · ASR API connection failed" : `Unable to transcribe audio · ${lastFailureReason || "Unknown error"}`);
  const rememberFailure = (message) => {
    const value = String(message || "").replace(/\s+/g, " ").trim();
    if (!value) return;
    const lower = value.toLowerCase();
    lastFailureReason = /helper|failed to fetch|err_connection|websocket|network|connect/.test(lower) ? "helper"
      : /api|aliyun|volc|dashscope|unauthor|forbidden|timeout|asr/.test(lower) ? "api" : value.slice(0, 80);
  };
  const replaceFailureTitle = () => {
    if (!lastFailureReason) return;
    const title = failureTitle();
    const matches = Array.from(document.querySelectorAll("span,div,p")).filter((element) => visible(element) && element.children.length === 0 && /^(Unable to transcribe audio|无法转写音频)$/.test((element.textContent || "").trim()));
    matches.at(-1)?.replaceChildren(document.createTextNode(title));
  };
  function installFailureReasonBridge() {
    if (window.__CODEX_DICTATION_FAILURE_BRIDGE__ === version) return;
    new MutationObserver(replaceFailureTitle).observe(document.documentElement || document, { childList: true, subtree: true, characterData: true });
    window.__CODEX_DICTATION_FAILURE_BRIDGE__ = version;
  }

  // Keep interim ASR text in one native ProseMirror range until Codex commits the final transcript.
  function installTranscriptPreviewBridge() {
    if (window.__codexDictationTranscriptBridge__ === version) return;
    const originalAddEventListener = WebSocket.prototype.addEventListener;
    const states = new Map();
    let lastFocusedComposer = null;
    const clearPreview = (socket) => {
      const current = states.get(socket);
      if (!current) return;
      states.delete(socket);
      const view = current.controller.view;
      if (!view.isDestroyed && current.length > 0) try {
        const tr = view.state.tr.delete(current.from, current.from + current.length);
        tr.setSelection(view.state.selection.constructor.create(tr.doc, current.from));
        view.dispatch(tr);
      } catch {}
    };
    const clearPreviewForController = (controller) => {
      for (const [socket, current] of states) if (current.controller === controller) clearPreview(socket);
    };
    const patchController = (controller) => {
      if (controller.__codexDictationPreviewPatched === version) return;
      const originalInsert = controller.insertDictationText.bind(controller);
      controller.insertDictationText = (text) => {
        clearPreviewForController(controller);
        return originalInsert(text);
      };
      controller.__codexDictationPreviewPatched = version;
    };
    window.__CODEX_DICTATION_REGISTER_COMPOSER__ = (controller) => {
      if (!controller?.view?.state?.tr || typeof controller.insertDictationText !== "function") return;
      window.__CODEX_DICTATION_COMPOSERS__ ||= [];
      if (!window.__CODEX_DICTATION_COMPOSERS__.includes(controller)) window.__CODEX_DICTATION_COMPOSERS__.push(controller);
      if (controller.view.dom.contains(document.activeElement) || controller.view.dom === document.activeElement) lastFocusedComposer = controller;
      if (!window.__CODEX_DICTATION_COMPOSER__ || !window.__CODEX_DICTATION_COMPOSER__.view?.dom?.isConnected) window.__CODEX_DICTATION_COMPOSER__ = controller;
    };
    document.addEventListener("focusin", (event) => {
      const target = event.target;
      const composer = (window.__CODEX_DICTATION_COMPOSERS__ || []).find((item) => item?.view?.dom?.contains(target));
      if (composer) lastFocusedComposer = composer;
    }, true);
    const begin = (socket) => {
      const composers = (window.__CODEX_DICTATION_COMPOSERS__ || [window.__CODEX_DICTATION_COMPOSER__]).filter((item) => item?.view?.state?.tr && !item.view.isDestroyed && item.view.dom.isConnected);
      const modal = composers.find((item) => visible(item.view.dom.closest('[role="dialog"],[aria-modal="true"]')));
      const active = composers.find((item) => item.view.dom.contains(document.activeElement) || item.view.dom === document.activeElement);
      const controller = modal || active || (composers.includes(lastFocusedComposer) ? lastFocusedComposer : null) || (composers.length === 1 ? composers[0] : null);
      if (!controller?.view?.state?.tr || typeof controller.insertDictationText !== "function" || controller.view.isDestroyed || !controller.view.dom.isConnected) return null;
      patchController(controller);
      const selection = controller.view.state.selection;
      return { socket, controller, from: selection.from, length: selection.to - selection.from, order: [], textByUtterance: new Map(), closing: false };
    };
    const ensureState = (socket) => {
      let state = states.get(socket);
      if (!state) {
        state = begin(socket);
        if (state) states.set(socket, state);
      }
      return state;
    };
    const dispatchPreview = (socket, next) => {
      const state = states.get(socket);
      if (!state) return;
      const view = state.controller.view;
      if (view.isDestroyed) { states.delete(socket); return; }
      try {
        const tr = view.state.tr.delete(state.from, state.from + state.length);
        tr.insertText(next, state.from);
        const pos = state.from + next.length;
        tr.setSelection(view.state.selection.constructor.create(tr.doc, pos));
        view.dispatch(tr);
        state.length = next.length;
      } catch { clearPreview(socket); }
    };
    const updateUtterance = (message, socket) => {
      const state = ensureState(socket);
      if (!state) return;
      const id = String(message.utterance_id || "default");
      if (!state.textByUtterance.has(id)) state.order.push(id);
      state.textByUtterance.set(id, String(message.text || ""));
      dispatchPreview(socket, state.order.map((item) => state.textByUtterance.get(item) || "").filter(Boolean).join(" "));
    };
    const handle = (event, socket) => {
      if (typeof event.data !== "string") return;
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      if (message.type === "session.started") {
        lastFailureReason = "";
        clearPreview(socket);
      } else if (message.type === "speech.started") {
        const state = states.get(socket);
        if (state) {
          const id = String(message.utterance_id || "default");
          if (!state.textByUtterance.has(id)) {
            state.order.push(id);
            state.textByUtterance.set(id, "");
          }
        }
      } else if (message.type === "transcript.delta" || message.type === "transcript.segment") {
        updateUtterance(message, socket);
      } else if (message.type === "transcript.final" && states.has(socket)) {
        updateUtterance(message, socket);
      } else if (message.type === "transcript.failed" || message.type === "session.error") {
        rememberFailure(message.error?.message || message.error || "ASR API error");
        window.setTimeout(replaceFailureTitle, 0);
        clearPreview(socket);
      } else if (message.type === "session.updated" && message.session?.status === "closed") {
        const state = states.get(socket);
        if (state) state.closing = true;
      }
    };
    WebSocket.prototype.addEventListener = function (type, listener, options) {
      if (type !== "message" || typeof listener !== "function") return originalAddEventListener.call(this, type, listener, options);
      const socket = this;
      if (!socket.__codexDictationPreviewCloseListener) {
        socket.__codexDictationPreviewCloseListener = true;
        if (socket.url?.includes("/dictation")) originalAddEventListener.call(socket, "error", () => { rememberFailure("Helper connection failed"); replaceFailureTitle(); }, { once: true });
        originalAddEventListener.call(socket, "close", () => {
          const state = states.get(socket);
          if (socket.url?.includes("/dictation") && !state?.closing && !lastFailureReason) { rememberFailure("Helper connection closed"); replaceFailureTitle(); }
          if (!state) return;
          if (!state.closing) clearPreview(socket);
          else {
            const closingState = state;
            window.setTimeout(() => { if (states.get(socket) === closingState) clearPreview(socket); }, 1000);
          }
        }, { once: true });
      }
      return originalAddEventListener.call(socket, type, function (event) { try { handle(event, socket); } catch {} return listener.call(this, event); }, options);
    };
    window.__codexDictationTranscriptBridge__ = version;
  }

  let titleSettings = null;
  let titleSettingsAt = 0;
  const readTitleSettings = async (force) => {
    if (!force && titleSettings && Date.now() - titleSettingsAt < 10000) return titleSettings;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 1500);
    try {
      const response = await fetch(helperConfig.url, { cache: "no-store", signal: controller.signal });
      if (!response.ok) throw new Error(`Helper HTTP ${response.status}`);
      const state = await response.json();
      titleSettings = state?.title && typeof state.title === "object" ? state.title : { mode: "off" };
      titleSettingsAt = Date.now();
      return titleSettings;
    } finally {
      window.clearTimeout(timeout);
    }
  };
  const requestTitle = async (payload) => {
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 60000);
    try {
      const titleUrl = new URL("/title", helperConfig.url);
      titleUrl.search = new URL(helperConfig.url).search;
      const response = await fetch(titleUrl.toString(), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
        cache: "no-store",
        signal: controller.signal,
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.error || `Title helper HTTP ${response.status}`);
      return { title: typeof result.title === "string" ? result.title : "", description: typeof result.description === "string" ? result.description : "" };
    } finally {
      window.clearTimeout(timeout);
    }
  };
  globalThis.__CODEX_TITLE_ROUTER__ = {
    version,
    title(conversation, args) {
      const prompt = String(args?.prompt || "");
      if (!prompt || !titleSettings || titleSettings.mode === "off" || titleSettings.available === false) return void 0;
      return requestTitle({
        prompt,
        model: typeof conversation?.latestModel === "string" ? conversation.latestModel : null,
        provider: typeof conversation?.modelProvider === "string" ? conversation.modelProvider : null,
      });
    },
  };
  readTitleSettings(true).catch(() => {});

  const titleHint = (text, mode) => mode === "current" ? text.titleCurrentHint : mode === "custom" ? text.titleCustomHint : text.titleOffHint;
  const mountTitleSettings = (native) => {
    const language = locale();
    const text = copy[language];
    const existing = document.querySelector("[data-codex-title-settings]");
    if (existing?.dataset.codexTitleVersion === `${version}-${language}`) {
      const asrCard = document.querySelector("[data-codex-dictation-asr-settings]");
      if (asrCard && asrCard.nextElementSibling !== existing) asrCard.insertAdjacentElement("afterend", existing);
      return;
    }
    if (existing?.__codexTitleRefreshTimer) window.clearInterval(existing.__codexTitleRefreshTimer);
    existing?.remove();
    const referenceCard = native.dictionaryCard;
    if (!referenceCard) return;
    const section = referenceCard.cloneNode(false);
    detachFlowConstraints(section);
    section.dataset.codexTitleSettings = "";
    section.dataset.codexTitleVersion = `${version}-${language}`;
    section.removeAttribute("id");
    const contentRoot = referenceCard.firstElementChild?.cloneNode(false) || document.createElement("div");
    contentRoot.replaceChildren();
    section.append(contentRoot);
    section.style.setProperty("display", "block", "important");
    section.style.setProperty("width", "100%", "important");
    section.style.setProperty("box-sizing", "border-box", "important");
    contentRoot.style.setProperty("display", "block", "important");
    contentRoot.style.setProperty("width", "100%", "important");
    contentRoot.style.setProperty("box-sizing", "border-box", "important");
    const labelStyle = "display:grid !important;grid-template-rows:auto 36px !important;gap:7px !important;min-width:0 !important;font-size:14px !important;line-height:1.2 !important;color:var(--color-text-secondary,currentColor)";
    contentRoot.innerHTML = `
      <style>
        [data-codex-title-settings] [data-title-summary] { cursor:pointer; list-style:none; display:flex !important; align-items:center !important; justify-content:space-between !important; gap:16px !important; }
        [data-codex-title-settings] [data-title-summary]::-webkit-details-marker { display:none; }
        [data-codex-title-settings] [data-title-row] { display:flex !important; align-items:end !important; flex-wrap:wrap !important; gap:16px !important; width:100% !important; margin-top:16px !important; }
        [data-codex-title-settings] [data-title-row] button { flex:0 0 auto !important; width:auto !important; }
        [data-codex-title-settings] [data-title-fields] { display:grid !important; grid-template-columns:minmax(0,1fr) minmax(0,1fr) !important; gap:16px !important; align-items:end !important; width:100% !important; margin-top:16px !important; }
        @media (max-width: 640px) { [data-codex-title-settings] [data-title-fields] { grid-template-columns:minmax(0,1fr) !important; } }
        ${selectPopupCss}
      </style>
      <details data-title-details>
        <summary data-title-summary>
          <h2 style="font-size:16px !important;font-weight:600 !important;line-height:1.25 !important;margin:0 !important;color:var(--color-text-primary,currentColor)">${text.titleTitle}</h2>
          <span data-title-status aria-live="polite" style="font-size:14px !important;line-height:1.4 !important;color:var(--color-text-secondary,currentColor);opacity:.72;white-space:nowrap">${text.connecting}</span>
        </summary>
        <p data-title-hint style="font-size:14px !important;line-height:1.4 !important;color:var(--color-text-secondary,currentColor);margin:8px 0 0 !important">${text.titleOffHint}</p>
        <form data-title-form style="margin-top:0 !important">
          <div data-title-row>
            <label style="${labelStyle};flex:0 1 360px !important">${text.titleMode}
              <select name="mode"><option value="off">${text.titleOff}</option><option value="custom">${text.titleCustom}</option><option value="current">${text.titleCurrent}</option></select>
            </label>
            <button type="submit" style="height:36px !important;white-space:nowrap !important">${text.save}</button>
          </div>
          <div data-title-fields>
            <label style="${labelStyle}">${text.titleBaseUrl}
              <input name="baseUrl" autocomplete="off" placeholder="https://example.com/v1" />
            </label>
            <label style="${labelStyle}">${text.titleModel}
              <input name="model" autocomplete="off" placeholder="gpt-4o-mini" />
            </label>
            <label style="${labelStyle}">${text.titleWireApi}
              <select name="wireApi"><option value="chat">Chat Completions</option><option value="responses">Responses</option></select>
            </label>
            <label style="${labelStyle}">${text.titleApiKey}
              <input name="apiKey" type="password" autocomplete="new-password" minlength="8" maxlength="1024" placeholder="${text.placeholderKey}" />
            </label>
          </div>
        </form>
      </details>`;
    const details = section.querySelector("[data-title-details]");
    const form = section.querySelector("[data-title-form]");
    const status = section.querySelector("[data-title-status]");
    const hint = section.querySelector("[data-title-hint]");
    const fields = section.querySelector("[data-title-fields]");
    const modeInput = form.elements.mode;
    const wireInput = form.elements.wireApi;
    const baseUrlInput = form.elements.baseUrl;
    const modelInput = form.elements.model;
    const keyInput = form.elements.apiKey;
    let submitButton = form.querySelector('button[type="submit"]');
    const nativeButton = Array.from(native.dictionaryCard.querySelectorAll("button")).find(visible);
    if (nativeButton) {
      const replacement = nativeButton.cloneNode(true);
      replacement.type = "submit";
      replacement.textContent = text.save;
      replacement.disabled = false;
      replacement.removeAttribute("disabled");
      replacement.removeAttribute("aria-disabled");
      submitButton.replaceWith(replacement);
      submitButton = replacement;
    }
    const nativeInput = native.input;
    if (nativeInput) {
      const inputStyle = getComputedStyle(nativeInput);
      for (const control of [modeInput, baseUrlInput, modelInput, wireInput, keyInput]) {
        for (const property of ["font-family", "font-size", "font-weight", "line-height", "border-radius", "border", "background-color", "color", "padding", "color-scheme"]) control.style.setProperty(property, inputStyle.getPropertyValue(property), "important");
        control.style.setProperty("height", "36px", "important");
        control.style.setProperty("min-width", "0", "important");
        control.style.setProperty("width", "100%", "important");
        control.style.setProperty("box-sizing", "border-box", "important");
      }
    }
    const setStatus = (value, error = false) => {
      status.textContent = value;
      status.style.color = error ? "var(--color-text-error,#c33)" : "var(--color-text-secondary,currentColor)";
      status.style.opacity = error ? "1" : ".72";
    };
    const updateFields = () => {
      fields.style.setProperty("display", modeInput.value === "custom" ? "grid" : "none", "important");
      hint.textContent = titleHint(text, modeInput.value);
    };
    let dirty = false;
    modeInput.addEventListener("change", () => { dirty = true; updateFields(); });
    wireInput.addEventListener("change", () => { dirty = true; });
    for (const control of [baseUrlInput, modelInput, keyInput]) control.addEventListener("input", () => { dirty = true; });
    const refreshState = async () => {
      try {
        const state = await fetch(helperConfig.url, { cache: "no-store" }).then((response) => response.json());
        const title = state?.title && typeof state.title === "object" ? state.title : {};
        if (!dirty) {
          modeInput.value = ["off", "custom", "current"].includes(title.mode) ? title.mode : "off";
          if (document.activeElement !== baseUrlInput) baseUrlInput.value = title.baseUrl || "";
          if (document.activeElement !== modelInput) modelInput.value = title.model || "";
          wireInput.value = title.wireApi === "responses" ? "responses" : "chat";
          updateFields();
        }
        keyInput.placeholder = title.hasApiKey ? text.saved : text.placeholderKey;
        setStatus(modeInput.value === "off" ? text.titleOff : title.available === false ? text.titleUnavailable : text.configured);
      } catch {
        setStatus(text.unavailable, true);
      }
      readTitleSettings(true).catch(() => {});
    };
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      submitButton.disabled = true;
      setStatus(text.saving);
      try {
        const response = await fetch(helperConfig.url, {
          method: "POST",
          headers: { "Content-Type": "text/plain;charset=UTF-8" },
          body: JSON.stringify({
            titleMode: modeInput.value,
            titleBaseUrl: baseUrlInput.value.trim(),
            titleModel: modelInput.value.trim(),
            titleWireApi: wireInput.value,
            titleApiKey: keyInput.value.trim(),
          }),
        });
        const result = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(result.error || text.titleSaveError);
        keyInput.value = "";
        keyInput.placeholder = text.saved;
        dirty = false;
        setStatus(text.configured);
        readTitleSettings(true).catch(() => {});
      } catch (error) {
        setStatus(error instanceof Error ? error.message : text.titleSaveError, true);
      } finally {
        submitButton.disabled = false;
      }
    });
    const anchor = document.querySelector("[data-codex-dictation-asr-settings]") || native.anchor || referenceCard;
    anchor.insertAdjacentElement("afterend", section);
    details.open = true;
    updateFields();
    refreshState();
    section.__codexTitleRefreshTimer = window.setInterval(refreshState, 2500);
  };

  const mountVoiceSettings = () => {
    const existing = document.querySelector("[data-codex-dictation-asr-settings]");
    const native = dictationSettings();
    if (!native) {
      if (existing?.__codexDictationStateTimer) window.clearInterval(existing.__codexDictationStateTimer);
      existing?.remove();
      const staleTitle = document.querySelector("[data-codex-title-settings]");
      if (staleTitle?.__codexTitleRefreshTimer) window.clearInterval(staleTitle.__codexTitleRefreshTimer);
      staleTitle?.remove();
      return;
    }
    try { mountTitleSettings(native); } catch {}
    applyRecordingsCollapse(native);
    const language = locale();
    if (existing?.dataset.codexDictationAsrVersion === `${version}-${language}`) return;
    if (existing?.__codexDictationStateTimer) window.clearInterval(existing.__codexDictationStateTimer);
    existing?.remove();
    const referenceCard = native.dictionaryCard;
    if (!referenceCard) return;
    const section = referenceCard.cloneNode(false);
    detachFlowConstraints(section);
    section.dataset.codexDictationAsrSettings = "";
    section.dataset.codexDictationAsrVersion = `${version}-${language}`;
    section.removeAttribute("id");
    const contentTemplate = referenceCard.firstElementChild;
    const contentRoot = contentTemplate?.cloneNode(false) || document.createElement("div");
    contentRoot.replaceChildren();
    section.append(contentRoot);
    section.style.setProperty("display", "block", "important");
    section.style.setProperty("width", "100%", "important");
    section.style.setProperty("box-sizing", "border-box", "important");
    contentRoot.style.setProperty("display", "block", "important");
    contentRoot.style.setProperty("width", "100%", "important");
    contentRoot.style.setProperty("box-sizing", "border-box", "important");
    const text = copy[language];
    contentRoot.innerHTML = `
      <style>
        @media (max-width: 640px) {
          [data-codex-dictation-asr-settings] [data-asr-header] { align-items: flex-start !important; flex-direction: column !important; gap: 6px !important; }
          [data-codex-dictation-asr-settings] form { grid-template-columns: minmax(0, 1fr) !important; gap: 12px !important; }
        }
        [data-codex-dictation-asr-settings] [data-asr-form] button[type=submit] { justify-self: start !important; width: auto !important; }
      </style>
      <div data-asr-header style="display:flex !important;align-items:center !important;justify-content:space-between !important;gap:16px !important;width:100% !important">
        <h2 style="font-size:16px !important;font-weight:600 !important;line-height:1.25 !important;margin:0 !important;color:var(--color-text-primary,currentColor)">${text.title}</h2>
        <span data-asr-status aria-live="polite" style="font-size:14px !important;line-height:1.4 !important;color:var(--color-text-secondary,currentColor);opacity:.72;white-space:nowrap">${text.connecting}</span>
      </div>
      <p style="font-size:14px !important;line-height:1.4 !important;color:var(--color-text-secondary,currentColor);margin:6px 0 18px !important">${text.description}</p>
      <form data-asr-form style="display:grid !important;grid-template-columns:minmax(0,1fr) minmax(0,1fr) auto !important;gap:16px !important;align-items:end !important;width:100% !important">
        <label style="display:grid !important;grid-template-rows:auto 36px !important;gap:7px !important;min-width:0 !important;font-size:14px !important;line-height:1.2 !important;color:var(--color-text-secondary,currentColor)">${text.provider}
          <select name="provider" style="height:36px !important;min-width:0 !important;width:100% !important;box-sizing:border-box !important">
            <option value="aliyun">${text.aliyun}</option><option value="volcengine">${text.volcengine}</option>
          </select>
        </label>
        <label data-endpoint-label style="display:grid !important;grid-template-rows:auto 36px !important;gap:7px !important;min-width:0 !important;font-size:14px !important;line-height:1.2 !important;color:var(--color-text-secondary,currentColor)">${text.endpoint}
          <input name="endpointId" autocomplete="off" placeholder="${text.placeholderWorkspace}" pattern="[A-Za-z0-9-]{8,128}" title="${text.endpoint}" required style="height:36px !important;min-width:0 !important;width:100% !important;box-sizing:border-box !important" />
        </label>
        <label style="display:grid !important;grid-template-rows:auto 36px !important;gap:7px !important;min-width:0 !important;font-size:14px !important;line-height:1.2 !important;color:var(--color-text-secondary,currentColor)">${text.apiKey}
          <input name="apiKey" type="password" autocomplete="new-password" minlength="8" maxlength="1024" placeholder="${text.placeholderKey}" style="height:36px !important;min-width:0 !important;width:100% !important;box-sizing:border-box !important" />
        </label>
        <button type="submit" style="height:36px !important;align-self:end !important;white-space:nowrap !important">${text.save}</button>
      </form>
      <div style="border-top:1px solid var(--color-border-secondary,rgba(127,127,127,.2));margin:20px 0 16px !important"></div>
      <h3 data-retry-title style="font-size:16px !important;font-weight:600 !important;line-height:1.25 !important;margin:0 !important;color:var(--color-text-primary,currentColor)">${text.reconnectTitle}</h3>
      <p style="font-size:14px !important;line-height:1.4 !important;color:var(--color-text-secondary,currentColor);margin:6px 0 14px !important">${text.reconnectDescription}</p>
      <form data-retry-form style="display:grid !important;grid-template-columns:minmax(0,1fr) minmax(140px,180px) auto !important;gap:16px !important;align-items:end !important;width:100% !important">
        <div style="display:grid !important;gap:5px !important;min-width:0 !important">
          <span data-retry-provider style="font-size:14px !important;line-height:1.35 !important;color:var(--color-text-primary,currentColor)">${text.retryLoading}</span>
          <span data-retry-status aria-live="polite" style="font-size:13px !important;line-height:1.35 !important;color:var(--color-text-secondary,currentColor);opacity:.72">${text.retryDefault}</span>
        </div>
        <label style="display:grid !important;grid-template-rows:auto 36px !important;gap:7px !important;min-width:0 !important;font-size:14px !important;line-height:1.2 !important;color:var(--color-text-secondary,currentColor)">${text.retryCount}
          <input name="retryCount" type="number" min="0" max="9007199254740991" step="1" inputmode="numeric" required style="height:36px !important;min-width:0 !important;width:100% !important;box-sizing:border-box !important" />
        </label>
        <button type="submit" style="height:36px !important;align-self:end !important;white-space:nowrap !important">${text.retrySave}</button>
      </form>`;
    const nativeInput = native.input;
    const titles = section.querySelectorAll("h2,[data-retry-title]");
    const nativeTitle = Array.from(native.dictionaryCard.querySelectorAll("h1,h2,h3,p,span,div"))
      .filter((item) => visible(item) && item.children.length === 0 && (item.textContent || "").trim() && !item.closest("button"))
      .sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top)
      .find((item) => parseFloat(getComputedStyle(item).fontWeight) >= 500);
    if (titles.length && nativeTitle) {
      const style = getComputedStyle(nativeTitle);
      for (const title of titles) for (const property of ["font-size", "font-weight", "line-height", "font-family", "letter-spacing"]) title.style.setProperty(property, style.getPropertyValue(property), "important");
    }
    for (const input of Array.from(section.querySelectorAll("input"))) {
      if (nativeInput) {
        const replacement = nativeInput.cloneNode(false);
        replacement.name = input.name;
        replacement.type = input.type;
        replacement.autocomplete = input.autocomplete;
        replacement.required = input.required;
        for (const attribute of ["pattern", "title", "minlength", "maxlength", "min", "max", "step", "inputmode"]) {
          if (input.hasAttribute(attribute)) replacement.setAttribute(attribute, input.getAttribute(attribute));
          else replacement.removeAttribute(attribute);
        }
        replacement.removeAttribute("id");
        replacement.removeAttribute("data-dictation-dictionary-entry-index");
        input.replaceWith(replacement);
      }
      const target = section.querySelector(`input[name="${input.name}"]`);
      if (target && target.name !== "retryCount") target.placeholder = target.name === "endpointId" ? text.placeholderWorkspace : text.placeholderKey;
      target?.style.setProperty("height", "36px", "important");
      target?.style.setProperty("min-width", "0", "important");
      target?.style.setProperty("width", "100%", "important");
      target?.style.setProperty("box-sizing", "border-box", "important");
      target?.style.setProperty("display", "block", "important");
    }
    const nativeButton = Array.from(native.dictionaryCard.querySelectorAll("button")).find(visible);
    let saveButton = section.querySelector('[data-asr-form] button[type="submit"]');
    if (nativeButton) {
      const replacement = nativeButton.cloneNode(true);
      replacement.type = "submit";
      replacement.textContent = text.save;
      replacement.disabled = false;
      replacement.removeAttribute("disabled");
      replacement.removeAttribute("aria-disabled");
      saveButton.replaceWith(replacement);
      saveButton = replacement;
    }
    let retryButton = section.querySelector('[data-retry-form] button[type="submit"]');
    if (nativeButton) {
      const replacement = nativeButton.cloneNode(true);
      replacement.type = "submit";
      replacement.textContent = text.retrySave;
      replacement.disabled = true;
      replacement.setAttribute("disabled", "");
      retryButton.replaceWith(replacement);
      retryButton = replacement;
    }
    (native.anchor || referenceCard).insertAdjacentElement("afterend", section);

    const form = section.querySelector("[data-asr-form]");
    const providerInput = form.elements.provider;
    const endpointInput = form.elements.endpointId;
    const apiKeyInput = form.elements.apiKey;
    const endpointLabel = section.querySelector("[data-endpoint-label]");
    const status = section.querySelector("[data-asr-status]");
    const retryForm = section.querySelector("[data-retry-form]");
    const retryInput = retryForm.elements.retryCount;
    const retryProvider = section.querySelector("[data-retry-provider]");
    const retryStatus = section.querySelector("[data-retry-status]");
    if (nativeInput) {
      const inputStyle = getComputedStyle(nativeInput);
      for (const property of ["font-family", "font-size", "font-weight", "line-height", "border-radius", "border", "background-color", "color", "padding"]) {
        providerInput?.style.setProperty(property, inputStyle.getPropertyValue(property), "important");
        endpointInput?.style.setProperty(property, inputStyle.getPropertyValue(property), "important");
        apiKeyInput?.style.setProperty(property, inputStyle.getPropertyValue(property), "important");
        retryInput?.style.setProperty(property, inputStyle.getPropertyValue(property), "important");
      }
    }
    let workspaceDirty = false;
    endpointInput.addEventListener("input", () => { workspaceDirty = true; });
    let selectedProvider = providerInput.value || "aliyun";
    const updateProviderFields = () => {
      const volc = providerInput.value === "volcengine";
      endpointLabel.firstChild.textContent = `${volc ? text.resource : text.endpoint}`;
      endpointInput.placeholder = volc ? text.placeholderResource : text.placeholderWorkspace;
      endpointInput.pattern = volc ? "[A-Za-z0-9._-]{4,128}" : "[A-Za-z0-9-]{8,128}";
      endpointInput.title = volc ? text.resource : text.endpoint;
    };
    providerInput.addEventListener("change", () => {
      if (providerInput.value !== selectedProvider) endpointInput.value = "";
      selectedProvider = providerInput.value;
      workspaceDirty = true;
      updateProviderFields();
    });
    const setStatus = (text, error = false) => {
      status.textContent = text;
      status.style.color = error ? "var(--color-text-error,#c33)" : "var(--color-text-secondary,currentColor)";
      status.style.opacity = error ? "1" : ".72";
    };
    let stateTimer;
    const refreshState = () => {
      const controller = new AbortController();
      const timeout = window.setTimeout(() => controller.abort(), 1500);
      return fetch(helperConfig.url, { cache: "no-store", signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error(text.loadError);
        const state = await response.json();
        if (!workspaceDirty) {
          providerInput.value = state.provider || "aliyun";
          if (document.activeElement !== endpointInput) endpointInput.value = providerInput.value === "volcengine" ? (state.volcResourceId || "") : (state.workspaceId || "");
          selectedProvider = providerInput.value;
          updateProviderFields();
        }
        apiKeyInput.placeholder = state.hasApiKey ? text.saved : text.placeholderKey;
        setStatus(state.ready ? text.configured : text.notConfigured);
      })
      .catch(() => setStatus(text.unavailable, true))
      .finally(() => window.clearTimeout(timeout));
    };
    refreshState();
    stateTimer = window.setInterval(refreshState, 2500);
    section.__codexDictationStateTimer = stateTimer;
    const syncDictionary = async () => {
      const entries = Array.from(document.querySelectorAll("[data-dictation-dictionary-entry-index]"))
        .map((input) => input.value.trim())
        .filter(Boolean);
      try {
        const current = await fetch(helperConfig.url, { cache: "no-store" }).then((response) => response.json());
        if (!current) return;
        await fetch(helperConfig.url, {
          method: "POST",
          headers: { "Content-Type": "text/plain;charset=UTF-8" },
          body: JSON.stringify({ provider: current.provider, workspaceId: current.workspaceId, volcResourceId: current.volcResourceId, dictionary: entries }),
        });
      } catch {}
    };
    for (const input of Array.from(document.querySelectorAll("[data-dictation-dictionary-entry-index]"))) input.addEventListener("blur", syncDictionary);
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      const button = form.querySelector('button[type="submit"]');
      const dictionary = Array.from(document.querySelectorAll("[data-dictation-dictionary-entry-index]"))
        .map((input) => input.value.trim())
        .filter(Boolean);
      button.disabled = true;
      setStatus(text.saving);
      try {
        const response = await fetch(helperConfig.url, {
          method: "POST",
          headers: { "Content-Type": "text/plain;charset=UTF-8" },
          body: JSON.stringify({ provider: providerInput.value, workspaceId: providerInput.value === "aliyun" ? endpointInput.value.trim() : "", volcResourceId: providerInput.value === "volcengine" ? endpointInput.value.trim() : "", apiKey: apiKeyInput.value.trim(), dictionary }),
        });
        const result = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(result.error || text.saveError);
        apiKeyInput.value = "";
        apiKeyInput.placeholder = text.saved;
        workspaceDirty = false;
        setStatus(text.configured);
      } catch (error) {
        setStatus(error instanceof Error ? error.message : text.saveError, true);
      } finally {
        button.disabled = false;
      }
    });
    let retryState = null;
    let retryDirty = false;
    const setRetryStatus = (value, error = false) => {
      retryStatus.textContent = value;
      retryStatus.style.color = error ? "var(--color-text-error,#c33)" : "var(--color-text-secondary,currentColor)";
      retryStatus.style.opacity = error ? "1" : ".72";
    };
    const refreshRetryState = async () => {
      try {
        retryState = await readModelRetryConfig();
        retryProvider.textContent = `${text.currentProvider}: ${retryState.providerName}${retryState.providerName === retryState.providerId ? "" : ` (${retryState.providerId})`}`;
        retryInput.value = String(retryState.retries);
        retryInput.disabled = !retryState.editable;
        retryButton.disabled = !retryState.editable;
        if (retryState.editable) {
          retryInput.removeAttribute("disabled");
          retryButton.removeAttribute("disabled");
          setRetryStatus(retryState.retries === 5 ? text.retryDefault : text.reconnectDescription);
        } else setRetryStatus(text.retryUnsupported);
      } catch {
        retryState = null;
        retryProvider.textContent = text.retryUnavailable;
        retryInput.disabled = true;
        retryButton.disabled = true;
        setRetryStatus(text.retryUnavailable, true);
      }
    };
    retryInput.addEventListener("input", () => { retryDirty = true; });
    retryForm.addEventListener("submit", async (event) => {
      event.preventDefault();
      const retries = Number(retryInput.value);
      if (!Number.isSafeInteger(retries) || retries < 0) { setRetryStatus(text.retryInvalid, true); return; }
      retryButton.disabled = true;
      try {
        const current = await readModelRetryConfig();
        if (!current.editable) throw new Error(text.retryUnsupported);
        await current.client.sendRequest("config/batchWrite", {
          edits: [{ keyPath: `model_providers.${current.providerId}.stream_max_retries`, value: retries, mergeStrategy: "upsert" }],
          filePath: current.filePath,
          expectedVersion: current.expectedVersion,
          reloadUserConfig: true,
        });
        retryState = { ...current, retries };
        retryDirty = false;
        retryProvider.textContent = `${text.currentProvider}: ${current.providerName}${current.providerName === current.providerId ? "" : ` (${current.providerId})`}`;
        setRetryStatus(text.retrySaved);
      } catch (error) {
        setRetryStatus(error instanceof Error ? error.message : text.retrySaveError, true);
      } finally {
        retryButton.disabled = !retryState?.editable;
      }
    });
    refreshRetryState();
    stateTimer = window.setInterval(() => {
      refreshState();
      if (!retryDirty && document.activeElement !== retryInput) refreshRetryState();
    }, 2500);
    window.clearInterval(section.__codexDictationStateTimer);
    section.__codexDictationStateTimer = stateTimer;
  };

  const isConnectInfoUrl = (value) => {
    try {
      const url = new URL(typeof value === "string" ? value : value?.url || "", location.href);
      return url.pathname === "/codex/dictation-stream-connect-info";
    } catch { return false; }
  };

  function installConnectInfoFetchBridge() {
    if (window.__CODEX_DICTATION_FETCH_PATCHED__ === version) return;
    const originalFetch = window.fetch.bind(window);
    window.fetch = (input, init) => isConnectInfoUrl(input)
      ? Promise.resolve(new Response(JSON.stringify(connectInfo), { status: 200, headers: { "Content-Type": "application/json", "Cache-Control": "no-store" } }))
      : originalFetch(input, init);
    window.__CODEX_DICTATION_FETCH_PATCHED__ = version;
  }

  async function patchConnectInfo() {
    installConnectInfoFetchBridge();
    const appUrl = assetUrls().find((url) => /\/app-initial-[^/]+\.js(?:\?|$)/.test(url));
    if (!appUrl) throw new Error("Codex app-initial asset not found");
    const module = await import(appUrl);
    let patched = 0;
    for (const value of Object.values(module)) {
      if (typeof value?.getInstance !== "function") continue;
      try {
        const client = value.getInstance();
        if (!client || typeof client.post !== "function" || client.__codexDictationAsrPatched) continue;
        const originalPost = client.post.bind(client);
        client.post = (url, ...args) => isConnectInfoUrl(url)
          ? Promise.resolve({ body: connectInfo, headers: {}, status: 200 })
          : originalPost(url, ...args);
        client.__codexDictationAsrPatched = true;
        patched += 1;
      } catch {}
    }
    if (patched === 0) throw new Error("Codex HTTP client not found");
  }

  async function patchDictationCapability() {
    if (window.__CODEX_DICTATION_CAPABILITY_PATCHED__ === version) return;
    const urls = assetUrls().filter((url) => /\/app-initial-[^/]+\.js(?:\?|$)/.test(url));
    for (const url of urls) {
      try {
        const module = await import(url);
        for (const key of Object.keys(module)) {
          const fn = module[key];
          if (typeof fn !== "function" || fn.__codexDictationPatched === version) continue;
          const source = String(fn);
          if (!source.includes("authMethod") || !source.includes("chatgpt")) continue;
          const original = fn;
          const wrapped = function (...args) {
            const result = original.apply(this, args);
            if (result === false && args.some((arg) => arg && typeof arg === "object" && (arg.authMethod === "apikey" || arg.authMethod === "apiKey"))) return true;
            return result;
          };
          wrapped.__codexDictationPatched = version;
          try { module[key] = wrapped; } catch { continue; }
          window.__CODEX_DICTATION_CAPABILITY_PATCHED__ = version;
          return;
        }
      } catch {}
    }
  }

  function ensureNativeDictationHotkey() {
    const rows = Array.from(document.querySelectorAll("button,[role=button]")).filter(visible);
    const target = rows.find((element) => {
      const text = `${element.textContent || ""} ${element.getAttribute("aria-label") || ""}`.toLowerCase();
      const row = element.parentElement?.parentElement?.textContent?.toLowerCase() || "";
      return (text.includes("off") || text.includes("hold-to-dictate") || text.includes("hold to dictate")) &&
        (row.includes("hold-to-dictate") || row.includes("hold to dictate") || row.includes("toggle dictation"));
    });
    if (!target || target.disabled || target.dataset.codexDictationHotkeyAttempted === version) return;
    const rowText = target.parentElement?.parentElement?.textContent?.toLowerCase() || "";
    if (!rowText.includes("off")) return;
    target.dataset.codexDictationHotkeyAttempted = version;
    target.click();
    window.setTimeout(() => {
      for (const type of ["keydown", "keyup"]) {
        document.dispatchEvent(new KeyboardEvent(type, {
          key: "d",
          code: "KeyD",
          ctrlKey: true,
          altKey: true,
          bubbles: true,
          cancelable: true,
        }));
      }
    }, 120);
  }

  function unlockKeepVisibleSwitch() {
    const row = Array.from(document.querySelectorAll("button,[role=button],[role=switch]")).find((element) => {
      if (!visible(element)) return false;
      const text = `${element.textContent || ""} ${element.getAttribute("aria-label") || ""} ${element.parentElement?.parentElement?.textContent || ""}`.toLowerCase();
      return text.includes("keep dictation bar visible");
    });
    if (!row) return;
    row.removeAttribute("disabled");
    row.removeAttribute("aria-disabled");
    if ("disabled" in row) row.disabled = false;
  }

  window.__CODEX_DICTATION_ASR_VERSION__ = version;
  installFailureReasonBridge();
  installTranscriptPreviewBridge();
  mountVoiceSettings();
  window.__codexDictationAsrTimer = window.setInterval(mountVoiceSettings, 1000);
  window.setInterval(() => {
    patchConnectInfo().catch(() => {});
    patchDictationCapability().catch(() => {});
    ensureNativeDictationHotkey();
    unlockKeepVisibleSwitch();
    readTitleSettings(true).catch(() => {});
  }, 2000);
  patchConnectInfo().catch(() => {});
  patchDictationCapability().catch(() => {});
  ensureNativeDictationHotkey();
  unlockKeepVisibleSwitch();
})();
