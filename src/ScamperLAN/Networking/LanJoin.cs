using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
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
    private const string JoinArgumentPrefix = "--scamperlan-join-test=";
    private static readonly string[] JoinConnectorProperties =
    {
        "IsPlayerInLobby",
        "IsJoiningMultiplayer",
        "IsSingleplayer",
        "IsStartingSingleplayer",
        "IsClientConnectedToServer",
        "IsLobbyBrowserReady",
        "HasValidRemoteSession"
    };
    private static readonly string[] ActiveSessionProperties =
    {
        "IsPlayerInLobby",
        "IsJoiningMultiplayer",
        "IsSingleplayer",
        "IsStartingSingleplayer",
        "IsClientConnectedToServer",
        "HasValidRemoteSession"
    };

    private void SetupJoinTest()
    {
        var args = Environment.GetCommandLineArgs();
        var joins = args
            .Where(a => a.StartsWith(JoinArgumentPrefix, StringComparison.Ordinal))
            .ToArray();

        if (joins.Length == 0)
        {
            return;
        }

        if (args.Contains("--scamperlan-menu-host-test", StringComparer.Ordinal))
        {
            Log.LogError("[LAN JOIN] Cannot combine menu host and join tests.");
            return;
        }

        try
        {
            var ip = ValidateJoinRequest(args, joins);
            var connectorType = GetConnectorType();
            ConfigureJoinEntryPoint(connectorType);
            ValidateJoinConnectorProperties(connectorType);
            RegisterJoinTick(connectorType);

            joinAddress = ip;
            Log.LogInfo(
                $"[LAN JOIN] Armed for {ip}:{TestPort}; will attempt once when the game is idle " +
                "and lobby services are ready. Do not enter an official lobby.");
        }
        catch (Exception ex)
        {
            joinAddress = null;
            Log.LogError("[LAN JOIN] Setup refused: " + ex);
        }
    }

    private static string ValidateJoinRequest(string[] args, string[] joins)
    {
        if (joins.Length != 1 || args.Contains("--scamperlan-host-test", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Use exactly one join option and no host option.");
        }

        var ip = joins[0][JoinArgumentPrefix.Length..];
        var localAddresses = LocalLanAddresses().ToArray();
        if (!IsSupportedLanAddress(ip) || localAddresses.Length == 0 ||
            localAddresses.Contains(ip, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Join target must be another player's supported private IPv4 address. " +
                "Connect to the same private network first.");
        }

        return ip;
    }

    private static Type GetConnectorType() =>
        Assembly.Load("BezbroGames.Scamper").GetType("ScamperServerConnectorPlayflow", true)!;

    private static void ConfigureJoinEntryPoint(Type connectorType)
    {
        joinMethod = Require(connectorType, "StartRemoteConnection", typeof(string), typeof(ushort));
        logger?.LogInfo("[LAN JOIN] Native connection entry point return type: " + joinMethod.ReturnType.FullName);
        if (joinMethod.ReturnType == typeof(void) || joinMethod.ReturnType == typeof(bool))
        {
            return;
        }

        if (joinMethod.ReturnType.FullName?.Contains("IEnumerator", StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException(
                "Unexpected StartRemoteConnection return type: " + joinMethod.ReturnType.FullName);
        }

        var timing = connectorType.Assembly.GetType("MEC.Timing", true)!;
        coroutineMethod = timing.GetMethod(
            "RunCoroutine",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { joinMethod.ReturnType },
            null);

        if (coroutineMethod == null)
        {
            throw new MissingMethodException("MEC.Timing.RunCoroutine not available for native return type.");
        }
    }

    private static void ValidateJoinConnectorProperties(Type connectorType)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var name in JoinConnectorProperties)
        {
            var property = connectorType.GetProperty(name, flags);
            if (property?.PropertyType != typeof(bool))
            {
                throw new MissingMemberException(connectorType.FullName, name);
            }
        }
    }

    private void RegisterJoinTick(Type connectorType)
    {
        var update = Require(connectorType, "Update");
        harmony!.Patch(update, postfix: new HarmonyMethod(typeof(Plugin), nameof(JoinTick)));
    }

    private static bool ReadJoinFlag(object instance, string name) => instance.GetType()
        .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance) is bool b
        ? b : throw new MissingMemberException(name);

    private static string JoinSnapshot(object instance) => ReadProperties(instance,
        "IsLobbyBrowserReady", "IsPlayerInLobby", "IsJoiningMultiplayer", "IsSingleplayer",
        "IsStartingSingleplayer", "IsClientConnectedToServer", "HasValidRemoteSession");

    public static void JoinTick(object __instance)
    {
        if (joinAddress == null)
        {
            return;
        }

        if (joinAttempted)
        {
            if (joinSubmitted && joinCheckpoint < joinCheckpoints.Length &&
                joinMonitor.Elapsed.TotalSeconds >= joinCheckpoints[joinCheckpoint])
            {
                logger?.LogInfo("[LAN JOIN] Status after " + joinCheckpoints[joinCheckpoint++] + "s:" + JoinSnapshot(__instance));
            }
            return;
        }
        if (!joinClock.IsRunning)
        {
            joinClock.Start();
        }

        try
        {
            if (ActiveSessionProperties.Any(name => ReadJoinFlag(__instance, name)))
            {
                joinAttempted = true;
                logger?.LogWarning("[LAN JOIN] Cancelled because a session is active or starting.");
                return;
            }
            if (joinClock.Elapsed.TotalSeconds > 120)
            {
                joinAttempted = true;
                logger?.LogWarning("[LAN JOIN] Timed out waiting for readiness; no connection attempted.");
                return;
            }
            if (joinClock.Elapsed.TotalSeconds < 20 || !ReadJoinFlag(__instance, "IsLobbyBrowserReady"))
            {
                return;
            }

            joinAttempted = true;
            logger?.LogInfo(
                $"[LAN JOIN] Invoking native StartRemoteConnection for {joinAddress}:{TestPort}; " +
                "no lobby creation or admission changes." + JoinSnapshot(__instance));
            var result = joinMethod!.Invoke(__instance, new object[] { joinAddress, TestPort });
            if (coroutineMethod != null)
            {
                if (result == null)
                {
                    throw new InvalidOperationException("StartRemoteConnection returned no coroutine.");
                }

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
        catch (Exception ex)
        {
            joinAttempted = true;
            logger?.LogError("[LAN JOIN] Failed: " + ex);
        }
    }
}
