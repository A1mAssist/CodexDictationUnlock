/**
 * Codex Context Panel — CDP injection.
 *
 * Renders a context breakdown panel inside the Codex desktop UI. The data
 * comes from CodexDictation's local helper, which reads the rollout files
 * Codex already writes and returns only token counts.
 *
 * The big number is the provider's own count — the same number the native
 * "Context usage" ring shows, so it is exact. The meter below it covers the
 * whole context window: transcript content, the compact buffer, and the
 * unused remainder, all scaled to the provider's window.
 *
 * Injected by CodexDictation with the rest of its runtime script. Safe to
 * inject repeatedly: the version guard makes re-injection a no-op.
 */
(() => {
  "use strict";
  const VERSION = "35";
  const PANEL_ID = "codex-context-panel";
  const helperConfig = __HELPER_CONFIG__;
  if (location.href.includes('avatar-overlay') || location.href.includes('quick-chat')) return;
  // The guard exists to make repeated injection a no-op, but a Codex reload
  // wipes the panel and leaves the version marker behind. Treat a missing
  // panel as "not installed" so the script can mount again.
  if (window.__CODEX_CONTEXT_PANEL_VERSION__ === VERSION && document.getElementById(PANEL_ID) && window.__codexContextPanelTimer) return;
  window.__CODEX_CONTEXT_PANEL_VERSION__ = VERSION;
  if (window.__codexContextPanelTimer) window.clearInterval(window.__codexContextPanelTimer);
  document.getElementById(PANEL_ID)?.remove();
  window.__CODEX_CONTEXT_PANEL_STATE__ = "idle";


  const locale = () => ((document.documentElement.lang || navigator.language || "en").toLowerCase().startsWith("zh") ? "zh" : "en");
  const copy = {
    en: {
      title: "Context",
      live: "Live window",
      used: "used", of: "of", window: "window",
      bands: "Context window",
      auto: "Auto compact",
      threshold: "Threshold",
      remaining: "Remaining",
      endOfTurn: "End-of-turn",
      cap: "90% cap",
      atLimit: "at limit",
      turns: (n) => `≈${n} ${n === 1 ? "turn" : "turns"}`,
      top: "Top 3 consumers",
      unavailable: "Context helper unavailable",
      provider: "Provider actual",
      cached: "cached", cacheHit: "Cache hit", output: "output",
      refresh: "Refresh",
      collapse: "Hide",
      expand: "Show",
    },
    zh: {
      title: "上下文",
      live: "实时窗口",
      used: "已用", of: "/", window: "窗口",
      bands: "上下文窗口",
      auto: "自动压缩",
      threshold: "阈值",
      remaining: "剩余",
      endOfTurn: "回合结束",
      cap: "90% 上限",
      atLimit: "已到阈值",
      turns: (n) => `≈${n} 轮`,
      top: "占用 Top 3",
      unavailable: "无法连接上下文助手",
      provider: "Provider 实测",
      cached: "缓存", cacheHit: "缓存命中", output: "输出",
      refresh: "刷新",
      collapse: "收起",
      expand: "展开",
    },
  };
  const t = () => copy[locale()];

  const num = (n) => (n == null ? "—" : n >= 1000 ? (n / 1000).toFixed(n >= 100000 ? 0 : 1) + "k" : String(n));
  const shortDetail = (detail) => {
    const value = String(detail || "");
    if (/^(?:[A-Za-z]:[\\/]|\\\\|\/)/.test(value)) return value.split(/[\\/]/).filter(Boolean).at(-1) || value;
    return value;
  };

  // ---- native official number -------------------------------------------
  const nativeUsage = () => {
    const ring = Array.from(document.querySelectorAll("[aria-label]"))
      .find((el) => /^Context usage:/i.test(el.getAttribute("aria-label") || ""));
    const label = ring && ring.getAttribute("aria-label");
    const m = label && /(\d+(?:\.\d+)?)\s*%/.exec(label);
    return { percent: m ? Number(m[1]) : null, label: label || null };
  };

  // ---- panel DOM ---------------------------------------------------------
  const style = document.createElement("style");
  style.id = "codex-context-panel-style";
  style.textContent = `
    [data-codex-context-panel] { pointer-events: auto; }
    [data-codex-context-panel] { width: 300px; max-width: 100%; }
    [data-codex-context-panel].ccp-float {
      position: absolute; top: 8px; right: 8px; z-index: 30; pointer-events: auto;
      background: var(--color-surface-elevated-secondary, rgb(23,23,23));
    }
    [data-codex-context-panel] .ccp-card {
      width: 100%; box-sizing: border-box;
      color: var(--color-text-primary, #fff); padding: 14px 16px 14px;
      display: flex; flex-direction: column; gap: 8px; font-size: 13px; line-height: 1.4;
    }
    [data-codex-context-panel] .ccp-head { display: flex; align-items: center; gap: 8px; }
    [data-codex-context-panel] .ccp-title { font-weight: 600; font-size: 13px; flex: none; }
    [data-codex-context-panel] .ccp-sub {
      color: var(--color-text-secondary, rgba(255,255,255,.65)); font-size: 12px;
      flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
    [data-codex-context-panel] .ccp-details { display: flex; flex-direction: column; gap: 8px; }
    [data-codex-context-panel] .ccp-refresh, [data-codex-context-panel] .ccp-collapse {
      background: transparent; border: 1px solid var(--color-border-secondary, rgba(255,255,255,.12));
      color: var(--color-text-secondary, rgba(255,255,255,.65)); border-radius: 8px; padding: 2px 8px;
      font-size: 11px; cursor: pointer;
    }
    [data-codex-context-panel] .ccp-collapse { margin-left: auto; }
    [data-codex-context-panel] .ccp-refresh:hover, [data-codex-context-panel] .ccp-collapse:hover { color: var(--color-text-primary, #fff); background: var(--color-surface-secondary, rgba(255,255,255,.06)); }
    [data-codex-context-panel] .ccp-meter {
      position: relative;
      height: 14px; box-sizing: border-box; border-radius: 999px;
      padding: 2px; background: #0b0b0d;
      box-shadow: 0 0 0 1px rgba(148,163,184,.40), 0 0 8px rgba(0,0,0,.35);
      display: flex;
    }
    [data-codex-context-panel] .ccp-meter-inner {
      display: flex; width: 100%; height: 100%; border-radius: 999px; overflow: hidden;
      background: var(--color-surface-secondary, rgba(255,255,255,.08));
    }
    [data-codex-context-panel] .ccp-meter-inner > i { display: block; height: 100%; }
    [data-codex-context-panel] .ccp-mark {
      position: absolute; top: -2px; bottom: -2px; width: 1px; background: #f59e0b;
      box-shadow: 0 0 0 1px rgba(0,0,0,.55); pointer-events: none; z-index: 1;
    }
    [data-codex-context-panel] .ccp-row { display: flex; align-items: baseline; gap: 6px; }
    [data-codex-context-panel] .ccp-big { font-size: 20px; font-weight: 600; font-variant-numeric: tabular-nums; }
    [data-codex-context-panel] .ccp-label { color: var(--color-text-secondary, rgba(255,255,255,.65)); font-size: 12px; }
    [data-codex-context-panel] .ccp-section { border-top: 1px solid var(--color-border-secondary, rgba(255,255,255,.08)); padding-top: 6px; }
    [data-codex-context-panel] .ccp-legend { display: flex; flex-direction: column; gap: 2px; }
    [data-codex-context-panel] .ccp-legend-row { display: flex; align-items: center; gap: 8px; min-width: 0; }
    [data-codex-context-panel] .ccp-dot { width: 8px; height: 8px; border-radius: 2px; flex: none; }
    [data-codex-context-panel] .ccp-name { flex: 1; min-width: 0; color: var(--color-text-secondary, rgba(255,255,255,.65)); white-space: nowrap; }
    [data-codex-context-panel] .ccp-val { font-variant-numeric: tabular-nums; color: var(--color-text-primary, #fff); }
    [data-codex-context-panel] .ccp-pct { font-variant-numeric: tabular-nums; color: var(--color-text-secondary, rgba(255,255,255,.65)); width: 40px; text-align: right; }
    [data-codex-context-panel] .ccp-kv { display: flex; justify-content: space-between; gap: 10px; }
    [data-codex-context-panel] .ccp-kv span:first-child { color: var(--color-text-secondary, rgba(255,255,255,.65)); }
    [data-codex-context-panel] .ccp-kv span:last-child { font-variant-numeric: tabular-nums; }
    [data-codex-context-panel] .ccp-consumer-head { display: flex; align-items: baseline; gap: 6px; min-width: 0; }
    [data-codex-context-panel] .ccp-consumer-head .ccp-name {
      color: var(--color-text-primary, #fff); flex: none; max-width: 48%;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
    [data-codex-context-panel] .ccp-detail {
      color: var(--color-text-secondary, rgba(255,255,255,.65)); font-size: 11px; flex: 1;
      min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
  `;
  // Replace any earlier copy so a re-injection picks up new styles.
  document.getElementById(style.id)?.remove();
  document.head.appendChild(style);

  const el = (tag, cls, text) => {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text != null) node.textContent = text;
    return node;
  };

  let refs = null;
  let compactConfig = null;
  let compactConfigLoaded = false;
  let collapsed = false;
  const collapsedKey = "codex-context-panel-collapsed";
  try { collapsed = window.localStorage.getItem(collapsedKey) === "1"; } catch {}
  const applyCollapsed = () => {
    if (!refs) return;
    const text = t();
    const label = collapsed ? text.expand : text.collapse;
    refs.details.style.display = collapsed ? "none" : "";
    refs.collapse.textContent = label;
    refs.collapse.setAttribute("aria-expanded", String(!collapsed));
    refs.collapse.setAttribute("aria-label", label);
    refs.collapse.title = label;
  };

  const buildCard = () => {
    const card = el("div", "ccp-card");
    const head = el("div", "ccp-head");
    head.appendChild(el("span", "ccp-title", t().title));
    const sub = el("span", "ccp-sub", "");
    head.appendChild(sub);
    const collapse = el("button", "ccp-collapse", "");
    collapse.type = "button";
    collapse.addEventListener("click", () => {
      collapsed = !collapsed;
      try { window.localStorage.setItem(collapsedKey, collapsed ? "1" : "0"); } catch {}
      applyCollapsed();
    });
    head.appendChild(collapse);
    const refresh = el("button", "ccp-refresh", t().refresh);
    refresh.type = "button";
    refresh.addEventListener("click", () => {
      compactConfigLoaded = false;
      readCompactConfig();
      load(true);
    });
    head.appendChild(refresh);
    card.appendChild(head);

    const details = el("div", "ccp-details");
    const row = el("div", "ccp-row");
    const big = el("span", "ccp-big", "—");
    const label = el("span", "ccp-label", "");
    row.appendChild(big);
    row.appendChild(label);
    card.appendChild(row);

    const meter = el("div", "ccp-meter");
    const meterInner = el("div", "ccp-meter-inner");
    meter.appendChild(meterInner);
    const mark = el("i", "ccp-mark");
    mark.setAttribute("aria-hidden", "true");
    meter.appendChild(mark);
    card.appendChild(meter);

    const auto = el("div", "ccp-section ccp-auto");
    auto.appendChild(el("div", "ccp-sub", t().auto));
    details.appendChild(auto);

    const section = el("div", "ccp-section");
    section.appendChild(el("div", "ccp-sub", t().bands));
    const legend = el("div", "ccp-legend");
    section.appendChild(legend);
    details.appendChild(section);

    const top = el("div", "ccp-section ccp-top");
    details.appendChild(top);

    const provider = el("div", "ccp-section ccp-provider");
    provider.appendChild(el("div", "ccp-sub", t().provider));
    details.appendChild(provider);

    card.appendChild(details);

    return {
      card, details, collapse, sub, big, label, meter, mark, legend, auto, top, provider,
    };
  };

  const render = (data, native) => {
    if (!refs) return;
    applyCollapsed();
    const text = t();
    const kv = (k, v) => {
      const line = el("div", "ccp-kv");
      line.appendChild(el("span", null, k));
      line.appendChild(el("span", null, v));
      return line;
    };
    refs.sub.textContent = data ? (data.name || data.cwd || "") : "";
    if (!data) {
      refs.big.textContent = native.percent != null ? native.percent + "%" : "—";
      refs.label.textContent = text.unavailable;
      refs.legend.replaceChildren();
      refs.auto.replaceChildren(el("div", "ccp-sub", text.auto));
      refs.top.replaceChildren();
      refs.top.hidden = true;
      refs.mark.style.display = "none";
      refs.provider.replaceChildren(el("div", "ccp-sub", text.provider));
      return;
    }

    const p = data.provider || {};
    // The provider's own count, exactly as the native ring reports it.
    const percent = native.percent != null ? native.percent : p.usedPct;
    refs.big.textContent = percent != null ? Math.round(percent) + "%" : "—";
    refs.label.textContent = p.contextWindow
      ? `${text.used} ${num(p.usedTokens)} ${text.of} ${num(p.contextWindow)} ${text.window}`
      : text.live;

    // The meter is the composition of the live window: the provider's exact
    // total, split by what the transcript actually holds.
    refs.meterInner.replaceChildren();
    for (const band of data.bands || []) {
      const i = document.createElement("i");
      i.style.width = Math.max(0, band.pct) + "%";
      i.style.background = band.color;
      i.title = `${band.label} ${band.tokens}`;
      refs.meterInner.appendChild(i);
    }

    // Codex caps automatic compaction at 90% of the model window. A configured
    // lower limit wins; without explicit config the cap itself is the real one.
    const windowSize = Number(p.contextWindow || compactConfig?.contextWindow);
    const cap = windowSize > 0 ? windowSize * 0.9 : null;
    const configured = Number(compactConfig?.autoLimit);
    const threshold = cap == null ? null : Number.isFinite(configured) && configured > 0 ? Math.min(configured, cap) : cap;
    const thresholdPct = threshold == null || !(windowSize > 0) ? null : threshold / windowSize * 100;
    refs.mark.style.display = thresholdPct == null ? "none" : "";
    if (thresholdPct != null) {
      refs.mark.style.left = Math.max(0, Math.min(100, thresholdPct)) + "%";
      refs.mark.title = `${text.threshold} ${num(threshold)}`;
    }
    refs.auto.replaceChildren(el("div", "ccp-sub", text.auto));
    if (threshold == null) {
      refs.auto.appendChild(kv(text.threshold, "—"));
    } else {
      const suffix = !(Number.isFinite(configured) && configured > 0) || configured > cap
        ? text.cap
        : Math.round(threshold * 100 / windowSize) + "%";
      refs.auto.appendChild(kv(text.threshold, `${num(threshold)} (${suffix})`));
      const used = p.usedTokens == null ? null : Number(p.usedTokens);
      if (Number.isFinite(used)) {
        const remaining = Math.max(0, threshold - used);
        const growth = data.recentGrowthTokens == null ? null : Number(data.recentGrowthTokens);
        let value = num(remaining);
        if (remaining <= 0) value += ` · ${text.atLimit}`;
        else if (Number.isFinite(growth) && growth > 0) value += ` · ${text.turns(Math.ceil(remaining / growth))}`;
        refs.auto.appendChild(kv(text.remaining, value));
      }
      const postTurn = Number(compactConfig?.postTurn);
      if (Number.isFinite(postTurn) && postTurn > 0) refs.auto.appendChild(kv(text.endOfTurn, `${postTurn}%`));
    }

    refs.legend.replaceChildren();
    for (const band of data.bands || []) {
      const row = el("div", "ccp-legend-row");
      const dot = el("span", "ccp-dot");
      dot.style.background = band.color;
      row.appendChild(dot);
      row.appendChild(el("span", "ccp-name", locale() === "zh" ? band.zh : band.label));
      row.appendChild(el("span", "ccp-val", num(band.tokens)));
      row.appendChild(el("span", "ccp-pct", band.pct + "%"));
      refs.legend.appendChild(row);
    }

    const topItems = Array.isArray(data.topItems) ? data.topItems.filter(item => item && item.tokens > 0) : [];
    refs.top.replaceChildren(el("div", "ccp-sub", text.top));
    refs.top.hidden = topItems.length === 0;
    for (const item of topItems.slice(0, 3)) {
      const head = el("div", "ccp-consumer-head");
      head.appendChild(el("span", "ccp-name", locale() === "zh" ? (item.zh || item.label) : (item.label || item.zh)));
      if (item.detail) {
        const detail = el("span", "ccp-detail", shortDetail(item.detail));
        detail.title = item.detail;
        head.appendChild(detail);
      }
      head.appendChild(el("span", "ccp-val", num(item.tokens)));
      refs.top.appendChild(head);
    }

    refs.provider.replaceChildren(el("div", "ccp-sub", text.provider));
    const input = p.inputTokens == null ? null : Number(p.inputTokens);
    const cached = p.cachedInputTokens == null ? null : Number(p.cachedInputTokens);
    const cacheHit = Number.isFinite(input) && input > 0 && Number.isFinite(cached) ? cached / input * 100 : null;
    refs.provider.appendChild(kv(text.cached, num(p.cachedInputTokens)));
    refs.provider.appendChild(kv(text.cacheHit, cacheHit == null ? "—" : cacheHit.toFixed(1) + "%"));
    refs.provider.appendChild(kv(text.output, num(p.outputTokens)));
  };

  // ---- mount -------------------------------------------------------------
  // The right rail column is React-owned: appending there gets wiped on the
  // next re-render, so the panel lives in the floating overlay container that
  // Codex renders outside React's tree, and we re-attach on a timer.
  const host = () => {
    const native = document.querySelector("[data-summary-panel-variant]");
    if (native) {
      // Clone the native panel's own box so the injected card inherits the
      // same surface, radius, shadow and width as the panel next to it.
      const clone = native.cloneNode(false);
      clone.removeAttribute("data-summary-panel-variant");
      clone.removeAttribute("data-summary-panel-density");
      clone.setAttribute("data-codex-context-panel", "");
      clone.id = PANEL_ID;
      clone.style.height = "auto";
      clone.style.maxHeight = "none";
      clone.style.overflow = "auto";
      const { card } = buildCard();
      clone.appendChild(card);
      return clone;
    }
    // No right rail (the home page, or the panel is collapsed): float the
    // card over the right edge of the workspace, which exists in every view.
    const workspace = document.querySelector("[class*='_WorkspaceContent']");
    if (!workspace) return null;
    const wrap = el("div", "ccp-float");
    wrap.id = PANEL_ID;
    wrap.setAttribute("data-codex-context-panel", "");
    const { card } = buildCard();
    wrap.appendChild(card);
    return wrap;
  };

  const attach = (wrap) => {
    // The native panel is a sibling inside the right rail; put ours next to
    // it so both share the rail's background and spacing.
    const native = document.querySelector("[data-summary-panel-variant]");
    if (native && native.parentElement) {
      native.parentElement.appendChild(wrap);
      wrap.classList.remove("ccp-float");
      return true;
    }
    // No right rail (the home page, or the panel is collapsed): float the
    // card over the right edge of the workspace, which exists in every view.
    const workspace = document.querySelector("[class*='_WorkspaceContent']");
    if (!workspace) return false;
    workspace.appendChild(wrap);
    wrap.classList.add("ccp-float");
    return true;
  };

  const bind = (wrap) => {
    const card = wrap.querySelector(".ccp-card");
    refs = {
      card,
      details: card.querySelector(".ccp-details"),
      collapse: card.querySelector(".ccp-collapse"),
      sub: card.querySelector(".ccp-head .ccp-sub"),
      big: card.querySelector(".ccp-big"),
      label: card.querySelector(".ccp-label"),
      meter: card.querySelector(".ccp-meter"),
      meterInner: card.querySelector(".ccp-meter-inner"),
      mark: card.querySelector(".ccp-mark"),
      legend: card.querySelector(".ccp-legend"),
      auto: card.querySelector(".ccp-auto"),
      top: card.querySelector(".ccp-top"),
      provider: card.querySelector(".ccp-provider"),
    };
    applyCollapsed();
    return attach(wrap);
  };

  const mount = () => {
    if (settingsOpen()) {
      document.getElementById(PANEL_ID)?.remove();
      refs = null;
      return false;
    }
    if (document.getElementById(PANEL_ID)) return true;
    const wrap = host();
    // A Codex update can rename the DOM this clones. Say so instead of
    // failing silently: the watcher reads this back and reports it.
    if (!wrap) {
      window.__CODEX_CONTEXT_PANEL_MOUNT_ERROR__ =
        "no anchor: neither the summary panel nor the workspace root is in the DOM";
      return false;
    }
    window.__CODEX_CONTEXT_PANEL_MOUNT_ERROR__ = null;
    return bind(wrap);
  };

  // The panel must sit next to the native one. It can end up elsewhere:
  // React may re-render the rail, or the panel may have mounted before the
  // native panel existed. Check the placement, not just the presence.
  const misplaced = () => {
    const panel = document.getElementById(PANEL_ID);
    if (!panel) return false;
    const native = document.querySelector("[data-summary-panel-variant]");
    if (native && native.parentElement) {
      return panel.parentElement !== native.parentElement;
    }
    // No native panel: floating is correct, but it must not cover one that
    // appears later. That case is handled by the same check.
    return false;
  };

  // ---- data --------------------------------------------------------------
  // CodexDictation serves the parsed rollout summary from its local helper.
  // Chromium blocks app:// -> 127.0.0.1 unless the launch flags disable
  // Local Network Access checks; CodexDictation already passes those flags.
  const settingsOpen = () => Array.from(document.querySelectorAll("h1"))
    .some((element) => element.getClientRects().length > 0 &&
      /^(settings|设置)$/i.test((element.textContent || "").trim()) &&
      String(element.parentElement?.className || "").includes("navigation-header"));
  const selectedThread = () => Array.from(document.querySelectorAll('[data-app-action-sidebar-thread-selected="true"]'))
    .find((element) => element.getClientRects().length > 0);
  const threadId = () => {
    const raw = document.querySelector("[data-above-composer-conversation-id]")?.getAttribute("data-above-composer-conversation-id") ||
      selectedThread()?.getAttribute("data-app-action-sidebar-thread-id") || "";
    const match = /([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/i.exec(raw);
    return match ? match[1] : "";
  };
  const threadTitle = () => {
    const selected = selectedThread();
    const node = selected?.querySelector("[data-app-action-sidebar-thread-title]") || selected;
    const title = (node?.getAttribute("data-app-action-sidebar-thread-title") || node?.getAttribute("aria-label") || "").trim();
    return title || document.title.trim();
  };
  const contextUrl = (id) => {
    const url = new URL("/context", helperConfig.url);
    url.search = new URL(helperConfig.url).search;
    url.searchParams.set("threadId", id);
    return url.toString();
  };
  const positiveNumber = (value) => {
    const number = Number(value);
    return Number.isFinite(number) && number > 0 ? number : null;
  };
  const readCompactConfig = async () => {
    const override = window.__CODEX_CONTEXT_PANEL_COMPACT_CONFIG__;
    if (override && typeof override === "object") {
      compactConfig = override;
      compactConfigLoaded = true;
      if (window.__ccpLastData && refs) render(window.__ccpLastData, nativeUsage());
      return;
    }
    if (compactConfigLoaded) return;
    compactConfigLoaded = true;
    try {
      const client = window.__CODEX_DICTATION_APP_SERVERS__?.get?.("local") || window.__CODEX_DICTATION_APP_SERVER__;
      if (!client?.sendRequest) return;
      const response = await client.sendRequest("config/read", { includeLayers: true, cwd: null }, { priority: "critical" });
      const raw = response?.config && typeof response.config === "object" ? response.config : {};
      const profile = typeof raw.profile === "string" && raw.profiles?.[raw.profile] && typeof raw.profiles[raw.profile] === "object" ? raw.profiles[raw.profile] : null;
      const config = profile ? { ...raw, ...Object.fromEntries(Object.entries(profile).filter(([, value]) => value != null)) } : raw;
      compactConfig = {
        autoLimit: positiveNumber(config.model_auto_compact_token_limit),
        postTurn: positiveNumber(config.model_post_turn_compact_threshold_percent),
        contextWindow: positiveNumber(config.model_context_window),
      };
      if (window.__ccpLastData && refs) render(window.__ccpLastData, nativeUsage());
    } catch {}
  };

  let visible = false;
  let currentThread = "";
  let requestSeq = 0;
  const dataCache = new Map();
  const CACHE_LIMIT = 20;
  const removePanel = () => {
    refs = null;
    document.getElementById(PANEL_ID)?.remove();
  };
  const receive = (data) => {
    visible = !!data?.hasConversation;
    window.__CODEX_CONTEXT_PANEL_STATE__ = visible ? "visible" : "empty";
    if (!visible) {
      if (currentThread) dataCache.delete(currentThread);
      window.__ccpLastData = null;
      removePanel();
      return;
    }
    window.__ccpLastData = data;
    if (currentThread) {
      if (dataCache.size >= CACHE_LIMIT && !dataCache.has(currentThread)) dataCache.delete(dataCache.keys().next().value);
      dataCache.set(currentThread, data);
    }
    if (settingsOpen()) {
      removePanel();
      return;
    }
    if (!document.getElementById(PANEL_ID)) {
      refs = null;
      if (!mount()) return;
    }
    render(data, nativeUsage());
  };
  window.__codexContextPanelReceive = receive;

  const load = async (force) => {
    const id = threadId();
    if (!id) {
      currentThread = "";
      requestSeq += 1;
      visible = false;
      window.__CODEX_CONTEXT_PANEL_STATE__ = "empty";
      window.__ccpLastData = null;
      removePanel();
      return;
    }
    if (!force && id === currentThread) return;
    currentThread = id;
    const request = ++requestSeq;
    const cached = dataCache.get(id);
    if (cached) {
      cached.name = threadTitle() || cached.name || "";
      visible = true;
      window.__CODEX_CONTEXT_PANEL_STATE__ = "visible";
      window.__ccpLastData = cached;
      if (settingsOpen()) {
        removePanel();
      } else {
        if (!document.getElementById(PANEL_ID)) {
          refs = null;
          mount();
        }
        if (refs) render(cached, nativeUsage());
      }
    } else {
      visible = false;
      window.__CODEX_CONTEXT_PANEL_STATE__ = "loading";
      window.__ccpLastData = null;
      removePanel();
    }
    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 15000);
    try {
      const response = await fetch(contextUrl(id), { cache: "no-store", signal: controller.signal });
      if (request !== requestSeq || threadId() !== id) return;
      if (response.status === 404) {
        dataCache.clear();
        window.__CODEX_CONTEXT_PANEL_STATE__ = "empty";
        receive(null);
        return;
      }
      if (!response.ok) throw new Error(`Context helper HTTP ${response.status}`);
      const data = await response.json();
      if (request !== requestSeq || threadId() !== id) return;
      data.name = threadTitle() || data.name || "";
      receive(data);
    } catch {
      if (request !== requestSeq || threadId() !== id) return;
      if (window.__ccpLastData) {
        window.__CODEX_CONTEXT_PANEL_STATE__ = "visible";
      } else {
        window.__CODEX_CONTEXT_PANEL_STATE__ = "error";
        visible = false;
        removePanel();
      }
    } finally {
      window.clearTimeout(timeout);
    }
  };
  // The Voice settings switch calls this after saving so the panel reacts
  // before the next five-second poll.
  window.__codexContextPanelRefresh = () => load(true);

  // Rebuild when the rail is re-rendered by React, and refresh the numbers
  // every five seconds. A rebuilt panel redraws the last data it received.
  let ticks = 0;
  window.__codexContextPanelTimer = window.setInterval(() => {
    if (settingsOpen()) {
      removePanel();
      return;
    }
    const id = threadId();
    if (!id) {
      currentThread = "";
      visible = false;
      window.__CODEX_CONTEXT_PANEL_STATE__ = "empty";
      window.__ccpLastData = null;
      removePanel();
      return;
    }
    if (id !== currentThread) {
      load(false);
      return;
    }
    if (!visible) {
      removePanel();
    } else if (!document.getElementById(PANEL_ID)) {
      refs = null;
      if (mount()) render(window.__ccpLastData, nativeUsage());
    } else if (misplaced()) {
      const panel = document.getElementById(PANEL_ID);
      const wrap = host();
      if (wrap) {
        // Rebuild in the right place; carry the last data over.
        panel.remove();
        if (bind(wrap)) render(window.__ccpLastData, nativeUsage());
      }
    }
    if (visible && refs) {
      const title = threadTitle();
      if (title) refs.sub.textContent = title;
    }
    if (++ticks === 1 || ticks % 5 === 0) load(true);
  }, 1000);

  // Settings and thread switches replace the workspace immediately, while the
  // sidebar's selected row updates in the same render. Follow that signal
  // instead of waiting for the one- and five-second polls.
  let viewCheckQueued = false;
  const syncPanelToView = () => {
    if (viewCheckQueued) return;
    viewCheckQueued = true;
    window.setTimeout(() => {
      viewCheckQueued = false;
      if (settingsOpen()) {
        removePanel();
        return;
      }
      const id = threadId();
      if (!id) {
        currentThread = "";
        requestSeq += 1;
        visible = false;
        window.__CODEX_CONTEXT_PANEL_STATE__ = "empty";
        window.__ccpLastData = null;
        removePanel();
        return;
      }
      if (id !== currentThread) {
        load(false);
        return;
      }
      if (visible && !document.getElementById(PANEL_ID)) {
        refs = null;
        if (mount()) render(window.__ccpLastData, nativeUsage());
      }
    }, 0);
  };
  window.__codexContextPanelSettingsObserver?.disconnect();
  window.__codexContextPanelSettingsObserver = new MutationObserver(syncPanelToView);
  window.__codexContextPanelSettingsObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-app-action-sidebar-thread-selected", "data-app-action-sidebar-thread-id", "data-above-composer-conversation-id"] });

  readCompactConfig();
  load(true);
})();