using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Api.ModKit;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Displays;

internal static class EncountersDisplay
{
    private sealed class CharacterDetails
    {
        internal bool Attributes;
        internal bool Statistics;
        internal bool Attacks;
        internal bool Features;
        internal bool Inventory;
    }

    private static readonly Dictionary<MonsterDefinition, CharacterDetails> MonsterDetails = [];
    private static readonly ConditionalWeakTable<RulesetCharacterHero, CharacterDetails> HeroDetails = new();
    private static readonly UI.PagedList<MonsterDefinition> Bestiary = new("EncounterMonsterSearch", 8);
    private static readonly UI.PagedList<RulesetCharacterHero> Heroes = new("EncounterHeroSearch", 8);
    private static readonly string[] AbilityScores =
    [
        AttributeDefinitions.Strength, AttributeDefinitions.Dexterity, AttributeDefinitions.Constitution,
        AttributeDefinitions.Intelligence, AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma
    ];

    private static void DisplayText(string text)
    {
        UI.Label(text, new GUIStyle(GUI.skin.label) { wordWrap = true }, UI.ExpandWidth(true));
    }

    private static string FormatValue(string titleKey, object value)
    {
        return Gui.Format("ModUi/&EncounterValueFormat", Gui.Localize(titleKey), value?.ToString() ?? string.Empty);
    }

    private static void DisplayCharacterHeader(string title, string subtitle, string actionText, Action action)
    {
        var formattedAction = actionText.Bold().Red();
        var actionWidth = Mathf.Max(30f, GUI.skin.button.CalcSize(new GUIContent(formattedAction)).x);
        using (UI.HorizontalScope())
        {
            UI.ActionButton(formattedAction, action, UI.Width(actionWidth));
            DisplayText(title.Orange().Bold());
        }

        DisplayText(subtitle);
    }

    private static CharacterDetails GetDetails<T>(Dictionary<T, CharacterDetails> details, T character)
    {
        if (!details.TryGetValue(character, out var state))
        {
            state = new CharacterDetails();
            details.Add(character, state);
        }

        return state;
    }

    private static CharacterDetails GetDetails(
        ConditionalWeakTable<RulesetCharacterHero, CharacterDetails> details, RulesetCharacterHero hero)
    {
        return details.GetValue(hero, _ => new CharacterDetails());
    }

    private static void DisplayAttributes(Func<int, int> getScore)
    {
        DisplayText(string.Join(" • ", AbilityScores.Select((attribute, index) =>
            FormatValue($"Attribute/&{attribute}Title", getScore(index)))));
    }

    private static void DisplayDetailButtons(CharacterDetails state, params NamedAction[] additional)
    {
        var attributes = Gui.Localize("Modal/&ContentPropertyAbilityScoresTitle");
        var statistics = Gui.Localize("ModUi/&EncounterStatistics");
        NamedAction[] common =
        [
            new(attributes, () => UI.DisclosureToggle(attributes, ref state.Attributes, 0)),
            new(statistics, () => UI.DisclosureToggle(statistics, ref state.Statistics, 0))
        ];

        var actions = common.Concat(additional).ToArray();
        UI.HStack(null, actions.Length, actions);
    }

    private static void DisplayHeroStats([NotNull] RulesetCharacterHero hero, string actionText, Action action)
    {
        DisplayCharacterHeader($"{hero.Name} {hero.SurName}",
            $"{hero.RaceDefinition.FormatTitle()} {hero.ClassesHistory[0].FormatTitle()}", actionText, action);
        var state = GetDetails(HeroDetails, hero);
        var inventory = Gui.Localize("Screen/&InventoryTitle");
        DisplayDetailButtons(state,
            new NamedAction(inventory, () => UI.DisclosureToggle(inventory, ref state.Inventory, 0)));

        if (state.Attributes)
        {
            DisplayAttributes(index => hero.TryGetAttributeValue(AbilityScores[index]));
        }

        if (state.Statistics)
        {
            DisplayText(string.Join(" • ",
                FormatValue("Attribute/&ArmorClassTitle", hero.TryGetAttributeValue(AttributeDefinitions.ArmorClass)),
                FormatValue("Attribute/&HitDiceTitle", $"{hero.MaxHitDiceCount():0#}{hero.MainHitDie}"),
                FormatValue("Attribute/&ExperienceTitle", hero.TryGetAttributeValue(AttributeDefinitions.Experience)),
                FormatValue("Attribute/&CharacterLevelTitle", hero.TryGetAttributeValue(AttributeDefinitions.CharacterLevel))));
        }

        if (!state.Inventory)
        {
            return;
        }

        foreach (var slot in hero.CharacterInventory.EnumerateAllSlots(false, true))
        {
            if (slot.EquipedItem != null)
            {
                DisplayText(slot.EquipedItem.ItemDefinition.FormatTitle());
            }
        }
    }

