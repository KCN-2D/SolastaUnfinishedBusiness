using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStatsPanelPatcher
{
    private static readonly ConditionalWeakTable<CharacterStatsPanel, RulesetCharacter>
        NonHeroSubjects = new();
    private static readonly ConditionalWeakTable<AttackModesPanel, NonHeroAttackModesBinding>
        NonHeroAttackModeSubjects = new();

    internal static void BindAbilityScores(
        AbilityScoresListingPanel panel,
        RulesetCharacter character)
    {
        if (!panel || character == null)
        {
            return;
        }

        panel.Unbind();

        for (var index = 0; index < AttributeDefinitions.AbilityScoreNames.Length; index++)
        {
            var attribute = character.GetAttribute(AttributeDefinitions.AbilityScoreNames[index]);

            panel.abilityScoreBoxes[index].Bind(attribute, panel.abilityScoreBoxes);
        }

        panel.gameObject.SetActive(true);
        panel.RefreshNow();
    }

    internal static void RefreshAbilityScores(AbilityScoresListingPanel panel)
    {
        if (!panel)
        {
            return;
        }

        panel.gameObject.SetActive(true);
        panel.RefreshNow();
    }

    internal static void UnbindAbilityScores(AbilityScoresListingPanel panel)
    {
        if (panel)
        {
            panel.Unbind();
        }
    }

    internal static void BindCharacter(
        CharacterStatsPanel panel,
        RulesetCharacter character)
    {
        if (!panel || character == null)
        {
            return;
        }

        NonHeroSubjects.Remove(panel);
        UnbindStatBoxes(panel);
        panel.Unbind();
        NonHeroSubjects.Add(panel, character);

        panel.heroCharacter = null;
        panel.guiCharacter = new GuiCharacter(character);

        var flags = CharacterStatsPanel.MoveFlag;

        if (BindAttribute(panel.armorClassBox, character, AttributeDefinitions.ArmorClass))
        {
            flags |= CharacterStatsPanel.ArmorClassFlag;
        }

        if (BindAttribute(panel.initiativeBox, character, AttributeDefinitions.Initiative, "+0;-#"))
        {
            flags |= CharacterStatsPanel.InitiativeFlag;
        }

        if (BindAttribute(panel.proficiencyBox, character, AttributeDefinitions.ProficiencyBonus, "+0;-#"))
        {
            flags |= CharacterStatsPanel.ProficiencyFlag;
        }

        if (BindAttribute(panel.hitPointBox, character, AttributeDefinitions.HitPoints))
        {
            flags |= CharacterStatsPanel.HitPointMaxFlag;
        }

        if (character is RulesetCharacterSimulacrum duplicate &&
            SimulacrumBehavior.TryGetClassLevels(duplicate, out var classes) &&
            classes.Count > 0)
        {
            flags |= CharacterStatsPanel.HitDiceFlag;
        }

        panel.healthTooltip = Gui.GetTooltip(panel.healthLabel.gameObject);
        panel.Show(flags);
        RefreshCharacterCore(panel, character);
    }

    internal static void RefreshCharacter(CharacterStatsPanel panel)
    {
        if (panel && NonHeroSubjects.TryGetValue(panel, out var character))
        {
            RefreshCharacterCore(panel, character);
        }
    }

    internal static void UnbindCharacter(CharacterStatsPanel panel)
    {
        if (!panel || !NonHeroSubjects.Remove(panel))
        {
            return;
        }

        UnbindStatBoxes(panel);
        panel.Unbind();
    }

    internal static void BindAttackModes(
        AttackModesPanel panel,
        RulesetCharacter character)
    {
        if (!panel || character == null)
        {
            return;
        }

        panel.Unbind();
        var binding = new NonHeroAttackModesBinding(panel, character);

        NonHeroAttackModeSubjects.Add(panel, binding);
        binding.Bind();
        panel.gameObject.SetActive(true);
        panel.RefreshNow();
    }

    internal static void RefreshAttackModes(AttackModesPanel panel)
    {
        if (panel && NonHeroAttackModeSubjects.TryGetValue(panel, out _))
        {
            panel.RefreshNow();
        }
    }

    internal static void RefreshAttackModes(
        AttackModesPanel panel,
        RulesetCharacter character)
    {
        if (!panel || character == null)
        {
            return;
        }

        panel.relevantAttackModes.Clear();
        panel.relevantAttackModes.AddRange(character.AttackModes.Where(mode =>
            mode.ActionType is ActionDefinitions.ActionType.Main or
                ActionDefinitions.ActionType.Bonus));

        while (panel.attackModesTable.childCount < panel.relevantAttackModes.Count)
        {
            Gui.GetPrefabFromPool(panel.attackModePrefab, panel.attackModesTable);
        }

        for (var index = 0; index < panel.attackModesTable.childCount; index++)
        {
            var child = panel.attackModesTable.GetChild(index);
            var attackModeBox = child.GetComponent<AttackModeBox>();

            attackModeBox.Unbind();

            if (index >= panel.relevantAttackModes.Count)
            {
                child.gameObject.SetActive(false);

                continue;
            }

            child.gameObject.SetActive(true);
            attackModeBox.Bind(panel.relevantAttackModes[index]);
        }
    }

    internal static void UnbindAttackModes(AttackModesPanel panel)
    {
        if (!panel)
        {
            return;
        }

        RemoveNonHeroAttackModeSubject(panel);
        panel.Unbind();
    }

    private static void RemoveNonHeroAttackModeSubject(AttackModesPanel panel)
    {
        if (panel &&
            NonHeroAttackModeSubjects.TryGetValue(panel, out var binding))
        {
            binding.Unbind();
            NonHeroAttackModeSubjects.Remove(panel);
        }
    }

    private static bool BindAttribute(
        CharacterStatBox box,
        RulesetCharacter character,
        string attributeName,
        string valueFormat = null)
    {
        box.Unbind();
        character.TryGetAttribute(attributeName, out var attribute);

        if (attribute == null)
        {
            return false;
        }

        box.Bind(attribute, valueFormat);

        return true;
    }

    private static void UnbindStatBoxes(CharacterStatsPanel panel)
    {
        panel.armorClassBox.Unbind();
        panel.initiativeBox.Unbind();
        panel.proficiencyBox.Unbind();
        panel.hitPointBox.Unbind();
    }

    private static void RemoveNonHeroSubject(CharacterStatsPanel panel)
    {
        if (panel && NonHeroSubjects.Remove(panel))
        {
            UnbindStatBoxes(panel);
        }
    }

    private static void RefreshCharacterCore(
        CharacterStatsPanel panel,
        RulesetCharacter character)
    {
        if (panel.armorClassBox.Activated)
        {
            panel.armorClassBox.Refresh();
        }

        if (panel.initiativeBox.Activated)
        {
            panel.initiativeBox.Refresh();
        }

        if (panel.moveBox.Activated)
        {
            panel.moveBox.ValueLabel.Text = panel.guiCharacter.MovePoints;
        }

        if (panel.proficiencyBox.Activated)
        {
            panel.proficiencyBox.Refresh();
        }

        if (panel.hitPointBox.Activated)
        {
            panel.hitPointBox.Refresh();
            panel.guiCharacter.FormatHealthLabelAdvanced(
                panel.healthLabel,
                panel.maxHealthLabel,
                panel.healthTooltip,
                false);
        }
        else
        {
            panel.maxHealthLabel.Text = string.Empty;
        }

        if (panel.hitDiceBox.Activated &&
            character is RulesetCharacterSimulacrum duplicate &&
            SimulacrumBehavior.TryGetClassLevels(duplicate, out var classLevels))
        {
            panel.hitDiceBox.ValueLabel.Text = BuildHitDiceLabel(classLevels);
        }
    }

    private static string BuildHitDiceLabel(
        IReadOnlyList<SimulacrumBehavior.ClassLevelSeed> classLevels)
    {
        var dice = new Dictionary<RuleDefinitions.DieType, int>();

        foreach (var classLevel in classLevels)
        {
            dice.TryGetValue(classLevel.ClassDefinition.HitDice, out var count);
            dice[classLevel.ClassDefinition.HitDice] = count + classLevel.Level;
        }

        return string.Join(
            " ",
            dice.Select(entry => $"{entry.Value}{Gui.GetDieSymbol(entry.Key)}"));
    }

    private sealed class NonHeroAttackModesBinding
    {
        private readonly AttackModesPanel _panel;
        private readonly RulesetInventory.ItemEquipedHandler _itemEquiped;
        private readonly RulesetInventory.ItemUnequipedHandler _itemUnequiped;
        private readonly RulesetCharacter.CharacterRefreshedHandler _characterRefreshed;

        internal NonHeroAttackModesBinding(
            AttackModesPanel panel,
            RulesetCharacter character)
        {
            _panel = panel;
            Character = character;
            _itemEquiped = (_, _, _) => _panel.Dirty = true;
            _itemUnequiped = (_, _, _) => _panel.Dirty = true;
            _characterRefreshed = _ => _panel.RefreshNow();
        }

        internal RulesetCharacter Character { get; }

        internal void Bind()
        {
            Character.CharacterInventory.ItemEquiped += _itemEquiped;
            Character.CharacterInventory.ItemUnequiped += _itemUnequiped;
            Character.CharacterRefreshed += _characterRefreshed;
        }

        internal void Unbind()
        {
            Character.CharacterInventory.ItemEquiped -= _itemEquiped;
            Character.CharacterInventory.ItemUnequiped -= _itemUnequiped;
            Character.CharacterRefreshed -= _characterRefreshed;
        }
    }

    [HarmonyPatch(typeof(AttackModesPanel), nameof(AttackModesPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AttackModesBind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(AttackModesPanel __instance)
        {
            RemoveNonHeroAttackModeSubject(__instance);
        }
    }

    [HarmonyPatch(typeof(AttackModesPanel), nameof(AttackModesPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AttackModesUnbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(AttackModesPanel __instance)
        {
            RemoveNonHeroAttackModeSubject(__instance);
        }
    }

    [HarmonyPatch(typeof(AttackModesPanel), nameof(AttackModesPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AttackModesRefresh_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(AttackModesPanel __instance)
        {
            if (!NonHeroAttackModeSubjects.TryGetValue(__instance, out var binding))
            {
                return true;
            }

            RefreshAttackModes(__instance, binding.Character);

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterStatsPanel), nameof(CharacterStatsPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStatsPanel __instance)
        {
            RemoveNonHeroSubject(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStatsPanel), nameof(CharacterStatsPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStatsPanel __instance)
        {
            RemoveNonHeroSubject(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStatsPanel), nameof(CharacterStatsPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(CharacterStatsPanel __instance)
        {
            if (!NonHeroSubjects.TryGetValue(__instance, out var character))
            {
                return true;
            }

            RefreshCharacterCore(__instance, character);

            return false;
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStatsPanel __instance)
        {
            UiTextHelpers.KeepCharacterStatsPanelTextInside(__instance);

            //PATCH: Format hit dice box to support MC scenarios (MULTICLASS)
            var hero = __instance.guiCharacter?.RulesetCharacterHero;

            if (!__instance.hitDiceBox.Activated ||
                hero == null ||
                hero.ClassesAndLevels.Count <= 1)
            {
                return;
            }

            __instance.hitDiceBox.ValueLabel.Text =
                MulticlassGameUi.GetAllClassesHitDiceLabel(__instance.guiCharacter, out var dieTypeCount);
            __instance.hitDiceBox.ValueLabel.TMP_Text.fontSize = MulticlassGameUi.GetFontSize(dieTypeCount);
            UiTextHelpers.FitCharacterStatBox(__instance.hitDiceBox);
        }
    }
}
