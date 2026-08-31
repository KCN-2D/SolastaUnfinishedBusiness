using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using TMPro;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class AttackModeBoxPatcher
{
    private static ConditionalWeakTable<TMP_Text, OverflowModeState> OriginalOverflowModes { get; } = new();

    [HarmonyPatch(typeof(AttackModeBox), nameof(AttackModeBox.Bind), typeof(RulesetAttackMode))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(AttackModeBox __instance, RulesetAttackMode attackMode)
        {
            try
            {
                PostfixSafe(__instance, attackMode);
            }
            catch (Exception ex)
            {
                Main.Error(ex);
            }
        }

        private static void PostfixSafe(AttackModeBox instance, RulesetAttackMode attackMode)
        {
            var magicalTag = instance.magicalTag;
            var tagActive = magicalTag && magicalTag.gameObject.activeSelf;

            if (!(attackMode?.Magical ?? false) && !tagActive)
            {
                return;
            }

            if (!magicalTag)
            {
                return;
            }

            var magicalText = magicalTag.GetComponentInChildren<GuiLabel>(true)?.TMP_Text;

            if (!magicalText)
            {
                return;
            }

            RepairEmptyCjkTextMesh(magicalText);
        }
    }

    private static void RepairEmptyCjkTextMesh(TMP_Text text)
    {
        if (!TranslatorContext.HasCJKChar(text.text))
        {
            RestoreOverflowMode(text);

            return;
        }

        if (HasGeneratedGeometry(text))
        {
            return;
        }

        try
        {
            text.SetText(text.text, false);
            text.ForceMeshUpdate(true);

            if (HasGeneratedGeometry(text) ||
                text.overflowMode != TextOverflowModes.Ellipsis)
            {
                return;
            }

            // A CJK fallback glyph can exceed the badge's first line under Ellipsis, which clears
            // the mesh in this TMP version. Keep Overflow only when it demonstrably restores geometry.
            OriginalOverflowModes.Remove(text);
            OriginalOverflowModes.Add(text, new OverflowModeState(text.overflowMode));
            text.overflowMode = TextOverflowModes.Overflow;
            text.ForceMeshUpdate(true);

            if (HasGeneratedGeometry(text))
            {
                return;
            }

            RestoreOverflowMode(text);
        }
        catch (Exception ex)
        {
            RestoreOverflowMode(text);
            Main.Error(ex);
        }
    }

    private static bool HasGeneratedGeometry(TMP_Text text)
    {
        return (text.textInfo?.characterCount ?? 0) > 0 &&
               CountMeshVertices(text.textInfo) > 0;
    }

    private static void RestoreOverflowMode(TMP_Text text)
    {
        try
        {
            if (!OriginalOverflowModes.TryGetValue(text, out var state))
            {
                return;
            }

            OriginalOverflowModes.Remove(text);

            if (text.overflowMode == state.OverflowMode)
            {
                return;
            }

            text.overflowMode = state.OverflowMode;
            text.ForceMeshUpdate(true);
        }
        catch (Exception ex)
        {
            Main.Error(ex);
        }
    }

    private sealed class OverflowModeState
    {
        internal OverflowModeState(TextOverflowModes overflowMode)
        {
            OverflowMode = overflowMode;
        }

        internal TextOverflowModes OverflowMode { get; }
    }

    private static int CountMeshVertices(TMP_TextInfo textInfo)
    {
        if (textInfo?.meshInfo == null)
        {
            return -1;
        }

        var count = 0;

        foreach (var meshInfo in textInfo.meshInfo)
        {
            count += meshInfo.vertexCount;
        }

        return count;
    }
}
