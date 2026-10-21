using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.ModKit.Utility;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class CustomReactionsContext
{
    private static bool _forcePreferredCantrip; //used by actual feature
    private static bool _forcePreferredCantripUI; //used for local UI state

    internal static string FormatReactionDescription(ReactionRequest request, int selectedSubOption = -1)
    {
        if (request == null)
        {
            return string.Empty;
        }

        var sections = new List<string>();
        var selected = selectedSubOption >= 0 ? selectedSubOption : request.SelectedSubOption;
        var effectDefinition = GetReactionEffectDefinition(request, selected);
        var effectContext = ReactionRequestCallback.GetEffectContext(request.ReactionParams);
        var effects = GetReactionEffectTargets(request, selected).ToArray();
        var targets = effects.SelectMany(effect => effect.Targets)
            .Where(target => target?.RulesetCharacter != null)
            .GroupBy(target => target.Guid)
            .Select(group => ReactionCharacterNameFormatter.Format(group.First()))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        // Targetless resource conversions must not be presented as targeting the caster.
        // Target selection and ordinary spell reactions use the current action targets.
        if (targets.Length > 0)
        {
            sections.Add(Gui.Format("Reaction/&CustomReactionTargetContextFormat", string.Join(", ", targets)));
        }

        if (request is IReactionRequestMultiSelection { Selection: { Budget: { } budget } selection })
        {
            sections.Add(Gui.Format("Reaction/&SubitemSelectionCostFormat",
                selection.Cost.ToString(), budget.ToString()));
        }
        if (request is IReactionRequestMultiSelection { Selection: { Count: > 0, IsGroupValid: false } })
        {
            sections.Add(Gui.Localize("Reaction/&PartyOnlyHarmfulInterruptsSuppressedDescription"));
        }

        var adverseTargets = effects.Where(effect => effectContext?.Beneficial == false ||
                effectContext?.Beneficial != true && (
                request is ReactionRequestSelectTarget { IsOffensive: true } ||
                TargetingContext.IsHarmfulInterruptEffect(effect.Definition,
                    GetReactionEffectDescription(request, effect.Definition))))
            .SelectMany(effect => effect.Targets).Where(target => target?.RulesetCharacter != null)
            .GroupBy(target => target.Guid).Select(group => group.First()).ToArray();
        if (adverseTargets.Any(target =>
                TargetingContext.IsPartyAllyTarget(request.Character, target, true)))
        {
            sections.Add(TargetingContext.FormatPartyTargetWarning(request.Character, adverseTargets));
        }
        var supportedEnemies = effects.Where(effect => effectContext?.Beneficial == true ||
                effectContext?.Beneficial != false && (request is ReactionRequestSelectTarget { IsBeneficial: true } ||
                TargetingContext.IsBeneficialEffect(effect.Definition,
                    GetReactionEffectDescription(request, effect.Definition))))
            .SelectMany(effect => effect.Targets).Where(target =>
                target != null && request.Character.IsOppositeSide(target.Side)).ToArray();
        if (supportedEnemies.Length > 0)
        {
            sections.Add(TargetingContext.FormatPartyTargetWarning(request.Character, supportedEnemies, hostileAid: true));
        }

        // Action names already shown in the heading or selection list need no second label.
        if (request is not (ReactionRequestWarcaster or ReactionRequestSpendBundlePower))
        {
            var effectTitle = CustomTooltipProvider.FormatTitle(effectDefinition);
            var reactionTitle = Gui.Localize(request.FormatTitle());

            if (!CustomTooltipProvider.IsUnavailableContent(effectTitle) &&
                !string.Equals(effectTitle.StripHTML().Trim(), (reactionTitle ?? string.Empty).StripHTML().Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                sections.Add(Gui.Format("Reaction/&CustomReactionEffectContextFormat", effectTitle));
            }
        }

        return string.Join("\n", sections);
    }

    private static IEnumerable<(BaseDefinition Definition, IEnumerable<GameLocationCharacter> Targets)>
        GetReactionEffectTargets(ReactionRequest request, int selected)
    {
        var definition = GetReactionEffectDefinition(request, selected);
        if (ReactionRequestCallback.GetEffectContext(request.ReactionParams) is { } context)
        {
            yield return (context.Definition, new[] { context.Target });
            yield break;
        }
        switch (request)
        {
            case ReactionRequestSpendBundlePower bundle:
                IEnumerable<FeatureDefinitionPower> powers = bundle.Selection != null ? bundle.SelectedPowers :
                    definition is FeatureDefinitionPower power ? [power] : [];
                foreach (var selectedPower in powers)
                {
                    var effect = bundle.GetEffectContext(selectedPower);
                    yield return (effect.Definition, new[] { effect.Target });
                }
                break;
            case ReactionRequestCustom { Target: not null } custom:
                yield return (definition, new[] { custom.Target });
                break;
            case ReactionRequestSelectTarget target:
                yield return (definition, target.Selection != null ? target.SelectedTargets :
                    selected >= 0 && selected < target.Candidates.Count ? new[] { target.Candidates[selected] } : []);
                break;
            default:
                yield return (definition, request.ReactionParams.TargetCharacters);
                break;
        }
    }

    private static EffectDescription GetReactionEffectDescription(ReactionRequest request, BaseDefinition definition)
    {
        var effect = request.ReactionParams.RulesetEffect;
        var source = effect is RulesetEffectSpell spell ? RulesetEffectSpellWithOrigin.GetOriginSpell(spell) :
            effect?.SourceDefinition;
        // A hovered alternative must not inherit the current selection's dynamic
        // description. The matching live effect owns its modified forms and targets.
        return definition != null && effect != null && source == definition
            ? effect.EffectDescription
            : (definition as IMagicEffect)?.EffectDescription;
    }

    internal static string FormatReactionTooltipDescription(ReactionRequest request, int selectedSubOption = -1)
    {
        if (request == null)
        {
            return string.Empty;
        }

        var selected = selectedSubOption >= 0 ? selectedSubOption : request.SelectedSubOption;
        var description = CustomTooltipProvider.FormatDescription(GetReactionEffectDefinition(request, selected));
        if (CustomTooltipProvider.IsUnavailableContent(description))
        {
            description = request.FormatDescription();
        }

        // Trigger-specific prose belongs beside the effect's details, outside the interruption body.
        var trigger = request switch
        {
            ReactionRequestSpendSpellSlotExtended => request.ReactionParams.StringParameter2,
            ReactionRequestCustom => request.FormatDescription(),
            _ => null
        };
        return !string.IsNullOrWhiteSpace(trigger) && trigger != description
            ? trigger + "\n\n" + description
            : description;
    }

    private static BaseDefinition GetReactionEffectDefinition(ReactionRequest request, int selected)
    {
        if (ReactionRequestCallback.GetEffectContext(request.ReactionParams)?.Definition is { } contextDefinition)
        {
            return contextDefinition;
        }
        BaseDefinition definition = request.ReactionParams.RulesetEffect switch
        {
            RulesetEffectSpell effect => RulesetEffectSpellWithOrigin.GetOriginSpell(effect),
            RulesetEffectPower effect => effect.PowerDefinition,
            _ => request.ReactionParams.UsablePower?.PowerDefinition
        };

        switch (request)
        {
            case ReactionRequestCustom { EffectDefinition: not null } custom:
                definition = custom.EffectDefinition;
                break;
            case ReactionRequestSelectTarget target:
                definition = target.EffectDefinition;
                break;
            case ReactionRequestWarcaster:
                definition = selected > 0
                    ? request.ReactionParams.SpellRepertoire?.KnownSpells.ElementAtOrDefault(selected - 1)
                    : null;
                break;
            case ReactionRequestSpendBundlePower:
                var spell = request.ReactionParams.SpellRepertoire?.KnownSpells.ElementAtOrDefault(selected);
                definition = spell == null ? null : PowerBundle.GetPower(spell);
                break;
        }

        return definition;
    }

    internal static void Load()
    {
        MakeReactDefinition(ReactionRequestWarcaster.Name);
        MakeReactDefinition(ReactionRequestSpendBundlePower.Name);
        MakeReactDefinition(ReactionRequestSelectTarget.Name);
        MakeReactDefinition(ReactionRequestSelectSmiteSpell.Name);
        MakeReactDefinition(ReactionRequestSelectSmiteSlot.Name);
    }

    private static void MakeReactDefinition(string name)
    {
        ReactionDefinitionBuilder
            .Create(name)
            .SetGuiPresentation(Category.Reaction)
            .AddToDB();
    }

    internal static void SaveReadyActionPreferredCantrip(
        [CanBeNull] CharacterActionParams actionParams,
        ReadyActionType readyActionType)
    {
        if (actionParams != null && readyActionType == ReadyActionType.Cantrip)
        {
            actionParams.BoolParameter4 = _forcePreferredCantripUI;
        }
    }

    internal static void ReadReadyActionPreferredCantrip(CharacterActionParams actionParams)
    {
        if (actionParams is { ReadyActionType: ReadyActionType.Cantrip })
        {
            _forcePreferredCantrip = actionParams.BoolParameter4;
        }
    }

    internal static void SetupForcePreferredToggle(RectTransform parent)
    {
        PersonalityFlagToggle toggle;

        if (parent.childCount < 3)
        {
            // ReSharper disable once Unity.UnknownResource
            var prefab = Resources.Load<GameObject>("Gui/Prefabs/CharacterEdition/PersonalityFlagToggle");
            var asset = UnityEngine.Object.Instantiate(prefab, parent, false);

            asset.name = "ForcePreferredToggle";

            var transform = asset.GetComponent<RectTransform>();
            transform.SetParent(parent, false);
            transform.localScale = new Vector3(1f, 1f, 1f);
            transform.anchoredPosition = new Vector2(0f, 1);
            transform.localPosition = new Vector3(0, -30);
            transform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 200);
            transform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 25);

            var title = parent.GetChild(0);

            title.localPosition = new Vector3(-100, 55);

            var group = parent.GetChild(1).GetComponent<RectTransform>();

            group.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 25);
            group.localPosition = new Vector3(-100, 5);

            toggle = asset.GetComponent<PersonalityFlagToggle>();

            var guiLabel = toggle.titleLabel;

            guiLabel.Text = "UI/&ForcePreferredCantripTitle";

            var tooltip = toggle.tooltip;

            tooltip.Content = "UI/&ForcePreferredCantripDescription";

            toggle.PersonalityFlagDefinition = DatabaseHelper.GetDefinition<PersonalityFlagDefinition>("Authority");

            toggle.PersonalityFlagSelected = (_, _, state) =>
            {
                _forcePreferredCantripUI = state;
                tooltip.Content = "UI/&ForcePreferredCantripDescription";
            };
        }
        else
        {
            toggle = parent.FindChildRecursive("ForcePreferredToggle").GetComponent<PersonalityFlagToggle>();
        }

        toggle.Refresh(_forcePreferredCantripUI, true);
        toggle.tooltip.Content = "UI/&ForcePreferredCantripDescription";
    }

    internal static bool CheckAndModifyCantrips(List<SpellDefinition> readied, SpellDefinition preferred)
    {
        if (!_forcePreferredCantrip)
        {
            return readied.Contains(preferred);
        }

        readied.RemoveAll(c => c != preferred);

        return readied.Count != 0;
    }
}
