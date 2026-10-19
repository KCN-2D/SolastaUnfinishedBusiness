using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Validators;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Feats;

// Epic boons share their level gate and reversible ability increase, while their
// powers use the normal action, resource, condition and spell-repertoire paths.
internal static class EpicBoons
{
    private static readonly List<FeatDefinition> Feats = [];
    private static readonly Dictionary<string, FeatureDefinition> AbilityIncreases = [];
    private static readonly string[] MentalAbilities =
        [AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma];
    private static readonly string[] EnergyTypes =
        [DamageTypeAcid, DamageTypeCold, DamageTypeFire, DamageTypeLightning, DamageTypeNecrotic,
            DamageTypePoison, DamageTypePsychic, DamageTypeRadiant, DamageTypeThunder];
    private const string Prefix = "BoonOf";
    private const string Suffix = "2024";

    internal static IReadOnlyList<FeatDefinition> CreateFeats()
    {
        if (Feats.Count != 0)
        {
            return Feats;
        }

        Build("CombatProwess", [Feature("CombatProwess", new CombatProwess()),
            ReactionToggle("CombatProwess", ExtraActionId.BoonOfCombatProwess2024Toggle)]);
        BuildTravel();
        BuildEnergyResistance();
        BuildEruptingSpellpower();
        BuildFate();
        BuildFortitude();
        Build("IrresistibleOffense", [ReactionToggle("IrresistibleOffense", ExtraActionId.BoonOfIrresistibleOffense2024Toggle)],
            [AttributeDefinitions.Strength, AttributeDefinitions.Dexterity],
            extra: ability => [Feature("IrresistibleOffense" + ability, new IrresistibleOffense(ability))]);
        BuildSchoolMastery();
        BuildRecovery();
        BuildSkill();
        BuildSpeed();
        var recall = FeatureDefinitionMagicAffinityBuilder.Create("MagicAffinityBoonOfSpellRecall2024")
            .SetGuiPresentationNoContent(true).SetPreserveSlotRolls(16, 4)
            .AddCustomSubFeatures(new SpellCastingResourceContext.SlotPreservationRoll(
                DieType.D4, (roll, level) => roll == level)).AddToDB();
        Build("SpellRecall", [recall], MentalAbilities, caster: true);
        // Old saves can still reference this former component; retain its identity without the extra benefit.
        FeatureDefinitionSavingThrowAffinityBuilder.Create("SavingThrowAffinityBoonOfTheIronMind2024")
            .SetGuiPresentationNoContent(true).AddToDB();
        Build("TheIronMind", [Feature("TheIronMind", new IronMind())]);
        BuildNightSpirit();
        Build("Truesight", [FeatureDefinitionSenseBuilder.Create("SenseBoonOfTruesight2024")
            .SetGuiPresentationNoContent(true).SetSense(SenseMode.Type.Truesight, 12).AddToDB()]);
        return Feats;
    }

    private static FeatDefinition GetBoon(string name) => GetDefinition<FeatDefinition>("FeatGroup" + Prefix + name + Suffix);

    private static FeatureDefinition Feature(string name, params object[] behaviors) => FeatureDefinitionBuilder
        .Create("Feature" + Prefix + name + Suffix).SetGuiPresentationNoContent(true)
        .AddCustomSubFeatures(behaviors).AddToDB();

