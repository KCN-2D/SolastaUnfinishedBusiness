using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiCharacterPatcher
{
    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.Name), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Name_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, ref string __result)
        {
            if (__instance.RulesetCharacter is RulesetCharacterSimulacrum duplicate &&
                SimulacrumBehavior.TryGetDisplayName(duplicate, out var displayName))
            {
                __result = displayName;
            }
        }
    }

    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.FullName), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FullName_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, ref string __result)
        {
            if (__instance.RulesetCharacter is RulesetCharacterSimulacrum duplicate &&
                SimulacrumBehavior.TryGetDisplayName(duplicate, out var displayName))
            {
                __result = displayName;
            }
        }
    }

    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.RaceTitle), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RaceTitle_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, ref string __result)
        {
            if (__instance.RulesetCharacter is RulesetCharacterSimulacrum duplicate &&
                SimulacrumBehavior.TryGetHumanoidIdentity(duplicate, out var race, out var subRace))
            {
                __result = (subRace ? subRace : race).FormatTitle();
            }
        }
    }

    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.RaceDescription), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RaceDescription_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, ref string __result)
        {
            if (__instance.RulesetCharacter is RulesetCharacterSimulacrum duplicate &&
                SimulacrumBehavior.TryGetHumanoidIdentity(duplicate, out var race, out var subRace))
            {
                __result = (subRace ? subRace : race).FormatDescription();
            }
        }
    }

    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.CharacterLevel), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterLevel_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, ref int __result)
        {
            if (__instance.RulesetCharacter is RulesetCharacterSimulacrum duplicate)
            {
                var characterLevel =
                    duplicate.TryGetAttributeValue(AttributeDefinitions.CharacterLevel);

                if (characterLevel > 0)
                {
                    __result = characterLevel;
                }
            }
        }
    }

    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.DisplayUniqueLevelSpellSlots))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DisplayUniqueLevelSpellSlots_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            GuiCharacter __instance,
            RulesetSpellRepertoire spellRepertoire,
            RectTransform uniqueLevelSlotsGroup)
        {
            //PATCH: Don't display Unique Level Spell Slots if MC hero (MULTICLASS)
            if (!uniqueLevelSlotsGroup || spellRepertoire == null)
            {
                return;
            }

            if (Global.InspectedHero != null)
            {
                return;
            }

            // Ordinary substitute monsters have no class slot identity. A Simulacrum
            // does, and must use the copied Warlock/multiclass repertoires rather than
            // being treated as a wildshape UI.
            var character = __instance.RulesetCharacter;

            if (character is RulesetCharacterHero or RulesetCharacterSimulacrum &&
                !(Main.Settings.DisplayPactSlotsOnSpellSelectionPanel &&
                  !Main.Settings.UseAlternateSpellPointsSystem))
            {
                return;
            }

            if (character is RulesetCharacterHero or RulesetCharacterSimulacrum &&
                !SharedSpellsContext.IsMulticaster(character))
            {
                return;
            }

            uniqueLevelSlotsGroup.gameObject.SetActive(false);
        }

        [UsedImplicitly]
        public static void GetSlotsNumber(
            RulesetSpellRepertoire spellRepertoire,
            int spellLevel,
            out int remaining,
            out int max,
            GuiCharacter guiCharacter)
        {
            //PATCH: calculates the Warlock slots number correctly under a MC scenario (MULTICLASS)
            spellRepertoire.GetSlotsNumber(spellLevel, out remaining, out max);

            var character = guiCharacter.RulesetCharacter;

            if (!SharedSpellsContext.IsMulticaster(character))
            {
                return;
            }

            max = SharedSpellsContext.GetWarlockMaxSlots(character);
            remaining = max - SharedSpellsContext.GetWarlockUsedSlots(character);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var getSlotsNumberMethod = typeof(RulesetSpellRepertoire).GetMethod("GetSlotsNumber");
            var myGetSlotsNumberMethod = typeof(DisplayUniqueLevelSpellSlots_Patch).GetMethod("GetSlotsNumber");

            return instructions.ReplaceCalls(getSlotsNumberMethod, "GuiCharacter.DisplayUniqueLevelSpellSlots",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, myGetSlotsNumberMethod));
        }
    }

    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.MainClassDefinition), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class MainClassDefinition_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            GuiCharacter __instance,
            ref CharacterClassDefinition __result)
        {
            if (__instance.RulesetCharacter is RulesetCharacterSimulacrum duplicate &&
                SimulacrumBehavior.TryGetPrimaryClass(duplicate, out var primaryClass))
            {
                __result = primaryClass;

                return;
            }

            //PATCH: EnableEnhancedCharacterInspection
            // NOTE: don't use SelectedClass??. which bypasses Unity object lifetime check
            var selectedClass = CharacterInspectionScreenEnhancement.SelectedClass;

            if (selectedClass)
            {
                __result = selectedClass;
            }
        }
    }

    //PATCH: Changes Game UI to support Multiclass
    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.LevelAndClassAndSubclass), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LevelAndClassAndSubclass_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, ref string __result)
        {
            __result = MulticlassGameUi.GetAllClassesLabel(__instance, ' ') ?? __result;
        }
    }

    //PATCH: Changes Game UI to support Multiclass
    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.ClassAndLevel), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ClassAndLevel_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, ref string __result)
        {
            __result = MulticlassGameUi.GetAllClassesLabel(__instance, ' ') ?? __result;
        }
    }

    //PATCH: Changes Game UI to support Multiclass
    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.LevelAndExperienceTooltip), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LevelAndExperienceTooltip_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, ref string __result)
        {
            __result = MulticlassGameUi.GetLevelAndExperienceTooltip(__instance) ?? __result;
        }
    }

    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.BackgroundDescription), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BackgroundDescription_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, ref string __result)
        {
            //PATCH: Enables additional background display on inspection panel
            if (!Main.Settings.EnableAdditionalBackstoryDisplay)
            {
                return;
            }

            var hero = __instance.RulesetCharacterHero;

            if (hero == null)
            {
                return;
            }

            var additionalBackstory = hero.AdditionalBackstory;

            if (string.IsNullOrEmpty(additionalBackstory))
            {
                return;
            }

            var builder = new StringBuilder();

            builder.Append(__result);
            builder.Append("\n\n<B>");
            builder.Append(Gui.Format("Stage/&IdentityAdditionalBackstoryHeader"));
            builder.Append("</B>\n\n");
            builder.Append(additionalBackstory);

            __result = builder.ToString();
        }
    }

    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.FormatHealthGauge))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatHealthGauge_Patch
    {
        /// <summary>
        ///     This mods the vertical gauge in the monster portrait.
        ///     The gauge now shows health in steps instead of a continuous value.
        /// </summary>
        [UsedImplicitly]
        public static void Prefix(GuiCharacter __instance, out bool __state)
        {
            //PATCH: HideMonsterHitPoints
            if (!Main.Settings.HideMonsterHitPoints)
            {
                __state = false;
                return;
            }

            var dirty = __instance.HasHealthUpdated();

            // capture current state of dirty flag for use in Postfix
            __state = __instance.healthGaugeDirty || dirty;
        }

        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, Image healthGauge, float parentHeight, bool __state)
        {
            if (!Main.Settings.HideMonsterHitPoints)
            {
                return;
            }

            if (!__state)
            {
                return; // health wasn't dirty so healthGauge hasn't been updated
            }

            if (__instance.RulesetCharacterMonster is not { Side: Side.Enemy })
            {
                return;
            }

            var ratio = Mathf.Clamp(__instance.CurrentHitPoints / (float)__instance.HitPoints, 0.0f, 1f);

            ratio = CampaignsContext.GetSteppedHealthRatio(ratio);

            healthGauge.rectTransform.offsetMax = new Vector2(healthGauge.rectTransform.offsetMax.x,
                (float)(-parentHeight * (1.0 - ratio)));
        }
    }

    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.FormatHealthLabel))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatHealthLabel_Patch
    {
        private static readonly Regex HitPointRegex = new(@"^<#.{6}>(?<current_hp>\d{1,4})</color>/(?<max_hp>\d{1,4})",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        ///     Mods the monster health label (current/max) hit points to hide current hit points.
        /// </summary>
        [UsedImplicitly]
        public static void Prefix(GuiCharacter __instance, out bool __state)
        {
            //PATCH: HideMonsterHitPoints
            if (!Main.Settings.HideMonsterHitPoints)
            {
                __state = false;
                return;
            }

            var dirty = __instance.HasHealthUpdated();

            // capture current state of dirty flag for use in Postfix
            __state = __instance.healthLabelDirty || dirty;
        }

        [UsedImplicitly]
        public static void Postfix(GuiCharacter __instance, GuiLabel healthLabel, bool __state)
        {
            if (!Main.Settings.HideMonsterHitPoints)
            {
                return;
            }

            if (!__state)
            {
                return; // health wasn't dirty so healthLabel hasn't been updated
            }

            // A monster has __instance.RulesetCharacterMonster != null and __instance.RulesetCharacter != null
            // A hero has __instance.RulesetCharacterHero != null and __instance.RulesetCharacter != null
            // A hero with wildshape has __instance.RulesetCharacterMonster != null, __instance.RulesetCharacter != null and __instance.RulesetCharacter.IsSubstitute == true

            if (!__instance.HasHitPointsKnowledge || !IsMonster())
            {
                return;
            }
            // Our heroes now have enough bestiary knowledge to display the monster's hit points
            // which makes picking off damaged monsters easier than it might be.

            // Make the following changes:
            // 1) Full hit points are still displayed, e.g. 28/28
            // 2) Less than full hit points are hidden, but the number of digits is shown, so **/28 or */28.
            // Standard health colours will still be in effect.  Green (50%-100%), Orange (25%-50%), Red (0-25%).

            // Normal text formatting runs before the patch so the healthLabel text at this point is
            // "?? / ??" (if HasHitPointsKnowledge=false), or <#color>current_hp</color>/max_hp

            var text = healthLabel.Text;

            // extract current and max hp
            var match = HitPointRegex.Match(text);

            if (!match.Success || match.Groups["current_hp"].Value == match.Groups["max_hp"].Value)
            {
                return;
            }

            var hp = match.Groups["current_hp"].Value;
            var hpLen = hp.Length;
            var stars = new string('*', hpLen);

            // replace with asterisks
            healthLabel.Text = text.Replace($">{hp}<", $">{stars}<");

            return;

            bool IsMonster()
            {
                if (__instance.RulesetCharacterMonster == null)
                {
                    // definitely not a monster
                    return false;
                }

                if (__instance.RulesetCharacter.IsSubstitute)
                {
                    // It's a hero wild shaping (probably).
                    return false;
                }

                return __instance.RulesetCharacter.Side == Side.Enemy;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterPlateGame), nameof(CharacterPlateGame.OnPointerEnter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnPointerEnter_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterPlateGame __instance)
        {
            //PATCH: EnableStatsOnHeroTooltip
            var hero = __instance.GuiCharacter?.RulesetCharacterHero;
            var tooltip = __instance.GuiTooltip;

            if (!tooltip)
            {
                return;
            }

            // Native DescriptionChanged prepares the monster tooltip after this Prefix. A
            // Simulacrum therefore replaces its data provider in Postfix, once the native image
            // provider is available, instead of being overwritten with the shared shell stats.
            if (__instance.GuiCharacter?.RulesetCharacter is RulesetCharacterSimulacrum)
            {
                return;
            }

            if (hero == null || !Main.Settings.EnableStatsOnHeroTooltip)
            {
                tooltip.TooltipClass = "HeroDescription";

                return;
            }

            var sb = new StringBuilder();
            var totalAttacks = (float)hero.successfulAttacks + hero.failedAttacks;
            var hitAccuracy = totalAttacks > 0 ? hero.successfulAttacks / totalAttacks : 0;
            var feet = hero.travelledCells / 5f;
            var meter = feet * 0.3048;

            sb.AppendLine(hero.Name);
            sb.AppendLine();
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatHitAccuracyTitle")}</b> {hitAccuracy:P2}");
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatCriticalHitsTitle")}</b> {hero.criticalHits:N0}");
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatCriticalFailuresTitle")}</b> {hero.criticalFailures:N0}");
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatInflictedDamageTitle")}</b> {hero.inflictedDamage:N0}");
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatSlainEnemiesTitle")}</b> {hero.slainEnemies:N0}");
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatSustainedInjuriesTitle")}</b> {hero.sustainedInjuries:N0}");
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatRestoredHealthTitle")}</b> {hero.restoredHealth:N0}");
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatUsedMagicAndPowersTitle")}</b> {hero.usedMagicAndPowers:N0}");
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatKnockOutsTitle")}</b> {hero.knockOuts:N0}");
            sb.AppendLine($"<b>{Gui.Localize("Modal/&StatTravelledCellsTitle")}</b> {feet:N0}ft / {meter:N0}m");

            tooltip.Content = sb.ToString();
            tooltip.TooltipClass = string.Empty;
        }

        [UsedImplicitly]
        public static void Postfix(CharacterPlateGame __instance)
        {
            SetupSimulacrumTooltip(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterPlateGame), nameof(CharacterPlateGame.DescriptionChanged))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DescriptionChanged_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterPlateGame __instance)
        {
            SetupSimulacrumTooltip(__instance);
        }
    }

    private static void SetupSimulacrumTooltip(CharacterPlateGame characterPlate)
    {
        if (characterPlate.GuiCharacter?.RulesetCharacter is not
            RulesetCharacterSimulacrum duplicate)
        {
            return;
        }

        SetupSimulacrumTooltip(characterPlate.GuiTooltip, duplicate);
    }

    [HarmonyPatch(typeof(WorldLocationCharacter), nameof(WorldLocationCharacter.DescriptionChanged))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class WorldLocationCharacterDescriptionChanged_Patch
    {
        [UsedImplicitly]
        public static void Postfix(WorldLocationCharacter __instance)
        {
            if (__instance?.GameLocationCharacter?.RulesetCharacter is
                RulesetCharacterSimulacrum duplicate)
            {
                SetupSimulacrumTooltip(__instance.GraphicsTooltip, duplicate);
            }
        }
    }

    private static void SetupSimulacrumTooltip(
        ITooltip tooltip,
        RulesetCharacterSimulacrum duplicate)
    {
        if (tooltip == null ||
            ServiceRepository.GetService<IGuiWrapperService>()
                ?.GetGuiMonsterDefinition(duplicate.MonsterDefinition.Name) is not
                { } guiMonsterDefinition)
        {
            return;
        }

        var nativeImageProvider = tooltip.DataProvider as IImageProvider;

        if (nativeImageProvider is SimulacrumMonsterTooltipProvider)
        {
            return;
        }

        tooltip.TooltipClass = guiMonsterDefinition.TooltipClass;
        tooltip.Content = guiMonsterDefinition.Description;
        tooltip.Context = duplicate;
        var dataProvider = new SimulacrumMonsterTooltipProvider(
            guiMonsterDefinition,
            duplicate,
            nativeImageProvider);

        tooltip.DataProvider = dataProvider;
        SimulacrumDiagnostics.RecordTooltip(
            duplicate,
            dataProvider.HitPoints,
            dataProvider.ArmorClass,
            dataProvider.MoveModesString);
    }

    private sealed class SimulacrumMonsterTooltipProvider(
        GuiMonsterDefinition definition,
        RulesetCharacterSimulacrum character,
        IImageProvider imageProvider) :
        ITitleProvider,
        IImageProvider,
        IDescriptionProvider,
        ISubTitleProvider,
        IMonsterBasicInfoProvider,
        IMonsterAttacksProvider
    {
        private readonly Dictionary<Image, bool> _originalPreserveAspect = [];

        public string Title => definition.Title;
        public string Subtitle => definition.Subtitle;
        public string Description => definition.Description;
        public int ArmorClass => character.TryGetAttributeValue(AttributeDefinitions.ArmorClass);
        public int HitPoints => character.TryGetAttributeValue(AttributeDefinitions.HitPoints);
        public int HitPointsUnaltered => HitPoints;
        public string MoveModesString => Gui.FormatMoveModes(character.MoveModes, character, false, -1);
        public float ChallengeRating => definition.ChallengeRating;
        public int KnowledgeLevel => 4;
        public List<MonsterAttackIteration> AttackIterations => definition.AttackIterations;

        public string GetDisplayName(object context)
        {
            return definition.GetDisplayName(context);
        }

        public void SetupSprite(Image image, object context)
        {
            if (image && !_originalPreserveAspect.ContainsKey(image))
            {
                _originalPreserveAspect.Add(image, image.preserveAspect);
            }

            if (imageProvider != null)
            {
                imageProvider.SetupSprite(image, context);
            }
            else
            {
                definition.SetupSprite(image, context);
            }

            if (image)
            {
                image.preserveAspect = true;
            }
        }

        public void ReleaseSprite(Image image)
        {
            if (imageProvider != null)
            {
                imageProvider.ReleaseSprite(image);
            }
            else
            {
                definition.ReleaseSprite(image);
            }

            if (image && _originalPreserveAspect.TryGetValue(image, out var preserveAspect))
            {
                image.preserveAspect = preserveAspect;
                _originalPreserveAspect.Remove(image);
            }
        }

        public bool CanAccess(BestiaryDefinitions.BestiaryAccess access)
        {
            return true;
        }
    }

    //PATCH: supports custom portraits on heroes and monsters
    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.AssignPortraitImage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AssignPortraitImage_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            GuiCharacter __instance,
            RawImage portraitImage,
            RectTransform portraitGroup,
            RectTransform portraitBack,
            System.Action response,
            out bool __state)
        {
            __state = SimulacrumPortraits.TryAssign(__instance, portraitImage);

            if (__state)
            {
                CompleteSimulacrumPortraitBinding(
                    portraitImage,
                    portraitGroup,
                    portraitBack,
                    null,
                    response);
            }
            else
            {
                // Character plate images are pooled. Remove a previous Simulacrum binding before
                // native code reuses this RawImage for its owner or another party member.
                SimulacrumPortraits.Release(portraitImage);
            }

            return !__state;
        }

        [UsedImplicitly]
        public static void Postfix(
            GuiCharacter __instance,
            RawImage portraitImage,
            bool __state)
        {
            if (!__state)
            {
                PortraitsContext.ChangePortrait(__instance, portraitImage);
            }
        }
    }

    //PATCH: supports custom portraits on heroes and monsters
    [HarmonyPatch(typeof(GuiCharacter), nameof(GuiCharacter.AssignActiveCharacterPortraitImage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AssignActiveCharacterPortraitImage_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            GuiCharacter __instance,
            RawImage portraitRawImage,
            Image portraitImage,
            RectTransform portraitGroup,
            RectTransform portraitBack,
            System.Action response,
            out bool __state)
        {
            __state = SimulacrumPortraits.TryAssign(
                __instance,
                portraitRawImage,
                true);

            if (__state)
            {
                CompleteSimulacrumPortraitBinding(
                    portraitRawImage,
                    portraitGroup,
                    portraitBack,
                    portraitImage,
                    response);
            }
            else
            {
                SimulacrumPortraits.Release(portraitRawImage);
            }

            return !__state;
        }

        [UsedImplicitly]
        public static void Postfix(
            GuiCharacter __instance,
            RawImage portraitRawImage,
            bool __state)
        {
            if (!__state)
            {
                PortraitsContext.ChangePortrait(__instance, portraitRawImage);
            }
        }
    }

    private static void CompleteSimulacrumPortraitBinding(
        RawImage portraitImage,
        RectTransform portraitGroup,
        RectTransform portraitBack,
        Image alternatePortraitImage,
        System.Action response)
    {
        if (portraitBack)
        {
            portraitBack.gameObject.SetActive(false);
        }

        if (portraitGroup)
        {
            portraitGroup.gameObject.SetActive(true);
        }

        if (portraitImage)
        {
            portraitImage.gameObject.SetActive(true);
        }

        if (alternatePortraitImage)
        {
            alternatePortraitImage.gameObject.SetActive(false);
        }

        response?.Invoke();
    }
}
