using System;
using System.Reflection;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private static object? lanGuestMenuOwner, lanGuestMenuIdle;
    private static MethodInfo? hasReadyLanGuestPlayer, ensureLanGuestInput, updateLanGuestPlayerList;
    private static PropertyInfo? lanGuestPlayerListSingleton;

    private static void SetupLanGuestMenu(Type ui, Type connectorType)
    {
        hasReadyLanGuestPlayer = Require(connectorType, "HasReadyLocalPlayerForRemoteSession");
        ensureLanGuestInput = Require(ui, "EnsureUIInputEnabled");
        lanGuestMenuIdle = Enum.Parse(previewConnected!.GetType(), "MainMenu");
        var playerList = ui.Assembly.GetType("BezbroGames.Scamper.Networking.PlayerListManager", true)!;
        lanGuestPlayerListSingleton = playerList.GetProperty("Singleton", BindingFlags.Public | BindingFlags.Static);
        updateLanGuestPlayerList = Require(playerList, "UpdateUI");
    }

    private static bool IsLanGuestReady(object connection) =>
        (nativeIntentConnector != null && hasReadyLanGuestPlayer!.Invoke(nativeIntentConnector, null) is true) ||
        ReadMember(ReadMember(connection, "FirstObject"), "IsSpawned") is true;

    private static void UpdateLanGuestMenu(object ui)
    {
        var panel = ReadMember(ui, "clientMenu");
        if (panel == null || ReadMember(ui, "_lobbyUiState")?.ToString() == "Leaving") return;

        string? screen = ReadMember(ui, "currentScreenType")?.ToString();
        bool firstShow = lanGuestMenuOwner == null || !SameNativeInstance(lanGuestMenuOwner, ui);
        if (firstShow && screen is ("JoinCode" or "FindCode"))
        {
            previewNavigate!.Invoke(ui, new[] { previewMain });
            screen = ReadMember(ui, "currentScreenType")?.ToString();
        }

        // Spectate, settings and round result screens own the display while open.
        if (screen != "MainMenu") return;

        bool needsMenu = firstShow || ReadMember(panel, "activeInHierarchy") is not true ||
            !Equals(ReadMember(ui, "_lobbyUiState"), previewConnected);
        if (needsMenu)
        {
            previewStopPolling!.Invoke(ui, null);
            ensureLanGuestInput!.Invoke(ui, null);
            previewSetState!.Invoke(ui, new[] { previewConnected });
            if (ReadMember(panel, "activeInHierarchy") is not true) return;

            lanGuestMenuOwner = ui;
            RefreshTvControls(ui);
            logger?.LogInfo(firstShow
                ? "[LAN NATIVE CLIENT] Local player ready; connected room menu opened."
                : "[LAN NATIVE CLIENT] Connected room menu restored.");
        }

        var code = ReadMember(ui, "joinCodeText");
        if (code != null && ReadMember(code, "text") as string != nativeRequestedCode)
        {
            WriteMember(code, "text", nativeRequestedCode);
        }
        if (needsMenu && lanGuestPlayerListSingleton?.GetValue(null) is { } players && IsUnityObjectAlive(players))
        {
            updateLanGuestPlayerList!.Invoke(players, null);
        }
    }

    private static void CloseLanGuestMenu()
    {
        var ui = lanGuestMenuOwner;
        lanGuestMenuOwner = null;
        try
        {
            if (ui != null && IsUnityObjectAlive(ui) && Equals(ReadMember(ui, "_lobbyUiState"), previewConnected))
            {
                previewSetState!.Invoke(ui, new[] { lanGuestMenuIdle });
                RefreshTvControls(ui);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[LAN NATIVE CLIENT] Could not reset the room menu: " + ex.GetBaseException().Message);
        }
    }
}
