using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RepeatableFeatPatcher
{
    [HarmonyPatch(typeof(CharacterBuildingManager), nameof(CharacterBuildingManager.IsFeatKnownOrTrained))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsFeatKnownOrTrained_Patch
    {
        [UsedImplicitly]
        public static void Postfix(FeatDefinition feat, ref bool __result)
        {
            if (SkillFeats.IsRepeatable(feat))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStageProficiencySelectionPanel),
        nameof(CharacterStageProficiencySelectionPanel.OnFeatSelected))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnFeatSelected_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterStageProficiencySelectionPanel __instance, ProficiencySingleItem __0)
        {
            var feat = __0?.BaseDefinition as FeatDefinition;

            if (!SkillFeats.IsRepeatable(feat))
            {
                return true;
            }

            var hero = __instance.currentHero;
            var buildingData = hero?.GetHeroBuildingData();
            var service = __instance.CharacterBuildingService;

            if (buildingData == null || service == null ||
                __instance.currentLearnStep < 0 || __instance.currentLearnStep >= __instance.allTags.Count)
            {
                return false;
            }

            var tag = __instance.allTags[__instance.currentLearnStep];
            var pool = service.GetPointPoolOfTypeAndTag(buildingData, HeroDefinitions.PointsPoolType.Feat, tag);

            if (pool == null || pool.remainingPoints <= 0 ||
                !Tabletop2024Context.IsFeatMatchingPrerequisites(service, buildingData, feat, out _) ||
                !Tabletop2024Context.TryPrepareIndependentFeatTraining(buildingData, tag, feat, service))
            {
                return false;
            }

            // The native click handler toggles a known feat off. Repeatable feats instead spend another point;
            // the existing back/reset command clears the whole tag and restores its choices together.
            ServiceRepository.GetService<IHeroBuildingCommandService>()?.TrainCharacterFeature(
                hero, tag, feat.Name, HeroDefinitions.PointsPoolType.Feat);

            return false;
        }
    }
}
