(() => {
  "use strict";
  const version = "47";
  const connectInfo = __CONNECT_INFO__;
  const helperConfig = __HELPER_CONFIG__;
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
    },
  };
  const cardFor = (element) => {
    let card = element?.parentElement;
    while (card && card !== document.body) {
      const rect = card.getBoundingClientRect();
      if (visible(card) && rect.width >= 500 && rect.height >= 100 && parseFloat(getComputedStyle(card).borderRadius) > 0) return card;
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
    const cards = Array.from(container.children).filter((item) => visible(item) && parseFloat(getComputedStyle(item).borderRadius) > 0);
    return cards.includes(dictionaryCard) ? { input, dictionaryCard, container, cards } : null;
  };

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
      const active = composers.find((item) => item.view.dom.contains(document.activeElement) || item.view.dom === document.activeElement);
      const controller = active || (composers.includes(lastFocusedComposer) ? lastFocusedComposer : null) || (composers.length === 1 ? composers[0] : null);
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
        clearPreview(socket);
      } else if (message.type === "speech.started") {
        const state = ensureState(socket);
        if (state) {
          const id = String(message.utterance_id || "default");
          if (!state.textByUtterance.has(id)) {
            state.order.push(id);
            state.textByUtterance.set(id, "");
          }
        }
      } else if (message.type === "transcript.delta" || message.type === "transcript.final") {
        updateUtterance(message, socket);
      } else if (message.type === "transcript.failed" || message.type === "session.error") {
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
        originalAddEventListener.call(socket, "close", () => {
          const state = states.get(socket);
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

  const mountVoiceSettings = () => {
    const existing = document.querySelector("[data-codex-dictation-asr-settings]");
    const native = dictationSettings();
    if (!native) {
      if (existing?.__codexDictationStateTimer) window.clearInterval(existing.__codexDictationStateTimer);
      existing?.remove();
      return;
    }
    const language = locale();
    if (existing?.dataset.codexDictationAsrVersion === `${version}-${language}`) return;
    if (existing?.__codexDictationStateTimer) window.clearInterval(existing.__codexDictationStateTimer);
    existing?.remove();
    const referenceCard = native.cards.at(-1);
    if (!referenceCard) return;
    const section = referenceCard.cloneNode(false);
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
      </style>
      <div data-asr-header style="display:flex !important;align-items:center !important;justify-content:space-between !important;gap:16px !important;width:100% !important">
        <h2 style="font-size:16px !important;font-weight:600 !important;line-height:1.25 !important;margin:0 !important;color:var(--color-text-primary,currentColor)">${text.title}</h2>
        <span data-asr-status aria-live="polite" style="font-size:14px !important;line-height:1.4 !important;color:var(--color-text-secondary,currentColor);opacity:.72;white-space:nowrap">${text.connecting}</span>
      </div>
      <p style="font-size:14px !important;line-height:1.4 !important;color:var(--color-text-secondary,currentColor);margin:6px 0 18px !important">${text.description}</p>
      <form style="display:grid !important;grid-template-columns:minmax(0,1fr) minmax(0,1fr) auto !important;gap:16px !important;align-items:end !important;width:100% !important">
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
      </form>`;
    const nativeInput = native.input;
    const title = section.querySelector("h2");
    const nativeTitle = Array.from(native.dictionaryCard.querySelectorAll("h1,h2,h3,p,span,div"))
      .filter((item) => visible(item) && item.children.length === 0 && (item.textContent || "").trim() && !item.closest("button"))
      .sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top)
      .find((item) => parseFloat(getComputedStyle(item).fontWeight) >= 500);
    if (title && nativeTitle) {
      const style = getComputedStyle(nativeTitle);
      for (const property of ["font-size", "font-weight", "line-height", "font-family", "letter-spacing"]) title.style.setProperty(property, style.getPropertyValue(property), "important");
    }
    for (const input of Array.from(section.querySelectorAll("input"))) {
      if (nativeInput) {
        const replacement = nativeInput.cloneNode(false);
        replacement.name = input.name;
        replacement.type = input.type;
        replacement.autocomplete = input.autocomplete;
        replacement.required = input.required;
        for (const attribute of ["pattern", "title", "minlength", "maxlength"]) {
          if (input.hasAttribute(attribute)) replacement.setAttribute(attribute, input.getAttribute(attribute));
          else replacement.removeAttribute(attribute);
        }
        replacement.removeAttribute("id");
        replacement.removeAttribute("data-dictation-dictionary-entry-index");
        input.replaceWith(replacement);
      }
      const target = section.querySelector(`input[name="${input.name}"]`);
      if (target) target.placeholder = target.name === "endpointId" ? text.placeholderWorkspace : text.placeholderKey;
      target?.style.setProperty("height", "36px", "important");
      target?.style.setProperty("min-width", "0", "important");
      target?.style.setProperty("width", "100%", "important");
      target?.style.setProperty("box-sizing", "border-box", "important");
      target?.style.setProperty("display", "block", "important");
    }
    const nativeButton = Array.from(native.dictionaryCard.querySelectorAll("button")).find(visible);
    let saveButton = section.querySelector('button[type="submit"]');
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
    referenceCard.insertAdjacentElement("afterend", section);

    const form = section.querySelector("form");
    const providerInput = form.elements.provider;
    const endpointInput = form.elements.endpointId;
    const apiKeyInput = form.elements.apiKey;
    const endpointLabel = section.querySelector("[data-endpoint-label]");
    const status = section.querySelector("[data-asr-status]");
    if (nativeInput) {
      const inputStyle = getComputedStyle(nativeInput);
      for (const property of ["font-family", "font-size", "font-weight", "line-height", "border-radius", "border", "background-color", "color", "padding"]) {
        providerInput?.style.setProperty(property, inputStyle.getPropertyValue(property), "important");
        endpointInput?.style.setProperty(property, inputStyle.getPropertyValue(property), "important");
        apiKeyInput?.style.setProperty(property, inputStyle.getPropertyValue(property), "important");
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
  };

  async function patchConnectInfo() {
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
        client.post = (url, ...args) => url === "/codex/dictation-stream-connect-info"
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
  installTranscriptPreviewBridge();
  mountVoiceSettings();
  window.__codexDictationAsrTimer = window.setInterval(mountVoiceSettings, 1000);
  window.setInterval(() => {
    patchConnectInfo().catch(() => {});
    patchDictationCapability().catch(() => {});
    ensureNativeDictationHotkey();
    unlockKeepVisibleSwitch();
  }, 2000);
  patchConnectInfo().catch(() => {});
  patchDictationCapability().catch(() => {});
  ensureNativeDictationHotkey();
  unlockKeepVisibleSwitch();
})();
