using System;
using System.Linq;
using System.Reflection;

namespace ScamperLAN;

public sealed partial class Plugin
{
    private const string LanModeToggleObjectName = "ScamperLANModeSelector";
    private static MethodInfo? unityObjectExists;

    private static void EnsureLanModeToggle(object ui)
    {
        if (lanModeToggleRoot != null && IsUnityObjectAlive(lanModeToggleRoot))
        {
            bool show = !IsSessionActiveForModeSwitch();
            if (ReadMember(lanModeToggleRoot, "activeSelf") is bool active && active != show)
            {
                SetActive(lanModeToggleRoot, show);
                if (show && lanModeToggleUi != null)
                {
                    RefreshTvControls(lanModeToggleUi);
                }
            }

            SetLanModeToggleText();
            return;
        }

        if (lanModeToggleRoot != null)
        {
            lanModeToggleRoot = lanModeToggleText = lanModeToggleUi = null;
        }

        if (ReadMember(ui, "currentScreenType")?.ToString() != "MainMenu" ||
            (lanModeToggleUi != null && SameNativeInstance(ui, lanModeToggleUi)))
        {
            return;
        }

        lanModeToggleUi = ui;
        object? clone = null;
        try
        {
            var menuRoot = MainMenuScreenRoot(ui);
            var menuTransform = ReadMember(menuRoot, "transform")
                ?? throw new MissingMemberException(menuRoot.GetType().FullName, "transform");
            var joinOptionsTransform = FindTransform(menuTransform, "Join Options");
            var sourceTransform = FindTransform(joinOptionsTransform, "Find Code");
            var typeCodeTransform = FindTransform(joinOptionsTransform, "Type Code");
            clone = CloneTvControl(sourceTransform, joinOptionsTransform, LanModeToggleObjectName);
            PositionLanModeToggle(clone, joinOptionsTransform, sourceTransform, typeCodeTransform);
            var label = InstallTvButton(clone, LanModeToggleUnityAction, out float fontSize);
            lanModeToggleRoot = clone;
            lanModeToggleText = label;
            lanModeToggleFontSize = fontSize;
            SetLanModeToggleText();
            SetActive(clone, !IsSessionActiveForModeSwitch());
            RefreshTvControls(ui);
            logger?.LogInfo("[LAN UI] Added the Online/LAN selector with a Button on the visible control.");
        }
        catch (Exception ex)
        {
            lanModeToggleRoot = lanModeToggleText = null;
            if (clone != null)
            {
                SetActive(clone, false);
            }

            logger?.LogWarning("[LAN UI] Could not create the TV mode selector: " + ex.GetBaseException().Message);
        }
    }

    private static void RefreshTvControls(object ui)
    {
        try
        {
            // The physical touchscreen searches a cached list of Unity controls.
            var monitor = ReadMember(ui, "monitorScreen");
            if (monitor != null)
            {
                Require(monitor.GetType(), "RefreshUIControls").Invoke(monitor, null);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[LAN UI] Could not refresh the touchscreen control list: " + ex.GetBaseException().Message);
        }
    }

    private static bool IsUnityObjectAlive(object value)
    {
        if (unityObjectExists == null)
        {
            var unityObject = Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Object", true)!;
            unityObjectExists = unityObject.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static,
                null, new[] { unityObject }, null) ?? throw new MissingMethodException(unityObject.FullName, "op_Implicit");
        }

        return unityObjectExists.Invoke(null, new[] { value }) is true;
    }

    private static object MainMenuScreenRoot(object ui)
    {
        var screens = ReadMember(ui, "screens")
            ?? throw new MissingMemberException(ui.GetType().FullName, "screens");
        var countValue = ReadMember(screens, "Length") ?? ReadMember(screens, "Count");
        if (countValue == null)
        {
            throw new MissingMemberException(screens.GetType().FullName, "Length");
        }

