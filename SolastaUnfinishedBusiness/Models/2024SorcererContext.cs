using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Validators;
using static FeatureDefinitionAttributeModifier;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionActionAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPointPools;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSubclassChoices;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly ConditionalWeakTable<RulesetCondition, SpellEffectOrigin>
        SpellDerivedConditionOrigins = new();

    private static readonly ConditionalWeakTable<RulesetEffectPower, SpellEffectOrigin>
        SpellDerivedPowerOrigins = new();

    private sealed class SpellEffectOrigin(
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spellDefinition,
        ulong casterGuid,
        int baseSaveDc,
        bool useSpellListClassification,
        RulesetEffectSpell spellEffect)
    {
        // Keep the originating effect, rather than a metamagic snapshot: selection can finish
        // after an origin is first bound, and recurring powers must retain that final choice.
        internal RulesetEffectSpell SpellEffect { get; } = spellEffect;
        internal RulesetSpellRepertoire SpellRepertoire { get; } = spellRepertoire;
        internal SpellDefinition SpellDefinition { get; } = spellDefinition;
        internal ulong CasterGuid { get; } = casterGuid;
        internal int BaseSaveDc { get; } = baseSaveDc;
        internal bool UseSpellListClassification { get; } = useSpellListClassification;
    }

    private static readonly FeatureDefinitionDamageAffinity DamageAffinitySorcererDraconicElementalResistance2024 =
        FeatureDefinitionDamageAffinityBuilder
            .Create(GetDefinition<FeatureDefinitionDamageAffinity>("DamageAffinitySorcererDraconicElementalResistance"),
                "DamageAffinitySorcererDraconicElementalResistance2024")
            .SetGuiPresentation("Feature/&PowerSorcererDraconicElementalResistanceTitle",
                "Feature/&DamageAffinitySorcererDraconicElementalResistance2024Description")
            .AddToDB();

    private static readonly CharacterFeatureReplacement DraconicResistanceReplacement = new(
        Sorcerer,
        CharacterSubclassDefinitions.SorcerousDraconicBloodline,
        6,
        PowerSorcererDraconicElementalResistance,
        DamageAffinitySorcererDraconicElementalResistance2024,
        () => Main.Settings.EnableSorcererDraconicBloodlineResistance2024);

    internal static void SwitchSorcererDraconicBloodlineElementalAffinity()
    {
        var unlocks = CharacterSubclassDefinitions.SorcerousDraconicBloodline.FeatureUnlocks;
        var level = Main.Settings.EnableSorcererDraconicBloodlineElementalAffinity2024
            ? 6
            : Main.Settings.EnableSorcererOrigin2024 ? 3 : 1;

        foreach (var unlock in unlocks.Where(x =>
                     x.FeatureDefinition == FeatureDefinitionFeatureSets.FeatureSetSorcererDraconicChoice ||
                     x.FeatureDefinition == RulesContext.InvocationPoolSorcererDraconicChoice))
        {
            unlock.level = level;
        }

        unlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchSorcererDraconicBloodlineResistance()
    {
        DraconicResistanceReplacement.Apply();
    }

    internal static void SynchronizeSorcererFeatures(RulesetCharacterHero hero)
    {
        DraconicResistanceReplacement.Synchronize(hero);
    }

    private static readonly ConditionDefinition ConditionSorcererInnateSorcery = ConditionDefinitionBuilder
        .Create("ConditionSorcererInnateSorcery")
        .SetGuiPresentation(Category.Condition, ConditionAuraOfCourage)
        .AddCustomSubFeatures(new ModifyMagicEffectAttackModifierInnateSorcery())
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerSorcererInnateSorcery = FeatureDefinitionPowerBuilder
        .Create("PowerSorcererInnateSorcery")
        .SetGuiPresentation(Category.Feature, PowerTraditionShockArcanistGreaterArcaneShock)
        .SetUsesFixed(ActivationTime.BonusAction, RechargeRate.LongRest, 1, 2)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.Minute, 1)
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetEffectForms(EffectFormBuilder.ConditionForm(ConditionSorcererInnateSorcery))
                .SetCasterEffectParameters(PowerSorcererDraconicElementalResistance)
                .Build())
        .AddCustomSubFeatures(new ValidatorsValidatePowerUse(c =>
            c.GetClassLevel(Sorcerer) < 7 || c.GetRemainingPowerUses(PowerSorcererInnateSorcery) > 0))
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerSorcererSorceryIncarnate = FeatureDefinitionPowerBuilder
        .Create(PowerSorcererInnateSorcery, "PowerSorcererSorceryIncarnate")
        .SetUsesFixed(ActivationTime.BonusAction, RechargeRate.SorceryPoints, 2, 0)
        .AddCustomSubFeatures(new ValidatorsValidatePowerUse(c =>
            c.GetClassLevel(Sorcerer) >= 7 && c.GetRemainingPowerUses(PowerSorcererInnateSorcery) == 0))
        .AddToDB();

    private static readonly FeatureDefinitionFeatureSet FeatureSetSorcererSorceryIncarnate =
        FeatureDefinitionFeatureSetBuilder
            .Create("FeatureSetSorcererSorceryIncarnate")
            .SetGuiPresentation(Category.Feature)
            .SetFeatureSet(PowerSorcererSorceryIncarnate)
            .AddToDB();

    // Keep the former refund marker registered so older saves can resolve its definition.
    private static readonly ConditionDefinition ConditionArcaneApotheosis = ConditionDefinitionBuilder
        .Create("ConditionArcaneApotheosis")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .SetFixedAmount(0)
        .AddToDB();

    private static readonly FeatureDefinition FeatureSorcererArcaneApotheosis =
        FeatureDefinitionBuilder
            .Create("FeatureSorcererArcaneApotheosis")
            .SetGuiPresentation(Category.Feature)
            .AddToDB();

    private static readonly FeatureDefinitionPower PowerSorcerousRestoration = FeatureDefinitionPowerBuilder
        .Create(PowerSorcererManaPainterTap, "PowerSorcerousRestoration")
        .SetOrUpdateGuiPresentation(Category.Feature)
        .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
        .AddToDB();


    internal static void SwitchSorcererArcaneApotheosis()
    {
        Sorcerer.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == FeatureSorcererArcaneApotheosis ||
            x.FeatureDefinition == Level20Context.PowerSorcerousRestoration);

        Sorcerer.FeatureUnlocks.Add(
            Main.Settings.EnableSorcererArcaneApotheosis2024
                ? new FeatureUnlockByLevel(FeatureSorcererArcaneApotheosis, 20)
                : new FeatureUnlockByLevel(Level20Context.PowerSorcerousRestoration, 20));

        Sorcerer.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchSorcererMetamagic()
    {
        MetamagicContext.SwitchSorcererMetamagicRules2024();

        Sorcerer.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == PointPoolSorcererMetamagic ||
            x.FeatureDefinition == PointPoolSorcererAdditionalMetamagic ||
            x.FeatureDefinition == ActionAffinitySorcererMetamagicToggle);

        if (Main.Settings.EnableSorcererMetamagic2024)
        {
            Sorcerer.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(PointPoolSorcererMetamagic, 2),
                new FeatureUnlockByLevel(ActionAffinitySorcererMetamagicToggle, 2),
                new FeatureUnlockByLevel(PointPoolSorcererMetamagic, 10),
                new FeatureUnlockByLevel(PointPoolSorcererMetamagic, 17));
        }
        else
        {
            Sorcerer.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(PointPoolSorcererMetamagic, 3),
                new FeatureUnlockByLevel(ActionAffinitySorcererMetamagicToggle, 3),
                new FeatureUnlockByLevel(PointPoolSorcererAdditionalMetamagic, 17));
        }

        Sorcerer.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchSorcererOriginLearningLevel()
    {
        var origins = DatabaseRepository.GetDatabase<CharacterSubclassDefinition>()
            .Where(x => x.Name.StartsWith("Sorcerous"))
            .ToList();

        var fromLevel = 3;
        var toLevel = 1;

        if (Main.Settings.EnableSorcererOrigin2024)
        {
            fromLevel = 1;
            toLevel = 3;
        }

        // handle level 2 grants
        var featuresGrantedAt2 = new[]
        {
            ("SorcerousManaPainter", "PowerSorcererManaPainterDrain"),
            ("SorcerousChildRift", "PowerSorcererChildRiftDeflection"),
            ("SorcerousSpellBlade", "FeatureSetSorcerousSpellBladeManaShield")
        };

        var level = Main.Settings.EnableSorcererOrigin2024 ? 3 : 2;

        foreach (var (subClassName, featureName) in featuresGrantedAt2)
        {
            var subClass = GetDefinition<CharacterSubclassDefinition>(subClassName);
            var feature = GetDefinition<FeatureDefinition>(featureName);

            subClass.FeatureUnlocks.FirstOrDefault(x => x.FeatureDefinition == feature)!.level = level;
        }

        SwitchSubclassLearningLevel(origins, Sorcerer, SubclassChoiceSorcerousOrigin, fromLevel, toLevel);
        SwitchSorcererDraconicBloodlineElementalAffinity();
    }

    internal static bool IsArcaneApotheosisValid(RulesetCharacter rulesetCharacter, RulesetEffect rulesetEffect)
    {
        var character = GameLocationCharacter.GetFromActor(rulesetCharacter);

        return IsArcaneApotheosisValid(character, rulesetEffect, false);
    }

    private static bool IsArcaneApotheosisValid(
        GameLocationCharacter character,
        RulesetEffect rulesetEffect,
        bool validateMetamagicOption = true)
    {
        if (character == null || !Main.Settings.EnableSorcererArcaneApotheosis2024 ||
            rulesetEffect is not RulesetEffectSpell rulesetEffectSpell ||
            (validateMetamagicOption && !rulesetEffectSpell.MetamagicOption))
        {
            return false;
        }

        var rulesetCharacter = character.RulesetCharacter;
        var sorcererLevel = rulesetCharacter.GetClassLevel(Sorcerer);

        if (sorcererLevel < 20)
        {
            return false;
        }

        if (Gui.Battle != null &&
            (Gui.Battle.ActiveContender != character ||
             !character.OnceInMyTurnIsValid(FeatureSorcererArcaneApotheosis.Name)))
        {
            return false;
        }

        return rulesetCharacter.HasConditionOfCategoryAndType(
            AttributeDefinitions.TagEffect, ConditionSorcererInnateSorcery.Name);
    }

    internal static void ModifyInnateSorcerySaveDc(
        RulesetEffectSpell spellEffect,
        ref int saveDc)
    {
        // Some spells resolve saving throws outside their effect forms. The DC belongs
        // to the sorcerer spell even when its description has no native saving throw.
        if (IsInnateSorceryValid(spellEffect))
        {
            saveDc++;
        }
    }

    internal static void ModifyInnateSorcerySaveDc(
        RulesetEffectPower powerEffect,
        ref int saveDc)
    {
        if (!TryGetSpellDerivedPowerOrigin(powerEffect, out var spellOrigin) ||
            !IsSorcererSpell(
                spellOrigin.SpellRepertoire,
                spellOrigin.SpellDefinition,
                EffectHelpers.GetCharacterByGuid(spellOrigin.CasterGuid),
                spellOrigin.UseSpellListClassification))
        {
            return;
        }

        var spellRepertoire = spellOrigin.SpellRepertoire;

        if (powerEffect.EffectDescription.DifficultyClassComputation ==
            EffectDifficultyClassComputation.SpellCastingFeature)
        {
            saveDc = spellRepertoire?.SaveDC ?? spellOrigin.BaseSaveDc;
        }

        if (IsInnateSorceryValid(spellOrigin))
        {
            saveDc++;
        }
    }

    internal static void ModifyInnateSorcerySaveDc(
        RulesetCondition condition,
        ref int saveDc)
    {
        if (condition != null &&
            IsInnateSorceryValid(GetSpellDerivedConditionOrigin(condition)))
        {
            saveDc++;
        }
    }

    internal static RulesetEffectSpell GetSpellDerivedPowerSpell(RulesetEffectPower powerEffect)
    {
        return TryGetSpellDerivedPowerOrigin(powerEffect, out var spellOrigin)
            ? spellOrigin.SpellEffect
            : null;
    }

    internal static RulesetEffectSpell GetSpellDerivedConditionSpell(RulesetCondition condition)
    {
        return GetSpellDerivedConditionOrigin(condition)?.SpellEffect;
    }

    internal static RulesetEffectSpell GetSpellDerivedProxySpell(RulesetCharacterEffectProxy proxy)
    {
        var resolving = new HashSet<ulong>();
        while (proxy != null && resolving.Add(proxy.EffectGuid))
        {
            switch (EffectHelpers.GetEffectByGuid(proxy.EffectGuid))
            {
                case RulesetEffectSpell spell:
                    return spell;
                case RulesetEffectPower power:
                    if (power.OriginItem != null || power.User == null || power.PowerDefinition == null)
                    {
                        return null;
                    }
                    if (SpellDerivedPowerOrigins.TryGetValue(power, out var origin))
                    {
                        return origin.SpellEffect;
                    }
                    if (power.User is RulesetCharacterEffectProxy owner)
                    {
                        proxy = owner;
                        break;
                    }
                    return GetSpellDerivedPowerSpell(power);
                default:
                    return null;
            }
        }
        return null;
    }

    internal static void BindSpellDerivedPowerOrigin(RulesetEffectPower powerEffect)
    {
        if (powerEffect == null)
        {
            return;
        }

        SpellDerivedPowerOrigins.Remove(powerEffect);

        if (TryResolveSpellDerivedPowerOrigin(powerEffect, out var spellOrigin))
        {
            SpellDerivedPowerOrigins.Add(powerEffect, spellOrigin);
        }
    }

    internal static void BindSpellDerivedConditionOrigin(
        RulesetCondition condition,
        RulesetEffectSpell spellEffect)
    {
        if (condition == null)
        {
            return;
        }

        SpellDerivedConditionOrigins.Remove(condition);

        var spellOrigin = CreateSpellEffectOrigin(spellEffect, condition.SourceGuid);

        if (spellOrigin != null)
        {
            SpellDerivedConditionOrigins.Add(condition, spellOrigin);
        }
    }

    internal static void UnbindSpellDerivedConditionOrigin(RulesetCondition condition)
    {
        if (condition != null)
        {
            SpellDerivedConditionOrigins.Remove(condition);
        }
    }

    private static bool IsInnateSorceryValid(SpellEffectOrigin spellOrigin)
    {
        return spellOrigin != null &&
               IsInnateSorceryValid(
                   spellOrigin.SpellRepertoire,
                   spellOrigin.SpellDefinition,
                   EffectHelpers.GetCharacterByGuid(spellOrigin.CasterGuid),
                   spellOrigin.UseSpellListClassification);
    }

    private static bool IsInnateSorceryValid(
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spellDefinition,
        RulesetCharacter effectCaster,
        bool useSpellListClassification)
    {
        return IsSorcererSpell(
                   spellRepertoire,
                   spellDefinition,
                   effectCaster,
                   useSpellListClassification) &&
               (HasInnateSorceryCondition(effectCaster) ||
                HasInnateSorceryCondition(spellRepertoire?.GetCaster()));
    }

    private static bool IsInnateSorceryValid(RulesetEffect rulesetEffect)
    {
        if (rulesetEffect is RulesetEffectSpell spellEffect)
        {
            var effectCaster = spellEffect.Caster;

            return effectCaster?.IsSpellCastAsClassOrSubclassSpell(spellEffect, Sorcerer) == true &&
                   (HasInnateSorceryCondition(effectCaster) ||
                    HasInnateSorceryCondition(spellEffect.SpellRepertoire?.GetCaster()));
        }

        return rulesetEffect is RulesetEffectPower powerEffect &&
               TryGetSpellDerivedPowerOrigin(powerEffect, out var spellOrigin) &&
               IsInnateSorceryValid(spellOrigin);
    }

    internal static bool HasInnateSorceryCondition(RulesetCharacter character)
    {
        if (character == null)
        {
            return false;
        }

        if (character.HasConditionOfCategoryAndType(
                AttributeDefinitions.TagEffect,
                ConditionSorcererInnateSorcery.Name))
        {
            return true;
        }

        var featureOwner = character.GetFeatureOwnerOrSelf();

        return featureOwner != character &&
               featureOwner?.HasConditionOfCategoryAndType(
                   AttributeDefinitions.TagEffect,
                   ConditionSorcererInnateSorcery.Name) == true;
    }

    private static bool IsSorcererSpell(
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spellDefinition,
        RulesetCharacter effectCaster,
        bool useSpellListClassification)
    {
        return (effectCaster ?? spellRepertoire?.GetCaster())
            ?.IsSpellCastAsClassOrSubclassSpell(
                spellRepertoire,
                spellDefinition,
                Sorcerer,
                useSpellListClassification) == true;
    }

    private static bool TryGetSpellDerivedPowerOrigin(
        RulesetEffectPower powerEffect,
        out SpellEffectOrigin spellOrigin,
        HashSet<RulesetEntity> resolving = null)
    {
        spellOrigin = null;

        if (powerEffect == null || powerEffect.OriginItem != null)
        {
            return false;
        }

        if (SpellDerivedPowerOrigins.TryGetValue(powerEffect, out spellOrigin))
        {
            return true;
        }

        if (!TryResolveSpellDerivedPowerOrigin(powerEffect, out spellOrigin, resolving))
        {
            return false;
        }

        SpellDerivedPowerOrigins.Add(powerEffect, spellOrigin);

        return true;
    }

    private static bool TryResolveSpellDerivedPowerOrigin(
        RulesetEffectPower powerEffect,
        out SpellEffectOrigin spellOrigin,
        HashSet<RulesetEntity> resolving = null)
    {
        spellOrigin = null;

        if (powerEffect == null ||
            powerEffect.OriginItem != null ||
            powerEffect.User == null ||
            powerEffect.PowerDefinition == null)
        {
            return false;
        }

        resolving ??= [];
        if (!resolving.Add(powerEffect))
        {
            return false;
        }

        try
        {
            // Recurrent proxy powers retain the precise parent spell's native EffectGuid.
            if (powerEffect.User is RulesetCharacterEffectProxy proxy &&
                GetSpellDerivedProxySpell(proxy) is { } parent)
            {
                spellOrigin = CreateSpellEffectOrigin(parent);
                return spellOrigin != null;
            }

            foreach (var condition in powerEffect.User.AllConditions.Where(x =>
                         x.ConditionDefinition.Features.Contains(powerEffect.PowerDefinition)))
            {
                var candidate = GetSpellDerivedConditionOrigin(condition, resolving);

                // A usable power does not identify which equal condition feature granted it.
                // Refuse an unknown or ambiguous origin instead of changing the wrong spell.
                if (!MergeSpellEffectOrigin(ref spellOrigin, candidate))
                {
                    spellOrigin = null;
                    return false;
                }
            }

            return spellOrigin != null;
        }
        finally
        {
            resolving.Remove(powerEffect);
        }
    }

    private static bool MergeSpellEffectOrigin(ref SpellEffectOrigin origin, SpellEffectOrigin candidate)
    {
        if (candidate == null ||
            (origin != null &&
             (origin.SpellRepertoire != candidate.SpellRepertoire ||
              origin.SpellDefinition != candidate.SpellDefinition ||
              origin.CasterGuid != candidate.CasterGuid ||
              origin.BaseSaveDc != candidate.BaseSaveDc ||
              origin.UseSpellListClassification != candidate.UseSpellListClassification ||
              origin.SpellEffect != candidate.SpellEffect)))
        {
            return false;
        }

        origin = candidate;
        return true;
    }

    private static SpellEffectOrigin GetSpellDerivedConditionOrigin(
        RulesetCondition condition, HashSet<RulesetEntity> resolving = null)
    {
        if (condition == null)
        {
            return null;
        }

        if (SpellDerivedConditionOrigins.TryGetValue(condition, out var spellOrigin))
        {
            return spellOrigin;
        }

        resolving ??= [];
        if (!resolving.Add(condition))
        {
            return null;
        }

        try
        {
            var sourceCharacter = EffectHelpers.GetCharacterByGuid(condition.SourceGuid);
            if (sourceCharacter == null)
            {
                return null;
            }

            foreach (var spell in sourceCharacter.SpellsCastByMe
                         .Where(x => x.TrackedConditionGuids.Contains(condition.Guid)))
            {
                if (!MergeSpellEffectOrigin(ref spellOrigin, CreateSpellEffectOrigin(spell, condition.SourceGuid)))
                {
                    return null;
                }
            }

            // A spell-granted power may itself grant another condition and power. Follow the
            // native ownership links, using a path guard rather than guessing by definition name.
            foreach (var power in sourceCharacter.PowersUsedByMe
                         .Where(x => x.TrackedConditionGuids.Contains(condition.Guid)))
            {
                if (!TryGetSpellDerivedPowerOrigin(power, out var candidate, resolving) ||
                    !MergeSpellEffectOrigin(ref spellOrigin, candidate))
                {
                    return null;
                }
            }

            if (spellOrigin != null)
            {
                SpellDerivedConditionOrigins.Add(condition, spellOrigin);
            }

            return spellOrigin;
        }
        finally
        {
            resolving.Remove(condition);
        }
    }

    private static SpellEffectOrigin CreateSpellEffectOrigin(
        RulesetEffectSpell spellEffect,
        ulong fallbackCasterGuid = 0)
    {
        if (spellEffect == null || !spellEffect.SpellDefinition)
        {
            return null;
        }

        var spellDefinition = spellEffect.SpellDefinition;

        if (SpellsContext.SpellsChildMaster.TryGetValue(spellDefinition, out var masterSpell))
        {
            spellDefinition = masterSpell;
        }

        var casterGuid = spellEffect.Caster?.Guid ??
                         (fallbackCasterGuid != 0
                             ? fallbackCasterGuid
                             : spellEffect.SpellRepertoire?.GetCaster()?.Guid ?? 0);

        if (casterGuid == 0)
        {
            return null;
        }

        return new SpellEffectOrigin(
            spellEffect.SpellRepertoire,
            spellDefinition,
            casterGuid,
            GetSpellBaseSaveDc(spellEffect),
            spellEffect.UsesSpellListClassification(),
            spellEffect);
    }

    internal static int GetSpellBaseSaveDc(RulesetEffectSpell spellEffect)
    {
        var saveDc = spellEffect.SaveDC;

        if (IsInnateSorceryValid(spellEffect))
        {
            saveDc--;
        }

        return saveDc;
    }

    internal static void SwitchSorcererInnateSorcery()
    {
        Sorcerer.FeatureUnlocks.RemoveAll(x =>
            x.FeatureDefinition == PowerSorcererInnateSorcery ||
            x.FeatureDefinition == FeatureSetSorcererSorceryIncarnate);

        if (Main.Settings.EnableSorcererInnateSorcery2024)
        {
            Sorcerer.FeatureUnlocks.AddRange(
                new FeatureUnlockByLevel(PowerSorcererInnateSorcery, 1),
                new FeatureUnlockByLevel(FeatureSetSorcererSorceryIncarnate, 7));
        }

        Sorcerer.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private static void LoadSorcererSorcerousRestoration()
    {
        RestActivityDefinitionBuilder
            .Create("RestActivitySorcerousRestoration")
            .SetGuiPresentation(
                "Feature/&PowerSorcerousRestorationShortTitle", "Feature/&PowerSorcerousRestorationDescription")
            .SetRestData(RestDefinitions.RestStage.AfterRest, RestType.ShortRest,
                RestActivityDefinition.ActivityCondition.CanUsePower, "UsePower", PowerSorcerousRestoration.Name)
            .AddToDB();

        PowerSorcerousRestoration.EffectDescription.EffectForms[0].SpellSlotsForm.type =
            (SpellSlotsForm.EffectType)ExtraEffectType.RecoverSorceryHalfLevelDown;
    }

    internal static void SwitchSorcererSorcerousRestorationAtLevel5()
    {
        Sorcerer.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == PowerSorcerousRestoration);

        if (Main.Settings.EnableSorcererSorcerousRestoration2024)
        {
            Sorcerer.FeatureUnlocks.Add(new FeatureUnlockByLevel(PowerSorcerousRestoration, 5));
        }

        Sorcerer.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchSorcererDraconicBloodlineAC()
    {
        var feature = FeatureDefinitionAttributeModifiers.AttributeModifierSorcererDraconicResilienceAC;
        var featureSet = FeatureDefinitionFeatureSets.FeatureSetSorcererDraconicResilience;
        if (Main.Settings.EnableSorcererDraconicBloodlineAC2024)
        {
            feature.modifierOperation = AttributeModifierOperation.SetWithDexPlusOtherAbilityScoreBonusIfBetter;
            feature.modifierValue = 10;
            feature.modifierAbilityScore = AttributeDefinitions.Charisma;

            featureSet.GuiPresentation.Description = "Feature/&FeatureSetSorcererDraconicResilience2024Description";
        }
        else
        {
            feature.modifierOperation = AttributeModifierOperation.Set;
            feature.modifierValue = 13;
            //not really needed, just returning to default
            feature.modifierAbilityScore = AttributeDefinitions.Constitution;

            featureSet.GuiPresentation.Description = "Feature/&FeatureSetSorcererDraconicResilienceDescription";
        }
    }

    internal static void MarkArcaneApotheosisUsed(RulesetCharacter caster)
    {
        GameLocationCharacter.GetFromActor(caster)?.SetSpecialFeatureUses(FeatureSorcererArcaneApotheosis.Name, 1);
    }

    private sealed class ModifyMagicEffectAttackModifierInnateSorcery : IModifyMagicEffectAttackModifier
    {
        private readonly TrendInfo _trendInfo =
            new(1, FeatureSourceType.CharacterFeature, "PowerSorcererInnateSorcery", null);

        public void ModifyMagicEffectAttackModifier(
            RulesetCharacter attacker,
            RulesetActor defender,
            RulesetAttackMode attackMode,
            RulesetEffect rulesetEffect,
            ActionModifier actionModifier)
        {
            if (IsInnateSorceryValid(rulesetEffect))
            {
                actionModifier.AttackAdvantageTrends.Add(_trendInfo);
            }
        }
    }
}
