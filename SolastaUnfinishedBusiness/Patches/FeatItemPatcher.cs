using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class FeatItemPatcher
{
    [HarmonyPatch(typeof(FeatItem), nameof(FeatItem.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            FeatItem __instance,
            RulesetCharacterHero inspectedCharacter,
            FeatDefinition featDefinition,
            ProficiencyBaseItem.OnItemClickedHandler onItemClicked,
            ProficiencyBaseItem.OnItemHoverChangedHandler onItemHoverChanged,
            bool flexibleWidth)
        {
            if (featDefinition == null)
            {
                return true;
            }

            var group = featDefinition.GetFirstSubFeatureOfType<IGroupedFeat>();

            if (group == null ||
                onItemClicked == null)
            {
                return true;
            }

            var guiWrapperService = ServiceRepository.GetService<IGuiWrapperService>();
            var guiFeatDefinition = guiWrapperService?.GetGuiFeatDefinition(featDefinition.Name);

            if (guiFeatDefinition == null)
            {
                return true;
            }

            __instance.GuiFeatDefinition = guiFeatDefinition;
            __instance.Bind(
                inspectedCharacter,
                featDefinition,
                _ =>
                {
                    var selector = SubFeatSelectionModal.Get();

                    selector.Cancel();
                    selector.Bind(inspectedCharacter, __instance, featDefinition, group, onItemClicked,
                        __instance.RectTransform);
                    selector.Show();
                },
                flexibleWidth);

            __instance.GuiFeatDefinition?.SetupTooltip(__instance.Tooltip, inspectedCharacter);
            __instance.OnItemHoverChanged = onItemHoverChanged;

            SubFeatSelectionModal.SetColor(__instance, SubFeatSelectionModal.HeaderColor);

            return false;
        }
    }

    [HarmonyPatch(typeof(FeatItem), "IsStrikethroughStyle")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsStrikethroughStyle_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(FeatItem __instance, ProficiencyBaseItem hoveredItem, ref bool __result)
        {
            if (__instance?.GuiFeatDefinition?.FeatDefinition == null ||
                hoveredItem is not FeatItem hoveredFeatItem ||
                hoveredFeatItem.GuiFeatDefinition?.FeatDefinition == null ||
                string.IsNullOrEmpty(__instance.StageTag) ||
                string.IsNullOrEmpty(hoveredFeatItem.StageTag) ||
                __instance.CurrentPoolType != HeroDefinitions.PointsPoolType.Feat ||
                hoveredFeatItem.CurrentPoolType != HeroDefinitions.PointsPoolType.Feat)
            {
                __result = false;

                return false;
            }

            return true;
        }

        [UsedImplicitly]
        public static global::System.Exception Finalizer(global::System.Exception __exception, ref bool __result)
        {
            if (__exception == null)
            {
                return null;
            }

            __result = false;

#if DEBUG
            Main.Log($"Suppressed FeatItem.IsStrikethroughStyle exception: {__exception}");
#endif

            return null;
        }
    }

    [HarmonyPatch(typeof(FeatItem), nameof(FeatItem.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(FeatItem __instance)
        {
            //PATCH: sets FeatItem's color back to default
            SubFeatSelectionModal.SetColor(__instance, SubFeatSelectionModal.DefaultColor);
        }
    }
}
