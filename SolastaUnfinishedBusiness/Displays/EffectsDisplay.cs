using System.Linq;
using System.Collections.Generic;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Displays;

internal static class EffectsDisplay
{
    private static readonly ConditionDefinition DummyCondition = ConditionDefinitionBuilder
        .Create("DummyCondition")
        .SetGuiPresentation("Dummy", "Dummy")
        .SetSpecialDuration(RuleDefinitions.DurationType.Round, 1)
        .AddToDB();

    private static readonly Dictionary<EffectHelpers.EffectType, string[]> EffectKeys = [];
    private static readonly Dictionary<EffectHelpers.EffectType, UI.PagedList<string>> EffectLists = [];
    private static readonly UI.PagedList<string> ConditionList = new("ConditionEffectSearch", 32);
    private static string[] _conditionKeys = [];

    private static bool ToggleCaster { get; set; }
    private static bool ToggleCondition { get; set; }
    private static bool ToggleEffect { get; set; }
    private static bool ToggleImpact { get; set; }
    private static bool ToggleZone { get; set; }

    internal static void DisplayEffects()
    {
        if (EffectsContext.Dirty)
        {
            EffectsContext.DumpEffects();
            EffectKeys.Clear();
            _conditionKeys = [];
        }

        UI.Label();

        if (!Gui.GameCampaign || Gui.GameCampaign.Party.CharactersList.Count < 2)
        {
            UI.Label(Gui.Localize("ModUi/&EffectsRequiresParty"));
            UI.Label();

            return;
        }

        var toggle = ToggleCaster;

        if (UI.DisclosureToggle(Gui.Localize("ModUi/&EffectsCaster"), ref toggle))
        {
            ToggleCaster = toggle;
        }

        if (ToggleCaster)
        {
            DisplayEffects(EffectHelpers.EffectType.Caster);
        }

        UI.Label();

        toggle = ToggleCondition;

        if (UI.DisclosureToggle(Gui.Localize("ModUi/&EffectsCondition"), ref toggle))
        {
            ToggleCondition = toggle;
        }

        if (ToggleCondition)
        {
            DisplayConditionEffects();
        }

        UI.Label();

        toggle = ToggleEffect;

        if (UI.DisclosureToggle(Gui.Localize("ModUi/&EffectsEffect"), ref toggle))
        {
            ToggleEffect = toggle;
        }

        if (ToggleEffect)
        {
            DisplayEffects(EffectHelpers.EffectType.Effect);
        }

        UI.Label();

        toggle = ToggleImpact;

        if (UI.DisclosureToggle(Gui.Localize("ModUi/&EffectsImpact"), ref toggle))
        {
            ToggleImpact = toggle;
        }

        if (ToggleImpact)
        {
            DisplayEffects(EffectHelpers.EffectType.Impact);
        }

        UI.Label();

        toggle = ToggleZone;

        if (UI.DisclosureToggle(Gui.Localize("ModUi/&EffectsZone"), ref toggle))
        {
            ToggleZone = toggle;
        }

        if (ToggleZone)
        {
            DisplayEffects(EffectHelpers.EffectType.Zone);
        }

        UI.Label();
    }

    private static void DisplayEffects(EffectHelpers.EffectType effectType)
    {
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        UI.Label();

        var effects = EffectsContext.Effects[effectType];
        if (!EffectLists.TryGetValue(effectType, out var list))
        {
            list = new UI.PagedList<string>($"EffectSearch{effectType}", 32);
            EffectLists.Add(effectType, list);
        }
        if (!EffectKeys.TryGetValue(effectType, out var keys) || keys.Length != effects.Count)
        {
            keys = effects.Keys.ToArray();
            EffectKeys[effectType] = keys;
        }
        var labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        list.Refresh(keys, key => key + " - " + effects[key][0].Item1);
        list.DrawControls(labelStyle);
        for (var index = 0; index < list.VisibleCount; index++)
        {
            var key = list.GetVisibleItem(index);
            var effect = effects[key][0];
            using (UI.HorizontalScope())
            {
                UI.ActionButton("+".Bold().Red(), () =>
                    {
                        var source = characterService.PartyCharacters[0];
                        var target = characterService.PartyCharacters[1];

                        EffectHelpers.StartVisualEffect(source, target, effect.Item2, effectType);
                    },
                    UI.Width(30f));

                var label = key + " - " + effect.Item1;

                UI.Label(label, labelStyle, UI.ExpandWidth(true));
            }
        }
    }

    private static void DisplayConditionEffects()
    {
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        UI.Label();

        var effects = EffectsContext.ConditionEffects;
        if (_conditionKeys.Length != effects.Count)
        {
            _conditionKeys = effects.Keys.ToArray();
        }
        var labelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        ConditionList.Refresh(_conditionKeys, key => key + " - " + effects[key][0].Item1);
        ConditionList.DrawControls(labelStyle);
        for (var index = 0; index < ConditionList.VisibleCount; index++)
        {
            var key = ConditionList.GetVisibleItem(index);
            var conditionEffect = effects[key][0];
            using (UI.HorizontalScope())
            {
                UI.ActionButton("+".Bold().Red(), () =>
                    {
                        var source = characterService.PartyCharacters[0];
                        var rulesetCharacter = source.RulesetCharacter;
                        var baseDefinition = conditionEffect.Item2;

                        switch (baseDefinition)
                        {
                            case ConditionDefinition conditionDefinition:
                            {
                                DummyCondition.conditionStartParticleReference =
                                    conditionDefinition.conditionStartParticleReference;
                                DummyCondition.conditionParticleReference =
                                    conditionDefinition.conditionParticleReference;
                                DummyCondition.conditionEndParticleReference =
                                    conditionDefinition.conditionEndParticleReference;
                                break;
                            }
                            case SpellDefinition spellDefinition:
                            {
                                var effectParticleParameters =
                                    spellDefinition.EffectDescription.EffectParticleParameters;

                                DummyCondition.conditionStartParticleReference =
                                    effectParticleParameters.conditionStartParticleReference;
                                DummyCondition.conditionParticleReference =
                                    effectParticleParameters.conditionParticleReference;
                                DummyCondition.conditionEndParticleReference =
                                    effectParticleParameters.conditionEndParticleReference;
                                break;
                            }
                            case FeatureDefinitionPower featureDefinitionPower:
                            {
                                var effectParticleParameters =
                                    featureDefinitionPower.EffectDescription.EffectParticleParameters;

                                DummyCondition.conditionStartParticleReference =
                                    effectParticleParameters.conditionStartParticleReference;
                                DummyCondition.conditionParticleReference =
                                    effectParticleParameters.conditionParticleReference;
                                DummyCondition.conditionEndParticleReference =
                                    effectParticleParameters.conditionEndParticleReference;
                                break;
                            }
                        }

                        rulesetCharacter.RemoveAllConditionsOfCategoryAndType(
                            AttributeDefinitions.TagEffect, DummyCondition.Name);
                        rulesetCharacter.InflictCondition(
                            DummyCondition.Name,
                            DummyCondition.DurationType,
                            DummyCondition.DurationParameter,
                            DummyCondition.TurnOccurence,
                            AttributeDefinitions.TagEffect,
                            rulesetCharacter.guid,
                            rulesetCharacter.CurrentFaction.Name,
                            1,
                            DummyCondition.Name,
                            0,
                            0,
                            0);
                    },
                    UI.Width(30f));

                var label = key + " - " + conditionEffect.Item1;

                UI.Label(label, labelStyle, UI.ExpandWidth(true));
            }
        }
    }
}
