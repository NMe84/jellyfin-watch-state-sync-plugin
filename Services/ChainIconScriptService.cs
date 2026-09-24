using System.IO;
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
            ["name"]                   = "Watch State Sync – Indicators",
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

    private static string ChainIconScript
    {
        get
        {
            using var stream = typeof(ChainIconScriptService).Assembly
                .GetManifestResourceStream("Jellyfin.Plugin.WatchSync.Web.syncIndicators.js")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
