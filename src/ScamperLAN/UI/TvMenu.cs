using System;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private static MethodInfo? soloStart;
    private static bool menuRequested, menuStarted;
    private static object? menuUi;
    private static readonly Stopwatch menuClock = new();
    private static long nextMenuPoll;
    private static string? lastMenuState;
    private static bool menuUiError;
    private static bool previewEnabled, previewAttempted;
    private static MethodInfo? previewNavigate, previewSetState, previewStopPolling;
    private static object? previewMain, previewConnected;
    private static string? lastPreviewState;
    private static Type? previewSyncType;
    private static MethodInfo? previewApplyPrivacy;
    private static bool previewPrivacyApplied;
    private static int? lastPreviewCount;
    private static bool previewDetailsError;
    private static bool roundUiRunning, restoreAfterRound, lastReady, keypadStatusWritten;
    private static MethodInfo? refreshKeypad;
    private static readonly System.Collections.Generic.HashSet<string> reportedBlockedActions = new();
    private static string? lastRoundState, lastRoundUiEvent;

    private void SetupMenuHostTest()
    {
        if (!nativeMode && !Environment.GetCommandLineArgs().Contains("--scamperlan-menu-host-test", StringComparer.Ordinal))
        {
            return;
        }

        try
        {
            var game = Assembly.Load("BezbroGames.Scamper");
            var ui = game.GetType("BezbroGames.Scamper.UI.MainUIHandler", true)!;
            var method = Require(ui, "BeginJoinOrCreateByCode", typeof(string), typeof(bool));
            if (method.ReturnType != typeof(void))
            {
                throw new InvalidOperationException("Unexpected TV method return type.");
            }

            harmony!.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), nameof(MenuCodePrefix)));
            var ct = game.GetType("ScamperServerConnectorPlayflow", true)!;
            soloStart = Require(ct, "StartSingleplayer");
            if (soloStart.ReturnType != typeof(void))
            {
                throw new InvalidOperationException("Unexpected singleplayer entry point.");
            }

            harmony.Patch(Require(ui, "Update"), postfix: new HarmonyMethod(typeof(Plugin), nameof(MenuStatusTick)));
            foreach (var name in new[] { "JoinRandom", "RequestAutoJoinPreferredLobby" })
            {
                var entry = Require(ui, name);
                if (entry.ReturnType != typeof(void))
                {
                    throw new InvalidOperationException("Unexpected return type for " + name);
                }

                harmony.Patch(entry, prefix: new HarmonyMethod(typeof(Plugin), nameof(BlockOnlineEntry)));
            }
            menuRequested = requested;
            if (nativeMode || Environment.GetCommandLineArgs().Contains("--scamperlan-menu-preview", StringComparer.Ordinal))
            {
                SetupLocalPreview(ui, ct);
            }

            if (nativeMode)
            {
                SetupNativeMenu(ui, ct);
                return;
            }
            if (menuRequested)
            {
                Log.LogInfo($"[LAN MENU] Ready. Enter a six-character code on the TV to start a local host on {bindAddress}:{TestPort}; no online lobby or round is requested. The code is a test trigger, not a discovery code.");
            }
            else
            {
                Log.LogError("[LAN MENU] Host test is not armed. TV code submissions are blocked while menu-test mode is active.");
            }
        }
        catch (Exception ex)
        {
            menuRequested = false;
            Log.LogError("[LAN MENU] Setup failed; DO NOT use the TV. Remove test options and restart: " + ex);
        }
    }

    public static bool BlockOnlineEntry() => false;

    private void SetupLocalPreview(Type ui, Type ct)
    {
        if (!requested || (!nativeMode && bindAddress != "127.0.0.1"))
        {
            Log.LogWarning("[LAN PREVIEW] Requires a loopback host. Preview disabled.");
            return;
        }
        try
        {
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var screenType = ui.GetProperty("currentScreenType", f)?.PropertyType
                ?? throw new MissingMemberException(ui.FullName, "currentScreenType");
            previewNavigate = Require(ui, "ChangeToScreen", screenType);
            previewMain = Enum.Parse(screenType, "MainMenu");
            previewSetState = ui.GetMethods(f).Single(m => m.Name == "SetLobbyUiState" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
            previewConnected = Enum.Parse(previewSetState.GetParameters()[0].ParameterType, "Connected");
            previewStopPolling = Require(ui, "StopAutoJoinPolling");
            refreshKeypad = Require(ui, "RefreshJoinCodeUI");
            var gc = ui.Assembly.GetType("BezbroGames.Scamper.Core.GameController", true)!;
            foreach (var name in new[] { "IsRoundActiveOrSynced", "IsRoundStartTransitionActive" })
            {
                if (gc.GetProperty(name, f)?.PropertyType != typeof(bool))
                {
                    throw new MissingMemberException(gc.FullName, name);
                }
            }

            harmony!.Patch(Require(ui, "ApplyRoundStateUI", typeof(bool), typeof(bool)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(TrackRoundUi)));
            var guards = new[]{"StartAutoJoinPolling","RequestFindCodeLobbyList","CreateNewFindCode",
                "Leave","PromoteSubject","KickSubject","ReportSubject"}.Select(n => Require(ui, n)).ToList();
            guards.Add(Require(ct, "ChangeIsPublic", typeof(bool)));
            foreach (var m in guards)
            {
                if (m.ReturnType != typeof(void))
                {
                    throw new InvalidOperationException("Unexpected guard signature: " + m.Name);
                }

                harmony!.Patch(m, prefix: new HarmonyMethod(typeof(Plugin), nameof(BlockPreviewOnlineAction)));
            }
            previewSyncType = ui.Assembly.GetType("UISync", true)!;
            previewApplyPrivacy = Require(previewSyncType, "ApplyIsPublicSliderState", typeof(bool));
            var publicAction = Require(previewSyncType, "UpdateIsPublic", typeof(bool));
            var capacityAction = Require(previewSyncType, "ChangeMaximumPlayerCount", typeof(int));
            harmony!.Patch(previewApplyPrivacy, prefix: new HarmonyMethod(typeof(Plugin), nameof(PreviewPrivateVisual)));
            harmony.Patch(publicAction, prefix: new HarmonyMethod(typeof(Plugin), nameof(BlockPreviewOnlineAction)));
            harmony.Patch(capacityAction, prefix: new HarmonyMethod(typeof(Plugin), nameof(BlockPreviewOnlineAction)));
            previewEnabled = true;
            Log.LogInfo(nativeMode ? "[LAN PREVIEW] Radmin native host UI experiment; no cloud membership fabricated. Close game to leave." : "[LAN PREVIEW] Ready. Loopback-only native TV visual experiment; no cloud membership is fabricated. Close game to leave.");
        }
        catch (Exception ex)
        {
            previewEnabled = false;
            Log.LogError("[LAN PREVIEW] Setup failed; restart without preview: " + ex);
        }
    }

    public static bool BlockPreviewOnlineAction(MethodBase __originalMethod)
    {
        if (!previewEnabled && !nativeMode)
        {
            return true;
        }

        if (reportedBlockedActions.Add(__originalMethod.Name))
        {
            logger?.LogInfo("[LAN PREVIEW] Blocked unsupported action: " + __originalMethod.Name);
        }

        return false;
    }

    public static void PreviewPrivateVisual(ref bool __0)
    {
        if (previewEnabled && menuStarted && previewAttempted)
        {
            __0 = false;
        }
    }

    private static object? UiProperty(object obj, string name) => obj.GetType().GetProperty(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(obj);

    public static void TrackRoundUi(bool __0, bool __1)
    {
        if (!previewEnabled || !menuStarted)
        {
            return;
        }

        roundUiRunning = __0;
        if (__0 && previewAttempted)
        {
            restoreAfterRound = true;
        }

        string info = $"running={__0}; singleplayer={__1}; restorePending={restoreAfterRound}";
        if (info != lastRoundUiEvent)
        {
            lastRoundUiEvent = info;
            logger?.LogInfo("[LAN ROUND UI] " + info);
        }
    }

    private static void TryLocalPreview(object ui, bool ready)
    {
        if (!previewEnabled)
        {
            return;
        }

        if (!ready)
        {
            return;
        }

        var gc = UiProperty(ui, "gameController");
        var active = gc == null ? null : UiProperty(gc, "IsRoundActiveOrSynced");
        var transition = gc == null ? null : UiProperty(gc, "IsRoundStartTransitionActive");
        var screen = UiProperty(ui, "currentScreenType")?.ToString();
        if (previewAttempted && (active is true || transition is true))
        {
            restoreAfterRound = true;
        }

        string roundState = $"uiRunning={roundUiRunning}; active={active}; transition={transition}; screen={screen}; restorePending={restoreAfterRound}";
        if (roundState != lastRoundState)
        {
            lastRoundState = roundState;
            logger?.LogInfo("[LAN ROUND STATE] " + roundState);
        }
        bool initial = !previewAttempted;
        bool restore = previewAttempted && restoreAfterRound && !roundUiRunning && active is false && transition is false &&
            (screen == "MainMenu" || screen == "JoinCode");
        if (initial || restore)
        {
            previewAttempted = true;
            restoreAfterRound = false;
            try
            {
                previewStopPolling!.Invoke(ui, null);
                previewNavigate!.Invoke(ui, new[] { previewMain });
                previewSetState!.Invoke(ui, new[] { previewConnected });
                previewPrivacyApplied = false;
                logger?.LogInfo(restore ? "[LAN PREVIEW] Restored native lobby after round; session flags unchanged." :
                    "[LAN PREVIEW] Requested MainMenu and Connected visual state. Session flags unchanged.");
            }
            catch (Exception ex)
            {
                logger?.LogError("[LAN PREVIEW] Navigation failed: " + ex);
            }
        }
        if (!previewAttempted)
        {
            return;
        }

        var panel = UiProperty(ui, "clientMenu");
        string snapshot = $"screen={UiProperty(ui, "currentScreenType")}; clientMenu.activeSelf={(panel == null ? null : UiProperty(panel, "activeSelf"))}; clientMenu.activeInHierarchy={(panel == null ? null : UiProperty(panel, "activeInHierarchy"))}; ready={ready}";
        if (snapshot != lastPreviewState)
        {
            lastPreviewState = snapshot;
            logger?.LogInfo("[LAN PREVIEW STATE] " + snapshot);
        }
        if (roundUiRunning || active is true || transition is true || UiProperty(ui, "currentScreenType")?.ToString() != "MainMenu")
        {
            return;
        }

        try
        {
            if (!previewPrivacyApplied && previewSyncType != null)
            {
                var sync = previewSyncType.GetProperty("Singleton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                if (sync != null)
                {
                    previewApplyPrivacy!.Invoke(sync, new object[] { false });
                    previewPrivacyApplied = true;
                    logger?.LogInfo("[LAN PREVIEW] Public slider set visually off; public and capacity actions blocked.");
                }
            }
            var nm = connector == null ? null : UiProperty(connector, "NetworkManager");
            var sm = nm == null ? null : UiProperty(nm, "ServerManager");
            var clients = sm == null ? null : UiProperty(sm, "Clients");
            if (clients != null && UiProperty(clients, "Count") is int count)
            {
                if (lastPreviewCount != count)
                {
                    lastPreviewCount = count;
                    logger?.LogInfo($"[LAN PREVIEW] FishNet connected clients={count}; LAN capacity unverified.");
                }
            }
        }
        catch (Exception ex)
        {
            if (!previewDetailsError)
            {
                previewDetailsError = true;
                logger?.LogWarning("[LAN PREVIEW] Extra display probe failed: " + ex.Message);
            }
        }
        var code = UiProperty(ui, "joinCodeText");
        if (code != null)
        {
            var text = code.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (text?.CanWrite == true && text.PropertyType == typeof(string) && !string.Equals(text.GetValue(code) as string, nativeMode ? nativeHostCode : "LAN OK", StringComparison.Ordinal))
            {
                text.SetValue(code, nativeMode ? nativeHostCode : "LAN OK");
            }
        }
    }

    public static void MenuStatusTick(object __instance)
    {
        if (nativeMode && nativeClientRequested)
        {
            NativeClientTick(__instance);
            return;
        }
        if (!menuStarted || menuUi == null || connector == null || !menuClock.IsRunning || menuClock.ElapsedMilliseconds < nextMenuPoll)
        {
            return;
        }

        nextMenuPoll = menuClock.ElapsedMilliseconds + 500;
        try
        {
            string yakServer = testMultipass == null ? "NotObserved" : State(testMultipass, true, YakIndex);
            string yakClient = testMultipass == null ? "NotObserved" : State(testMultipass, false, YakIndex);
            string udp = testMultipass == null ? "NotObserved" : State(testMultipass, true, TugboatIndex);
            bool connected = ReadFlag("IsClientConnectedToServer");
            bool starting = ReadFlag("IsStartingSingleplayer");
            bool cloudLobby = ReadFlag("IsPlayerInLobby"), remoteSession = ReadFlag("HasValidRemoteSession");
            bool ready = !cloudLobby && !remoteSession && yakServer == "Started" && yakClient == "Started" && udp == "Started" && connected && !starting;
            string status = cloudLobby || remoteSession ? "LAN OFF" : ready ? "LAN OK" : (attempted && testMultipass == null ? "LAN OFF" : "LAN...");
            string snapshot = $"Yak server={yakServer}; Yak client={yakClient}; UDP={udp}; connected={connected}; starting={starting}; singleplayer={ReadFlag("IsSingleplayer")}; cloudLobby={cloudLobby}; remoteSession={remoteSession}; nativeClientMenu={ReadFlag("CanDisplayClientMenu")}";
            if (snapshot != lastMenuState)
            {
                lastMenuState = snapshot;
                logger?.LogInfo("[LAN MENU STATUS] " + status + " | " + snapshot);
            }
            if (lastReady && !ready)
            {
                logger?.LogWarning("[LAN MENU] Previously ready host lost readiness; restart the test if it disconnected.");
                if (previewEnabled && !cloudLobby && !remoteSession)
                {
                    var code = UiProperty(__instance, "joinCodeText");
                    var textProperty = code?.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (textProperty?.CanWrite == true && textProperty.PropertyType == typeof(string))
                    {
                        textProperty.SetValue(code, "LAN OFF");
                    }
                }
            }
            lastReady = ready;
            if (cloudLobby || remoteSession || (previewEnabled && ready))
            {
                if (keypadStatusWritten)
                {
                    keypadStatusWritten = false;
                    try
                    {
                        refreshKeypad?.Invoke(__instance, null);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning("[LAN MENU] Keypad refresh failed: " + ex.Message);
                    }
                }
                if (!cloudLobby && !remoteSession)
                {
                    TryLocalPreview(__instance, ready);
                }

                return;
            }
            if (previewEnabled && previewAttempted)
            {
                return;
            }

            TryLocalPreview(__instance, ready);
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var label = __instance.GetType().GetProperty("enteredJoinCodeText", flags)?.GetValue(__instance);
            if (label == null)
            {
                return;
            }

            var text = label.GetType().GetProperty("text", flags);
            if (text?.CanWrite == true && text.PropertyType == typeof(string) && !string.Equals(text.GetValue(label) as string, status, StringComparison.Ordinal))
            {
                text.SetValue(label, status);
                keypadStatusWritten = true;
            }
        }
        catch (Exception ex)
        {
            if (!menuUiError)
            {
                menuUiError = true;
                logger?.LogWarning("[LAN MENU STATUS] UI/state probe failed: " + ex.Message);
            }
        }
    }

    public static bool MenuCodePrefix(object __instance, string __0)
    {
        if (nativeMode)
        {
            if (!NativeCodeSubmission(__instance, __0))
            {
                return false;
            }
        }
        try
        {
            if (menuStarted)
            {
                logger?.LogInfo("[LAN MENU] Host request already submitted; check the status and do not resubmit.");
                return false;
            }
            if (!menuRequested || connector == null || soloStart == null)
            {
                logger?.LogWarning("[LAN MENU] Ignored code: host not ready.");
                return false;
            }
            if (string.IsNullOrEmpty(__0) || __0.Length != 6 || __0.Any(c => !(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z')))
            {
                logger?.LogWarning("[LAN MENU] Enter a six-character alphanumeric test code.");
                return false;
            }
            if (new[] { "IsPlayerInLobby", "IsJoiningMultiplayer", "IsSingleplayer", "IsStartingSingleplayer", "IsClientConnectedToServer", "HasValidRemoteSession" }.Any(ReadFlag) || !ReadFlag("CanStartSingleplayerSession"))
            {
                logger?.LogWarning("[LAN MENU] Refused: game connector not idle or cannot start a local session.");
                return false;
            }
            menuStarted = true;
            menuUi = __instance;
            menuClock.Restart();
            nextMenuPoll = 0;
            logger?.LogInfo("[LAN MENU] TV code intercepted locally. Calling connector.StartSingleplayer directly; no code registration, lobby creation, or round start requested.");
            soloStart.Invoke(connector, null);
            logger?.LogInfo("[LAN MENU] Local session requested. Remain in the intermission and check UDP port 7777.");
        }
        catch (Exception ex)
        {
            logger?.LogError("[LAN MENU] Host request failed; restart game before retry: " + ex);
        }
        return false;
    }
}
