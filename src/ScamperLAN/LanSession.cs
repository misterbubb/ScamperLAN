using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using HarmonyLib;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private const string CodeDigits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private static bool nativeMode, nativeReady, nativeClientRequested, nativeClientSubmitted;
    private static string nativeHostCode = "", nativeRequestedAddress = "";
    private static string nativeRequestedCode = "";
    private static bool nativeClientLobbyAttempted;
    private static bool nativeJoinCodePrefilled;
    private static bool nativeLocalHealthLogged, nativePendingHealthLogged, nativeEverAuthenticated;
    private const double AdmissionDeadlineSeconds = 45;
    private const string CloudConsistencyReason = "the periodic session consistency check found mismatched lobby and network state";
    private static object? nativeClientMultipass, nativeIntentConnector;
    private static MethodInfo? nativeSetSessionIntent;
    private static MethodInfo? nativeSelect, nativeSetAddress, nativeGetAddress, nativeGetPort, nativeRefresh;
    private static PropertyInfo? nativeEnteredCode;
    private static readonly Stopwatch nativeClientClock = new();
    private static string? nativeLastClientState;
    private static long nativeNextPoll;
    private static bool nativeTimeoutLogged, nativeProbeErrorLogged;

    private static string EncodeNativeCode(string address)
    {
        if (!IsRadminAddress(address)) throw new ArgumentException("Expected Radmin IPv4.");
        var b = IPAddress.Parse(address).GetAddressBytes();
        int value = (b[1] << 16) | (b[2] << 8) | b[3];
        var code = new char[6]; code[0] = 'L';
        for (int i = 5; i >= 1; i--) { code[i] = CodeDigits[value % 36]; value /= 36; }
        return new string(code);
    }

    private static bool TryDecodeNativeCode(string input, out string address)
    {
        address = "";
        if (input == null || input.Length != 6) return false;
        var code = input.ToUpperInvariant();
        if (code[0] != 'L') return false;
        int value = 0;
        for (int i = 1; i < 6; i++)
        {
            int digit = CodeDigits.IndexOf(code[i]);
            if (digit < 0) return false;
            value = value * 36 + digit;
        }
        if (value > 0xFFFFFF) return false;
        address = $"26.{value >> 16}.{(value >> 8) & 255}.{value & 255}";
        return IsRadminAddress(address);
    }

    private void SetupNativeMenu(Type ui, Type ct)
    {
        try
        {
            if (!requested || !previewEnabled || multipassType == null) throw new InvalidOperationException("Host transport or native UI setup failed.");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            nativeSelect = Require(multipassType, "SetClientTransport", typeof(int));
            nativeSetAddress = Require(multipassType, "SetClientAddress", typeof(string), typeof(int));
            var transportType = multipassType.Assembly.GetType("FishNet.Transporting.Transport", true)!;
            nativeGetAddress = Require(transportType, "GetClientAddress");
            nativeGetPort = Require(transportType, "GetPort");
            if (nativeGetAddress.ReturnType != typeof(string) || nativeGetPort.ReturnType != typeof(ushort) || startMethod?.ReturnType != typeof(bool))
                throw new InvalidOperationException("Unknown transport API signature.");
            nativeSetSessionIntent = Require(ct, "set__shouldBeInSession", typeof(bool));
            if (nativeSetSessionIntent.ReturnType != typeof(void) ||
                ct.GetProperty("ShouldBeInSession", flags)?.PropertyType != typeof(bool))
                throw new InvalidOperationException("Unknown session intent API.");
            nativeEnteredCode = ui.GetProperty("currentEnteredJoinCode", flags);
            nativeRefresh = Require(ui, "RefreshJoinCodeUI");
            if (nativeEnteredCode?.PropertyType != typeof(string) || !nativeEnteredCode.CanWrite)
                throw new MissingMemberException(ui.FullName, "currentEnteredJoinCode setter");
            var screenType = ui.GetProperty("currentScreenType", flags)?.PropertyType
                ?? throw new MissingMemberException(ui.FullName, "currentScreenType");
            if (!screenType.IsEnum) throw new InvalidOperationException("Unexpected TV screen type.");
            var changeScreen = Require(ui, "ChangeToScreen", screenType);
            if (changeScreen.ReturnType != typeof(void)) throw new InvalidOperationException("Unexpected TV screen navigation return type.");
            harmony!.Patch(changeScreen, postfix: new HarmonyMethod(typeof(Plugin), nameof(NativeJoinCodeScreenChanged)));
            var randomize = Require(ui, "RandomizeJoinCode");
            if (randomize.ReturnType != typeof(void)) throw new InvalidOperationException("Unexpected randomize method return type.");
            harmony!.Patch(randomize, prefix: new HarmonyMethod(typeof(Plugin), nameof(NativeRandomize)));
            if (!TryDecodeNativeCode(nativeHostCode, out var decoded) || decoded != bindAddress)
                throw new InvalidOperationException("Address code self-test failed.");
            var recovery = Require(ct, "QueueSessionRecovery", typeof(string));
            var failure = Require(ct, "FailLobbyAttempt", typeof(string), typeof(string), typeof(bool));
            var showError = Require(ui, "ShowMultiplayerConnectionError", typeof(string));
            if (recovery.ReturnType != typeof(void) || failure.ReturnType != typeof(void) || showError.ReturnType != typeof(void))
                throw new InvalidOperationException("Unexpected session recovery/error signatures.");
            harmony.Patch(recovery, prefix: new HarmonyMethod(typeof(Plugin), nameof(NativeRecovery)));
            harmony.Patch(failure, prefix: new HarmonyMethod(typeof(Plugin), nameof(NativeFailureTrace)));
            harmony.Patch(showError, prefix: new HarmonyMethod(typeof(Plugin), nameof(NativeErrorTrace)));
            Log.LogInfo("[LAN RECOVERY TRACE] Recovery and failure hooks installed.");
            nativeReady = true;
            Log.LogInfo($"[LAN NATIVE] Ready. Own host code: {nativeHostCode}. Type Code opens prefilled; Enter hosts, or replace the code to join. Randomize restores your code. No automatic join. Close game to leave.");
        }
        catch (Exception ex) { nativeReady = false; Log.LogError("[LAN NATIVE] Setup refused; do not use online controls. Disable LAN config and restart for vanilla: " + ex); }
    }

    public static bool NativeRandomize(object __instance)
    {
        if (!nativeMode) return true;
        if (!nativeReady || menuStarted || nativeClientRequested) return false;
        try { nativeEnteredCode!.SetValue(__instance, nativeHostCode); nativeRefresh!.Invoke(__instance, null); }
        catch (Exception ex) { logger?.LogError("[LAN NATIVE] Could not fill host code; type the logged code manually: " + ex.Message); }
        return false;
    }

    public static void NativeJoinCodeScreenChanged(object __instance, object __0)
    {
        if (!nativeMode || !nativeReady || nativeJoinCodePrefilled || __0?.ToString() != "JoinCode") return;
        nativeJoinCodePrefilled = true;
        try
        {
            var current = nativeEnteredCode?.GetValue(__instance) as string;
            if (!string.IsNullOrWhiteSpace(current)) return;
            nativeEnteredCode!.SetValue(__instance, nativeHostCode);
            nativeRefresh!.Invoke(__instance, null);
            logger?.LogInfo("[LAN NATIVE] Filled the empty TV code field with this PC's host code; replace it with another host's code to join.");
        }
        catch (Exception ex) { logger?.LogWarning("[LAN NATIVE] Could not prefill the TV code field: " + ex.Message); }
    }

    private static bool NativeCodeSubmission(object ui, string code)
    {
        if (!nativeReady || menuStarted || nativeClientRequested || connector == null)
        { logger?.LogWarning("[LAN NATIVE] Not ready or session already requested; close game to reset."); return false; }
        try
        {
            if (!TryDecodeNativeCode(code, out var address)) { logger?.LogWarning("[LAN NATIVE] Invalid LAN code; use the host's six-character L code."); return false; }
            if (!LocalRadminAddresses().Contains(bindAddress, StringComparer.Ordinal)) throw new InvalidOperationException("Radmin interface changed; restart the game.");
            if (!ReadFlag("IsLobbyBrowserReady") || new[] { "IsPlayerInLobby", "IsJoiningMultiplayer", "IsSingleplayer", "IsStartingSingleplayer", "IsClientConnectedToServer", "HasValidRemoteSession", "ShouldBeInSession" }.Any(ReadFlag))
                throw new InvalidOperationException("Game not idle or startup not ready. Wait until main menu; never join from an active session.");
            if (address == bindAddress) return true;
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.Connect(IPAddress.Parse(address), TestPort);
                if (probe.LocalEndPoint is not IPEndPoint ep || ep.Address.ToString() != bindAddress)
                    throw new InvalidOperationException("Route to host does not use Radmin.");
            }
            var nm = UiProperty(connector, "NetworkManager") ?? throw new InvalidOperationException("NetworkManager unavailable.");
            var tm = UiProperty(nm, "TransportManager") ?? throw new InvalidOperationException("TransportManager unavailable.");
            var raw = UiProperty(tm, "Transport") ?? throw new InvalidOperationException("Transport unavailable.");
            object mp = NativeCastMultipass(raw);
            if (new[] { YakIndex, TugboatIndex }.Any(i => State(mp, true, i) != "Stopped" || State(mp, false, i) != "Stopped"))
                throw new InvalidOperationException("Transport already active; restart before joining.");
            var tug = getTransportMethod!.Invoke(mp, new object[] { TugboatIndex }) ?? throw new InvalidOperationException("Tugboat missing.");
            nativeClientRequested = true;
            nativeRequestedAddress = address;
            nativeRequestedCode = code.ToUpperInvariant();
            nativeClientLobbyAttempted = false;
            nativeClientMultipass = mp;
            nativeEverAuthenticated = nativePendingHealthLogged = nativeLocalHealthLogged = false;
            nativeClientClock.Restart();
            nativeNextPoll = 0;
            nativeTimeoutLogged = nativeProbeErrorLogged = false;
            nativeLastClientState = null;
            nativeSelect!.Invoke(mp, new object[] { TugboatIndex });
            portMethod!.Invoke(mp, new object[] { TestPort, TugboatIndex });
            nativeSetAddress!.Invoke(mp, new object[] { address, TugboatIndex });
            if (!string.Equals(nativeGetAddress!.Invoke(tug, null) as string, address, StringComparison.Ordinal) ||
                nativeGetPort!.Invoke(tug, null) is not ushort port || port != TestPort)
                throw new InvalidOperationException("Client endpoint verification failed.");
            logger?.LogInfo($"[LAN NATIVE] TV requested client connection to {address}:{TestPort} (code {code.ToUpperInvariant()}); admission unchanged.");
            nativeIntentConnector = connector;
            nativeSetSessionIntent!.Invoke(nativeIntentConnector, new object[] { true });
            if (!ReadFlag("ShouldBeInSession")) throw new InvalidOperationException("Session intent did not register.");
            logger?.LogInfo("[LAN NATIVE] Local join intent registered; cloud membership and admission unchanged.");
            nativeClientSubmitted = startMethod!.Invoke(mp, new object[] { false, TugboatIndex }) is true;
            logger?.LogInfo("[LAN NATIVE] Client start result=" + nativeClientSubmitted);
            if (!nativeClientSubmitted) StopNativeClient();
        }
        catch (Exception ex)
        {
            logger?.LogError("[LAN NATIVE] Connection refused or failed: " + ex);
            if (nativeClientRequested) StopNativeClient();
        }
        return false;
    }

    public static bool NativeRecovery(object __instance, string __0)
    {
        if (!nativeMode || !nativeClientRequested) return true;
        try
        {
            logger?.LogInfo("[LAN RECOVERY TRACE] Enter reason=" + __0);
            if (!string.Equals(__0, CloudConsistencyReason, StringComparison.Ordinal))
                return RecoveryFallback("reason differs from periodic consistency check");
            if (!nativeClientSubmitted) return RecoveryFallback("client start has not returned success");
            if (nativeIntentConnector == null || nativeClientMultipass == null) return RecoveryFallback("missing local join owner or transport");
            if (!SameNativeInstance(__instance, nativeIntentConnector)) return RecoveryFallback("connector instance differs");
            if (ReadJoinFlag(__instance, "IsPlayerInLobby")) return RecoveryFallback("cloud lobby exists");
            if (ReadJoinFlag(__instance, "HasValidRemoteSession")) return RecoveryFallback("native remote session exists");
            if (ReadJoinFlag(__instance, "IsSingleplayer")) return RecoveryFallback("singleplayer session");
            if (!ReadJoinFlag(__instance, "ShouldBeInSession")) return RecoveryFallback("session intent already cleared");
            if (!ReadJoinFlag(__instance, "IsClientConnectedToServer")) return RecoveryFallback("connector does not report connected");
            string clientState = State(nativeClientMultipass, false, TugboatIndex);
            string serverState = State(nativeClientMultipass, true, TugboatIndex);
            if (clientState != "Started" || serverState != "Stopped") return RecoveryFallback("transport state: client=" + clientState + " server=" + serverState);
            if (!IsRadminAddress(nativeRequestedAddress) || !LocalRadminAddresses().Contains(bindAddress, StringComparer.Ordinal)) return RecoveryFallback("Radmin address changed");
            var tug = getTransportMethod!.Invoke(nativeClientMultipass, new object[] { TugboatIndex });
            if (tug == null) return RecoveryFallback("Tugboat unavailable");
            string? target = nativeGetAddress!.Invoke(tug, null) as string;
            if (!string.Equals(target, nativeRequestedAddress, StringComparison.Ordinal)) return RecoveryFallback("endpoint differs: actual=" + target + " expected=" + nativeRequestedAddress);
            var nm = UiProperty(__instance, "NetworkManager");
            var cm = nm == null ? null : UiProperty(nm, "ClientManager");
            var connection = cm == null ? null : UiProperty(cm, "Connection");
            bool authenticated = connection != null && UiProperty(connection, "IsAuthenticated") is true;
            if (!authenticated)
            {
                if (nativeEverAuthenticated || !nativeClientClock.IsRunning ||
                    nativeClientClock.Elapsed.TotalSeconds >= AdmissionDeadlineSeconds) return RecoveryFallback("admission grace unavailable: previouslyAuthenticated=" + nativeEverAuthenticated + " elapsed=" + nativeClientClock.Elapsed.TotalSeconds);
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
        if (ReferenceEquals(first, second)) return true;
        return UiProperty(first, "Pointer") is IntPtr a && a != IntPtr.Zero && UiProperty(second, "Pointer") is IntPtr b && a == b;
    }

    public static void NativeFailureTrace(string __0, string __1)
    {
        if (nativeMode && nativeClientRequested)
        {
            logger?.LogWarning("[LAN FAILURE] " + __0 + " | " + __1);
            logger?.LogInfo("[LAN FAILURE TRACE] " + Environment.StackTrace);
        }
    }

    public static void NativeErrorTrace(string __0)
    {
        if (nativeMode && nativeClientRequested) logger?.LogWarning("[LAN UI ERROR] " + __0);
    }

    private static object NativeCastMultipass(object raw)
    {
        if (multipassType!.IsInstanceOfType(raw)) return raw;
        var cast = raw.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
        if (cast == null) throw new InvalidOperationException("IL2CPP TryCast unavailable.");
        var result = cast.MakeGenericMethod(multipassType).Invoke(raw, null);
        if (result == null || !multipassType.IsInstanceOfType(result)) throw new InvalidOperationException("Active transport is not Multipass.");
        return result;
    }

    private static void NativeClientTick(object ui)
    {
        if (!nativeClientClock.IsRunning || nativeClientClock.ElapsedMilliseconds < nativeNextPoll) return;
        nativeNextPoll = nativeClientClock.ElapsedMilliseconds + 500;
        try
        {
            var state = nativeClientMultipass == null ? "Stopped" : State(nativeClientMultipass, false, TugboatIndex);
            var nm = connector == null ? null : UiProperty(connector, "NetworkManager");
            var cm = nm == null ? null : UiProperty(nm, "ClientManager");
            var connection = cm == null ? null : UiProperty(cm, "Connection");
            bool authenticated = connection != null && UiProperty(connection, "IsAuthenticated") is true;
            if (authenticated && state == "Started") nativeEverAuthenticated = true;
            if (authenticated && state == "Started" && !nativeClientLobbyAttempted)
                ShowNativeClientLobby(ui);
            if (state == "Stopped") ReleaseNativeIntent();
            string snapshot = $"UDP={state}; submitted={nativeClientSubmitted}; authenticated={authenticated}; " + JoinSnapshot(connector!) + ReadProperties(connector, "ShouldBeInSession");
            if (snapshot != nativeLastClientState) { nativeLastClientState = snapshot; logger?.LogInfo("[LAN NATIVE CLIENT] " + snapshot); }
            if (nativeClientClock.Elapsed.TotalSeconds >= AdmissionDeadlineSeconds && !nativeTimeoutLogged)
            {
                nativeTimeoutLogged = true;
                logger?.LogInfo("[LAN NATIVE CLIENT] 45s checkpoint: " + snapshot + "; inspect admission and player-spawn logs. Transport alone does not prove admission.");
                if (state == "Starting" || (state == "Started" && !authenticated))
                {
                    logger?.LogWarning("[LAN NATIVE CLIENT] Native admission did not finish within 45 seconds; stopping the LAN attempt.");
                    StopNativeClient();
                    state = nativeClientMultipass == null ? "Stopping" : State(nativeClientMultipass, false, TugboatIndex);
                    authenticated = false;
                }
            }
        }
        catch (Exception ex) { if (!nativeProbeErrorLogged) { nativeProbeErrorLogged = true; logger?.LogWarning("[LAN NATIVE CLIENT] State probe failed: " + ex.Message); } }
    }

    private static void ShowNativeClientLobby(object ui)
    {
        nativeClientLobbyAttempted = true;
        if (!previewEnabled || previewNavigate == null || previewSetState == null ||
            previewStopPolling == null || previewMain == null || previewConnected == null)
        {
            logger?.LogWarning("[LAN NATIVE CLIENT] Native lobby navigation unavailable; leaving the existing TV screen unchanged.");
            return;
        }
        try
        {
            previewStopPolling.Invoke(ui, null);
            previewNavigate.Invoke(ui, new[] { previewMain });
            previewSetState.Invoke(ui, new[] { previewConnected });
            var code = UiProperty(ui, "joinCodeText");
            var text = code?.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (text?.CanWrite == true && text.PropertyType == typeof(string))
                text.SetValue(code, nativeRequestedCode);
            logger?.LogInfo("[LAN NATIVE CLIENT] Authenticated; opened the game's existing Connected lobby screen with the host's LAN code.");
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[LAN NATIVE CLIENT] Could not open the vanilla Connected lobby screen: " + ex.Message);
        }
    }

    private static void ReleaseNativeIntent()
    {
        var owner = nativeIntentConnector;
        if (owner == null) return;
        try
        {
            if (ReadJoinFlag(owner, "IsPlayerInLobby") || ReadJoinFlag(owner, "HasValidRemoteSession"))
            { logger?.LogWarning("[LAN NATIVE] Session ownership changed; leaving native intent untouched."); nativeIntentConnector = null; return; }
            nativeSetSessionIntent!.Invoke(owner, new object[] { false });
            if (ReadJoinFlag(owner, "ShouldBeInSession")) throw new InvalidOperationException("Session intent remained set.");
            nativeIntentConnector = null;
            nativeLocalHealthLogged = false;
            nativePendingHealthLogged = false;
            logger?.LogInfo("[LAN NATIVE] Local join intent released.");
        }
        catch (Exception ex) { logger?.LogWarning("[LAN NATIVE] Intent cleanup failed; close game: " + ex.Message); }
    }

    private static void StopNativeClient()
    {
        ReleaseNativeIntent();
        var mp = nativeClientMultipass;
        nativeClientMultipass = null;
        if (mp == null) return;
        try { if (State(mp, false, TugboatIndex) != "Stopped") stopMethod!.Invoke(mp, new object[] { false, TugboatIndex }); }
        catch (Exception ex) { logger?.LogWarning("[LAN NATIVE CLIENT] Stop failed; close game: " + ex.Message); }
    }
}