        var screenType = ReadMember(ui, "currentScreenType")?.GetType()
            ?? throw new MissingMemberException(ui.GetType().FullName, "currentScreenType");
        var mainMenu = Enum.Parse(screenType, "MainMenu");
        var item = screens.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var getItem = item?.GetMethod ?? screens.GetType().GetMethod("get_Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(int) }, null);
        if (getItem == null)
        {
            throw new MissingMethodException(screens.GetType().FullName, "get_Item(Int32)");
        }

        var count = Convert.ToInt32(countValue);
        for (var index = 0; index < count; index++)
        {
            var screen = getItem.Invoke(screens, new object[] { index });
            if (screen != null && Equals(ReadMember(screen, "screenType"), mainMenu))
            {
                return ReadMember(screen, "screen")
                    ?? throw new MissingMemberException(screen.GetType().FullName, "screen");
            }
        }

        throw new InvalidOperationException("The TV MainMenu screen root was not found.");
    }

    private static object FindTransform(object parent, string childName)
    {
        var find = parent.GetType().GetMethod("Find", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException(parent.GetType().FullName, "Find(String)");
        return find.Invoke(parent, new object[] { childName })
            ?? throw new InvalidOperationException("The native MainMenu object was not found: " + childName);
    }

    private static void PositionLanModeToggle(object clone, object parent, object typeCodeTransform, object findCodeTransform)
    {
        var transform = ReadMember(clone, "transform")
            ?? throw new MissingMemberException(clone.GetType().FullName, "transform");
        var typePosition = TransformPositionToLocal(parent, typeCodeTransform);
        var findPosition = TransformPositionToLocal(parent, findCodeTransform);
        var typeHeight = RectHeight(typeCodeTransform);
        var findHeight = RectHeight(findCodeTransform);
        var cloneHeight = RectHeight(transform);
        var typeScale = Convert.ToSingle(ReadMember(ReadMember(typeCodeTransform, "localScale"), "y"));
        var findScale = Convert.ToSingle(ReadMember(ReadMember(findCodeTransform, "localScale"), "y"));
        var cloneScale = Convert.ToSingle(ReadMember(ReadMember(transform, "localScale"), "y"));
        var vectorType = typePosition.GetType();
        var x = 0f;
        var y = (Convert.ToSingle(ReadMember(typePosition, "y")) + Convert.ToSingle(ReadMember(findPosition, "y"))) / 2f -
            Math.Max(typeHeight * typeScale, findHeight * findScale) / 2f - cloneHeight * cloneScale / 2f - 54f;
        var z = Convert.ToSingle(ReadMember(typePosition, "z"));
        var moved = Activator.CreateInstance(vectorType, new object[] { x, y, z })
            ?? throw new InvalidOperationException("Could not build TV control position.");
        WriteMember(transform, "localPosition", moved);
        var setAsLastSibling = transform.GetType().GetMethod("SetAsLastSibling", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(transform.GetType().FullName, "SetAsLastSibling()");
        setAsLastSibling.Invoke(transform, null);
    }

    private static object CloneTvControl(object sourceTransform, object parent, string name)
    {
        var source = AsGameObject(sourceTransform)
            ?? throw new InvalidOperationException("The native TV control is not a GameObject.");
        var unityObject = Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Object", true)!;
        var instantiate = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "Instantiate" &&
                m.GetParameters().Length == 3 &&
                m.GetParameters()[0].ParameterType == unityObject &&
                m.GetParameters()[1].ParameterType.IsInstanceOfType(parent) &&
                m.GetParameters()[2].ParameterType == typeof(bool));
        var cloneValue = instantiate.Invoke(null, new[] { source, parent, (object)false })
            ?? throw new InvalidOperationException("TV control clone failed.");
        var clone = AsGameObject(cloneValue)
            ?? throw new InvalidOperationException("TV control clone is not a GameObject.");
        SetActive(clone, false);
        WriteMember(clone, "name", name);
        return clone;
    }

