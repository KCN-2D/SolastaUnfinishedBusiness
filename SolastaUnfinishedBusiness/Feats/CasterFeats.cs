using System.Collections.Generic;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Properties;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionAttributeModifiers;

namespace SolastaUnfinishedBusiness.Feats;

internal static class CasterFeats
{
    internal static readonly Dictionary<string, SpellDefinition[]> MagicTouchedData = new()
    {
        { "AegisTouched", [ShieldOfFaith, ProtectionFromEvilGood, ProtectionFromPoison] },
        { "CelestialTouched", [HealingWord, CureWounds, LesserRestoration] },
        { "FlameTouched", [BurningHands, HellishRebuke, ScorchingRay] },
        { "IridescentTouched", [ColorSpray, FaerieFire, SpellsContext.ColorBurst] },
        { "PeregrinationTouched", [Longstrider, ExpeditiousRetreat, SpiderClimb] },
        { "RetinueTouched", [Bless, Heroism, EnhanceAbility] },
        { "ShadowTouched", [Invisibility, FalseLife, InflictWounds] },
        { "VerdantTouched", [Barkskin, Entangle, Goodberry] }
    };

    internal static void CreateFeats([NotNull] List<FeatDefinition> feats)
    {
        var groups = new List<FeatDefinition>();
        var groupFeats = new List<FeatDefinition>();

        #region Telekinetic

        const string TELEKINETIC = "Telekinetic";

        groupFeats.SetRange(FeatDefinitionBuilder
                .Create($"Feat{TELEKINETIC}Int")
                .SetGuiPresentation(Category.Feat)
                .SetFeatures(
                    BuildTelekinesis(AttributeDefinitions.Intelligence, MotionForm.MotionType.DragToOrigin),
                    BuildTelekinesis(AttributeDefinitions.Intelligence, MotionForm.MotionType.PushFromOrigin),
                    AttributeModifierCreed_Of_Pakri)
                .SetFeatFamily(TELEKINETIC)
                .AddToDB(),
            FeatDefinitionBuilder
                .Create($"Feat{TELEKINETIC}Cha")
                .SetGuiPresentation(Category.Feat)
                .SetFeatures(
                    BuildTelekinesis(AttributeDefinitions.Charisma, MotionForm.MotionType.DragToOrigin),
                    BuildTelekinesis(AttributeDefinitions.Charisma, MotionForm.MotionType.PushFromOrigin),
                    AttributeModifierCreed_Of_Solasta)
                .SetFeatFamily(TELEKINETIC)
                .AddToDB(),
            FeatDefinitionBuilder
                .Create($"Feat{TELEKINETIC}Wis")
                .SetGuiPresentation(Category.Feat)
                .SetFeatures(
                    BuildTelekinesis(AttributeDefinitions.Wisdom, MotionForm.MotionType.DragToOrigin),
                    BuildTelekinesis(AttributeDefinitions.Wisdom, MotionForm.MotionType.PushFromOrigin),
                    AttributeModifierCreed_Of_Maraike)
                .SetFeatFamily(TELEKINETIC)
                .AddToDB());

        groups.Add(GroupFeats.MakeGroup($"FeatGroup{TELEKINETIC}", TELEKINETIC, groupFeats));
        feats.AddRange(groupFeats);

        #endregion

        #region Fey Teleportation

        const string FEY_TELEPORT = "FeyTeleport";

        SpellDefinition[] spells = [MistyStep];

        var autoPreparedSpells = FeatureDefinitionAutoPreparedSpellsBuilder
            .Create($"AutoPreparedSpells{FEY_TELEPORT}ation")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(ValidateRepertoireForAutoprepared.AnyClassOrSubclass)
            .SetSpellcastingClass(null)
            .SetAutoTag(FEY_TELEPORT)
            .AddToDB();

        var learnTirmarian = FeatureDefinitionProficiencyBuilder
            .Create($"ProficiencyFeat{FEY_TELEPORT}ationTirmarian")
            .SetGuiPresentationNoContent(true)
            .SetProficiencies(ProficiencyType.Language, "Language_Tirmarian")
            .AddToDB();

        groupFeats.SetRange(
            FeatDefinitionBuilder
                .Create($"Feat{FEY_TELEPORT}ationInt")
                .SetFeatures(autoPreparedSpells, AttributeModifierCreed_Of_Pakri, learnTirmarian)
                .AddFeatures(BuildFreeSpellFeatures(spells, FEY_TELEPORT, AttributeDefinitions.Intelligence,
                    RechargeRate.ShortRest))
                .SetGuiPresentation(Category.Feat)
                .SetFeatFamily(FEY_TELEPORT)
                .AddToDB(),
            FeatDefinitionBuilder
                .Create($"Feat{FEY_TELEPORT}ationCha")
                .SetFeatures(autoPreparedSpells, AttributeModifierCreed_Of_Solasta, learnTirmarian)
                .AddFeatures(BuildFreeSpellFeatures(spells, FEY_TELEPORT, AttributeDefinitions.Charisma,
                    RechargeRate.ShortRest))
                .SetGuiPresentation(Category.Feat)
                .SetFeatFamily(FEY_TELEPORT)
                .AddToDB(),
            FeatDefinitionBuilder
                .Create($"Feat{FEY_TELEPORT}ationWis")
                .SetFeatures(autoPreparedSpells, AttributeModifierCreed_Of_Maraike, learnTirmarian)
                .AddFeatures(BuildFreeSpellFeatures(spells, FEY_TELEPORT, AttributeDefinitions.Wisdom,
                    RechargeRate.ShortRest))
                .SetGuiPresentation(Category.Feat)
                .SetFeatFamily(FEY_TELEPORT)
                .AddToDB());

        groups.Add(GroupFeats.MakeGroup($"FeatGroup{FEY_TELEPORT}", FEY_TELEPORT, groupFeats));
        feats.AddRange(groupFeats);

        #endregion

        #region Touched Magic

        const string TOUCHED_MAGIC = "TouchedMagic";

        var featGroups = new List<FeatDefinition>();

        foreach (var kvp in MagicTouchedData)
        {
            var tag = kvp.Key;

            spells = kvp.Value;
            autoPreparedSpells = FeatureDefinitionAutoPreparedSpellsBuilder
                .Create($"AutoPreparedSpellsFeat{tag}")
                .SetGuiPresentationNoContent(true)
                .AddCustomSubFeatures(ValidateRepertoireForAutoprepared.AnyClassOrSubclass)
                .SetSpellcastingClass(null)
                .SetAutoTag(tag)
                .AddToDB();

            groupFeats.SetRange(
                FeatDefinitionBuilder
                    .Create($"Feat{tag}Int")
                    .SetFeatures(autoPreparedSpells, AttributeModifierCreed_Of_Pakri)
                    .AddFeatures(BuildFreeSpellFeatures(spells, tag, AttributeDefinitions.Intelligence))
                    .SetGuiPresentation(Category.Feat)
                    .SetFeatFamily(TOUCHED_MAGIC)
                    .SetMustCastSpellsPrerequisite()
                    .AddToDB(),
                FeatDefinitionBuilder
                    .Create($"Feat{tag}Wis")
                    .SetFeatures(autoPreparedSpells, AttributeModifierCreed_Of_Maraike)
                    .AddFeatures(BuildFreeSpellFeatures(spells, tag, AttributeDefinitions.Wisdom))
                    .SetGuiPresentation(Category.Feat)
                    .SetFeatFamily(TOUCHED_MAGIC)
                    .SetMustCastSpellsPrerequisite()
                    .AddToDB(),
                FeatDefinitionBuilder
                    .Create($"Feat{tag}Cha")
                    .SetFeatures(autoPreparedSpells, AttributeModifierCreed_Of_Solasta)
                    .AddFeatures(BuildFreeSpellFeatures(spells, tag, AttributeDefinitions.Charisma))
                    .SetGuiPresentation(Category.Feat)
                    .SetFeatFamily(TOUCHED_MAGIC)
                    .SetMustCastSpellsPrerequisite()
                    .AddToDB());

            featGroups.Add(GroupFeats.MakeGroup($"FeatGroup{tag}", TOUCHED_MAGIC, groupFeats));
        }

        groups.Add(GroupFeats.MakeGroup("FeatGroupTouchedMagic", TOUCHED_MAGIC, featGroups));

        #endregion

        GroupFeats.FeatGroupSpellCombat.AddFeats(groups.ToArray());
    }

