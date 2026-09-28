using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly ConditionDefinition ConditionHalfOrcAdrenalineRush = ConditionDefinitionBuilder
        .Create(ConditionDefinitions.ConditionDashingBonus, "ConditionHalfOrcAdrenalineRush")
        // The Dash ends this turn; temporary hit points last until the next long rest.
        .SetSpecialDuration(DurationType.Round, 0, TurnOccurenceType.EndOfTurn)
        .AddToDB();

    internal static readonly FeatureDefinitionPower PowerHalfOrcAdrenalineRush = FeatureDefinitionPowerBuilder
        .Create("PowerHalfOrcAdrenalineRush")
        .SetGuiPresentation(Category.Feature, FeatureDefinitionPowers.PowerMonkStepOfTheWindDash)
        .SetUsesProficiencyBonus(ActivationTime.BonusAction, RechargeRate.ShortRest)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.UntilLongRest)
                .SetParticleEffectParameters(FeatureDefinitionPowers.PowerFighterActionSurge)
                .SetEffectForms(
                    EffectFormBuilder.ConditionForm(ConditionHalfOrcAdrenalineRush),
                    EffectFormBuilder.Create()
                        .SetTempHpForm()
                        .SetBonusMode(AddBonusMode.Proficiency)
                        .Build())
                .Build())
        .AddToDB();

    private static readonly CharacterFeatureReplacement HalfOrcAdrenalineRushReplacement = new(
        CharacterRaceDefinitions.HalfOrc,
        FeatureDefinitionAdditionalDamages.AdditionalDamageHalfOrcSavageAttacks,
        PowerHalfOrcAdrenalineRush,
        () => Main.Settings.EnableHalfOrcAdrenalineRush2024);

    internal static void SwitchHalfOrcAdrenalineRush()
    {
        HalfOrcAdrenalineRushReplacement.Apply();
    }

    internal static void SynchronizeSpeciesFeatures(RulesetCharacterHero hero)
    {
        HalfOrcAdrenalineRushReplacement.Synchronize(hero);
    }
}

// Keep the original casting features and repertoires: changing an ability must not relearn spells or restore slots.
internal static class SpeciesSpellcastingContext
{
    private static readonly Dictionary<FeatureDefinition, string> AbilityChoices = new();
    private static readonly HashSet<FeatureDefinitionCastSpell> CastingFeatures = [];
    private static FeatureDefinitionFeatureSet _selectionFeature;

    internal static FeatureDefinitionFeatureSet SelectionFeature => _selectionFeature;

    internal static void Switch()
    {
        EnsureSelectionFeature();

        _selectionFeature.GuiPresentation.hidden = !Main.Settings.EnableSpeciesSpellcastingAbility2024;

        foreach (var choice in AbilityChoices.Keys)
        {
            choice.GuiPresentation.hidden = _selectionFeature.GuiPresentation.Hidden;
        }

        var races = DatabaseRepository.GetDatabase<CharacterRaceDefinition>().ToArray();
        var castingRaces = new HashSet<CharacterRaceDefinition>();

        foreach (var race in races)
        {
            var features = EnumerateFeatures(race.FeatureUnlocks.Select(x => x.FeatureDefinition))
                .OfType<FeatureDefinitionCastSpell>()
                .Where(x => x.SpellCastingOrigin == FeatureDefinitionCastSpell.CastingOrigin.Race)
                .ToArray();

            if (features.Length == 0)
            {
                continue;
            }

            castingRaces.Add(race);

            foreach (var feature in features)
            {
                if (CastingFeatures.Add(feature))
                {
                    feature.AddCustomSubFeatures(new FormattedDefinitionText(
                        description: () => FormatCastingDescription(feature),
                        characterDescription: character => FormatCastingDescription(feature, character)));
                }
            }
        }

        foreach (var race in races)
        {
            // A parent and its subrace share one selection, even when both grant spells.
            race.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == _selectionFeature);

            if (Main.Settings.EnableSpeciesSpellcastingAbility2024 && castingRaces.Contains(race) &&
                !HasCastingAncestor(race, races, castingRaces, new HashSet<CharacterRaceDefinition>()))
            {
                race.FeatureUnlocks.Add(new FeatureUnlockByLevel(_selectionFeature, 1));
            }
        }

        var entities = ServiceRepository.GetService<IRulesetEntityService>();

        if (entities == null)
        {
            return;
        }