    private static object InstallTvButton(object clone, Action action, out float fontSize)
    {
        var buttonType = Assembly.Load("UnityEngine.UI").GetType("UnityEngine.UI.Button", true)!;
        var originalButton = GetComponentInChildren(clone, buttonType)
            ?? throw new InvalidOperationException("Cloned TV control has no Button component.");
        var hitImage = ReadMember(originalButton, "targetGraphic")
            ?? throw new InvalidOperationException("Cloned TV control has no hit image.");
        // Make hits on the label and the child image reach the same button.
        WriteMember(originalButton, "enabled", false);
        WriteMember(hitImage, "raycastTarget", true);
        var renderer = ReadMember(hitImage, "canvasRenderer")
            ?? throw new InvalidOperationException("Cloned TV control has no CanvasRenderer.");
        WriteMember(renderer, "cullTransparentMesh", false);
        var button = GetUnityComponent(clone, buttonType, "AddComponent")
            ?? throw new InvalidOperationException("Could not add the TV Button.");
        WriteMember(button, "targetGraphic", hitImage);

        var label = FindRootTextComponent(clone)
            ?? throw new InvalidOperationException("The cloned control has no editable root text label.");
        fontSize = Convert.ToSingle(ReadMember(label, "fontSize"));
        WriteMember(label, "enableWordWrapping", false);
        WriteMember(label, "enableAutoSizing", true);
        WriteMember(label, "fontSizeMin", fontSize * 0.55f);
        WriteMember(label, "fontSizeMax", fontSize);
        WriteMember(label, "raycastTarget", true);
        AttachTvButtonListener(button, action);
        return label;
    }

    private static void AttachTvButtonListener(object button, Action action)
    {
        var clickEvent = ReadMember(button, "onClick")
            ?? throw new MissingMemberException(button.GetType().FullName, "onClick");
        var unityActionType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEngine.Events.UnityAction", false))
            .FirstOrDefault(t => t != null)
            ?? Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.Events.UnityAction", true)!;
        var runtime = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Il2CppInterop.Runtime")
            ?? Assembly.Load("Il2CppInterop.Runtime");
        var convert = runtime.GetType("Il2CppInterop.Runtime.DelegateSupport", true)!
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Delegate));
        var il2CppAction = convert.MakeGenericMethod(unityActionType).Invoke(null, new object[] { action })
            ?? throw new InvalidOperationException("Could not create the native TV click callback.");
        var addListener = clickEvent.GetType().GetMethod("AddListener", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { unityActionType }, null)
            ?? throw new MissingMethodException(clickEvent.GetType().FullName, "AddListener(UnityAction)");
        addListener.Invoke(clickEvent, new[] { il2CppAction });
    }

    private static object TransformPositionToLocal(object parent, object child)
    {
        var worldPosition = ReadMember(child, "position")
            ?? throw new MissingMemberException(child.GetType().FullName, "position");
        var inverse = parent.GetType().GetMethod("InverseTransformPoint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { worldPosition.GetType() }, null)
            ?? throw new MissingMethodException(parent.GetType().FullName, "InverseTransformPoint(Vector3)");
        return inverse.Invoke(parent, new[] { worldPosition })
            ?? throw new InvalidOperationException("Could not map TV control position into the selector parent.");
    }

    private static float RectHeight(object transform)
    {
        var rectTransformType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEngine.RectTransform", false))
            .FirstOrDefault(t => t != null)
            ?? Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.RectTransform", true)!;
        var rectTransform = CastIl2CppObject(transform, rectTransformType)
            ?? throw new InvalidOperationException("TV navigation control does not use a RectTransform.");
        var rect = ReadMember(rectTransform, "rect")
            ?? throw new MissingMemberException(rectTransform.GetType().FullName, "rect");
        if (ReadMember(rect, "height") is not float height || height <= 0f)
        {
            throw new InvalidOperationException("TV navigation control has no positive layout height.");
        }

        return height;
    }

