using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Validators;
using TA;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Subclasses.Builders.MetamagicBuilders;

namespace SolastaUnfinishedBusiness.Models;

internal static class MetamagicContext
{
    internal const string FeatMetamagicAdeptPointPoolTag = "PointPoolFeatMetamagicAdept";

    internal static HashSet<MetamagicOptionDefinition> Metamagic { get; private set; } = [];

    private const string MetamagicSeekingSpell = "MetamagicSeekingSpell";
    private const string MetamagicCarefulSpell = "MetamagicCarefullSpell";
    private const string MetamagicExtendedSpell = "MetamagicExtendedSpell";
    private const string MetamagicHeightenedSpell = "MetamagicHeightenedSpell";
    private const string MetamagicQuickenedSpell = "MetamagicQuickenedSpell";
    private const string MetamagicTwinnedSpell = "MetamagicTwinnedSpell";
    private const string MetamagicSeekingSpellDescription = "Feature/&MetamagicSeekingSpellDescription";
    private const string MetamagicSeekingSpell2024Description = "Feature/&MetamagicSeekingSpell2024Description";
    private const string MetamagicOptionExtendedSpellTitle = "Rules/&MetamagicOptionExtendedSpellTitle";
    private const string MetamagicCarefulSpell2024Description =
        "Rules/&MetamagicOptionCarefulSpell2024Description";
    private const string MetamagicExtendedSpell2024Description =
        "Rules/&MetamagicOptionExtendedSpell2024Description";
    private const string MetamagicQuickenedSpell2024Description =
        "Rules/&MetamagicOptionQuickenedSpell2024Description";
    private const string MetamagicTwinnedSpell2024Description =
        "Rules/&MetamagicOptionTwinnedSpell2024Description";
    private const string LeveledSpellCastThisTurn = "Metamagic2024LeveledSpellCastThisTurn";
    private const string QuickenedLeveledSpellCastThisTurn = "Metamagic2024QuickenedLeveledSpellCastThisTurn";
    private const string ConditionCarefulSpell2024 = "ConditionMetamagicCarefulSpell2024Protected";
    private const string ConditionExtendedSpell2024 = "ConditionMetamagicExtendedSpell2024Concentration";
    internal const string FailureFlagTwinnedSpell2024InvalidTargetAdvancement =
        "Failure/&FailureFlagTwinnedSpell2024InvalidTargetAdvancement";

    private static readonly Dictionary<string, MetamagicCostState> LegacyCostStates = [];
    private static readonly Dictionary<string, string> LegacyDescriptionKeys = [];

    private static bool _rules2024SubFeaturesInstalled;
    private static ConditionDefinition _conditionCarefulSpell2024;
    private static ConditionDefinition _conditionExtendedSpell2024;

    internal static void LateLoad()
    {
        var metamagicOptions = new[]
        {
            BuildMetamagicAltruisticSpell(),
            BuildMetamagicFocusedSpell(),
            BuildMetamagicPowerfulSpell(),
            BuildMetamagicSeekingSpell(),
            BuildMetamagicTransmutedSpell(),
            BuildMetamagicWidenedSpell()
        };

        foreach (var metamagicOption in metamagicOptions)
        {
            LoadMetamagic(metamagicOption);
        }

        // sorting
        Metamagic = Metamagic.OrderBy(x => x.FormatTitle()).ToHashSet();

        // settings paring
        foreach (var name in Main.Settings.MetamagicEnabled
                     .Where(name => Metamagic.All(x => x.Name != name))
                     .ToArray())
        {
            Main.Settings.MetamagicEnabled.Remove(name);
        }
    }

