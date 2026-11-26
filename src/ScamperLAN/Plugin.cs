using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ScamperLAN;

[BepInPlugin("scamperlan.diagnostics", "ScamperLAN", "1.1.5")]
public sealed partial class Plugin : BasePlugin
{
    private static ManualLogSource? logger;
    private Harmony? harmony;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public override void Load()
    {
        logger = Log;
        harmony = new Harmony("scamperlan.diagnostics");
        Log.LogInfo("ScamperLAN experimental native LAN plugin loaded.");
        Watch("BezbroGames.Scamper", "ScamperServerConnectorPlayflow",
            "Awake", "StartSingleplayer", "StartSingleplayerAfterCleanup", "JoinServer",
            "StartRemoteConnection", "StopAllConnections", "StopTransportConnections", "OnClientStateChanged");
        Watch("FishNet.Runtime", "FishNet.Managing.Server.ServerManager",
            "StartConnection", "StopConnection", "Transport_OnServerConnectionState");
        Watch("FishNet.Runtime", "FishNet.Managing.Client.ClientManager",
            "StartConnection", "StopConnection", "Transport_OnClientConnectionState", "ParseAuthenticated", "ParseVersion");
        Watch("FishNet.Runtime", "FishNet.Transporting.Multipass.Multipass",
            "SetClientTransport", "StartConnection", "StopConnection");
        Watch("FishNet.Runtime", "FishNet.Transporting.Tugboat.Tugboat",
            "StartConnection", "StopConnection", "SetPort", "HandleServerConnectionState", "HandleClientConnectionState");
        Watch("FishNet.Runtime", "FishNet.Transporting.Yak.Yak",
            "StartConnection", "StopConnection", "HandleServerConnectionState", "HandleClientConnectionState");
        WatchTransportSelection();
        lanSupportEnabled = true;
        lanModeSelected = false;
        Log.LogInfo("[LAN] Online mode selected. Use the TV selector to switch to LAN.");
        SetupHostTest();
        SetupMenuHostTest();
        if (lanSupportEnabled)
        {
            SetupRoundSync();
            SetupNativeLifecycleTrace();
        }
        if (lanSupportEnabled || Environment.GetCommandLineArgs().Contains("--scamperlan-menu-host-test", StringComparer.Ordinal))
        {
            Watch("BezbroGames.Scamper", "StartZone", "StartGame", "StartGameLocal", "StartGameServer");
            Watch("BezbroGames.Scamper", "BezbroGames.Scamper.UI.MainUIHandler", "EnableSingleplayerMenu", "EnableClientMenu", "DisableClientMenu", "ApplyRoundStateUI", "OnDifficultySliderValueChanged");
            Watch("BezbroGames.Scamper", "BezbroGames.Scamper.Core.GameController", "ForceResetLocalRound", "StartGame");
        }
        Watch("BezbroGames.Scamper", "BezbroGames.Scamper.Networking.ScamperNetworkAuthenticator", "OnRemoteConnection", "OnClientConnectionState", "SendClientReport", "OnClientReport", "OnAdmissionResult", "CompleteAdmission");
        Watch("BezbroGames.Scamper", "BezbroGames.Scamper.Networking.ScamperPlayerSpawner", "SceneManager_OnClientLoadedStartScenes", "ServerManager_OnRemoteConnectionState");
        Log.LogInfo("Online remains the default. Use the TV selector to choose LAN; no automatic connection is made.");
    }

    private void Watch(string assembly, string typeName, params string[] names)
    {
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assembly)
                ?? Assembly.Load(assembly);
            var type = loaded.GetType(typeName, false);
            if (type == null)
            {
                Log.LogWarning("Missing type: " + typeName);
                return;
            }
            foreach (var name in names)
            {
                var methods = type.GetMethods(All).Where(m => m.Name == name && !m.IsGenericMethod && !m.IsAbstract).ToArray();
                if (methods.Length == 0)
                {
                    Log.LogWarning("Missing method: " + typeName + "." + name);
                }

                foreach (var method in methods)
                {
                    try
                    {
                        harmony!.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), nameof(Before)),
                            postfix: new HarmonyMethod(typeof(Plugin), nameof(After)));
                        Log.LogInfo("Hooked: " + Describe(method));
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning("Hook failed: " + Describe(method) + " | " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning("Cannot inspect " + typeName + " | " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void WatchTransportSelection()
    {
        try
        {
            var type = Assembly.Load("FishNet.Runtime").GetType("FishNet.Transporting.Multipass.Multipass", true)!;
            Add("SetClientTransport", new[] { typeof(int) }, nameof(TraceSelect));
            Add("StartConnection", new[] { typeof(bool), typeof(int) }, nameof(TraceStart));
            void Add(string name, Type[] signature, string hook)
            {
                var method = type.GetMethod(name, All, null, signature, null);
                if (method == null)
                {
                    Log.LogWarning("Transport detail hook unavailable: " + name);
                    return;
                }
                try
                {
                    harmony!.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), hook));
                    Log.LogInfo("Transport detail hook installed: " + Describe(method));
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Transport detail hook failed: " + name + " | " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning("Transport detail setup failed: " + ex.Message);
        }
    }

    public static void TraceSelect(int __0) =>
        logger?.LogInfo("[LAN TRANSPORT] client transport index=" + __0);

    public static void TraceStart(bool __0, int __1) =>
        logger?.LogInfo("[LAN TRANSPORT] start server=" + __0 + " index=" + __1);

    private static string Describe(MethodBase method) => method.DeclaringType?.Name + "." + method.Name + "(" +
        string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";

    public static void Before(MethodBase __originalMethod) => logger?.LogInfo("[LAN TRACE] ENTER " + Describe(__originalMethod));
    public static void After(MethodBase __originalMethod, object __instance)
    {
        try
        {
            var name = __originalMethod.DeclaringType?.Name;
            var info = "";
            if (name == "ScamperServerConnectorPlayflow")
            {
                info = ReadProperties(__instance, "IsSingleplayer", "IsStartingSingleplayer", "IsHost", "IsServerInstance", "IsClientConnectedToServer");
            }
            else if (name is "ServerManager" or "ClientManager")
            {
                info = ReadProperties(__instance, "Started");
            }
            else if (name == "Tugboat" && __instance != null)
            {
                var method = __instance.GetType().GetMethod("GetPort", Type.EmptyTypes);
                if (method != null)
                {
                    info = " port=" + method.Invoke(__instance, null);
                }
            }
            logger?.LogInfo("[LAN TRACE] EXIT " + Describe(__originalMethod) + info);
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[LAN TRACE] Snapshot unavailable: " + ex.GetType().Name);
        }
    }

    private static string ReadProperties(object? instance, params string[] names)
    {
        if (instance == null)
        {
            return "";
        }

        return string.Concat(names.Select(n =>
        {
            try
            {
                var p = instance.GetType().GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return p?.PropertyType == typeof(bool) ? " " + n + "=" + p.GetValue(instance) : "";
            }
            catch
            {
                return " " + n + "=unavailable";
            }
        }));
    }

    public override bool Unload()
    {
        StopNativeClient();
        StopHostTest();
        FinishLanTransportCleanup();
        ShutDownDiscovery(null);
        harmony?.UnpatchSelf();
        return true;
    }
}
