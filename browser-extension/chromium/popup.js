/* Velocity — toolbar popup and standalone offer window. Only real responses are shown. */
const api = globalThis.browser ?? globalThis.chrome;
const params = new URLSearchParams(location.search);

const THEMES = {
  Midnight: { page: "#0B0E13", card: "#141823", alt: "#1A1F2B", border: "#232A37", text: "#F2F5FA", secondary: "#AFBACB", muted: "#7A8698", accent: "#5B8CFF", accent2: "#7C6BFF", success: "#34D399", danger: "#F87171", scheme: "dark" },
  Ember: { page: "#17110D", card: "#221A16", alt: "#2A201A", border: "#3A2C24", text: "#FFF4EA", secondary: "#E3CBB6", muted: "#B08F76", accent: "#E8792F", accent2: "#F2A03D", success: "#4CCB92", danger: "#F0746F", scheme: "dark" },
  Light: { page: "#EFF3FA", card: "#FFFFFF", alt: "#F7FAFE", border: "#E2E8F2", text: "#0F172A", secondary: "#44506A", muted: "#6C7890", accent: "#3B6FD4", accent2: "#6A7BF0", success: "#0F9D63", danger: "#C93A50", scheme: "light" }
};

const element = id => document.getElementById(id);
let details = null;
let host = { running: false, destination: "" };

function applyTheme(name) {
  const theme = THEMES[name] || THEMES.Midnight;
  const root = document.documentElement.style;
  root.setProperty("--page", theme.page);
  root.setProperty("--card", theme.card);
  root.setProperty("--alt", theme.alt);
  root.setProperty("--border", theme.border);
  root.setProperty("--text", theme.text);
  root.setProperty("--secondary", theme.secondary);
  root.setProperty("--muted", theme.muted);
  root.setProperty("--accent", theme.accent);
  root.setProperty("--accent2", theme.accent2);
  root.setProperty("--success", theme.success);
  root.setProperty("--danger", theme.danger);
  document.documentElement.style.colorScheme = theme.scheme;
  document.body.style.background = theme.page;
}

function send(message) {
  return Promise.resolve(api.runtime.sendMessage(message))
    .catch(error => ({ ok: false, error: String(error?.message || error) }));
}

function formatBytes(value) {
  if (value === null || value === undefined || Number.isNaN(Number(value))) return "Unknown size";
  let size = Number(value);
  const units = ["B", "KB", "MB", "GB", "TB"];
  let index = 0;
  while (size >= 1024 && index < units.length - 1) { size /= 1024; index++; }
  return `${size.toFixed(index === 0 || size >= 100 ? 0 : 1)} ${units[index]}`;
}

function typeLabel(value) {
  const extension = /\.([a-z0-9]{1,5})$/i.exec(String(value.name || ""))?.[1];
  if (extension) return extension.toUpperCase();
  const subtype = String(value.type || "").split("/")[1];
  return subtype ? subtype.split(";")[0].toUpperCase().slice(0, 6) : "FILE";
}

function node(tag, className, text) {
  const item = document.createElement(tag);
  if (className) item.className = className;
  if (text !== undefined) item.textContent = text;
  return item;
}

function metaRow(label, value, monospace) {
  const row = node("div", "meta");
  const key = node("span", "label", label);
  const val = node("span", monospace ? "value path" : "value", value);
  val.title = value;
  row.append(key, val);
  return row;
}

function render() {
  const body = element("body");
  const actions = element("actions");
  const footer = element("footer");
  body.textContent = "";
  actions.textContent = "";
  element("status").textContent = "";

  if (!details) return;

  const fatal = Boolean(details.error);
  const usable = !fatal;
  element("title").textContent = fatal ? "Velocity can't take this link" : "Download with Velocity";
  element("subtitle").textContent = fatal
    ? (details.site || "Unknown server")
    : details.isDownload ? "Verified by the server response" : "This does not look like a file";

  body.append(node("div", "name", details.name || "Unknown file"));

  const chips = node("div", "chips");
  if (usable && details.isDownload) {
    chips.append(node("span", "chip", typeLabel(details)), node("span", "chip size", formatBytes(details.size)));
    if (details.type) chips.append(node("span", "chip size", details.type));
    if (details.resumable) chips.append(node("span", "chip size", "Resumable"));
  }
  if (chips.childElementCount) body.append(chips);

  if (fatal) {
    body.append(node("div", "error", details.error));
  } else if (!details.isDownload) {
    body.append(node("div", "error", "The server answered without a file download. You can still send the link to Velocity and let the download engine try it."));
  } else {
    body.append(
      metaRow("Save to", details.destination || "Velocity's default download folder", true),
      metaRow("From", details.site || "unknown", false),
      metaRow("Link", details.url || "", false)
    );
  }

  if (!details.url) {
    footer.textContent = "Open a web page or right-click a file link to use Velocity.";
    return;
  }

  const primary = node("button", "btn primary", details.isDownload && usable ? "Download" : "Try anyway");
  primary.type = "button";
  primary.addEventListener("click", () => finish("start"));

  const later = node("button", "btn secondary", "Download later");
  later.type = "button";
  later.addEventListener("click", () => finish("later"));

  // Keeping the file in the browser starts a fresh download in Chrome. Closing this window is the cancel path: the
  // browser copy was already cancelled when the offer was made, so closing downloads nothing.
  const cancel = node("button", "btn ghost", "Keep in Chrome");
  cancel.title = "Let Chrome download this file instead";
  cancel.type = "button";
  cancel.addEventListener("click", () => finish("browser"));

  if (usable && details.isDownload) actions.append(primary, later, cancel);
  else if (usable) actions.append(primary, cancel);
  else actions.append(cancel);

  footer.textContent = host.error
    ? `Velocity app: ${host.error}`
    : host.running
      ? "Velocity is running and ready."
      : "Velocity is not running — it will start when you send this download.";
}

