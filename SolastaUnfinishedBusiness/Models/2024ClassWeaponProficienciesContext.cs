using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionProficiencys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.WeaponCategoryDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.WeaponTypeDefinitions;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly Dictionary<FeatureDefinitionProficiency, List<string>> ClassWeaponProficiencies = new();
    private static readonly Dictionary<CharacterClassDefinition,
        (FeatureDefinitionProficiency Proficiency, bool Enabled)> ClassWeaponProficiencyRules = new();

    internal static void SwitchBardWeaponProficiency()
    {
        SwitchClassWeaponProficiency(Bard, ProficiencyBardWeapon, Main.Settings.EnableBardWeaponProficiency2024,
            additionalWeapon: Main.Settings.EnableBardScimitarSpecialization ? ScimitarType.Name : null);
    }

    internal static void SwitchMonkWeaponProficiency()
    {
        SwitchClassWeaponProficiency(Monk, ProficiencyMonkWeapon, Main.Settings.EnableMonkWeaponProficiency2024,
            [TagsDefinitions.WeaponTagLight],
            Main.Settings.EnableMonkKatanaSpecialization ? CustomWeaponsContext.KatanaWeaponType.Name : null);
    }

    internal static void SwitchRogueWeaponProficiency()
    {
        SwitchClassWeaponProficiency(Rogue, ProficiencyRogueWeapon, Main.Settings.EnableRogueWeaponProficiency2024,
            [TagsDefinitions.WeaponTagFinesse, TagsDefinitions.WeaponTagLight],
            Main.Settings.EnableRogueScimitarSpecialization ? ScimitarType.Name : null);
    }

    internal static void SwitchSorcererWeaponProficiency()
    {
        SwitchClassWeaponProficiency(Sorcerer, ProficiencySorcererWeapon,
            Main.Settings.EnableSorcererWeaponProficiency2024);
    }

    internal static void SwitchWizardWeaponProficiency()
    {
        SwitchClassWeaponProficiency(Wizard, ProficiencyWizardWeapon, Main.Settings.EnableWizardWeaponProficiency2024);
    }

    private static void SwitchClassWeaponProficiency(
        CharacterClassDefinition characterClass,
        FeatureDefinitionProficiency proficiency,
        bool enabled,
        string[] martialWeaponTags = null,
        string additionalWeapon = null)
    {
        const string conjuredWeaponType = "ConjuredWeaponType";

        if (!ClassWeaponProficiencies.TryGetValue(proficiency, out var originalProficiencies))
        {
            // FixDivineBlade supplies this proficiency independently of the chosen tabletop rules.
            originalProficiencies = proficiency.Proficiencies
                .Concat([conjuredWeaponType])
                .Distinct()
                .ToList();
            ClassWeaponProficiencies.Add(proficiency, originalProficiencies);
        }

        var proficiencies = enabled
            ? new List<string> { SimpleWeaponCategory.Name, conjuredWeaponType }
            : [.. originalProficiencies];

        if (enabled && martialWeaponTags != null)
        {
            // Use ordinary weapons as the source of a weapon type's properties. An enchanted
            // variant must not grant proficiency with every other weapon of its type.
            proficiencies.AddRange(DatabaseRepository.GetDatabase<ItemDefinition>()
                .Where(item => item.IsWeapon && !item.Magical &&
                               item.WeaponDescription.WeaponTypeDefinition.WeaponCategory == MartialWeaponCategory.Name &&
                               item.WeaponDescription.WeaponTags.Any(martialWeaponTags.Contains))
                .Select(item => item.WeaponDescription.WeaponType));
        }

        if (additionalWeapon != null)
        {
            proficiencies.Add(additionalWeapon);
        }

        proficiency.proficiencies = proficiencies.Distinct().ToList();
        ClassWeaponProficiencyRules[characterClass] = (proficiency, enabled);

        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characterService == null)
        {
            return;
        }

        foreach (var character in characterService.PartyCharacters)
        {
            if (character.RulesetCharacter is RulesetCharacterHero hero)
            {
                hero.RefreshAll();
            }
        }
    }

    internal static bool IsClassWeaponProficiencyExcludedFromMulticlass(
        CharacterClassDefinition characterClass, FeatureDefinition feature)
    {
        return ClassWeaponProficiencyRules.TryGetValue(characterClass, out var rule) &&
               rule.Enabled && rule.Proficiency == feature;
    }

    internal static void UpdateClassWeaponProficiencies(RulesetCharacterHero hero)
    {
        if (hero.ClassesHistory.Count == 0)
        {
            return;
        }

        foreach (var entry in ClassWeaponProficiencyRules)
        {
            var characterClass = entry.Key;
            var rule = entry.Value;

            if (hero.GetClassLevel(characterClass) == 0 ||
                !hero.ActiveFeatures.TryGetValue(AttributeDefinitions.GetClassTag(characterClass, 1), out var features))
            {
                continue;
            }

            var granted = hero.ClassesHistory[0] == characterClass ||
                          !MulticlassContext.IsFeatureExcludedFromMulticlass(characterClass, rule.Proficiency);

            if (!granted)
            {
                features.RemoveAll(feature => feature == rule.Proficiency);
            }
            else if (!features.Contains(rule.Proficiency))
            {
                features.Add(rule.Proficiency);
            }
        }
    }

    internal static bool IsMonkWeapon2024(WeaponDescription weapon)
    {
        var type = weapon.WeaponTypeDefinition;

        return type.WeaponProximity == AttackProximity.Melee &&
               (type.WeaponCategory == SimpleWeaponCategory.Name ||
                (type.WeaponCategory == MartialWeaponCategory.Name &&
                 weapon.WeaponTags.Contains(TagsDefinitions.WeaponTagLight)));
    }
}
