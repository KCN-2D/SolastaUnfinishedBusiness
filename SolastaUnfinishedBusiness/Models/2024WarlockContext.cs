using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.CustomUI;
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
    private static readonly InvocationDefinition InvocationFiendishVigor = GetDefinition<InvocationDefinition>("FiendishVigor");
    private static readonly InvocationDefinition InvocationAscendantStep = GetDefinition<InvocationDefinition>("AscendantStep");
    private static readonly InvocationDefinition InvocationLifedrinker = GetDefinition<InvocationDefinition>("Lifedrinker");

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
                AttributeDefinitions.Charisma, IsPactBladeWeapon,
                _ => Main.Settings.EnableWarlockInvocationProgression2024),
            new WeaponDamageTypeChoice(
                () => Main.Settings.EnableWarlockInvocationProgression2024, IsPactBladeWeapon,
                DamageTypeNecrotic, DamageTypePsychic, DamageTypeRadiant),
            WeaponDamageTypeChoice.Handler)
        .AddToDB();

    private static readonly SpellDefinition FalseLifeFiendishVigor = SpellDefinitionBuilder
        .Create(FalseLife, "FalseLifeFiendishVigor")
        .SetGuiPresentation(FalseLife.GuiPresentation.Title, "Invocation/&FiendishVigor2024Description", FalseLife)
        .AddToDB();

    internal static void RefreshFiendishVigor()
    {
        // Keep the invocation's dice tied to the effective False Life rules, without maximizing
        // ordinary spell-slot casts made by a character who also knows this invocation.
        FalseLifeFiendishVigor.effectDescription = EffectDescriptionBuilder.Create(FalseLife).Build();

        foreach (var form in FalseLifeFiendishVigor.EffectDescription.EffectForms
                     .Where(form => form.FormType == EffectForm.EffectFormType.TemporaryHitPoints))
        {
            var temporaryHitPoints = form.TemporaryHitPointsForm;
            temporaryHitPoints.BonusHitPoints += temporaryHitPoints.DiceNumber * DiceMaxValue[(int)temporaryHitPoints.DieType];
            temporaryHitPoints.DiceNumber = 0;
        }

        PowerBundle.ClearSpellEffectCacheForDefinition(FalseLifeFiendishVigor);
        InvocationFiendishVigor.grantedSpell = Main.Settings.EnableWarlockInvocationProgression2024
            ? FalseLifeFiendishVigor
            : FalseLife;
        InvocationFiendishVigor.GuiPresentation.description = Main.Settings.EnableWarlockInvocationProgression2024
            ? "Invocation/&FiendishVigor2024Description"
            : "Invocation/&FiendishVigorDescription";
    }

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

    private static readonly FeatureDefinitionFeatureSet FeatureSetPactTome2024 = FeatureDefinitionFeatureSetBuilder
        .Create("FeatureSetPactTome2024")
        .SetGuiPresentation(FeatureSetPactTome.GuiPresentation.Title, "Feature/&PactTome2024Description")
        .AddFeatureSet(
            FeatureSetPactTome.FeatureSet[0],
            FeatureDefinitionPointPoolBuilder.Create(
                    GetDefinition<FeatureDefinitionPointPool>("PointPoolInvocationBookAncientSecrets"),
                    "PointPoolPactTomeRitual2024")
                .SetGuiPresentation(FeatureSetPactTome.GuiPresentation.Title, "Feature/&PactTome2024Description")
                .AddToDB(),
            GetDefinition<FeatureDefinitionMagicAffinity>("MagicAffinityInvocationBookAncientSecrets"),
            GetDefinition<FeatureDefinitionActionAffinity>("ActionAffinityInvocationBookAncientSecrets"))
        .AddToDB();

    internal static void GrantPactTomeRitualSpells(RulesetCharacterHero hero)
    {
        var repertoire = hero.SpellRepertoires.FirstOrDefault(spells => spells.SpellCastingClass == Warlock);

        if (repertoire == null)
        {
            return;
        }

        foreach (var entry in hero.GetHeroBuildingData().AcquiredSpells
                     .Where(entry => entry.Key.Contains("PactTomeRitual")))
        {
            if (!repertoire.ExtraSpellsByTag.TryGetValue(entry.Key, out var spells))
            {
                spells = [];
                repertoire.ExtraSpellsByTag.Add(entry.Key, spells);
            }

            foreach (var spell in entry.Value.Where(spell => !spells.Contains(spell)))
            {
                spells.Add(spell);
            }
        }
    }

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

    private static Dictionary<InvocationDefinition, int> _invocationLevels2014;

    internal static void SwitchWarlockInvocationsProgression()
    {
        Warlock.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == FeatureSetPactSelection || x.FeatureDefinition == PointPoolWarlockInvocation1);

        _invocationLevels2014 ??= DatabaseRepository.GetDatabase<InvocationDefinition>()
            .Where(invocation => InvocationsContext.Invocations.Contains(invocation) ||
                                 invocation.ContentPack != CeContentPackContext.CeContentPack)
            .ToDictionary(invocation => invocation, invocation => invocation.RequiredLevel);

        foreach (var entry in _invocationLevels2014)
        {
            var invocation = entry.Key;
            var level = entry.Value;

            invocation.requiredLevel = Main.Settings.EnableWarlockInvocationProgression2024 &&
                                       level == 1 && invocation != ArmorOfShadows &&
                                       invocation != InvocationsBuilders.EldritchMind
                ? 2
                : level;
        }

        if (Main.Settings.EnableWarlockInvocationProgression2024)
        {
            Warlock.FeatureUnlocks.Add(new FeatureUnlockByLevel(PointPoolWarlockInvocation1, 1));
            PointPoolWarlockInvocation2.GuiPresentation.Title =
                "Feature/&PointPoolWarlockInvocationAdditionalTitle";
            PointPoolWarlockInvocation2.GuiPresentation.Description =
                "Feature/&PointPoolWarlockInvocationAdditionalDescription";
            PointPoolWarlockInvocation5.poolAmount = 2;

            InvocationPactBlade.GuiPresentation.hidden = false;
            InvocationPactChain.GuiPresentation.hidden = false;
            InvocationPactTome.GuiPresentation.hidden = false;
            InvocationPactTome.grantedFeature = FeatureSetPactTome2024;
            InvocationPactTome.GuiPresentation.description = "Feature/&PactTome2024Description";

            OneWithShadows.GuiPresentation.description = "Invocation/&OneWithShadowsAlternateDescription";
            OneWithShadows.grantedFeature = PowerInvocationOneWithShadows;

            FeatureSetPactBlade.GuiPresentation.description = "Feature/&FeatureSetPactBladeAlternateDescription";

            OtherworldlyLeap.requiredLevel = 2;
            InvocationAscendantStep.requiredLevel = 5;
            InvocationLifedrinker.requiredLevel = 9;
        }
        else
        {
            Warlock.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == PointPoolWarlockInvocation1);
            PointPoolWarlockInvocation2.GuiPresentation.Title =
                "Feature/&PointPoolWarlockInvocationInitialTitle";
            PointPoolWarlockInvocation2.GuiPresentation.Description =
                "Feature/&PointPoolWarlockInvocationInitialDescription";
            PointPoolWarlockInvocation5.poolAmount = 1;

            InvocationPactBlade.GuiPresentation.hidden = true;
            InvocationPactChain.GuiPresentation.hidden = true;
            InvocationPactTome.GuiPresentation.hidden = true;
            InvocationPactTome.grantedFeature = FeatureSetPactTome.FeatureSet[0];
            InvocationPactTome.GuiPresentation.description = FeatureSetPactTome.GuiPresentation.Description;

            Warlock.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureSetPactSelection, 3));

            OneWithShadows.GuiPresentation.description = "Invocation/&OneWithShadowsDescription";
            OneWithShadows.grantedFeature = LightAffinityInvocationOneWithShadows;

            FeatureSetPactBlade.GuiPresentation.description = "Feature/&FeatureSetPactBladeDescription";

            OtherworldlyLeap.requiredLevel = 9;
        }

        RefreshFiendishVigor();
        SwitchWarlockInvocationEffects();
        SwitchWarlockCantripInvocations();

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
