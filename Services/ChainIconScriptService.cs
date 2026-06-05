using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.WatchSync.Services;

/// <summary>
/// Registers (and unregisters) the chain-icon client-side script via the
/// Jellyfin JavaScript Injector plugin. If the injector is not installed this
/// service is a harmless no-op.
/// </summary>
public class ChainIconScriptService : IHostedService
{
    private const string ScriptId = "watchsync-chain-icon";

    private readonly ILogger<ChainIconScriptService> _logger;

    public ChainIconScriptService(ILogger<ChainIconScriptService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return Task.CompletedTask;

        var payload = new JObject
        {
            ["id"]                     = ScriptId,
            ["name"]                   = "Watch State Sync – Chain Icon",
            ["script"]                 = ChainIconScript,
            ["enabled"]                = true,
            ["requiresAuthentication"] = true,
            ["pluginId"]               = plugin.Id.ToString(),
            ["pluginName"]             = plugin.Name,
            ["pluginVersion"]          = plugin.Version.ToString()
        };

        var registered = JavaScriptInjectorBridge.RegisterScript(payload, _logger);

        if (registered)
            _logger.LogInformation("WatchSync: chain-icon script registered via JavaScript Injector");
        else
            _logger.LogDebug("WatchSync: JavaScript Injector not present — chain-icon script skipped");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null) return Task.CompletedTask;

