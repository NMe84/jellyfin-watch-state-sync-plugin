// Watch State Sync – web UI indicators, injected via the JavaScript Injector plugin.
//
//  • Cards (home, library, search, detail pages): a small link badge next to
//    the card's other indicators (top right) on every series / season /
//    episode card the current user is synced on.
//  • Detail pages: a "Synced with …" line under the title, plus a link button
//    in the action bar that opens a popup listing the synced users.
//
// Data: one GET /WatchSync/me per minute (series the user is synced on).
// Season/episode cards are mapped to their series from the card's own series
// link when present, otherwise with one batched /Items lookup per render.
(function () {
  'use strict';

  var BTN_ID   = 'ws-sync-btn';
  var LINE_ID  = 'ws-sync-line';
  var MODAL_ID = 'ws-sync-modal';
  var BADGE    = 'ws-sync-badge';
  var TTL_MS   = 60000;

  var synced   = null;  // seriesId -> [user names]; null until loaded
  var syncedFor = '';   // user id the map belongs to
  var loadedAt = 0;
  var loading  = false;
  var seriesOf = {};    // itemId -> seriesId ('' = not part of a series)
  var pending  = {};    // itemIds with a lookup in flight
  var scheduled = false;

  // ── Helpers ────────────────────────────────────────────────────────────────

  function norm(id) { return (id || '').replace(/-/g, '').toLowerCase(); }

  function api(path) {
    return fetch(window.ApiClient.serverAddress() + path, {
      headers: { 'X-Emby-Token': window.ApiClient.accessToken() }
    }).then(function (res) {
      if (!res.ok) throw new Error(res.status);
      return res.json();
    });
  }

  function formatNames(names) {
    if (names.length <= 1) return names.join('');
    return names.slice(0, -1).join(', ') + ' and ' + names[names.length - 1];
  }

  function currentUserId() {
    return window.ApiClient && window.ApiClient.getCurrentUserId
      ? window.ApiClient.getCurrentUserId() : null;
  }

  // ── Data ───────────────────────────────────────────────────────────────────

  function refreshSynced() {
    var userId = currentUserId();
    if (!userId || loading) return;
    if (userId === syncedFor && Date.now() - loadedAt < TTL_MS) return;

    loading = true;
    api('/WatchSync/me').then(function (list) {
      var map = {};
      list.forEach(function (c) { map[norm(c.seriesId)] = c.users; });
      synced = map;
      syncedFor = userId;
      // Re-evaluate every card and the detail page against the new data.
      document.querySelectorAll('.card[data-ws]').forEach(function (c) { c.removeAttribute('data-ws'); });
      schedule();
    }).catch(function () {
      synced = synced || {};
      syncedFor = userId;   // retry after TTL, not on every DOM change
    }).then(function () {
      loadedAt = Date.now();
      loading = false;
    });
  }

  // Batch-resolve item ids (seasons/episodes, detail pages) to their series id.
  function resolveSeries(ids) {
    ids = ids.filter(function (id) { return !pending[id] && seriesOf[id] === undefined; });
    if (!ids.length) return;
    ids.forEach(function (id) { pending[id] = true; });

    api('/Items?userId=' + currentUserId() + '&Ids=' + ids.join(',') +
        '&EnableImages=false&EnableUserData=false&EnableTotalRecordCount=false')
      .then(function (res) {
        (res.Items || []).forEach(function (item) {
          seriesOf[norm(item.Id)] = item.Type === 'Series' ? norm(item.Id) : norm(item.SeriesId);
        });
      })
      .catch(function () {})
      .then(function () {
        ids.forEach(function (id) {
          delete pending[id];
          if (seriesOf[id] === undefined) seriesOf[id] = '';
        });
        schedule();
      });
  }

  // ── Card badges ────────────────────────────────────────────────────────────

  function setBadge(card, names) {
    var old = card.querySelector('.' + BADGE);
    if (!names) { if (old) old.remove(); return; }
    if (old) return;

    // Join Jellyfin's own indicator row (unplayed count, played check) in the
    // top-right corner; bottom right is taken by the play button on touch UIs.
    var box = card.querySelector('.cardIndicators');
    if (!box) {
      var host = card.querySelector('.cardImageContainer') || card.querySelector('.cardScalable');
      if (!host) return;
      box = document.createElement('div');
      box.className = 'cardIndicators';
      host.appendChild(box);
    }

    var badge = document.createElement('div');
    badge.className = BADGE + ' countIndicator indicator';
    badge.title = 'Watch state synced with ' + formatNames(names);
    var icon = document.createElement('span');
    icon.className = 'material-icons';
    icon.setAttribute('aria-hidden', 'true');
    icon.style.fontSize = '1.3em';
    icon.textContent = 'link';
    badge.appendChild(icon);
    box.insertBefore(badge, box.firstChild);
  }

  function scanCards() {
    var lookup = [];
    document.querySelectorAll('.card[data-id]').forEach(function (card) {
      var id = norm(card.getAttribute('data-id'));
      // Library grids recycle card elements, so remember which item was handled.
      if (card.getAttribute('data-ws') === id) return;

      var type = card.getAttribute('data-type');
      if (type !== 'Series' && type !== 'Season' && type !== 'Episode') {
        card.setAttribute('data-ws', id);
        setBadge(card, null);
        return;
      }

      var sid = type === 'Series' ? id : seriesOf[id];
      if (sid === undefined) {
        // Episode cards with a footer link to their series (Next Up, Latest, …).
        var link = card.querySelector('a[data-type="Series"][data-id]');
        if (link) sid = seriesOf[id] = norm(link.getAttribute('data-id'));
      }
      if (sid === undefined) { lookup.push(id); return; }

      card.setAttribute('data-ws', id);
      setBadge(card, synced[sid]);
    });
    resolveSeries(lookup);
  }

  // ── Detail page ────────────────────────────────────────────────────────────

  function showModal(names, origin) {
    var old = document.getElementById(MODAL_ID);
    if (old) old.remove();

    var overlay = document.createElement('div');
    overlay.id = MODAL_ID;
    overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.7);z-index:9999;' +
      'display:flex;align-items:center;justify-content:center;padding:20px';

    var box = document.createElement('div');
    box.className = 'focuscontainer';
    box.style.cssText = 'background:var(--color-card-background,#202020);border-radius:2px;' +
      'padding:24px;max-width:400px;width:100%';

    var h = document.createElement('h3');
    h.className = 'formDialogHeaderTitle';
    h.style.margin = '0 0 12px';
    h.textContent = 'Watch State Sync';

    var msg = document.createElement('p');
    msg.style.cssText = 'margin:0 0 20px;line-height:1.5;';
    msg.textContent = 'Watch state synchronized with ' + formatNames(names) + '.';

    var ok = document.createElement('button');
    ok.type = 'button';
    ok.className = 'raised button-submit emby-button';
    ok.style.cssText = 'float:right;';
    ok.textContent = 'OK';

    function close() {
      document.removeEventListener('keydown', onKey, true);
      overlay.remove();
      if (origin) origin.focus();
    }

    // Capture phase so we run before Jellyfin's back-navigation handler (TV remotes).
    function onKey(e) {
      if (e.key === 'Escape' || e.key === 'GoBack' || e.keyCode === 27 || e.keyCode === 461) {
        e.stopPropagation();
        e.preventDefault();
        close();
      }
    }
    document.addEventListener('keydown', onKey, true);

    ok.addEventListener('click', function (e) { e.stopPropagation(); close(); });
    overlay.addEventListener('click', function (e) { if (e.target === overlay) close(); });

    box.appendChild(h);
    box.appendChild(msg);
    box.appendChild(ok);
    overlay.appendChild(box);
    document.body.appendChild(overlay);
    ok.focus();
  }

  function makeButton(container, names) {
    var btn = document.createElement('button');
    btn.id   = BTN_ID;
    btn.type = 'button';
    // Copy the structural classes of the always-visible "more" button.
    var ref = container.querySelector('.btnMoreCommands');
    btn.className = ref
      ? ref.className.split(' ').filter(function (c) { return c && c !== 'btnMoreCommands'; }).join(' ')
      : 'button-flat detailButton emby-button paper-icon-button-light';
    btn.title = 'Watch state synchronized with ' + formatNames(names);
    btn.style.color = 'var(--mui-palette-primary-main, #00a4dc)';
    btn.innerHTML = '<div class="detailButton-content">' +
      '<span class="material-icons detailButton-icon" aria-hidden="true">link</span></div>';
    btn.addEventListener('click', function () { showModal(names, btn); });
    container.insertBefore(btn, ref || null);
  }

  function makeLine(page, names) {
    var anchor = page.querySelector('.itemMiscInfo-primary') || page.querySelector('.nameContainer');
    if (!anchor) return;
    var line = document.createElement('div');
    line.id = LINE_ID;
    line.style.cssText = 'display:flex;align-items:center;gap:.4em;margin:.5em 0;' +
      'color:var(--mui-palette-primary-main,#00a4dc);';
    var icon = document.createElement('span');
    icon.className = 'material-icons';
    icon.setAttribute('aria-hidden', 'true');
    icon.style.fontSize = '1.2em';
    icon.textContent = 'link';
    var text = document.createElement('span');
    text.textContent = 'Synced with ' + formatNames(names);
    line.appendChild(icon);
    line.appendChild(text);
    anchor.parentNode.insertBefore(line, anchor.nextSibling);
  }

  function updateDetail() {
    var page  = document.querySelector('.itemDetailPage:not(.hide)');
    var match = /[?&]id=([0-9a-f-]{32,36})/i.exec(window.location.hash);
    var id    = page && match ? norm(match[1]) : '';
    var sid   = id ? seriesOf[id] : '';
    if (id && sid === undefined) { resolveSeries([id]); sid = ''; }
    var names = sid ? synced[sid] : null;

    // Remove indicators that belong to a different item (the page element is reused).
    [BTN_ID, LINE_ID].forEach(function (elId) {
      var el = document.getElementById(elId);
      if (el && (!names || el.getAttribute('data-item') !== id || !page.contains(el))) el.remove();
    });
    if (!names) return;

    var buttons = page.querySelector('.mainDetailButtons');
    if (buttons && !document.getElementById(BTN_ID)) {
      makeButton(buttons, names);
      document.getElementById(BTN_ID).setAttribute('data-item', id);
    }
    if (!document.getElementById(LINE_ID)) {
      makeLine(page, names);
      var line = document.getElementById(LINE_ID);
      if (line) line.setAttribute('data-item', id);
    }
  }

  // ── Driver ─────────────────────────────────────────────────────────────────

  function run() {
    scheduled = false;
    if (!currentUserId()) return;
    if (currentUserId() !== syncedFor || Date.now() - loadedAt >= TTL_MS) refreshSynced();
    if (!synced || currentUserId() !== syncedFor) return;
    scanCards();
    updateDetail();
  }

  function schedule() {
    if (scheduled) return;
    scheduled = true;
    // setTimeout, not requestAnimationFrame: rAF is paused while the page is hidden
    // (background tab, app in background), which left cards unbadged.
    setTimeout(run, 50);
  }

  new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', schedule);
  schedule();
})();
