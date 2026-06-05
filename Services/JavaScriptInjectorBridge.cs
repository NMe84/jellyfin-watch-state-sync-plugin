using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.WatchSync.Services;

/// <summary>
/// Reflection-based bridge to the Jellyfin JavaScript Injector plugin
/// (https://github.com/n00bcodr/Jellyfin-JavaScript-Injector).
///
/// All calls are no-ops and return safe defaults when the injector is not installed,
/// so the dependency stays fully optional.
/// </summary>
internal static class JavaScriptInjectorBridge
{
    private const string InjectorAssemblyName = "Jellyfin.Plugin.JavaScriptInjector";
    private const string InterfaceTypeName    = "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";

    // Cached after the first resolution attempt.
    private static Type?  _type;
    private static bool   _resolved;

    private static Type? ResolveType()
    {
        if (_resolved) return _type;
        _resolved = true;

        // The injector lives in a separate AssemblyLoadContext; search all contexts.
        foreach (var ctx in AssemblyLoadContext.All)
        {
            foreach (var asm in ctx.Assemblies)
            {
                if (asm.FullName?.Contains(InjectorAssemblyName, StringComparison.Ordinal) == true)
                {
                    _type = asm.GetType(InterfaceTypeName);
                    if (_type is not null) return _type;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Registers a script with the JavaScript Injector.
    /// Returns <c>true</c> on success, <c>false</c> if the injector is absent or the call fails.
    /// </summary>
    public static bool RegisterScript(JObject payload, ILogger? logger = null)
    {
        try
        {
            var type = ResolveType();
            if (type is null) return false;

            var method = type.GetMethod("RegisterScript", BindingFlags.Static | BindingFlags.Public);
            var result = method?.Invoke(null, new object[] { payload });
            return result is true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "WatchSync: RegisterScript call failed");
            return false;
        }
    }

    /// <summary>
    /// Removes all scripts that were registered under <paramref name="pluginId"/>.
    /// Returns the number of scripts removed.
    /// </summary>
    public static int UnregisterAll(string pluginId, ILogger? logger = null)
    {
        try
        {
            var type = ResolveType();
            if (type is null) return 0;

            var method = type.GetMethod(
                "UnregisterAllScriptsFromPlugin",
                BindingFlags.Static | BindingFlags.Public);

            var result = method?.Invoke(null, new object[] { pluginId });
            return result is int count ? count : 0;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "WatchSync: UnregisterAllScriptsFromPlugin call failed");
            return 0;
        }
    }
}
