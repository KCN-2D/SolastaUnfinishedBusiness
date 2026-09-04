using System;
using System.Collections;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Subclasses;
using SolastaUnfinishedBusiness.Subclasses.Builders;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.InvocationDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionLightAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionFeatureSets;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPointPools;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly FeatureDefinitionPointPool PointPoolWarlockInvocation1 = FeatureDefinitionPointPoolBuilder
        .Create(PointPoolWarlockInvocation2, "PointPoolWarlockInvocation1")
        .SetGuiPresentation("PointPoolWarlockInvocationInitial", Category.Feature)
        .SetPool(HeroDefinitions.PointsPoolType.Invocation, 1)
        .AddToDB();

    internal static readonly InvocationDefinition InvocationPactBlade = InvocationDefinitionBuilder
        .Create("InvocationPactBlade")
        .SetGuiPresentation(
            FeatureSetPactBlade.GuiPresentation.Title,
            "Feature/&FeatureSetPactBladeAlternateDescription",
            ThirstingBlade)
        .SetGrantedFeature(FeatureSetPactBlade)
        .AddCustomSubFeatures(
            new CanUseAttribute(
                AttributeDefinitions.Charisma, IsPactBladeWeapon))
        .AddToDB();

    private static readonly InvocationDefinition InvocationPactChain = InvocationDefinitionBuilder
        .Create("InvocationPactChain")
        .SetGuiPresentation(
            FeatureSetPactChain.GuiPresentation.Title,
            FeatureSetPactChain.GuiPresentation.Description,
            PowerPactChainImp)
        .SetGrantedFeature(FeatureSetPactChain)
        .AddToDB();

    private static readonly InvocationDefinition InvocationPactTome = InvocationDefinitionBuilder
        .Create("InvocationPactTome")
        // need to build a new gui presentation to be able to hide this and don't affect the set itself
        .SetGuiPresentation(
            FeatureSetPactTome.GuiPresentation.Title,
            FeatureSetPactTome.GuiPresentation.Description,
            Identify)
        .SetGrantedFeature(FeatureSetPactTome.FeatureSet[0]) // grant pool directly instead of feature set
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerWarlockMagicalCunning = FeatureDefinitionPowerBuilder
        .Create("PowerWarlockMagicalCunning")
        .SetGuiPresentation(Category.Feature, PowerWizardArcaneRecovery)
        .SetUsesFixed(ActivationTime.Minute1, RechargeRate.LongRest)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetCasterEffectParameters(Banishment)
                .Build())
        .AddCustomSubFeatures(new PowerOrSpellFinishedByMeMagicalCunning())
        .AddToDB();

    private static readonly FeatureDefinition FeatureEldritchMaster = FeatureDefinitionBuilder
        .Create("FeatureEldritchMaster")
        .SetGuiPresentation(Category.Feature)
        .AddToDB();

    private static readonly SpellDefinition InvisibilityOneWithShadows = SpellDefinitionBuilder
        .Create(Invisibility, "InvisibilityOneWithShadows")
        .SetGuiPresentation(Invisibility.GuiPresentation.Title, Invisibility.GuiPresentation.Description, Invisibility,
            true)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create(Invisibility)
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .Build())
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerInvocationOneWithShadows = FeatureDefinitionPowerBuilder
        .Create("PowerInvocationOneWithShadows")
        .SetGuiPresentation("OneWithShadowsAlternate", Category.Invocation, Invisibility)
        .SetUsesFixed(ActivationTime.Action)
        .SetShowCasting(false)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.Hour, 1)
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .Build())
        .AddCustomSubFeatures(
            new ValidatorsValidatePowerUse(ValidatorsCharacter.IsNotInBrightLight),
            new PowerOrSpellFinishedByMeInvocationOneWithShadows())
        .AddToDB();

    internal static void SwitchWarlockMagicalCunningAndImprovedEldritchMaster()
    {
        Warlock.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == PowerWarlockMagicalCunning ||
            x.FeatureDefinition == FeatureEldritchMaster ||
            x.FeatureDefinition == Level20Context.PowerWarlockEldritchMaster);

        if (Main.Settings.EnableWarlockMagicalCunningAndImprovedEldritchMaster2024)
        {
            Warlock.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(PowerWarlockMagicalCunning, 2),
                new FeatureUnlockByLevel(FeatureEldritchMaster, 20));
        }
        else
        {
            Warlock.FeatureUnlocks.Add(new FeatureUnlockByLevel(Level20Context.PowerWarlockEldritchMaster, 20));
        }

        Warlock.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchWarlockPatronLearningLevel()
    {
        var patrons = DatabaseRepository.GetDatabase<CharacterSubclassDefinition>()
            .Where(x => x.Name.StartsWith("Patron"))
            .ToList();

        var fromLevel = 3;
        var toLevel = 1;

        // handle this exception that adds features at levels 2 and 3 on sub
        var patronEldritchSurge = GetDefinition<CharacterSubclassDefinition>(PatronEldritchSurge.Name);

        patronEldritchSurge.FeatureUnlocks.RemoveAll(x =>
            x.Level <= 3 && x.FeatureDefinition == EldritchVersatilityBuilders.UnLearn1Versatility);

        if (Main.Settings.EnableWarlockToLearnPatronAtLevel3)
        {
            fromLevel = 1;
            toLevel = 3;
        }

        // put things back if it should not be changed
        if (!Main.Settings.EnableWarlockToLearnPatronAtLevel3)
        {
            patronEldritchSurge.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(EldritchVersatilityBuilders.UnLearn1Versatility, 2),
                new FeatureUnlockByLevel(EldritchVersatilityBuilders.UnLearn1Versatility, 3));
        }

        SwitchSubclassLearningLevel(
            patrons,
            Warlock,
            FeatureDefinitionSubclassChoices.SubclassChoiceWarlockOtherworldlyPatrons,
            fromLevel,
            toLevel);
    }

    internal static void SwitchWarlockInvocationsProgression()
    {
        Warlock.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == FeatureSetPactSelection);

        if (Main.Settings.EnableWarlockInvocationProgression2024)
        {
            Warlock.FeatureUnlocks.Add(new FeatureUnlockByLevel(PointPoolWarlockInvocation1, 1));
            PointPoolWarlockInvocation2.GuiPresentation.Title =
                "Feature/&PointPoolWarlockInvocationAdditionalTitle";
            PointPoolWarlockInvocation2.GuiPresentation.Description =
                "Feature/&PointPoolWarlockInvocationAdditionalDescription";
            PointPoolWarlockInvocation5.poolAmount = 2;

            foreach (var invocation in DatabaseRepository.GetDatabase<InvocationDefinition>()
                         .Where(x =>
                             x.requiredLevel == 1 &&
                             x != ArmorOfShadows &&
                             x != InvocationsBuilders.EldritchMind &&
                             (InvocationsContext.Invocations.Contains(x) ||
                              x.ContentPack != CeContentPackContext.CeContentPack)))
            {
                invocation.requiredLevel = 2;
            }

            InvocationPactBlade.GuiPresentation.hidden = false;
            InvocationPactChain.GuiPresentation.hidden = false;
            InvocationPactTome.GuiPresentation.hidden = false;

            OneWithShadows.GuiPresentation.description = "Invocation/&OneWithShadowsAlternateDescription";
            OneWithShadows.grantedFeature = PowerInvocationOneWithShadows;

            FeatureSetPactBlade.GuiPresentation.description = "Feature/&FeatureSetPactBladeAlternateDescription";

            OtherworldlyLeap.requiredLevel = 2;
        }
        else
        {
            Warlock.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == PointPoolWarlockInvocation1);
            PointPoolWarlockInvocation2.GuiPresentation.Title =
                "Feature/&PointPoolWarlockInvocationInitialTitle";
            PointPoolWarlockInvocation2.GuiPresentation.Description =
                "Feature/&PointPoolWarlockInvocationInitialDescription";
            PointPoolWarlockInvocation5.poolAmount = 1;

            foreach (var invocation in DatabaseRepository.GetDatabase<InvocationDefinition>()
                         .Where(x =>
                             x.requiredLevel == 2 &&
                             x != ArmorOfShadows &&
                             x != InvocationsBuilders.EldritchMind &&
                             (InvocationsContext.Invocations.Contains(x) ||
                              x.ContentPack != CeContentPackContext.CeContentPack)))
            {
                invocation.requiredLevel = 1;
            }

            InvocationPactBlade.GuiPresentation.hidden = true;
            InvocationPactChain.GuiPresentation.hidden = true;
            InvocationPactTome.GuiPresentation.hidden = true;

            Warlock.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureSetPactSelection, 3));

            OneWithShadows.GuiPresentation.description = "Invocation/&OneWithShadowsDescription";
            OneWithShadows.grantedFeature = LightAffinityInvocationOneWithShadows;

            FeatureSetPactBlade.GuiPresentation.description = "Feature/&FeatureSetPactBladeDescription";

            OtherworldlyLeap.requiredLevel = 9;
        }

        GuiWrapperContext.RecacheInvocations();

        Warlock.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private static bool IsPactBladeWeapon(
        RulesetAttackMode mode,
        RulesetItem _,
        RulesetCharacter character)
    {
        return mode?.SourceDefinition is ItemDefinition { IsWeapon: true } itemDefinition &&
               (ValidatorsWeapon.IsMelee(itemDefinition) ||
                (ValidatorsWeapon.IsTwoHandedRanged(mode) &&
                 character.HasActiveInvocation(InvocationsBuilders.ImprovedPactWeapon)));
    }

    private sealed class PowerOrSpellFinishedByMeInvocationOneWithShadows : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            action.ActingCharacter.MyExecuteActionCastNoCost(InvisibilityOneWithShadows, 0, action.ActionParams);

            yield break;
        }
    }

    private sealed class PowerOrSpellFinishedByMeMagicalCunning : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            var character = action.ActingCharacter.RulesetCharacter;
            var repertoire = character.SpellRepertoires.FirstOrDefault(candidate =>
                candidate?.SpellCastingClass == Warlock);
            var warlockClassLevel = character switch
            {
                RulesetCharacterHero hero => hero.GetClassLevel(Warlock),
                RulesetCharacterSimulacrum simulacrum
                    when SimulacrumBehavior.TryGetClassLevels(simulacrum, out var classLevels) =>
                    classLevels
                        .Where(entry => entry.ClassDefinition == Warlock)
                        .Select(entry => entry.Level)
                        .FirstOrDefault(),
                _ => 0
            };

            if (repertoire == null || warlockClassLevel <= 0)
            {
                yield break;
            }

            var slotLevel = character.SpellRepertoires.Count(
                                candidate => candidate.UsesSharedSpellSlots()) > 1
                ? SharedSpellsContext.PactMagicSlotsTab
                : repertoire.spellsSlotCapacities
                    .Where(pair => pair.Key is > 0 and <= 9 && pair.Value > 0)
                    .Select(pair => pair.Key)
                    .DefaultIfEmpty()
                    .Max();
            repertoire.spellsSlotCapacities.TryGetValue(slotLevel, out var maxSlots);
            var halfSlotsRoundUp = (maxSlots + 1) / (warlockClassLevel == 20 ? 1 : 2);

            if (!repertoire.usedSpellsSlots.TryGetValue(slotLevel, out var value))
            {
                yield break;
            }

            repertoire.usedSpellsSlots[slotLevel] -= Math.Min(value, halfSlotsRoundUp);

            if (value > 0)
            {
                repertoire.RepertoireRefreshed?.Invoke(repertoire);
            }
        }
    }
}
