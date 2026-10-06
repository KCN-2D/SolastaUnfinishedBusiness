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
        var searingSaveAbility = AttributeDefinitions.Constitution;
        var burning = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionOnFire, "ConditionSearingSmiteBurning2024")
            .SetGuiPresentation(searing.GuiPresentation)
            .SetFeatures()
            .ClearSpecialInterruptions()
            .SetSpecialDuration(DurationType.Minute, 1, TurnOccurenceType.StartOfTurn)
            .AddCustomSubFeatures(new TrackSmiteCondition(searing, searingSaveAbility))
            .AddToDB();

        var searingDamage = CloneDamage(searing, "AdditionalDamageSearingSmite");
        burning.GuiPresentation.Description = "Spell/&SearingSmite2024Description";
        searingDamage.hasSavingThrow = false;
        searingDamage.ConditionOperations.SetRange(ConditionOperation(burning));
        AddRevisedSmite(searing, searingDamage, searingSaveAbility);

        var blinding = SpellsContext.BlindingSmite;
        var blindingSaveAbility = AttributeDefinitions.Constitution;
        var blinded = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionBlinded, "ConditionBlindingSmite2024")
            .SetParentCondition(ConditionDefinitions.ConditionBlinded)
            .SetGuiPresentation(blinding.GuiPresentation)
            .SetFeatures()
            .SetSpecialDuration(DurationType.Minute, 1, TurnOccurenceType.EndOfTurn)
            .AddCustomSubFeatures(new TrackSmiteCondition(blinding, blindingSaveAbility))
            .AddToDB();

        var blindingDamage = CloneDamage(blinding, "AdditionalDamageBlindingSmite");
        blinded.GuiPresentation.Description = "Spell/&BlindingSmite2024Description";
        blindingDamage.hasSavingThrow = false;
        blindingDamage.ConditionOperations.SetRange(ConditionOperation(blinded));
        SetSlotAdvancement(blindingDamage, 3, 3);
        AddRevisedSmite(blinding, blindingDamage, blindingSaveAbility);

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
            SetSavingThrow(form, attacker.RulesetCharacter, staggering);
            return [form];
        }));
        AddRevisedSmite(staggering, staggeringDamage, AttributeDefinitions.Wisdom);

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
            SetSavingThrow(push, attacker.RulesetCharacter, thunderous);
            SetSavingThrow(prone, attacker.RulesetCharacter, thunderous);
            return [push, prone];
        }));
        AddRevisedSmite(thunderous, thunderousDamage, AttributeDefinitions.Strength);

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
        AddRevisedSmite(banishing, null, AttributeDefinitions.Charisma);
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

    private static void AddRevisedSmite(
        SpellDefinition spell, FeatureDefinitionAdditionalDamage damage, string savingThrowAbility = null)
    {
        var carrier = spell.EffectDescription.EffectForms
            .First(form => form.FormType == EffectForm.EffectFormType.Condition).ConditionForm.ConditionDefinition;
        RevisedSmites.Add(new RevisedSmite(spell, carrier, damage, savingThrowAbility));
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

    internal static bool RequiresSavingThrow(RulesetEffectSpell effect)
    {
        if (effect == null || !AlLSmiteSpells.Contains(effect.SpellDefinition))
        {
            return false;
        }

        var revised = RevisedSmites.FirstOrDefault(entry => entry.Spell == effect.SpellDefinition);
        if (Main.Settings.EnableSmiteSpells2024 && revised != null)
        {
            return revised.SavingThrowAbility != null;
        }

        return effect.EffectDescription.EffectForms
            .Where(form => form.FormType == EffectForm.EffectFormType.Condition)
            .SelectMany(form => form.ConditionForm.ConditionDefinition.Features)
            .Any(feature => feature is FeatureDefinitionAdditionalDamage { HasSavingThrow: true } ||
                            feature is FeatureDefinitionPower { EffectDescription.HasSavingThrow: true });
    }

    // Smite saves are deferred to an additional-damage form, a granted power, or a tracked condition.
    // Resolve their native definition ownership rather than relying on the weapon action's repertoire.
    internal static RulesetEffectSpell GetSavingThrowSpell(
        RulesetCharacter caster, BaseDefinition sourceDefinition, IEnumerable<EffectForm> forms,
        RulesetEffect activeEffect = null)
    {
        if (caster == null)
        {
            return null;
        }

        var spellEffect = activeEffect switch
        {
            RulesetEffectSpell spell => spell,
            RulesetEffectPower power => Tabletop2024Context.GetSpellDerivedPowerSpell(power),
            _ => null
        };
        var savingThrow = forms?.Select(form => form.OverrideSavingThrowInfo).FirstOrDefault(info => info != null);
        if (savingThrow == null && IsOwnedSmiteEffect(caster, spellEffect))
        {
            return spellEffect;
        }

        // The native roll uses the first override only. Other forms and the original action's
        // definition must not lend their metamagic to an unrelated additional feature's save.
        var sourceName = savingThrow?.SourceDefinitionName ?? sourceDefinition?.Name;

        var sourceSpell = AlLSmiteSpells.FirstOrDefault(spell =>
            spell.Name == sourceName ||
            spell.EffectDescription.EffectForms
                .Where(form => form.FormType == EffectForm.EffectFormType.Condition)
                .SelectMany(form => form.ConditionForm.ConditionDefinition.Features)
                .Concat(RevisedSmites.FirstOrDefault(entry => entry.Spell == spell)?.Features ?? [])
                .Any(feature => feature.Name == sourceName));
        if (sourceSpell == null)
        {
            return null;
        }

        if (IsOwnedSmiteEffect(caster, spellEffect) && spellEffect.SpellDefinition == sourceSpell)
        {
            return spellEffect;
        }

        var carrier = sourceSpell.EffectDescription.EffectForms
            .First(form => form.FormType == EffectForm.EffectFormType.Condition).ConditionForm.ConditionDefinition;
        if (!caster.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect, carrier.Name, out var condition) ||
            condition.SourceGuid != caster.Guid)
        {
            return null;
        }

        spellEffect = Tabletop2024Context.GetSpellDerivedConditionSpell(condition);
        return IsOwnedSmiteEffect(caster, spellEffect) && spellEffect.SpellDefinition == sourceSpell
            ? spellEffect : null;
    }

    private static bool IsOwnedSmiteEffect(RulesetCharacter caster, RulesetEffectSpell effect)
    {
        return effect != null && effect.Caster == caster && caster.SpellsCastByMe.Contains(effect) &&
               AlLSmiteSpells.Contains(effect.SpellDefinition);
    }

    internal static RulesetEffectSpell GetConditionSavingThrowSpell(RulesetCondition condition)
    {
        var caster = EffectHelpers.GetCharacterByGuid(condition.SourceGuid);
        var effect = Tabletop2024Context.GetSpellDerivedConditionSpell(condition);
        return caster != null && IsOwnedSmiteEffect(caster, effect) ? effect : null;
    }

    internal static int GetConditionSavingThrowDc(RulesetCondition condition)
    {
        return GetConditionSavingThrowSpell(condition)?.SaveDC ?? condition.SaveOverrideDC;
    }

    private static string GetRevisedSavingThrowAbility(SpellDefinition spell)
    {
        return RevisedSmites.First(entry => entry.Spell == spell).SavingThrowAbility;
    }

    private static void SetSavingThrow(EffectForm form, RulesetCharacter caster, SpellDefinition spell)
    {
        var effect = GetSmiteEffect(caster, spell);
        form.SavingThrowAffinity = EffectSavingThrowType.Negates;
        form.OverrideSavingThrowInfo = new OverrideSavingThrowInfo(GetRevisedSavingThrowAbility(spell),
            effect?.SaveDC ?? 10, spell.Name, FeatureSourceType.Spell);
    }

    internal static IEnumerator SaveAgainstSmite(
        GameLocationBattleManager battleManager,
        GameLocationCharacter caster,
        GameLocationCharacter target,
        SpellDefinition spell,
        ConditionDefinition appliedCondition,
        Action<bool> completed)
    {
        var effect = GetSmiteEffect(caster.RulesetCharacter, spell);
        if (effect == null)
        {
            completed(true);
            yield break;
        }

        var ability = GetRevisedSavingThrowAbility(spell);
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
                spell.Name, spell, spell.SchoolOfMagic, MetamagicContext.PrepareSavingThrowMetamagic(effect, target.RulesetCharacter,
                    true, caster.Side, false, effectDescription.EffectForms),
                out outcome, out delta);
        }

        var modifier = new ActionModifier();
        using var heightenedSaveScope = MetamagicContext.DelayHeightenedConsumption(effect, target.RulesetCharacter);
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
            SourceEffect = effect,
            SavingThrowForms = effectDescription.EffectForms,
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

        var effect = Main.Settings.EnableSmiteSpells2024
            ? GetSavingThrowSpell(source, null, [form], formsParams.activeEffect) : null;
        if (effect != null)
        {
            foreach (var condition in target.AllConditions.Where(condition =>
                         condition.ConditionDefinition == conditionForm.ConditionDefinition &&
                         condition.SourceGuid == source.Guid))
            {
                var tracked = Tabletop2024Context.GetSpellDerivedConditionSpell(condition);
                if (tracked != null && tracked != effect)
                {
                    continue;
                }

                if (!effect.TrackedConditionGuids.Contains(condition.Guid))
                {
                    effect.TrackCondition(source, source.Guid, target, target.Guid, condition, AttributeDefinitions.TagEffect);
                }
                condition.saveOverrideDC = Tabletop2024Context.GetSpellBaseSaveDc(effect);
                Tabletop2024Context.BindSpellDerivedConditionOrigin(condition, effect);
            }
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
            Tabletop2024Context.BindSpellDerivedConditionOrigin(condition, effect);
            if (RepeatSaveAbility == null)
            {
                return;
            }

            // Native condition processing applies recurrent damage before this cancellation save.
            // Configure it after application so Blinding/Searing Smite never roll an initial save.
            condition.canSaveToCancel = true;
            condition.hasSaveOverride = true;
            condition.saveOverrideAbilityScoreName = RepeatSaveAbility;
            condition.saveOverrideDC = Tabletop2024Context.GetSpellBaseSaveDc(effect);
            condition.saveOverrideSourceName = spell.Name;
            condition.saveOverrideSourceType = FeatureSourceType.Spell;
        }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition condition) { }
    }

    private sealed class RevisedSmite(
        SpellDefinition spell, ConditionDefinition carrier, FeatureDefinitionAdditionalDamage damage,
        string savingThrowAbility)
    {
        internal SpellDefinition Spell { get; } = spell;
        internal ConditionDefinition Carrier { get; } = carrier;
        internal FeatureDefinitionAdditionalDamage Damage { get; } = damage;
        internal string SavingThrowAbility { get; } = savingThrowAbility;
        internal FeatureDefinition[] Features { get; } = carrier.Features.ToArray();
        internal EffectDescription Effect { get; } = EffectDescriptionBuilder.Create(spell.EffectDescription).Build();
        internal string Description { get; } = spell.GuiPresentation.Description;
        internal string CarrierDescription { get; } = carrier.GuiPresentation.Description;
    }
}
