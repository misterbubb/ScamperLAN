using System;
using System.Linq;
using System.Reflection;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private static MethodInfo? setFindCodeJoinState, updateFindCodePagination, setFindCodeRowFullState;
    private static MethodInfo? resolveFindCodeEnvironment, getFindCodeFrequencyColor;
    private static Type? findCodeFrequencyBand;

    private static void SetupLanFindCodeView(Type ui)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        setFindCodeJoinState = Require(ui, "SetFindCodeJoinState", typeof(bool), typeof(bool));
        updateFindCodePagination = Require(ui, "UpdateFindCodePagination", typeof(int), typeof(int));
        resolveFindCodeEnvironment = ui.GetMethod("ResolveFindCodeEnvironmentText", instance)
            ?? throw new MissingMethodException(ui.FullName, "ResolveFindCodeEnvironmentText");
        setFindCodeRowFullState = ui.GetMethod("ApplyFindCodeRowFullState", instance)
            ?? throw new MissingMethodException(ui.FullName, "ApplyFindCodeRowFullState");
        getFindCodeFrequencyColor = ui.GetMethod("GetFindCodeFrequencyColor", statics)
            ?? throw new MissingMethodException(ui.FullName, "GetFindCodeFrequencyColor");
        findCodeFrequencyBand = getFindCodeFrequencyColor.GetParameters().Single().ParameterType;
    }

    public static bool SelectLanFindCodeListing(object __instance, int __0)
    {
        if (!lanModeSelected || !IsFindCodeScreen(__instance)) return true;
        if (lanFindCodeView is { } view && SameNativeInstance(view.Owner, __instance)) view.Select(__0);
        return false;
    }

    public static bool CycleLanFindCodePage(object __instance, bool __0)
    {
        if (!lanModeSelected || !IsFindCodeScreen(__instance)) return true;
        if (lanFindCodeView is { } view && SameNativeInstance(view.Owner, __instance)) view.Cycle(__0);
        return false;
    }

    private static void RenderLanRooms(object ui, LanOffer[] rooms)
    {
        if (lanFindCodeView == null || !SameNativeInstance(lanFindCodeView.Owner, ui))
        {
            lanFindCodeView = new LanFindCodeView(ui);
        }
        lanFindCodeView.Draw(rooms);
    }

    private sealed class LanFindCodeView
    {
        private sealed record Row(object Parent, object? Code, object? Count, object? Intensity,
            object? Environment, object? Frequency, object? Backdrop);

        private readonly Row[] rows;
        private readonly object? emptyPanel, selectedSprite, defaultSprite;
        private LanOffer[]? drawnRooms;
        private Guid[] visibleIds = Array.Empty<Guid>();
        private int page;
        private bool dirty = true;

        internal object Owner { get; }
        internal Guid SelectedId { get; private set; }

        internal LanFindCodeView(object ui)
        {
            Owner = ui;
            var parents = ReadMember(ui, "findCodeListingParents")
                ?? throw new InvalidOperationException("The TV has no Find Code row array.");
            var size = Convert.ToInt32(ReadMember(parents, "Length"));
            if (size <= 0) throw new InvalidOperationException("The TV has no Find Code row slots.");

            var codes = ReadMember(ui, "findCodeCodeTexts");
            var counts = ReadMember(ui, "findCodeCountTexts");
            var intensities = ReadMember(ui, "findCodeIntensityTexts");
            var frequencies = ReadMember(ui, "findCodeFrequencyRenderers");
            var backdrops = ReadMember(ui, "findCodeBackdropRenderers");
            rows = new Row[size];
            for (int index = 0; index < size; index++)
            {
                var parent = Element(parents, index) ?? throw new InvalidOperationException("A Find Code row slot is missing.");
                rows[index] = new Row(parent, Element(codes, index), Element(counts, index), Element(intensities, index),
                    resolveFindCodeEnvironment!.Invoke(ui, new[] { (object)index, parent }),
                    Element(frequencies, index), Element(backdrops, index));
            }
            emptyPanel = ReadMember(ui, "noJoinableCodesSubscreen");
            selectedSprite = ReadMember(ui, "clickedGradient");
            defaultSprite = ReadMember(ui, "defaultGradient");
        }

        internal void Select(int visibleIndex)
        {
            if ((uint)visibleIndex >= (uint)visibleIds.Length || visibleIds[visibleIndex] == SelectedId) return;
            SelectedId = visibleIds[visibleIndex];
            dirty = true;
        }

        internal void Cycle(bool next)
        {
            int total = Pages(drawnRooms?.Length ?? 0);
            if (total <= 1) return;
            page = (page + (next ? 1 : total - 1)) % total;
            SelectedId = Guid.Empty;
            dirty = true;
        }

        internal void Draw(LanOffer[] rooms)
        {
            if (!dirty && drawnRooms != null && drawnRooms.SequenceEqual(rooms)) return;

            // The generated generic interop wrapper marshals LobbyListEntry as a
            // reference, while the game's List stores a native value inline.
            // Keep discovered rooms managed and update only the TV's authored controls.
            int total = Pages(rooms.Length);
            page = Math.Min(page, total - 1);
            var visible = rooms.Skip(page * rows.Length).Take(rows.Length).ToArray();
            if (!visible.Any(offer => offer.Room.Id == SelectedId)) SelectedId = Guid.Empty;

            for (int index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                bool hasRoom = index < visible.Length;
                if (hasRoom) DrawRow(index, row, visible[index].Room);
                SetBrowserControl(row.Parent, hasRoom);
            }
            SetBrowserControl(emptyPanel, rooms.Length == 0);
            setFindCodeJoinState!.Invoke(Owner, new object[] { SelectedId != Guid.Empty, rooms.Length != 0 });
            WriteMember(Owner, "_findCodeCurrentPage", page);
            updateFindCodePagination!.Invoke(Owner, new object[] { rooms.Length, total });
            RefreshTvControls(Owner);
            if (drawnRooms?.Length != rooms.Length)
            {
                logger?.LogInfo($"[LAN ROOM] Find Code displays {rooms.Length} joinable LAN rooms.");
            }
            drawnRooms = rooms;
            visibleIds = visible.Select(offer => offer.Room.Id).ToArray();
            dirty = false;
        }

        private void DrawRow(int index, Row row, LanRoom room)
        {
            SetRowText(row.Code, room.Code);
            SetRowText(row.Count, $"{room.Players}/{room.Capacity}");
            SetRowText(row.Intensity, room.Intensity == 0 ? "-" : room.Intensity.ToString());
            SetRowText(row.Environment, string.Empty);

            var sprite = room.Id == SelectedId ? selectedSprite : defaultSprite;
            if (row.Backdrop != null && sprite != null) WriteMember(row.Backdrop, "sprite", sprite);
            if (row.Frequency != null)
            {
                if (getFindCodeFrequencyColor?.Invoke(null, new[] { Enum.ToObject(findCodeFrequencyBand!, room.FrequencyBand) }) is { } color)
                {
                    WriteMember(row.Frequency, "color", color);
                }
                WriteMember(row.Frequency, "enabled", room.FrequencyBand != 0);
            }
            setFindCodeRowFullState?.Invoke(Owner,
                new[] { (object)index, row.Parent, false, row.Code, row.Count, row.Intensity, row.Environment });
        }

        private int Pages(int count) => Math.Max(1, (count + rows.Length - 1) / rows.Length);

        private static object? Element(object? array, int index)
        {
            if (array is Array managed) return index < managed.Length ? managed.GetValue(index) : null;
            if (array == null || index >= Convert.ToInt32(ReadMember(array, "Length"))) return null;
            return array.GetType().GetProperty("Item", new[] { typeof(int) })?.GetValue(array, new object[] { index });
        }

        private static void SetBrowserControl(object? control, bool active)
        {
            if (control != null && !Equals(ReadMember(control, "activeSelf"), active)) SetActive(control, active);
        }

        private static void SetRowText(object? label, string text)
        {
            if (label != null && !Equals(ReadMember(label, "text"), text)) WriteMember(label, "text", text);
        }
    }
}