    internal static void SwitchSorcererMetamagicRules2024()
    {
        EnsureRules2024SubFeatures();

        var enabled = Main.Settings.EnableSorcererMetamagic2024;

        if (TryGetMetamagic(MetamagicHeightenedSpell, out var heightenedSpell))
        {
            SetOrRestoreFixedCost(heightenedSpell, 2, enabled);
        }

        if (TryGetMetamagic(MetamagicTwinnedSpell, out var twinnedSpell))
        {
            SetOrRestoreFixedCost(twinnedSpell, 1, enabled);
        }

        if (TryGetMetamagic(MetamagicSeekingSpell, out var seekingSpell))
        {
            SetOrRestoreFixedCost(seekingSpell, GetSeekingSpellCost(), enabled);
            seekingSpell.GuiPresentation.description = enabled
                ? MetamagicSeekingSpell2024Description
                : MetamagicSeekingSpellDescription;
        }

        SetOrRestoreDescription(MetamagicCarefulSpell, MetamagicCarefulSpell2024Description, enabled);
        SetOrRestoreDescription(MetamagicExtendedSpell, MetamagicExtendedSpell2024Description, enabled);
        SetOrRestoreDescription(MetamagicQuickenedSpell, MetamagicQuickenedSpell2024Description, enabled);
        SetOrRestoreDescription(MetamagicTwinnedSpell, MetamagicTwinnedSpell2024Description, enabled);
    }

    internal static int GetSeekingSpellCost()
    {
        return Main.Settings.EnableSorcererMetamagic2024 ? 1 : 2;
    }

    internal static string GetSeekingSpellReactionDescription(string defenderName)
    {
        var key = Main.Settings.EnableSorcererMetamagic2024
            ? "CustomReactionMetamagicSeekingSpell2024Description"
            : "CustomReactionMetamagicSeekingSpellDescription";

        return key.Formatted(Category.Reaction, defenderName);
    }

    internal static bool HasLeveledSpellCastThisTurn(GameLocationCharacter character)
    {
        return Main.Settings.EnableSorcererMetamagic2024 &&
               character.UsedSpecialFeatures.ContainsKey(LeveledSpellCastThisTurn);
    }

    internal static bool CanConfirmQuickenedSpell2024(
        GameLocationCharacter character,
        RulesetEffectSpell rulesetEffectSpell)
    {
        return !Main.Settings.EnableSorcererMetamagic2024 ||
               RulesetEffectSpellWithOrigin.GetOriginSpell(rulesetEffectSpell).SpellLevel == 0 ||
               !HasLeveledSpellCastThisTurn(character);
    }

