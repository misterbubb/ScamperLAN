using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using HarmonyLib;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private static bool lanSupportEnabled, lanModeSelected, nativeReady, nativeClientRequested, nativeClientSubmitted;
    private static string nativeHostCode = "", nativeRequestedAddress = "";
    private static string nativeRequestedCode = "";
    private static bool nativeLocalHealthLogged, nativePendingHealthLogged, nativeEverAuthenticated;
    private const double AdmissionDeadlineSeconds = 45;
    private const string CloudConsistencyReason = "the periodic session consistency check found mismatched lobby and network state";
    private static object? nativeClientMultipass, nativeIntentConnector;
    private static MethodInfo? nativeSetSessionIntent;
    private static MethodInfo? nativeSelect, nativeSetAddress, nativeGetAddress, nativeGetPort, nativeRefresh;
    private static PropertyInfo? nativeEnteredCode, nativeClientTransport;
    private static readonly Stopwatch nativeClientClock = new();
    private static string? nativeLastClientState;
    private static long nativeNextPoll;
    private static bool nativeTimeoutLogged, nativeProbeErrorLogged;

    private void SetupNativeMenu(Type ui, Type ct)
    {
        try
        {
            if (!requested || !previewEnabled || multipassType == null)
            {
                throw new InvalidOperationException("Host transport or native UI setup failed.");
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            nativeSelect = Require(multipassType, "SetClientTransport", typeof(int));
            nativeSetAddress = Require(multipassType, "SetClientAddress", typeof(string), typeof(int));
            var transportType = multipassType.Assembly.GetType("FishNet.Transporting.Transport", true)!;
            nativeClientTransport = multipassType.GetProperty("ClientTransport", flags);
            nativeGetAddress = Require(transportType, "GetClientAddress");
            nativeGetPort = Require(transportType, "GetPort");
            if (nativeClientTransport?.PropertyType != transportType || !nativeClientTransport.CanWrite ||
                nativeGetAddress.ReturnType != typeof(string) || nativeGetPort.ReturnType != typeof(ushort) ||
                startMethod?.ReturnType != typeof(bool))
            {
                throw new InvalidOperationException("Unknown transport API signature.");
            }

            nativeSetSessionIntent = Require(ct, "set__shouldBeInSession", typeof(bool));
            if (nativeSetSessionIntent.ReturnType != typeof(void) ||
                ct.GetProperty("ShouldBeInSession", flags)?.PropertyType != typeof(bool))
            {
                throw new InvalidOperationException("Unknown session intent API.");
            }

            nativeEnteredCode = ui.GetProperty("currentEnteredJoinCode", flags);
            nativeRefresh = Require(ui, "RefreshJoinCodeUI");
            if (nativeEnteredCode?.PropertyType != typeof(string) || !nativeEnteredCode.CanWrite)
            {
                throw new MissingMemberException(ui.FullName, "currentEnteredJoinCode setter");
            }

            var recovery = Require(ct, "QueueSessionRecovery", typeof(string));
            var failure = Require(ct, "FailLobbyAttempt", typeof(string), typeof(string), typeof(bool));
            var showError = Require(ui, "ShowMultiplayerConnectionError", typeof(string));
            if (recovery.ReturnType != typeof(void) || failure.ReturnType != typeof(void) || showError.ReturnType != typeof(void))
            {
                throw new InvalidOperationException("Unexpected session recovery/error signatures.");
            }

            SetupLanRoomBrowser(ui, ct);
            SetupLanGuestMenu(ui, ct);
            harmony!.Patch(recovery, prefix: new HarmonyMethod(typeof(Plugin), nameof(NativeRecovery)));
            harmony.Patch(failure, prefix: new HarmonyMethod(typeof(Plugin), nameof(NativeFailureTrace)));
            harmony.Patch(showError, prefix: new HarmonyMethod(typeof(Plugin), nameof(NativeErrorTrace)));
            Log.LogInfo("[LAN RECOVERY TRACE] Recovery and failure hooks installed.");
            nativeReady = true;
            Log.LogInfo("[LAN NATIVE] Ready. Select LAN on the TV to open the local and Radmin room browser.");
        }
        catch (Exception ex)
        {
            nativeReady = false;
            Log.LogError("[LAN NATIVE] Setup refused; Online remains available. Check the LAN network adapter and restart: " + ex);
        }
    }

    private static bool StartDiscoveredLanClient(IPAddress endpoint, string code)
    {
        if (!CanUseLanLobby() || connector == null || !LanNetworks.HasLocalRoute(Discovery().Networks, endpoint))
        {
            return false;
        }
        string address = endpoint.ToString();
        try
        {
            var nm = UiProperty(connector, "NetworkManager") ?? throw new InvalidOperationException("NetworkManager unavailable.");
            var tm = UiProperty(nm, "TransportManager") ?? throw new InvalidOperationException("TransportManager unavailable.");
            var raw = UiProperty(tm, "Transport") ?? throw new InvalidOperationException("Transport unavailable.");
            object mp = NativeCastMultipass(raw);
            if (new[] { YakIndex, TugboatIndex }.Any(i => State(mp, true, i) != "Stopped" || State(mp, false, i) != "Stopped"))
            {
                throw new InvalidOperationException("Transport already active; restart before joining.");
            }

            var settings = new LanTransportSettings(mp, server: false);
            lanTransportSettings = settings;
            var tug = settings.Tugboat;
            nativeClientRequested = true;
            nativeRequestedAddress = address;
            nativeRequestedCode = code.ToUpperInvariant();
            nativeClientMultipass = mp;
            nativeEverAuthenticated = nativePendingHealthLogged = nativeLocalHealthLogged = false;
            nativeClientClock.Restart();
            nativeNextPoll = 0;
            nativeTimeoutLogged = nativeProbeErrorLogged = false;
            nativeLastClientState = null;
            nativeSelect!.Invoke(mp, new object[] { TugboatIndex });
            var selected = nativeClientTransport!.GetValue(mp);
            if (selected == null || !SameNativeInstance(selected, tug))
            {
                throw new InvalidOperationException("Could not select the LAN client transport.");
            }

            portMethod!.Invoke(mp, new object[] { TestPort, TugboatIndex });
            nativeSetAddress!.Invoke(mp, new object[] { address, TugboatIndex });
            if (!string.Equals(nativeGetAddress!.Invoke(tug, null) as string, address, StringComparison.Ordinal) ||
                nativeGetPort!.Invoke(tug, null) is not ushort port || port != TestPort)
            {
                throw new InvalidOperationException("Client endpoint verification failed.");
            }

            logger?.LogInfo($"[LAN NATIVE] TV requested client connection to {address}:{TestPort} (code {code.ToUpperInvariant()}); admission unchanged.");
            nativeIntentConnector = connector;
            nativeSetSessionIntent!.Invoke(nativeIntentConnector, new object[] { true });
            if (!ReadFlag("ShouldBeInSession"))
            {
                throw new InvalidOperationException("Session intent did not register.");
            }

            logger?.LogInfo("[LAN NATIVE] Local join intent registered; cloud membership and admission unchanged.");
            nativeClientSubmitted = startMethod!.Invoke(mp, new object[] { false, TugboatIndex }) is true;
            logger?.LogInfo("[LAN NATIVE] Client start result=" + nativeClientSubmitted);
            if (nativeClientSubmitted)
            {
                return true;
            }
            StopNativeClient();
        }
        catch (Exception ex)
        {
            logger?.LogError("[LAN NATIVE] Connection refused or failed: " + ex);
            if (nativeClientRequested)
            {
                StopNativeClient();
            }
        }
        return false;
    }

    public static bool NativeRecovery(object __instance, string __0)
    {
        if (!lanModeSelected || !nativeClientRequested)
        {
            return true;
        }

        try
        {
            logger?.LogInfo("[LAN RECOVERY TRACE] Enter reason=" + __0);
            if (!string.Equals(__0, CloudConsistencyReason, StringComparison.Ordinal))
            {
                return RecoveryFallback("reason differs from periodic consistency check");
            }

            if (!nativeClientSubmitted)
            {
                return RecoveryFallback("client start has not returned success");
            }

            if (nativeIntentConnector == null || nativeClientMultipass == null)
            {
                return RecoveryFallback("missing local join owner or transport");
            }

            if (!SameNativeInstance(__instance, nativeIntentConnector))
            {
                return RecoveryFallback("connector instance differs");
            }

            if (ReadJoinFlag(__instance, "IsPlayerInLobby"))
            {
                return RecoveryFallback("cloud lobby exists");
            }

            if (ReadJoinFlag(__instance, "HasValidRemoteSession"))
            {
                return RecoveryFallback("native remote session exists");
            }

            if (ReadJoinFlag(__instance, "IsSingleplayer"))
            {
                return RecoveryFallback("singleplayer session");
            }

            if (!ReadJoinFlag(__instance, "ShouldBeInSession"))
            {
                return RecoveryFallback("session intent already cleared");
            }

            if (!ReadJoinFlag(__instance, "IsClientConnectedToServer"))
            {
                return RecoveryFallback("connector does not report connected");
            }

            string clientState = State(nativeClientMultipass, false, TugboatIndex);
            string serverState = State(nativeClientMultipass, true, TugboatIndex);
            if (clientState != "Started" || serverState != "Stopped")
            {
                return RecoveryFallback("transport state: client=" + clientState + " server=" + serverState);
            }

            if (!IPAddress.TryParse(nativeRequestedAddress, out var peer) ||
                !LanNetworks.HasLocalRoute(Discovery().Networks, peer))
            {
                return RecoveryFallback("private LAN address or adapter changed");
            }

            var tug = getTransportMethod!.Invoke(nativeClientMultipass, new object[] { TugboatIndex });
            if (tug == null)
            {
                return RecoveryFallback("Tugboat unavailable");
            }

            string? target = nativeGetAddress!.Invoke(tug, null) as string;
            if (!string.Equals(target, nativeRequestedAddress, StringComparison.Ordinal))
            {
                return RecoveryFallback("endpoint differs: actual=" + target + " expected=" + nativeRequestedAddress);
            }

            var nm = UiProperty(__instance, "NetworkManager");
            var cm = nm == null ? null : UiProperty(nm, "ClientManager");
            var connection = cm == null ? null : UiProperty(cm, "Connection");
            bool authenticated = connection != null && UiProperty(connection, "IsAuthenticated") is true;
            if (!authenticated)
            {
                if (nativeEverAuthenticated || !nativeClientClock.IsRunning ||
                    nativeClientClock.Elapsed.TotalSeconds >= AdmissionDeadlineSeconds)
                {
                    return RecoveryFallback("admission grace unavailable: previouslyAuthenticated=" + nativeEverAuthenticated + " elapsed=" + nativeClientClock.Elapsed.TotalSeconds);
                }

                if (!nativePendingHealthLogged)
                {
                    nativePendingHealthLogged = true;
                    logger?.LogInfo("[LAN RECOVERY] Waiting for native FishNet admission; deferring cloud-only lobby consistency recovery for this pending LAN join (bounded deadline). Authentication unchanged.");
                }
                return false;
            }
            nativeEverAuthenticated = true;
            if (!nativeLocalHealthLogged)
            {
                nativeLocalHealthLogged = true;
                logger?.LogInfo("[LAN RECOVERY] Authenticated private LAN transport is healthy; using local session health instead of PlayFlow lobby recovery. No membership or admission flags changed.");
            }
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[LAN RECOVERY] Local health check failed; allowing native recovery: " + ex.Message);
            return true;
        }
    }

    private static bool RecoveryFallback(string reason)
    {
        logger?.LogWarning("[LAN RECOVERY TRACE] Allowing original recovery: " + reason);
        return true;
    }

    private static bool SameNativeInstance(object first, object second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        return UiProperty(first, "Pointer") is IntPtr a && a != IntPtr.Zero && UiProperty(second, "Pointer") is IntPtr b && a == b;
    }

    public static void NativeFailureTrace(string __0, string __1)
    {
        if (lanModeSelected && nativeClientRequested)
        {
            logger?.LogWarning("[LAN FAILURE] " + __0 + " | " + __1);
            logger?.LogInfo("[LAN FAILURE TRACE] " + Environment.StackTrace);
        }
    }

    public static void NativeErrorTrace(string __0)
    {
        if (lanModeSelected && nativeClientRequested)
        {
            logger?.LogWarning("[LAN UI ERROR] " + __0);
        }
    }

    private static object NativeCastMultipass(object raw)
    {
        if (multipassType!.IsInstanceOfType(raw))
        {
            return raw;
        }

        var cast = raw.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
        if (cast == null)
        {
            throw new InvalidOperationException("IL2CPP TryCast unavailable.");
        }

        var result = cast.MakeGenericMethod(multipassType).Invoke(raw, null);
        if (result == null || !multipassType.IsInstanceOfType(result))
        {
            throw new InvalidOperationException("Active transport is not Multipass.");
        }

        return result;
    }

    private static void NativeClientTick(object ui)
    {
        if (!nativeClientClock.IsRunning || nativeClientClock.ElapsedMilliseconds < nativeNextPoll)
        {
            return;
        }

        nativeNextPoll = nativeClientClock.ElapsedMilliseconds + 500;
        try
        {
            var state = nativeClientMultipass == null ? "Stopped" : State(nativeClientMultipass, false, TugboatIndex);
            var nm = connector == null ? null : UiProperty(connector, "NetworkManager");
            var cm = nm == null ? null : UiProperty(nm, "ClientManager");
            var connection = cm == null ? null : UiProperty(cm, "Connection");
            bool authenticated = connection != null && UiProperty(connection, "IsAuthenticated") is true;
            if (authenticated && state == "Started")
            {
                nativeEverAuthenticated = true;
                if (nativeClientSubmitted && IsLanGuestReady(connection!))
                {
                    UpdateLanGuestMenu(ui);
                }
            }

            if (state == "Stopped")
            {
                StopNativeClient();
                return;
            }

            string snapshot = $"UDP={state}; submitted={nativeClientSubmitted}; authenticated={authenticated}; " + JoinSnapshot(connector!) + ReadProperties(connector, "ShouldBeInSession");
            if (snapshot != nativeLastClientState)
            {
                nativeLastClientState = snapshot;
                logger?.LogInfo("[LAN NATIVE CLIENT] " + snapshot);
            }
            if (nativeClientClock.Elapsed.TotalSeconds >= AdmissionDeadlineSeconds && !nativeTimeoutLogged)
            {
                nativeTimeoutLogged = true;
                logger?.LogInfo("[LAN NATIVE CLIENT] 45s checkpoint: " + snapshot + "; inspect admission and player-spawn logs. Transport alone does not prove admission.");
                if (state == "Starting" || (state == "Started" && !authenticated))
                {
                    logger?.LogWarning("[LAN NATIVE CLIENT] Native admission did not finish within 45 seconds; stopping the LAN attempt.");
                    StopNativeClient();
                }
            }
        }
        catch (Exception ex)
        {
            if (!nativeProbeErrorLogged)
            {
                nativeProbeErrorLogged = true;
                logger?.LogWarning("[LAN NATIVE CLIENT] State probe failed: " + ex.Message);
            }
        }
    }

    private static void ReleaseNativeIntent()
    {
        var owner = nativeIntentConnector;
        if (owner == null)
        {
            return;
        }

        try
        {
            if (ReadJoinFlag(owner, "IsPlayerInLobby") || ReadJoinFlag(owner, "HasValidRemoteSession"))
            {
                logger?.LogWarning("[LAN NATIVE] Session ownership changed; leaving native intent untouched.");
                nativeIntentConnector = null;
                return;
            }
            nativeSetSessionIntent!.Invoke(owner, new object[] { false });
            if (ReadJoinFlag(owner, "ShouldBeInSession"))
            {
                throw new InvalidOperationException("Session intent remained set.");
            }

            nativeIntentConnector = null;
            nativeLocalHealthLogged = false;
            nativePendingHealthLogged = false;
            logger?.LogInfo("[LAN NATIVE] Local join intent released.");
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[LAN NATIVE] Intent cleanup failed; close game: " + ex.Message);
        }
    }

    private static void StopNativeClient()
    {
        if (nativeClientRequested)
        {
            nativeClientRequested = nativeClientSubmitted = false;
            CloseLanGuestMenu();
            nativeRequestedAddress = nativeRequestedCode = "";
            nativeEverAuthenticated = nativeLocalHealthLogged = nativePendingHealthLogged = false;
            nativeTimeoutLogged = nativeProbeErrorLogged = false;
            nativeClientClock.Reset();
            nativeLastClientState = null;
            nativeNextPoll = 0;
        }
        ReleaseNativeIntent();
        StopLanTransport(server: false);
    }
}