    private static FeatureDefinitionPower Pool(string name, RechargeRate recharge, int uses) =>
        FeatureDefinitionPowerBuilder.Create("Power" + Prefix + name + "Pool" + Suffix)
            .SetGuiPresentationNoContent(true).SetUsesFixed(ActivationTime.NoCost, recharge, 1, uses)
            .SetShowCasting(false)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden).AddToDB();

    private static FeatureDefinitionActionAffinity ReactionToggle(string name, ExtraActionId toggle,
        string reaction = null)
    {
        var description = name == "EruptingSpellpower"
            ? "Feature/&PowerBoonOfEruptingSpellpower2024Description"
            : reaction == "EnergyRedirection" ? "Reaction/&UseBoonOfEnergyRedirection2024Description"
            : "Reaction/&CustomReactionBoonOf" + (reaction ?? name) + Suffix + "Description";
        ActionDefinitionBuilder.Create(SolastaUnfinishedBusiness.Api.DatabaseHelper.ActionDefinitions.MetamagicToggle,
                toggle.ToString())
            .SetGuiPresentation("Feat/&FeatBoonOf" + name + Suffix + "Title", description,
                Sprites.GetToggleActionSprite(toggle))
            .SetActionId(toggle).SetActionType(ActionType.NoCost).SetActionScope(ActionScope.All)
            .SetActivatedPower(null, false).SetParameter(ActionParameter.None)
            .RequiresAuthorization().OverrideClassName("Toggle").AddToDB();
        // This permission controls future prompts, not use of the associated pool.
        // Keeping the toggle independent lets the player change it after spending that pool.
        return FeatureDefinitionActionAffinityBuilder.Create(
                FeatureDefinitionActionAffinitys.ActionAffinitySorcererMetamagicToggle,
                "ActionAffinity" + toggle)
            .SetGuiPresentationNoContent(true).SetAuthorizedActions((Id)toggle).AddToDB();
    }

    private static bool PromptEnabled(RulesetCharacter character, ExtraActionId toggle) =>
        character?.IsToggleEnabled((Id)toggle) == true;

    private static FeatDefinition Build(string name, FeatureDefinition[] features, string[] abilities = null,
        bool caster = false, Func<string, FeatureDefinition[]> extra = null, string variant = null)
    {
        abilities ??= AttributeDefinitions.AbilityScoreNames;
        var family = Prefix + name + Suffix;
        var title = "Feat/&Feat" + family + "Title";
        var description = "Feat/&Feat" + family + "Description";
        var sprite = Sprites.GetPowerSprite("CapabilityEpicBoon" + name + Suffix);
        var children = new List<FeatDefinition>();
        foreach (var ability in abilities)
        {
            var abilityKey = name + variant + ability;
            if (!AbilityIncreases.TryGetValue(abilityKey, out var increase))
            {
                var marker = ConditionDefinitionBuilder.Create("ConditionBoonAbility" + abilityKey + Suffix)
                    .SetGuiPresentationNoContent(true).SetSilent(Silent.Always)
                    .SetAmountOrigin(ConditionDefinition.OriginOfAmount.SourceGain)
                    .SetSpecialDuration(DurationType.Permanent).AddToDB();
                increase = FeatureDefinitionBuilder.Create("FeatureBoonAbility" + abilityKey + Suffix)
                    .SetGuiPresentationNoContent(true)
                    .AddCustomSubFeatures(new EpicAbilityIncrease(ability, marker)).AddToDB();
                AbilityIncreases.Add(abilityKey, increase);
            }
            var selectedAbility = ability;
            var trainedFeatures = features.Concat(extra?.Invoke(ability) ?? []).Append(increase).ToArray();
            foreach (var feature in trainedFeatures.Where(feature => feature.GuiPresentation.Hidden &&
                         feature.GuiPresentation.Title == Gui.NoLocalization))
            {
                // Hidden components still identify their source in native rolls and combat feedback.
                feature.GuiPresentation.title = title;
                feature.GuiPresentation.description = description;
                feature.GuiPresentation.spriteReference = sprite;
            }
            var builder = FeatDefinitionWithPrerequisitesBuilder.Create("Feat" + family + variant + ability)
                .SetGuiPresentation(title, description, sprite).SetFeatFamily(family)
                .SetFeatures(trainedFeatures)
                .SetValidators(ValidateLevel, (feat, hero) => ValidateAbility(hero, selectedAbility, feat));
            if (caster) builder.SetMustCastSpellsPrerequisite();
            var child = builder.AddToDB();

            string FormatTitle()
            {
                var abilityTitle = Gui.Localize("Attribute/&" + selectedAbility + "TitleLong");

                return variant == null
                    ? Gui.Format("Feat/&EpicBoonAbilityTitleFormat", Gui.Localize(title), abilityTitle)
                    : Gui.Format("Feat/&GeneralFeat2024VariantTitle2", Gui.Localize(title),
                        Gui.Localize("SchoolOfMagic/&" + variant + "Title"), abilityTitle);
            }

            child.AddCustomSubFeatures(new FormattedDefinitionText(FormatTitle));
            children.Add(child);
        }
        var group = GroupFeats.MakeGroupWithPreRequisite("FeatGroup" + family + variant, family, ValidateLevel,
            children.ToArray());
        group.GuiPresentation.title = title;
        group.GuiPresentation.description = description;
        group.GuiPresentation.spriteReference = sprite;
        if (caster) group.mustCastSpellsPrerequisite = true;
        if (variant == null) Feats.Add(group);
        else group.AddCustomSubFeatures(new FormattedDefinitionText(() => VariantTitle(title, variant)));
        return group;
    }

    private static string VariantTitle(string title, string variant) => variant == null ? Gui.Localize(title) :
        Gui.Format("Feat/&EpicBoonAbilityTitleFormat", Gui.Localize(title),
            Gui.Localize("SchoolOfMagic/&" + variant + "Title"));

    private static (bool result, string output) ValidateLevel(FeatDefinitionWithPrerequisites _, RulesetCharacterHero hero)
    {
        var description = Gui.Format("Tooltip/&PreReqLevelFormat", "19");
        var valid = hero != null && LevelUpHelper.GetCurrentOrPendingCharacterLevel(hero) >= 19;
        return (valid, valid ? description : Gui.Colorize(description, Gui.ColorFailure));
    }

    private static (bool result, string output) ValidateAbility(RulesetCharacterHero hero, string ability,
        FeatDefinition candidate)
    {
        var pending = 0;
        if (hero != null && hero.TryGetHeroBuildingData(out var data))
        {
            pending = data.LevelupTrainedFeats.Values.SelectMany(value => value)
                .Where(feat => feat != candidate).SelectMany(feat => feat.Features)
                .SelectMany(feature => feature.GetAllSubFeaturesOfType<EpicAbilityIncrease>())
                .Count(increase => increase.Ability == ability && !Has(hero, increase.Marker));
        }
        var valid = hero != null &&
            Math.Max(hero.GetAttribute(ability).BaseValue, hero.TryGetAttributeValue(ability)) + pending < 30;
        return (valid, valid ? string.Empty : Gui.Colorize(
            Gui.Localize("Feat/&EpicBoonAbilityMaximumPrerequisite"), Gui.ColorFailure));
    }

    // The marker persists the actual cap increments. This distinguishes a native
    // 30/20 cap from an already increased cap after saving, reloading or undoing.
    private sealed class EpicAbilityIncrease(string ability, ConditionDefinition marker) : ICustomLevelUpLogic, ICustomLevelUpState
    {
        internal string Ability => ability;
        internal ConditionDefinition Marker => marker;
        public IEnumerable<RulesetCondition> GetStateConditions(RulesetCharacterHero hero) =>
            hero.ConditionsByCategory.Values.SelectMany(conditions => conditions)
                .Where(condition => condition.ConditionDefinition == marker);
        public (int BaseValue, int MaxValue, int MaxEditableValue) GetAttributeIncreases(
            RulesetCharacterHero hero, string attribute)
        {
            if (attribute != ability || !hero.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, marker.Name, out var state)) return default;
            return (1, state.Amount / 64, state.Amount % 64);
        }
        public void ApplyFeature(RulesetCharacterHero hero, string tag)
        {
            if (Has(hero, marker)) return;
            var attribute = hero.GetAttribute(ability);
            if (attribute.BaseValue >= 30) return;
            var value = attribute.BaseValue + 1;
            var maxIncrease = Math.Max(0, value - attribute.MaxValue);
            var editableIncrease = attribute.MaxEditableValue > 0
                ? Math.Max(0, value - attribute.MaxEditableValue) : 0;
            // Native conditions retain sourceAmount only for a non-None amount origin.
            var state = hero.InflictCondition(marker.Name, DurationType.Permanent, 0, TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect, hero.Guid, hero.CurrentFaction.Name, 1, marker.Name,
                maxIncrease * 64 + editableIncrease, 0, 0);
            if (state == null) return;
            attribute.BaseValue = value;
            attribute.MaxValue += maxIncrease;
            attribute.MaxEditableValue += editableIncrease;
            attribute.Refresh();
            hero.AbilityScoreIncreased?.Invoke(hero, ability, 1, Math.Max(maxIncrease, editableIncrease));
        }
        public void RemoveFeature(RulesetCharacterHero hero, string tag)
        {
            if (!hero.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect, marker.Name,
                    out var state)) return;
            var amount = state.Amount;
            var attribute = hero.GetAttribute(ability);
            attribute.BaseValue--;
            attribute.MaxValue -= amount / 64;
            attribute.MaxEditableValue -= amount % 64;
            hero.RemoveCondition(state);
            attribute.Refresh();
            hero.AbilityScoreIncreased?.Invoke(hero, ability, -1, -Math.Max(amount / 64, amount % 64));
        }
    }

    private static void Inflict(RulesetCharacter character, ConditionDefinition condition,
        DurationType duration = DurationType.Round, TurnOccurenceType occurrence = TurnOccurenceType.EndOfTurn,
        ulong source = 0)
    {
        character.InflictCondition(condition.Name, duration, 0, occurrence, AttributeDefinitions.TagEffect,
            source == 0 ? character.Guid : source, character.CurrentFaction.Name, 1, condition.Name, 0, 0, 0);
    }

    private static void Remove(RulesetCharacter character, ConditionDefinition condition)
    {
        foreach (var active in character.ConditionsByCategory.Values.SelectMany(value => value)
                     .Where(value => value.ConditionDefinition == condition).ToArray())
            character.RemoveCondition(active);
    }

    private static bool Has(RulesetCharacter character, ConditionDefinition condition) =>
        character.HasConditionOfCategoryAndType(AttributeDefinitions.TagEffect, condition.Name);

    private static bool Available(RulesetCharacter character, FeatureDefinitionPower power,
        bool allowIncapacitated = false) =>
        character is { IsDeadOrDyingOrUnconscious: false } &&
        (allowIncapacitated || !character.IsIncapacitated) &&
        character.CanUsePower(power, considerHaving: true) &&
        character.GetRemainingUsesOfPower(PowerProvider.Get(power, character)) > 0;

    private sealed class CombatProwess : ITryAlterOutcomeAttack, ICharacterTurnStartListener
    {
        private const string Used = "BoonOfCombatProwess2024";
        // Resolve a miss after numerical roll adjustments, before hit reactions.
        public int HandlerPriority => -1;
        public void OnCharacterTurnStarted(GameLocationCharacter character) => character.UsedSpecialFeatures.Remove(Used);
        public IEnumerator OnTryAlterOutcomeAttack(GameLocationBattleManager battleManager, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, GameLocationCharacter helper,
            ActionModifier actionModifier, RulesetAttackMode attackMode, RulesetEffect rulesetEffect)
        {
            if (helper != attacker || !PromptEnabled(attacker.RulesetCharacter, ExtraActionId.BoonOfCombatProwess2024Toggle) ||
                action.AttackRollOutcome is not (RollOutcome.Failure or RollOutcome.CriticalFailure)
                || attacker.UsedSpecialFeatures.ContainsKey(Used)) yield break;
            yield return helper.MyReactToDoNothing(ExtraActionId.DoNothingFree, attacker,
                "BoonOfCombatProwess2024", Gui.Localize("Reaction/&CustomReactionBoonOfCombatProwess2024Description"),
                () =>
                {
                    if (!PromptEnabled(attacker.RulesetCharacter, ExtraActionId.BoonOfCombatProwess2024Toggle) ||
                        attacker.UsedSpecialFeatures.ContainsKey(Used) ||
                        action.AttackRollOutcome is not (RollOutcome.Failure or RollOutcome.CriticalFailure)) return;
                    attacker.UsedSpecialFeatures[Used] = 1;
                    TryAlterOutcomeAttack.ForceHit(action, defender, actionModifier);
                }, battleManager: battleManager, target: defender, effectDefinition: GetBoon("CombatProwess"),
                reactionStillValid: () => PromptEnabled(attacker.RulesetCharacter,
                    ExtraActionId.BoonOfCombatProwess2024Toggle) && !attacker.UsedSpecialFeatures.ContainsKey(Used) &&
                    action.AttackRollOutcome is RollOutcome.Failure or RollOutcome.CriticalFailure);
        }
    }

    private static void BuildTravel()
    {
        var ready = ConditionDefinitionBuilder.Create("ConditionBoonOfDimensionalTravelReady2024")
            .SetGuiPresentationNoContent(true).SetSilent(Silent.WhenAddedOrRemoved)
            .SetSpecialInterruptions(ConditionInterruption.UsedActionOrReaction, ConditionInterruption.Moved,
                ConditionInterruption.AnyBattleTurnEnd).AddToDB();
        var power = FeatureDefinitionPowerBuilder.Create("PowerBoonOfDimensionalTravel2024")
            .SetGuiPresentation(Category.Feature, Sprites.GetPowerSprite("CapabilityEpicBoonDimensionalTravel2024"))
            .SetUsesFixed(ActivationTime.NoCost)
            .SetEffectDescription(EffectDescriptionBuilder.Create(MistyStep).UseQuickAnimations().Build())
            .AddCustomSubFeatures(new ValidatorsValidatePowerUse(character => Has(character, ready)),
                new Travel(ready)).AddToDB();
        power.GuiPresentation.title = "Feature/&PowerBoonOfDimensionalTravel2024Title";
        power.GuiPresentation.description = "Feature/&PowerBoonOfDimensionalTravel2024Description";
        Build("DimensionalTravel", [power]);
    }

    private sealed class Travel(ConditionDefinition ready) : IActionFinishedByMe, IPowerOrSpellFinishedByMe,
        ICharacterBattleStartedListener, ICharacterBattleEndedListener, IOnCharacterPostLoad, IOnLocationCharacterRestored
    {
        public int Priority => 0;
        public void OnCharacterBattleStarted(GameLocationCharacter character, bool surprise) => Remove(character.RulesetCharacter, ready);
        public void OnCharacterBattleEnded(GameLocationCharacter character) => Remove(character.RulesetCharacter, ready);
        public void OnCharacterPostLoad(RulesetCharacter character) => Remove(character, ready);
        public void OnLocationCharacterRestored(RulesetCharacter character) => Remove(character, ready);

        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            var actor = action.ActingCharacter;
            if ((Gui.Battle == null || actor.IsMyTurn()) && !action.Aborted && action.ActionType == ActionType.Main &&
                action.CompletesAction() && IsAttackOrMagicAction(action))
                // Exploration has no turn clock; the next action or move expires
                // this permission. Loading or entering/leaving battle also clears it.
                Inflict(actor.RulesetCharacter, ready, DurationType.Permanent);
            yield break;
        }

        private static bool IsAttackOrMagicAction(CharacterAction action)
        {
            return action.ActionId == Id.AttackMain || action is CharacterActionCastSpell ||
                   action.ActionParams.RulesetEffect?.OriginItem?.ItemDefinition.Magical == true ||
                   action.ActionParams.RulesetEffect is RulesetEffectPower { PowerDefinition.SurrogateToSpell: not null };
        }

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition definition)
        {
            if (!action.Aborted && !action.ExecutionFailed && !action.Countered)
            {
                Remove(action.ActingCharacter.RulesetCharacter, ready);
            }
            yield break;
        }
    }

    private static void BuildFortitude()
    {
        var cooldown = ConditionDefinitionBuilder.Create("ConditionBoonOfFortitudeHealing2024")
            .SetGuiPresentationNoContent(true).SetSilent(Silent.Always)
            .SetSpecialDuration(DurationType.Round, 0, TurnOccurenceType.StartOfTurn).AddToDB();
        var behavior = new Fortitude(cooldown);
        Build("Fortitude", [FeatureDefinitionAttributeModifierBuilder.Create("AttributeModifierBoonOfFortitude2024")
            .SetGuiPresentationNoContent(true)
            .SetModifier(FeatureDefinitionAttributeModifier.AttributeModifierOperation.Additive,
                AttributeDefinitions.HitPoints, 40).AddToDB(), Feature("Fortitude", behavior),
            ReactionToggle("Fortitude", ExtraActionId.BoonOfFortitude2024Toggle)]);
    }

    private sealed class Fortitude(ConditionDefinition cooldown) : IReactToHealingReceived,
        IHealingModificationProvider
    {
        public IEnumerator ReactToHealingReceived(RulesetCharacter character, int healing, ulong sourceGuid,
            HealingCap cap, IHealingModificationProvider provider, GameLocationBattle battle)
        {
            var actor = GameLocationCharacter.GetFromActor(character);
            if (healing <= 0 || ReferenceEquals(provider, this) || actor == null ||
                !ReferenceEquals(battle, Gui.Battle)) yield break;
            if (!Available()) yield break;
            yield return actor.MyReactToDoNothing(ExtraActionId.DoNothingFree, actor, "BoonOfFortitude2024",
                Gui.Localize("Reaction/&CustomReactionBoonOfFortitude2024Description"), () =>
                {
                    if (!Available()) return;
                    var before = character.CurrentHitPoints;
                    character.ReceiveHealing(Bonus(), true, sourceGuid, HealingCap.MaximumHitPoints, this);
                    if (battle != null && character.CurrentHitPoints > before)
                        Inflict(character, cooldown, occurrence: TurnOccurenceType.StartOfTurn);
                }, target: actor, effectDefinition: GetBoon("Fortitude"), reactionStillValid: Available);

            bool Available() => ReferenceEquals(battle, Gui.Battle) &&
                PromptEnabled(character, ExtraActionId.BoonOfFortitude2024Toggle) && !character.IsDead &&
                Bonus() > 0 && (battle == null || !Has(character, cooldown));

            int Bonus()
            {
                var maximum = character.TryGetAttributeValue(AttributeDefinitions.HitPoints);
                return Math.Min(maximum - character.CurrentHitPoints, AttributeDefinitions.ComputeAbilityScoreModifier(
                    character.TryGetAttributeValue(AttributeDefinitions.Constitution)));
            }
        }

        // The native healing-origin parameter distinguishes this bonus from a new heal.
        public string Name => "FeatureBoonOfFortitude2024";
        public int ComputeSpellHealing(int classLevel, int spellLevel, int healing) => 0;
        public RuleDefinitions.LevelSourceType AddLevel => RuleDefinitions.LevelSourceType.None;
        public int HealingBonusDiceNumber => 0;
        public DieType HealingBonusDiceType => DieType.D1;
        public bool HealsSelfWhenCastingHealingSpell => false;
        public ConditionDefinition AddsConditionWhenCastingHealingSpell => null;
        public TurnOccurenceType AddedConditionOccurenceType => TurnOccurenceType.StartOfTurn;
        public bool AdvantageOnHitDieSpending => false;
        public bool HitDiceHealsKindred => false;
        public bool HealSelfHealsKindred => false;
        public bool MedecineStabilizeTo1HitPoint => false;
        public bool CannotGainHitPoints => false;
        public bool MaximizeReceivedHealing => false;
        public UnityEngine.GameObject HealingReceivedParticle => null;
    }

    private sealed class IrresistibleOffense(string ability) : IModifyDamageAffinity,
        IPhysicalAttackBeforeHitConfirmedOnEnemy, IMagicEffectBeforeHitConfirmedOnEnemy, ITryAlterOutcomeAttack
    {
        public void ModifyDamageAffinity(RulesetActor defender, RulesetActor attacker, List<FeatureDefinition> features)
        {
            if (attacker?.GetSubFeaturesByType<IModifyDamageAffinity>().Contains(this) != true) return;
            features.RemoveAll(feature => feature is FeatureDefinitionDamageAffinity
            { DamageAffinityType: DamageAffinityType.Resistance } affinity && IsPhysical(affinity.DamageType));
        }
        private static bool IsPhysical(string type) => type is DamageTypeBludgeoning or DamageTypePiercing or DamageTypeSlashing;
        private readonly ConditionalWeakTable<ActionModifier, AttackRoll> _rolls = new();
        private sealed class AttackRoll(CharacterAction action, RulesetActor target)
        {
            internal CharacterAction Action { get; } = action;
            internal RulesetActor Target { get; } = target;
        }
        public int HandlerPriority => int.MaxValue;
        public IEnumerator OnTryAlterOutcomeAttack(GameLocationBattleManager battle, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, GameLocationCharacter helper,
            ActionModifier modifier, RulesetAttackMode mode, RulesetEffect effect)
        {
            if (helper == attacker)
            {
                _rolls.Remove(modifier);
                if (action.AttackRoll == 20) _rolls.Add(modifier, new AttackRoll(action, defender.RulesetActor));
            }
            yield break;
        }
        private IEnumerator Add(GameLocationCharacter attacker, GameLocationCharacter defender,
            ActionModifier modifier, List<EffectForm> forms)
        {
            if (!_rolls.TryGetValue(modifier, out var roll)) yield break;
            _rolls.Remove(modifier);
            var form = forms.FirstOrDefault(value => value.FormType == EffectForm.EffectFormType.Damage);
            if (form == null || !Available()) yield break;
            yield return attacker.MyReactToDoNothing(ExtraActionId.DoNothingFree, attacker,
                "BoonOfIrresistibleOffense2024",
                Gui.Localize("Reaction/&CustomReactionBoonOfIrresistibleOffense2024Description"), () =>
                {
                    if (!Available()) return;
                    forms.Add(EffectFormBuilder.Create().SetDamageForm(form.DamageForm.DamageType, 0, DieType.D1,
                        attacker.RulesetCharacter.TryGetAttributeValue(ability)).Build());
                }, target: defender, effectDefinition: GetBoon("IrresistibleOffense"), reactionStillValid: Available);

            bool Available() => ReferenceEquals(roll.Target, defender.RulesetActor) &&
                !roll.Action.Aborted && roll.Action.AttackRoll == 20 && forms.Contains(form) &&
                !attacker.RulesetCharacter.IsDeadOrDyingOrUnconscious && !defender.RulesetCharacter.IsDead &&
                PromptEnabled(attacker.RulesetCharacter, ExtraActionId.BoonOfIrresistibleOffense2024Toggle);
        }
        public IEnumerator OnPhysicalAttackBeforeHitConfirmedOnEnemy(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier modifier,
            RulesetAttackMode attackMode, bool ranged, AdvantageType advantage, List<EffectForm> forms,
            bool firstTarget, bool critical) => Add(attacker, defender, modifier, forms);
        public IEnumerator OnMagicEffectBeforeHitConfirmedOnEnemy(GameLocationBattleManager battleManager,
            GameLocationCharacter attacker, GameLocationCharacter defender, ActionModifier modifier,
            RulesetEffect effect, List<EffectForm> forms, bool firstTarget, bool critical)
            => Add(attacker, defender, modifier, forms);
    }

    private static void BuildSkill()
    {
        // Keep the native pool's serialized definition for existing characters.
        _ = FeatureDefinitionPointPoolBuilder.Create("PointPoolBoonOfSkillExpertise2024")
            .SetGuiPresentationNoContent(true).SetPool(HeroDefinitions.PointsPoolType.Expertise, 1).AddToDB();
        var skills = DatabaseRepository.GetDatabase<SkillDefinition>().Select(skill => skill.Name).ToArray();
        Build("Skill", [FeatureDefinitionProficiencyBuilder.Create("ProficiencyBoonOfSkill2024")
            .SetGuiPresentationNoContent(true).SetProficiencies(ProficiencyType.Skill, skills).AddToDB(),
            SkillFeats.ExpertisePool]);
    }

    private static void BuildSpeed()
    {
        var power = FeatureDefinitionPowerBuilder.Create("PowerBoonOfSpeed2024")
            .SetGuiPresentation("PowerBoonOfSpeed2024", Category.Feature,
                Sprites.GetPowerSprite("CapabilityEpicBoonSpeed2024"))
            .SetUsesFixed(ActivationTime.BonusAction)
            .SetShowCasting(false)
            .SetEffectDescription(EffectDescriptionBuilder.Create(
                    GetDefinition<FeatureDefinitionPower>("PowerMonkStepOftheWindDisengage"))
                .SetDurationData(DurationType.Round, 0)
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetEffectForms(EffectFormBuilder.ConditionForm(ConditionDefinitions.ConditionDisengaging)).Build())
            .AddCustomSubFeatures(new EscapeGrapple()).AddToDB();
        Build("Speed", [power, FeatureDefinitionMovementAffinityBuilder.Create("MovementAffinityBoonOfSpeed2024")
            .SetGuiPresentationNoContent(true).SetBaseSpeedAdditiveModifier(6).AddToDB()]);
    }

    private sealed class EscapeGrapple : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition definition)
        {
            if (action.Aborted || action.ExecutionFailed || action.Countered) yield break;
            var character = action.ActingCharacter.RulesetCharacter;
            foreach (var condition in character.ConditionsByCategory.Values.SelectMany(value => value)
                         .Where(value => value.ConditionDefinition.IsSubtypeOf(RuleDefinitions.ConditionGrappled) ||
                                         value.ConditionDefinition.Name == "ConditionGrappleTarget").ToArray())
                character.RemoveCondition(condition);
        }
    }

    private sealed class IronMind : IPreventRemoveConcentrationOnDamage, IPreventForcedConcentrationLoss
    {
        public bool PreventForcedConcentrationLoss(RulesetCharacter character) => true;
        public SpellDefinition[] SpellsThatShouldNotRollConcentrationCheckFromDamage(RulesetCharacter character) =>
            character.ConcentratedSpell == null ? [] : [character.ConcentratedSpell.SpellDefinition];
    }

    private static void BuildEnergyResistance()
    {
        var pool = FeatureDefinitionPowerBuilder.Create("PowerBoonOfEnergyResistance2024")
            .SetGuiPresentation("PowerBoonOfEnergyResistance2024", Category.Feature,
                Sprites.GetPowerSprite("CapabilityEpicBoonEnergyResistance2024"))
            .SetUsesFixed(ActivationTime.NoCost, RechargeRate.LongRest, 1, 2)
            .AddCustomSubFeatures(ValidatorsValidatePowerUse.NotInCombat).AddToDB();
        var conditions = EnergyTypes.Select(type => ConditionDefinitionBuilder
            .Create("ConditionBoonOfEnergyResistance" + type + Suffix)
            .SetGuiPresentation("PowerBoonOfEnergyResistance2024", Category.Feature,
                Sprites.GetConditionSprite("ConditionEpicBoonEnergyResistance2024"))
            .SetSilent(Silent.WhenAddedOrRemoved).SetSpecialDuration(DurationType.Permanent)
            .SetFeatures(FeatureDefinitionDamageAffinityBuilder.Create("DamageAffinityBoonOfEnergyResistance" + type + Suffix)
                .SetGuiPresentationNoContent(true).SetDamageType(type)
                .SetDamageAffinityType(DamageAffinityType.Resistance).AddToDB()).AddToDB()).ToArray();
        var powers = new List<FeatureDefinitionPower>();
        var features = new List<FeatureDefinition> { pool,
            ReactionToggle("EnergyResistance", ExtraActionId.BoonOfEnergyRedirection2024Toggle, "EnergyRedirection") };
        for (var i = 0; i < EnergyTypes.Length; i++)
        {
            var type = EnergyTypes[i];
            var condition = conditions[i];
            condition.AddCustomSubFeatures(new FormattedDefinitionText(() => Gui.Format(
                "Feat/&EpicBoonAbilityTitleFormat", Gui.Localize(pool.GuiPresentation.Title),
                Gui.Localize("Rules/&" + type + "Title"))));
            var power = FeatureDefinitionPowerSharedPoolBuilder.Create("PowerBoonOfEnergyResistance" + type + Suffix)
                .SetGuiPresentation(pool.GuiPresentation).SetSharedPool(ActivationTime.NoCost, pool)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetDurationData(DurationType.Permanent)
                    .SetEffectForms(EffectFormBuilder.ConditionForm(condition))
                    .SetParticleEffectParameters(new EffectParticleParameters())
                    .SetCasterEffectParameters(ProtectionFromEnergy)
                    .SetAnimationMagicEffect(ProtectionFromEnergy.EffectDescription.AnimationMagicEffect)
                    .SetSpeedAndImpactOffset(SpeedType.Instant).UseQuickAnimations().Build())
                .AddCustomSubFeatures(ValidatorsValidatePowerUse.NotInCombat,
                    new ValidatorsValidatePowerUse(character =>
                        character.GetRemainingPowerCharges(pool) == character.GetMaxUsesForPool(pool) ||
                        !Has(character, condition)),
                    new EnergyAttunement(pool, condition, conditions))
                .AddToDB();
            power.AddCustomSubFeatures(new FormattedDefinitionText(() => condition.FormatTitle()));
            powers.Add(power);
            var damageVisual = EnergyDamageVisual(type);
            var retaliate = FeatureDefinitionPowerBuilder.Create("PowerBoonOfEnergyRedirection" + type + Suffix)
                .SetGuiPresentation("PowerBoonOfEnergyRedirection2024", Category.Feature,
                    Sprites.GetPowerSprite("CapabilityEpicBoonEnergyRedirection2024"))
                .SetUsesFixed(ActivationTime.Reaction).SetExplicitAbilityScore(AttributeDefinitions.Constitution)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 12, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                        EffectDifficultyClassComputation.AbilityScoreAndProficiency, AttributeDefinitions.Constitution)
                    .SetEffectForms(EffectFormBuilder.Create().SetDamageForm(type, 2, DieType.D12)
                        .SetBonusMode(AddBonusMode.AbilityBonus).HasSavingThrow(EffectSavingThrowType.Negates).Build())
                    // A reaction strikes one creature: retain its damage-colored caster/impact only,
                    // without copying area, projectile, zone or persistent condition particles.
                    .SetParticleEffectParameters(new EffectParticleParameters())
                    .SetCasterEffectParameters(damageVisual)
                    .SetImpactEffectParameters(type == DamageTypeRadiant
                        ? damageVisual.ParticleEffectReference() : damageVisual.ParticleImpactReference())
                    .SetAnimationMagicEffect(damageVisual.EffectDescription.AnimationMagicEffect)
                    .SetSpeedAndImpactOffset(SpeedType.Instant).UseQuickAnimations().Build())
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden).AddToDB();
            var redirection = new EnergyRedirection(retaliate, type, condition);
            retaliate.AddCustomSubFeatures(new EnergyRedirectionPermission(condition),
                new ValidatorsValidatePowerUse(character =>
                    PromptEnabled(character, ExtraActionId.BoonOfEnergyRedirection2024Toggle) && Has(character, condition)));
            features.Add(retaliate);
            condition.AddCustomSubFeatures(redirection);
        }
        PowerBundle.RegisterPowerBundle(pool, false, powers);
        Build("EnergyResistance", features.ToArray());
    }

    private static SpellDefinition EnergyDamageVisual(string damageType) => damageType switch
    {
        DamageTypeAcid => AcidSplash,
        DamageTypeCold => RayOfFrost,
        DamageTypeFire => FireBolt,
        DamageTypeLightning => LightningBolt,
        DamageTypeNecrotic => ChillTouch,
        DamageTypePoison => VenomousSpike,
        DamageTypePsychic => MindTwist,
        DamageTypeRadiant => SacredFlame,
        DamageTypeThunder => Shatter,
        _ => throw new ArgumentOutOfRangeException(nameof(damageType), damageType, null)
    };

    private sealed class EnergyAttunement(FeatureDefinitionPower pool, ConditionDefinition selected,
        ConditionDefinition[] choices) : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition definition)
        {
            if (action.Aborted || action.Countered || action.ExecutionFailed) yield break;
            var character = action.ActingCharacter.RulesetCharacter;
            if (!Has(character, selected) || definition is not FeatureDefinitionPower choice) yield break;
            var active = character.ConditionsByCategory.Values.SelectMany(value => value)
                .Where(value => choices.Contains(value.ConditionDefinition)).ToArray();
            var remaining = character.GetRemainingPowerCharges(pool);
            var maximum = character.GetMaxUsesForPool(pool);
            // A successful first choice begins the new pair. Retain the old pair
            // until this confirmation, including if the player keeps one old type.
            if (remaining == maximum - choice.CostPerUse)
            {
                foreach (var condition in active.Where(value => value.ConditionDefinition != selected))
                    character.RemoveCondition(condition);
            }
            else
            {
                foreach (var condition in active.Take(Math.Max(0, active.Length - 2)))
                    character.RemoveCondition(condition);
            }
        }
    }

    private sealed class EnergyRedirection(FeatureDefinitionPower power, string damageType,
        ConditionDefinition attunement) : IReactToDamageReceived
    {
        public IEnumerator ReactToDamageReceived(RulesetCharacter character, int damage, string type, ulong sourceGuid,
            int hitPointsBefore, int hitPointsAfter)
        {
            var defender = GameLocationCharacter.GetFromActor(character);
            if (damage <= 0 || type != damageType || defender == null ||
                !PromptEnabled(character, ExtraActionId.BoonOfEnergyRedirection2024Toggle) ||
                !Has(character, attunement) || !defender.CanReact()) yield break;
            var characters = ServiceRepository.GetService<IGameLocationCharacterService>();
            var candidates = characters?.ValidCharacters?.Where(CandidateValid).ToArray() ?? [];
            if (candidates.Length == 0) yield break;
            var usablePower = PowerProvider.Get(power, character);
            var implementation = ServiceRepository.GetService<IRulesetImplementationService>();
            var parameters = new CharacterActionParams(defender, Id.PowerReaction)
            {
                UsablePower = usablePower,
                RulesetEffect = implementation.InstantiateEffectPower(character, usablePower, false),
                IsReactionEffect = true
            };
            RulesetEntity.TryGetEntity<RulesetCharacter>(sourceGuid, out var source);
            var attacker = source == null ? null : GameLocationCharacter.GetFromActor(source);
            yield return defender.MyReactToSelectTarget(candidates, attacker ?? defender, "AdditionalEffectTarget",
                Gui.Localize("Reaction/&UseBoonOfEnergyRedirection2024Description"),
                effectDefinition: power, candidateValidator: CandidateValid, actionParams: parameters);
            if (!parameters.ReactionValidated) parameters.RulesetEffect.Terminate(false);
            yield break;

            bool CandidateValid(GameLocationCharacter candidate) =>
                character is { IsDeadOrDyingOrUnconscious: false, IsIncapacitated: false } &&
                PromptEnabled(character, ExtraActionId.BoonOfEnergyRedirection2024Toggle) &&
                Has(character, attunement) &&
                defender.GetActionTypeStatus(ActionType.Reaction) == ActionStatus.Available &&
                character.CanUsePower(power, considerHaving: true) &&
                IsEnergyRedirectionTarget(defender, candidate);
        }
    }
    private sealed class EnergyRedirectionPermission(ConditionDefinition attunement) : IValidateMagicEffectBeforeSpend
    {
        public bool IsValid(CharacterActionMagicEffect action, GameLocationCharacter actingCharacter,
            IReadOnlyList<GameLocationCharacter> targets, out string failure)
        {
            failure = string.Empty;
            return PromptEnabled(actingCharacter.RulesetCharacter, ExtraActionId.BoonOfEnergyRedirection2024Toggle) &&
                   Has(actingCharacter.RulesetCharacter, attunement) && targets.Count == 1 &&
                   IsEnergyRedirectionTarget(actingCharacter, targets[0]);
        }
    }

    private static bool IsEnergyRedirectionTarget(GameLocationCharacter defender, GameLocationCharacter candidate)
    {
        if (candidate == null || candidate == defender || !defender.IsOppositeSide(candidate.Side) ||
            candidate.RulesetCharacter is not { IsDead: false } || candidate.RulesetCharacter is RulesetCharacterEffectProxy ||
            ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters.Contains(candidate) != true ||
            !defender.IsWithinRange(candidate, 12) || !defender.CanSeeTarget(candidate)) return false;
        var battle = ServiceRepository.GetService<IGameLocationBattleService>();
        if (battle == null) return false;
        var modifier = new ActionModifier();
        battle.ComputeCover(defender, defender.LocationPosition, candidate, candidate.LocationPosition,
            modifier, CoverType.None, false);
        return modifier.CoverType != CoverType.Full;
    }

    private static void BuildFate()
    {
        var pool = Pool("Fate", RechargeRate.ShortRest, 1);
        var icon = Sprites.GetPowerSprite("CapabilityEpicBoonFate2024");
        pool.GuiPresentation.spriteReference = icon;
        pool.AddCustomSubFeatures(new ReactionResourcePowerPool(pool, Sprites.FateResourceIcon));
        var choices = new[] { "Bonus", "Penalty" }.Select(choice =>
            FeatureDefinitionPowerSharedPoolBuilder.Create("PowerBoonOfFate" + choice + Suffix)
                .SetGuiPresentation("PowerBoonOfFate" + choice + Suffix, Category.Feature, icon)
                .SetSharedPool(ActivationTime.NoCost, pool).SetShowCasting(false)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetTargetingData(Side.All, RangeType.Distance, 12, TargetType.IndividualsUnique)
                    .UseQuickAnimations().Build())
                .AddCustomSubFeatures(ModifyPowerVisibility.Hidden).AddToDB()).ToArray();
        PowerBundle.RegisterPowerBundle(pool, false, choices);
        Build("Fate", [pool, .. choices, Feature("Fate", new Fate(pool, choices[0], choices[1])),
            ReactionToggle("Fate", ExtraActionId.BoonOfFate2024Toggle)]);
    }

    private sealed class Fate(FeatureDefinitionPower pool, FeatureDefinitionPower bonus,
        FeatureDefinitionPower penalty) : ITryAlterOutcomeAttack, ITryAlterOutcomeAttributeCheck,
        ITryAlterOutcomeSavingThrow, IInitiativeEndListener
    {
        public int HandlerPriority => -8;
        public IEnumerator OnInitiativeEnded(GameLocationCharacter character)
        {
            PowerProvider.Get(pool, character.RulesetCharacter).Recharge();
            yield break;
        }
        private bool CanAlter(GameLocationCharacter actor, GameLocationCharacter helper)
        {
            return actor?.RulesetCharacter != null && helper != null &&
                PromptEnabled(helper.RulesetCharacter, ExtraActionId.BoonOfFate2024Toggle) &&
                Available(helper.RulesetCharacter, pool, allowIncapacitated: true) && helper.IsWithinRange(actor, 12) &&
                ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters.Contains(actor) == true;
        }
        private IEnumerator Prompt(GameLocationBattleManager battle, GameLocationCharacter actor,
            GameLocationCharacter helper, Action<int> change)
        {
            if (!CanAlter(actor, helper)) yield break;
            yield return helper.MyReactToSpendPowerBundle(PowerProvider.Get(pool, helper.RulesetCharacter),
                [actor], actor, "BoonOfFate2024",
                Gui.Format("Reaction/&ReactionSpendPowerBundleBoonOfFate2024Description",
                    ReactionCharacterNameFormatter.Format(actor)), request =>
                {
                    if (!CanAlter(actor, helper) ||
                        request.ReactionParams.RulesetEffect is not RulesetEffectPower selected ||
                        selected.PowerDefinition != bonus && selected.PowerDefinition != penalty) return;
                    var amount = helper.RulesetCharacter.RollDiceAndSum(DieType.D4, RollContext.None, 2);
                    change(selected.PowerDefinition == penalty ? -amount : amount);
                }, battleManager: battle, reactionStillValid: () => CanAlter(actor, helper));
        }
        public IEnumerator OnTryAlterOutcomeAttack(GameLocationBattleManager battle, CharacterAction action,
            GameLocationCharacter attacker, GameLocationCharacter defender, GameLocationCharacter helper,
            ActionModifier modifier, RulesetAttackMode mode, RulesetEffect effect)
        {
            // Natural 1/20 cannot be changed by a numerical bonus or penalty.
            if (action.AttackRoll is 1 or 20) yield break;
            yield return Prompt(battle, attacker, helper, delta =>
            {
                action.AttackSuccessDelta += delta;
                action.AttackRollOutcome = action.AttackSuccessDelta >= 0 ? RollOutcome.Success : RollOutcome.Failure;
                modifier.AttackRollModifier += delta;
            });
        }
        public IEnumerator OnTryAlterAttributeCheck(GameLocationBattleManager battle, int rawRoll,
            AbilityCheckData data, GameLocationCharacter defender, GameLocationCharacter helper) =>
            Prompt(battle, defender, helper, delta =>
            {
                data.AbilityCheckRoll += delta;
                data.AbilityCheckSuccessDelta += delta;
                data.AbilityCheckRollOutcome = data.AbilityCheckSuccessDelta >= 0 ? RollOutcome.Success : RollOutcome.Failure;
            });
        public IEnumerator OnTryAlterOutcomeSavingThrow(GameLocationBattleManager battle,
            GameLocationCharacter attacker, GameLocationCharacter defender, GameLocationCharacter helper,
            SavingThrowData data, bool visual)
        {
            if (defender?.RulesetCharacter == null || data == null ||
                defender.RulesetCharacter.IsAutomaticallyFailingSavingThrow(data.SavingThrowAbility)) yield break;
            yield return Prompt(battle, defender, helper, delta =>
            {
                data.SaveOutcomeDelta += delta;
                data.SaveOutcome = data.SaveOutcomeDelta >= 0 ? RollOutcome.Success : RollOutcome.Failure;
                if (data.SaveActionModifier != null) data.SaveActionModifier.SavingThrowModifier += delta;
            });
        }
    }
    private static void BuildRecovery()
    {
        var pool = FeatureDefinitionPowerBuilder.Create("PowerBoonOfRecovery2024")
            .SetGuiPresentation("PowerBoonOfRecovery2024", Category.Feature,
                Sprites.GetPowerSprite("CapabilityEpicBoonRecovery2024"))
            .SetUsesFixed(ActivationTime.BonusAction, RechargeRate.LongRest, 1, 10).AddToDB();
        var powers = new List<FeatureDefinitionPower>();
        for (var dice = 1; dice <= 10; dice++)
        {
            var count = dice;
            var power = FeatureDefinitionPowerSharedPoolBuilder.Create("PowerBoonOfRecovery" + dice + Suffix)
                .SetGuiPresentation(pool.GuiPresentation).SetSharedPool(ActivationTime.BonusAction, pool, dice)
                .SetEffectDescription(EffectDescriptionBuilder.Create(
                        GetDefinition<FeatureDefinitionPower>("PowerFighterSecondWind"))
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(EffectFormBuilder.Create().SetHealingForm(HealingComputation.Dice, 0,
                        DieType.D10, dice, false, HealingCap.MaximumHitPoints).Build())
                    .UseQuickAnimations().Build()).AddToDB();
            power.AddCustomSubFeatures(new FormattedDefinitionText(() => Gui.Format(
                "Feature/&PowerBoonOfRecoveryDice2024Title", count.ToString())));
            powers.Add(power);
        }
        PowerBundle.RegisterPowerBundle(pool, false, powers);
        var lastStand = Pool("RecoveryLastStand", RechargeRate.LongRest, 1);
        lastStand.GuiPresentation.spriteReference = Sprites.GetPowerSprite("CapabilityEpicBoonRecovery2024");
        lastStand.AddCustomSubFeatures(new LastStand(lastStand));
        Build("Recovery", [pool, lastStand, ReactionToggle("Recovery", ExtraActionId.BoonOfRecoveryLastStand2024Toggle)]);
    }

    private sealed class LastStand(FeatureDefinitionPower power) : IReactToDamageReceived
    {
        public IEnumerator ReactToDamageReceived(RulesetCharacter character, int damage, string damageType,
            ulong sourceGuid, int hitPointsBefore, int hitPointsAfter)
        {
            var defender = GameLocationCharacter.GetFromActor(character);
            if (hitPointsBefore <= 0 || hitPointsAfter != 0 || defender == null ||
                !PromptEnabled(character, ExtraActionId.BoonOfRecoveryLastStand2024Toggle) ||
                character.IsDead || character.CurrentHitPoints > 0 ||
                character.GetRemainingUsesOfPower(PowerProvider.Get(power, character)) <= 0) yield break;
            RulesetEntity.TryGetEntity<RulesetCharacter>(sourceGuid, out var source);
            var waiter = source == null ? defender : GameLocationCharacter.GetFromActor(source) ?? defender;
            yield return defender.MyReactToDoNothing(ExtraActionId.DoNothingFree, waiter,
                "BoonOfRecovery2024", Gui.Localize("Reaction/&CustomReactionBoonOfRecovery2024Description"), () =>
                {
                    if (!PromptEnabled(character, ExtraActionId.BoonOfRecoveryLastStand2024Toggle) ||
                        character.IsDead || character.CurrentHitPoints > 0 ||
                        character.GetRemainingUsesOfPower(PowerProvider.Get(power, character)) <= 0) return;
                    character.UsePower(PowerProvider.Get(power, character));
                    var maximum = character.TryGetAttributeValue(AttributeDefinitions.HitPoints);
                    defender.MyExecuteActionStabilizeAndStandUp(1, () =>
                    {
                        if (maximum > 1) character.ReceiveHealing(maximum / 2, true, character.Guid);
                    });
                }, target: defender, effectDefinition: GetBoon("Recovery"),
                reactionStillValid: () => PromptEnabled(character, ExtraActionId.BoonOfRecoveryLastStand2024Toggle) &&
                    !character.IsDead && character.CurrentHitPoints <= 0 &&
                    character.GetRemainingUsesOfPower(PowerProvider.Get(power, character)) > 0);
        }
    }

    private static void BuildNightSpirit()
    {
        var resistances = EnergyTypes.Concat([DamageTypeBludgeoning, DamageTypePiercing, DamageTypeSlashing, DamageTypeForce])
            .Where(type => type is not (DamageTypePsychic or DamageTypeRadiant))
            .Select(type => FeatureDefinitionDamageAffinityBuilder.Create("DamageAffinityBoonOfTheNightSpirit" + type + Suffix)
                .SetGuiPresentationNoContent(true).SetDamageType(type)
                .SetDamageAffinityType(DamageAffinityType.Resistance).AddToDB()).ToArray();
        var shadow = ConditionDefinitionBuilder.Create("ConditionBoonOfTheNightSpiritShadow2024")
            .SetGuiPresentationNoContent(true).SetSilent(Silent.WhenAddedOrRemoved).SetFeatures(resistances).AddToDB();
        var lightBuilder = FeatureDefinitionLightAffinityBuilder.Create("LightAffinityBoonOfTheNightSpirit2024")
            .SetGuiPresentationNoContent(true);
        foreach (var state in new[] { LocationDefinitions.LightingState.Dim, LocationDefinitions.LightingState.Unlit,
                     LocationDefinitions.LightingState.Darkness })
            lightBuilder.AddLightingEffectAndCondition(new FeatureDefinitionLightAffinity.LightingEffectAndCondition
                { lightingState = state, condition = shadow });
        var invisible = ConditionDefinitionBuilder.Create(ConditionDefinitions.ConditionInvisible, "ConditionBoonOfTheNightSpiritInvisible2024")
            .SetGuiPresentation("PowerBoonOfTheNightSpirit2024", Category.Feature,
                Sprites.GetConditionSprite("ConditionEpicBoonTheNightSpirit2024"))
            .SetSpecialInterruptions(Array.Empty<ConditionInterruption>()).AddToDB();
        var power = FeatureDefinitionPowerBuilder.Create("PowerBoonOfTheNightSpirit2024")
            .SetGuiPresentation("PowerBoonOfTheNightSpirit2024", Category.Feature,
                Sprites.GetPowerSprite("CapabilityEpicBoonTheNightSpirit2024"))
            .SetUsesFixed(ActivationTime.BonusAction)
            .SetEffectDescription(EffectDescriptionBuilder.Create(Invisibility).ClearEffectAdvancements()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Permanent)
                .SetEffectForms(EffectFormBuilder.ConditionForm(invisible)).UseQuickAnimations().Build())
            .AddCustomSubFeatures(new ValidatorsValidatePowerUse(character =>
                GameLocationCharacter.GetFromActor(character)?.LightingState is
                    LocationDefinitions.LightingState.Dim or LocationDefinitions.LightingState.Unlit or
                    LocationDefinitions.LightingState.Darkness)).AddToDB();
        Build("TheNightSpirit", [lightBuilder.AddToDB(), power, Feature("TheNightSpirit", new NightSpirit(invisible, power))]);
    }

    private sealed class NightSpirit(ConditionDefinition invisible, FeatureDefinitionPower power) : IActionFinishedByMe,
        ICharacterBeforeTurnEndListener
    {
        private readonly ConditionalWeakTable<RulesetCharacter, RulesetCondition> _unfinishedAttacks = new();

        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            if (action.Aborted ||
                action.ActionType is not (ActionType.Main or ActionType.Bonus or ActionType.Reaction))
                yield break;
            var character = action.ActingCharacter.RulesetCharacter;
            if (action.ActionParams.RulesetEffect is RulesetEffectPower effect && effect.PowerDefinition == power)
            {
                _unfinishedAttacks.Remove(character);
                yield break;
            }
            if (!action.CompletesAction())
            {
                if (character.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect, invisible.Name,
                        out var condition))
                {
                    _unfinishedAttacks.Remove(character);
                    _unfinishedAttacks.Add(character, condition);
                }
                yield break;
            }
            _unfinishedAttacks.Remove(character);
            Remove(character, invisible);
        }

        public void OnCharacterBeforeTurnEnded(GameLocationCharacter actor)
        {
            var character = actor.RulesetCharacter;
            if (!_unfinishedAttacks.TryGetValue(character, out var condition)) return;
            _unfinishedAttacks.Remove(character);
            // Ending the turn also finishes an Attack action with unused strikes.
            // A later activation of Night Spirit belongs to a new use and is kept.
            if (character.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect, invisible.Name,
                    out var current) && ReferenceEquals(current, condition))
                character.RemoveCondition(condition);
        }
    }

    private static void BuildEruptingSpellpower()
    {
        // Preserve the serialized identity while removing the old reservation from loaded heroes.
        var ready = ConditionDefinitionBuilder.Create("ConditionBoonOfEruptingSpellpowerReady2024")
            .SetGuiPresentationNoContent(true).SetSilent(Silent.Always).AddToDB();
        var selected = ConditionDefinitionBuilder.Create("ConditionBoonOfEruptingSpellpowerSelected2024")
            .SetGuiPresentationNoContent(true).SetSilent(Silent.Always).AllowMultipleInstances().AddToDB();
        var power = FeatureDefinitionPowerBuilder.Create("PowerBoonOfEruptingSpellpower2024")
            .SetGuiPresentation("PowerBoonOfEruptingSpellpower2024", Category.Feature,
                Sprites.GetPowerSprite("CapabilityEpicBoonEruptingSpellpower2024"), hidden: true)
            .SetUsesFixed(ActivationTime.NoCost, RechargeRate.ShortRest, 1, 1).SetShowCasting(false)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden).AddToDB();
        var behavior = new SpellOverload(ready, selected, power);
        Build("EruptingSpellpower", [power, Feature("EruptingSpellpower", behavior),
            ReactionToggle("EruptingSpellpower", ExtraActionId.BoonOfEruptingSpellpower2024Toggle)], MentalAbilities, true);
    }

    private sealed class SpellOverload(ConditionDefinition legacyReady, ConditionDefinition selectedCondition,
        FeatureDefinitionPower power) :
        IMagicEffectInitiatedByMe, IModifyMagicEffectDamageDice, IOnMagicEffectDamageApplied,
        IInitiativeEndListener, IActionFinishedByMe,
        IOnCharacterPostLoad, IOnLocationCharacterRestored
    {
        private sealed class SelectedSpell(ulong casterGuid) { internal ulong CasterGuid { get; } = casterGuid; }
        private readonly ConditionalWeakTable<RulesetEffectSpell, SelectedSpell> _selected = new();
        public int Priority => 0;
        public void OnCharacterPostLoad(RulesetCharacter character) => Restore(character);
        public void OnLocationCharacterRestored(RulesetCharacter character) => Restore(character);

        private void Restore(RulesetCharacter character)
        {
            Remove(character, legacyReady);
            foreach (var spell in character.EnumerateActiveEffectsActivatedByMe().OfType<RulesetEffectSpell>())
                if (TryGetSelectedCaster(spell, out _)) HasDamage(spell.EffectDescription.EffectForms, []);
        }
        public IEnumerator OnInitiativeEnded(GameLocationCharacter character)
        {
            Remove(character.RulesetCharacter, legacyReady);
            PowerProvider.Get(power, character.RulesetCharacter).Recharge();
            yield break;
        }
        public IEnumerator OnMagicEffectInitiatedByMe(CharacterAction action, RulesetEffect effect,
            GameLocationCharacter attacker, List<GameLocationCharacter> targets)
        {
            Remove(attacker.RulesetCharacter, legacyReady);
            if (effect is not RulesetEffectSpell spell || !CanChoose(action, attacker, spell) ||
                !HasDamage(spell.EffectDescription.EffectForms, [])) yield break;
            yield return attacker.MyReactToDoNothing(ExtraActionId.DoNothingFree, attacker,
                "BoonOfEruptingSpellpower2024", Gui.Format(
                    "Reaction/&CustomReactionBoonOfEruptingSpellpower2024Description", spell.SpellDefinition.FormatTitle()),
                () =>
                {
                    if (!CanChoose(action, attacker, spell)) return;
                    if (spell.EffectDescription.DurationType != DurationType.Instantaneous)
                    {
                        if (!TrackSelection(spell, attacker.RulesetCharacter)) return;
                    }
                    else _selected.Add(spell, new SelectedSpell(attacker.Guid));
                    attacker.RulesetCharacter.UsePower(PowerProvider.Get(power, attacker.RulesetCharacter));
                }, target: attacker, effectDefinition: GetBoon("EruptingSpellpower"),
                reactionStillValid: () => CanChoose(action, attacker, spell));
        }
        private bool CanChoose(CharacterAction action, GameLocationCharacter attacker, RulesetEffectSpell spell) =>
            !action.Aborted && !action.Countered && action is not CharacterActionMagicEffect { ExecutionFailed: true } &&
            ReferenceEquals(action.ActionParams.RulesetEffect, spell) && !TryGetSelectedCaster(spell, out _) &&
            PromptEnabled(attacker.RulesetCharacter, ExtraActionId.BoonOfEruptingSpellpower2024Toggle) &&
            Available(attacker.RulesetCharacter, power) &&
            SpellSlotCastingLimit2024Context.WouldExpendSpellSlot(attacker.RulesetCharacter,
                spell.SpellRepertoire, RulesetEffectSpellWithOrigin.GetOriginSpell(spell), spell);

        private bool TrackSelection(RulesetEffectSpell spell, RulesetCharacter caster)
        {
            var condition = caster.InflictCondition(selectedCondition.Name, DurationType.Permanent, 0,
                TurnOccurenceType.EndOfTurn, AttributeDefinitions.TagEffect, caster.Guid,
                caster.CurrentFaction.Name, spell.EffectLevel, spell.SpellDefinition.Name, 0, 0, 0);
            if (condition == null) return false;
            try
            {
                spell.TrackCondition(caster, caster.Guid, caster, caster.Guid, condition, AttributeDefinitions.TagEffect);
            }
            catch
            {
                caster.RemoveCondition(condition);
                throw;
            }
            return true;
        }

        private bool TryGetSelectedCaster(RulesetEffectSpell spell, out ulong casterGuid)
        {
            casterGuid = 0;
            if (spell == null || spell.Terminated) return false;
            foreach (var guid in spell.TrackedConditionGuids)
            {
                if (!RulesetEntity.TryGetEntity<RulesetCondition>(guid, out var condition) ||
                    condition.ConditionDefinition != selectedCondition || condition.SourceGuid != spell.SourceGuid ||
                    condition.TargetGuid != spell.SourceGuid) continue;
                casterGuid = condition.SourceGuid;
                return true;
            }
            if (spell.EffectDescription.DurationType != DurationType.Instantaneous ||
                !_selected.TryGetValue(spell, out var selected) || selected.CasterGuid != spell.SourceGuid) return false;
            casterGuid = selected.CasterGuid;
            return true;
        }

        private void RemoveSelection(RulesetEffectSpell spell)
        {
            _selected.Remove(spell);
            foreach (var guid in spell.TrackedConditionGuids.ToArray())
                if (RulesetEntity.TryGetEntity<RulesetCondition>(guid, out var condition) &&
                    condition.ConditionDefinition == selectedCondition &&
                    RulesetEntity.TryGetEntity<RulesetCharacter>(condition.TargetGuid, out var target))
                    target.RemoveCondition(condition);
        }

        // Child powers retain their precise parent spell through the shared native origin
        // tracking. Registering this observer never selects or modifies another cast.
        private bool HasDamage(IEnumerable<EffectForm> forms, HashSet<BaseDefinition> visited)
        {
            var damaging = false;
            foreach (var form in forms)
            {
                if (form.FormType == EffectForm.EffectFormType.Damage) damaging = true;
                else if (form.FormType == EffectForm.EffectFormType.Condition &&
                         form.ConditionForm.ConditionDefinition is { } condition && visited.Add(condition))
                {
                    damaging |= HasDamage(condition.RecurrentEffectForms, visited);
                    foreach (var child in condition.Features.OfType<FeatureDefinitionPower>())
                        damaging |= RegisterDamagePower(child, visited);
                }
                else if (form.FormType == EffectForm.EffectFormType.Summon &&
                         !string.IsNullOrEmpty(form.SummonForm.EffectProxyDefinitionName) &&
                         DatabaseRepository.GetDatabase<EffectProxyDefinition>().TryGetElement(
                             form.SummonForm.EffectProxyDefinitionName, out var proxy) && visited.Add(proxy))
                {
                    damaging |= RegisterDamagePower(proxy.AttackPower, visited);
                }
            }
            return damaging;
        }
        private bool RegisterDamagePower(FeatureDefinitionPower child, HashSet<BaseDefinition> visited)
        {
            if (child == null || !visited.Add(child) || !HasDamage(child.EffectDescription.EffectForms, visited)) return false;
            if (!child.GetAllSubFeaturesOfType<SpellOverload>().Contains(this)) child.AddCustomSubFeatures(this);
            return true;
        }
        public void ModifyDamageDice(RulesetCharacter character, DamageForm form,
            RulesetImplementationDefinitions.ApplyFormsParams parameters, List<int> rolled)
        {
            if (!TryGetSelectedCaster(SourceSpell(parameters.activeEffect), out _)) return;
            for (var i = 0; i < rolled.Count; i++)
                if (rolled[i] < 3) rolled[i] = 3;
        }
        public void OnDamageApplied(RulesetCharacter character, DamageForm form,
            RulesetImplementationDefinitions.ApplyFormsParams parameters, int damageReceived)
        {
            if (damageReceived <= 0 || parameters.targetCharacter is not RulesetCharacter { IsDead: false } ||
                !TryGetSelectedCaster(SourceSpell(parameters.activeEffect), out _) ||
                ServiceRepository.GetService<IRulesetImplementationService>() is not RulesetImplementationManager implementation)
                return;
            // Native motion owns the condition, map posture and fall animation.
            // Re-entering ApplyEffectForms here would clear the enclosing damage caches.
            implementation.ApplyMotionForm(EffectFormBuilder.MotionForm(MotionForm.MotionType.FallProne), parameters, null);
        }
        private static RulesetEffectSpell SourceSpell(RulesetEffect effect) => effect switch
        {
            RulesetEffectSpell direct => direct,
            RulesetEffectPower child => Tabletop2024Context.GetSpellDerivedPowerSpell(child),
            _ => null
        };
        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            if (action.ActionParams.RulesetEffect is not RulesetEffectSpell spell) yield break;
            if (action.Aborted || action.Countered || action is CharacterActionMagicEffect { ExecutionFailed: true })
            {
                RemoveSelection(spell);
                yield break;
            }
            // An instant description can leave a native tracked ongoing condition or proxy.
            // Promote only an already selected cast that genuinely remains active; a marker
            // must never be the reason an otherwise completed instant effect stays alive.
            if (!spell.Terminated && _selected.TryGetValue(spell, out var selected) &&
                EffectHelpers.GetCharacterByGuid(selected.CasterGuid) is { } caster &&
                spell.SourceGuid == caster.Guid && caster.EnumerateActiveEffectsActivatedByMe().Contains(spell) &&
                (spell.TrackedConditionGuids.Count > 0 || spell.TrackedEffectProxyGuids.Count > 0 ||
                 spell.TrackedItemPropertyGuids.Count > 0 || spell.TrackedLightSourceGuids.Count > 0 ||
                 spell.TrackedSummonedItemGuids.Count > 0) && TrackSelection(spell, caster))
                _selected.Remove(spell);
            yield break;
        }
    }

    private static readonly List<(SpellListDefinition List, string School, int MaximumLevel)> SchoolSpellLists = [];

    private static bool IsSchoolSpell(SpellDefinition spell, string school, int maximumLevel) =>
        spell is { Implemented: true, GuiPresentation.hidden: false, SpellLevel: >= 1 } &&
        spell.SpellLevel <= maximumLevel && spell.SchoolOfMagic == school &&
        !SpellsContext.SpellsChildMaster.ContainsKey(spell) &&
        ServiceRepository.GetService<IGamingPlatformService>().IsContentPackAvailable(spell.ContentPack);

    private static bool IsOwnClassSpell(RulesetCharacterHero hero, SpellDefinition spell) =>
        hero != null && hero.SpellRepertoires
            .Where(repertoire => repertoire.UsesSharedSpellSlots())
            .Select(repertoire => repertoire.SpellCastingFeature.SpellListDefinition)
            .Where(list => list != null)
            .Any(list => list.SpellsByLevel.Any(level =>
                level.Level == spell.SpellLevel && level.Spells.Contains(spell)));

    private static (bool result, string output) ValidateSchoolSpellChoices(RulesetCharacterHero hero, string school = null)
    {
        var valid = hero != null && SchoolSpellLists.Where(entry => entry.MaximumLevel == 1 &&
                (school == null || entry.School == school))
            .Any(entry => entry.List.SpellsByLevel.SelectMany(level => level.Spells)
                .Any(spell => IsSchoolSpell(spell, entry.School, 1) && IsOwnClassSpell(hero, spell)));
        return (valid, valid ? string.Empty : Gui.Colorize(
            Gui.Localize("Tooltip/&PrerequisiteMagicSchoolMasteryClassSpell2024"), Gui.ColorFailure));
    }

    internal static void RefreshSchoolMasterySpellLists()
    {
        if (SchoolSpellLists.Count == 0)
        {
            return;
        }

        // The database also contains invocation, monster and effect-only copies of
        // player spells. Only active learning lists define selectable spell roots.
        // Share sorted buckets across the ability variants instead of scanning the
        // entire catalog again for every generated list and spell level.
        var spellsBySchoolAndLevel = SpellsContext.GetActiveSpells(7)
            .Where(spell => IsSchoolSpell(spell, spell.SchoolOfMagic, 7))
            .GroupBy(spell => (spell.SchoolOfMagic, spell.SpellLevel))
            .ToDictionary(group => group.Key,
                group => group.OrderBy(spell => spell.Name, StringComparer.Ordinal).ToArray());

        foreach (var (list, school, maximumLevel) in SchoolSpellLists)
        {
            foreach (var level in list.SpellsByLevel)
            {
                level.Spells.Clear();
                if (level.Level is < 1 || level.Level > maximumLevel ||
                    !spellsBySchoolAndLevel.TryGetValue((school, level.Level), out var spells))
                {
                    continue;
                }

                level.Spells.AddRange(spells);
            }
        }
    }

    private static void BuildSchoolMastery()
    {
        string[] schools = ["Abjuration", "Conjuration", "Divination", "Enchantment", "Evocation", "Illusion",
            "Necromancy", "Transmutation"];
        var groups = new List<FeatDefinition>();
        foreach (var school in schools)
        {
            var schoolName = GetDefinition<SchoolOfMagicDefinition>("School" + school).Name;
            var group = Build("MagicSchoolMastery", [], MentalAbilities, true, ability =>
            {
                var tag = "BoonOfMagicSchoolMastery" + school + ability + Suffix;
                var features = new List<FeatureDefinition>();
                foreach (var rote in new[] { true, false })
                {
                    var sourceTag = tag + (rote ? "Rote" : "Arcanum");
                    var maximumLevel = rote ? 1 : 7;
                    // Learning indexes complete level buckets; the empty cantrip bucket grants no cantrips.
                    var list = SpellListDefinitionBuilder.Create("SpellList" + sourceTag)
                        .SetGuiPresentationNoContent(true).ClearSpells().FinalizeSpells(true, maximumLevel).AddToDB();
                    SchoolSpellLists.Add((list, schoolName, maximumLevel));
                    var casting = FeatureDefinitionCastSpellBuilder.Create("CastSpell" + sourceTag)
                        .SetGuiPresentationNoContent(true).SetFocusType(EquipmentDefinitions.FocusType.Universal)
                        .SetSpellCastingOrigin(FeatureDefinitionCastSpell.CastingOrigin.Race)
                        .SetSpellKnowledge(SpellKnowledge.Selection).SetSpellReadyness(SpellReadyness.AllKnown)
                        .SetSlotsRecharge(RechargeRate.LongRest)
                        .SetSlotsPerLevel(FeatHelpers.BuildFixedFreeCastingSlots(
                            Enumerable.Range(1, maximumLevel).ToArray()))
                        .SetKnownSpells(1, FeatureDefinitionCastSpellBuilder.CasterProgression.Flat)
                        .SetReplacedSpells(1, 0).SetUniqueLevelSlots(false).SetCannotUpcast()
                        .SetSpellList(list).SetSpellCastingAbility(ability)
                        .AddCustomSubFeatures(new FeatHelpers.SpellTag(sourceTag, allowSlotCasting: true,
                            unlimitedFreeCasting: rote, dedicatedSelectionList: true,
                            selectionValidator: (hero, spell) => IsSchoolSpell(spell, schoolName, maximumLevel) &&
                                                               list.SpellsByLevel.Any(level =>
                                                                   level.Level == spell.SpellLevel &&
                                                                   level.Spells.Contains(spell)) &&
                                                               (!rote || IsOwnClassSpell(hero, spell)),
                            freeCastingIgnoresComponents: rote,
                            presentation: new FormattedDefinitionText(
                                () => Gui.Format("Screen/&BoonOfMagicSchoolMastery" + (rote ? "Rote" : "Arcanum") +
                                    "2024Title", Gui.Localize("SchoolOfMagic/&" + school + "Title")),
                                () => Gui.Localize("Screen/&BoonOfMagicSchoolMastery" + (rote ? "Rote" : "Arcanum") +
                                    "2024Description")),
                            selectionGroup: tag, selectionOrder: rote ? 0 : 1))
                        .AddToDB();
                    features.Add(casting);
                    features.Add(FeatureDefinitionPointPoolBuilder.Create("PointPool" + sourceTag)
                        .SetGuiPresentationNoContent(true)
                        .SetSpellOrCantripPool(HeroDefinitions.PointsPoolType.Spell, 1, list, sourceTag, 1, maximumLevel)
                        .AddToDB());
                }
                return features.ToArray();
            }, school);
            // School choices belong to the one Epic setting, not eight extra toggles.
            GroupFeats.Groups.Remove(group);
            group.familyTag = "BoonOfMagicSchoolMastery" + school + Suffix;
            group.hasFamilyTag = true;
            foreach (var child in group.GetFirstSubFeatureOfType<GroupedFeat>().GetSubFeats(true))
            {
                child.familyTag = group.FamilyTag;
                child.hasFamilyTag = true;
                ((FeatDefinitionWithPrerequisites)child).Validators.Add((_, hero) =>
                    ValidateSchoolSpellChoices(hero, schoolName));
            }
            ((FeatDefinitionWithPrerequisites)group).Validators.Add((_, hero) =>
                ValidateSchoolSpellChoices(hero, schoolName));
            groups.Add(group);
        }
        var root = GroupFeats.MakeGroupWithPreRequisite("FeatGroupBoonOfMagicSchoolMastery2024", null,
            ValidateLevel, groups.ToArray());
        root.GuiPresentation.title = "Feat/&FeatBoonOfMagicSchoolMastery2024Title";
        root.GuiPresentation.description = "Feat/&FeatBoonOfMagicSchoolMastery2024Description";
        root.GuiPresentation.spriteReference = Sprites.GetPowerSprite("CapabilityEpicBoonMagicSchoolMastery2024");
        root.mustCastSpellsPrerequisite = true;
        ((FeatDefinitionWithPrerequisites)root).Validators.Add((_, hero) => ValidateSchoolSpellChoices(hero));
        // Populate after the shared spell lists and their child-spell metadata load.
        // The existing feat-list refresh also follows spell-list option changes.
        Feats.Add(root);
    }

}