    private static object? FindRootTextComponent(object gameObject)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Unity.TextMeshPro");
        var textType = assembly?.GetType("TMPro.TMP_Text", false);
        if (textType != null)
        {
            var component = GetComponent(gameObject, textType);
            if (component != null && ReadMember(component, "text") is string)
                return component;
        }

        return null;
    }

    private static object? GetComponent(object gameObject, Type componentType) =>
        GetUnityComponent(gameObject, componentType, "GetComponent");

    private static object? GetUnityComponent(object gameObject, Type componentType, string methodName)
    {
        var method = gameObject.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.FullName == "Il2CppSystem.Type");
        if (method == null)
            return null;

        var runtime = Assembly.Load("Il2CppInterop.Runtime");
        var il2CppType = runtime.GetType("Il2CppInterop.Runtime.Il2CppType", true)!;
        var from = il2CppType.GetMethod("From", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(Type) }, null)
            ?? throw new MissingMethodException(il2CppType.FullName, "From(System.Type)");
        var typeArgument = from.Invoke(null, new object[] { componentType });
        var component = method.Invoke(gameObject, new[] { typeArgument });
        return component == null ? null : CastIl2CppObject(component, componentType);
    }

    private static object? GetComponentInChildren(object gameObject, Type componentType)
    {
        var method = gameObject.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetComponentInChildren" &&
                m.GetParameters().Length == 2 &&
                m.GetParameters()[1].ParameterType == typeof(bool));
        if (method == null)
        {
            return null;
        }

        object? typeArgument;
        var parameterType = method.GetParameters()[0].ParameterType;
        if (parameterType == typeof(Type))
        {
            typeArgument = componentType;
        }
        else if (parameterType.FullName == "Il2CppSystem.Type")
        {
            var runtime = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Il2CppInterop.Runtime")
                ?? Assembly.Load("Il2CppInterop.Runtime");
            var il2CppType = runtime.GetType("Il2CppInterop.Runtime.Il2CppType", true)!;
            var from = il2CppType.GetMethod("From", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Type) }, null)
                ?? throw new MissingMethodException(il2CppType.FullName, "From(System.Type)");
            typeArgument = from.Invoke(null, new object[] { componentType });
        }
        else
        {
            return null;
        }

        var component = method.Invoke(gameObject, new[] { typeArgument!, (object)true });
        return component == null ? null : CastIl2CppObject(component, componentType) ?? component;
    }

    private static object? AsGameObject(object value)
    {
        var gameObjectType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("UnityEngine.GameObject", false))
            .FirstOrDefault(t => t != null)
            ?? Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.GameObject", true)!;
        var casted = CastIl2CppObject(value, gameObjectType);
        if (casted != null)
        {
            return casted;
        }

        return ReadMember(value, "gameObject");
    }

    private static object? CastIl2CppObject(object value, Type targetType)
    {
        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        var cast = value.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
        if (cast == null)
        {
            return null;
        }

        try
        {
            return cast.MakeGenericMethod(targetType).Invoke(value, null);
        }
        catch
        {
            return null;
        }
    }

    private static object? ReadMember(object? instance, string name)
    {
        if (instance == null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = instance.GetType();
        return (object?)type.GetProperty(name, flags)?.GetValue(instance) ?? type.GetField(name, flags)?.GetValue(instance);
    }

    private static void WriteMember(object instance, string name, object value)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = instance.GetType();
        var property = type.GetProperty(name, flags);
        if (property?.CanWrite == true)
        {
            property.SetValue(instance, value);
            return;
        }

        var field = type.GetField(name, flags) ?? throw new MissingMemberException(type.FullName, name);
        field.SetValue(instance, value);
    }

    private static void SetActive(object gameObject, bool active)
    {
        var method = gameObject.GetType().GetMethod("SetActive", BindingFlags.Public | BindingFlags.Instance,
            null, new[] { typeof(bool) }, null);
        method?.Invoke(gameObject, new object[] { active });
    }

    private static void SetLanModeToggleText()
    {
        if (lanModeToggleText != null)
        {
            var label = lanModeSelected ? "LAN" : "ONLINE";
            if (!string.Equals(ReadMember(lanModeToggleText, "text") as string, label, StringComparison.Ordinal))
            {
                WriteMember(lanModeToggleText, "text", label);
                if (lanModeToggleFontSize > 0f)
                {
                    WriteMember(lanModeToggleText, "fontSize", lanModeToggleFontSize);
                }
            }
        }
    }

    private static bool IsSessionActiveForModeSwitch()
    {
        if (menuStarted || nativeClientRequested || attempted || lanTransportSettings != null)
        {
            return true;
        }

        if (connector == null)
        {
            return false;
        }

        try
        {
            return IsConnectorInSession();
        }
        catch
        {
            return true;
        }
    }

    private static bool IsConnectorInSession() =>
        ActiveSessionProperties.Any(ReadFlag) || ReadFlag("ShouldBeInSession");

    public static void LanModeToggleUnityAction()
    {
        if (lanModeToggleUi != null && ReadMember(lanModeToggleUi, "currentScreenType")?.ToString() == "MainMenu")
        {
            logger?.LogInfo("[LAN UI] Mode selector activated.");
            ToggleLanMode(lanModeToggleUi);
        }
    }

    private static void ToggleLanMode(object ui)
    {
        if (IsSessionActiveForModeSwitch())
        {
            logger?.LogWarning("[LAN UI] Leave the current session and wait for it to disconnect before switching modes.");
            return;
        }

        if (!lanModeSelected)
        {
            if (!nativeReady || connector == null)
            {
                logger?.LogWarning("[LAN UI] LAN networking is unavailable. Check the earlier setup log.");
                return;
            }

            try
            {
                onlineCodeBeforeLan = nativeEnteredCode!.GetValue(ui) as string;
                lanModeSelected = true;
                previewStopPolling!.Invoke(ui, null);
                stopFindCodeRefresh!.Invoke(ui, null);
            }
            catch (Exception ex)
            {
                lanModeSelected = false;
                logger?.LogWarning("[LAN UI] Could not stop Online lobby activity: " + ex.GetBaseException().Message);
                ResumeOnlinePolling(ui);
                return;
            }

            try
            {
                nativeEnteredCode!.SetValue(ui, string.Empty);
                nativeRefresh!.Invoke(ui, null);
            }
            catch (Exception ex)
            {
                logger?.LogWarning("[LAN UI] Could not clear the Online keypad: " + ex.GetBaseException().Message);
            }
            logger?.LogInfo("[LAN UI] LAN selected. Find Code now lists rooms from local and Radmin networks.");
        }
        else
        {
            try
            {
                nativeEnteredCode!.SetValue(ui, onlineCodeBeforeLan ?? string.Empty);
            }
            catch (Exception ex)
            {
                logger?.LogWarning("[LAN UI] Could not restore the Online code field: " + ex.GetBaseException().Message);
                return;
            }

            lanModeSelected = false;
            ShutDownDiscovery(ui);
            onlineCodeBeforeLan = null;
            try
            {
                nativeRefresh!.Invoke(ui, null);
            }
            catch (Exception ex)
            {
                logger?.LogWarning("[LAN UI] Online code refresh failed: " + ex.GetBaseException().Message);
            }

            ResumeOnlinePolling(ui);
            logger?.LogInfo("[LAN UI] Online selected. TV multiplayer now uses the game's normal lobby flow.");
        }

        SetLanModeToggleText();
    }

    private static void ResumeOnlinePolling(object ui)
    {
        try
        {
            startAutoJoinPolling!.Invoke(ui, null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[LAN UI] Online polling could not resume: " + ex.GetBaseException().Message);
        }
    }
}
