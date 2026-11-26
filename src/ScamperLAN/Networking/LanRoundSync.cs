using System;
using System.Reflection;
using HarmonyLib;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private static bool lanRoundGateLogged, lanRoundGateErrorLogged;

    private void SetupRoundSync()
    {
        try
        {
            var game = Assembly.Load("BezbroGames.Scamper");
            var zone = game.GetType("StartZone", true)!;
            var barrier = zone.GetMethod("ShouldUseRoundStartLoadBarrier",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            if (barrier == null || !barrier.IsStatic || barrier.ReturnType != typeof(bool))
            {
                throw new MissingMethodException(zone.FullName, "ShouldUseRoundStartLoadBarrier() : bool");
            }

            harmony!.Patch(barrier, postfix: new HarmonyMethod(typeof(Plugin), nameof(LanRoundBarrierPostfix)));
            Log.LogInfo("[LAN ROUND SYNC] Native round barrier selector hook installed.");
        }
        catch (Exception ex)
        {
            Log.LogError("[LAN ROUND SYNC] Selector hook unavailable; vanilla round routing remains: " + ex);
            return;
        }

        try
        {
            var zone = Assembly.Load("BezbroGames.Scamper").GetType("StartZone", true)!;
            var capture = Require(zone, "CaptureRoundStartParticipants_Server");
            if (capture.ReturnType != typeof(void))
            {
                throw new InvalidOperationException("Unexpected participant capture signature.");
            }

            harmony!.Patch(capture, postfix: new HarmonyMethod(typeof(Plugin), nameof(LanRoundParticipantsPostfix)));
        }
        catch (Exception ex)
        {
            Log.LogWarning("[LAN ROUND SYNC] Participant trace unavailable: " + ex.Message);
        }
        Watch("BezbroGames.Scamper", "StartZone", "CommitStartGameServer",
            "BeginRoundStartBlackoutBarrier_Server", "BeginRoundStartLoadBarrier_Server",
            "RpcLogic___StartRoundStartVisualSequence_Client_508783514",
            "NotifyLocalRoundStartBlackoutReady", "NotifyLocalRoundStartLoadReady", "NotifyLocalRoundStartLoadFinished",
            "ReleaseRoundStartBlackoutBarrier_Server", "ReleaseRoundStartLoadBarrier_Server",
            "AbortRoundStartLoadAfterFailure");
        Watch("BezbroGames.Scamper", "BezbroGames.Scamper.Core.GameController", "LoadMap",
            "ApplyLocalRoundStateFromSync", "SetRoundStartObjectsReadyForReveal");
    }

    private static bool IsActiveRoundLanHost()
    {
        if (!lanModeSelected || !nativeReady || !requested || !menuStarted || !attempted || stopping ||
            nativeClientRequested || connector == null || testMultipass == null)
        {
            return false;
        }

        if (!ReadFlag("IsSingleplayer") || ReadFlag("IsStartingSingleplayer") ||
            ReadFlag("IsPlayerInLobby") || ReadFlag("HasValidRemoteSession") || !ReadFlag("IsClientConnectedToServer"))
        {
            return false;
        }

        if (State(testMultipass, true, YakIndex) != "Started" ||
            State(testMultipass, false, YakIndex) != "Started" ||
            State(testMultipass, true, TugboatIndex) != "Started" ||
            State(testMultipass, false, TugboatIndex) != "Stopped")
        {
            return false;
        }

        var nm = UiProperty(connector, "NetworkManager");
        var cm = nm == null ? null : UiProperty(nm, "ClientManager");
        var sm = nm == null ? null : UiProperty(nm, "ServerManager");
        var local = cm == null ? null : UiProperty(cm, "Connection");
        return sm != null && UiProperty(sm, "Started") is true &&
            local != null && UiProperty(local, "IsAuthenticated") is true;
    }

    public static void LanRoundBarrierPostfix(ref bool __result)
    {
        if (__result)
        {
            return;
        }

        try
        {
            if (!IsActiveRoundLanHost())
            {
                lanRoundGateLogged = false;
                return;
            }
            __result = true;
            if (!lanRoundGateLogged)
            {
                lanRoundGateLogged = true;
                logger?.LogInfo("[LAN ROUND SYNC] Active LAN host selected native multiplayer round barriers. Native authentication, participant selection and readiness checks unchanged.");
            }
        }
        catch (Exception ex)
        {
            if (!lanRoundGateErrorLogged)
            {
                lanRoundGateErrorLogged = true;
                logger?.LogWarning("[LAN ROUND SYNC] Host guard failed; native result preserved: " + ex.Message);
            }
        }
    }

    public static void LanRoundParticipantsPostfix(object __instance)
    {
        try
        {
            if (!IsActiveRoundLanHost())
            {
                return;
            }

            var ids = UiProperty(__instance, "_roundStartParticipantClientIds");
            var count = ids == null ? null : UiProperty(ids, "Count");
            var nm = UiProperty(connector!, "NetworkManager");
            var sm = nm == null ? null : UiProperty(nm, "ServerManager");
            var clients = sm == null ? null : UiProperty(sm, "Clients");
            var connected = clients == null ? null : UiProperty(clients, "Count");
            logger?.LogInfo($"[LAN ROUND SYNC] Native participant snapshot count={count ?? "unavailable"}; connected FishNet clients={connected ?? "unavailable"}. No participants added by mod.");
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[LAN ROUND SYNC] Participant snapshot unavailable: " + ex.Message);
        }
    }
}