        foreach (var hero in entities.RulesetEntities.Values.OfType<RulesetCharacterHero>()
                     .Where(x => x.SpellRepertoires.Any(r => CastingFeatures.Contains(r.SpellCastingFeature)))
                     .ToArray())
        {
            // Native refresh computes attack, save DC, ability bonuses and their trends using the effective ability.
            hero.RefreshSpellRepertoires();
            hero.RefreshAttackModes();
        }
    }

    internal static bool IsSelectionFeature(FeatureDefinition feature) =>
        feature != null && feature == _selectionFeature;

    internal static bool IsAbilityChoice(FeatureDefinition feature) =>
        feature != null && AbilityChoices.ContainsKey(feature);

    internal static bool TryGetSelectedAbilityFeature(RulesetCharacterHero hero, out FeatureDefinition feature)
    {
        feature = null;

        if (hero == null || !hero.ActiveFeatures.TryGetValue(AttributeDefinitions.TagRace, out var features))
        {
            return false;
        }

        feature = features.FirstOrDefault(IsAbilityChoice);
        return feature != null;
    }

    internal static bool TryGetSpellcastingAbility(RulesetSpellRepertoire repertoire, out string ability)
    {
        ability = null;

        // Several feats and fighting styles also use CastingOrigin.Race. Only actual species casting qualifies.
        if (!Main.Settings.EnableSpeciesSpellcastingAbility2024 ||
            repertoire?.SpellCastingFeature == null || !CastingFeatures.Contains(repertoire.SpellCastingFeature) ||
            !TryGetSelectedAbilityFeature(repertoire.GetCaster()?.GetOriginalHero(), out var feature))
        {
            return false;
        }

        ability = AbilityChoices[feature];
        return true;
    }

    private static void EnsureSelectionFeature()
    {
        if (_selectionFeature != null)
        {
            return;
        }

        foreach (var ability in new[]
                 {
                     AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma
                 })
        {
            var choice = FeatureDefinitionBuilder.Create($"FeatureSpeciesSpellcasting{ability}")
                .SetGuiPresentation($"Attribute/&{ability}TitleLong", "Feature/&SpeciesSpellcastingAbilitySelectedDescription")
                .AddCustomSubFeatures(new FormattedDefinitionText(description: () => FormatSelectedAbility(ability)))
                .AddToDB();

            AbilityChoices.Add(choice, ability);
        }

        _selectionFeature = FeatureDefinitionFeatureSetBuilder.Create("FeatureSetSpeciesSpellcastingAbility")
            .SetGuiPresentation(Category.Feature)
            .SetMode(FeatureDefinitionFeatureSet.FeatureSetMode.Exclusion)
            .SetFeatureSet(AbilityChoices.Keys.ToArray())
            .AddToDB();
    }

    private static string FormatSelectedAbility(string ability) =>
        Gui.Format("Feature/&SpeciesSpellcastingAbilitySelectedDescription", Gui.FormatAbilityScoreLong(ability));

    private static string FormatCastingDescription(FeatureDefinitionCastSpell feature, RulesetCharacter character = null)
    {
        if (!Main.Settings.EnableSpeciesSpellcastingAbility2024)
        {
            return null;
        }

        // A saved hero without a choice retains the original ability until character rebuilding selects one.
        if (character != null &&
            !TryGetSelectedAbilityFeature(character.GetOriginalHero(), out _))
        {
            return Gui.Localize(feature.GuiPresentation.Description);
        }

        var descriptionKey = feature.GuiPresentation.Description.Replace("Description", "SpeciesSpellcastingDescription");

        return TranslatorContext.HasTranslation(descriptionKey) ? Gui.Localize(descriptionKey) : null;
    }

    private static bool HasCastingAncestor(CharacterRaceDefinition race, CharacterRaceDefinition[] races,
        HashSet<CharacterRaceDefinition> castingRaces, HashSet<CharacterRaceDefinition> visited)
    {
        if (!visited.Add(race))
        {
            return false;
        }

        return races.Where(parent => parent.SubRaces.Contains(race))
            .Any(parent => castingRaces.Contains(parent) || HasCastingAncestor(parent, races, castingRaces, visited));
    }

    private static IEnumerable<FeatureDefinition> EnumerateFeatures(IEnumerable<FeatureDefinition> roots)
    {
        var pending = new Stack<FeatureDefinition>(roots);
        var visited = new HashSet<FeatureDefinition>();

        while (pending.Count > 0)
        {
            var feature = pending.Pop();

            if (feature == null || !visited.Add(feature))
            {
                continue;
            }

            yield return feature;

            if (feature is not FeatureDefinitionFeatureSet set)
            {
                continue;
            }

            foreach (var child in set.FeatureSet)
            {
                pending.Push(child);
            }
        }
    }
}
