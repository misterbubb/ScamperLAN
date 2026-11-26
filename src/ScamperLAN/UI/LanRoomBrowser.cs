using System;
using System.Linq;
using System.Reflection;
using System.Net;
using HarmonyLib;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private const int FindCodeScreenIndex = 14; // The TV prefab's serialized Find Code destination.
    private const int TypeCodeScreenIndex = 3;
    private const int TypedCodeDiscoveryMs = 2_300;
    private static LanDiscovery? discovery;
    private static Guid hostedLanRoom, lastLanJoin;
    private static LanRoom? advertisedRoom;
    private static long nextAdvertisement, nextAutoJoin;
    private static LanFindCodeView? lanFindCodeView;
    private static string? pendingLanCode;
    private static long pendingLanDeadline;
    private static object? pendingLanUi;
    private static (object Ui, int Index, bool UpdateNavigation)? pendingLanScreen;
    private static (object Ui, Guid RoomId)? pendingLanSelection;
    private static bool lanBrowserErrorLogged;
    private static MethodInfo? showLanError, getFrequencyLevel, generateRoomCode;
    private static MethodInfo? changeLobbyScreen;
    private static object?[]? hiddenPublicControls;
    private static bool[]? originalPublicControlStates;
    private static object? publicControlOwner;

    private void SetupLanRoomBrowser(Type ui, Type connectorType)
    {
        const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        SetupLanFindCodeView(ui);
        showLanError = Require(ui, "ShowMultiplayerConnectionError", typeof(string));
        getFrequencyLevel = ui.GetMethod("GetSelectedFrequencyLevel", statics, null, Type.EmptyTypes, null);
        generateRoomCode = ui.GetMethod("GenerateRandomJoinCode", statics, null, Type.EmptyTypes, null);
        changeLobbyScreen = Require(ui, "OnClickChangeToScreen", typeof(int));

        Prefix(Require(ui, "RequestCompatibleScreen", typeof(int), typeof(bool)), nameof(OpenLanLobbyScreen));
        Prefix(Require(ui, "StartFindCodeLobbyRefresh"), nameof(StartLanFindCode));
        Prefix(Require(ui, "RequestFindCodeLobbyList"), nameof(RequestOnlineFindCode));
        Prefix(Require(ui, "RenderFindCodeLobbyPage"), nameof(RequestOnlineFindCode));
        Prefix(Require(ui, "SelectFindCodeListing", typeof(int)), nameof(SelectLanFindCodeListing));
        Prefix(Require(ui, "CycleFindCodePage", typeof(bool)), nameof(CycleLanFindCodePage));
        Prefix(Require(ui, "CreateNewFindCode"), nameof(CreateLanRoom));
        var gameAuthenticator = connectorType.Assembly.GetType("BezbroGames.Scamper.Networking.ScamperNetworkAuthenticator", true)!;
        var admission = gameAuthenticator.GetMethods(instance).Single(m => m.Name == "OnRemoteConnection" &&
            m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType.FullName == "FishNet.Connection.NetworkConnection");
        Prefix(admission, nameof(AllowLanAdmission));

        void Prefix(MethodInfo method, string name) =>
            harmony!.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), name));
    }

    private static LanDiscovery Discovery() => discovery ??= new LanDiscovery();

    public static bool OpenLanLobbyScreen(object __instance, int __0, bool __1)
    {
        if (!lanModeSelected || !nativeReady || __0 is not (FindCodeScreenIndex or TypeCodeScreenIndex)) return true;
        pendingLanScreen = (__instance, __0, __1);
        return false;
    }

    private static void OpenPendingLanLobbyScreen(object ui)
    {
        if (pendingLanScreen is not { } request || !SameNativeInstance(request.Ui, ui)) return;
        pendingLanScreen = null;
        if (!lanModeSelected || !CanUseLanLobby()) return;

        try
        {
            // The VR click callback has returned. Let the stock screen transition finish
            // before the next Update fills the browser.
            changeLobbyScreen!.Invoke(ui, new object[] { request.Index });
            var opened = ReadMember(ui, "currentScreenType")?.ToString();
            if (request.UpdateNavigation)
            {
                if (ReadMember(ui, "typeCodeNavigation") is { } typeCode) SetActive(typeCode, request.Index == TypeCodeScreenIndex);
                if (ReadMember(ui, "findCodeNavigation") is { } findCode) SetActive(findCode, request.Index == FindCodeScreenIndex);
            }
            logger?.LogInfo("[LAN ROOM] TV navigation opened " + opened + ".");
        }
        catch (Exception ex)
        {
            LogDiscoveryWarning("Could not open the LAN screen: " + ex.GetBaseException().Message);
        }
    }

    public static bool StartLanFindCode()
    {
        if (!lanModeSelected) return true;
        lanFindCodeView = null;
        return false;
    }

    public static bool RequestOnlineFindCode() => !lanModeSelected;

    public static bool CreateLanRoom(object __instance)
    {
        if (!lanModeSelected) return true;
        StartLanRoom(__instance);
        return false;
    }

    private static void StartLanRoom(object ui, string? requestedCode = null)
    {
        if (!CanUseLanLobby() || !ReadFlag("CanStartSingleplayerSession"))
        {
            ReportLanError(ui, "Wait for the current session to finish before creating a LAN room.");
            return;
        }
        try
        {
            if (LanNetworks.FindInterfaces().Length == 0)
            {
                ReportLanError(ui, "Connect to your local network or Radmin before creating a LAN room.");
                return;
            }
            if (requestedCode == null)
            {
                var existingCodes = Discovery().Rooms().Select(offer => offer.Room.Code).ToHashSet(StringComparer.Ordinal);
                var gameCode = generateRoomCode?.Invoke(null, null) as string;
                requestedCode = LanDiscovery.ValidCode(gameCode) ? gameCode! : LanDiscovery.NewCode();
                while (existingCodes.Contains(requestedCode)) requestedCode = LanDiscovery.NewCode();
            }
            nativeHostCode = requestedCode;
            hostedLanRoom = Guid.NewGuid();
            pendingLanCode = null;
            pendingLanUi = null;
            menuStarted = true;
            menuUi = ui;
            menuClock.Restart();
            nextMenuPoll = nextAdvertisement = 0;
            logger?.LogInfo("[LAN ROOM] Creating room " + nativeHostCode + ".");
            soloStart!.Invoke(connector, null);
        }
        catch (Exception ex)
        {
            ResetLanHosting();
            menuStarted = false;
            menuUi = null;
            attempted = false;
            menuClock.Reset();
            StopHostTest();
            logger?.LogWarning("[LAN ROOM] Host creation failed: " + ex.GetBaseException().Message);
            ReportLanError(ui, "Could not create a LAN room.");
        }
    }

    public static bool JoinLanBrowserSelection(object __instance)
    {
        if (!lanModeSelected || !IsFindCodeScreen(__instance))
        {
            return true;
        }
        if (lanFindCodeView is { } view && SameNativeInstance(view.Owner, __instance) && view.SelectedId != Guid.Empty)
        {
            pendingLanSelection = (__instance, view.SelectedId);
        }
        return false;
    }

    private static bool IsFindCodeScreen(object ui) => ReadMember(ui, "currentScreenType")?.ToString() == "FindCode";

    private static bool CanUseLanLobby() => nativeReady && connector != null && !IsSessionActiveForModeSwitch();

    private static void TickLanBrowser(object ui)
    {
        try
        {
            UpdateLanPublicControls(ui);
            if (!lanModeSelected || !nativeReady || menuStarted) return;
            string? screen = ReadMember(ui, "currentScreenType")?.ToString();
            bool list = screen == "FindCode", keypad = screen == "JoinCode";
            if (!keypad && pendingLanUi != null && SameNativeInstance(pendingLanUi, ui))
            {
                pendingLanCode = null;
                pendingLanUi = null;
            }
            Discovery().Tick((list || keypad) && !nativeClientRequested, null, LogDiscoveryWarning);
            if (!list && !keypad)
            {
                lanFindCodeView = null;
                return;
            }

            var rooms = Discovery().Rooms();
            var joinable = list ? rooms.Where(offer => offer.Room.HasSpace).ToArray() : Array.Empty<LanOffer>();
            if (list) RenderLanRooms(ui, joinable);
            if (pendingLanSelection is { } selection && SameNativeInstance(selection.Ui, ui))
            {
                pendingLanSelection = null;
                if (list) JoinAdvertisedRoom(ui, selection.RoomId, showError: true);
                return;
            }
            if (pendingLanCode != null && pendingLanUi != null && SameNativeInstance(pendingLanUi, ui))
            {
                ResolveTypedLanCode(ui, rooms);
                return;
            }
            if (list && CanUseLanLobby() && Environment.TickCount64 >= nextAutoJoin &&
                ReadMember(ui, "_autoJoinEnabled") is true)
            {
                var candidate = joinable.FirstOrDefault(offer => offer.Room.Id != lastLanJoin);
                if (candidate.Room.Id != Guid.Empty)
                {
                    nextAutoJoin = Environment.TickCount64 + 4_000;
                    JoinAdvertisedRoom(ui, candidate.Room.Id, showError: false);
                }
            }
        }
        catch (Exception ex)
        {
            if (!lanBrowserErrorLogged)
            {
                lanBrowserErrorLogged = true;
                LogDiscoveryWarning("TV refresh failed: " + ex.GetBaseException().Message);
            }
        }
    }

    private static void QueueTypedLanCode(object ui, string? input)
    {
        if (IsFindCodeScreen(ui))
        {
            JoinLanBrowserSelection(ui);
            return;
        }
        string code = input?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!LanDiscovery.ValidCode(code) || !CanUseLanLobby())
        {
            ReportLanError(ui, "Enter a six-character LAN room code while disconnected.");
            return;
        }
        if (pendingLanCode == code && pendingLanUi != null && SameNativeInstance(pendingLanUi, ui)) return;
        pendingLanCode = code;
        pendingLanUi = ui;
        pendingLanDeadline = Environment.TickCount64 + TypedCodeDiscoveryMs;
    }

    private static void ResolveTypedLanCode(object ui, LanOffer[] rooms)
    {
        var code = pendingLanCode;
        if (code == null) return;
        var matches = rooms.Where(offer => offer.Room.Code == code).Take(2).ToArray();
        if (matches.Length == 0 && Environment.TickCount64 < pendingLanDeadline)
        {
            return;
        }
        pendingLanCode = null;
        pendingLanUi = null;
        if (matches.Length == 1)
        {
            JoinAdvertisedRoom(ui, matches[0].Room.Id, showError: true);
        }
        else if (matches.Length == 0)
        {
            StartLanRoom(ui, code);
        }
        else
        {
            ReportLanError(ui, "More than one LAN room has that code. Select the room from Find Code.");
        }
    }

    private static void JoinAdvertisedRoom(object ui, Guid id, bool showError)
    {
        if (!CanUseLanLobby()) return;
        var browser = Discovery();
        var offer = browser.Endpoints(id).FirstOrDefault(item => LanNetworks.HasLocalRoute(browser.Networks, item.Address));
        if (offer.Room.Id == Guid.Empty)
        {
            if (showError) ReportLanError(ui, "That LAN room is no longer available.");
            return;
        }
        if (!offer.Room.HasSpace)
        {
            if (showError) ReportLanError(ui, "That LAN room is full.");
            return;
        }
        lastLanJoin = offer.Room.Id;
        if (!StartDiscoveredLanClient(offer.Address, offer.Room.Code) && showError)
        {
            ReportLanError(ui, "Could not connect to that LAN room.");
        }
    }

    private static void ReportLanError(object ui, string message)
    {
        logger?.LogWarning("[LAN ROOM] " + message);
        try { showLanError?.Invoke(ui, new object[] { message }); }
        catch (Exception ex) { LogDiscoveryWarning(ex.GetBaseException().Message); }
    }

    private static void TickLanHosting()
    {
        if (!lanModeSelected || !menuStarted || hostedLanRoom == Guid.Empty || testMultipass == null ||
            lanTransportCleanupPending)
        {
            discovery?.StopHosting();
            advertisedRoom = null;
            return;
        }
        try
        {
            long now = Environment.TickCount64;
            if (now >= nextAdvertisement)
            {
                nextAdvertisement = now + 500;
                advertisedRoom = BuildHostedAdvertisement();
            }
            Discovery().Tick(false, advertisedRoom, LogDiscoveryWarning);
        }
        catch (Exception ex)
        {
            discovery?.StopHosting();
            if (!lanBrowserErrorLogged)
            {
                lanBrowserErrorLogged = true;
                LogDiscoveryWarning("Room advertising failed: " + ex.GetBaseException().Message);
            }
        }
    }

    private static LanRoom? BuildHostedAdvertisement()
    {
        if (State(testMultipass!, true, TugboatIndex) != "Started" || !ReadFlag("IsClientConnectedToServer") ||
            ReadFlag("IsStartingSingleplayer") || ReadFlag("IsPlayerInLobby") || ReadFlag("HasValidRemoteSession"))
        {
            return null;
        }
        var nm = UiProperty(connector!, "NetworkManager");
        var server = ReadMember(nm, "ServerManager");
        var clients = ReadMember(server, "Clients");
        int players = ReadMember(clients, "Count") is int count ? Math.Max(1, count) : 1;
        var tugboat = getTransportMethod!.Invoke(testMultipass, new object[] { TugboatIndex });
        int capacity = tugboat == null ? 4 : Convert.ToInt32(Require(tugboat.GetType(), "GetMaximumClients").Invoke(tugboat, null));
        capacity = Math.Clamp(capacity, 2, 64);

        object? slider = ReadMember(menuUi, "intensitySlider");
        byte intensity = ReadMember(slider, "value") is float value && float.IsFinite(value)
            ? (byte)Math.Clamp((int)Math.Round(value), 1, 10) : (byte)0;
        string? frequency = (ReadMember(getFrequencyLevel?.Invoke(null, null), "name") as string)?.ToUpperInvariant();
        byte band = frequency switch
        {
            "HIGH" => 1,
            "MID" or "MIDDLE" or "MEDIUM" => 2,
            "LOW" => 3,
            _ => 0
        };
        return new LanRoom(hostedLanRoom, nativeHostCode, (byte)Math.Min(players, capacity), (byte)capacity, intensity, band);
    }

    public static bool AllowLanAdmission(object __0)
    {
        if (!lanModeSelected || !menuStarted || hostedLanRoom == Guid.Empty ||
            ReadMember(__0, "TransportIndex") is not int index || index != TugboatIndex)
        {
            return true;
        }

        try
        {
            var connectionType = __0.GetType();
            var address = Require(connectionType, "GetAddress").Invoke(__0, null) as string;
            if (IPAddress.TryParse(address, out var peer) && LanNetworks.HasLocalRoute(LanNetworks.FindInterfaces(),
                peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer))
            {
                return true;
            }
            Require(connectionType, "Disconnect", typeof(bool)).Invoke(__0, new object[] { true });
            LogDiscoveryWarning("Rejected a connection outside the active local and Radmin networks.");
        }
        catch (Exception ex)
        {
            LogDiscoveryWarning("Could not validate the LAN peer: " + ex.GetBaseException().Message);
        }
        return false;
    }

    private static void UpdateLanPublicControls(object ui)
    {
        if (!lanModeSelected)
        {
            RestoreLanPublicControls();
            return;
        }
        if (publicControlOwner != null && !SameNativeInstance(publicControlOwner, ui))
        {
            RestoreLanPublicControls();
        }
        if (hiddenPublicControls == null)
        {
            var clientMenu = ReadMember(ui, "clientMenu");
            var transform = ReadMember(clientMenu, "transform");
            if (transform == null) return;
            var find = Require(transform.GetType(), "Find", typeof(string));
            hiddenPublicControls = new[] { "PublicCheckbox", "IsPublicBlocker", "OldUnused/publicFailedAttemptButton" }
                .Select(name => find.Invoke(transform, new object[] { name }))
                .Select(node => ReadMember(node, "gameObject"))
                .ToArray();
            originalPublicControlStates = hiddenPublicControls.Select(control => ReadMember(control, "activeSelf") is true).ToArray();
            publicControlOwner = ui;
        }
        bool changed = false;
        foreach (var control in hiddenPublicControls)
        {
            if (control != null && IsUnityObjectAlive(control) && ReadMember(control, "activeSelf") is true)
            {
                SetActive(control, false);
                changed = true;
            }
        }
        if (changed) RefreshTvControls(ui);
    }

    private static void RestoreLanPublicControls()
    {
        if (hiddenPublicControls == null) return;
        for (int i = 0; i < hiddenPublicControls.Length; i++)
        {
            if (hiddenPublicControls[i] is { } control && IsUnityObjectAlive(control))
            {
                SetActive(control, originalPublicControlStates![i]);
            }
        }
        if (publicControlOwner is { } ui && IsUnityObjectAlive(ui)) RefreshTvControls(ui);
        hiddenPublicControls = null;
        publicControlOwner = null;
        originalPublicControlStates = null;
    }

    private static void ResetLanHosting()
    {
        advertisedRoom = null;
        hostedLanRoom = Guid.Empty;
        nativeHostCode = string.Empty;
        discovery?.StopHosting();
    }

    private static void ShutDownDiscovery(object? ui)
    {
        if (ui != null)
        {
            try
            {
                // Draw every stock row when Online opens after LAN changed the shared controls.
                WriteMember(ui, "_findCodeRowsInitialized", false);
            }
            catch (Exception ex) { LogDiscoveryWarning(ex.GetBaseException().Message); }
        }
        discovery?.Dispose();
        discovery = null;
        pendingLanCode = null;
        pendingLanUi = null;
        pendingLanScreen = null;
        pendingLanSelection = null;
        lanFindCodeView = null;
        advertisedRoom = null;
        lastLanJoin = Guid.Empty;
        try { RestoreLanPublicControls(); }
        catch (Exception ex) { LogDiscoveryWarning(ex.GetBaseException().Message); }
    }

    private static void LogDiscoveryWarning(string message) => logger?.LogWarning("[LAN ROOM] " + message);
}
