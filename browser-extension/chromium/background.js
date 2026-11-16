/*
 * Velocity browser integration — background service worker (Chromium MV3 / Firefox MV2).
 *
 * How a download is detected:
 *  1. A link click that looks like a file is verified with a real HTTP request first. The probe asks
 *     for one byte, so the file itself is never pulled through the browser.
 *  2. A download the browser starts on its own is cancelled the moment it exists — before any
 *     storage call is awaited — and only then offered to the user. Chrome keeps no copy, so there is
 *     nothing left to resume: cancelling the offer can never fall back to the browser's download manager.
 *  3. The decision comes from the server response — Content-Disposition, Content-Type, size and
 *     status — not from the file extension alone.
 */
const api = globalThis.browser ?? globalThis.chrome;
const isFirefox = typeof globalThis.browser !== "undefined";

const HOST = "com.velocity.download_manager";
const DEDUPE_MS = 8000;
const PENDING_KEY = "pending";
const ORPHANS_KEY = "orphanDownloads";

const DEFAULTS = { intercept: true, ask: true, detect: true, excluded: [], theme: "auto" };

// Content the browser renders in place. It is only taken over when the server marks it as an attachment
// (or the link carries a download attribute), so Velocity never breaks PDF viewing, video or audio playback.
const VIEWABLE_TYPES = /^(application\/pdf|image\/|audio\/|video\/|text\/|application\/(json|xml|xhtml\+xml|javascript))/i;
const DOWNLOAD_HINT = /\.(7z|aac|apk|avi|bin|bmp|csv|deb|dmg|docx?|epub|exe|flac|gif|gz|img|iso|jar|mpe?g|jpe?g|m4a|mkv|mov|mp3|mp4|msi|odt|ogg|pdf|png|pptx?|psd|rar|rpm|rtf|svg|tar|tgz|torrent|ts|ttf|txt|wav|webm|webp|xlsx?|zip)(?:$|[?#])/i;
// Used only when the response carries no metadata at all. Renderable types (PDF, images, audio, video)
// are deliberately excluded so the browser keeps its own viewer when nothing proves it is an attachment.
const UNVERIFIED_HINT = /\.(7z|apk|bin|csv|dat|deb|dmg|docx?|epub|exe|gz|img|iso|jar|msi|odt|pptx?|psd|rar|rpm|rtf|tar|tgz|torrent|ttf|xlsx?|zip)(?:$|[?#])/i;

// Automatic detection: what the response *is*, not what the URL ends with. Types the browser renders
// (HTML, JSON, XML, plain text without an attachment) are deliberately absent, so a page never looks
// like a file just because of its name.
const FILE_TYPES = {
  "application/zip": "archive",
  "application/x-zip-compressed": "archive",
  "application/x-rar-compressed": "archive",
  "application/vnd.rar": "archive",
  "application/x-7z-compressed": "archive",
  "application/x-tar": "archive",
  "application/gzip": "archive",
  "application/x-gzip": "archive",
  "application/x-bzip2": "archive",
  "application/pdf": "document",
  "application/msword": "document",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document": "document",
  "application/vnd.ms-excel": "document",
  "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": "document",
  "application/vnd.ms-powerpoint": "document",
  "application/vnd.openxmlformats-officedocument.presentationml.presentation": "document",
  "application/vnd.oasis.opendocument.text": "document",
  "application/vnd.oasis.opendocument.spreadsheet": "document",
  "application/vnd.oasis.opendocument.presentation": "document",
  "application/rtf": "document",
  "application/epub+zip": "document",
  "application/x-msdownload": "application",
  "application/vnd.microsoft.portable-executable": "application",
  "application/x-msi": "application",
  "application/vnd.android.package-archive": "application",
  "application/x-apple-diskimage": "application",
  "application/java-archive": "application",
  "application/octet-stream": "file"
};

const FILE_HINT = /\.(7z|aac|apk|avi|bin|bmp|csv|deb|dmg|docx?|epub|exe|flac|flv|gif|gz|img|iso|jar|jpe?g|m4a|m4v|mkv|mov|mp3|mp4|mpe?g|msi|odt|ogg|oga|opus|pdf|png|pptx?|psd|rar|rpm|rtf|svg|tar|tgz|torrent|ttf|txt|wav|webm|webp|wma|wmv|xlsx?|zip)(?:$|[?#])/i;
const MEDIA_HINT = /\.(aac|avi|flac|flv|m4a|m4v|mkv|mov|mp3|mp4|mpe?g|oga|ogg|opus|wav|webm|wma|wmv)(?:$|[?#])/i;
const IMAGE_HINT = /\.(avif|bmp|gif|ico|jpe?g|png|svg|webp)(?:$|[?#])/i;
const STREAM_HINT = /\.(m3u8|mpd|m4s|ts)(?:$|[?#])/i;

const action = api.action ?? api.browserAction;
const recent = new Map();
// The links that currently have an offer on screen. A second click on the same link is cancelled like every other
// browser download but never opens a second prompt.
const liveOffers = new Map();

// An offer that nobody answers (the tab was closed, the window was dismissed) stops holding its link after a
// while, so the same file can always be offered again.
const OFFER_TTL_MS = 10 * 60 * 1000;

function holdOffer(url) {
  if (url) liveOffers.set(url, Date.now());
}

function hasLiveOffer(url) {
  const at = liveOffers.get(url);
  if (!at) return false;
  if (Date.now() - at < OFFER_TTL_MS) return true;
  liveOffers.delete(url);
  return false;
}

function releaseOffer(url) {
  if (!url) return;
  liveOffers.delete(url);
  recent.delete(url);
}

/* ─────────────────────────── settings & host ─────────────────────────── */

async function getOptions() {
  try {
    return { ...DEFAULTS, ...(await api.storage.local.get(DEFAULTS)) };
  } catch {
    return { ...DEFAULTS };
  }
}

// The download listener has to decide before Chrome writes anything, and awaiting storage would hand the browser
// a head start. This cache is primed at start-up and refreshed whenever the options page saves.
let optionCache = { ...DEFAULTS };
function primeOptions() {
  getOptions().then(value => { optionCache = value; }, () => { /* keep the last known options */ });
}
primeOptions();
api.storage?.onChanged?.addListener((changes, area) => {
  if (area === "local" && Object.keys(changes || {}).some(key => key in DEFAULTS)) primeOptions();
});

let hostCache = { at: 0, value: null };
const HOST_CACHE_KEY = "hostSettings";

function withTimeout(promise, milliseconds, fallback) {
  return Promise.race([
    promise,
    new Promise(resolve => setTimeout(() => resolve(fallback), milliseconds))
  ]);
}

async function getHost(force = false) {
  if (!force && hostCache.value && Date.now() - hostCache.at < 4000) return hostCache.value;

  // The last known settings are enough to draw the popup instantly; the helper is refreshed in the
  // background, so a cold native host never delays the offer.
  if (!force) {
    const stored = await readHostCache();
    if (stored) {
      hostCache = { at: Date.now(), value: stored };
      void refreshHost();
      return stored;
    }
  }

  return await refreshHost();
}

async function readHostCache() {
  try {
    const stored = await api.storage.local.get(HOST_CACHE_KEY);
    return stored?.[HOST_CACHE_KEY] ?? null;
  } catch {
    return null;
  }
}

async function refreshHost() {
  // A stuck helper must never hold a link click hostage.
  const response = await withTimeout(sendNative({ type: "settings" }), 2500, { ok: false, error: "The Velocity helper did not answer in time." });
  const value = response?.ok && response.settings
    ? response.settings
    : { destination: "", theme: "Midnight", running: false, appPath: "", version: "", error: response?.error || null };
  hostCache = { at: Date.now(), value };
  if (!value.error) {
    try { await api.storage.local.set({ [HOST_CACHE_KEY]: value }); } catch { /* caching is best effort */ }
  }
  return value;
}

function sendNative(payload) {
  const call = api.runtime.sendNativeMessage.bind(api.runtime);
  if (isFirefox) {
    return call(HOST, payload).catch(error => ({ ok: false, error: nativeError(error) }));
  }
  return new Promise(resolve => {
    try {
      call(HOST, payload, response => {
        const error = api.runtime.lastError;
        resolve(error ? { ok: false, error: nativeError(error) } : response ?? { ok: false, error: "The native host returned an empty response." });
      });
    } catch (error) {
      resolve({ ok: false, error: nativeError(error) });
    }
  });
}

function nativeError(error) {
  const message = String(error?.message || error);
  if (/not found|forbidden|Access to the specified native messaging host/i.test(message))
    return "The Velocity native host is not installed. Run browser-extension/register-native-host.ps1 once, then reload the extension.";
  if (/exited|disconnected|pipe/i.test(message))
    return "The Velocity helper stopped unexpectedly. Reinstall the native host with register-native-host.ps1.";
  return message;
}

/* ─────────────────────────── chrome.downloads helpers ─────────────────────────── */

function downloadsCall(method, ...args) {
  if (isFirefox) {
    return api.downloads[method](...args).then(() => true, () => false);
  }
  return new Promise(resolve => {
    try {
      api.downloads[method](...args, () => resolve(!api.runtime.lastError));
    } catch {
      resolve(false);
    }
  });
}

function searchDownloads(query) {
  if (isFirefox) return api.downloads.search(query).catch(() => []);
  return new Promise(resolve => {
    try {
      api.downloads.search(query, items => resolve(items || []));
    } catch {
      resolve([]);
    }
  });
}

/* ─────────────────────────── probing ─────────────────────────── */

function filenameFromDisposition(value) {
  if (!value) return "";
  const encoded = /filename\*\s*=\s*([^']*)'[^']*'([^;]+)/i.exec(value);
  if (encoded) {
    try {
      return decodeURIComponent(encoded[2].trim().replace(/^"|"$/g, ""));
    } catch { /* fall back to the plain form */ }
  }
  const plain = /filename\s*=\s*("([^"]*)"|[^;]+)/i.exec(value);
  if (plain) return (plain[2] ?? plain[1] ?? "").trim().replace(/^"|"$/g, "");
  return "";
}

function filenameFromUrl(value) {
  try {
    const url = new URL(value);
    const last = url.pathname.split("/").filter(Boolean).pop();
    const named = url.searchParams.get("filename") || url.searchParams.get("file") || url.searchParams.get("name");
    // A path segment with a real extension is the file name. A bare endpoint ("…/report?id=77") is not, and
    // a signed URL normally carries the intended name in its query, so that name wins there.
    if (last && /\.[a-z0-9]{2,5}$/i.test(last)) return decodeURIComponent(last);
    if (named) return decodeURIComponent(named);
    if (last) return decodeURIComponent(last);
  } catch { /* fall through */ }
  return `download-${Date.now()}`;
}

function httpError(status, statusText) {
  const suffix = statusText ? ` ${statusText}` : "";
  if (status === 401) return `The server requires you to sign in (HTTP 401${suffix}).`;
  if (status === 403) return `The server refused the download (HTTP 403${suffix}). The link may be private, expired or blocked.`;
  if (status === 404) return "The file was not found (HTTP 404). The link may be wrong or the file was removed.";
  if (status === 410) return "This download link has expired (HTTP 410). Reload the page and try again.";
  if (status === 429) return "The server is limiting requests (HTTP 429). Wait a moment and try again.";
  if (status >= 500) return `The server failed to deliver the file (HTTP ${status}${suffix}).`;
  return `The server answered HTTP ${status}${suffix}.`;
}

async function probe(url, suggestedName, force = false, allowGet = true) {
  const target = String(url || "");
  if (!/^https?:/i.test(target)) {
    return {
      ok: false,
      error: /^blob:/i.test(target)
        ? "This link is generated inside the page (blob: URL), so Velocity cannot fetch it. Let the browser save it instead."
        : "Only http and https links can be downloaded."
    };
  }

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 20000);

  // HEAD first: it returns the same headers without asking the browser to hand the response to its
  // download stack (a GET of such a file arrives as an empty 204 with no metadata at all). GET stays as
  // the fallback for servers that reject HEAD.
  let info = null;
  let lastError = null;
  try {
    info = await readHeaders(target, "HEAD", controller.signal);
  } catch (error) {
    lastError = error;
  }

  if (allowGet && (!info || weakHeaders(info))) {
    try {
      const fallback = await readHeaders(target, "GET", controller.signal);
      if (fallback && (fallback.headers.get("content-type") || fallback.headers.get("content-length") || fallback.headers.get("content-disposition"))) info = fallback;
    } catch (error) {
      lastError = lastError || error;
    }
  }

  clearTimeout(timer);

  if (!info) {
    const aborted = lastError?.name === "AbortError";
    return { ok: false, error: aborted ? "The server did not respond within 20 seconds." : `The server could not be reached (${lastError?.message || "network error"}).` };
  }

  const headers = info.headers;
  const disposition = headers.get("content-disposition") || "";
  const type = (headers.get("content-type") || "").split(";")[0].trim().toLowerCase();
  const contentRange = headers.get("content-range") || "";
  const totalFromRange = /bytes\s+\d+-\d+\/(\d+)/i.exec(contentRange);
  let size = totalFromRange ? Number(totalFromRange[1]) : null;
  if (size === null) {
    const length = (headers.get("content-length") || "").trim();
    if (/^\d+$/.test(length)) size = Number(length);
  }

  const finalUrl = info.finalUrl;

  // Some responses reach the page with every header stripped (204 with no metadata). The link itself is
  // then the only evidence: offer it, and let the download engine do the real check.
  if (info.status === 204 && !disposition && !type && !headers.get("content-length")) {
    let hinted = false;
    try { hinted = UNVERIFIED_HINT.test(new URL(target).pathname + new URL(target).search); } catch { /* keep false */ }
    return {
      ok: true,
      isDownload: force || hinted,
      name: suggestedName || filenameFromUrl(target),
      size: null,
      type: "",
      site: safeHost(target),
      url: target,
      requestedUrl: target,
      resumable: false,
      fromDisposition: false,
      unverified: true
    };
  }
  if (!info.ok) {
    return { ok: false, status: info.status, error: httpError(info.status, info.statusText), url: finalUrl, site: safeHost(finalUrl) };
  }

  const dispositionName = filenameFromDisposition(disposition);
  const name = suggestedName || dispositionName || filenameFromUrl(finalUrl);
  const attachment = /attachment/i.test(disposition);
  let looksLikeFile = false;
  try {
    const parsed = new URL(finalUrl);
    looksLikeFile = DOWNLOAD_HINT.test(parsed.pathname + parsed.search) || DOWNLOAD_HINT.test(target);
  } catch { /* keep false */ }
  const viewable = Boolean(type) && VIEWABLE_TYPES.test(type);
  const typeIsBinary = Boolean(type) && !viewable;
  // A renderable file is only treated as a download when the server or the page says so; otherwise Chrome
  // keeps its own viewer/player and Velocity stays out of the way.
  const isDownload = attachment || force || typeIsBinary || (looksLikeFile && !viewable);

  return {
    ok: true,
    isDownload,
    name,
    size: Number.isFinite(size) && size >= 0 ? size : null,
    type: type || "",
    site: safeHost(finalUrl),
    url: finalUrl,
    requestedUrl: target,
    resumable: /bytes/i.test(headers.get("accept-ranges") || ""),
    fromDisposition: dispositionName !== ""
  };
}

async function readHeaders(target, method, signal) {
  const response = await fetch(target, {
    method,
    headers: method === "GET" ? { Range: "bytes=0-0" } : undefined,
    redirect: "follow",
    credentials: "include",
    cache: "no-store",
    signal
  });

  const info = {
    status: response.status,
    statusText: response.statusText,
    ok: response.ok,
    headers: response.headers,
    finalUrl: response.url || target
  };

  // Release the connection immediately: only the headers were needed.
  if (method === "GET") { try { await response.body?.cancel(); } catch { /* already closed */ } }
  return info;
}

function weakHeaders(info) {
  if (info.status === 204 || info.status === 405 || info.status === 501) return true;
  const headers = info.headers;
  return !headers.get("content-type") && !headers.get("content-length") && !headers.get("content-disposition");
}

function safeHost(value) {
  try { return new URL(value).host; } catch { return ""; }
}

function extensionOf(value) {
  const match = /\.([a-z0-9]{1,5})(?:$|[?#])/i.exec(String(value || ""));
  return match ? match[1].toLowerCase() : "";
}

// Turns one verified response into a decision about the file behind it. Extensions are supporting
// evidence only: the Content-Disposition and the Content-Type decide, and a type the browser renders
// needs an explicit attachment before it counts as a download.
function classifyFile(info, detection) {
  const type = String(info.type || "").split(";")[0].trim().toLowerCase();
  const name = info.name || filenameFromUrl(info.url || "");
  const evidence = `${name} ${info.url || ""}`;
  const attachment = Boolean(info.fromDisposition);

  const known = FILE_TYPES[type];
  if (known) return { kind: known, extension: extensionOf(name) };
  if (/^(video|audio)\//.test(type))
    return detection === "media" || MEDIA_HINT.test(evidence) ? { kind: "media", extension: extensionOf(name) } : null;
  if (type.startsWith("image/"))
    return IMAGE_HINT.test(evidence) ? { kind: "image", extension: extensionOf(name) } : null;
  if (type.startsWith("text/"))
    return attachment ? { kind: "document", extension: extensionOf(name) } : null;
  if (!type) {
    if (!attachment && !FILE_HINT.test(evidence)) return null;
    return Number(info.size) > 0 ? { kind: "file", extension: extensionOf(name) } : null;
  }
  // Any other resource the server explicitly marks as an attachment is a download, whatever its type is.
  // Pages are excluded because browsers send "attachment" for "save this page" too.
  if (attachment && !/^(text\/html|application\/(xhtml\+xml|json|xml))/i.test(type))
    return { kind: "file", extension: extensionOf(name) };
  return null;
}

/* ─────────────────────────── automatic detection ─────────────────────────── */

const DETECT_TTL_MS = 10 * 60 * 1000;
const DETECT_CACHE_LIMIT = 300;
const detectCache = new Map();

// The same check the offer uses, but the answer is "is this a file?" rather than "should Velocity take
// this click?". A page that renders a PDF still has a downloadable PDF behind it.
async function detectFile(url, detection, headOnly = false) {
  const cacheKey = `${detection}${headOnly ? ":head" : ""}:${url}`;
  const cached = detectCache.get(cacheKey);
  if (cached && Date.now() - cached.at < DETECT_TTL_MS) return cached.value;
  if (detectCache.size >= DETECT_CACHE_LIMIT) detectCache.delete(detectCache.keys().next().value);

  const info = await probe(url, "", detection === "media", !headOnly);
  const value = describeFile(info, url, detection);
  detectCache.set(cacheKey, { at: Date.now(), value });
  return value;
}

function describeFile(info, requestedUrl, detection) {
  if (!info.ok) return { found: false, error: info.error || "", url: requestedUrl };
  const file = classifyFile(info, detection);
  if (!file) return { found: false, url: requestedUrl, type: info.type || "" };

  return {
    found: true,
    details: {
      url: info.url || requestedUrl,
      requestedUrl,
      name: info.name || filenameFromUrl(info.url || requestedUrl),
      size: info.size,
      type: info.type || "",
      site: info.site || safeHost(info.url || requestedUrl),
      resumable: Boolean(info.resumable),
      fromDisposition: Boolean(info.fromDisposition),
      kind: file.kind,
      extension: file.extension || "",
      detection
    }
  };
}

function headerValue(headers, name) {
  const found = (headers || []).find(header => String(header.name).toLowerCase() === name);
  return found ? String(found.value ?? "") : "";
}

// Passive network observation. It never blocks or alters a request; it only notices a response that is
// a file the page fetched itself (a script-built link, a token URL, an XHR "prepare download" call) and
// tells the page so the floater can offer it.
const NETWORK_TYPES = ["xmlhttprequest", "media", "object", "other"];
const NETWORK_MIN_BYTES = 64 * 1024;
const NETWORK_FILE_MIME = /^application\/(zip|x-zip-compressed|x-rar-compressed|vnd\.rar|x-7z-compressed|x-tar|gzip|x-gzip|x-bzip2|pdf|msword|vnd\.ms-excel|vnd\.ms-powerpoint|vnd\.openxmlformats-officedocument\.[a-z.]+|vnd\.oasis\.opendocument\.[a-z.]+|rtf|epub\+zip|x-msdownload|x-msi|vnd\.microsoft\.portable-executable|vnd\.android\.package-archive|x-apple-diskimage|java-archive)/i;
const NETWORK_DEDUPE_MS = 60 * 1000;
const networkSeen = new Map();
// The last manifest each tab loaded. A page can fetch its playlist before the content script has finished
// starting, and that message would otherwise be lost — the strip would never appear for that player.
const tabStreams = new Map();

if (api.webRequest?.onHeadersReceived) {
  api.webRequest.onHeadersReceived.addListener(
    details => { void inspectNetworkResponse(details).catch(() => { /* observation only */ }); },
    { urls: ["http://*/*", "https://*/*"], types: NETWORK_TYPES },
    ["responseHeaders"]);
}

// A reloaded page requests the same manifest again with a fresh signature. The dedupe window belongs to one
// navigation, so it is cleared whenever the tab starts loading a new document.
if (api.tabs?.onUpdated) {
  api.tabs.onUpdated.addListener((tabId, info) => {
    if (!info) return;
    if (info.status !== "loading" && !info.url) return;
    for (const key of [...networkSeen.keys()]) if (key.startsWith(`${tabId}|`)) networkSeen.delete(key);
    if (info.status === "loading") tabStreams.delete(tabId);
  });
}

async function inspectNetworkResponse(details) {
  // A probe made by this worker carries no tab; only page traffic is interesting.
  if (!Number.isInteger(details.tabId) || details.tabId < 0) return;
  // 206 means ranges and chunked players: that is traffic, not a finished file, but it still reveals the
  // real media type and size of what the player is loading.
  const partial = details.statusCode === 206;
  if (!partial && details.statusCode !== 200) return;
  const url = details.url;
  if (!/^https?:/i.test(url)) return;

  const key = `${details.tabId}|${url}`;
  const previous = networkSeen.get(key);
  if (previous && Date.now() - previous < NETWORK_DEDUPE_MS) return;

  const options = await getOptions();
  if (!options.detect || isExcluded(url, options.excluded)) return;

  const headers = details.responseHeaders || [];
  const disposition = headerValue(headers, "content-disposition");
  const type = headerValue(headers, "content-type").split(";")[0].trim().toLowerCase();
  const declared = headerValue(headers, "content-length");
  const length = declared !== "" && Number.isFinite(Number(declared)) ? Number(declared) : null;

  const attachment = /attachment/i.test(disposition);

  networkSeen.set(key, Date.now());
  if (networkSeen.size > 500) networkSeen.delete(networkSeen.keys().next().value);

  // The response knows the real media type and size even when the player hides them, so a strip that could
  // not get a size from its first probe can still show it: the properties are passed on as they appear.
  if (/^(video|audio)\//.test(type)) {
    const item = {
      url,
      name: filenameFromDisposition(disposition) || "",
      size: length,
      type
    };
    try { await api.tabs.sendMessage(details.tabId, { type: "velocity-properties", item }); } catch { /* no detector */ }
  }

  // A manifest is the whole video; its segments are only traffic. It is offered as one object, never as a fragment.
  if (MANIFEST_HINT.test(url)) {
    const item = { url, name: filenameFromUrl(url), size: null, type, site: safeHost(url), kind: "stream", resumable: false };
    tabStreams.set(details.tabId, item);
    if (tabStreams.size > 60) tabStreams.delete(tabStreams.keys().next().value);
    try { await api.tabs.sendMessage(details.tabId, { type: "velocity-detected", item }); } catch { /* no detector */ }
    return;
  }

  if (partial) return;
  // Inline media and images are left to the page detector, so a player never produces two badges.
  if (!attachment && !NETWORK_FILE_MIME.test(type)) return;
  // An unambiguous file type may arrive chunked (no declared length); anything else has to prove it has a
  // body worth offering, so a stream or a stub response never becomes a download button.
  if (!attachment && length === null) return;
  if (!attachment && length < NETWORK_MIN_BYTES) return;
  const size = length === null ? null : length;

  if (STREAM_HINT.test(new URL(url).pathname)) return;

  const item = {
    url,
    name: filenameFromDisposition(disposition) || filenameFromUrl(url),
    size,
    type,
    site: safeHost(url),
    resumable: /bytes/i.test(headerValue(headers, "accept-ranges")),
    fromDisposition: attachment,
    kind: "network"
  };

  try { await api.tabs.sendMessage(details.tabId, { type: "velocity-detected", item }); } catch { /* tab has no detector */ }
}

function isExcluded(url, excluded) {
  const host = safeHost(url);
  return (excluded || []).some(entry => {
    const value = String(entry || "").trim().replace(/^www\./i, "").toLowerCase();
    if (!value) return false;
    const bare = host.toLowerCase();
    return bare === value || bare.endsWith(`.${value}`);
  });
}

/* ─────────────────────────── presenting an offer ─────────────────────────── */

async function present(tabId, details) {
  if (typeof tabId === "number" && tabId >= 0) {
    try {
      const reply = await api.tabs.sendMessage(tabId, { type: "velocity-popup", details });
      if (reply?.shown) return true;
    } catch { /* no injected page in that tab: use the standalone window */ }
  }
  await openFallbackWindow(details);
  return false;
}

async function openFallbackWindow(details) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(details)) {
    if (value !== null && value !== undefined) params.set(key, String(value));
  }
  await createCenteredPopup(api.runtime.getURL(`popup.html?${params}`), 470, 670);
}

// An offer is a dialog, so it is placed in the exact middle of the window the user is working in — and therefore of
// that screen, whatever its resolution or scaling. The position is clamped to the window so a small or moved window
// still produces a visible, centered dialog instead of one half outside the screen.
async function createCenteredPopup(url, width, height) {
  let left;
  let top;
  try {
    const current = await api.windows.getLastFocused({ windowTypes: ["normal"] });
    if (current && Number.isFinite(current.left) && Number.isFinite(current.top) && current.width > 0 && current.height > 0) {
      left = Math.round(current.left + (current.width - width) / 2);
      top = Math.round(current.top + (current.height - height) / 2);
    }
  } catch { /* no window to center against: let the browser decide */ }

  const options = { url, type: "popup", width, height, focused: true };
  if (Number.isFinite(left) && Number.isFinite(top)) {
    options.left = Math.max(0, left);
    options.top = Math.max(0, top);
  }
  await api.windows.create(options);
}

/* ─────────────────────────── pending offers ─────────────────────────── */

async function readPending() {
  try {
    const stored = await api.storage.session.get(PENDING_KEY);
    return stored?.[PENDING_KEY] ?? {};
  } catch {
    return {};
  }
}

async function writePending(map) {
  try { await api.storage.session.set({ [PENDING_KEY]: map }); } catch { /* best effort */ }
}

// One prompt at a time, everywhere. Two downloads that arrive together must not fight over a single card: the
// second is held here and shown when the first has been answered, because its browser copy is already gone.
let offerActive = null;
const offerQueue = [];
let offerExpiry = null;

// Counters for the options page and for support: how many downloads were captured, offered and handed over. They
// make a lost download visible instead of silent.
const stats = { captured: 0, enqueued: 0, offered: 0, delivered: 0, browserKept: 0, offerFailures: 0, droppedByDecision: 0 };

async function enqueueOffer(tabId, details) {
  offerQueue.push({ tabId, details });
  stats.enqueued++;
  await pumpOffers();
}

async function pumpOffers() {
  if (offerActive || offerQueue.length === 0) return;
  const next = offerQueue.shift();
  offerActive = next;
  stats.offered++;
  await rememberPending(next.tabId, next.details);
  clearTimeout(offerExpiry);
  offerExpiry = setTimeout(() => { void finishOffer(next.details); }, OFFER_TTL_MS);
  try {
    await present(next.tabId, next.details);
  } catch (error) {
    // A prompt that cannot be shown must not leave the queue blocked: the download is still offered again below
    // when the page reports it, and this one is finished so the next offer can appear.
    stats.offerFailures++;
    console.warn("Velocity could not show an offer", error);
    void finishOffer(next.details);
  }
}

// Ends one offer, however it ended, and moves the queue on. Called for a decision, a dismissal, a closed tab or an
// expired prompt, so a prompt can never be replaced while it is still unanswered.
//
// A download that is only *waiting* in the queue is never removed by a page-side event: a page that closes or goes
// away says nothing about a download it was never shown, and dropping it there is what made several simultaneous
// downloads quietly disappear. Only an explicit decision (`drop`) takes a waiting download out of the queue.
async function finishOffer(details, options = {}) {
  const url = details?.url;
  if (offerActive && (details === undefined || offerActive.details === details || offerActive.details.url === url)) {
    const current = offerActive;
    offerActive = null;
    clearTimeout(offerExpiry);
    offerExpiry = null;
    releaseOffer(current.details.url);
    await forgetPending(current.tabId);
    void pumpOffers();
    return;
  }

  if (!options.drop) return;

  releaseOffer(url);
  const queued = url ? offerQueue.findIndex(entry => entry.details === details || entry.details.url === url) : -1;
  if (queued >= 0)
  {
    offerQueue.splice(queued, 1);
    stats.droppedByDecision++;
  }
}

async function rememberPending(tabId, details) {
  if (typeof tabId !== "number" || tabId < 0) return;
  const map = await readPending();
  map[tabId] = details;
  await writePending(map);
}

async function forgetPending(tabId) {
  if (typeof tabId !== "number" || tabId < 0) return;
  const map = await readPending();
  delete map[tabId];
  await writePending(map);
}

async function removeOrphan(id) {
  try {
    const stored = await api.storage.local.get({ [ORPHANS_KEY]: [] });
    await api.storage.local.set({ [ORPHANS_KEY]: stored[ORPHANS_KEY].filter(value => value !== id) });
  } catch { /* best effort */ }
}

/* ─────────────────────────── actions ─────────────────────────── */

// Chrome starts writing the moment a download exists, so the browser's copy is cancelled and its list entry erased
// right away. Nothing is paused and nothing is kept for later: an erased download cannot be resumed, finished or
// re-offered by the browser's download manager.
//
// The browser's download item is still being created while onCreated is dispatched. Cancelling and erasing it from
// inside that notification touches the item while Chrome is walking its own download list, which is what made the
// browser fall over on some files. The same work is therefore done on a later task, and the entry is only erased
// once Chrome itself reports it as no longer in progress, so the browser's copy still goes away immediately but the
// browser is never asked to touch an item it is still creating.
// `chrome.downloads.erase` is deliberately NOT used. Erasing an entry destroys Chrome's own DownloadItem while the
// download UI (the bubble in current Chrome) still holds a pointer to it, and Chrome 157 writes through that dead
// pointer at a fixed instruction — a null write that takes the whole browser down. It happened on the user's machine
// after two or three attempts on the same link, and five crash dumps show the same chrome.dll+0x4bf8da9 address.
// Cancelling is enough for Velocity's guarantee: a cancelled download can never be resumed, finished or saved by the
// browser, and its bytes stop arriving exactly as before. Its list entry simply stays as "interrupted" instead of
// being deleted behind the browser's back.
async function cancelBrowserDownload(id) {
  // Issued in the same task as the notification, so the browser's copy stops as early as it always did.
  await downloadsCall("cancel", id);

  // Wait until Chrome itself agrees the item is no longer transferring. Nothing is deleted afterwards.
  for (let attempt = 0; attempt < 20; attempt++) {
    const item = await findDownload(id);
    if (!item || item.state === "interrupted" || item.state === "complete") break;
    await new Promise(resolve => setTimeout(resolve, 50));
  }

  await removeOrphan(id);
}

// Reads one download entry. An entry Chrome has already forgotten is reported as null rather than as an error.
async function findDownload(id) {
  if (isFirefox) {
    try {
      const items = await api.downloads.search({ id });
      return items[0] ?? null;
    } catch {
      return null;
    }
  }
  return new Promise(resolve => {
    try {
      api.downloads.search({ id }, items => {
        if (api.runtime.lastError) resolve(null);
        else resolve(items && items[0] ? items[0] : null);
      });
    } catch {
      resolve(null);
    }
  });
}

// "Keep in Chrome" and a failed hand-off both need a real browser download, so one is started on purpose. It is a
// new request with its own entry, never a resumed download.
function startBrowserDownload(url, name) {
  const options = { url, saveAs: false };
  if (name && /^[^\\/:*?"<>|]{1,180}$/.test(name)) options.filename = name;
  return downloadsCall("download", options);
}

async function handleAction(request) {
  const details = request.details || {};
  const decision = ["start", "later", "browser", "cancel"].includes(request.action) ? request.action : "start";

  if (decision === "cancel") {
    // The browser's copy was cancelled when the offer was made, so dismissing the offer simply ends the request.
    // Chrome is never asked to start, resume or keep it.
    await finishOffer(details, { drop: true });
    return { ok: true, cancelled: true };
  }

  if (!details.url) return { ok: false, error: "This offer has no download link." };

  // The user chose the browser: one deliberate, brand new download in Chrome.
  if (decision === "browser") {
    await finishOffer(details, { drop: true });
    const started = await startBrowserDownload(details.url, details.name);
    return started ? { ok: true, browser: true } : { ok: false, error: "Chrome refused to start that download." };
  }

  const host = await withTimeout(sendNative({
    type: "download",
    url: details.url,
    filename: details.name || null,
    action: decision,
    // Sent by Velocity as the Referer header. Many video hosts refuse a request that arrives without it,
    // and it is the page's own address rather than anything guessed.
    referer: details.pageUrl || request.pageUrl || null
  }), 8000, { ok: false, error: "The Velocity helper did not answer in time. Check that the native host is registered." });

  if (!host?.ok) {
    // Velocity could not take it. Nothing is left in the browser, so the file is started there deliberately rather
    // than silently lost; that is a fresh request, never a resumed download.
    await finishOffer(details, { drop: true });
    const restored = await startBrowserDownload(details.url, details.name);
    return {
      ok: false,
      error: `${host?.error || "Velocity did not accept the download."}${restored ? " Chrome is downloading it instead." : ""}`
    };
  }

  await finishOffer(details);
  hostCache = { at: 0, value: null };
  return { ok: true, launched: Boolean(host.launched), destination: host.destination || "" };
}

/* ─────────────────────────── browser-initiated downloads ─────────────────────────── */

api.downloads.onCreated.addListener(item => {
  // Nothing inside the interception may escape as an unhandled rejection in the worker: a captured download that
  // cannot be offered is counted and reported, never thrown into the browser's event loop.
  takeOver(item).catch(error => {
    stats.offerFailures++;
    console.warn("Velocity could not offer a download", error);
  });
});

// Every download Chrome starts on its own is captured here. The browser's copy is cancelled first and probed
// afterwards: a probe costs a round trip, and every millisecond before the cancel is a millisecond of the file the
// browser would otherwise have saved behind Velocity's back.
async function takeOver(item) {
  const url = item.finalUrl || item.url || "";
  // A download this extension started itself ("Keep in Chrome") is already the user's choice: leave it alone.
  if (item.byExtensionId || !/^https?:/i.test(url)) return;

  // The listener cannot await storage, so it reads the cache that the options page keeps in sync.
  if (!optionCache.intercept || isExcluded(url, optionCache.excluded)) return;

  const duplicate = hasLiveOffer(url) || (recent.has(url) && Date.now() - recent.get(url) < DEDUPE_MS);
  await cancelBrowserDownload(item.id);
  if (duplicate) return;
  stats.captured++;

  recent.set(url, Date.now());
  holdOffer(url);

  const options = optionCache;
  // HEAD only: the body of the file is never pulled through the browser as part of an offer. A probe that fails
  // outright still describes a download Chrome had already accepted, so the offer is never dropped: it says what
  // it knows and hands over the same link.
  let result;
  try {
    result = await probe(url, item.filename || "", true, false);
  } catch {
    result = { ok: false };
  }
  const tabId = Number.isInteger(item.tabId) && item.tabId >= 0 ? item.tabId : await activeTabId();
  const host = await getHost().catch(() => ({}));

  // The browser had already accepted this as a download, so a server that refuses the worker's HEAD request still
  // describes a real file. The offer then says what it knows instead of throwing the download away.
  const offered = result.ok
    ? result
    : {
        ok: true,
        isDownload: true,
        unverified: true,
        name: item.filename || filenameFromUrl(url),
        size: null,
        type: "",
        site: safeHost(url),
        url,
        requestedUrl: url,
        resumable: false,
        fromDisposition: false
      };

  const details = {
    ...offered,
    // Chrome knows the name, type and size of what it was about to save, even when HEAD was refused.
    name: offered.name || item.filename || filenameFromUrl(url),
    size: offered.size ?? (Number.isFinite(item.fileSize) && item.fileSize > 0 ? item.fileSize : null),
    type: offered.type || item.mime || "",
    pageUrl: item.referrer || "",
    destination: host.destination || "Velocity's default download folder",
    theme: resolveTheme(options.theme, host.theme),
    takenOver: true
  };
  if (!options.ask) {
    await rememberPending(tabId, details);
    const outcome = await handleAction({ action: "start", details, tabId });
    flashBadge(outcome.ok ? "✓" : "!");
    return;
  }
  await enqueueOffer(tabId, details);
}

// The page a download belongs to. Taking the download's own tab when Chrome reports one; otherwise the focused
// *web* tab — never an extension window such as Velocity's own prompt, which closes as soon as it is answered and
// would take the queued downloads with it.
async function activeTabId() {
  const usable = tab => Number.isInteger(tab?.id) && tab.id >= 0 && /^https?:/i.test(tab.url || "");
  for (const query of [{ active: true, lastFocusedWindow: true }, { active: true, currentWindow: true }, { active: true }]) {
    try {
      const tabs = await api.tabs.query(query);
      const found = (tabs || []).find(usable);
      if (found) return found.id;
    } catch { /* try the next query */ }
  }
  return -1;
}

function resolveTheme(setting, hostTheme) {
  if (setting && setting !== "auto") return setting;
  return ["Midnight", "Ember", "Light"].includes(hostTheme) ? hostTheme : "Midnight";
}

function flashBadge(text) {
  if (!action?.setBadgeText) return;
  try {
    const color = action.setBadgeBackgroundColor({ color: "#3B6FD4" });
    Promise.resolve(color).catch(() => {});
    action.setBadgeText({ text });
    setTimeout(() => { try { action.setBadgeText({ text: "" }); } catch { /* ignore */ } }, 2500);
  } catch { /* badge is cosmetic */ }
}

/* ─────────────────────────── streams (HLS / DASH) ─────────────────────────── */

const MANIFEST_HINT = /\.(m3u8|mpd)(?:$|[?#])/i;

// A manifest is a description of a video, not a video: reading it is what lets the strip say which resolution a
// stream really is and how large it will be, instead of offering one of its segments as "the file".
// HLS writes its attributes in upper case and DASH in lower case, so every key is stored lower-case and read
// the same way.
function attributesOf(value) {
  const found = {};
  for (const match of String(value || "").matchAll(/([A-Za-z0-9-]+)=\s*("[^"]*"|[^,\s]*)/g)) {
    const raw = match[2].trim();
    found[match[1].toLowerCase()] = raw.startsWith('"') && raw.endsWith('"') ? raw.slice(1, -1) : raw;
  }
  return found;
}

function heightOf(resolution) {
  const match = /^(\d+)x(\d+)$/.exec(String(resolution || "").trim());
  return match ? Number(match[2]) : 0;
}

function resolveUrl(value, base) {
  try { return new URL(value, base).href; } catch { return ""; }
}

function qualityName(height) {
  if (!height) return "";
  if (height >= 2000) return "4K";
  return `${height}p`;
}

// Number of segments and their total duration from a media playlist; live playlists have no ENDLIST.
function mediaSummary(lines, url) {
  let duration = 0;
  let segments = 0;
  let live = true;
  let init = "";
  let encrypted = "";
  let pending = 0;
  for (const line of lines) {
    if (line.startsWith("#EXTINF:")) {
      pending = Number.parseFloat(line.slice(8)) || 0;
      continue;
    }
    if (line.startsWith("#EXT-X-ENDLIST")) { live = false; continue; }
    if (line.startsWith("#EXT-X-MAP:")) { init = attributesOf(line.slice(11)).uri || ""; continue; }
    if (line.startsWith("#EXT-X-KEY:")) {
      const attributes = attributesOf(line.slice(11));
      if (attributes.method && attributes.method !== "NONE") encrypted = attributes.method;
      continue;
    }
    if (line.startsWith("#")) continue;
    if (!resolveUrl(line, url)) continue;
    duration += pending;
    pending = 0;
    segments++;
  }
  return { duration, segments, live, init, encrypted };
}

function parseHls(text, url) {
  const lines = text.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  const variants = [];
  let pending = null;
  for (const line of lines) {
    if (line.startsWith("#EXT-X-STREAM-INF:")) { pending = attributesOf(line.slice(18)); continue; }
    if (line.startsWith("#") || !pending) continue;
    const variantUrl = resolveUrl(line, url);
    if (variantUrl) {
      const height = heightOf(pending.resolution);
      // Only the URI that followed a STREAM-INF line is a variant; a media playlist's bare lines are segments.
      variants.push({
        url: variantUrl,
        height,
        label: qualityName(height),
        bandwidth: Number(pending["average-bandwidth"] || pending.bandwidth) || 0
      });
    }
    pending = null;
  }

  if (variants.length === 0) {
    const media = mediaSummary(lines, url);
    return {
      ok: true,
      stream: {
        kind: "hls",
        url,
        variants: [],
        height: 0,
        label: "HLS",
        segments: media.segments,
        duration: media.duration,
        size: null,
        estimated: false,
        downloadable: !media.live && media.encrypted !== "SAMPLE-AES",
        reason: media.live
          ? "This is a live stream, so there is no finished video to save."
          : media.encrypted === "SAMPLE-AES"
            ? "This stream is DRM protected, so it cannot be saved as a normal file."
            : ""
      }
    };
  }

  const best = variants.slice().sort((a, b) => b.bandwidth - a.bandwidth)[0];
  return {
    ok: true,
    stream: {
      kind: "hls",
      url,
      variants: variants.map(variant => (
        variant.url === url ? variant : { ...variant, url: resolveUrl(variant.url, url) }
      )),
      height: best.height,
      label: best.label || "HLS",
      segments: 0,
      duration: 0,
      size: null,
      estimated: false,
      downloadable: true,
      reason: ""
    }
  };
}

// DASH describes the video as fragments. Velocity identifies it and says plainly that it cannot join those
// fragments, instead of handing the user a file that plays only its first two seconds.
function parseDash(text, url) {
  const variants = [];
  for (const match of text.matchAll(/<Representation\b([^>]*)>/gi)) {
    const attributes = attributesOf(match[1].replace(/\s+/g, " "));
    const declaredHeight = Number(attributes.height) || heightOf(attributes.resolution) || 0;
    variants.push({
      url,
      height: declaredHeight,
      label: qualityName(declaredHeight),
      bandwidth: Number(attributes.bandwidth) || 0
    });
  }
  const best = variants.slice().sort((a, b) => b.bandwidth - a.bandwidth)[0];
  return {
    ok: true,
    stream: {
      kind: "dash",
      url,
      variants,
      height: best ? best.height : 0,
      label: best && best.label ? best.label : "DASH",
      segments: 0,
      duration: 0,
      size: null,
      estimated: false,
      downloadable: false,
      reason: "DASH splits this video into fragments that Velocity cannot join into one file."
    }
  };
}

// Reads a manifest in the worker, where the page's cookies and referer rules still apply.
async function readStream(target) {
  let response;
  let text;
  try {
    response = await fetch(target, { credentials: "include", cache: "no-store", redirect: "follow" });
    if (!response.ok) return { ok: false, error: `The stream playlist answered HTTP ${response.status}.` };
    text = await response.text();
  } catch (error) {
    return { ok: false, error: `The stream playlist could not be read (${error?.message || "network error"}).` };
  }

  const url = response.url || target;
  if (/^\uFEFF?#EXTM3U/m.test(text)) {
    const parsed = parseHls(text, url);
    // The size of a stream is only knowable from its bitrate and its length, so the chosen variant's media
    // playlist is read for its duration; without that, the row shows the quality and no invented size.
    const stream = parsed.stream;
    if (stream.variants.length > 0) {
      const best = stream.variants.slice().sort((a, b) => b.bandwidth - a.bandwidth)[0];
      try {
        const media = await fetch(best.url, { credentials: "include", cache: "no-store" });
        if (media.ok) {
          const summary = mediaSummary((await media.text()).split(/\r?\n/).map(line => line.trim()).filter(Boolean), best.url);
          stream.segments = summary.segments;
          stream.duration = summary.duration;
          if (best.bandwidth > 0 && summary.duration > 0) {
            stream.size = Math.round((best.bandwidth * summary.duration) / 8);
            stream.estimated = true;
          }
        }
      } catch { /* the playlist itself is still usable */ }
    }
    return { ok: true, stream };
  }
  if (/<MPD[\s>]/i.test(text)) return parseDash(text, url);
  return { ok: false, error: "The server answered with something other than a stream playlist." };
}

/* ─────────────────────────── messaging ─────────────────────────── */

api.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const result = handleMessage(message, sender, sender?.tab?.id ?? -1);
  if (isFirefox) return result;
  result.then(sendResponse, error => sendResponse({ ok: false, error: String(error?.message || error) }));
  return true;
});

async function handleMessage(message, sender, tabId) {
  switch (message?.type) {
    case "velocity-probe": {
      const options = await getOptions();
      if (!options.intercept || isExcluded(message.url, options.excluded)) {
        // Velocity is off for this page: let Chrome behave exactly as if the extension were not installed.
        return { ok: true, bypass: true };
      }
      const details = await probe(message.url, message.name || "", Boolean(message.force));
      if (!details.ok) return details;
      const host = await getHost();
      return {
        ok: true,
        details: {
          ...details,
          pageUrl: sender?.tab?.url || sender?.url || "",
          destination: host.destination || "Velocity's default download folder",
          theme: resolveTheme(options.theme, host.theme)
        }
      };
    }

    case "velocity-action":
      // The page that made the request is the referer a protected host expects, so it travels with it.
      return handleAction({ ...message, tabId, pageUrl: sender?.tab?.url || sender?.url || "" });

    case "velocity-manifest":
      return readStream(message.url);

    case "velocity-streams": {
      // Asked at start-up: the page may have loaded its playlist before this content script was ready.
      return { ok: true, stream: tabStreams.get(tabId) || null };
    }

    case "velocity-hello": {
      // A reloaded page asks whether an offer is still waiting for it.
      const options = await getOptions();
      if (!options.intercept) return { ok: true, pending: null };
      const map = await readPending();
      const pending = map[tabId];
      if (!pending) return { ok: true, pending: null };
      // The offer stays valid until it is answered: the browser download behind it no longer exists.
      return { ok: true, pending };
    }

    case "velocity-inspect": {
      const options = await getOptions();
      const host = await getHost();
      if (!options.intercept || isExcluded(message.url, options.excluded)) {
        return { ok: true, bypass: true, options, host: { running: Boolean(host.running), destination: host.destination || "", theme: host.theme || "", error: host.error || null } };
      }
      const details = await probe(message.url, message.name || "", Boolean(message.force));
      return {
        ok: details.ok,
        details: details.ok
          ? { ...details, theme: resolveTheme(options.theme, host.theme), destination: host.destination || "Velocity's default download folder" }
          : details,
        options,
        host: { running: Boolean(host.running), destination: host.destination || "", theme: host.theme || "", error: host.error || null }
      };
    }

    case "velocity-stats":
      return { ok: true, stats: { ...stats, queued: offerQueue.length, active: Boolean(offerActive) } };

    case "velocity-status": {
      const host = await getHost(true);
      return { ok: true, host, options: await getOptions() };
    }

    case "velocity-options-changed":
      hostCache = { at: 0, value: null };
      return { ok: true };

    case "velocity-detect-boot": {
      // Answered from local storage only: the detector must come up without waiting for the native host.
      const options = await getOptions();
      // Warm the app theme for later badges without holding the answer hostage to the native host.
      if (!hostCache.value) void refreshHost();
      return {
        ok: true,
        enabled: options.detect !== false && !isExcluded(sender?.tab?.url || "", options.excluded),
        excluded: options.excluded || [],
        theme: resolveTheme(options.theme, hostCache.value?.theme)
      };
    }

    case "velocity-detect": {
      const options = await getOptions();
      if (!options.detect || isExcluded(message.url, options.excluded)) return { ok: true, found: false, disabled: true };
      const result = await detectFile(String(message.url), message.kind === "media" ? "media" : "link", Boolean(message.headOnly));
      return { ok: true, ...result, theme: resolveTheme(options.theme, hostCache.value?.theme) };
    }

    default:
      return { ok: false, error: `Unknown message: ${message?.type}` };
  }
}

/* ─────────────────────────── context menus ─────────────────────────── */

function createMenus() {
  try {
    api.contextMenus.create({ id: "velocity-download", title: "Download with Velocity", contexts: ["link", "image", "video", "audio"] });
    api.contextMenus.create({ id: "velocity-page", title: "Add this page to Velocity", contexts: ["page"] });
  } catch { /* the menus already exist */ }
}

api.runtime.onInstalled.addListener(() => {
  try {
    if (isFirefox) api.contextMenus.removeAll().then(createMenus, createMenus);
    else api.contextMenus.removeAll(createMenus);
  } catch {
    createMenus();
  }
});

api.contextMenus?.onClicked.addListener(info => { void contextMenu(info); });

async function contextMenu(info) {
  const url = info.linkUrl || info.srcUrl || info.pageUrl;
  if (!url) return;
  const options = await getOptions();
  const host = await getHost();
  const theme = resolveTheme(options.theme, host.theme);
  const destination = host.destination || "Velocity's default download folder";

  // The user asked for this exact link, so even a renderable file is treated as a download.
  const result = await handleMessage({ type: "velocity-probe", url, force: true }, { tab: { url: info.pageUrl } }, info.tab?.id ?? -1);
  if (result.bypass) {
    const forced = await probe(url, "", true);
    if (forced.ok) { await present(info.tab?.id ?? -1, { ...forced, destination, theme }); return; }
  }
  if (!result.ok) {
    await openFallbackWindow({ ...result, url, name: "", site: safeHost(url), theme, destination });
    return;
  }
  await present(info.tab?.id ?? -1, { ...result.details, destination, theme });
}

/* ─────────────────────────── housekeeping ─────────────────────────── */

api.tabs.onRemoved.addListener(tabId => {
  void (async () => {
    // A captured download is never thrown away because the tab it came from closed — that is a real file whose
    // browser copy is already gone. The offer is re-pointed at the standalone prompt instead, which is where an
    // offer goes when its page cannot show one.
    for (const entry of offerQueue) if (entry.tabId === tabId) entry.tabId = -1;
    if (offerActive?.tabId === tabId) {
      offerActive.tabId = -1;
      await forgetPending(tabId);
      void present(-1, offerActive.details);
    }
  })();
});

// Ids recorded by an older version (which paused downloads instead of cancelling them) are cancelled, never resumed
// and never erased: see cancelBrowserDownload for why Chrome's list is not deleted from.
api.runtime.onStartup?.addListener(() => {
  void (async () => {
    try {
      const stored = await api.storage.local.get({ [ORPHANS_KEY]: [] });
      for (const id of stored[ORPHANS_KEY]) await downloadsCall("cancel", id);
      await api.storage.local.set({ [ORPHANS_KEY]: [] });
    } catch { /* nothing to clean up */ }
  })();
});
