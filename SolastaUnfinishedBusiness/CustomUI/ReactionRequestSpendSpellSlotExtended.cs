using System;
using System.Linq;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Subclasses.Builders;

namespace SolastaUnfinishedBusiness.CustomUI;

internal sealed class ReactionRequestSpendSpellSlotExtended : ReactionRequest
{
    private readonly GuiCharacter _guiCharacter;

    internal ReactionRequestSpendSpellSlotExtended(CharacterActionParams actionParams)
        : base("SpendSpellSlot", actionParams)
    {
        SubOptionsAvailability.Clear();

        var rulesetCharacter = actionParams.ActingCharacter.RulesetCharacter;
        var hero = rulesetCharacter.GetOriginalHero();
        var spellRepertoire = ReactionParams.SpellRepertoire;
        var spell = (ReactionParams.RulesetEffect as RulesetEffectSpell)?.SpellDefinition;
        int selected;

        if (rulesetCharacter is RulesetCharacterSimulacrum smiteDuplicate &&
            actionParams.StringParameter == InvocationsBuilders.EldritchSmiteTag)
        {
            selected = AddSimulacrumPactSlotOption(
                smiteDuplicate,
                spellRepertoire);
        }
        else if (rulesetCharacter is RulesetCharacterSimulacrum)
        {
            var minimumLevel = Math.Max(1, actionParams.IntParameter);
            var maximumLevel = spellRepertoire.spellsSlotCapacities.Keys
                .Concat(spellRepertoire.usedSpellsSlots.Keys)
                .Where(level => level >= minimumLevel)
                .DefaultIfEmpty(minimumLevel)
                .Max();

            selected = MulticlassGameUi.AddAvailableSubLevels(
                SubOptionsAvailability,
                null,
                spellRepertoire,
                minimumLevel,
                maximumLevel,
                spell);
        }
        else if (actionParams.StringParameter == InvocationsBuilders.EldritchSmiteTag)
        {
            var minLevel = SharedSpellsContext.GetWarlockSpellLevel(hero);

            selected = MulticlassGameUi.AddAvailableSubLevels(SubOptionsAvailability, hero, spellRepertoire,
                minLevel, minLevel);
        }
        else
        {
            selected = MulticlassGameUi.AddAvailableSubLevels(SubOptionsAvailability, hero, spellRepertoire,
                actionParams.IntParameter);
        }

        if (selected >= 0)
        {
            SelectSubOption(selected);
        }

        _guiCharacter = new GuiCharacter(Character);
    }

    public override int SelectedSubOption =>
        Array.IndexOf([.. SubOptionsAvailability.Keys], ReactionParams.IntParameter);

    public override string SuboptionTag => ReactionParams.StringParameter;

    public override string FormatDescription()
    {
        return Gui.Format(
            string.Format(
                DatabaseHelper.GetDefinition<ReactionDefinition>(DefinitionName).GuiPresentation
                    .Description, ReactionParams.StringParameter), _guiCharacter.Name);
    }

    public override string FormatReactDescription()
    {
        return Gui.Format(
            string.Format(
                DatabaseHelper.GetDefinition<ReactionDefinition>(DefinitionName).ReactDescription,
                ReactionParams.StringParameter), _guiCharacter.Name);
    }

    public override string FormatReactTitle()
    {
        return Gui.Format(
            string.Format(
                DatabaseHelper.GetDefinition<ReactionDefinition>(DefinitionName).ReactTitle,
                ReactionParams.StringParameter), _guiCharacter.Name);
    }

    public override string FormatTitle()
    {
        return Gui.Localize(string.Format(
            DatabaseHelper.GetDefinition<ReactionDefinition>(DefinitionName).GuiPresentation.Title,
            ReactionParams.StringParameter));
    }

    public override void SelectSubOption(int option)
    {
        ReactionParams.IntParameter = SubOptionsAvailability.Keys.ToArray()[option];
    }

    private int AddSimulacrumPactSlotOption(
        RulesetCharacterSimulacrum duplicate,
        RulesetSpellRepertoire repertoire)
    {
        var warlock = DatabaseHelper.CharacterClassDefinitions.Warlock;
        var warlockLevel = duplicate.GetClassLevel(warlock);

        if (warlockLevel <= 0 || repertoire?.SpellCastingClass != warlock)
        {
            return -1;
        }

        var pactSpellLevel = Math.Min(5, (warlockLevel + 1) / 2);
        var pactMax = warlockLevel switch
        {
            1 => 1,
            <= 10 => 2,
            <= 16 => 3,
            _ => 4
        };

        pactMax += duplicate
            .FeaturesByType<FeatureDefinitionMagicAffinity>()
            .Where(x => x == DatabaseHelper.FeatureDefinitionMagicAffinitys
                .MagicAffinityChitinousBoonAdditionalSpellSlot)
            .SelectMany(x => x.AdditionalSlots)
            .Sum(x => x.SlotsNumber);

        var hasSharedClassSlots = duplicate.SpellRepertoires.Count(x =>
            x.SpellCastingFeature?.SpellCastingOrigin !=
            FeatureDefinitionCastSpell.CastingOrigin.Race) > 1;
        var usedSlotKey = hasSharedClassSlots
            ? SharedSpellsContext.PactMagicSlotsTab
            : pactSpellLevel;

        repertoire.usedSpellsSlots.TryGetValue(usedSlotKey, out var used);

        SubOptionsAvailability.Add(pactSpellLevel, used < pactMax);

        return used < pactMax ? 0 : -1;
    }
}
