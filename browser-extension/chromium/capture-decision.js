/*
 * The decision the extension makes about a download Chrome has just started: offer it to the user, or cancel it
 * quietly because Chrome is replaying something from its own list.
 *
 * Kept in its own file so it can be executed and asserted outside the browser (artifacts/e2e/capture-decision.js);
 * a service worker loads it with importScripts. No DOM, no chrome.* — only the rule.
 */
(function (global) {
  "use strict";

  // A download whose own start time is older than this is a replay of an earlier session, not a new request.
  const RESTORED_MS = 2 * 60 * 1000;

  /**
   * @param item          the chrome.downloads DownloadItem Chrome reported
   * @param record        the ledger entry for this URL ({ how: "velocity" | "chrome" | "offered" | "declined" }) or null
   * @param cancelledIds  ids of downloads this extension already cancelled in an earlier session (Chrome reuses an id
   *                      when it retries an interrupted download, which is why it is the reliable replay signal)
   * @param now           clock, for testing
   * @returns "offer" | "silent-replay" | "silent-known"
   */
  function shouldOfferCapture(item, record, cancelledIds, now) {
    const at = typeof now === "number" ? now : Date.now();
    const id = item && item.id;
    const known = Array.isArray(cancelledIds) ? cancelledIds : [];

    // The same download item coming back: Chrome is retrying what is already in its list. It was handled once, so it
    // is handled the same way again — cancelled, never offered, however many times the browser restarts.
    if (id !== undefined && id !== null && known.includes(id)) return "silent-replay";

    const startedAt = Date.parse((item && item.startTime) || "");
    const restored = Number.isFinite(startedAt) && at - startedAt > RESTORED_MS;
    if (restored) return "silent-replay";

    if (record) {
      // The user already answered for this file. A download with no page behind it cannot be a fresh choice, and a
      // declined file is never asked about again.
      const tabless = !(Number.isInteger(item && item.tabId) && item.tabId >= 0);
      if (record.how === "declined" || tabless) return "silent-known";
    }

    return "offer";
  }

  const api = { shouldOfferCapture, RESTORED_MS };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else global.VelocityCapture = api;
})(typeof self !== "undefined" ? self : globalThis);
