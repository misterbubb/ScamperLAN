using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private void SetupNativeLifecycleTrace()
    {
        WatchLan("BezbroGames.Scamper", "BezbroGames.Scamper.Core.GameController",
            "BeginLocalEnvironmentLoadLease", "EndLocalEnvironmentLoadLease",
            "WaitForLocalEnvironmentReadiness", "ReportLocalEnvironmentLoadState",
            "ReportLocalEnvironmentLoadState_ServerRpc", "UpdateScamperPlayerMapPresenceLocal",
            "ApplyScamperPlayerMapPresence");
        WatchLan("BezbroGames.Scamper", "BezbroGames.Scamper.Core.ScamperPlayer",
            "ApplyAuthoritativeRespawn");
        WatchLan("BezbroGames.Scamper", "BezbroGames.Scamper.Core.PlayerGlobals",
            "KillPlayer", "RespawnPlayer", "SnapRespawnTransformsToOrigin",
            "ResetLocalRigSolversAfterRespawn");
        WatchLan("BezbroGames.Scamper", "BezbroGames.Scamper.Networking.ScamperClientLiveness",
            "NoteRejectedAcknowledgement");
    }

    private void WatchLan(string assembly, string typeName, params string[] names)
    {
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)
                ?? Assembly.Load(assembly);
            var type = loaded.GetType(typeName, false);
            if (type == null)
            {
                Log.LogWarning("Missing LAN trace type: " + typeName);
                return;
            }
            foreach (var name in names)
            {
                var methods = type.GetMethods(All).Where(m => m.Name == name && !m.IsGenericMethod && !m.IsAbstract).ToArray();
                if (methods.Length == 0)
                {
                    Log.LogWarning("Missing LAN trace method: " + typeName + "." + name);
                    continue;
                }
                foreach (var method in methods)
                {
                    try
                    {
                        harmony!.Patch(method,
                            prefix: new HarmonyMethod(typeof(Plugin), nameof(LanFlowBefore)),
                            postfix: new HarmonyMethod(typeof(Plugin), nameof(LanFlowAfter)));
                        Log.LogInfo("LAN flow trace installed: " + Describe(method));
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning("LAN flow trace hook failed: " + Describe(method) + " | " + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning("Cannot inspect LAN flow type " + typeName + " | " + ex.Message);
        }
    }

    private static bool ShouldTraceLanFlow() => lanModeSelected && !stopping &&
        ((nativeClientRequested && nativeClientSubmitted) || (requested && attempted && menuStarted && !nativeClientRequested));

    public static void LanFlowBefore(MethodBase __originalMethod, object[] __args)
    {
        if (ShouldTraceLanFlow())
        {
            logger?.LogInfo("[LAN NATIVE FLOW] ENTER " + Describe(__originalMethod) + SafeTraceArgs(__args));
        }
    }

    public static void LanFlowAfter(MethodBase __originalMethod)
    {
        if (ShouldTraceLanFlow())
        {
            logger?.LogInfo("[LAN NATIVE FLOW] EXIT " + Describe(__originalMethod));
        }
    }

    private static string SafeTraceArgs(object[] args)
    {
        if (args == null || args.Length == 0)
        {
            return string.Empty;
        }

        return " args=[" + string.Join(",", args.Select(value =>
        {
            if (value == null)
            {
                return "null";
            }

            var type = value.GetType();
            return type.IsPrimitive || value is decimal
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? type.Name
                : type.Name;
        })) + "]";
    }
}
