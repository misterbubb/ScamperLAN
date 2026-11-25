using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using HarmonyLib;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private static string? joinAddress;
    private static MethodInfo? joinMethod, coroutineMethod;
    private static readonly Stopwatch joinClock = new();
    private static bool joinAttempted, joinSubmitted;
    private static readonly Stopwatch joinMonitor = new();
    private static int joinCheckpoint;
    private static readonly int[] joinCheckpoints = { 1, 5, 15, 30 };

    private static bool IsRadminAddress(string text) =>
        IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork &&
        ip.ToString() == text && ip.GetAddressBytes()[0] == 26;

    private static IEnumerable<string> LocalRadminAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
            (n.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase) ||
             n.Description.Contains("Radmin", StringComparison.OrdinalIgnoreCase)))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address.ToString()).Where(IsRadminAddress);

    private void SetupJoinTest()
    {
        var args = Environment.GetCommandLineArgs();
        var joins = args.Where(a => a.StartsWith("--scamperlan-join-test=", StringComparison.Ordinal)).ToArray();
        if (joins.Length == 0) return;
        if (args.Contains("--scamperlan-menu-host-test", StringComparer.Ordinal))
        { Log.LogError("[LAN JOIN] Cannot combine menu host and join tests."); return; }
        try
        {
            if (joins.Length != 1 || args.Contains("--scamperlan-host-test", StringComparer.Ordinal))
                throw new InvalidOperationException("Use exactly one join option and no host option.");
            var ip = joins[0].Substring("--scamperlan-join-test=".Length);
            var local = LocalRadminAddresses().ToArray();
            if (!IsRadminAddress(ip) || local.Length == 0 || local.Contains(ip, StringComparer.Ordinal))
                throw new InvalidOperationException("Join target must be another player's Radmin IPv4; an active local Radmin adapter is required.");
            var ct = Assembly.Load("BezbroGames.Scamper").GetType("ScamperServerConnectorPlayflow", true)!;
            joinMethod = Require(ct, "StartRemoteConnection", typeof(string), typeof(ushort));
            if (joinMethod.ReturnType != typeof(void) && joinMethod.ReturnType != typeof(bool))
            {
                if (joinMethod.ReturnType.FullName?.Contains("IEnumerator", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Unexpected StartRemoteConnection return type: " + joinMethod.ReturnType.FullName);
                var timing = ct.Assembly.GetType("MEC.Timing", true)!;
                coroutineMethod = timing.GetMethod("RunCoroutine", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { joinMethod.ReturnType }, null);
                if (coroutineMethod == null) throw new MissingMethodException("MEC.Timing.RunCoroutine not available for native return type.");
            }
            Log.LogInfo("[LAN JOIN] Native connection entry point return type: " + joinMethod.ReturnType.FullName);
            foreach (var name in new[] { "IsPlayerInLobby", "IsJoiningMultiplayer", "IsSingleplayer", "IsStartingSingleplayer", "IsClientConnectedToServer", "IsLobbyBrowserReady", "HasValidRemoteSession" })
                if (ct.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType != typeof(bool))
                    throw new MissingMemberException(ct.FullName, name);
            harmony!.Patch(Require(ct, "Update"), postfix: new HarmonyMethod(typeof(Plugin), nameof(JoinTick)));
            joinAddress = ip;
            Log.LogInfo($"[LAN JOIN] Armed for {ip}:{TestPort}; will attempt once when the game is idle and lobby services are ready. Do not enter an official lobby.");
        }
        catch (Exception ex) { joinAddress = null; Log.LogError("[LAN JOIN] Setup refused: " + ex); }
    }

    private static bool ReadJoinFlag(object instance, string name) => instance.GetType()
        .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) is bool b
        ? b : throw new MissingMemberException(name);

    private static string JoinSnapshot(object instance) => ReadProperties(instance,
        "IsLobbyBrowserReady", "IsPlayerInLobby", "IsJoiningMultiplayer", "IsSingleplayer",
        "IsStartingSingleplayer", "IsClientConnectedToServer", "HasValidRemoteSession");

    public static void JoinTick(object __instance)
    {
        if (joinAddress == null) return;
        if (joinAttempted)
        {
            if (joinSubmitted && joinCheckpoint < joinCheckpoints.Length &&
                joinMonitor.Elapsed.TotalSeconds >= joinCheckpoints[joinCheckpoint])
            {
                logger?.LogInfo("[LAN JOIN] Status after " + joinCheckpoints[joinCheckpoint++] + "s:" + JoinSnapshot(__instance));
            }
            return;
        }
        if (!joinClock.IsRunning) joinClock.Start();
        try
        {
            if (new[] { "IsPlayerInLobby", "IsJoiningMultiplayer", "IsSingleplayer", "IsStartingSingleplayer", "IsClientConnectedToServer", "HasValidRemoteSession" }
                .Any(n => ReadJoinFlag(__instance, n)))
            { joinAttempted = true; logger?.LogWarning("[LAN JOIN] Cancelled because a session is active or starting."); return; }
            if (joinClock.Elapsed.TotalSeconds > 120)
            { joinAttempted = true; logger?.LogWarning("[LAN JOIN] Timed out waiting for readiness; no connection attempted."); return; }
            if (joinClock.Elapsed.TotalSeconds < 20 || !ReadJoinFlag(__instance, "IsLobbyBrowserReady")) return;
            joinAttempted = true;
            logger?.LogInfo($"[LAN JOIN] Invoking native StartRemoteConnection for {joinAddress}:{TestPort}; no lobby creation or admission changes." + JoinSnapshot(__instance));
            var result = joinMethod!.Invoke(__instance, new object[] { joinAddress, TestPort });
            if (coroutineMethod != null)
            {
                if (result == null) throw new InvalidOperationException("StartRemoteConnection returned no coroutine.");
                coroutineMethod.Invoke(null, new[] { result });
            }
            else if (result is bool accepted && !accepted)
            {
                logger?.LogWarning("[LAN JOIN] Native connection request returned false; no retry.");
                return;
            }
            joinSubmitted = true;
            joinMonitor.Start();
            logger?.LogInfo("[LAN JOIN] Native connection request returned. Observing transport and admission events." + JoinSnapshot(__instance));
        }
        catch (Exception ex) { joinAttempted = true; logger?.LogError("[LAN JOIN] Failed: " + ex); }
    }
}
