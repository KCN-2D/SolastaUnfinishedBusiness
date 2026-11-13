using System;
using System.Collections.Generic;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal sealed class ReactionRequestSelectDamageDice : ReactionRequest, IReactionRequestMultiSelection
{
    internal const string Name = "ReactionSelectDamageDice";
    private readonly Func<bool> _stillValid;
    private readonly int _cost;
    private readonly SpellDefinition _spell;

    internal ReactionRequestSelectDamageDice(CharacterActionParams parameters, SpellDefinition spell,
        IReadOnlyList<(DieType Type, int Value, string DamageType)> dice, int maximum, int cost, Func<bool> stillValid)
        : base(Name, parameters)
    {
        Dice = dice;
        _spell = spell;
        _stillValid = stillValid;
        _cost = cost;
        Selection = new ReactionSelection(dice.Count, maximum, 1, index => index < dice.Count && stillValid());
        parameters.SpellRepertoire = new RulesetSpellRepertoire();
        for (var index = 0; index < dice.Count; index++)
        {
            SubOptionsAvailability.Add(index, true);
        }
    }

    internal IReadOnlyList<(DieType Type, int Value, string DamageType)> Dice { get; }
    public ReactionSelection Selection { get; }
    public bool CanContinueSelection => _stillValid();
    public override int SelectedSubOption => -1;
    public override string SuboptionTag => "DamageDice";
    public override bool IsStillValid => _stillValid() && Selection.IsValid;

    public override void SelectSubOption(int option) => Selection.ApplyCommand(option);
    public override string FormatTitle() => Gui.Localize("Rules/&MetamagicOptionEmpoweredSpellTitle");
    public override string FormatDescription() => Gui.Format("Reaction/&CustomReactionEmpoweredSpellDescription",
        Selection.Maximum.ToString(), _spell.FormatTitle(), _cost.ToString());
    public override string FormatReactTitle() => Gui.Localize("Reaction/&CustomReactionEmpoweredSpellReactTitle");
    public override string FormatReactDescription() => Gui.Localize("Reaction/&CustomReactionEmpoweredSpellReactDescription");

    internal string FormatDie(int option)
    {
        var die = Dice[option];
        return Gui.Format("Reaction/&EmpoweredSpellDieFormat", (option + 1).ToString(),
            DiceMaxValue[(int)die.Type].ToString(), die.Value.ToString(), Gui.Localize($"Rules/&{die.DamageType}Title"));
    }
}
