// ==UserScript==
// @name         Jellyfin Watch State Sync Chain Icon
// @namespace    jellyfin-watch-sync
// @version      1.0.0
// @description  Shows a 🔗 badge with synced-user names on show, season and episode pages.
// @match        http*://*/web/*
// @grant        none
// ==/UserScript==

/**
 * HOW TO USE
 * ----------
 * Option A – Browser extension (recommended):
 *   Install Tampermonkey (Chrome/Edge/Firefox), paste this script into a new userscript.
 *
 * Option B – Jellyfin web/config.json:
 *   Add a <script> tag pointing at this file to Jellyfin's custom scripts list, OR
 *   serve this file from a static host and reference it in
 *   /jellyfin/web/config.json under "customScript".
 *
 * Option C – jellyfin-plugin-pages:
 *   Add a second PluginPageInfo entry in Plugin.cs that returns this file as an
 *   auto-loading script page (requires the jellyfin-plugin-pages companion plugin).
 */

(function () {
  'use strict';

  // ── Config ─────────────────────────────────────────────────────────────────
  const BADGE_ID   = 'ws-chain-badge';
  const POLL_MS    = 800;   // how often to check for page changes
  const BADGE_STYLE = [
    'display:inline-flex',
    'align-items:center',
    'gap:5px',
    'background:rgba(0,164,220,.18)',
    'border:1px solid rgba(0,164,220,.5)',
    'border-radius:4px',
    'padding:4px 10px',
    'font-size:13px',
    'color:#00a4dc',
    'margin-top:8px',
    'cursor:default',
  ].join(';');

  // ── Helpers ─────────────────────────────────────────────────────────────────
  function getToken() {
    try {
      // Jellyfin stores the token in localStorage as part of ApiClient state.
      const raw = localStorage.getItem('jellyfin_credentials');
      if (raw) {
        const parsed = JSON.parse(raw);
        const server = parsed.Servers && parsed.Servers[0];
        if (server && server.AccessToken) return server.AccessToken;
      }
    } catch (_) {}
    // Fallback: try ApiClient directly (works in plugin-page context).
    if (window.ApiClient) return window.ApiClient.accessToken();
    return '';
  }

  function getCurrentUserId() {
    try {
      const raw = localStorage.getItem('jellyfin_credentials');
      if (raw) {
        const parsed = JSON.parse(raw);
        const server = parsed.Servers && parsed.Servers[0];
        if (server && server.UserId) return server.UserId;
      }
    } catch (_) {}
    if (window.ApiClient) return window.ApiClient.getCurrentUserId();
    return null;
  }

  function getServerUrl() {
    return window.location.origin;
  }

  async function fetchConnections(itemId, userId) {
    const token = getToken();
    const url = `${getServerUrl()}/WatchSync/connections/item/${itemId}/user/${userId}`;
    const res = await fetch(url, {
      headers: { 'Authorization': `MediaBrowser Token="${token}"` }
    });
    if (!res.ok) return [];
    return res.json();
  }

  // ── Extract itemId from the current URL hash ─────────────────────────────
  function getItemIdFromHash() {
    // Jellyfin web uses hash routing: #/details?id=GUID or #/itemdetails?id=GUID
    const hash = window.location.hash;
    const m = hash.match(/[?&]id=([0-9a-f-]{36})/i);
    return m ? m[1] : null;
  }

  // ── Badge injection ───────────────────────────────────────────────────────
  function removeBadge() {
    const existing = document.getElementById(BADGE_ID);
    if (existing) existing.remove();
  }

  function injectBadge(names) {
    removeBadge();

    // Try to find a good anchor point: the item detail header area.
    const anchor =
      document.querySelector('.itemName') ||
      document.querySelector('.nameContainer') ||
      document.querySelector('h1.itemName') ||
      document.querySelector('.detailPageContent h1');

    if (!anchor) return;

    const badge = document.createElement('div');
    badge.id = BADGE_ID;
    badge.setAttribute('style', BADGE_STYLE);
    badge.title = 'Watch state synced with: ' + names.join(', ');
    badge.innerHTML = '🔗 Synced with ' + names.map(n => `<strong>${n}</strong>`).join(', ');

    // Insert after the anchor element.
    anchor.insertAdjacentElement('afterend', badge);
  }

  // ── Page change detection ─────────────────────────────────────────────────
  let lastHash   = '';
  let lastItemId = '';

  async function checkPage() {
    const currentHash = window.location.hash;
    if (currentHash === lastHash) return;
    lastHash = currentHash;

    removeBadge();

    const itemId = getItemIdFromHash();
    if (!itemId || itemId === lastItemId) return;
    lastItemId = itemId;

    // Only act on detail-type routes.
    if (!/details|itemdetails/i.test(currentHash)) return;

    const userId = getCurrentUserId();
    if (!userId) return;

    try {
      const connections = await fetchConnections(itemId, userId);
      if (connections && connections.length > 0) {
        const names = connections.map(c => c.userName);
        // Give the page a moment to finish rendering before injecting.
        setTimeout(() => injectBadge(names), 600);
      }
    } catch (_) {
      // Silently ignore — the icon is a cosmetic enhancement.
    }
  }

  setInterval(checkPage, POLL_MS);
  checkPage();
})();
