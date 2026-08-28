(() => {
  "use strict";
  const version = "40";
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
      title: "Dictation ASR · Aliyun DashScope",
      description: "Uses Aliyun DashScope real-time ASR.",
      workspace: "Aliyun Workspace ID",
      apiKey: "Aliyun API key",
      save: "Save",
      connecting: "Connecting to helper...",
      configured: "Configured",
      notConfigured: "Not configured",
      unavailable: "Helper unavailable",
      saving: "Saving",
      saved: "Saved",
      placeholderWorkspace: "ws-...",
      placeholderKey: "sk-...",
      saveError: "Unable to save ASR settings",
      loadError: "Unable to load ASR settings",
    },
    zh: {
      title: "听写 ASR · 阿里云 DashScope",
      description: "使用阿里云 DashScope 实时语音识别。",
      workspace: "阿里云 Workspace ID",
      apiKey: "阿里云 API Key",
      save: "保存",
      connecting: "正在连接助手…",
      configured: "已配置",
      notConfigured: "未配置",
      unavailable: "助手不可用",
      saving: "正在保存",
      saved: "已保存",
      placeholderWorkspace: "ws-…",
      placeholderKey: "sk-…",
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

  // Codex currently ignores transcript.delta; mirror its preview into the focused composer.
  function installTranscriptPreviewBridge() {
    if (window.__codexDictationTranscriptBridge__) return;
    const originalAddEventListener = WebSocket.prototype.addEventListener;
    let base = null;
    let preview = "";
    let nativeInsert = null;
    let nativeInsertOriginal = null;
    let skipNativeFinal = false;
    let nativeController = null;
    const visibleTextboxes = () => Array.from(document.querySelectorAll("textarea,[contenteditable='true'],[role='textbox']")).filter(visible);
    const target = () => visibleTextboxes().find((node) => node === document.activeElement) || visibleTextboxes().at(-1);
    const read = (node) => node?.matches("textarea,input") ? node.value : (node?.innerText || node?.textContent || "");
    const begin = () => {
      const node = target();
      if (!node) return null;
      const text = read(node);
      if (node.matches("textarea,input")) {
        return { node, text, start: node.selectionStart ?? text.length, end: node.selectionEnd ?? text.length };
      }
      const selection = window.getSelection();
      const range = selection?.rangeCount ? selection.getRangeAt(0) : null;
      if (range && node.contains(range.startContainer)) {
        const before = range.cloneRange();
        before.selectNodeContents(node);
        before.setEnd(range.startContainer, range.startOffset);
        return { node, text, range: range.cloneRange(), inserted: null };
      }
      const end = document.createRange();
      end.selectNodeContents(node);
      end.collapse(false);
      return { node, text, range: end, inserted: null };
    };
    const findNativeController = (node) => {
      if (!node) return null;
      const fiberKey = Object.keys(node).find((key) => key.startsWith("__reactFiber$"));
      let fiber = fiberKey ? node[fiberKey] : null;
      for (let depth = 0; fiber && depth < 100; depth += 1, fiber = fiber.return) {
        const props = fiber.memoizedProps;
        const values = props && typeof props === "object" ? Object.values(props) : [];
        for (const value of values) {
          if (value && typeof value.insertDictationText === "function") return value;
        }
      }
      return null;
    };
    const findAnyNativeController = () => {
      for (const node of visibleTextboxes()) {
        const controller = findNativeController(node);
        if (controller) return controller;
      }
      return null;
    };
    const patchNativeController = (controller) => {
      if (!controller || controller.__codexDictationPatched === version) return;
      const original = controller.insertDictationText.bind(controller);
      controller.insertDictationText = (text) => {
        if (skipNativeFinal) {
          skipNativeFinal = false;
          return;
        }
        return original(text);
      };
      controller.__codexDictationPatched = version;
    };
    const write = (node, value) => {
      if (!node) return;
      if (node.matches("textarea,input")) {
        const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")?.set || Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set;
        setter?.call(node, value);
      } else node.textContent = value;
      node.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: value }));
    };
    const render = () => {
      if (!base?.node?.isConnected) return;
      if (base.node.matches("textarea,input")) {
        const value = base.text.slice(0, base.start) + preview + base.text.slice(base.end);
        write(base.node, value);
        const caret = base.start + preview.length;
        base.node.setSelectionRange(caret, caret);
        return;
      }
      if (base.inserted) base.inserted.deleteContents();
      const insertion = document.createTextNode(preview);
      base.range.deleteContents();
      base.range.insertNode(insertion);
      base.inserted = document.createRange();
      base.inserted.selectNode(insertion);
      const selection = window.getSelection();
      selection?.removeAllRanges();
      const caret = document.createRange();
      caret.setStartAfter(insertion);
      caret.collapse(true);
      selection?.addRange(caret);
      base.range = caret;
      base.node.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: preview }));
    };
    const handle = (event) => {
      if (typeof event.data !== "string") return;
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      if (message.type === "speech.started") {
        skipNativeFinal = false;
        base = begin();
        preview = "";
        nativeController = findNativeController(base?.node) || findAnyNativeController();
        patchNativeController(nativeController);
        nativeInsert = nativeController?.insertDictationText?.bind(nativeController) || null;
        nativeInsertOriginal = nativeInsert;
      } else if (message.type === "transcript.delta" && base?.node?.isConnected) {
        const next = String(message.text || "");
        if (nativeInsert && (preview === "" || next.startsWith(preview))) {
          const suffix = next.slice(preview.length);
          if (suffix) nativeInsert(suffix);
          preview = next;
        } else {
          preview = next;
          render();
        }
      } else if (message.type === "transcript.delta") {
        // Some providers emit the first transcript before speech.started.
        base = begin();
        preview = String(message.text || "");
        nativeController = findNativeController(base?.node) || findAnyNativeController();
        patchNativeController(nativeController);
        nativeInsert = nativeController?.insertDictationText?.bind(nativeController) || null;
        nativeInsertOriginal = nativeInsert;
        if (nativeInsertOriginal) nativeInsertOriginal(preview);
        else render();
      } else if (message.type === "transcript.final") {
        if (nativeInsertOriginal) {
          skipNativeFinal = true;
          patchNativeController(findNativeController(target()) || findAnyNativeController());
        }
        if (!nativeInsert && base?.node?.isConnected && preview) {
          preview = "";
          if (base.node.matches("textarea,input")) render();
          else if (base.inserted) {
            const caret = document.createRange();
            caret.setStart(base.inserted.startContainer, base.inserted.startOffset);
            caret.collapse(true);
            base.inserted.deleteContents();
            const selection = window.getSelection();
            selection?.removeAllRanges();
            selection?.addRange(caret);
          }
        }
        base = null;
        preview = "";
        nativeInsert = null;
        nativeInsertOriginal = null;
        nativeController = null;
      }
    };
    WebSocket.prototype.addEventListener = function (type, listener, options) {
      if (type !== "message" || typeof listener !== "function") return originalAddEventListener.call(this, type, listener, options);
      const wrapped = function (event) { handle(event); return listener.call(this, event); };
      return originalAddEventListener.call(this, type, wrapped, options);
    };
    const onMessage = Object.getOwnPropertyDescriptor(WebSocket.prototype, "onmessage");
    if (onMessage?.set && onMessage.get) {
      Object.defineProperty(WebSocket.prototype, "onmessage", {
        configurable: onMessage.configurable,
        enumerable: onMessage.enumerable,
        get: onMessage.get,
        set(listener) {
          return onMessage.set.call(this, typeof listener === "function"
            ? function (event) { handle(event); return listener.call(this, event); }
            : listener);
        },
      });
    }
    window.__codexDictationTranscriptBridge__ = true;
  }

  const mountVoiceSettings = () => {
    const existing = document.querySelector("[data-codex-dictation-asr-settings]");
    const native = dictationSettings();
    if (!native) {
      existing?.remove();
      return;
    }
    const language = locale();
    if (existing?.dataset.codexDictationAsrVersion === `${version}-${language}`) return;
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
      <div data-asr-header style="display:flex !important;align-items:center !important;justify-content:space-between !important;gap:16px !important;width:100% !important">
        <h2 style="font-size:16px !important;font-weight:600 !important;line-height:1.25 !important;margin:0 !important;color:var(--color-text-primary,currentColor)">${text.title}</h2>
        <span data-asr-status aria-live="polite" style="font-size:14px !important;line-height:1.4 !important;color:var(--color-text-secondary,currentColor);opacity:.72;white-space:nowrap">${text.connecting}</span>
      </div>
      <p style="font-size:14px !important;line-height:1.4 !important;color:var(--color-text-secondary,currentColor);margin:6px 0 18px !important">${text.description}</p>
      <form style="display:grid !important;grid-template-columns:minmax(0,1fr) minmax(0,1fr) auto !important;gap:16px !important;align-items:end !important;width:100% !important">
        <label style="display:grid !important;grid-template-rows:auto 36px !important;gap:7px !important;min-width:0 !important;font-size:14px !important;line-height:1.2 !important;color:var(--color-text-secondary,currentColor)">${text.workspace}
          <input name="workspaceId" autocomplete="off" placeholder="${text.placeholderWorkspace}" required style="height:36px !important;min-width:0 !important;width:100% !important;box-sizing:border-box !important" />
        </label>
        <label style="display:grid !important;grid-template-rows:auto 36px !important;gap:7px !important;min-width:0 !important;font-size:14px !important;line-height:1.2 !important;color:var(--color-text-secondary,currentColor)">${text.apiKey}
          <input name="apiKey" type="password" autocomplete="new-password" placeholder="${text.placeholderKey}" style="height:36px !important;min-width:0 !important;width:100% !important;box-sizing:border-box !important" />
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
        input.replaceWith(replacement);
      }
      const target = section.querySelector(`input[name="${input.name}"]`);
      if (target) target.placeholder = target.name === "workspaceId" ? text.placeholderWorkspace : text.placeholderKey;
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
    const workspaceInput = form.elements.workspaceId;
    const apiKeyInput = form.elements.apiKey;
    const status = section.querySelector("[data-asr-status]");
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
        workspaceInput.value = state.workspaceId || "";
        apiKeyInput.placeholder = state.hasApiKey ? text.saved : text.placeholderKey;
        setStatus(state.ready ? text.configured : text.notConfigured);
        if (state.ready) {
          window.clearInterval(stateTimer);
          stateTimer = undefined;
        }
      })
      .catch(() => setStatus(text.unavailable, true))
      .finally(() => window.clearTimeout(timeout));
    };
    refreshState();
    stateTimer = window.setInterval(refreshState, 2500);
    const syncDictionary = async () => {
      const entries = Array.from(document.querySelectorAll("[data-dictation-dictionary-entry-index]"))
        .map((input) => input.value.trim())
        .filter(Boolean);
      try {
        const current = await fetch(helperConfig.url, { cache: "no-store" }).then((response) => response.json());
        if (!current.workspaceId) return;
        await fetch(helperConfig.url, {
          method: "POST",
          headers: { "Content-Type": "text/plain;charset=UTF-8" },
          body: JSON.stringify({ workspaceId: current.workspaceId, dictionary: entries }),
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
          body: JSON.stringify({ workspaceId: workspaceInput.value.trim(), apiKey: apiKeyInput.value.trim(), dictionary }),
        });
        const result = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(result.error || text.saveError);
        apiKeyInput.value = "";
        apiKeyInput.placeholder = text.saved;
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