    private static void DisplayMonsterStats([NotNull] MonsterDefinition monster, string actionText, Action action)
    {
        DisplayCharacterHeader(monster.FormatTitle(), Gui.Localize($"Alignment/&{monster.Alignment}Title"),
            actionText, action);
        var state = GetDetails(MonsterDetails, monster);
        var attacks = Gui.Format("ModUi/&EncounterCountFormat",
            Gui.Localize("Screen/&JournalBestiaryAttacksTitle"), monster.AttackIterations.Count.ToString());
        var features = Gui.Format("ModUi/&EncounterCountFormat",
            Gui.Localize("Screen/&SubclassFeaturesTitle"), monster.Features.Count.ToString());
        DisplayDetailButtons(state,
            new NamedAction(attacks, () => UI.DisclosureToggle(attacks, ref state.Attacks, 0)),
            new NamedAction(features, () => UI.DisclosureToggle(features, ref state.Features, 0)));

        if (state.Attributes)
        {
            DisplayAttributes(index => monster.AbilityScores[index]);
        }

        if (state.Statistics)
        {
            DisplayText(string.Join(" • ",
                FormatValue("Attribute/&ArmorClassTitle", monster.ArmorClass),
                FormatValue("Attribute/&HitDiceTitle", $"{monster.HitDice:0#}{monster.HitDiceType}"),
                FormatValue("Modal/&ContentPropertyChallengeRatingTitle", monster.ChallengeRating)));
        }

        if (state.Features)
        {
            foreach (var feature in monster.Features)
            {
                DisplayText(FormatDefinitionTitle(feature));
            }
        }

        if (!state.Attacks)
        {
            return;
        }

        foreach (var iteration in monster.AttackIterations)
        {
            var attack = iteration.MonsterAttackDefinition;
            var maxUses = attack.MaxUses < 0 ? Gui.Localize("ModUi/&EncounterUnlimited") : attack.MaxUses.ToString();

            DisplayText(FormatDefinitionTitle(attack).Cyan());
            DisplayText(string.Join(" • ",
                Gui.Localize($"Action/&ActionType{attack.ActionType}Title"),
                FormatValue("Equipment/&ReachTitle", attack.ReachRange),
                FormatValue("Screen/&AttackToHitTitle", attack.ToHitBonus),
                Gui.Format("ModUi/&EncounterMaximumUsesFormat", maxUses)));

            if (attack.Magical)
            {
                DisplayText(Gui.Localize("Tooltip/&TagMagicalTitle"));
            }
        }
    }

    private static string FormatDefinitionTitle(BaseDefinition definition)
    {
        var title = definition.FormatTitle();

        return title == "None" || title == Gui.Localize("Tooltip/&ValueNoneTitle")
            ? definition.Name.SplitCamelCase()
            : title;
    }

    internal static void DisplayEncountersGeneral()
    {
        UI.Label();
        DisplayText(Gui.Localize("ModUi/&PlayerControllers"));
        UI.Label();

        var guiEnabled = GUI.enabled;

        try
        {
            GUI.enabled = guiEnabled && PlayerControllerContext.CanControlCharacters;
            var toggle = Main.Settings.EnableEnemiesControlledByPlayer;

            if (UI.Toggle(Gui.Localize("ModUi/&EnableEnemiesControlledByPlayer"), ref toggle, UI.AutoWidth()))
            {
                Main.Settings.EnableEnemiesControlledByPlayer = toggle;
            }

            DisplayHeroesControllerTable();
        }
        finally
        {
            GUI.enabled = guiEnabled;
        }

        if (!PlayerControllerContext.CanControlCharacters)
        {
            DisplayText(Gui.Localize("ModUi/&PlayerControllersSinglePlayerOnly"));
        }

        DisplayAdvancedCombatAiToggles();
        DisplayEncountersTable();
    }