    [NotNull]
    private static FeatureDefinition[] BuildFreeSpellFeatures(
        IEnumerable<SpellDefinition> spells,
        string name,
        string castingAttribute, RechargeRate rechargeRate = RechargeRate.LongRest)
    {
        var features = new List<FeatureDefinition>();
        var invocations = new List<InvocationDefinition>();
        foreach (var spell in spells)
        {
            // Keep the original repertoire identity for the first spell. Each other
            // spell owns its free use, including reactions unsupported by the old menu.
            var featureName = features.Count == 0
                ? $"CastSpell{name}{castingAttribute}"
                : $"CastSpell{name}{spell.Name}{castingAttribute}";
            var spellList = SpellListDefinitionBuilder
                .Create($"SpellList{featureName}")
                .SetGuiPresentationNoContent(true)
                .ClearSpells()
                .SetSpellsAtLevel(spell.SpellLevel, spell)
                .FinalizeSpells(false, spell.SpellLevel)
                .AddToDB();
            var spellFeature = FeatHelpers.BuildFixedFreeSpellCasting(
                featureName, spellList, castingAttribute, name, spell.SpellLevel, rechargeRate,
                allowSlotCasting: true);
            features.Add(spellFeature);

            if (spell.castingTime == ActivationTime.Reaction)
            {
                spellFeature.AddCustomSubFeatures(new FeatHelpers.LegacyInvocationFreeCast(null, spell.SpellLevel));
                continue;
            }

            // Hidden compatibility definitions resolve saved invocations. Casting
            // and consumption now belong exclusively to the native spell repertoire.
            var invocation = CustomInvocationDefinitionBuilder
                .Create($"CustomInvocation{name}{spell.Name}{castingAttribute}")
                .SetGuiPresentation(spell.GuiPresentation)
                .AddCustomSubFeatures(ModifyInvocationVisibility.Marker,
                    ValidateRepertoireForAutoprepared.HasSpellCastingFeature(featureName))
                .SetPoolType(InvocationPoolTypeCustom.Pools.PlaneMagic)
                .SetGrantedSpell(spell, longRestRecharge: rechargeRate == RechargeRate.LongRest)
                .AddToDB();

            if (rechargeRate == RechargeRate.ShortRest)
            {
                invocation.AddCustomSubFeatures(RechargeInvocationOnShortRest.Marker);
            }

            invocations.Add(invocation);
            spellFeature.AddCustomSubFeatures(
                new FeatHelpers.LegacyInvocationFreeCast(invocation.Name, spell.SpellLevel));
        }

        var grant = FeatureDefinitionGrantInvocationsBuilder
            .Create($"GrantInvocations{name}{castingAttribute}")
            .SetGuiPresentationNoContent(true)
            .SetInvocations(invocations)
            .AddToDB();

        features.Add(grant);
        return features.ToArray();
    }

