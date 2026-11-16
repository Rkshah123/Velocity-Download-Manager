/* Velocity — extension options. Values are stored locally; the app's folder and theme come from the host. */
const api = globalThis.browser ?? globalThis.chrome;
const DEFAULTS = { intercept: true, ask: true, detect: true, excluded: [], theme: "auto" };

const THEMES = {
  Midnight: { page: "#0B0E13", card: "#141823", alt: "#1A1F2B", border: "#232A37", text: "#F2F5FA", secondary: "#AFBACB", muted: "#7A8698", accent: "#5B8CFF", accent2: "#7C6BFF", success: "#34D399", danger: "#F87171", warn: "#F5B84B" },
  Ember: { page: "#17110D", card: "#221A16", alt: "#2A201A", border: "#3A2C24", text: "#FFF4EA", secondary: "#E3CBB6", muted: "#B08F76", accent: "#E8792F", accent2: "#F2A03D", success: "#4CCB92", danger: "#F0746F", warn: "#F3B24A" },
  Light: { page: "#EFF3FA", card: "#FFFFFF", alt: "#F7FAFE", border: "#E2E8F2", text: "#0F172A", secondary: "#44506A", muted: "#6C7890", accent: "#3B6FD4", accent2: "#6A7BF0", success: "#0F9D63", danger: "#C93A50", warn: "#B47800" }
};

const send = message => Promise.resolve(api.runtime.sendMessage(message)).catch(() => ({ ok: false }));

function applyTheme(name) {
  const theme = THEMES[name] || THEMES.Midnight;
  const root = document.documentElement.style;
  for (const [key, value] of Object.entries(theme)) root.setProperty(`--${key}`, value);
  document.documentElement.style.colorScheme = name === "Light" ? "light" : "dark";
}

function setOptions(options) {
  document.getElementById("intercept").checked = options.intercept !== false;
  document.getElementById("ask").checked = options.ask !== false;
  document.getElementById("detect").checked = options.detect !== false;
  document.getElementById("theme").value = options.theme || "auto";
  document.getElementById("excluded").value = (options.excluded || []).join("\n");
}

async function loadStatus() {
  const dot = document.getElementById("hostDot");
  const state = document.getElementById("hostState");
  const detail = document.getElementById("hostDetail");
  state.textContent = "Checking the Velocity app…";
  dot.className = "dot";

  const response = await send({ type: "velocity-status" });
  const host = response?.host;
  if (!response?.ok || !host) {
    dot.className = "dot bad";
    state.textContent = "The extension background could not answer.";
    detail.textContent = "Reload the extension from the extensions page and try again.";
    return;
  }

  if (host.error) {
    dot.className = "dot bad";
    state.textContent = "Velocity helper not reachable";
    detail.textContent = host.error;
    return;
  }

  dot.className = host.running ? "dot ok" : "dot warn";
  state.textContent = host.running ? "Velocity is running" : "Velocity is installed but not running";
  detail.textContent = [
    host.destination ? `Save folder: ${host.destination}` : "Save folder: Velocity's default",
    host.version ? `Assisted by Velocity ${host.version}` : "",
    host.running ? "" : "It starts automatically when you send a download."
  ].filter(Boolean).join(" · ");
}

function normalizeExclusions(value) {
  return [...new Set(String(value)
    .split(/[\n,;]+/)
    .map(line => line.trim().toLowerCase().replace(/^https?:\/\//, "").replace(/^www\./, "").split("/")[0])
    .filter(Boolean))];
}

async function boot() {
  let options = DEFAULTS;
  try { options = { ...DEFAULTS, ...(await api.storage.local.get(DEFAULTS)) }; } catch { /* defaults are fine */ }

  const status = await send({ type: "velocity-status" });
  applyTheme(options.theme === "auto" ? (status?.host?.theme || "Midnight") : options.theme);
  setOptions(options);

  document.getElementById("theme").addEventListener("change", event => {
    const chosen = event.target.value;
    applyTheme(chosen === "auto" ? (status?.host?.theme || "Midnight") : chosen);
  });

  document.getElementById("refresh").addEventListener("click", loadStatus);
  document.getElementById("save").addEventListener("click", async () => {
    const next = {
      intercept: document.getElementById("intercept").checked,
      ask: document.getElementById("ask").checked,
      detect: document.getElementById("detect").checked,
      theme: document.getElementById("theme").value,
      excluded: normalizeExclusions(document.getElementById("excluded").value)
    };
    await api.storage.local.set(next);
    setOptions(next);
    applyTheme(next.theme === "auto" ? (status?.host?.theme || "Midnight") : next.theme);
    await send({ type: "velocity-options-changed" });
    const note = document.getElementById("note");
    note.textContent = "Saved.";
    setTimeout(() => { note.textContent = ""; }, 2500);
  });

  loadStatus();
}

boot();
