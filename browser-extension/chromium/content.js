/*
 * Velocity in-page download offer.
 *
 * Rendered in a closed shadow root so the host page's stylesheet cannot reach it, and its CSS is
 * delivered through a constructed stylesheet so a strict Content-Security-Policy cannot block it.
 * Nothing here simulates progress: it reports what the native host actually answered.
 */
(() => {
  if (globalThis.__velocityOfferInstalled) return;
  globalThis.__velocityOfferInstalled = true;

  const api = globalThis.browser ?? globalThis.chrome;
  const HOST_ID = "velocity-offer-host";
  const DOWNLOAD_HINT = /\.(7z|aac|apk|avi|bin|bmp|csv|deb|dmg|docx?|epub|exe|flac|gif|gz|img|iso|jar|mpe?g|jpe?g|m4a|mkv|mov|mp3|mp4|msi|odt|ogg|pdf|png|pptx?|psd|rar|rpm|rtf|svg|tar|tgz|torrent|ts|ttf|txt|wav|webm|webp|xlsx?|zip)(?:$|[?#])/i;

  const THEMES = {
    Midnight: { card: "#141823", alt: "#1A1F2B", border: "#232A37", text: "#F2F5FA", secondary: "#AFBACB", muted: "#7A8698", accent: "#5B8CFF", accent2: "#7C6BFF", success: "#34D399", danger: "#F87171", shadow: "0 20px 44px rgba(0,0,0,.58)" },
    Ember: { card: "#221A16", alt: "#2A201A", border: "#3A2C24", text: "#FFF4EA", secondary: "#E3CBB6", muted: "#B08F76", accent: "#E8792F", accent2: "#F2A03D", success: "#4CCB92", danger: "#F0746F", shadow: "0 20px 44px rgba(0,0,0,.5)" },
    Light: { card: "#FFFFFF", alt: "#F7FAFE", border: "#E2E8F2", text: "#0F172A", secondary: "#44506A", muted: "#6C7890", accent: "#3B6FD4", accent2: "#6A7BF0", success: "#0F9D63", danger: "#C93A50", shadow: "0 20px 44px rgba(15,23,42,.18)" }
  };

  let active = null;          // { host, card, details, closeTimer, restoreFocus }
  let probing = false;
  // A second detection must never replace a prompt that is still unanswered: that file's browser copy is already
  // cancelled, so it would be lost without ever being offered.
  const queued = [];

  // Every extension round-trip is bounded, so a stalled worker can never deaden page clicks.
  function ask(message, milliseconds) {
    return Promise.race([
      api.runtime.sendMessage(message),
      new Promise((_, reject) => setTimeout(() => reject(new Error("timeout")), milliseconds))
    ]);
  }

  /* ─────────────────────────── formatting ─────────────────────────── */

  function formatBytes(value) {
    if (value === null || value === undefined || Number.isNaN(Number(value))) return "Unknown size";
    let size = Number(value);
    const units = ["B", "KB", "MB", "GB", "TB"];
    let index = 0;
    while (size >= 1024 && index < units.length - 1) { size /= 1024; index++; }
    return `${size.toFixed(index === 0 || size >= 100 ? 0 : 1)} ${units[index]}`;
  }

  function typeLabel(details) {
    const name = String(details.name || "");
    const extension = /\.([a-z0-9]{1,5})$/i.exec(name)?.[1];
    if (extension) return extension.toUpperCase();
    if (details.type) {
      const subtype = details.type.split("/")[1];
      if (subtype) return subtype.split(";")[0].toUpperCase().slice(0, 6);
    }
    return "FILE";
  }

  function styles(theme) {
    const t = THEMES[theme] || THEMES.Midnight;
    return `
      :host { all: initial; }
      * { box-sizing: border-box; }
      .wrap {
        position: fixed; inset: 0; display: flex; align-items: center; justify-content: center;
        padding: 16px; pointer-events: none; z-index: 2147483647;
      }
      .card {
        pointer-events: auto; width: 372px; max-width: calc(100vw - 32px); max-height: calc(100vh - 32px);
        overflow: auto;
        background: ${t.card}; color: ${t.text}; border: 1px solid ${t.border}; border-radius: 14px;
        box-shadow: ${t.shadow}; padding: 14px 16px 15px; font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
        font-size: 13px; line-height: 1.45; animation: velocity-in .18s ease-out;
      }
      @media (prefers-reduced-motion: reduce) { .card { animation: none; } }
      @keyframes velocity-in { from { opacity: 0; transform: translateY(-10px) scale(.99); } to { opacity: 1; transform: none; } }
      .head { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
      .brand {
        width: 30px; height: 30px; border-radius: 9px; flex: none;
        background: linear-gradient(135deg, ${t.accent}, ${t.accent2});
        display: flex; align-items: center; justify-content: center;
        color: #fff; font-weight: 700; font-size: 15px;
      }
      .titles { flex: 1; min-width: 0; }
      .title { font-size: 13.5px; font-weight: 600; letter-spacing: .1px; }
      .subtitle { font-size: 11px; color: ${t.muted}; margin-top: 1px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .close {
        all: unset; cursor: pointer; width: 26px; height: 26px; border-radius: 7px; flex: none;
        display: flex; align-items: center; justify-content: center; color: ${t.muted}; font-size: 17px; line-height: 1;
      }
      .close:hover { background: ${t.alt}; color: ${t.text}; }
      .close:focus-visible { outline: 2px solid ${t.accent}; outline-offset: 1px; }
      .name { font-size: 13.5px; font-weight: 600; word-break: break-all; margin-bottom: 6px; }
      .chips { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin-bottom: 11px; }
      .chip {
        font-size: 10.5px; font-weight: 600; letter-spacing: .4px; padding: 2px 7px; border-radius: 6px;
        background: ${t.alt}; border: 1px solid ${t.border}; color: ${t.secondary};
      }
      .chip.size { letter-spacing: 0; font-weight: 500; }
      .meta { display: grid; grid-template-columns: 58px 1fr; gap: 8px; font-size: 11.5px; margin-bottom: 5px; }
      .meta .label { color: ${t.muted}; }
      .meta .value { color: ${t.secondary}; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .meta .value.path { font-family: "Cascadia Mono", "Consolas", monospace; font-size: 11px; }
      .error {
        margin: 4px 0 10px; padding: 9px 10px; border-radius: 8px; font-size: 11.5px; line-height: 1.5;
        color: ${t.danger}; background: ${theme === "Light" ? "#FCEAEE" : "rgba(248,113,113,.12)"};
        border: 1px solid ${theme === "Light" ? "#F6CEDA" : "rgba(248,113,113,.3)"};
      }
      .actions { display: flex; gap: 8px; margin-top: 12px; }
      .btn {
        all: unset; cursor: pointer; flex: 1; text-align: center; padding: 9px 10px; border-radius: 9px;
        font-size: 12.5px; font-weight: 600; border: 1px solid transparent; transition: background .12s ease, border-color .12s ease;
      }
      .btn:focus-visible { outline: 2px solid ${t.accent}; outline-offset: 2px; }
      .btn.primary { color: #fff; background: linear-gradient(135deg, ${t.accent}, ${t.accent2}); }
      .btn.primary:hover { filter: brightness(1.08); }
      .btn.secondary { color: ${t.text}; background: ${t.alt}; border-color: ${t.border}; }
      .btn.secondary:hover { border-color: ${t.accent}; }
      .btn.ghost { color: ${t.muted}; flex: 0 0 auto; padding: 9px 12px; }
      .btn.ghost:hover { color: ${t.text}; background: ${t.alt}; }
      .btn[disabled] { opacity: .55; cursor: default; filter: none; }
      .status { margin-top: 9px; font-size: 11px; min-height: 14px; color: ${t.muted}; display: flex; align-items: center; gap: 6px; }
      .dot { width: 6px; height: 6px; border-radius: 50%; background: currentColor; flex: none; }
      .status.ok { color: ${t.success}; }
      .status.bad { color: ${t.danger}; }
    `;
  }

  /* ─────────────────────────── rendering ─────────────────────────── */

  function hide() {
    if (!active) return;
    clearTimeout(active.closeTimer);
    active.host.remove();
    const restore = active.restoreFocus;
    active = null;
    if (restore && typeof restore.focus === "function" && restore.isConnected) {
      try { restore.focus({ preventScroll: true }); } catch { /* focus is best effort */ }
    }
  }

  function show(details) {
    if (active) {
      if (details?.url && !queued.some(item => item.url === details.url)) queued.push(details);
      return;
    }
    render(details);
  }

  // Shows the next waiting offer, if any, once nothing is on screen.
  function showNext() {
    if (active || queued.length === 0) return;
    render(queued.shift());
  }

  function render(details) {
    hide();
    const theme = THEMES[details.theme] ? details.theme : "Midnight";
    const host = document.createElement("div");
    host.id = HOST_ID;
    host.style.cssText = "all: initial; position: fixed; inset: 0 auto auto 0; width: 0; height: 0; z-index: 2147483647;";
    const shadow = host.attachShadow({ mode: "closed" });

    const sheet = document.createElement("style");
    sheet.textContent = styles(theme);
    shadow.append(sheet);

    const card = document.createElement("div");
    card.className = "card";
    card.setAttribute("role", "dialog");
    card.setAttribute("aria-label", "Velocity download offer");

    const failed = !details.isDownload;
    const head = document.createElement("div");
    head.className = "head";
    const brand = document.createElement("div");
    brand.className = "brand";
    brand.textContent = "V";
    const titles = document.createElement("div");
    titles.className = "titles";
    const title = document.createElement("div");
    title.className = "title";
    title.textContent = failed ? "Velocity can't take this link" : "Download with Velocity";
    const subtitle = document.createElement("div");
    subtitle.className = "subtitle";
    subtitle.textContent = failed
      ? (details.site || "Unknown server")
      : details.unverified ? "Identified from the link" : "Verified by the server response";
    titles.append(title, subtitle);
    const close = document.createElement("button");
    close.className = "close";
    close.type = "button";
    close.setAttribute("aria-label", "Cancel — nothing will be downloaded");
    close.title = "Cancel — nothing will be downloaded";
    close.textContent = "✕";
    close.addEventListener("click", () => finish("cancel"));
    head.append(brand, titles, close);

    const name = document.createElement("div");
    name.className = "name";
    name.textContent = details.name || "Unknown file";
    name.title = details.name || "";

    const chips = document.createElement("div");
    chips.className = "chips";
    if (!failed) {
      const typeChip = document.createElement("span");
      typeChip.className = "chip";
      typeChip.textContent = typeLabel(details);
      chips.append(typeChip);
      const sizeChip = document.createElement("span");
      sizeChip.className = "chip size";
      sizeChip.textContent = formatBytes(details.size);
      chips.append(sizeChip);
      if (details.type) {
        const mimeChip = document.createElement("span");
        mimeChip.className = "chip size";
        mimeChip.textContent = details.type;
        chips.append(mimeChip);
      }
      if (details.resumable) {
        const resumeChip = document.createElement("span");
        resumeChip.className = "chip size";
        resumeChip.textContent = "Resumable";
        chips.append(resumeChip);
      }
    }

    card.append(head, name);
    if (!failed) card.append(chips);

    if (details.error) {
      const error = document.createElement("div");
      error.className = "error";
      error.textContent = details.error;
      card.append(error);
    } else {
      const destination = document.createElement("div");
      destination.className = "meta";
      const destinationLabel = document.createElement("span");
      destinationLabel.className = "label";
      destinationLabel.textContent = "Save to";
      const destinationValue = document.createElement("span");
      destinationValue.className = "value path";
      destinationValue.textContent = details.destination || "Velocity's default download folder";
      destinationValue.title = destinationValue.textContent;
      destination.append(destinationLabel, destinationValue);

      const source = document.createElement("div");
      source.className = "meta";
      const sourceLabel = document.createElement("span");
      sourceLabel.className = "label";
      sourceLabel.textContent = "From";
      const sourceValue = document.createElement("span");
      sourceValue.className = "value";
      sourceValue.textContent = details.site || "unknown";
      source.append(sourceLabel, sourceValue);

      const link = document.createElement("div");
      link.className = "meta";
      const linkLabel = document.createElement("span");
      linkLabel.className = "label";
      linkLabel.textContent = "Link";
      const linkValue = document.createElement("span");
      linkValue.className = "value";
      linkValue.textContent = details.url || "";
      linkValue.title = details.url || "";
      link.append(linkLabel, linkValue);

      card.append(destination, source, link);
    }

    const actions = document.createElement("div");
    actions.className = "actions";
    const status = document.createElement("div");
    status.className = "status";
    status.setAttribute("role", "status");
    status.setAttribute("aria-live", "polite");

    let primary;
    let later;
    let cancelButton;

    if (failed) {
      primary = document.createElement("button");
      primary.className = "btn secondary";
      primary.type = "button";
      primary.textContent = "Open in browser";
      primary.addEventListener("click", () => {
        if (details.url) window.location.assign(details.url);
        hide();
      });
      cancelButton = document.createElement("button");
      cancelButton.className = "btn ghost";
      cancelButton.type = "button";
      cancelButton.textContent = "Close";
      cancelButton.addEventListener("click", () => finish("cancel"));
      actions.append(primary, cancelButton);
    } else {
      primary = document.createElement("button");
      primary.className = "btn primary";
      primary.type = "button";
      primary.textContent = "Download";
      primary.addEventListener("click", () => finish("start"));

      later = document.createElement("button");
      later.className = "btn secondary";
      later.type = "button";
      later.textContent = "Download later";
      later.addEventListener("click", () => finish("later"));

      cancelButton = document.createElement("button");
      cancelButton.className = "btn ghost";
      cancelButton.type = "button";
      // An explicit choice, not a fallback: Chrome starts a fresh download for this file. Closing the offer (or
      // pressing Escape) is the cancel path, and it downloads nothing at all.
      cancelButton.textContent = "Keep in Chrome";
      cancelButton.title = "Let Chrome download this file instead";
      cancelButton.addEventListener("click", () => finish("browser"));

      actions.append(primary, later, cancelButton);
    }

    card.append(actions, status);
    // The wrapper covers the viewport without blocking it, so the card sits in the exact middle of the page (and
    // therefore of the screen) whatever the window size, zoom or zoomed-in layout is.
    const wrap = document.createElement("div");
    wrap.className = "wrap";
    wrap.append(card);
    shadow.append(wrap);
    (document.body || document.documentElement).append(host);

    const previousFocus = document.activeElement;
    active = { host, card, details, closeTimer: null, restoreFocus: previousFocus };
    try { primary.focus({ preventScroll: true }); } catch { /* focus is best effort */ }

    const onKey = event => {
      if (event.key === "Escape" && active?.host === host) {
        event.preventDefault();
        finish("cancel");
      }
    };
    shadow.addEventListener("keydown", onKey);
  }

  function setStatus(text, tone) {
    if (!active) return;
    const status = active.card.querySelector(".status");
    if (!status) return;
    status.className = `status${tone ? ` ${tone}` : ""}`;
    status.textContent = "";
    if (!text) return;
    const dot = document.createElement("span");
    dot.className = "dot";
    status.append(dot, document.createTextNode(text));
  }

  function lockButtons(locked) {
    if (!active) return;
    for (const button of active.card.querySelectorAll(".btn")) button.disabled = locked;
  }

  async function finish(action) {
    const current = active;
    if (!current) return;
    const { details } = current;

    if (action === "cancel") {
      // Chrome cancelled its own copy when this offer appeared, so there is nothing to hand back and nothing that
      // could resume later. The card is closed first: the worker answers this call by presenting the next queued
      // offer, and that one must not be wiped out by this card closing after it.
      hide();
      showNext();
      try { await api.runtime.sendMessage({ type: "velocity-action", action: "cancel", details }); } catch { /* the worker may be idle */ }
      return;
    }

    lockButtons(true);
    setStatus(action === "later" ? "Adding to the Velocity queue…" : action === "browser" ? "Opening in Chrome…" : "Sending to Velocity…");

    if (action === "browser") {
      let opened;
      try {
        opened = await ask({ type: "velocity-action", action: "browser", details }, 8000);
      } catch (error) {
        opened = { ok: false, error: String(error?.message || error) };
      }
      if (opened?.ok) {
        setStatus("Chrome is downloading this file.", "ok");
        current.closeTimer = setTimeout(() => { hide(); showNext(); }, 1600);
      } else {
        lockButtons(false);
        setStatus(opened?.error || "Chrome refused to start the download.", "bad");
      }
      return;
    }

    let response;
    try {
      response = await ask({ type: "velocity-action", action, details }, 10000);
    } catch (error) {
      response = { ok: false, error: error?.message === "timeout" ? "Velocity did not answer in time. Check the native host and try again." : String(error?.message || error) };
    }

    if (response?.ok) {
      const where = response.destination ? ` to ${response.destination}` : "";
      setStatus(action === "later" ? `Queued in Velocity${where}.` : `Added to Velocity${where}.`, "ok");
      current.closeTimer = setTimeout(() => { hide(); showNext(); }, 1800);
      return;
    }

    lockButtons(false);
    setStatus(response?.error || "Velocity did not accept the download.", "bad");
    if (response?.error) {
      const error = document.createElement("div");
      error.className = "error";
      error.textContent = response.error;
      const status = current.card.querySelector(".status");
      if (status) status.replaceWith(error);
    }
  }

  /* ─────────────────────────── link clicks ─────────────────────────── */

  function candidate(anchor) {
    if (!anchor || anchor.hasAttribute("data-velocity-skip")) return null;
    const href = anchor.getAttribute("href");
    if (!href || href.startsWith("#") || /^(javascript|mailto|tel|data|blob):/i.test(href)) return null;
    let url;
    try { url = new URL(anchor.href, location.href); } catch { return null; }
    if (!/^https?:$/.test(url.protocol)) return null;
    const looksLikeFile = DOWNLOAD_HINT.test(url.pathname + url.search);
    if (!anchor.hasAttribute("download") && !looksLikeFile) return null;
    return url;
  }

  document.addEventListener("click", event => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    const anchor = event.target?.closest?.("a[href]");
    const url = candidate(anchor);
    if (!url || probing) return;

    const opensNewTab = anchor.target && anchor.target !== "_self";
    event.preventDefault();
    event.stopPropagation();
    probing = true;

    const forced = anchor.hasAttribute("download");
    ask({ type: "velocity-probe", url: url.href, name: anchor.getAttribute("download") || "", force: forced }, 15000)
      .then(response => {
        probing = false;
        if (response?.bypass) {
          // Velocity is switched off for this site: behave exactly like a browser without the extension.
          if (opensNewTab) window.open(url.href, "_blank", "noopener");
          else window.location.assign(url.href);
          return;
        }
        if (response?.ok && response.details?.isDownload) {
          show(response.details);
          return;
        }
        if (response?.ok) {
          // A verified page, not a file: continue the navigation the browser was about to make.
          if (opensNewTab) window.open(url.href, "_blank", "noopener");
          else window.location.assign(url.href);
          return;
        }
        show({
          name: url.pathname.split("/").filter(Boolean).pop() || url.host,
          url: url.href,
          site: url.host,
          size: null,
          type: "",
          isDownload: false,
          error: response?.error || "The link could not be checked.",
          theme: "Midnight"
        });
      })
      .catch(error => {
        probing = false;
        // The extension never answered: hand the link straight back to the browser.
        if (error?.message === "timeout") {
          if (opensNewTab) window.open(url.href, "_blank", "noopener");
          else window.location.assign(url.href);
          return;
        }
        show({
          name: url.pathname.split("/").filter(Boolean).pop() || url.host,
          url: url.href,
          site: url.host,
          size: null,
          type: "",
          isDownload: false,
          error: `Velocity could not check this link (${error?.message || error}).`,
          theme: "Midnight"
        });
      });
  }, true);

  /* ─────────────────────────── restore / messages ─────────────────────────── */

  api.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type !== "velocity-popup") return;
    show(message.details);
    sendResponse({ shown: true });
    return;
  });

  // A page never asks for an offer it did not just receive. Velocity used to re-present an unanswered offer here on
  // every load, so simply refreshing a page — or restoring it when the browser started — popped the card up again
  // for a file the user had already seen. A download is offered once, by the worker, at the moment it is captured;
  // the page renders that message and nothing else automatically.

  // Escape closes the offer even after a click elsewhere on the page moved focus out of the card.
  globalThis.addEventListener("keydown", event => {
    if (event.key !== "Escape" || !active) return;
    event.preventDefault();
    void finish("cancel");
  }, true);

  // Leaving the page dismisses the offer: the browser copy is already gone, so nothing is left running.
  globalThis.addEventListener("pagehide", () => {
    queued.length = 0;
    if (active?.details) {
      try { api.runtime.sendMessage({ type: "velocity-action", action: "cancel", details: active.details }); } catch { /* the worker may be idle */ }
    }
    hide();
  });
})();
