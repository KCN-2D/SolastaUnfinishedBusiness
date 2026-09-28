using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Patches;
using SolastaUnfinishedBusiness.Properties;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly List<(CharacterSubclassDefinition Subclass, FeatureUnlockByLevel Unlock)>
        LegacyDivineInterventionUnlocks = [];
    private static readonly ConditionalWeakTable<RulesetEffectSpell, object> PaidDivineInterventions = new();
    private static FeatureDefinitionPower _divineIntervention;
    private static FeatureDefinition _greaterDivineIntervention;
    private static SpellDefinition _divineInterventionSelection;
    private static ConditionDefinition _divineInterventionRecovery;

    private static void LoadClericDivineIntervention()
    {
        var behavior = new DivineInterventionBehavior();
        var description = new FormattedDefinitionText(characterDescription: character =>
            GetDivineInterventionWish(character) != null
                ? Gui.Localize("Feature/&PowerClericGreaterDivineIntervention2024Description")
                : null);

        _divineIntervention = FeatureDefinitionPowerBuilder.Create("PowerClericDivineIntervention2024")
            .SetGuiPresentation("Feature/&PowerClericDivineInterventionTitle",
                "Feature/&PowerClericDivineIntervention2024Description",
                PowerClericDivineInterventionCleric.GuiPresentation.SpriteReference)
            .SetUsesFixed(ActivationTime.Action, RechargeRate.LongRest)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self).Build())
            .AddCustomSubFeatures(behavior, description)
            .AddToDB();
        _greaterDivineIntervention = FeatureDefinitionBuilder.Create("FeatureClericGreaterDivineIntervention2024")
            .SetGuiPresentation(Category.Feature)
            .AddToDB();
        _divineInterventionRecovery = ConditionDefinitionBuilder.Create("ConditionClericDivineInterventionRecovery")
            .SetGuiPresentation(Category.Condition,
                Sprites.GetSprite("DivineInterventionRecovery", Resources.ConditionTimeStop, 27, 32))
            .SetConditionType(ConditionType.Neutral)
            .SetPossessive()
            .SetAmountOrigin(ConditionDefinition.OriginOfAmount.SourceGain)
            .AddCustomSubFeatures(new FormattedDefinitionText(conditionDescription: condition => Gui.Format(
                "Condition/&ConditionClericDivineInterventionRecoveryRemainingDescription",
                condition.Amount.ToString())))
            .AddToDB();
        _divineInterventionSelection = SpellDefinitionBuilder.Create("DivineInterventionSelection2024")
            .SetGuiPresentation(_divineIntervention.GuiPresentation)
            .SetSpellLevel(5)
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolEvocation)
            .SetCastingTime(ActivationTime.Action)
            .SetEffectDescription(_divineIntervention.EffectDescription)
            .AddCustomSubFeatures(behavior, description)
            .AddToDB();

        foreach (var domain in ClericDomains)
        {
            LegacyDivineInterventionUnlocks.AddRange(domain.FeatureUnlocks
                .Where(unlock => IsLegacyDivineIntervention(unlock.FeatureDefinition))
                .Select(unlock => (domain, unlock)));
        }
    }

    private static bool IsLegacyDivineIntervention(FeatureDefinition feature)
    {
        var visited = new HashSet<FeatureDefinitionPower>();

        for (var power = feature as FeatureDefinitionPower; power != null && visited.Add(power);
             power = power.OverriddenPower)
        {
            if (power == PowerClericDivineInterventionCleric || power == PowerClericDivineInterventionPaladin ||
                power == PowerClericDivineInterventionWizard)
            {
                return true;
            }
        }

        return false;
    }

    internal static void SwitchClericDivineIntervention()
    {
        if (_divineIntervention == null)
        {
            return;
        }

        Cleric.FeatureUnlocks.RemoveAll(unlock => unlock.FeatureDefinition == _divineIntervention ||
                                                  unlock.FeatureDefinition == _greaterDivineIntervention);

        if (Main.Settings.EnableClericDivineIntervention2024)
        {
            Cleric.FeatureUnlocks.Add(new FeatureUnlockByLevel(_divineIntervention, 10));
            Cleric.FeatureUnlocks.Add(new FeatureUnlockByLevel(_greaterDivineIntervention, 20));
        }

        Cleric.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);

        foreach (var (subclass, unlock) in LegacyDivineInterventionUnlocks)
        {
            subclass.FeatureUnlocks.Remove(unlock);

            if (!Main.Settings.EnableClericDivineIntervention2024)
            {
                subclass.FeatureUnlocks.Add(unlock);
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
                UpdateClericDivineIntervention(hero);
            }
        }
    }

    internal static void UpdateClericDivineIntervention(RulesetCharacterHero hero)
    {
        if (_divineIntervention == null || !hero.ClassesAndSubclasses.TryGetValue(Cleric, out var subclass))
        {
            return;
        }

        var level = hero.GetClassLevel(Cleric);
        var enabled = Main.Settings.EnableClericDivineIntervention2024;
        var previousUses = hero.UsablePowers
            .Where(power => power.PowerDefinition == _divineIntervention ||
                            IsLegacyDivineIntervention(power.PowerDefinition))
            .Select(power => power.RemainingUses).DefaultIfEmpty(1).Min();

        UpdateClassFeature(_divineIntervention, 10);
        UpdateClassFeature(_greaterDivineIntervention, 20);

        foreach (var (domain, unlock) in LegacyDivineInterventionUnlocks.Where(entry => entry.Subclass == subclass))
        {
            var tag = AttributeDefinitions.GetSubclassTag(Cleric, unlock.Level, domain);

            if (!hero.ActiveFeatures.TryGetValue(tag, out var features))
            {
                if (enabled || level < unlock.Level)
                {
                    continue;
                }

                features = [];
                hero.ActiveFeatures.Add(tag, features);
            }

            features.Remove(unlock.FeatureDefinition);

            if (!enabled && level >= unlock.Level)
            {
                features.Add(unlock.FeatureDefinition);
            }
        }

        hero.UsablePowers.RemoveAll(power => enabled
            ? IsLegacyDivineIntervention(power.PowerDefinition)
            : power.PowerDefinition == _divineIntervention);
        var granted = enabled
            ? level >= 10 ? new[] { _divineIntervention } : Array.Empty<FeatureDefinitionPower>()
            : LegacyDivineInterventionUnlocks.Where(entry => entry.Subclass == subclass && entry.Unlock.Level <= level)
                .Select(entry => (FeatureDefinitionPower)entry.Unlock.FeatureDefinition).ToArray();

        foreach (var power in granted.Where(power => hero.GetPowerFromDefinition(power) == null))
        {
            var usable = new RulesetUsablePower(power, null, Cleric);
            PowerProvider.BindUsesAttribute(hero, usable);
            usable.Recharge();
            PowerProvider.UpdateSaveDc(hero, usable);
            hero.UsablePowers.Add(usable);
        }

        // Toggling the rule must not refund an already spent daily use.
        foreach (var usable in hero.UsablePowers.Where(power => power.PowerDefinition == _divineIntervention ||
                                                              IsLegacyDivineIntervention(power.PowerDefinition)))
        {
            usable.remainingUses = Math.Min(usable.RemainingUses, previousUses);
        }

        return;

        void UpdateClassFeature(FeatureDefinition feature, int featureLevel)
        {
            var tag = AttributeDefinitions.GetClassTag(Cleric, featureLevel);

            if (!hero.ActiveFeatures.TryGetValue(tag, out var features))
            {
                if (!enabled || level < featureLevel)
                {
                    return;
                }

                features = [];
                hero.ActiveFeatures.Add(tag, features);
            }

            features.Remove(feature);

            if (enabled && level >= featureLevel)
            {
                features.Add(feature);
            }
        }
    }

    internal static bool IsDivineInterventionSpell(RulesetCharacter caster, SpellDefinition spell,
        RulesetEffectSpell activeSpell = null)
    {
        return _divineInterventionSelection != null &&
               (activeSpell is RulesetEffectSpellWithOrigin origin &&
                origin.OriginatingSpell == _divineInterventionSelection ||
                RulesetEffectSpellWithOrigin.TryGetPendingOrigin(caster, spell, out var originatingSpell, out _) &&
                originatingSpell == _divineInterventionSelection);
    }

    internal static bool TrySpendDivineIntervention(RulesetCharacter caster, RulesetEffectSpell spell)
    {
        if (!IsDivineInterventionSpell(caster, spell.SpellDefinition, spell))
        {
            return false;
        }

        if (PaidDivineInterventions.TryGetValue(spell, out _))
        {
            return true;
        }

        PaidDivineInterventions.Add(spell, new object());
        var power = caster.GetPowerFromDefinition(_divineIntervention);

        if (power != null)
        {
            caster.UsePower(power);
        }

        if (spell is RulesetEffectSpellWithOrigin { Mode: not RulesetEffectSpellWithOrigin.OriginMode.DivineIntervention })
        {
            var firstRoll = RollDie(DieType.D4, AdvantageType.None, out _, out _);
            var secondRoll = RollDie(DieType.D4, AdvantageType.None, out _, out _);
            var rests = firstRoll + secondRoll;
            caster.InflictCondition(_divineInterventionRecovery.Name, DurationType.Permanent, 0,
                TurnOccurenceType.EndOfTurn, AttributeDefinitions.TagEffect, caster.Guid,
                caster.CurrentFaction.Name, 9, _divineIntervention.Name, rests, 0, 0);
            caster.LogCharacterUsedPower(_divineIntervention, "Feedback/&DivineInterventionRecoveryRolled",
                extra:
                [
                    (ConsoleStyleDuplet.ParameterType.AbilityInfo, firstRoll.ToString()),
                    (ConsoleStyleDuplet.ParameterType.AbilityInfo, secondRoll.ToString()),
                    (ConsoleStyleDuplet.ParameterType.AbilityInfo, rests.ToString())
                ]);
        }

        return true;
    }

    internal static void RecoverClericDivineIntervention(RulesetCharacter character, RestType restType, bool simulate)
    {
        if (simulate || restType != RestType.LongRest || _divineInterventionRecovery == null ||
            character.GetSubFeaturesByType<IPreventRestRecovery>()
                .Any(prevention => prevention.PreventRestRecovery(character, restType)))
        {
            return;
        }

        foreach (var condition in character.ConditionsByCategory.Values.SelectMany(conditions => conditions)
                     .Where(condition => condition.ConditionDefinition == _divineInterventionRecovery).ToArray())
        {
            if (--condition.Amount <= 0)
            {
                character.RemoveCondition(condition);
            }
            else if (character.GetPowerFromDefinition(_divineIntervention) is { } power)
            {
                power.remainingUses = 0;
            }
        }
    }

    internal static bool TrySelectDivineIntervention(CharacterActionPanel panel, RulesetUsablePower usablePower)
    {
        if (usablePower.PowerDefinition != _divineIntervention)
        {
            return false;
        }

        var caster = panel.GuiCharacter.RulesetCharacter;
        var repertoire = caster.GetClassSpellRepertoire(Cleric);

        if (repertoire == null || !CanUseDivineIntervention(caster))
        {
            return true;
        }

        var modal = Gui.GuiService.GetScreen<SubspellSelectionModal>();
        panel.PowerSelectionPanel.Hide(true);
        modal.Bind(_divineInterventionSelection, caster, repertoire, (selectedRepertoire, spell, slotLevel) =>
        {
            panel.actionId = ActionDefinitions.Id.CastMain;
            panel.actionParams = new CharacterActionParams(panel.GuiCharacter.GameLocationCharacter,
                ActionDefinitions.Id.CastMain);
            panel.SpellcastEngaged(selectedRepertoire, spell, slotLevel);
        }, 5, panel.RectTransform);
        modal.Show();

        return true;
    }

    private static SpellDefinition GetDivineInterventionWish(RulesetCharacter caster)
    {
        // The menu and its description use the same effective upgrade, including multiclass characters.
        return Main.Settings.EnableClericDivineIntervention2024 && caster.GetClassLevel(Cleric) >= 20 &&
               TryGetDefinition<SpellDefinition>("Wish", out var wish) &&
               wish.HasSubFeatureOfType<ICustomSubspellSelectionProvider>()
            ? wish
            : null;
    }

    private static bool CanUseDivineIntervention(RulesetCharacter caster)
    {
        return Main.Settings.EnableClericDivineIntervention2024 && caster.GetClassLevel(Cleric) >= 10 &&
               caster.GetPowerFromDefinition(_divineIntervention) is { RemainingUses: > 0 } &&
               !caster.HasConditionOfType(_divineInterventionRecovery);
    }

    private sealed class DivineInterventionBehavior : ICustomSubspellSelectionProvider, IValidatePowerUse,
        IValidateSpellCasting
    {
        public bool BypassComponentsAndCastingTime => false;

        public bool BypassMaterialComponent => true;

        public bool CanUsePower(RulesetCharacter character, FeatureDefinitionPower power) =>
            CanUseDivineIntervention(character);

        public bool CanCastSpell(SpellCastingValidationContext context, out string failure)
        {
            failure = string.Empty;

            if (!IsDivineInterventionSpell(context.Caster, context.SpellDefinition, context.ActiveSpell))
            {
                return true;
            }

            if (context.ActiveSpell != null && PaidDivineInterventions.TryGetValue(context.ActiveSpell, out _))
            {
                return true;
            }

            if (!CanUseDivineIntervention(context.Caster))
            {
                failure = "Failure/&DivineInterventionUnavailable";
                return false;
            }

            // Only material components are waived for the normal intervention. Wish has its own rules.
            var mode = (context.ActiveSpell as RulesetEffectSpellWithOrigin)?.Mode;

            if (mode == null)
            {
                RulesetEffectSpellWithOrigin.TryGetPendingOrigin(context.Caster, context.SpellDefinition, out _,
                    out var pendingMode);
                mode = pendingMode;
            }

            return mode != RulesetEffectSpellWithOrigin.OriginMode.DivineIntervention ||
                   context.Caster.IsComponentVerbalValid(context.SpellDefinition, out failure) &&
                   context.Caster.IsComponentSomaticValid(context.SpellDefinition, out failure);
        }

        public ICustomSubspellSelectionSession CreateSession(SpellDefinition masterSpell, RulesetCharacter caster,
            RulesetSpellRepertoire repertoire, int slotLevel) => new DivineInterventionSelection(caster, repertoire);
    }

    private sealed class DivineInterventionSelection : ICustomSubspellSelectionSession,
        ICustomSubspellSelectionAvailability
    {
        private readonly RulesetCharacter _caster;
        private readonly RulesetSpellRepertoire _repertoire;
        private readonly Stack<List<SpellDefinition>> _pages = [];
        private readonly SpellDefinition _back = GetDefinition<SpellDefinition>("WishBack");
        private readonly SpellDefinition _wish;
        private List<SpellDefinition> _choices;
        private ICustomSubspellSelectionSession _wishSession;

        internal DivineInterventionSelection(RulesetCharacter caster, RulesetSpellRepertoire repertoire)
        {
            _caster = caster;
            _repertoire = repertoire;
            _choices = SpellListDefinitions.SpellListCleric.SpellsByLevel.Where(level => level.Level <= 5)
                .SelectMany(level => level.Spells).Where(IsEligible).Distinct()
                .OrderBy(spell => spell.SpellLevel).ThenBy(spell => spell.Name, StringComparer.Ordinal).ToList();

            _wish = GetDivineInterventionWish(caster);

            if (_wish != null)
            {
                _choices.Add(_wish);
            }
        }

        public bool BypassComponentsAndCastingTime => _wishSession != null;

        public List<SpellDefinition> GetSubspells()
        {
            if (_wishSession == null)
            {
                return _choices;
            }

            var choices = _wishSession.GetSubspells();
            return choices.FirstOrDefault() == _back ? choices : [_back, .. choices];
        }

        public bool IsAvailable(SpellDefinition spell, out string failure)
        {
            failure = string.Empty;

            if (spell == _back || _wishSession != null)
            {
                return true;
            }

            if (!CanUseDivineIntervention(_caster))
            {
                failure = "Failure/&DivineInterventionUnavailable";
                return false;
            }

            return SpellCastingValidation.IsValid(_caster, _repertoire, spell, null, out failure,
                       bypassMaterialComponent: true, bypassSpellSlotLimit: true) &&
                   _caster.IsComponentVerbalValid(spell, out failure) &&
                   _caster.IsComponentSomaticValid(spell, out failure);
        }

        public bool OnActivate(SubspellSelectionModal modal, int index)
        {
            if (!CanUseDivineIntervention(_caster))
            {
                return false;
            }

            if (_wishSession != null)
            {
                if (_wishSession.GetSubspells().FirstOrDefault() == _back)
                {
                    return _wishSession.OnActivate(modal, index);
                }

                if (index == 0)
                {
                    _wishSession = null;
                    SubspellSelectionModalPatcher.Refresh(modal);
                    return false;
                }

                return _wishSession.OnActivate(modal, index - 1);
            }

            if (index < 0 || index >= _choices.Count)
            {
                return false;
            }

            var spell = _choices[index];

            if (!IsAvailable(spell, out _))
            {
                return false;
            }

            if (spell == _back && _pages.Count > 0)
            {
                _choices = _pages.Pop();
            }
            else if (spell == _wish)
            {
                _wishSession = _wish.GetFirstSubFeatureOfType<ICustomSubspellSelectionProvider>()
                    ?.CreateSession(_divineInterventionSelection, _caster, _repertoire, 9);
            }
            else if (spell.SubspellsList.Count > 0)
            {
                _pages.Push(_choices);
                _choices = [_back, .. spell.SubspellsList.Where(IsEligible)];
            }
            else
            {
                using (RulesetEffectSpellWithOrigin.UseOrigin(_caster, _repertoire, spell, spell.SpellLevel,
                           _divineInterventionSelection, spell.SpellLevel, false,
                           RulesetEffectSpellWithOrigin.OriginMode.DivineIntervention))
                {
                    if (!SpellCastingValidation.IsValid(_caster, _repertoire, spell, null, out _, bypassMaterialComponent: true,
                            bypassSpellSlotLimit: true))
                    {
                        return false;
                    }

                    modal.spellCastEngaged?.Invoke(_repertoire, spell, spell.SpellLevel);
                }

                modal.Hide();
                return false;
            }

            SubspellSelectionModalPatcher.Refresh(modal);
            return false;
        }

        private static bool IsEligible(SpellDefinition spell)
        {
            var platform = ServiceRepository.GetService<IGamingPlatformService>();
            return spell is { Implemented: true, SpellLevel: >= 0 and <= 5 } &&
                   spell.ActivationTime is ActivationTime.Action or ActivationTime.BonusAction or
                       ActivationTime.Minute1 or ActivationTime.Minute10 or ActivationTime.Hours1 or ActivationTime.Hours24 &&
                   (spell.ContentPack == CeContentPackContext.CeContentPack || platform == null ||
                    platform.IsContentPackAvailable(spell.ContentPack));
        }
    }
}
