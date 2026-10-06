using JetBrains.Annotations;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Api.ModKit.Utility.Private;

internal static class UI
{
    // Helper functionality

    private static readonly GUIContent Content = new();

    internal static readonly GUIContent CheckOn = new(ModKit.UI.CheckGlyphOn);

    private static readonly GUIContent CheckOff = new(ModKit.UI.CheckGlyphOff);
    private static readonly GUIContent DisclosureOn = new(ModKit.UI.DisclosureGlyphOn);
    private static readonly GUIContent DisclosureOff = new(ModKit.UI.DisclosureGlyphOff);
    private static GUIContent LabelContent(string text)
    {
        Content.text = text;
        Content.image = null;
        Content.tooltip = null;
        return Content;
    }

    [UsedImplicitly]
    public static bool Toggle(
        Rect rect, GUIContent label, bool value, bool isEmpty, GUIContent on, GUIContent off, GUIStyle stateStyle,
        GUIStyle labelStyle)
        => ModKit.UI.Toggle(rect, label, value, isEmpty, on, off, stateStyle, labelStyle);

    // Button Control - Layout Version

    [UsedImplicitly]
    public static bool Toggle(GUIContent label, bool value, GUIContent on, GUIContent off, GUIStyle stateStyle,
        GUIStyle labelStyle, bool isEmpty = false, params GUILayoutOption[] options)
        => ModKit.UI.Toggle(label, value, on, off, stateStyle, labelStyle, isEmpty, options);

    [UsedImplicitly]
    public static bool Toggle(string label, bool value, string on, string off, GUIStyle stateStyle, GUIStyle labelStyle,
        params GUILayoutOption[] options)
    {
        return Toggle(LabelContent(label), value, new GUIContent(on), new GUIContent(off), stateStyle, labelStyle,
            false, options);
    }

    // Disclosure Toggles
    [UsedImplicitly]
    public static bool DisclosureToggle(GUIContent label, bool value, bool isEmpty = false,
        params GUILayoutOption[] options)
    {
        return Toggle(label, value, DisclosureOn, DisclosureOff, GUI.skin.textArea, GUI.skin.label, isEmpty,
            options);
    }

    [UsedImplicitly]
    public static bool DisclosureToggle(string label, bool value, GUIStyle stateStyle, GUIStyle labelStyle,
        bool isEmpty = false, params GUILayoutOption[] options)
    {
        return Toggle(LabelContent(label), value, DisclosureOn, DisclosureOff, stateStyle, labelStyle, isEmpty,
            options);
    }

    [UsedImplicitly]
    public static bool DisclosureToggle(string label, bool value, bool isEmpty = false,
        params GUILayoutOption[] options)
    {
        return DisclosureToggle(label, value, GUI.skin.box, GUI.skin.label, isEmpty, options);
    }

    // CheckBox 
    [UsedImplicitly]
    public static bool CheckBox(GUIContent label, bool value, bool isEmpty, params GUILayoutOption[] options)
    {
        return Toggle(label, value, CheckOn, CheckOff, GUI.skin.textArea, GUI.skin.label, isEmpty, options);
    }

    [UsedImplicitly]
    public static bool CheckBox(string label, bool value, bool isEmpty, GUIStyle style,
        params GUILayoutOption[] options)
    {
        return Toggle(LabelContent(label), value, CheckOn, CheckOff, GUI.skin.box, style, isEmpty, options);
    }

    [UsedImplicitly]
    public static bool CheckBox(string label, bool value, bool isEmpty, params GUILayoutOption[] options)
    {
        return CheckBox(label, value, isEmpty, GUI.skin.label, options);
    }
}