    [NotNull]
    private static FeatureDefinitionGrantInvocations BuildTelekinesis(
        string savingThrowDifficultyAbility, MotionForm.MotionType motionType)
    {
        const string NAME = "FeatTelekinetic";

        var motionTypeName = motionType.ToString();

        var isPull = motionType == MotionForm.MotionType.DragToOrigin;
        var sprite = Sprites.GetPowerSprite(motionTypeName,
            isPull ? Resources.TelekinesisPull : Resources.TelekinesisPush);

        if (isPull)
        {
            motionTypeName = string.Empty;
        }

        var power = FeatureDefinitionPowerBuilder
            .Create($"Power{NAME}{savingThrowDifficultyAbility}{motionTypeName}")
            .SetGuiPresentation($"{NAME}{motionTypeName}", Category.Feature, sprite)
            .SetUsesFixed(ActivationTime.BonusAction)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.All, RangeType.Distance, 6, TargetType.IndividualsUnique)
                    .SetSavingThrowData(
                        true,
                        AttributeDefinitions.Strength,
                        true,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency,
                        savingThrowDifficultyAbility)
                    .ExcludeCaster()
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetMotionForm(motionType, 1)
                            .Build())
                    .SetParticleEffectParameters(PowerSpellBladeSpellTyrant)
                    .Build())
            .AddToDB();

        var invocation = CustomInvocationDefinitionBuilder
            .Create($"CustomInvocation{NAME}{savingThrowDifficultyAbility}{motionTypeName}")
            .SetGuiPresentation(power.GuiPresentation)
            .SetPoolType(InvocationPoolTypeCustom.Pools.PlaneMagic)
            .SetGrantedFeature(power)
            .AddCustomSubFeatures(ModifyInvocationVisibility.Marker)
            .AddToDB();

        return FeatureDefinitionGrantInvocationsBuilder
            .Create($"GrantInvocations{NAME}{savingThrowDifficultyAbility}{motionTypeName}")
            .SetGuiPresentationNoContent(true)
            .SetInvocations(invocation)
            .AddToDB();
    }
}
