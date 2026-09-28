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
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Properties;
using SolastaUnfinishedBusiness.Spells;
using static RuleDefinitions;
using static FeatureDefinitionAttributeModifier;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionFeatureSets;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionAttributeModifiers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionFightingStyleChoices;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    internal static readonly SpellDefinition DivineSmiteSpell = SpellBuilders.BuildDivineSmite();

    internal static void SwitchPaladinRadiantStrikes()
    {
        var feature = FeatureDefinitionAdditionalDamages.AdditionalDamagePaladinImprovedDivineSmite;
        feature.GuiPresentation.title = Main.Settings.EnablePaladinRadiantStrikes2024
            ? "Feature/&PaladinRadiantStrikes2024Title"
            : "Feature/&PaladinImprovedDivineSmiteTitle";
        feature.GuiPresentation.description = Main.Settings.EnablePaladinRadiantStrikes2024
            ? "Feature/&PaladinRadiantStrikes2024Description"
            : "Feature/&PaladinImprovedDivineSmiteDescription";
    }


    internal static readonly FeatureDefinitionAutoPreparedSpells DivineSmite2024AutoSpell =
        FeatureDefinitionAutoPreparedSpellsBuilder.Create("AutoPreparedSpellsDivineSmite2024")
            .SetGuiPresentationNoContent(hidden: true)
            .SetSpellcastingClass(Paladin)
            .SetAutoTag("Paladin")
            .AddPreparedSpellGroup(2, DivineSmiteSpell)
            .AddToDB();
    
    private static readonly FeatureDefinitionFeatureSet DivineSmite2024FeatureSet = FeatureDefinitionFeatureSetBuilder
        .Create("FeatureSetDivineSmite2024")
        .SetGuiPresentation(Category.Feature)
        .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Union)
        .SetFeatureSet(DivineSmite2024AutoSpell)
        .AddToDB();

    private static readonly FeatureUnlockByLevel DivineSmite2024Unlock = new(DivineSmite2024FeatureSet, 2);

    private static readonly FeatureDefinitionAttributeModifier AttributeModifierPaladinChannelDivinity11 =
        FeatureDefinitionAttributeModifierBuilder
            .Create("AttributeModifierPaladinChannelDivinity11")
            .SetGuiPresentationNoContent(true)
            .SetModifier(AttributeModifierOperation.Additive, AttributeDefinitions.ChannelDivinityNumber, 1)
            .AddToDB();

    private static readonly ConditionDefinition ConditionFrightenedByAbjureFoes = ConditionDefinitionBuilder
        .Create(ConditionDefinitions.ConditionFrightened, "ConditionFrightenedByAbjureFoes")
        .SetParentCondition(ConditionDefinitions.ConditionFrightened)
        .SetSpecialInterruptions(ConditionInterruption.Damaged)
        .SetFeatures(
            FeatureDefinitionActionAffinityBuilder
                .Create("ActionAffinityFrightenedByAbjureFoes")
                .SetGuiPresentationNoContent(true)
                .SetForbiddenActions(ActionDefinitions.Id.DashBonus, ActionDefinitions.Id.DashMain)
                .AddToDB())
        .AddCustomSubFeatures(new ActionFinishedByMeCheckBonusOrMainOrMove())
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerPaladinAbjureFoes = FeatureDefinitionPowerBuilder
        .Create("PowerPaladinAbjureFoes")
        .SetGuiPresentation(Category.Feature,
            Sprites.GetSprite("PowerPaladinAbjureFoes", Resources.PowerPaladinAbjureFoes, 256, 128))
        .SetUsesFixed(ActivationTime.Action, RechargeRate.ChannelDivinity)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.Minute, 1)
                .SetTargetingData(Side.Enemy, RangeType.Distance, 12, TargetType.IndividualsUnique)
                .SetSavingThrowData(false, AttributeDefinitions.Wisdom, true,
                    EffectDifficultyClassComputation.SpellCastingFeature)
                .SetEffectForms(
                    EffectFormBuilder
                        .Create()
                        .HasSavingThrow(EffectSavingThrowType.Negates)
                        .SetConditionForm(ConditionFrightenedByAbjureFoes, ConditionForm.ConditionOperation.Add)
                        .Build())
                .SetCasterEffectParameters(PowerClericTurnUndead)
                .Build())
        .AddCustomSubFeatures(new ModifyEffectDescriptionPowerPaladinAbjureFoes())
        .AddToDB();

    private static readonly FeatureDefinitionPower PowerPaladinRestoringTouch = FeatureDefinitionPowerBuilder
        .Create("PowerPaladinRestoringTouch")
        .SetGuiPresentation(Category.Feature)
        .SetUsesFixed(ActivationTime.NoCost, RechargeRate.HealingPool, 5)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetTargetingData(Side.Ally, RangeType.Distance, 12, TargetType.IndividualsUnique)
                .Build())
        .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
        .AddToDB();

    private static readonly ConditionDefinition[] RestoringTouchConditions =
    [
        ConditionDefinitions.ConditionFrightened,
        ConditionDefinitions.ConditionBlinded,
        ConditionDefinitions.ConditionCharmed,
        ConditionDefinitions.ConditionDeafened,
        ConditionDefinitions.ConditionParalyzed,
        ConditionDefinitions.ConditionStunned
    ];

    private static readonly List<(CharacterSubclassDefinition Subclass, FeatureUnlockByLevel Feature)>
        SubclassFeatureTuples = [];

    private static void LoadPaladinRestoringTouch()
    {
        PowerPaladinLayOnHands.AddCustomSubFeatures(new PowerOrSpellFinishedByMeRestoringTouch());

        var powers = new List<FeatureDefinitionPower>();

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var condition in RestoringTouchConditions)
        {
            var power = FeatureDefinitionPowerSharedPoolBuilder
                .Create($"PowerPaladinRestoringTouch{condition.Name}")
                .SetGuiPresentation("Feature/&PowerPaladinRestoringTouchSubPowerTitle",
                    "Feature/&PowerPaladinRestoringTouchSubPowerDescription")
                .AddCustomSubFeatures(new FormattedDefinitionText(
                    () => Gui.Format("Feature/&PowerPaladinRestoringTouchSubPowerTitle", condition.FormatTitle()),
                    () => Gui.Format("Feature/&PowerPaladinRestoringTouchSubPowerDescription", condition.FormatTitle())))
                .SetSharedPool(ActivationTime.NoCost, PowerPaladinRestoringTouch, 5)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetTargetingData(Side.Ally, RangeType.Distance, 12, TargetType.IndividualsUnique)
                    .SetEffectForms(EffectFormBuilder.RemoveConditionForm(condition))
                    .Build())
                .AddToDB();

            powers.Add(power);
        }

        PowerBundle.RegisterPowerBundle(PowerPaladinRestoringTouch, false, [.. powers]);
    }

    private static void LoadPaladinRestoreLevel20Features()
    {
        // Include inactive UB subclasses as well as the native subclasses from the class choice.
        var subclasses = FeatureDefinitionSubclassChoices.SubclassChoicePaladinSacredOaths.Subclasses
            .Select(GetDefinition<CharacterSubclassDefinition>)
            .Concat(SubclassesContext.KlassListContextTab[Paladin].AllSubClasses)
            .Distinct();
        var rechargePowers = new Dictionary<FeatureDefinitionPower, FeatureDefinitionPower>();

        foreach (var subclass in subclasses)
        {
            var powers = subclass.FeatureUnlocks
                .Where(unlock => unlock.Level == 20)
                .SelectMany(unlock => EnumerateFeatureTree(unlock.FeatureDefinition))
                .OfType<FeatureDefinitionPower>()
                .Where(power => power.RechargeRate == RechargeRate.LongRest &&
                                power.ActivationTime != ActivationTime.Permanent &&
                                power.CostPerUse > 0)
                .Distinct();

            foreach (var power in powers)
            {
                if (!rechargePowers.TryGetValue(power, out var rechargePower))
                {
                    rechargePower = FeatureDefinitionPowerBuilder
                        .Create("PowerRecharge" + power.Name)
                        .SetGuiPresentation(
                            "Feature/&FeaturePaladinRechargeLv20PowerTitle",
                            "Feature/&FeaturePaladinRechargeLv20PowerDescription",
                            Sprites.GetSprite("PowerCallForCharge", Resources.PowerCallForCharge, 256, 128))
                        .SetUsesFixed(ActivationTime.NoCost)
                        .SetEffectDescription(EffectDescriptionBuilder.Create()
                            .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                            .SetParticleEffectParameters(PowerPaladinLayOnHands)
                            .UseQuickAnimations()
                            .Build())
                        .SetShowCasting(true)
                        .AddCustomSubFeatures(
                            new CustomBehaviourPaladinRechargeLv20Power(power),
                            new FormattedDefinitionText(
                                () => Gui.Format("Feature/&FeaturePaladinRechargeLv20PowerTitle", power.FormatTitle()),
                                () => Gui.Format("Feature/&FeaturePaladinRechargeLv20PowerDescription", power.FormatTitle())))
                        .AddToDB();
                    rechargePowers.Add(power, rechargePower);
                }

                SubclassFeatureTuples.Add((subclass, new FeatureUnlockByLevel(rechargePower, 20)));
            }
        }
    }

    internal static void SwitchPaladinRechargeLv20Power()
    {
        foreach (var (subclass, feature) in SubclassFeatureTuples)
        {
            subclass.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == feature.FeatureDefinition);

            if (Main.Settings.EnablePaladinRechargeLv20Feature)
            {
                subclass.FeatureUnlocks.Add(feature);
            }

            subclass.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        }

        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characterService == null)
        {
            return;
        }

        foreach (var character in characterService.PartyCharacters)
        {
            if (character.RulesetCharacter is RulesetCharacterHero hero)
            {
                UpdatePaladinRechargeLv20Power(hero);
            }
        }
    }

    internal static void UpdatePaladinRechargeLv20Power(RulesetCharacterHero hero)
    {
        if (!hero.ClassesAndSubclasses.TryGetValue(Paladin, out var subclass))
        {
            return;
        }

        var enabled = Main.Settings.EnablePaladinRechargeLv20Feature && hero.GetClassLevel(Paladin) >= 20;
        var tag = AttributeDefinitions.GetSubclassTag(Paladin, 20, subclass);
        var powers = SubclassFeatureTuples.Where(entry => entry.Subclass == subclass)
            .Select(entry => (FeatureDefinitionPower)entry.Feature.FeatureDefinition)
            .ToArray();

        if (!hero.ActiveFeatures.TryGetValue(tag, out var features))
        {
            if (!enabled || powers.Length == 0)
            {
                return;
            }

            features = [];
            hero.ActiveFeatures.Add(tag, features);
        }

        foreach (var power in powers)
        {
            if (enabled)
            {
                features.TryAdd(power);
            }
            else
            {
                features.Remove(power);
                hero.UsablePowers.RemoveAll(usable => usable.PowerDefinition == power);
            }
        }

        if (enabled)
        {
            foreach (var power in powers.Where(power => hero.GetPowerFromDefinition(power) == null))
            {
                hero.UsablePowers.Add(PowerProvider.Get(power, hero));
            }
        }
    }

    private static IEnumerable<FeatureDefinition> EnumerateFeatureTree(FeatureDefinition root)
    {
        var pending = new Stack<FeatureDefinition>();
        var visited = new HashSet<FeatureDefinition>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var feature = pending.Pop();

            if (feature == null || !visited.Add(feature))
            {
                continue;
            }

            yield return feature;

            if (feature is not FeatureDefinitionFeatureSet featureSet)
            {
                continue;
            }

            foreach (var child in featureSet.FeatureSet)
            {
                pending.Push(child);
            }
        }
    }

    internal static void SwitchPaladinDivineSmite()
    {
        //Hide 2014 feature if 2024 is enabled
        FeatureDefinitionAdditionalDamages.AdditionalDamagePaladinDivineSmite.GuiPresentation.hidden =
            Main.Settings.EnablePaladinSmite2024;

        //Auto-prepared Divine Smite spell
        if (Main.Settings.EnablePaladinSmite2024)
        {
            Paladin.FeatureUnlocks.TryAdd(DivineSmite2024Unlock);
        }
        else
        {
            Paladin.FeatureUnlocks.Remove(DivineSmite2024Unlock);
        }

        Paladin.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);

        //Add spell knowledge
        foreach (var duplet in SpellListDefinitions.SpellListPaladin.SpellsByLevel)
        {
            if (duplet.level != 1) { continue; }

            if (Main.Settings.EnablePaladinSmite2024)
            {
                duplet.spells.TryAdd(DivineSmiteSpell);
            }
            else
            {
                duplet.spells.Remove(DivineSmiteSpell);
            }

            break;
        }

        //Update all currently active characters
        var locationCharacterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        if (locationCharacterService != null)
        {
            foreach (var partyCharacter in locationCharacterService.PartyCharacters)
            {
                UpdatePaladinSmite(partyCharacter.RulesetCharacter as RulesetCharacterHero);
            }
        }
    }

    internal static void UpdatePaladinSmite(RulesetCharacterHero hero)
    {
        var tag = AttributeDefinitions.GetClassTag(Paladin, 2);
        if (!hero.ActiveFeatures.TryGetValue(tag, out var features)) { return; }

        if (Main.Settings.EnablePaladinSmite2024)
        {
            features.TryAddRange(DivineSmite2024FeatureSet.FeatureSet);
        }
        else
        {
            features.RemoveAll(DivineSmite2024FeatureSet.FeatureSet);
        }

        foreach (var repertoire in hero.SpellRepertoires)
        {
            hero.ComputeAutopreparedSpells(repertoire);
            
            var castingFeature = repertoire.SpellCastingFeature;
            if (castingFeature.SpellReadyness != SpellReadyness.Prepared
                || castingFeature.SpellKnowledge != SpellKnowledge.WholeList)
            {
                continue;
            }

            //un-prepare unknown spells
            repertoire.PreparedSpells.RemoveAll(spell =>
                castingFeature.SpellListDefinition.SpellsByLevel.All(x => !x.Spells.Contains(spell)));
                
            //prepare auto-spells
            repertoire.PreparedSpells.TryAddRange(repertoire.AutoPreparedSpells);
        }
    }

    internal static void SwitchPaladinSpellCastingAtOne()
    {
        var level = Main.Settings.EnablePaladinSpellCastingAtLevel1 ? 1 : 2;

        foreach (var featureUnlock in Paladin.FeatureUnlocks
                     .Where(x => x.FeatureDefinition == FeatureDefinitionCastSpells.CastSpellPaladin))
        {
            featureUnlock.level = level;
        }

        // allows back and forth compatibility with EnableRitualOnAllCasters2024
        foreach (var featureUnlock in Paladin.FeatureUnlocks
                     .Where(x => x.FeatureDefinition == FeatureSetClericRitualCasting))
        {
            featureUnlock.level = level;
        }

        Paladin.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);

        if (Main.Settings.EnablePaladinSpellCastingAtLevel1)
        {
            FeatureDefinitionCastSpells.CastSpellPaladin.slotsPerLevels = SharedSpellsContext.HalfRoundUpCastingSlots;
            SharedSpellsContext.ClassCasterType[PaladinClass] =
                FeatureDefinitionCastSpellBuilder.CasterProgression.HalfRoundUp;
        }
        else
        {
            FeatureDefinitionCastSpells.CastSpellPaladin.slotsPerLevels = SharedSpellsContext.HalfCastingSlots;
            SharedSpellsContext.ClassCasterType[PaladinClass] =
                FeatureDefinitionCastSpellBuilder.CasterProgression.Half;
        }
    }

    internal static void SwitchPaladinAbjureFoes()
    {
        Paladin.FeatureUnlocks
            .RemoveAll(x => x.FeatureDefinition == PowerPaladinAbjureFoes);

        if (Main.Settings.EnablePaladinAbjureFoes2024)
        {
            Paladin.FeatureUnlocks.Add(new FeatureUnlockByLevel(PowerPaladinAbjureFoes, 9));
        }

        Paladin.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchPaladinChannelDivinity()
    {
        Paladin.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == AttributeModifierPaladinChannelDivinity11);

        if (Main.Settings.EnablePaladinChannelDivinity2024)
        {
            AttributeModifierPaladinChannelDivinity.modifierValue = 2;
            AttributeModifierPaladinChannelDivinity.GuiPresentation.description =
                "Feature/&PaladinChannelDivinityDescription";
            Paladin.FeatureUnlocks.Add(new FeatureUnlockByLevel(AttributeModifierPaladinChannelDivinity11, 11));
        }
        else
        {
            AttributeModifierPaladinChannelDivinity.modifierValue = 1;
            AttributeModifierPaladinChannelDivinity.GuiPresentation.description =
                "Feature/&ClericChannelDivinityDescription";
            Paladin.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == AttributeModifierPaladinChannelDivinity11);
        }

        Paladin.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchPaladinLayOnHand()
    {
        PowerPaladinLayOnHands.activationTime = Main.Settings.EnablePaladinLayOnHands2024
            ? ActivationTime.BonusAction
            : ActivationTime.Action;
        PowerPaladinNeutralizePoison.activationTime = Main.Settings.EnablePaladinLayOnHands2024
            ? ActivationTime.BonusAction
            : ActivationTime.Action;
    }

    internal static void SwitchPaladinRestoringTouch()
    {
        Paladin.FeatureUnlocks
            .RemoveAll(x =>
                x.FeatureDefinition == PowerPaladinRestoringTouch ||
                x.FeatureDefinition == PowerPaladinCleansingTouch);

        Paladin.FeatureUnlocks.Add(Main.Settings.EnablePaladinRestoringTouch2024
            ? new FeatureUnlockByLevel(PowerPaladinRestoringTouch, 14)
            : new FeatureUnlockByLevel(PowerPaladinCleansingTouch, 14));

        Paladin.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private sealed class ModifyEffectDescriptionPowerPaladinAbjureFoes : IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return definition == PowerPaladinAbjureFoes;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            var charisma = character.TryGetAttributeValue(AttributeDefinitions.Charisma);
            var charismaModifier = AttributeDefinitions.ComputeAbilityScoreModifier(charisma);
            var targets = Math.Max(1, charismaModifier);

            effectDescription.targetParameter = targets;

            return effectDescription;
        }
    }

    private sealed class PowerOrSpellFinishedByMeRestoringTouch : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            if (!Main.Settings.EnablePaladinRestoringTouch2024 || action.Countered || action.ExecutionFailed ||
                action.ActionParams.TargetCharacters.Count == 0)
            {
                yield break;
            }

            var caster = action.ActingCharacter;
            var rulesetCaster = caster.RulesetCharacter;
            var usablePowerPool = rulesetCaster.UsablePowers.FirstOrDefault(u => u.PowerDefinition == PowerPaladinRestoringTouch);
            
            //No `Restoring Touch` - too low level
            if (usablePowerPool == null) { yield break; }

            var aborted = false;
            var target = action.ActionParams.TargetCharacters[0];
            var rulesetTarget = target.RulesetCharacter;

            while (!aborted && rulesetCaster.GetRemainingUsesOfPower(usablePowerPool) > 0)
            {
                var usablePowers = RestoringTouchConditions
                    .Where(x => rulesetTarget.HasConditionOfTypeOrSubType(x.Name))
                    .Select(x =>
                        PowerProvider.Get(
                            GetDefinition<FeatureDefinitionPower>($"PowerPaladinRestoringTouch{x.Name}"),
                            rulesetCaster))
                    .ToArray();

                if (usablePowers.Length == 0)
                {
                    yield break;
                }

                rulesetCaster.UsablePowers.AddRange(usablePowers);

                yield return caster.MyReactToSpendPowerBundle(
                    usablePowerPool,
                    [target],
                    caster,
                    "RestoringTouch",
                    reactionNotValidated: _ => aborted = true);

                foreach (var usablePower in usablePowers)
                {
                    rulesetCaster.UsablePowers.Remove(usablePower);
                }
            }
        }
    }

    internal sealed class CustomBehaviourPaladinRechargeLv20Power(FeatureDefinitionPower powerToRecharge)
        : IPowerOrSpellFinishedByMe, IValidatePowerUse
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition power)
        {
            var character = action.ActingCharacter.RulesetCharacter;

            // Recheck the same resources at completion; a cancelled or stale command must not spend a slot.
            if (action.Countered || action.ExecutionFailed ||
                !CanUsePower(character, power as FeatureDefinitionPower))
            {
                yield break;
            }

            var repertoire = character.GetClassSpellRepertoire(Paladin);
            var usablePower = character.GetPowerFromDefinition(powerToRecharge);

            repertoire.SpendSpellSlot(5);
            character.UpdateUsageForPowerPool(-powerToRecharge.CostPerUse, usablePower);
            character.PowerRecharged?.Invoke(character, usablePower);
        }

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power)
        {
            if (!Main.Settings.EnablePaladinRechargeLv20Feature || character.GetClassLevel(Paladin) < 20 ||
                character.GetPowerFromDefinition(powerToRecharge) is not { } usablePower ||
                character.GetRemainingUsesOfPower(usablePower) > 0)
            {
                return false;
            }

            var repertoire = character.GetClassSpellRepertoire(Paladin);
            var remaining = 0;
            repertoire?.GetSlotsNumber(5, out remaining, out _);

            return remaining > 0;
        }
    }

    internal static void SwitchPaladinAnyFightingStyle()
    {
        var fightingStyles = FightingStylePaladin.FightingStyles;
        
        fightingStyles.Remove("TwoWeapon");
        fightingStyles.Remove("Archery");

        if (Main.Settings.EnablePaladinAnyFightingStyle2024)
        {
            fightingStyles.TryAdd("TwoWeapon");
            fightingStyles.TryAdd("Archery");
        }
    }
}
