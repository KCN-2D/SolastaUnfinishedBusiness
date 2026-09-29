using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Models;

public static partial class SmiteSpells2024Context
{
    private static readonly List<RevisedSmite> RevisedSmites = [];
    private static readonly Dictionary<SpellDefinition, (ActivationTime Casting, bool Concentration, float Speed)>
        SmiteSpellDefaults = [];
    private static readonly Dictionary<FeatureDefinitionAdditionalDamage, RestrictedContextRequiredProperty>
        SmiteDamageDefaults = [];
    private static readonly Dictionary<ConditionDefinition, (bool Added, bool Removed)> SmiteConditionDefaults = [];
    internal static ConditionDefinition BanishingSmiteCondition2024 { get; private set; }

    private static void LoadRevisedSmiteEffects()
    {
        MarkInstantaneousSmite(Tabletop2024Context.DivineSmiteSpell);

        foreach (var spell in SmiteSpells)
        {
            SmiteSpellDefaults.Add(spell, (spell.castingTime, spell.RequiresConcentration,
                spell.EffectDescription.SpeedParameter));
        }
        foreach (var damage in SmiteDamages)
        {
            SmiteDamageDefaults.Add(damage, damage.RequiredProperty);
        }
        foreach (var condition in SmiteConditions)
        {
            SmiteConditionDefaults.Add(condition, (condition.SilentWhenAdded, condition.SilentWhenRemoved));
        }

        var searing = SpellsContext.SearingSmite;
        var burning = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionOnFire, "ConditionSearingSmiteBurning2024")
            .SetGuiPresentation(searing.GuiPresentation)
            .SetFeatures()
            .ClearSpecialInterruptions()
            .SetSpecialDuration(DurationType.Minute, 1, TurnOccurenceType.StartOfTurn)
            .AddCustomSubFeatures(new TrackSmiteCondition(searing, AttributeDefinitions.Constitution))
            .AddToDB();

        var searingDamage = CloneDamage(searing, "AdditionalDamageSearingSmite");
        burning.GuiPresentation.Description = "Spell/&SearingSmite2024Description";
        searingDamage.hasSavingThrow = false;
        searingDamage.ConditionOperations.SetRange(ConditionOperation(burning));
        AddRevisedSmite(searing, searingDamage);