        var removed = JavaScriptInjectorBridge.UnregisterAll(plugin.Id.ToString(), _logger);
        if (removed > 0)
            _logger.LogInformation("WatchSync: unregistered {Count} script(s) from JavaScript Injector", removed);

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Inline JavaScript — runs inside Jellyfin's web UI after authentication.
    //
    // Style notes for this verbatim C# string:
    //   • Only single quotes used in JS to avoid "" escaping clutter.
    //   • X-Emby-Token header used (no quoting of the token value required).
    //   • No template literals so backtics don't need escaping either.
    // -------------------------------------------------------------------------
    private const string ChainIconScript = @"
(function () {
  'use strict';

  var BTN_ID   = 'ws-sync-btn';
  var MODAL_ID = 'ws-sync-modal';

  var lastHash    = '';
  var lastItemId  = '';
  var cachedNames = null; // names for the current item, persisted for poll-based re-injection

  // ── Helpers ──────────────────────────────────────────────────────────────

  function getItemIdFromHash() {
    // Jellyfin uses 32-char GUIDs (no dashes) in URLs; also accept 36-char dashed format.
    var m = window.location.hash.match(/[?&]id=([0-9a-f]{32}|[0-9a-f-]{36})/i);
    return m ? m[1] : null;
  }

  function isDetailPage() {
    return /itemdetails|\/details/i.test(window.location.hash);
  }

  function removeBtn() {
    var b = document.getElementById(BTN_ID);
    if (b) b.parentNode.removeChild(b);
  }

  function formatNames(names) {
    if (names.length === 0) return '';
    if (names.length === 1) return names[0];
    if (names.length === 2) return names[0] + ' and ' + names[1];
    return names.slice(0, -1).join(', ') + ' and ' + names[names.length - 1];
  }

  // ── Modal ─────────────────────────────────────────────────────────────────

  function showModal(names, origin) {
    var old = document.getElementById(MODAL_ID);
    if (old) old.parentNode.removeChild(old);

    var overlay = document.createElement('div');
    overlay.id = MODAL_ID;
    overlay.style.cssText = [
      'position:fixed', 'inset:0',
      'background:rgba(0,0,0,.7)', 'z-index:9999',
      'display:flex', 'align-items:center', 'justify-content:center', 'padding:20px'
    ].join(';');

    var box = document.createElement('div');
    box.className = 'focuscontainer';
    box.style.cssText = [
      'background:var(--color-card-background,#202020)',
      'border-radius:2px', 'padding:24px',
      'max-width:400px', 'width:100%'
    ].join(';');

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
      overlay.parentNode.removeChild(overlay);
      if (origin) origin.focus();
    }

    // Use capture phase so our handler fires before Jellyfin's own back-navigation
    // listener, letting us both close the popup AND stop the event from propagating.
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
    ok.focus(); // pressing OK/Enter on the remote closes the popup immediately
  }

  // ── Button injection ──────────────────────────────────────────────────────

  function injectButton(names) {
    removeBtn();
    var container = document.querySelector('.mainDetailButtons');
    if (!container) return;

    var btn = document.createElement('button');
    btn.id    = BTN_ID;
    btn.type  = 'button';
    // Use .btnMoreCommands as the style reference — it is always visible (no
    // state classes) and shares the same structural classes as all buttons in
    // the bar.  .btnPlaystate can carry hide/state classes that would make a
    // copied button invisible.  Fall back to hardcoded classes if not found.
    var refBtn = container.querySelector('.btnMoreCommands');
    if (refBtn) {
      var excluded = { 'btnMoreCommands': true };
      btn.className = refBtn.className.split(' ').filter(function (c) { return c && !excluded[c]; }).join(' ');
    } else {
      btn.className = 'button-flat detailButton emby-button paper-icon-button-light';
    }
    btn.title = 'Watch state synchronized with ' + formatNames(names);
    btn.style.color = 'var(--mui-palette-primary-main, #00a4dc)';
    btn.innerHTML = '<div class=""detailButton-content"">' +
                    '<span class=""material-icons detailButton-icon"" aria-hidden=""true"">link</span>' +
                    '</div>';
    btn.addEventListener('click', function () { showModal(names, btn); });

    // Insert before the three-dots button when present, otherwise append.
    var moreBtn = container.querySelector('.btnMoreCommands');
    if (moreBtn) {
      container.insertBefore(btn, moreBtn);
    } else {
      container.appendChild(btn);
    }

  }

  // ── Page change detection ─────────────────────────────────────────────────
  //
  // Design: the 800ms poll drives everything.
  //   - On navigation to a new detail page: fetch connections, cache the names.
  //   - On every subsequent tick for the same page: if names are cached and the
  //     button is missing (React re-rendered the bar), re-inject immediately.
  //
  // This replaces the previous MutationObserver approach, which had two races:
  //   1. Observer fires once and disconnects — if React replaces the container
  //      AFTER the observer fires but BEFORE injectButton runs, nothing appears.
  //   2. Container appears and disappears before the API call returns — the
  //      observer is set up after the relevant mutation and never fires.

  function checkPage() {
    var currentHash = window.location.hash;

    // ── Not a detail page ───────────────────────────────────────────────────
    if (!isDetailPage()) {
      if (lastItemId) {
        lastItemId  = '';
        cachedNames = null;
        removeBtn();
      }
      lastHash = currentHash;
      return;
    }

    var itemId = getItemIdFromHash();
    if (!itemId) { lastHash = currentHash; return; }

    if (!window.ApiClient) return;  // ApiClient not ready — retry next tick
    var userId = window.ApiClient.getCurrentUserId();
    if (!userId) return;            // session not ready — retry next tick

    // ── Same detail page ────────────────────────────────────────────────────
    if (itemId === lastItemId) {
      // Ensure the button is present every tick (re-injects after React renders)
      if (cachedNames) {
        var container = document.querySelector('.mainDetailButtons');
        if (container && !document.getElementById(BTN_ID)) {
          injectButton(cachedNames);
        }
      }
      return;
    }

    // ── New detail page ─────────────────────────────────────────────────────
    lastHash    = currentHash;
    lastItemId  = itemId;
    cachedNames = null;
    removeBtn();

    var token   = window.ApiClient.accessToken();
    var baseUrl = window.ApiClient.serverAddress();
    var url     = baseUrl + '/WatchSync/connections/item/' + itemId + '/user/' + userId;

    fetch(url, { headers: { 'X-Emby-Token': token } })
      .then(function (res) { return res.ok ? res.json() : []; })
      .then(function (connections) {
        if (!connections || connections.length === 0) return;
        cachedNames = connections.map(function (c) { return c.userName; });
        // Try to inject immediately; if the container isn't ready yet the poll
        // will inject it within the next 800 ms tick.
        var container = document.querySelector('.mainDetailButtons');
        if (container) { injectButton(cachedNames); }
      })
      .catch(function () {});
  }

  setInterval(checkPage, 800);
  checkPage();
})();
";
}
