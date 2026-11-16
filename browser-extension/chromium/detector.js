/*
 * Velocity automatic file detection — floating download buttons (Chromium content script).
 *
 * What it does:
 *  - Watches links and media elements a page really offers, verifies each one with a header probe in the
 *    service worker, and shows a small Velocity button only for responses that are actually files.
 *  - Listens for files the page fetched itself (script-built links, token URLs, XHR "prepare download"
 *    calls) that the background worker observed in the real response headers.
 *  - Sends the download through the same native-host inbox the offer card uses; nothing is queued until
 *    the button is clicked.
 *
 * What it never does: extract streams, fetch blob:/data: URLs, intercept clicks, or change the page.
 * The UI lives in closed shadow roots and is positioned over the page instead of inside it, so no
 * stylesheet, layout or event handler of the site is touched.
 */
(() => {
  if (globalThis.__velocityDetectorInstalled) return;
  globalThis.__velocityDetectorInstalled = true;

  const api = globalThis.browser ?? globalThis.chrome;
  if (!api?.runtime?.id) return;

  /* ─────────────────────────── limits ─────────────────────────── */

  const MAX_VERIFICATIONS = 120;   // header probes per page
  const MAX_PARALLEL = 3;          // probes in flight at once
  const PREFETCH_LIMIT = 12;       // links verified while the page is idle, so hovering is instant
  const MAX_NETWORK_PILLS = 3;
  const HIDE_DELAY_MS = 700;
  const NETWORK_PILL_MS = 25000;
  const RESEND_GUARD_MS = 8000;
  const Z_INDEX = "2147483000";
  // Remembers the quality the user picked per site, so the strip's own click keeps using that source.
  const PREF_KEY = "preferredMedia";
  // Files the user closed on their own. This outlives the page and the browser: a file the user dismissed must never
  // pop back up on the next start, however the site re-requests it.
  const DISMISS_KEY = "dismissedOffers";
  const DISMISS_LIMIT = 500;
  const INTERACTION_TYPES = ["pointerdown", "mousedown", "keydown", "touchstart"];

  // Hints only decide whether a URL is worth a probe; the response decides whether a button appears.
  const STREAM_HINT = /\.(m3u8|mpd|m4s|ts)(?:$|[?#])/i;
  const MANIFEST_HINT = /\.(m3u8|mpd)(?:$|[?#])/i;
  // Query parameters that carry a signature or an expiry, never the identity of the video itself.
  const VOLATILE_PARAM = /^(exp|expires|expire|token|auth|auth_key|signature|sig|policy|key-pair-id|x-amz-[a-z-]+|hdnts|hdnea|begin|start|end|timestamp|time|ts|_|cb|nocache|rand)$/i;
  const FILE_HINT = /\.(7z|aac|apk|avi|bin|bmp|csv|deb|dmg|docx?|epub|exe|flac|flv|gif|gz|img|iso|jar|jpe?g|m4a|m4v|mkv|mov|mp3|mp4|mpe?g|msi|odt|ogg|oga|opus|pdf|png|pptx?|psd|rar|rpm|rtf|svg|tar|tgz|torrent|ttf|txt|wav|webm|webp|wma|wmv|xlsx?|zip)(?:$|[?#])/i;

  const THEMES = {
    Midnight: { card: "#141823", alt: "#1A1F2B", border: "#232A37", text: "#F2F5FA", secondary: "#AFBACB", muted: "#7A8698", accent: "#5B8CFF", accent2: "#7C6BFF", success: "#34D399", danger: "#F87171", shadow: "0 10px 26px rgba(0,0,0,.46)" },
    Ember: { card: "#221A16", alt: "#2A201A", border: "#3A2C24", text: "#FFF4EA", secondary: "#E3CBB6", muted: "#B08F76", accent: "#E8792F", accent2: "#F2A03D", success: "#4CCB92", danger: "#F0746F", shadow: "0 10px 26px rgba(0,0,0,.42)" },
    Light: { card: "#FFFFFF", alt: "#F7FAFE", border: "#E2E8F2", text: "#0F172A", secondary: "#44506A", muted: "#6C7890", accent: "#3B6FD4", accent2: "#6A7BF0", success: "#0F9D63", danger: "#C93A50", shadow: "0 10px 26px rgba(15,23,42,.18)" }
  };

  const state = {
    enabled: false,
    theme: "Midnight",
    excluded: [],
    verified: new Map(),    // url -> details
    rejected: new Map(),    // url -> when
    inflight: new Map(),    // url -> promise
    sent: new Map(),        // url -> when
    dismissed: new Set(),   // urls the user closed
    verifications: 0,
    floats: new Map(),      // element -> record
    link: null,             // the link float currently shown
    network: [],            // active network pills
    networkSeen: new Map(),
    // Manifests the page itself fetched. A blob:/MSE player publishes no address of its own, so this is what
    // makes the strip appear over such a player at all, and what fills its menu with the real qualities.
    streams: new Map(),
    streamOrder: [],
    preferred: {},           // site -> quality key chosen in the media list
    known: new Map(),        // identity -> best details seen for that video
    hideTimer: 0,
    frame: 0,
    observer: null,
    media: null,
    started: false,
    // True once the user has actually done something on this page. Passive offers are held back until then: a page
    // that fetches a file while it loads (a restored tab, a preview, a player preloading the next episode) is not the
    // user asking for a download, and offering it is what made offers appear every time the browser started.
    interacted: false
  };

  function markInteracted() {
    if (state.interacted) return;
    state.interacted = true;
    // Something the page already fetched may be worth offering now that the user is here.
    sweepMedia();
  }

  /* ─────────────────────────── messaging ─────────────────────────── */

  function ask(message, milliseconds) {
    return Promise.race([
      api.runtime.sendMessage(message),
      new Promise((_, reject) => setTimeout(() => reject(new Error("timeout")), milliseconds))
    ]);
  }

  const queue = [];
  let running = 0;

  function enqueue(task) {
    return new Promise((resolve, reject) => {
      queue.push({ task, resolve, reject });
      pump();
    });
  }

  function pump() {
    while (running < MAX_PARALLEL && queue.length) {
      const next = queue.shift();
      running++;
      next.task().then(next.resolve, next.reject).finally(() => { running--; pump(); });
    }
  }

  function verify(url, kind, headOnly = false) {
    if (state.verified.has(url)) return Promise.resolve(state.verified.get(url));
    // The same video requested again with a fresh signature is not a new video: its known properties are
    // reused, so a reloaded player does not lose the name and size it had a moment ago.
    const known = state.known.get(identityOf(url));
    if (known?.url && known.size !== null && known.size !== undefined) {
      // The address that was just requested is the one that will still work; only the properties are reused.
      const reuse = { ...known, url, requestedUrl: url };
      state.verified.set(url, reuse);
      return Promise.resolve(reuse);
    }
    const rejectedAt = state.rejected.get(url);
    if (rejectedAt) {
      // A probe that failed while the page was still loading deserves a second chance later.
      if (Date.now() - rejectedAt < 60000) return Promise.resolve(null);
      state.rejected.delete(url);
    }
    const pending = state.inflight.get(url);
    if (pending) return pending;
    if (state.verifications >= MAX_VERIFICATIONS) return Promise.resolve(null);
    state.verifications++;

    const promise = enqueue(async () => {
      try {
        const response = await ask({ type: "velocity-detect", url, kind, headOnly }, 20000);
        if (response?.theme && THEMES[response.theme]) state.theme = response.theme;
        if (response?.ok && response.found && response.details) {
          const details = remember({ ...response.details, url: response.details.url || url });
          state.verified.set(url, details);
          return details;
        }
        state.rejected.set(url, Date.now());
        return null;
      } catch {
        // A page that is still loading can fail the probe; allow one retry later.
        state.rejected.set(url, Date.now());
        return null;
      }
    });

    state.inflight.set(url, promise);
    promise.then(() => state.inflight.delete(url), () => state.inflight.delete(url));
    return promise;
  }

  /* ─────────────────────────── identity ─────────────────────────── */

  // IDM-style recognition. A player that reloads, seeks or switches quality asks for the same video with a new
  // signature or expiry stamp; those parameters are not part of the video, so they are dropped before comparing
  // addresses and one entry is updated instead of a second video being invented.
  function identityOf(value) {
    try {
      const url = value instanceof URL ? value : new URL(String(value), location.href);
      const query = [...url.searchParams.entries()]
        .filter(([name]) => !VOLATILE_PARAM.test(name))
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([name, text]) => `${name}=${text}`)
        .join("&");
      return `${url.origin}${url.pathname}${query ? `?${query}` : ""}`;
    } catch {
      return String(value);
    }
  }

  // Properties only ever improve: a 204, a HEAD without Content-Length or a stripped response must not erase a
  // size or a name the server already gave, which is what "retain and update" means here.
  function mergeDetails(previous, next) {
    if (!previous) return next;
    const defined = Object.fromEntries(Object.entries(next || {}).filter(([, value]) => value !== null && value !== undefined && value !== ""));
    return { ...previous, ...defined };
  }

  function remember(details) {
    if (!details?.url) return details;
    const key = identityOf(details.requestedUrl || details.url);
    const merged = { ...mergeDetails(state.known.get(key), details), requestedUrl: details.requestedUrl || details.url };
    state.known.set(key, merged);
    return merged;
  }

  // A real response for a video the strip is already showing: fill in what the first probe could not see.
  function rememberProperties(item) {
    if (!item?.url) return false;
    const key = identityOf(item.url);
    const merged = mergeDetails(state.known.get(key), item);
    state.known.set(key, merged);

    let changed = false;
    for (const [url, details] of state.verified) {
      if (identityOf(details.requestedUrl || details.url) !== key) continue;
      const better = mergeDetails(details, merged);
      if (better.size !== details.size || better.type !== details.type) {
        state.verified.set(url, better);
        changed = true;
      }
    }
    if (!changed) return false;

    for (const record of state.floats.values()) {
      if (record.kind !== "media" || !record.visible) continue;
      for (const entry of record.items || []) {
        if (identityOf(entry.details?.url || entry.url.href) !== key) continue;
        entry.details = mergeDetails(entry.details, merged);
      }
      rebuildMedia(record);
    }
    return true;
  }

  /* ─────────────────────────── formatting ─────────────────────────── */

  function formatBytes(value) {
    if (value === null || value === undefined || Number.isNaN(Number(value))) return "";
    let size = Number(value);
    if (size <= 0) return "";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let index = 0;
    while (size >= 1024 && index < units.length - 1) { size /= 1024; index++; }
    return `${size.toFixed(index === 0 || size >= 100 ? 0 : 1)} ${units[index]}`;
  }

  function shortName(name, limit = 30) {
    const text = String(name || "file").trim();
    return text.length > limit ? `${text.slice(0, limit - 1).trimEnd()}…` : text;
  }

  function typeLabel(details) {
    const extension = /\.([a-z0-9]{1,5})$/i.exec(String(details.name || ""))?.[1];
    if (extension) return extension.toUpperCase();
    const subtype = String(details.type || "").split("/")[1];
    return subtype ? subtype.split(";")[0].toUpperCase().slice(0, 6) : "";
  }

  /* ─────────────────────────── styles ─────────────────────────── */

  // The extension's own artwork, so an in-page mark is the Velocity icon rather than a letter in a box.
  const brandIcon = (() => {
    try { return api.runtime.getURL("icons/icon32.png"); } catch { return ""; }
  })();

  const sheets = new Map();

  function sheetFor(theme) {
    if (sheets.has(theme)) return sheets.get(theme);
    const t = THEMES[theme] || THEMES.Midnight;
    const css = `
      :host { all: initial; }
      * { box-sizing: border-box; }
      .btn {
        position: fixed; display: inline-flex; align-items: center; gap: 6px; height: 28px;
        padding: 0 10px 0 6px; border-radius: 9px; border: 1px solid ${t.border};
        background: ${t.card}; color: ${t.text}; cursor: pointer; white-space: nowrap;
        font: 600 11.5px/1 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
        box-shadow: ${t.shadow}; user-select: none;
        transition: transform .12s ease, border-color .12s ease, box-shadow .12s ease, opacity .12s ease;
      }
      .btn:hover { border-color: ${t.accent}; transform: translateY(-1px); }
      .btn:focus-visible { outline: 2px solid ${t.accent}; outline-offset: 2px; }
      .btn:active { transform: none; }
      .btn.compact { width: 26px; height: 26px; padding: 0; justify-content: center; border-radius: 8px; }
      .mark {
        width: 18px; height: 18px; flex: none; border-radius: 5px;
        background: #d9a96a url("${brandIcon}") center / contain no-repeat;
      }
      .label { max-width: 210px; overflow: hidden; text-overflow: ellipsis; }
      .meta { color: ${t.muted}; font-weight: 500; }
      .glyph { width: 14px; height: 14px; flex: none; display: block; }
      .spinner {
        width: 12px; height: 12px; flex: none; border-radius: 50%;
        border: 1.6px solid currentColor; border-top-color: transparent; animation: velocity-spin .7s linear infinite;
      }
      @keyframes velocity-spin { to { transform: rotate(360deg); } }
      .btn[data-state="done"] { border-color: ${t.success}; color: ${t.success}; }
      .btn[data-state="error"] { border-color: ${t.danger}; color: ${t.danger}; max-width: 300px; }
      /* IDM-style media bar: a small strip in the video's upper-right corner that opens the file list. */
      .mbar {
        position: fixed; display: inline-flex; align-items: center; gap: 2px; height: 30px; padding: 2px;
        border-radius: 10px; border: 1px solid ${t.border}; background: ${t.card}; color: ${t.text};
        box-shadow: ${t.shadow}; opacity: .93;
        font: 600 11.5px/1 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
        transition: opacity .12s ease, border-color .12s ease;
      }
      .mbar:hover, .mbar[data-state="sending"] { opacity: 1; }
      .mbar:hover { border-color: ${t.accent}; }
      .mbar[data-state="done"] { border-color: ${t.success}; }
      .mbar[data-state="error"] { border-color: ${t.danger}; color: ${t.danger}; }
      .mbar button, .mlist button { all: unset; cursor: pointer; box-sizing: border-box; }
      .mbar .mgo { display: inline-flex; align-items: center; gap: 6px; height: 26px; padding: 0 8px 0 5px; border-radius: 8px; }
      .mbar .mgo:hover { background: ${t.alt}; }
      .mbar .mmark {
        width: 20px; height: 20px; flex: none;
        background: #d9a96a url("${brandIcon}") center / contain no-repeat;
      }
      .mbar .mchevron { width: 22px; height: 26px; justify-content: center; border-radius: 8px; color: ${t.muted}; font-size: 10px; }
      .mbar .mchevron:hover { background: ${t.alt}; color: ${t.text}; }
      .mbar .mclose { width: 20px; height: 26px; justify-content: center; border-radius: 8px; color: ${t.muted}; font-size: 12px; }
      .mbar .mclose:hover { background: ${t.alt}; color: ${t.text}; }
      .mbar button:focus-visible, .mlist button:focus-visible { outline: 2px solid ${t.accent}; outline-offset: 1px; }
      .mlist {
        position: absolute; top: calc(100% + 6px); right: 0; width: 308px; padding: 6px;
        border-radius: 11px; border: 1px solid ${t.border}; background: ${t.card}; color: ${t.text};
        box-shadow: ${t.shadow}; display: none;
      }
      .mlist[data-open="true"] { display: block; }
      .mlist .mhead { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 2px 4px 6px; }
      .mlist .mtitle { color: ${t.muted}; font: 600 10px/1 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; letter-spacing: .4px; text-transform: uppercase; }
      .mlist .mall { color: ${t.accent}; font-size: 11px; font-weight: 700; padding: 3px 7px; border-radius: 6px; }
      .mlist .mall:hover { background: ${t.alt}; }
      .mlist .mrow { display: flex; align-items: center; gap: 2px; width: 100%; border-radius: 8px; }
      .mlist .mrow:hover { background: ${t.alt}; }
      .mlist .mpick { display: flex; align-items: center; gap: 8px; flex: 1; min-width: 0; padding: 7px 4px 7px 6px; border-radius: 8px; text-align: left; }
      .mlist .mname { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; font-size: 11.5px; }
      .mlist .mmeta { flex: none; color: ${t.muted}; font-size: 10.5px; font-weight: 500; }
      .mlist .mrow[data-sent="true"] .mmeta { color: ${t.success}; }
      .mlist .mstar { width: 24px; height: 22px; flex: none; display: flex; align-items: center; justify-content: center; border-radius: 6px; color: ${t.muted}; font-size: 12px; }
      .mlist .mstar:hover { color: ${t.text}; background: ${t.border}; }
      .mlist .mrow[data-preferred="true"] .mstar { color: ${t.accent}; }
      .btn.compact[data-state="ready"], .btn.compact[data-state="sending"] {
        border-color: transparent; color: #fff;
        background: linear-gradient(135deg, ${t.accent}, ${t.accent2});
      }
      .btn.compact[data-state="sending"] .glyph { display: none; }
      .btn[data-state="error"] .glyph { display: none; }
      .pill {
        position: fixed; width: 296px; padding: 9px 10px; border-radius: 11px; border: 1px solid ${t.border};
        background: ${t.card}; color: ${t.text}; box-shadow: ${t.shadow};
        font: 500 11.5px/1.4 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
        display: flex; align-items: center; gap: 9px;
        animation: velocity-in .16s ease-out;
      }
      @keyframes velocity-in { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: none; } }
      .pill .text { flex: 1; min-width: 0; }
      .pill .name { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .pill .sub { color: ${t.muted}; font-size: 10.5px; margin-top: 1px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .pill .go {
        all: unset; cursor: pointer; flex: none; height: 24px; padding: 0 9px; border-radius: 7px;
        display: inline-flex; align-items: center; gap: 5px; font-size: 11px; font-weight: 700; color: #fff;
        background: linear-gradient(135deg, ${t.accent}, ${t.accent2});
      }
      .pill .go:hover { filter: brightness(1.08); }
      .pill .go:focus-visible { outline: 2px solid ${t.accent}; outline-offset: 2px; }
      .pill .go[data-state="done"] { background: ${t.success}; }
      .pill .go[data-state="error"] { background: transparent; color: ${t.danger}; border: 1px solid ${t.danger}; }
      .pill .close {
        all: unset; cursor: pointer; flex: none; width: 20px; height: 20px; border-radius: 5px; color: ${t.muted};
        display: flex; align-items: center; justify-content: center; font-size: 14px; line-height: 1;
      }
      .pill .close:hover { background: ${t.alt}; color: ${t.text}; }
      @media (prefers-reduced-motion: reduce) {
        .btn, .pill { animation: none; transition: none; }
        .spinner { animation-duration: 1.6s; }
      }
    `;

    let sheet;
    if (typeof CSSStyleSheet === "function" && "adoptedStyleSheets" in Document.prototype) {
      try { sheet = new CSSStyleSheet(); sheet.replaceSync(css); } catch { sheet = null; }
    }
    if (!sheet) sheet = css;   // <style> fallback for older engines
    sheets.set(theme, sheet);
    return sheet;
  }

  const SVG_NS = "http://www.w3.org/2000/svg";

  function glyph() {
    const svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("class", "glyph");
    svg.setAttribute("aria-hidden", "true");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", "2");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("stroke-linejoin", "round");
    const arrow = document.createElementNS(SVG_NS, "path");
    arrow.setAttribute("d", "M12 4v10m0 0 4-4m-4 4-4-4");
    const tray = document.createElementNS(SVG_NS, "path");
    tray.setAttribute("d", "M5 17v1.5A1.5 1.5 0 0 0 6.5 20h11a1.5 1.5 0 0 0 1.5-1.5V17");
    svg.append(arrow, tray);
    return svg;
  }

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function attachStyles(root, theme) {
    const sheet = sheetFor(theme);
    if (typeof sheet === "string") {
      root.append(element("style", "", sheet));
      return;
    }
    try { root.adoptedStyleSheets = [sheet]; } catch { /* older engine: unthemed but usable */ }
  }

  /* ─────────────────────────── sending ─────────────────────────── */

  function describe(details) {
    const size = formatBytes(details.size);
    return size ? `${size}` : "file";
  }

  async function send(record, trigger) {
    if (record.state === "sending" || record.state === "done") return;
    const details = record.details || {};
    if (!details.url) return;
    if (record.stream && record.stream.downloadable === false) {
      setState(record, "error", record.stream.reason || "This stream cannot be downloaded as one file.");
      return;
    }

    const last = state.sent.get(details.url);
    if (last && Date.now() - last < RESEND_GUARD_MS) {
      setState(record, "done", "Already sent to Velocity.");
      return;
    }

    setState(record, "sending", "Sending to Velocity…");
    try {
      const response = await ask({
        type: "velocity-action",
        action: "start",
        details: {
          url: details.url,
          name: details.name || "",
          size: details.size ?? null,
          type: details.type || "",
          site: details.site || "",
          pageUrl: location.href
        }
      }, 20000);

      if (response?.ok) {
        state.sent.set(details.url, Date.now());
        const where = response.destination ? `Saved to ${response.destination}` : "Added to Velocity";
        setState(record, "done", where);
        setTimeout(() => hideRecord(record, true), 2400);
        if (trigger) { try { trigger(); } catch { /* decorative only */ } }
        return;
      }
      setState(record, "error", response?.error || "Velocity did not accept the download.");
    } catch (error) {
      setState(record, "error", error?.message === "timeout"
        ? "Velocity did not answer in time. Check the native host."
        : String(error?.message || error));
    }
  }

  /* ─────────────────────────── floats ─────────────────────────── */

  function createRecord(element_, details, kind) {
    const host = document.createElement("div");
    host.setAttribute("data-velocity-float", kind);
    host.setAttribute("data-velocity-float-state", "ready");
    host.setAttribute("data-velocity-url", details.url || "");
    host.setAttribute("data-velocity-file", details.name || "");
    host.style.cssText = `all: initial; position: fixed; inset: 0 auto auto 0; width: 0; height: 0; z-index: ${Z_INDEX};`;

    const root = host.attachShadow({ mode: "closed" });
    attachStyles(root, state.theme);

    const record = { element: element_, kind, details, host, root, state: "ready", message: "", visible: false, timer: 0, network: false };
    record.node = kind === "link" ? compactButton(record) : kind === "media" ? mediaBar(record) : pill(record);
    root.append(record.node);
    (document.body || document.documentElement).append(host);
    return record;
  }

  function compactButton(record) {
    const button = element("button", "btn compact");
    button.type = "button";
    button.dataset.state = "ready";
    button.setAttribute("role", "button");
    button.setAttribute("aria-label", `Download ${record.details.name || "this file"} with Velocity`);
    const size = describe(record.details);
    button.title = `${record.details.name || "Download"}${size ? ` · ${size}` : ""} — download with Velocity`;
    button.append(glyph());
    button.addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); void send(record); });
    button.addEventListener("pointerdown", event => event.stopPropagation());
    return button;
  }

  function pill(record) {
    const card = element("div", "pill");
    card.setAttribute("role", "group");
    card.setAttribute("aria-label", `Velocity detected ${record.details.name || "a file"}`);

    const mark = element("div", "mark");
    const text = element("div", "text");
    const name = element("div", "name", shortName(record.details.name, 34));
    name.title = record.details.name || "";
    const sub = element("div", "sub", [describe(record.details), typeLabel(record.details), record.details.site].filter(Boolean).join(" · "));
    text.append(name, sub);

    const go = element("button", "go");
    go.type = "button";
    go.append(glyph(), element("span", "", `Download`));
    go.title = `Download ${record.details.name || "this file"} with Velocity`;
    go.addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); void send(record); });

    const close = element("button", "close", "✕");
    close.type = "button";
    close.title = "Hide this offer";
    close.setAttribute("aria-label", "Dismiss");
    close.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      dismiss(record.details.url);
      hideRecord(record, true);
    });

    card.append(mark, text, go, close);
    record.go = go;
    record.sub = sub;
    return card;
  }

  /* ─────────────────────────── media bar (IDM style) ─────────────────────────── */

  // Reading a manifest is what turns "master.m3u8" into an entry with a real quality and, when the duration
  // and bitrate are known, the size of the whole video.
  const streamCache = new Map();

  function streamLabelOf(stream) {
    const height = Number(stream?.height) || 0;
    if (height >= 2000) return "4K";
    if (height > 0) return `${height}p`;
    return stream?.kind === "dash" ? "DASH" : "HLS";
  }

  function pageTitle(url) {
    const title = (document.title || "").replace(/[\\/:*?"<>|]+/g, " ").replace(/\s+/g, " ").trim();
    const base = title || fileNameOf(url).replace(/\.[a-z0-9]+$/i, "") || "video";
    return base.slice(0, 90);
  }

  function streamFacts(stream, url) {
    return {
      name: `${pageTitle(url)}${stream?.kind === "dash" ? ".mpd" : ".ts"}`,
      size: stream?.size ?? null,
      type: stream?.kind === "dash" ? "application/dash+xml" : "application/vnd.apple.mpegurl"
    };
  }

  function urlOf(value, base) {
    if (!value) return null;
    try {
      const url = new URL(value, base || location.href);
      return /^https?:$/.test(url.protocol) ? url : null;
    } catch {
      return null;
    }
  }

  // One row per quality the master playlist declares, so the menu reads like IDM's: 1080p, 720p, 480p…
  // Each row carries that variant's own playlist address, which is exactly the quality Velocity then downloads.
  function variantItems(stream, baseUrl) {
    const variants = (stream?.variants || []).filter(variant => variant && variant.url);
    if (variants.length === 0) return [];
    const title = pageTitle(baseUrl || stream.url || location.href);
    return variants
      .slice()
      .sort((left, right) =>
        (Number(right.height) || 0) - (Number(left.height) || 0) ||
        (Number(right.bandwidth) || 0) - (Number(left.bandwidth) || 0))
      .map((variant, index) => {
        const url = urlOf(variant.url, stream.url || baseUrl || location.href);
        if (!url) return null;
        const label = variant.label || (Number(variant.height) ? `${variant.height}p` : `source ${index + 1}`);
        const estimate = Number(variant.bandwidth) > 0 && Number(stream.duration) > 0
          ? Math.round((Number(variant.bandwidth) * Number(stream.duration)) / 8)
          : (variant.size ?? null);
        return {
          url,
          role: "video",
          fromStream: true,
          format: stream.kind === "dash" ? "DASH" : "HLS",
          stream,
          label,
          key: `${label}|${url.pathname}`,
          estimated: Boolean(estimate),
          details: {
            url: url.href,
            site: location.host,
            name: `${title} ${label}.${stream.kind === "dash" ? "mp4" : "ts"}`,
            size: estimate,
            type: stream.kind === "dash" ? "video/mp4" : "video/mp2t"
          }
        };
      })
      .filter(Boolean);
  }

  function applyStream(media, item, stream) {
    if (!stream || stream.error) return;
    item.stream = stream;
    item.label = streamLabelOf(stream);
    item.details = { ...(item.details || {}), url: stream.url || item.url.href, ...streamFacts(stream, item.url.href) };
    const record = media ? state.floats.get(media) : null;
    if (!record || !record.visible) return;
    // A master playlist is not one file but a menu. Replacing its single row with one row per quality is what
    // lets 1080p and 480p be picked separately, the way IDM's video list works.
    const qualities = variantItems(stream, item.url.href);
    const index = (record.items || []).indexOf(item);
    if (qualities.length > 1 && index >= 0) {
      const known = new Set(record.items.map(entry => entry.url.href));
      const additions = qualities.filter(entry => !known.has(entry.url.href));
      if (additions.length === qualities.length) record.items.splice(index, 1, ...qualities);
      else if (additions.length) record.items.splice(index + 1, 0, ...additions);
    }
    rebuildMedia(record);
  }

  function resolveStream(media, item) {
    const href = item.url.href;
    const known = streamCache.get(href);
    if (known) { applyStream(media, item, known); return Promise.resolve(known); }
    const key = `stream:${href}`;
    const pending = state.inflight.get(key) || ask({ type: "velocity-manifest", url: href }, 20000)
      .then(
        response => response?.ok === false ? { error: response.error || "" } : (response?.stream || { error: "" }),
        error => ({ error: String(error?.message || error) }));
    state.inflight.set(key, pending);
    return pending.then(stream => {
      state.inflight.delete(key);
      if (stream && !stream.error) streamCache.set(href, stream);
      applyStream(media, item, stream);
      return stream;
    });
  }

  function fileNameOf(value) {
    try {
      const url = value instanceof URL ? value : new URL(String(value));
      const last = url.pathname.split("/").filter(Boolean).pop();
      return last ? decodeURIComponent(last) : url.host;
    } catch {
      return "";
    }
  }

  function mediaRow(record, item, index, canPrefer) {
    const details = item.details || {};
    const name = details.name || fileNameOf(item.url) || `source ${index}`;
    // IDM lists a row as "name … format, size"; the same information here, with the quality ahead of the container
    // when the player reports one (480p, 720p, 1080p …).
    const isStream = item.role === "stream" || Boolean(item.fromStream);
    const label = item.role === "track" ? "VTT" : (item.label || "");
    const type = item.format || (isStream ? (item.stream?.kind === "dash" ? "DASH" : "HLS") : typeLabel({ name, type: details.type }));
    // A quality taken from a master playlist has no byte count of its own: its size is derived from the
    // bitrate the playlist declares, so it is shown as an estimate instead of a measured size.
    const measured = formatBytes(details.size);
    const size = measured && item.estimated ? `~${measured}` : measured;
    const meta = isStream
      // A stream states what it is and whether it can become one file, and its size is marked as an estimate.
      ? [label && label !== type ? label : "", type, item.stream ? (item.stream.estimated && !String(size).startsWith("~") ? `~${size}` : size) : "reading…",
        item.stream?.downloadable === false ? "not downloadable" : ""].filter(Boolean).join(" · ")
      : [label, type && type !== label ? type : "", item.role === "track" ? "subtitle" : "", size].filter(Boolean).join(" · ");

    const row = element("div", "mrow");
    row.dataset.sent = item.sent ? "true" : "false";
    row.dataset.preferred = "false";

    const pick = element("button", "mpick");
    pick.type = "button";
    pick.title = isStream && item.stream?.downloadable === false
      ? `${name} — ${item.stream.reason}`
      : `${name} — download with Velocity`;
    pick.append(element("span", "mname", shortName(name, 32)), element("span", "mmeta", item.sent ? "Added" : (meta || "file")));
    pick.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      void sendMediaItem(record, item);
    });
    pick.addEventListener("pointerdown", event => event.stopPropagation());
    row.append(pick);

    // One star per quality: it makes that file what the strip itself downloads from now on, on this site.
    if (canPrefer && item.role === "video") {
      const star = element("button", "mstar", "☆");
      star.type = "button";
      star.setAttribute("aria-pressed", "false");
      star.title = "Always download this file from this site";
      star.addEventListener("click", event => {
        event.preventDefault();
        event.stopPropagation();
        togglePreferred(record, item);
      });
      star.addEventListener("pointerdown", event => event.stopPropagation());
      row.append(star);
      record.stars.set(item.url.href, { row, star, item });
    }

    row.addEventListener("pointerdown", event => event.stopPropagation());
    return row;
  }

  // The floating bar itself: one compact strip in the video's upper-right corner. With a single file the
  // strip downloads it; with several (alternate sources, subtitles) it opens the list instead of guessing.
  function mediaBar(record) {
    const items = record.items || [];
    const primary = record.details || {};
    const size = formatBytes(primary.size);
    const description = primary.name ? `${primary.name}${size ? ` · ${size}` : ""}` : "this video";

    const bar = element("div", "mbar");
    bar.setAttribute("role", "group");
    bar.setAttribute("aria-label", "Velocity video download");
    bar.dataset.state = record.state || "ready";

    const list = element("div", "mlist");
    list.dataset.open = "false";

    // A choice only makes sense when the player offers several video files with distinct labels.
    const videoItems = items.filter(item => item.role === "video");
    const canPrefer = new Set(videoItems.map(item => item.key).filter(Boolean)).size > 1;
    record.canPrefer = canPrefer;
    record.stars = new Map();

    const openList = () => {
      list.dataset.open = "true";
      record.listOpen = true;
      chevron.setAttribute("aria-expanded", "true");
      // The panel hangs below the strip; it flips above when the player sits at the bottom of the viewport.
      const box = bar.getBoundingClientRect();
      const below = innerHeight - box.bottom - 10;
      list.style.top = below < 200 ? "auto" : "";
      list.style.bottom = below < 200 ? "calc(100% + 6px)" : "";
    };

    const go = element("button", "mgo");
    go.type = "button";
    go.title = `Download ${description} with Velocity`;
    go.append(element("span", "mmark"), element("span", "mtext", "Download this video"));
    // IDM's bar opens the quality menu before anything is downloaded: one click shows what the player really
    // offers, and a file is sent only when a quality is picked.
    go.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      openList();
    });
    go.addEventListener("pointerdown", event => event.stopPropagation());

    const chevron = element("button", "mchevron", "▾");
    chevron.type = "button";
    chevron.title = "Choose a file";
    chevron.setAttribute("aria-label", "Choose a file to download");
    chevron.setAttribute("aria-expanded", "false");
    chevron.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      if (list.dataset.open === "true") {
        list.dataset.open = "false";
        record.listOpen = false;
        chevron.setAttribute("aria-expanded", "false");
        return;
      }
      openList();
    });
    chevron.addEventListener("pointerdown", event => event.stopPropagation());

    const close = element("button", "mclose", "✕");
    close.type = "button";
    close.title = "Hide this button";
    close.setAttribute("aria-label", "Dismiss");
    close.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      dismiss(primary.url);
      hideRecord(record, true);
    });
    close.addEventListener("pointerdown", event => event.stopPropagation());

    // No bulk action: Velocity downloads the file the user picks and nothing else — no subtitles, thumbnails,
    // alternate sources or segments behind their back.
    const head = element("div", "mhead");
    head.append(element("span", "mtitle", "Detected files"));
    list.append(head);
    items.forEach((item, index) => list.append(mediaRow(record, item, index + 1, canPrefer)));
    list.addEventListener("pointerdown", event => event.stopPropagation());

    // The list is always reachable, because it is where the format, quality and size of every file are shown.
    bar.append(go, chevron, close, list);

    record.text = go.querySelector(".mtext");
    record.mark = go.querySelector(".mmark");
    record.go = go;
    record.list = list;
    refreshMedia(record);
    return bar;
  }

  /* ─────────────────────────── preferred quality ─────────────────────────── */

  function siteKey() {
    return location.host || "localhost";
  }

  function preferredItem(record) {
    const key = state.preferred[siteKey()];
    if (!key) return null;
    const videos = (record.items || []).filter(item => item.role === "video");
    if (videos.length < 2) return null;
    return videos.find(item => item.key === key) || null;
  }

  function describeItem(item) {
    const details = item?.details || {};
    const url = item?.url?.href || details.url || "";
    const name = details.name || (url ? fileNameOf(url) : "");
    const size = formatBytes(details.size);
    return `${name}${size ? ` · ${size}` : ""}`.trim();
  }

  function refreshMedia(record) {
    const chosen = preferredItem(record);
    if (record.go) {
      const target = chosen ? describeItem(chosen) : describeItem({ details: record.details });
      record.go.title = chosen
        ? `Download ${target} — your preferred file on this site`
        : (target ? `Download ${target} with Velocity` : "Download this video with Velocity");
    }

    for (const [url, entry] of record.stars || []) {
      const active = Boolean(chosen) && chosen.url.href === url;
      entry.row.dataset.preferred = String(active);
      entry.star.textContent = active ? "★" : "☆";
      entry.star.setAttribute("aria-pressed", String(active));
      entry.star.title = active
        ? "Stop always downloading this file from this site"
        : "Always download this file from this site";
    }
  }

  function togglePreferred(record, item) {
    if (!item.key) return;
    const host = siteKey();
    if (state.preferred[host] === item.key) delete state.preferred[host];
    else state.preferred[host] = item.key;
    savePreferences();
    for (const media of state.floats.values()) if (media.kind === "media") refreshMedia(media);
  }

  async function loadPreferences() {
    try {
      const stored = await api.storage.local.get({ [PREF_KEY]: {}, [DISMISS_KEY]: [] });
      const value = stored?.[PREF_KEY];
      state.preferred = value && typeof value === "object" ? value : {};
      const dismissed = stored?.[DISMISS_KEY];
      if (Array.isArray(dismissed)) for (const url of dismissed) if (url) state.dismissed.add(url);
    } catch {
      // Preferences are a convenience: without them the strip simply offers the list again.
    }
  }

  // A dismissal is remembered in storage, so closing an offer really ends it: reloading the page or restarting the
  // browser must not bring the same file back.
  function dismiss(url) {
    if (!url) return;
    state.dismissed.add(url);
    try {
      const write = api.storage.local.get({ [DISMISS_KEY]: [] }).then(stored => {
        const list = Array.isArray(stored?.[DISMISS_KEY]) ? stored[DISMISS_KEY] : [];
        const next = [url, ...list.filter(entry => entry !== url)].slice(0, DISMISS_LIMIT);
        return api.storage.local.set({ [DISMISS_KEY]: next });
      });
      if (write?.catch) write.catch(() => { /* best effort */ });
    } catch {
      // Storage can be unavailable while the worker restarts; the dismissal then lasts for this page only.
    }
  }

  function savePreferences() {
    try {
      const write = api.storage.local.set({ [PREF_KEY]: state.preferred });
      if (write?.catch) write.catch(() => { /* best effort */ });
    } catch {
      // Storage can be unavailable while the worker restarts; the choice then lasts for this page only.
    }
  }

  function rebuildMedia(record) {
    // The host carries the file's address for the outside world (the harness, a reloaded page, a click), so it
    // follows the newest details instead of remembering the first token that was seen.
    if (record.details?.url) record.host.setAttribute("data-velocity-url", record.details.url);
    if (record.details?.name) record.host.setAttribute("data-velocity-file", record.details.name);
    const wasOpen = record.listOpen === true;
    const bar = mediaBar(record);
    if (record.node?.isConnected) record.node.replaceWith(bar);
    else record.root.append(bar);
    record.node = bar;
    // A menu that is already open stays open while the page keeps reporting metadata, so it cannot close
    // itself while it is being read.
    if (wasOpen) {
      const list = record.list;
      if (list) {
        list.dataset.open = "true";
        record.listOpen = true;
        const chevron = bar.querySelector(".mchevron");
        if (chevron) chevron.setAttribute("aria-expanded", "true");
      }
    }
    place(record);
  }

  function showMedia(media, details, items) {
    // The verified details of the file that is playing belong to its own row, so that row shows its size too.
    if (items[0] && !items[0].details) items[0].details = details;
    // Every stream in the strip is described by its own playlist, in the worker that holds the page's cookies.
    for (const item of items) if (item.role === "stream") void resolveStream(media, item);
    const existing = state.floats.get(media);
    if (existing) {
      const signature = itemsSignature(items);
      const same = existing.signature === signature && existing.details?.size === details?.size;
      existing.details = details;
      existing.visible = true;
      existing.host.style.display = "";
      ensureOverlayParent(existing.host);
      // A playing video fires metadata events continuously. Rebuilding the strip for an unchanged list is
      // what made it blink and what closed the menu under the pointer, so an identical list keeps its node.
      if (same) { ensureTick(); return existing; }
      existing.items = items;
      existing.signature = signature;
      rebuildMedia(existing);
      ensureTick();
      return existing;
    }

    const record = createRecord(media, details, "media");
    record.items = items;
    record.signature = itemsSignature(items);
    rebuildMedia(record);
    state.floats.set(media, record);
    record.visible = true;
    record.node.style.opacity = "0";
    requestAnimationFrame(() => { if (record.visible) record.node.style.opacity = "1"; });
    ensureTick();

    // Alternate sources are verified exactly like the main one, and a row appears once the server answers.
    // A subtitle track is offered as it is: the player itself points at it, so there is nothing to verify.
    for (const item of items.slice(1)) {
      // A subtitle is offered as the player points at it, and a playlist quality is already described by its
      // manifest, so neither needs a header probe of its own.
      if (item.role === "track" || item.role === "stream" || item.fromStream) continue;
      const known = state.verified.get(item.url.href);
      if (known) { item.details = known; continue; }
      const done = value => {
        if (!value || !media.isConnected) return;
        item.details = value;
        const current = state.floats.get(media);
        if (current?.visible) rebuildMedia(current);
      };
      const pending = state.inflight.get(item.url.href);
      if (pending) { pending.then(done); continue; }
      void verify(item.url.href, "media").then(done);
    }
    return record;
  }

  // One capture path for every Velocity button: the same native-host message the link and pill buttons send.
  async function postToVelocity(item) {
    const source = item.details || {};
    const url = source.url || item.url?.href || "";
    if (!url) return { ok: false, error: "This file has no download link." };

    const last = state.sent.get(url);
    if (last && Date.now() - last < RESEND_GUARD_MS) return { ok: true, destination: "" };

    try {
      const response = await ask({
        type: "velocity-action",
        action: "start",
        details: {
          url,
          name: source.name || fileNameOf(url),
          size: source.size ?? null,
          type: source.type || "",
          site: source.site || location.host,
          pageUrl: location.href
        }
      }, 20000);
      if (!response?.ok) return { ok: false, error: response?.error || "Velocity did not accept the download." };
      state.sent.set(url, Date.now());
      item.sent = true;
      return { ok: true, destination: response.destination || "" };
    } catch (error) {
      return {
        ok: false,
        error: error?.message === "timeout"
          ? "Velocity did not answer in time. Check the native host."
          : String(error?.message || error)
      };
    }
  }

  async function sendMediaItem(record, item) {
    if (record.state === "sending" || item.sent) return;
    if (item.stream && item.stream.downloadable === false) {
      setState(record, "error", item.stream.reason || "This stream cannot be downloaded as one file.");
      return;
    }
    setState(record, "sending", "Sending to Velocity…");
    const outcome = await postToVelocity(item);
    if (!outcome.ok) { setState(record, "error", outcome.error); return; }
    setState(record, "done", outcome.destination ? `Saved to ${outcome.destination}` : "Added to Velocity");
    setTimeout(() => hideRecord(record, true), 2400);
  }

  function setState(record, next, message) {
    record.state = next;
    record.message = message || "";
    record.host.setAttribute("data-velocity-float-state", next);

    if (record.kind === "link") {
      record.node.dataset.state = next;
      record.node.disabled = next === "sending";
      if (next === "sending") {
        record.node.replaceChildren(element("span", "spinner"));
      } else if (next === "done") {
        record.node.replaceChildren(element("span", "", "✓"));
      } else if (next === "error") {
        record.node.replaceChildren(element("span", "", "!"));
        record.node.title = message || "Velocity could not start this download.";
        record.node.setAttribute("aria-label", message || "Velocity could not start this download.");
      } else {
        record.node.replaceChildren(glyph());
        record.node.disabled = false;
      }
      return;
    }

    if (record.kind === "media") {
      const bar = record.node;
      if (!bar) return;
      bar.dataset.state = next;
      if (next === "error" && message) bar.title = message;
      if (record.text) {
        record.text.textContent = next === "sending" ? "Sending…"
          : next === "done" ? (message || "Added to Velocity")
            : next === "error" ? "Could not send"
              : "Download this video";
      }
      if (record.mark) record.mark.replaceChildren(...(next === "sending" ? [element("span", "spinner")] : []));
      if (next === "done" || next === "error") {
        for (const row of bar.querySelectorAll(".mrow")) {
          if (row.dataset.sent === "true") row.querySelector(".mmeta").textContent = "Added";
        }
      }
      return;
    }

    if (!record.node) return;
    const sample = [describe(record.details), typeLabel(record.details), record.details.site]
      .filter(Boolean).join(" · ");
    record.sub.textContent = next === "error" ? (message || "Could not start the download.") : next === "done" ? (message || "Added to Velocity") : sample;
    if (record.go) {
      record.go.dataset.state = next;
      record.go.disabled = next === "sending" || next === "done";
      record.go.replaceChildren(
        next === "sending" ? element("span", "spinner") : next === "done" ? element("span", "", "✓") : glyph(),
        element("span", "", next === "done" ? "Added" : next === "error" ? "Retry" : "Download")
      );
    }
  }

  function place(record) {
    if (!record.visible) return;
    ensureOverlayParent(record.host);
    // A player the page is rebuilding (a React re-render, an MSE source swap) is not an absent video: the strip
    // keeps its place and re-attaches itself when the new element appears, instead of vanishing for good.
    if (record.element && !record.element.isConnected && !adoptPlayer(record)) return;
    const box = record.element?.isConnected ? record.element.getBoundingClientRect() : null;
    if (!box) return;

    const origin = overlayOrigin();

    const onScreen = box.bottom > 4 && box.top < innerHeight - 4 && box.right > 4 && box.left < innerWidth - 4 && box.width > 0;
    if (!onScreen) { record.host.style.opacity = "0"; record.host.style.pointerEvents = "none"; return; }
    record.host.style.opacity = "1";
    record.host.style.pointerEvents = "none";

    const node = record.node;
    node.style.pointerEvents = "auto";

    if (record.kind === "link") {
      const size = 26;
      let left = box.right + 6;
      if (left + size > innerWidth - 6) left = box.right - size - 6;
      if (left < 6) left = Math.min(innerWidth - size - 6, Math.max(6, box.left));
      const top = Math.min(Math.max(6, box.top + (box.height - size) / 2), innerHeight - size - 6);
      node.style.left = `${Math.round(left - origin.x)}px`;
      node.style.top = `${Math.round(top - origin.y)}px`;
      return;
    }

    if (record.kind === "media") {
      // IDM places its strip in the video's upper-right corner; the panel below it stays inside the viewport.
      const width = Math.max(140, Math.min(300, node.offsetWidth || 176));
      let left = box.right - width - 10;
      if (left < 6) left = 6;
      if (left + width > innerWidth - 6) left = Math.max(6, innerWidth - width - 6);
      const top = Math.min(Math.max(6, box.top + 10), Math.max(6, innerHeight - 38));
      node.style.left = `${Math.round(left - origin.x)}px`;
      node.style.top = `${Math.round(top - origin.y)}px`;
      return;
    }

    const width = Math.min(296, Math.max(180, node.offsetWidth || 220));
    let left = box.right - width - 8;
    if (left < 8) left = 8;
    if (left + width > innerWidth - 8) left = Math.max(8, innerWidth - width - 8);
    let top = box.top + 8;
    if (top + 40 > innerHeight - 8) top = Math.max(8, box.bottom - 48);
    node.style.left = `${Math.round(left)}px`;
    node.style.top = `${Math.round(top)}px`;
  }

  function tick() {
    state.frame = 0;
    let visible = 0;
    for (const record of state.floats.values()) {
      if (!record.visible) continue;
      visible++;
      place(record);
    }
    if (visible) state.frame = requestAnimationFrame(tick);
  }

  /* ─────────────────────────── overlay plumbing ─────────────────────────── */

  // The fullscreen element is painted in the browser's top layer, and anything left in <body> is not visible
  // inside it. Moving the strip into that element is what keeps IDM's bar on screen in fullscreen playback.
  function overlayParent() {
    return document.fullscreenElement || document.body || document.documentElement;
  }

  function ensureOverlayParent(host) {
    if (!host) return;
    const parent = overlayParent();
    if (parent && host.parentNode !== parent) parent.append(host);
  }

  // A fullscreen element that creates a containing block (transform, filter, containment) shifts fixed
  // coordinates, so the strip is placed relative to it in that case.
  function overlayOrigin() {
    const full = document.fullscreenElement;
    if (!full) return { x: 0, y: 0 };
    const style = getComputedStyle(full);
    const contained = style.transform !== "none" || style.perspective !== "none" || style.filter !== "none" ||
      /paint|layout|strict|content/.test(style.contain || "");
    if (!contained) return { x: 0, y: 0 };
    const box = full.getBoundingClientRect();
    return { x: box.left, y: box.top };
  }

  // A player element the page replaced with a fresh one: the strip follows the new video instead of dying with
  // the old element.
  function adoptPlayer(record) {
    const owned = new Set([...state.floats.values()].filter(other => other !== record).map(other => other.element));
    let best = null;
    let bestArea = 0;
    for (const media of document.querySelectorAll("video, audio")) {
      if (!media.isConnected || owned.has(media)) continue;
      const box = media.getBoundingClientRect();
      const area = box.width * box.height;
      if (area <= bestArea) continue;
      best = media;
      bestArea = area;
    }
    if (!best) return false;
    record.element = best;
    return true;
  }

  function itemsSignature(items) {
    return (items || [])
      .map(item => `${item.key || ""}|${item.label || ""}|${item.details?.size ?? ""}|${item.details?.name || ""}`)
      .join("~");
  }

  function ensureTick() {
    if (!state.frame) state.frame = requestAnimationFrame(tick);
  }

  function show(element_, details, kind) {
    const existing = state.floats.get(element_);
    if (existing) {
      existing.details = details;
      existing.visible = true;
      existing.host.style.display = "";
      if (kind === "link") state.link = existing;
      place(existing);
      ensureTick();
      return existing;
    }

    if (kind === "link") hideLink();

    const record = createRecord(element_, details, kind);
    state.floats.set(element_, record);
    record.visible = true;
    if (kind === "link") state.link = record;
    place(record);
    record.node.style.opacity = "0";
    requestAnimationFrame(() => { if (record.visible) record.node.style.opacity = "1"; });
    ensureTick();
    return record;
  }

  function hideRecord(record, remove) {
    if (!record) return;
    record.visible = false;
    if (remove) {
      record.host.remove();
      if (record.element && state.floats.get(record.element) === record) state.floats.delete(record.element);
      if (state.link === record) state.link = null;
      const index = state.network.indexOf(record);
      if (index >= 0) state.network.splice(index, 1);
      stackNetwork();
      return;
    }
    record.host.style.display = "none";
  }

  function hideFor(element_) {
    const record = state.floats.get(element_);
    if (record) hideRecord(record, false);
  }

  function hideLink() {
    if (!state.link) return;
    hideRecord(state.link, true);
    state.link = null;
  }

  function scheduleLinkHide() {
    clearTimeout(state.hideTimer);
    state.hideTimer = setTimeout(hideLink, HIDE_DELAY_MS);
  }

  function cancelLinkHide() {
    clearTimeout(state.hideTimer);
    state.hideTimer = 0;
  }

  function stackNetwork() {
    // Newest pill at the bottom; the stack grows upwards and never covers more than three offers.
    state.network.forEach((record, index) => {
      record.node.style.right = "18px";
      record.node.style.left = "auto";
      record.node.style.bottom = `${18 + index * 46}px`;
      record.node.style.top = "auto";
    });
  }

  /* ─────────────────────────── network offers ─────────────────────────── */

  function showNetwork(item) {
    if (!item?.url) return;
    // A file the page fetched on its own is only offered after the user did something on the page. Manifests are the
    // exception: they describe the video that is playing on screen, and their strip is attached to that video.
    if (item.kind !== "stream" && !state.interacted) return;
    // A manifest is not a file to offer in a corner: it is the video behind the player, so it is attached to
    // that player and its declared qualities fill the strip's own menu.
    if (item.kind === "stream") {
      rememberStream(item);
      const player = currentPlayer();
      if (player && state.enabled) {
        void showStream(player, state.streams.get(identityOf(item.url)));
        return;
      }
    }
    if (state.dismissed.has(item.url)) return;
    if (state.network.some(record => identityOf(record.details.url) === identityOf(item.url))) return;
    const shown = state.networkSeen.get(item.url);
    if (shown && Date.now() - shown < 300000) return;
    // The page's own media element already has a pill; do not stack a second offer for the same file.
    for (const record of state.floats.values()) if (record.details.url === item.url) return;

    const record = {
      element: null, kind: "network", details: item, state: "ready", message: "", visible: true, network: true, timer: 0
    };

    const host = document.createElement("div");
    host.setAttribute("data-velocity-float", "network");
    host.setAttribute("data-velocity-float-state", "ready");
    host.setAttribute("data-velocity-url", item.url);
    host.setAttribute("data-velocity-file", item.name || "");
    host.style.cssText = `all: initial; position: fixed; inset: auto 0 0 auto; width: 0; height: 0; z-index: ${Z_INDEX};`;
    const root = host.attachShadow({ mode: "closed" });
    attachStyles(root, state.theme);

    record.host = host;
    record.root = root;
    record.node = pill(record);
    root.append(record.node);
    (document.body || document.documentElement).append(host);

    state.networkSeen.set(item.url, Date.now());
    state.network.unshift(record);
    if (state.network.length > MAX_NETWORK_PILLS) {
      const oldest = state.network.pop();
      hideRecord(oldest, true);
    }
    stackNetwork();
    record.timer = setTimeout(() => hideRecord(record, true), NETWORK_PILL_MS);

    // A sniffed manifest is the video behind a player that exposes no element (blob/MSE). Its playlist is read
    // so the offer names the real quality and size instead of showing a bare ".m3u8".
    if (item.kind === "stream") {
      void ask({ type: "velocity-manifest", url: item.url }, 20000).then(response => {
        const stream = response?.stream;
        if (!stream || !record.visible) return;
        record.stream = stream;
        record.details = { ...record.details, ...streamFacts(stream, item.url) };
        if (record.node?.isConnected) {
          const replacement = pill(record);
          record.node.replaceWith(replacement);
          record.node = replacement;
          stackNetwork();
        }
      }, () => { /* the plain offer still works */ });
    }
  }

  /* ─────────────────────────── streams behind a player ─────────────────────────── */

  function rememberStream(item) {
    const key = identityOf(item.url);
    const previous = state.streams.get(key);
    state.streams.set(key, {
      url: item.url,
      identity: key,
      details: item,
      stream: previous?.stream || null,
      request: previous?.request || null,
      at: Date.now()
    });
    // Newest first: the manifest the player is using right now is the one the strip must offer.
    state.streamOrder = [key, ...state.streamOrder.filter(value => value !== key)].slice(0, 8);
  }

  function latestStream() {
    for (const key of state.streamOrder) {
      const entry = state.streams.get(key);
      // A manifest from an earlier video on the same page is not the current one.
      if (entry && Date.now() - entry.at < 6 * 60 * 60 * 1000) return entry;
    }
    return null;
  }

  // The player the strip belongs to: one already carrying a strip, then one that is playing, then the largest
  // visible one. This is what attaches a sniffed manifest to the video the user is actually watching.
  function currentPlayer() {
    const owned = new Set([...state.floats.values()].map(record => record.element).filter(Boolean));
    let best = null;
    let bestScore = -1;
    for (const media of document.querySelectorAll("video, audio")) {
      if (!media.isConnected) continue;
      const box = media.getBoundingClientRect();
      if (box.width < 120 || box.height < 28) continue;
      if (box.bottom < 0 || box.top > innerHeight || box.right < 0 || box.left > innerWidth) continue;
      const score = (owned.has(media) ? 1e9 : 0) + (media.paused ? 0 : 1e6) + box.width * box.height;
      if (score > bestScore) { best = media; bestScore = score; }
    }
    return best;
  }

  // The rows a sniffed manifest contributes: one per declared quality, or the playlist itself when it is a
  // plain media playlist with nothing to choose between.
  function streamItems(entry) {
    const stream = entry.stream;
    if (stream && Array.isArray(stream.variants) && stream.variants.length > 1) return variantItems(stream, entry.url);
    const url = urlOf(entry.url);
    if (!url) return [];
    return [{
      url,
      role: "stream",
      key: url.pathname,
      stream: stream || null,
      label: stream ? streamLabelOf(stream) : "HLS",
      estimated: Boolean(stream?.estimated),
      details: { url: url.href, site: location.host, ...streamFacts(stream || { kind: "hls" }, entry.url) }
    }];
  }

  async function showStream(media, entry) {
    if (!media || !media.isConnected || !entry || !state.enabled) return;
    if (state.dismissed.has(entry.url)) return;
    if (!entry.stream) {
      entry.request = entry.request || ask({ type: "velocity-manifest", url: entry.url }, 20000)
        .then(response => {
          entry.stream = response?.ok === false ? null : (response?.stream || null);
          return entry.stream;
        }, () => null);
      await entry.request;
      if (!media.isConnected || !state.enabled) return;
    }
    const rows = streamItems(entry);
    if (!rows.length) return;
    entry.at = Date.now();
    const facts = streamFacts(entry.stream || { kind: "hls" }, entry.url);
    showMedia(media, { url: entry.url, site: location.host, ...facts }, rows);
  }

  /* ─────────────────────────── candidates ─────────────────────────── */

  function absolute(value) {
    if (!value) return null;
    let url;
    try { url = new URL(value, location.href); } catch { return null; }
    if (!/^https?:$/.test(url.protocol)) return null;   // blob:, data:, javascript: belong to the page
    return url;
  }

  function linkCandidate(link) {
    if (!link || link.hasAttribute("data-velocity-skip")) return null;
    const href = link.getAttribute("href");
    if (!href || href.startsWith("#")) return null;
    const url = absolute(link.href || href);
    if (!url) return null;
    if (STREAM_HINT.test(url.pathname)) return null;   // playlists and segments are streams, not files
    // A missing extension proves nothing: plenty of real downloads are served through a script
    // ("/download?id=42", signed URLs). Those are checked with headers when the user hovers them; the
    // extension only decides which links are worth checking in advance.
    const hinted = link.hasAttribute("download") || FILE_HINT.test(url.pathname + url.search);
    return { url, hinted };
  }

  // Every http(s) file the player itself points at: the video that is playing, its alternate sources, its
  // subtitle tracks, and the stream manifests behind an HLS/DASH player. A blob:/data: player is a byte stream
  // built inside the page, so it is never a candidate.
  // A human label for a source: the player's own label/res attribute, then a resolution in the URL, then the
  // container. Two sources with the same label cannot be told apart, so the strip does not offer a choice then.
  const QUALITY_IN_URL = /(?:^|[^\d])(240|360|480|540|720|1080|1440|2160)(?:p|[^\d]|$)/i;

  // The player's own attributes come first, then the URL, then the resolution the element reports for the stream it
  // is playing, and only then the container. This is what the list labels a row with, IDM-style.
  function qualityLabel(url, mediaElement, media) {
    const explicit = ["label", "res", "data-res", "data-quality", "quality", "size"]
      .map(name => mediaElement?.getAttribute?.(name))
      .find(value => value && String(value).trim());
    if (explicit) return String(explicit).trim();

    const text = `${url.pathname}${url.search}`;
    const height = QUALITY_IN_URL.exec(text);
    if (height) return `${height[1]}p`;
    const named = /(?:quality|res|height|h)=(\d{3,4})/i.exec(url.search);
    if (named) return `${named[1]}p`;
    if (/(?:^|[^\d])(4k|uhd)(?:[^\d]|$)/i.test(text)) return "4K";
    if (/(?:^|[^\d])hd(?:[^\d]|$)/i.test(text)) return "HD";
    if (/(?:^|[^\d])sd(?:[^\d]|$)/i.test(text)) return "SD";

    // The element knows the real height of the stream it is playing, even when the URL says nothing.
    if (media && url.href === media.currentSrc && Number(media.videoHeight) >= 144) return `${media.videoHeight}p`;

    const extension = /\.([a-z0-9]{2,4})(?:$|[?#])/i.exec(url.pathname)?.[1];
    return extension ? extension.toUpperCase() : "";
  }

  function mediaItems(media) {
    const items = [];
    const seen = new Set();
    const push = (value, role, mediaElement) => {
      const url = absolute(value);
      if (!url || seen.has(url.href)) return;
      // Fragments are never the video. A .ts URL is a real MPEG-TS file here, so it stays a candidate.
      if (/\.(m4s|ism)(?:$|[?#])/i.test(url.pathname)) return;
      seen.add(url.href);
      // A playlist is the video described as segments: it becomes one stream entry, never a fragment.
      if (MANIFEST_HINT.test(url.pathname)) {
        items.push({ url, role: "stream", label: /\.mpd$/i.test(url.pathname) ? "DASH" : "HLS", key: url.pathname, stream: null });
        return;
      }
      const label = role === "track" ? "VTT" : qualityLabel(url, mediaElement, media);
      items.push({ url, role, label, key: label || fileNameOf(url) });
    };
    if (media.currentSrc) push(media.currentSrc, "video", media);
    push(media.getAttribute("src"), "video", media);
    for (const source of media.querySelectorAll("source[src]")) push(source.getAttribute("src"), "video", source);
    for (const track of media.querySelectorAll("track[src]")) push(track.getAttribute("src"), "track", track);
    return items;
  }

  function mediaCandidate(media) {
    return mediaItems(media)[0]?.url ?? null;
  }

  /* ─────────────────────────── reveal & scan ─────────────────────────── */

  function revealLink(link) {
    const candidate = linkCandidate(link);
    if (!candidate) { scheduleLinkHide(); return; }
    const url = candidate.url;
    if (state.dismissed.has(url.href)) { scheduleLinkHide(); return; }

    const cached = state.verified.get(url.href);
    if (cached) { show(link, cached, "link"); return; }

    const pending = state.inflight.get(url.href);
    if (pending) {
      pending.then(details => { if (details && link.isConnected) show(link, details, "link"); });
      return;
    }
    // A link without a file hint is probed with HEAD only, so hovering can never fire a page's GET
    // handlers (an unsubscribe or delete endpoint behind an innocent-looking anchor).
    void verify(url.href, "link", !candidate.hinted).then(details => {
      if (details && link.isConnected && hovered(link)) show(link, details, "link");
    });
  }

  let hoverTarget = null;

  function hovered(link) {
    try { return hoverTarget === link || link.matches(":hover"); } catch { return true; }
  }

  function revealMedia(media) {
    const items = mediaItems(media);
    if (!items.length) {
      // An MSE/blob player has no address of its own, but the manifest the page fetched for it is the video.
      const stream = latestStream();
      if (stream) void showStream(media, stream);
      // Nothing to offer yet: an existing strip stays put rather than blinking out of existence.
      return;
    }
    if (state.dismissed.has(items[0].url.href)) { hideFor(media); return; }
    const box = media.getBoundingClientRect();
    if (box.width < 120 || box.height < 28) return;
    // Only offer what the user can actually see, so a hidden player never produces a stray button.
    if (box.bottom < 0 || box.top > innerHeight) return;

    const primary = items[0].url.href;
    // A manifest has no size to probe: it is shown at once and its own properties arrive from the playlist.
    if (items[0].role === "stream") {
      showMedia(media, { url: primary, name: fileNameOf(primary), size: null, type: "" }, items);
      return;
    }
    const cached = state.verified.get(primary);
    if (cached) { showMedia(media, cached, items); return; }
    const pending = state.inflight.get(primary);
    if (pending) { pending.then(details => { if (details && media.isConnected) showMedia(media, details, items); }); return; }
    void verify(primary, "media").then(details => {
      if (details && media.isConnected) showMedia(media, details, items);
    });
  }

  function watchMedia(media) {
    if (!media || watchedMedia.has(media)) return;
    watchedMedia.add(media);
    if (state.media) state.media.observe(media);
    // A player reports its source and its real resolution only once metadata arrives, and swaps them while
    // playing, so the strip is refreshed on those events instead of trusting the first scan.
    // Every moment that can change what the player offers: metadata, playback, seeking, buffering and a
    // source swap. The strip is refreshed for all of them and never removed by them.
    for (const type of ["loadedmetadata", "loadeddata", "canplay", "resize", "play", "playing", "pause", "seeking", "seeked", "waiting", "stalled", "emptied", "durationchange", "ratechange"]) {
      media.addEventListener(type, () => { if (state.enabled) revealMedia(media); }, { passive: true });
    }
  }

  let watchedMedia = new WeakSet();

  function collect(root) {
    if (!root || (root.nodeType !== 1 && root.nodeType !== 9 && root.nodeType !== 11)) return;
    const scope = root.nodeType === 1 ? root : document;
    if (scope.matches?.("video, audio")) watchMedia(scope);
    for (const media of scope.querySelectorAll("video, audio")) watchMedia(media);
  }

  // IntersectionObserver is the normal trigger, but a restored or backgrounded tab can miss its first
  // notification. A small sweep on load, scroll and visibility changes keeps a real player from being
  // ignored just because of when it entered the page.
  let sweepTimer = 0;

  function sweepMedia() {
    if (!state.enabled) return;
    clearTimeout(sweepTimer);
    sweepTimer = setTimeout(() => {
      if (!state.enabled) return;
      let looked = 0;
      for (const media of document.querySelectorAll("video, audio")) {
        if (looked >= 12) break;
        looked++;
        if (state.floats.get(media)?.visible) continue;
        revealMedia(media);
      }
    }, 200);
  }

  function prefetch() {
    if (!state.enabled) return;
    let probes = 0;
    for (const link of document.querySelectorAll("a[href]")) {
      if (probes >= PREFETCH_LIMIT) break;
      const candidate = linkCandidate(link);
      // Only links that already look like files are verified in advance; anything else waits for a hover,
      // so a page full of ordinary links never triggers a burst of requests.
      if (!candidate || !candidate.hinted) continue;
      const url = candidate.url;
      if (state.verified.has(url.href) || state.inflight.has(url.href)) continue;
      const rejectedAt = state.rejected.get(url.href);
      if (rejectedAt && Date.now() - rejectedAt < 60000) continue;
      probes++;
      void verify(url.href, "link");
    }
  }

  /* ─────────────────────────── wiring ─────────────────────────── */

  function onPointerOver(event) {
    if (!state.enabled) return;
    const target = event.target;
    if (!(target instanceof Element)) return;
    if (target.closest("[data-velocity-float]")) return;   // moving onto our own button keeps it

    const link = target.closest("a[href]");
    if (!link) {
      hoverTarget = null;
      scheduleLinkHide();
      return;
    }
    if (link.closest("[data-velocity-float]")) return;
    if (!linkCandidate(link)) { hoverTarget = null; scheduleLinkHide(); return; }

    hoverTarget = link;
    cancelLinkHide();
    revealLink(link);
  }

  function onFocusIn(event) {
    if (!state.enabled) return;
    const link = event.target instanceof Element ? event.target.closest("a[href]") : null;
    if (!link) return;
    if (!linkCandidate(link)) return;
    hoverTarget = link;
    revealLink(link);
  }

  function onKeyDown(event) {
    if (event.key === "Escape" && state.link) { cancelLinkHide(); hideLink(); }
  }

  function start() {
    if (state.started) return;
    state.started = true;
    document.addEventListener("pointerover", onPointerOver, true);
    document.addEventListener("focusin", onFocusIn, true);
    document.addEventListener("keydown", onKeyDown, true);
    // Any real interaction unlocks the passive offers for this page. Scrolling deliberately does not: reading a page
    // is browsing, not asking for a download.
    for (const type of INTERACTION_TYPES) {
      document.addEventListener(type, markInteracted, { capture: true, passive: true });
    }
    document.addEventListener("scroll", () => { if (state.link) ensureTick(); sweepMedia(); }, true);
    document.addEventListener("fullscreenchange", onFullscreenChange, true);
    document.addEventListener("pointerdown", onOutsidePointer, true);
    globalThis.addEventListener("resize", ensureTick);

    if ("IntersectionObserver" in globalThis) {
      state.media = new IntersectionObserver(entries => {
        if (!state.enabled) return;
        for (const entry of entries) {
          const media = entry.target;
          // Only entering the viewport is a trigger. Leaving it must not delete the strip: a player that peeks
          // off-screen while scrolling or while its controls appear would otherwise lose the bar for good.
          if (entry.isIntersecting) revealMedia(media);
        }
      }, { threshold: [0, 0.2, 0.5] });
    }

    collect(document);

    if ("MutationObserver" in globalThis) {
      state.observer = new MutationObserver(records => {
        if (!state.enabled) return;
        for (const record of records) {
          if (record.type === "attributes") {
            const target = record.target;
            if (target.matches?.("video, audio")) revealMedia(target);
            else collect(target);
            continue;
          }
          for (const node of record.addedNodes) {
            if (node.nodeType !== 1) continue;
            collect(node);
            const media = node.matches?.("video, audio") ? node : node.querySelector?.("video, audio");
            if (media) revealMedia(media);
          }
        }
      });
      state.observer.observe(document.documentElement, {
        childList: true,
        subtree: true,
        attributeFilter: ["src", "href"]
      });
    }

    const idle = globalThis.requestIdleCallback || (callback => setTimeout(callback, 1200));
    idle(() => { prefetch(); sweepMedia(); });
    setTimeout(sweepMedia, 1500);
    globalThis.addEventListener("load", sweepMedia);
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible") { sweepMedia(); ensureTick(); }
    });
  }

  function onFullscreenChange() {
    for (const record of state.floats.values()) ensureOverlayParent(record.host);
    for (const record of state.network) ensureOverlayParent(record.host);
    if (state.link) ensureOverlayParent(state.link.host);
    ensureTick();
  }

  function onOutsidePointer(event) {
    const target = event.target;
    if (target instanceof Element && target.closest("[data-velocity-float]")) return;
    for (const record of state.floats.values()) {
      if (record.kind !== "media" || !record.listOpen) continue;
      record.listOpen = false;
      if (record.list) record.list.dataset.open = "false";
      const chevron = record.node?.querySelector?.(".mchevron");
      if (chevron) chevron.setAttribute("aria-expanded", "false");
    }
  }

  function stop() {
    if (!state.started) return;
    state.started = false;
    document.removeEventListener("pointerover", onPointerOver, true);
    document.removeEventListener("focusin", onFocusIn, true);
    document.removeEventListener("keydown", onKeyDown, true);
    for (const type of INTERACTION_TYPES) document.removeEventListener(type, markInteracted, true);
    document.removeEventListener("fullscreenchange", onFullscreenChange, true);
    document.removeEventListener("pointerdown", onOutsidePointer, true);
    globalThis.removeEventListener("resize", ensureTick);
    state.observer?.disconnect();
    state.observer = null;
    state.media?.disconnect();
    state.media = null;
    watchedMedia = new WeakSet();
    if (state.frame) { cancelAnimationFrame(state.frame); state.frame = 0; }
    for (const record of [...state.floats.values()]) hideRecord(record, true);
    for (const record of [...state.network]) hideRecord(record, true);
    hideLink();
  }

  function applySettings(options) {
    if (!options) return;
    state.excluded = options.excluded || [];
    const excluded = state.excluded.some(entry => {
      const value = String(entry || "").trim().replace(/^www\./i, "").toLowerCase();
      if (!value) return false;
      const host = location.hostname.toLowerCase();
      return host === value || host.endsWith(`.${value}`);
    });

    state.enabled = options.detect !== false && !excluded;
    if (state.enabled && !state.started) start();
    else if (!state.enabled && state.started) stop();
  }

  async function boot(attempt = 0) {
    try {
      await loadPreferences();
      const response = await ask({ type: "velocity-detect-boot" }, 8000);
      if (!response?.ok) return;
      if (THEMES[response.theme]) state.theme = response.theme;
      applySettings({ detect: response.enabled !== false, excluded: response.excluded || [] });
      // A page can load its playlist before this script has started, and that one message would otherwise be
      // lost for good. Asking the worker for the tab's last manifest makes the strip appear anyway.
      if (state.enabled) {
        const streams = await ask({ type: "velocity-streams" }, 5000).catch(() => null);
        if (streams?.stream && state.enabled) showNetwork({ ...streams.stream, kind: "stream" });
      }
      // A choice made in another tab (or restored from storage) updates every strip on this page.
      for (const record of state.floats.values()) if (record.kind === "media") refreshMedia(record);
    } catch {
      // The worker starts on demand; one retry covers a service worker that was mid-shutdown.
      if (attempt === 0) setTimeout(() => void boot(1), 800);
    }
  }

  /* ─────────────────────────── boot ─────────────────────────── */

  api.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    // The page's own network traffic knows the real type and size of what it is loading.
    if (message?.type === "velocity-properties") {
      sendResponse({ updated: rememberProperties(message.item) });
      return;
    }
    if (message?.type !== "velocity-detected") return;
    if (state.enabled) showNetwork(message.item);
    sendResponse({ shown: state.enabled });
    return;
  });

  try {
    api.storage.onChanged.addListener((changes, area) => {
      if (area !== "local" || !changes) return;
      if (!("detect" in changes) && !("excluded" in changes) && !("theme" in changes) && !(PREF_KEY in changes)) return;
      void boot();
    });
  } catch { /* storage events are optional */ }

  void boot();

  globalThis.addEventListener("pagehide", stop);
  // A page restored from the back/forward cache comes back with the same document, so the listeners removed
  // by pagehide have to be put back.
  globalThis.addEventListener("pageshow", event => { if (event.persisted) void boot(); });
})();