let answered = false;

async function finish(action) {
  if (!details) return;
  answered = true;
  const status = element("status");
  const buttons = [...document.querySelectorAll(".btn")];

  if (action === "cancel") {
    buttons.forEach(button => { button.disabled = true; });
    await send({ type: "velocity-action", action: "cancel", details });
    window.close();
    return;
  }

  buttons.forEach(button => { button.disabled = true; });
  status.className = "status";
  status.textContent = action === "later" ? "Adding to the Velocity queue…" : action === "browser" ? "Opening in Chrome…" : "Sending to Velocity…";

  const response = await send({ type: "velocity-action", action, details });
  if (response?.ok) {
    status.className = "status ok";
    status.replaceChildren(node("span", "dot"), document.createTextNode(
      action === "browser"
        ? "Chrome is downloading this file."
        : action === "later"
          ? "Queued in Velocity. You can close this window."
          : "Added to Velocity. You can close this window."));
    setTimeout(() => window.close(), 1600);
    return;
  }

  buttons.forEach(button => { button.disabled = false; });
  status.className = "status";
  status.replaceChildren();
  const problem = node("div", "error", response?.error || "Velocity did not accept the download.");
  document.querySelector(".card").append(problem);
}

function fromQuery() {
  const size = params.get("size");
  return {
    url: params.get("url") || "",
    name: params.get("name") || "",
    size: size && size !== "null" ? Number(size) : null,
    type: params.get("type") || "",
    site: params.get("site") || "",
    isDownload: params.get("isDownload") === "false" ? false : Boolean(params.get("isDownload")) || !params.get("error"),
    error: params.get("error") || "",
    destination: params.get("destination") || "",
    resumable: params.get("resumable") === "true",
    downloadId: params.get("downloadId") ? Number(params.get("downloadId")) : undefined,
    theme: params.get("theme") || "Midnight"
  };
}

async function boot() {
  if (params.get("url")) {
    details = fromQuery();
    applyTheme(details.theme);
    render();
    const status = await send({ type: "velocity-status" });
    if (status?.ok) {
      host = { running: Boolean(status.host?.running), destination: status.host?.destination || "", error: status.host?.error || null };
      if (details.destination) details.destination = details.destination || status.host.destination;
      render();
    }
    return;
  }

  const tabs = await api.tabs.query({ active: true, currentWindow: true }).catch(() => []);
  const tab = tabs?.[0];
  const status = await send({ type: "velocity-status" });
  if (status?.ok) host = { running: Boolean(status.host?.running), destination: status.host?.destination || "", error: status.host?.error || null };

  if (!tab?.url || !/^https?:/i.test(tab.url)) {
    applyTheme(params.get("theme") || status?.host?.theme || "Midnight");
    element("title").textContent = "Velocity";
    element("subtitle").textContent = "Nothing to download here";
    element("body").append(node("div", "error", "Open a normal web page, or right-click a file link and choose “Download with Velocity”."));
    element("footer").textContent = host.running ? "Velocity is running and ready." : "Velocity is not running — it will start when you send a download.";
    return;
  }

  const response = await send({ type: "velocity-inspect", url: tab.url, name: tab.title || "" });
  applyTheme(response?.details?.theme || status?.host?.theme || "Midnight");

  if (response?.bypass) {
    details = {
      url: tab.url,
      name: tab.title || "",
      site: new URL(tab.url).host,
      isDownload: false,
      error: "Interception is switched off for this site, so Chrome handles downloads normally.",
      theme: "Midnight"
    };
  } else if (!response?.ok) {
    details = { url: tab.url, name: tab.title || "", site: new URL(tab.url).host, isDownload: false, error: response?.error || "The link could not be checked.", theme: "Midnight" };
  } else {
    details = { ...response.details, destination: response.details.destination || host.destination };
  }
  render();
}

// Closing the offer window is the cancel path: the browser's copy was cancelled when the offer appeared, so an
// unanswered offer is simply released and nothing downloads.
window.addEventListener("pagehide", () => {
  if (details && !answered) {
    try { api.runtime.sendMessage({ type: "velocity-action", action: "cancel", details }); } catch { /* the worker may be idle */ }
  }
});

boot().catch(error => {
  document.getElementById("body").append(node("div", "error", `Velocity could not check this page: ${error?.message || error}`));
});