    internal static void RestrictToCantripsAfterQuickenedSpell2024(
        GameLocationCharacter character,
        ref bool cantripOnly)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            !character.UsedSpecialFeatures.ContainsKey(QuickenedLeveledSpellCastThisTurn))
        {
            return;
        }

        cantripOnly = true;
    }

    internal static void MarkLeveledSpellCast2024(CharacterActionMagicEffect action)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            action.ActionParams?.RulesetEffect is not RulesetEffectSpell rulesetEffectSpell ||
            RulesetEffectSpellWithOrigin.GetOriginSpell(rulesetEffectSpell).SpellLevel == 0)
        {
            return;
        }

        var actingCharacter = action.ActingCharacter;

        actingCharacter.UsedSpecialFeatures[LeveledSpellCastThisTurn] = 1;

        if (rulesetEffectSpell.MetamagicOption == MetamagicOptionDefinitions.MetamagicQuickenedSpell)
        {
            actingCharacter.UsedSpecialFeatures[QuickenedLeveledSpellCastThisTurn] = 1;
        }
    }

    internal static List<EffectForm> FilterCarefulSpell2024EffectForms(
        List<EffectForm> effectForms,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            _conditionCarefulSpell2024 == null ||
            formsParams.activeEffect is not RulesetEffectSpell rulesetEffectSpell ||
            rulesetEffectSpell.MetamagicOption?.Name != MetamagicCarefulSpell ||
            !formsParams.rolledSaveThrow ||
            formsParams.saveOutcome is not (RollOutcome.Success or RollOutcome.CriticalSuccess) ||
            !formsParams.targetCharacter.HasConditionOfCategoryAndType(
                AttributeDefinitions.TagEffect, _conditionCarefulSpell2024.Name))
        {
            return effectForms;
        }

        var filteredForms = effectForms
            .Where(effectForm =>
                effectForm.FormType != EffectForm.EffectFormType.Damage ||
                effectForm.SavingThrowAffinity != EffectSavingThrowType.HalfDamage)
            .ToList();

        return filteredForms.Count == effectForms.Count ? effectForms : filteredForms;
    }

    internal static bool TryHandleTwinnedSpell2024Availability(
        RulesetEffectSpell rulesetEffectSpell,
        MetamagicOptionDefinition metamagicOption,
        int remainingSorceryPoints,
        ref bool result,
        ref string failure)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            metamagicOption.Name != MetamagicTwinnedSpell ||
            remainingSorceryPoints < 1)
        {
            return false;
        }

        result = CanApplyTwinnedSpell2024(rulesetEffectSpell, false);
        failure = result ? string.Empty : FailureFlagTwinnedSpell2024InvalidTargetAdvancement;

        return true;
    }

    private static bool CanApplyTwinnedSpell2024(RulesetEffectSpell rulesetEffectSpell, bool requireSelectedMetamagic)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            requireSelectedMetamagic &&
            rulesetEffectSpell.MetamagicOption != MetamagicOptionDefinitions.MetamagicTwinnedSpell)
        {
            return false;
        }

        return ComputeTwinnedSpell2024AdditionalTargets(rulesetEffectSpell) > 0;
    }

    private static int ComputeTwinnedSpell2024AdditionalTargets(RulesetEffectSpell rulesetEffectSpell)
    {
        var effectDescription = rulesetEffectSpell.EffectDescription;

        return effectDescription is
               {
                   HasAdditionalSlotAdvancement: true,
                   TargetType: TargetType.Individuals or TargetType.IndividualsUnique
               }
            ? effectDescription.EffectAdvancement.ComputeAdditionalTargetsBySlotDelta(1)
            : 0;
    }

    private static void LoadMetamagic([NotNull] MetamagicOptionDefinition metamagicDefinition)
    {
        Metamagic.Add(metamagicDefinition);
        UpdateMetamagicVisibility(metamagicDefinition);
    }

    private static void UpdateMetamagicVisibility([NotNull] BaseDefinition metamagicDefinition)
    {
        metamagicDefinition.GuiPresentation.hidden =
            !Main.Settings.MetamagicEnabled.Contains(metamagicDefinition.Name);
    }

    internal static void SwitchMetamagic(MetamagicOptionDefinition metamagicDefinition, bool active)
    {
        if (!Metamagic.Contains(metamagicDefinition))
        {
            return;
        }

        var name = metamagicDefinition.Name;

        if (active)
        {
            Main.Settings.MetamagicEnabled.TryAdd(name);
        }
        else
        {
            Main.Settings.MetamagicEnabled.Remove(name);
        }

        UpdateMetamagicVisibility(metamagicDefinition);
    }

    internal static int CompareMetamagic(MetamagicOptionDefinition a, MetamagicOptionDefinition b)
    {
        var compare = Math.Max(a.SorceryPointsCost, 1) - Math.Max(b.SorceryPointsCost, 1);

        return compare == 0
            ? string.Compare(a.FormatTitle(), b.FormatTitle(), StringComparison.CurrentCultureIgnoreCase)
            : compare;
    }

    internal static bool IsVisibleMetamagicOption(MetamagicOptionDefinition option)
    {
        return option is { GuiPresentation.Hidden: false };
    }

    internal static List<MetamagicOptionDefinition> GetVisibleMetamagicOptions()
    {
        var metamagicDatabase = DatabaseRepository.GetDatabase<MetamagicOptionDefinition>();

        if (metamagicDatabase == null)
        {
            return [];
        }

        return metamagicDatabase
            .GetAllElements()
            .Where(IsVisibleMetamagicOption)
            .OrderBy(x => x, Comparer<MetamagicOptionDefinition>.Create(CompareMetamagic))
            .ToList();
    }

    internal static List<MetamagicOptionDefinition> GetRestrictedVisibleMetamagicOptions(
        IReadOnlyCollection<string> restrictedChoices)
    {
        var metamagicOptions = GetVisibleMetamagicOptions();

        if (restrictedChoices is not { Count: > 0 })
        {
            return metamagicOptions;
        }

        var restrictedChoiceNames = restrictedChoices.ToHashSet(StringComparer.Ordinal);

        return metamagicOptions
            .Where(option => restrictedChoiceNames.Contains(option.Name))
            .ToList();
    }

    private static void EnsureRules2024SubFeatures()
    {
        if (_rules2024SubFeaturesInstalled)
        {
            return;
        }

        _rules2024SubFeaturesInstalled = true;
        _conditionCarefulSpell2024 = BuildCarefulSpell2024Condition();
        _conditionExtendedSpell2024 = BuildExtendedSpell2024Condition();

        if (TryGetMetamagic(MetamagicCarefulSpell, out var carefulSpell))
        {
            carefulSpell.AddCustomSubFeatures(new CarefulSpell2024Behavior(_conditionCarefulSpell2024));
        }

        if (TryGetMetamagic(MetamagicExtendedSpell, out var extendedSpell))
        {
            extendedSpell.AddCustomSubFeatures(new ExtendedSpell2024Behavior(_conditionExtendedSpell2024));
        }

        MetamagicOptionDefinitions.MetamagicTwinnedSpell.AddCustomSubFeatures(
            new ValidateMetamagicApplication(ValidateTwinnedSpell2024));
    }

    private static ConditionDefinition BuildCarefulSpell2024Condition()
    {
        return ConditionDefinitionBuilder
            .Create(ConditionCarefulSpell2024)
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddToDB();
    }

    private static ConditionDefinition BuildExtendedSpell2024Condition()
    {
        var magicAffinity = FeatureDefinitionMagicAffinityBuilder
            .Create("MagicAffinityMetamagicExtendedSpell2024Concentration")
            .SetGuiPresentation(GuiPresentationBuilder.Build(
                MetamagicOptionExtendedSpellTitle,
                MetamagicExtendedSpell2024Description,
                hidden: true))
            .SetConcentrationModifiers(ConcentrationAffinity.Advantage)
            .AddToDB();

        return ConditionDefinitionBuilder
            .Create(ConditionExtendedSpell2024)
            .SetGuiPresentation(GuiPresentationBuilder.Build(
                MetamagicOptionExtendedSpellTitle,
                MetamagicExtendedSpell2024Description,
                hidden: true))
            .SetPossessive()
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddFeatures(magicAffinity)
            .AddToDB();
    }

    private static void ValidateTwinnedSpell2024(
        RulesetCharacter caster,
        RulesetEffectSpell rulesetEffectSpell,
        MetamagicOptionDefinition metamagicOption,
        ref bool result,
        ref string failure)
    {
        _ = caster;
        _ = metamagicOption;

        if (!Main.Settings.EnableSorcererMetamagic2024)
        {
            return;
        }

        result = CanApplyTwinnedSpell2024(rulesetEffectSpell, false);
        failure = result ? string.Empty : FailureFlagTwinnedSpell2024InvalidTargetAdvancement;
    }

    private static bool TryGetMetamagic(string name, out MetamagicOptionDefinition metamagicOption)
    {
        return DatabaseRepository.GetDatabase<MetamagicOptionDefinition>()
            .TryGetElement(name, out metamagicOption);
    }

    private static void SetOrRestoreFixedCost(
        MetamagicOptionDefinition metamagicOption,
        int rule2024Cost,
        bool enabled)
    {
        if (!LegacyCostStates.ContainsKey(metamagicOption.Name))
        {
            LegacyCostStates[metamagicOption.Name] =
                new MetamagicCostState(metamagicOption.CostMethod, metamagicOption.SorceryPointsCost);
        }

        if (enabled)
        {
            metamagicOption.costMethod = MetamagicCostMethod.FixedValue;
            metamagicOption.sorceryPointsCost = rule2024Cost;
            return;
        }

        var legacyState = LegacyCostStates[metamagicOption.Name];

        metamagicOption.costMethod = legacyState.CostMethod;
        metamagicOption.sorceryPointsCost = legacyState.SorceryPointsCost;
    }

    private static void SetOrRestoreDescription(string metamagicName, string rule2024Description, bool enabled)
    {
        if (!TryGetMetamagic(metamagicName, out var metamagicOption))
        {
            return;
        }

        if (!LegacyDescriptionKeys.ContainsKey(metamagicOption.Name))
        {
            LegacyDescriptionKeys[metamagicOption.Name] = metamagicOption.GuiPresentation.description;
        }

        metamagicOption.GuiPresentation.description = enabled
            ? rule2024Description
            : LegacyDescriptionKeys[metamagicOption.Name];
    }

    private readonly struct MetamagicCostState(
        MetamagicCostMethod costMethod,
        int sorceryPointsCost)
    {
        internal readonly MetamagicCostMethod CostMethod = costMethod;
        internal readonly int SorceryPointsCost = sorceryPointsCost;
    }

    private sealed class CarefulSpell2024Behavior(ConditionDefinition condition)
        : IMagicEffectInitiatedByMe, IMagicEffectFinishedByMe
    {
        public IEnumerator OnMagicEffectInitiatedByMe(
            CharacterAction action,
            RulesetEffect activeEffect,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (!Main.Settings.EnableSorcererMetamagic2024 ||
                activeEffect.MetamagicOption?.Name != MetamagicCarefulSpell ||
                !activeEffect.EffectDescription.EffectForms.Any(IsHalfDamageEffectForm))
            {
                yield break;
            }

            var rulesetAttacker = attacker.RulesetCharacter;
            var charismaModifier = Math.Max(1,
                AttributeDefinitions.ComputeAbilityScoreModifier(
                    rulesetAttacker.TryGetAttributeValue(AttributeDefinitions.Charisma)));

            var protectedTargets = 0;

            foreach (var target in targets)
            {
                if (target == attacker ||
                    target.Side != attacker.Side ||
                    target.RulesetCharacter == null)
                {
                    continue;
                }

                target.RulesetCharacter.InflictCondition(
                    condition.Name,
                    DurationType.Round,
                    0,
                    TurnOccurenceType.EndOfTurn,
                    AttributeDefinitions.TagEffect,
                    rulesetAttacker.guid,
                    rulesetAttacker.CurrentFaction.Name,
                    1,
                    condition.Name,
                    0,
                    0,
                    0);

                protectedTargets++;

                if (protectedTargets >= charismaModifier)
                {
                    break;
                }
            }
        }

        public IEnumerator OnMagicEffectFinishedByMe(
            CharacterAction action,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (!Main.Settings.EnableSorcererMetamagic2024 ||
                action.ActionParams?.RulesetEffect.MetamagicOption?.Name != MetamagicCarefulSpell)
            {
                yield break;
            }

            foreach (var target in targets)
            {
                if (target.RulesetCharacter == null)
                {
                    continue;
                }

                while (target.RulesetCharacter.TryGetConditionOfCategoryAndType(
                           AttributeDefinitions.TagEffect, condition.Name, out var activeCondition))
                {
                    target.RulesetCharacter.RemoveCondition(activeCondition);
                }
            }
        }

        private static bool IsHalfDamageEffectForm(EffectForm effectForm)
        {
            return effectForm is
            {
                FormType: EffectForm.EffectFormType.Damage,
                SavingThrowAffinity: EffectSavingThrowType.HalfDamage
            };
        }
    }

    private sealed class ExtendedSpell2024Behavior(ConditionDefinition condition) : IMagicEffectInitiatedByMe
    {
        public IEnumerator OnMagicEffectInitiatedByMe(
            CharacterAction action,
            RulesetEffect activeEffect,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (!Main.Settings.EnableSorcererMetamagic2024 ||
                activeEffect is not RulesetEffectSpell rulesetEffectSpell ||
                rulesetEffectSpell.MetamagicOption?.Name != MetamagicExtendedSpell ||
                !rulesetEffectSpell.SpellDefinition.RequiresConcentration)
            {
                yield break;
            }

            var rulesetCharacter = attacker.RulesetCharacter;

            rulesetCharacter.InflictCondition(
                condition.Name,
                activeEffect.EffectDescription.DurationType,
                activeEffect.EffectDescription.DurationParameter,
                activeEffect.EffectDescription.EndOfEffect,
                AttributeDefinitions.TagEffect,
                rulesetCharacter.guid,
                rulesetCharacter.CurrentFaction.Name,
                1,
                condition.Name,
                0,
                0,
                0);
        }
    }
}