        var blinding = SpellsContext.BlindingSmite;
        var blinded = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionBlinded, "ConditionBlindingSmite2024")
            .SetParentCondition(ConditionDefinitions.ConditionBlinded)
            .SetGuiPresentation(blinding.GuiPresentation)
            .SetFeatures()
            .SetSpecialDuration(DurationType.Minute, 1, TurnOccurenceType.EndOfTurn)
            .AddCustomSubFeatures(new TrackSmiteCondition(blinding, AttributeDefinitions.Constitution))
            .AddToDB();

        var blindingDamage = CloneDamage(blinding, "AdditionalDamageBlindingSmite");
        blinded.GuiPresentation.Description = "Spell/&BlindingSmite2024Description";
        blindingDamage.hasSavingThrow = false;
        blindingDamage.ConditionOperations.SetRange(ConditionOperation(blinded));
        SetSlotAdvancement(blindingDamage, 3, 3);
        AddRevisedSmite(blinding, blindingDamage);

        var staggering = SpellsContext.StaggeringSmite;
        MarkInstantaneousSmite(staggering);
        var stunned = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionStunned, "ConditionStaggeringSmite2024")
            .SetParentCondition(ConditionDefinitions.ConditionStunned)
            .SetGuiPresentation(staggering.GuiPresentation)
            .SetFeatures()
            .SetSpecialDuration(DurationType.Round, 1, TurnOccurenceType.EndOfSourceTurn)
            .AddCustomSubFeatures(new TrackSmiteCondition(staggering))
            .AddToDB();

        stunned.forceTurnOccurence = true;
        var staggeringDamage = CloneDamage(staggering, "AdditionalDamageStaggeringSmite");
        stunned.GuiPresentation.Description = "Spell/&StaggeringSmite2024Description";
        staggeringDamage.hasSavingThrow = false;
        staggeringDamage.ConditionOperations.Clear();
        SetSlotAdvancement(staggeringDamage, 4, 4);
        staggeringDamage.AddCustomSubFeatures(new AdditionalEffectFormOnDamageHandler((attacker, _, _) =>
        {
            var form = EffectFormBuilder.ConditionForm(stunned);
            SetSavingThrow(form, attacker.RulesetCharacter, staggering, AttributeDefinitions.Wisdom);
            return [form];
        }));
        AddRevisedSmite(staggering, staggeringDamage);

        var thunderous = SpellsContext.ThunderousSmite;
        MarkInstantaneousSmite(thunderous);
        var thunderousDamage = FeatureDefinitionAdditionalDamageBuilder
            .Create("AdditionalDamageThunderousSmite2024")
            .SetGuiPresentation(thunderous.GuiPresentation)
            .SetAttackModeOnly()
            .SetNotificationTag("ThunderousSmite")
            .SetDamageDice(DieType.D6, 2)
            .SetSpecificDamageType(DamageTypeThunder)
            .SetAdvancement(AdditionalDamageAdvancement.SlotLevel, 2)
            .AddToDB();
        thunderousDamage.AddCustomSubFeatures(new AdditionalEffectFormOnDamageHandler((attacker, _, _) =>
        {
            var push = EffectFormBuilder.Create().SetMotionForm(MotionForm.MotionType.PushFromOrigin, 2).Build();
            var prone = EffectFormBuilder.Create().SetMotionForm(MotionForm.MotionType.FallProne).Build();
            SetSavingThrow(push, attacker.RulesetCharacter, thunderous, AttributeDefinitions.Strength);
            SetSavingThrow(prone, attacker.RulesetCharacter, thunderous, AttributeDefinitions.Strength);
            return [push, prone];
        }));
        AddRevisedSmite(thunderous, thunderousDamage);

        var branding = SpellDefinitions.BrandingSmite;
        var branded = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionBranded, "ConditionBrandingSmite2024")
            .SetGuiPresentation(branding.GuiPresentation)
            .SetFeatures(
                FeatureDefinitionCombatAffinityBuilder.Create("CombatAffinityBrandingSmite2024")
                    .SetGuiPresentationNoContent(true)
                    .SetAttackOnMeAdvantage(AdvantageType.Advantage)
                    .AddToDB(),
                FeatureDefinitionConditionAffinityBuilder.Create("ConditionAffinityBrandingSmite2024")
                    .SetGuiPresentationNoContent(true)
                    .SetConditionAffinityType(ConditionAffinityType.Immunity)
                    .SetConditionType(ConditionDefinitions.ConditionInvisibleBase)
                    .AddToDB())
            .AddCustomSubFeatures(new TrackSmiteCondition(branding))
            .AddToDB();
        branded.GuiPresentation.Description = "Spell/&BrandingSmite2024Description";
        var brandingDamage = CloneDamage(branding, "AdditionalDamageBrandingSmite");
        brandingDamage.attackModeOnly = true;
        brandingDamage.ConditionOperations.SetRange(ConditionOperation(branded));
        brandingDamage.LightSourceForm.brightRange = 1;
        brandingDamage.LightSourceForm.dimAdditionalRange = 0;
        SetSlotAdvancement(brandingDamage, 2, 2);
        AddRevisedSmite(branding, brandingDamage);

        var banishing = SpellsContext.BanishingSmite;
        BanishingSmiteCondition2024 = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionBanished, "ConditionBanishingSmite2024")
            .SetGuiPresentation(banishing.GuiPresentation)
            .SetSpecialDuration(DurationType.Minute, 1)
            .AddCustomSubFeatures(new TrackSmiteCondition(banishing))
            .AddToDB();
        BanishingSmiteCondition2024.permanentlyRemovedIfExtraPlanar = false;
        BanishingSmiteCondition2024.GuiPresentation.Description = "Spell/&BanishingSmite2024Description";
        AddRevisedSmite(banishing, null);
    }

    private static FeatureDefinitionAdditionalDamage CloneDamage(SpellDefinition spell, string name)
    {
        var original = GetDefinition<FeatureDefinitionAdditionalDamage>(name);
        return FeatureDefinitionAdditionalDamageBuilder.Create(original, name + "2024")
            .SetGuiPresentation(spell.GuiPresentation)
            .SetDamageDice(original.DamageDieType, original.DamageDiceNumber)
            .SetDamageValueDetermination(original.DamageValueDetermination)
            .SetFlatDamageBonus(original.FlatBonus)
            .SetNotificationTag(original.NotificationTag)
            .SetAdvancement(original.DamageAdvancement, original.DiceByRankTable)
            .SetRequiredProperty(RestrictedContextRequiredProperty.None)
            .AddToDB();
    }

    private static void MarkInstantaneousSmite(SpellDefinition spell)
    {
        // The carrier survives until this hit resolves; it does not make the spell last for one minute.
        spell.AddCustomSubFeatures(new CustomSpellDuration(DurationType.Instantaneous, 0,
            () => Main.Settings.EnableSmiteSpells2024));
    }

    private static ConditionOperationDescription ConditionOperation(ConditionDefinition condition)
    {
        return new ConditionOperationDescription
        {
            operation = ConditionOperationDescription.ConditionOperation.Add,
            conditionDefinition = condition
        };
    }

    private static void SetSlotAdvancement(FeatureDefinitionAdditionalDamage damage, int dice, int level)
    {
        damage.damageAdvancement = AdditionalDamageAdvancement.SlotLevel;
        damage.DiceByRankTable.SetRange(DiceByRankBuilder.BuildDiceByRankTable(dice, begin: level));
    }

    private static void AddRevisedSmite(SpellDefinition spell, FeatureDefinitionAdditionalDamage damage)
    {
        var carrier = spell.EffectDescription.EffectForms
            .First(form => form.FormType == EffectForm.EffectFormType.Condition).ConditionForm.ConditionDefinition;
        RevisedSmites.Add(new RevisedSmite(spell, carrier, damage));
    }

    private static void SwitchRevisedSmiteEffects()
    {
        var enabled = Main.Settings.EnableSmiteSpells2024;
        foreach (var entry in RevisedSmites)
        {
            entry.Carrier.Features.SetRange(enabled && entry.Damage != null ? [entry.Damage] : entry.Features);
            entry.Spell.EffectDescription.EffectAdvancement.Copy(entry.Effect.EffectAdvancement);
            entry.Spell.GuiPresentation.Description = enabled
                ? $"Spell/&{entry.Spell.Name}2024Description" : entry.Description;
            entry.Carrier.GuiPresentation.Description = enabled
                ? entry.Spell.GuiPresentation.Description : entry.CarrierDescription;
            if (entry.Damage != null)
            {
                entry.Damage.GuiPresentation.Description = entry.Spell.GuiPresentation.Description;
            }
            if (enabled && entry.Spell != SpellsContext.BanishingSmite)
            {
                entry.Spell.EffectDescription.EffectAdvancement.effectIncrementMethod =
                    EffectIncrementMethod.PerAdditionalSlotLevel;
                entry.Spell.EffectDescription.EffectAdvancement.additionalDicePerIncrement = 1;
            }
            PowerBundle.ClearSpellEffectCacheForDefinition(entry.Spell);
        }

        // Banishment remains concentration based; the transferred target condition is tracked by this spell.
        SpellsContext.BanishingSmite.requiresConcentration = true;
        SpellDefinitions.BrandingSmite.requiresConcentration = true;
        SpellDefinitions.BrandingSmite.schoolOfMagic = enabled ? SchoolTransmutation : SchoolEvocation;
        SpellsContext.StaggeringSmite.schoolOfMagic = enabled ? SchoolOfMagicDefinitions.SchoolEnchantment.Name : SchoolEvocation;
    }

    internal static RulesetEffectSpell GetSmiteEffect(RulesetCharacter caster, SpellDefinition spell)
    {
        var carrier = spell.EffectDescription.EffectForms
            .First(form => form.FormType == EffectForm.EffectFormType.Condition).ConditionForm.ConditionDefinition;
        if (caster.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect, carrier.Name, out var condition) &&
            caster.FindEffectTrackingCondition(condition) is RulesetEffectSpell effect)
        {
            return effect;
        }

        return caster.SpellsCastByMe.LastOrDefault(effect => effect.SpellDefinition == spell);
    }

    private static void SetSavingThrow(EffectForm form, RulesetCharacter caster, SpellDefinition spell, string ability)
    {
        var effect = GetSmiteEffect(caster, spell);
        form.SavingThrowAffinity = EffectSavingThrowType.Negates;
        form.OverrideSavingThrowInfo = new OverrideSavingThrowInfo(ability,
            effect?.SaveDC ?? 10, spell.Name, FeatureSourceType.Spell);
    }

    internal static IEnumerator SaveAgainstSmite(
        GameLocationBattleManager battleManager,
        GameLocationCharacter caster,
        GameLocationCharacter target,
        SpellDefinition spell,
        string ability,
        ConditionDefinition appliedCondition,
        Action<bool> completed)
    {
        var effect = GetSmiteEffect(caster.RulesetCharacter, spell);
        if (effect == null)
        {
            completed(true);
            yield break;
        }

        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();
        var effectDescription = EffectDescriptionBuilder.Create(effect.EffectDescription)
            .SetEffectForms(EffectFormBuilder.ConditionForm(appliedCondition))
            .Build();

        bool RollSavingThrow(ActionModifier actionModifier, out RollOutcome outcome, out int delta)
        {
            return implementationService.TryRollSavingThrow(
                caster.RulesetCharacter, caster.Side, target.RulesetCharacter, actionModifier,
                false, true, ability, effect.SaveDC, false, false, false,
                FeatureSourceType.Spell, effectDescription.EffectForms, null, null,
                spell.Name, spell, spell.SchoolOfMagic, effect.MetamagicOption,
                out outcome, out delta);
        }

        var modifier = new ActionModifier();
        using var savingRollContext = new D20RollContext(target.RulesetCharacter, RollContext.SavingThrow,
            ability, advantageTrends: modifier.SavingThrowAdvantageTrends);

        yield return savingRollContext.Prompt(caster);

        bool rolledSavingThrow;
        RollOutcome outcome;
        int delta;

        using (savingRollContext.Activate())
        {
            rolledSavingThrow = RollSavingThrow(modifier, out outcome, out delta);
        }

        if (!rolledSavingThrow)
        {
            completed(true);
            yield break;
        }

        var savingThrowData = new SavingThrowData
        {
            SaveActionModifier = modifier,
            SaveOutcome = outcome,
            SaveOutcomeDelta = delta,
            SaveDC = RulesetActorExtensions.SaveDC,
            CurrentRoll = RulesetActorExtensions.SaveRoll,
            MinimumResult = RulesetActorExtensions.SaveMinimumResult,
            SaveBonusAndRollModifier = RulesetActorExtensions.SaveBonusAndRollModifier,
            SavingThrowAbility = RulesetActorExtensions.SavingThrowAbility,
            SourceDefinition = spell,
            EffectDescription = effectDescription,
            Title = spell.FormatTitle(),
            Action = null,
            RerollSavingThrow = RollSavingThrow
        };
        yield return TryAlterOutcomeSavingThrow.Handler(
            battleManager, caster, target, savingThrowData,
            target.RulesetCharacter.HasConditionOfTypeOrSubType(ConditionBorrowedLuck),
            effectDescription);

        completed(!savingThrowData.IsFailedSavingThrowOutcome() ||
                  !caster.RulesetCharacter.SpellsCastByMe.Contains(effect));
    }

    internal static bool IsTrackedSmiteCondition(RulesetCondition condition)
    {
        if (condition.ConditionDefinition.GetFirstSubFeatureOfType<TrackSmiteCondition>() == null)
        {
            return false;
        }

        return EffectHelpers.GetCharacterByGuid(condition.SourceGuid)?.FindEffectTrackingCondition(condition) != null;
    }

    internal static void FinalizeSmiteCondition(EffectForm form,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams)
    {
        if (form.ConditionForm is not { Operation: ConditionForm.ConditionOperation.Add } conditionForm ||
            formsParams.targetCharacter is not { } target || formsParams.sourceCharacter is not { } source)
        {
            return;
        }

        var behavior = conditionForm.ConditionDefinition?.GetFirstSubFeatureOfType<TrackSmiteCondition>();
        if (behavior?.RepeatSaveAbility == null)
        {
            return;
        }

        foreach (var condition in target.ConditionsByCategory.Values.SelectMany(conditions => conditions)
                     .Where(condition => condition.ConditionDefinition == conditionForm.ConditionDefinition &&
                                         condition.SourceGuid == source.Guid && condition.HasSaveOverride))
        {
            // ApplyConditionForm overwrites CanSaveToCancel after AddCondition callbacks.
            // Finish this metadata here, after the initial application has resolved without a save.
            condition.canSaveToCancel = true;
            condition.endOccurence = condition.ConditionDefinition.TurnOccurence;
        }
    }

    private sealed class TrackSmiteCondition(SpellDefinition spell, string repeatSaveAbility = null)
        : IOnConditionAddedOrRemoved
    {
        internal string RepeatSaveAbility { get; } = repeatSaveAbility;

        public void OnConditionAdded(RulesetCharacter target, RulesetCondition condition)
        {
            if (!target.ConditionsByCategory.Values.Any(conditions => conditions.Contains(condition)))
            {
                return;
            }

            var caster = EffectHelpers.GetCharacterByGuid(condition.SourceGuid);
            var effect = caster == null ? null : GetSmiteEffect(caster, spell);
            if (effect == null)
            {
                return;
            }

            if (spell == SpellDefinitions.BrandingSmite)
            {
                foreach (var invisible in target.ConditionsByCategory.Values.SelectMany(conditions => conditions)
                             .Where(active => active.ConditionDefinition.IsSubtypeOf(
                                 ConditionDefinitions.ConditionInvisibleBase.Name)).ToArray())
                {
                    target.RemoveCondition(invisible);
                }
            }

            condition.effectLevel = effect.EffectLevel;
            condition.effectDefinitionName = spell.Name;
            effect.TrackCondition(caster, caster.Guid, target, target.Guid, condition, AttributeDefinitions.TagEffect);
            if (RepeatSaveAbility == null)
            {
                return;
            }

            // Native condition processing applies recurrent damage before this cancellation save.
            // Configure it after application so Blinding/Searing Smite never roll an initial save.
            condition.canSaveToCancel = true;
            condition.hasSaveOverride = true;
            condition.saveOverrideAbilityScoreName = RepeatSaveAbility;
            condition.saveOverrideDC = effect.SaveDC;
            condition.saveOverrideSourceName = spell.Name;
            condition.saveOverrideSourceType = FeatureSourceType.Spell;
        }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition condition) { }
    }

    private sealed class RevisedSmite(
        SpellDefinition spell, ConditionDefinition carrier, FeatureDefinitionAdditionalDamage damage)
    {
        internal SpellDefinition Spell { get; } = spell;
        internal ConditionDefinition Carrier { get; } = carrier;
        internal FeatureDefinitionAdditionalDamage Damage { get; } = damage;
        internal FeatureDefinition[] Features { get; } = carrier.Features.ToArray();
        internal EffectDescription Effect { get; } = EffectDescriptionBuilder.Create(spell.EffectDescription).Build();
        internal string Description { get; } = spell.GuiPresentation.Description;
        internal string CarrierDescription { get; } = carrier.GuiPresentation.Description;
    }
}
