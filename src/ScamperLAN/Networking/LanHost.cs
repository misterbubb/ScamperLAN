using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private const int YakIndex = 0, TugboatIndex = 2;
    private const ushort TestPort = 7777;
    private static object? connector, testMultipass;
    private static bool attempted, stopping, requested;
    private static string bindAddress = "127.0.0.1";
    private static Type? multipassType, addressType;
    private static MethodInfo? stateMethod, bindMethod, portMethod, startMethod, stopMethod, getTransportMethod;

    private void SetupHostTest()
    {
        var args = Environment.GetCommandLineArgs();
        if (!lanSupportEnabled && !args.Contains("--scamperlan-host-test", StringComparer.Ordinal))
        {
            Log.LogInfo("[LAN HOST] Disabled; add --scamperlan-host-test to Steam launch options to opt in.");
            return;
        }
        if (!lanSupportEnabled && args.Any(a => a.StartsWith("--scamperlan-join-test=", StringComparison.Ordinal)))
        {
            Log.LogError("[LAN HOST] Cannot enable host and join modes together.");
            return;
        }
        var binds = lanSupportEnabled ? Array.Empty<string>() : args.Where(a => a.StartsWith("--scamperlan-bind=", StringComparison.Ordinal)).ToArray();
        if (lanSupportEnabled)
        {
            // Tugboat owns one IPv4 listener. Accepting rooms on multiple adapters requires a
            // wildcard bind, with subnet and route checks on the native admission callback.
            bindAddress = "0.0.0.0";
        }
        if (binds.Length > 1)
        {
            Log.LogError("[LAN HOST] Multiple bind options; refusing.");
            return;
        }
        if (binds.Length == 1)
        {
            var ip = binds[0].Substring("--scamperlan-bind=".Length);
            if (!IsSupportedLanAddress(ip) || !LocalLanAddresses().Contains(ip, StringComparer.Ordinal))
            {
                Log.LogError("[LAN HOST] Bind must be an IPv4 address assigned to an active supported private network adapter.");
                return;
            }
            bindAddress = ip;
        }
        try
        {
            var game = Assembly.Load("BezbroGames.Scamper");
            var fish = Assembly.Load("FishNet.Runtime");
            var ct = game.GetType("ScamperServerConnectorPlayflow", true)!;
            multipassType = fish.GetType("FishNet.Transporting.Multipass.Multipass", true)!;
            addressType = fish.GetType("FishNet.Transporting.IPAddressType", true)!;
            stateMethod = Require(multipassType, "GetConnectionState", typeof(bool), typeof(int));
            bindMethod = Require(multipassType, "SetServerBindAddress", typeof(string), addressType, typeof(int));
            portMethod = Require(multipassType, "SetPort", typeof(ushort), typeof(int));
            startMethod = Require(multipassType, "StartConnection", typeof(bool), typeof(int));
            stopMethod = Require(multipassType, "StopConnection", typeof(bool), typeof(int));
            getTransportMethod = Require(multipassType, "GetTransport", typeof(int));
            foreach (var n in new[] { "IsSingleplayer", "IsStartingSingleplayer", "IsPlayerInLobby", "IsClientConnectedToServer" })
            {
                if (ct.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.PropertyType != typeof(bool))
                {
                    throw new MissingMemberException(ct.FullName, n);
                }
            }

            harmony!.Patch(Require(ct, "Awake"), postfix: new HarmonyMethod(typeof(Plugin), nameof(CaptureConnector)));
            harmony.Patch(Require(ct, "Update"), postfix: new HarmonyMethod(typeof(Plugin), nameof(LanConnectorTick)));
            harmony.Patch(startMethod, postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterLocalStart)));
            harmony.Patch(Require(ct, "StopTransportConnections"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeCleanup)));
            harmony.Patch(Require(ct, "StopAllConnections"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeCleanup)));
            requested = true;
            Log.LogInfo($"[LAN HOST] Ready on UDP {TestPort}. LAN admission checks the local or Radmin route.");
        }
        catch (Exception ex)
        {
            requested = false;
            Log.LogError("[LAN HOST] Setup failed; no host test enabled: " + ex);
        }
    }

    private static MethodInfo Require(Type t, string name, params Type[] types) =>
        t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, types, null) ?? throw new MissingMethodException(t.FullName, name);

    public static void CaptureConnector(object __instance)
    {
        if (requested)
        {
            connector = __instance;
        }
    }
    private static bool ReadFlag(string name) => connector?.GetType().GetProperty(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(connector) is bool b
            ? b : throw new InvalidOperationException("Missing connector flag: " + name);
    private static string State(object mp, bool server, int index) =>
        stateMethod!.Invoke(mp, new object[] { server, index })?.ToString() ?? "unknown";

    public static void AfterLocalStart(object __instance, bool __0, int __1, bool __result)
    {
        if (!requested || attempted || stopping || lanTransportSettings != null ||
            __0 || __1 != YakIndex || !__result ||
            (lanSupportEnabled && (!lanModeSelected || !nativeReady || !menuStarted || nativeClientRequested)))
        {
            return;
        }

        try
        {
            if (!ReadFlag("IsSingleplayer") || !ReadFlag("IsStartingSingleplayer") || ReadFlag("IsPlayerInLobby") || !ReadFlag("IsClientConnectedToServer"))
            {
                logger?.LogWarning("[LAN HOST] Refused: not a fresh connected singleplayer session.");
                return;
            }
            if (State(__instance, true, YakIndex) != "Started" || State(__instance, false, YakIndex) != "Started" || State(__instance, true, TugboatIndex) != "Stopped" || State(__instance, false, TugboatIndex) != "Stopped")
            {
                logger?.LogWarning("[LAN HOST] Refused: unexpected transport state.");
                return;
            }
            attempted = true;
            var settings = new LanTransportSettings(__instance, server: true);
            lanTransportSettings = settings;
            testMultipass = __instance;

            var v4 = Enum.Parse(addressType!, "IPv4");
            var v6 = Enum.Parse(addressType!, "IPv6");
            bindMethod!.Invoke(__instance, new object[] { bindAddress, v4, TugboatIndex });
            bindMethod.Invoke(__instance, new object[] { "::1", v6, TugboatIndex });
            var getter = Require(settings.Tugboat.GetType(), "GetServerBindAddress", addressType!);
            if (!string.Equals(getter.Invoke(settings.Tugboat, new[] { v4 }) as string, bindAddress, StringComparison.Ordinal) ||
                !string.Equals(getter.Invoke(settings.Tugboat, new[] { v6 }) as string, "::1", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Bind address verification failed; no listener started.");
            }

            portMethod!.Invoke(__instance, new object[] { TestPort, TugboatIndex });
            logger?.LogInfo($"[LAN HOST] Requesting secondary Tugboat server on {bindAddress}:{TestPort}, leaving Yak client selected.");
            var result = startMethod!.Invoke(__instance, new object[] { true, TugboatIndex });
            logger?.LogInfo($"[LAN HOST] Start result={result}; Tugboat server={State(__instance, true, TugboatIndex)}; Yak server={State(__instance, true, YakIndex)}; Yak client={State(__instance, false, YakIndex)}");
            if (result is not true)
            {
                StopHostTest();
            }
        }
        catch (Exception ex)
        {
            logger?.LogError("[LAN HOST] Experiment failed: " + ex);
            StopHostTest();
        }
    }

    public static void BeforeCleanup()
    {
        StopNativeClient();
        StopHostTest();
    }
    public static void LanConnectorTick()
    {
        // Restore only after the native cleanup call and transport callbacks have returned.
        FinishLanTransportCleanup();
        TickLanHosting();
    }

    private static void StopHostTest()
    {
        discovery?.StopHosting();
        advertisedRoom = null;
        StopLanTransport(server: true);
    }
}