    private static void DisplayAdvancedCombatAiToggles()
    {
        var toggle = Main.Settings.EnableAdvancedCombatAI;

        if (UI.Toggle(Gui.Localize("ModUi/&EnableAdvancedCombatAI"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableAdvancedCombatAI = toggle;
        }

        if (Main.Settings.EnableAdvancedCombatAI && PlayerControllerContext.AreEnemiesControlledByPlayer)
        {
            DisplayText(Gui.Localize("ModUi/&AdvancedCombatAiManualNpcHelp"));
        }
    }

    private static void DisplayHeroesControllerTable()
    {
        var toggle = Main.Settings.EnableHeroesControlledByComputer;

        if (UI.Toggle(Gui.Localize("ModUi/&EnableHeroesControlledByComputer"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableHeroesControlledByComputer = toggle;
        }

        if (!Main.Settings.EnableHeroesControlledByComputer || !PlayerControllerContext.CanControlCharacters)
        {
            return;
        }

        if (!Gui.GameCampaign)
        {
            DisplayText(Gui.Localize("ModUi/&HeroesControllerLoadGameHelp"));
            return;
        }

        PlayerControllerContext.RefreshGuiState();

        var controllers = PlayerControllerContext.Controllers;
        var characters = PlayerControllerContext.PlayerCharacters;
        var choices = PlayerControllerContext.PlayerCharactersChoices;
        var buttonStyle = new GUIStyle(GUI.skin.button) { wordWrap = true };

        for (var index = 0; index < characters.Count; index++)
        {
            UI.Label();
            DisplayText(characters[index].Name.Orange().Bold());

            if (UI.SelectionGrid(ref choices[index], controllers, 4, buttonStyle, 2, UI.ExpandWidth(true)))
            {
                PlayerControllerContext.PlayerCharactersChoices = choices;
            }
        }
    }

    private static void DisplayEncountersTable()
    {
        UI.Label();
        DisplayText(Gui.Localize("ModUi/&Encounters").Khaki());
        UI.Label();
        DisplayText(Gui.Format("ModUi/&EncountersHelp", Gui.Localize("Setting/&SpawnEncounterTitle")));
        UI.Label();

        if (EncountersContext.EncounterCharacters.Count == 0)
        {
            DisplayText(Gui.Localize("ModUi/&EncounterListEmpty"));
        }
        else
        {
            for (var index = 0; index < EncountersContext.EncounterCharacters.Count; index++)
            {
                // Prevent captured closure.
                var currentIndex = index;

                switch (EncountersContext.EncounterCharacters[index])
                {
                    case RulesetCharacterMonster monster:
                        DisplayMonsterStats(monster.MonsterDefinition, "-",
                            () => EncountersContext.RemoveFromEncounter(currentIndex));
                        break;
                    case RulesetCharacterHero hero:
                        DisplayHeroStats(hero, "-", () => EncountersContext.RemoveFromEncounter(currentIndex));
                        break;
                }
            }
        }

        UI.Label();
    }

    private static void DisplayAddToEncounterHelp()
    {
        UI.Label();
        DisplayText(Gui.Format("ModUi/&EncounterAddCharactersHelp", EncountersContext.MaxEncounterCharacters.ToString()));
        UI.Label();
    }

    internal static void DisplayBestiary()
    {
        DisplayAddToEncounterHelp();
        Bestiary.Refresh(EncountersContext.GetMonsters(), monster => monster.FormatTitle());
        Bestiary.DrawControls(new GUIStyle(GUI.skin.label) { wordWrap = true });
        for (var index = 0; index < Bestiary.VisibleCount; index++)
        {
            var monster = Bestiary.GetVisibleItem(index);
            DisplayMonsterStats(monster, "+", () => EncountersContext.AddToEncounter(monster));
        }
    }

    internal static void DisplayNpcs()
    {
        DisplayAddToEncounterHelp();
        var heroes = EncountersContext.GetHeroes();
        UI.ActionButton(Gui.Localize("ModUi/&SettingsRefresh"), EncountersContext.RefreshHeroes, UI.AutoWidth());
        if (EncountersContext.UnavailableHeroCount > 0)
        {
            DisplayText(Gui.Format("ModUi/&EncounterUnavailableHeroes",
                EncountersContext.UnavailableHeroCount.ToString()));
        }
        Heroes.Refresh(heroes, hero => $"{hero.Name} {hero.SurName}", EncountersContext.HeroCatalogRevision);
        Heroes.DrawControls(new GUIStyle(GUI.skin.label) { wordWrap = true });
        for (var index = 0; index < Heroes.VisibleCount; index++)
        {
            var hero = Heroes.GetVisibleItem(index);
            DisplayHeroStats(hero, "+", () => EncountersContext.AddToEncounter(hero));
        }
    }
}
