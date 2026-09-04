using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.Subclasses;
using static FeatureDefinitionCastSpell;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterSubclassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Builders.Features.FeatureDefinitionCastSpellBuilder;

namespace SolastaUnfinishedBusiness.Models;

internal static class SharedSpellsContext
{
    private const int MaxSpellLevel = 9;

    internal const int PactMagicSlotsTab = -1;

    internal static readonly Dictionary<string, BaseDefinition> RecoverySlots = new()
    {
        { PowerCircleLandNaturalRecovery.Name, Druid },
        { PowerWizardArcaneRecovery.Name, Wizard },
        { Level20Context.PowerWarlockEldritchMasterName, Warlock },
        { WizardSpellMaster.PowerSpellMasterBonusRecoveryName, Wizard }
    };

    internal static readonly Dictionary<string, CasterProgression> ClassCasterType = new()
    {
        { Bard.Name, CasterProgression.Full },
        { Cleric.Name, CasterProgression.Full },
        { Druid.Name, CasterProgression.Full },
        { Sorcerer.Name, CasterProgression.Full },
        { Wizard.Name, CasterProgression.Full },
        { Paladin.Name, CasterProgression.Half },
        { Ranger.Name, CasterProgression.Half },
        { InventorClass.ClassName, CasterProgression.HalfRoundUp }
    };

    internal static readonly Dictionary<string, CasterProgression> SubclassCasterType = new()
    {
        { MartialSpellblade.Name, CasterProgression.OneThird },
        { RoguishArcaneScoundrel.Name, CasterProgression.OneThird },
        { RoguishShadowCaster.Name, CasterProgression.OneThird },
        { MartialSpellShield.FullName, CasterProgression.OneThird }
    };

    // supports custom MaxSpellLevelOfSpellCastLevel behaviors
    internal static bool UseMaxSpellLevelOfSpellCastingLevelDefaultBehavior { get; private set; }

    // supports auto prepared spells scenarios on subs
    internal static CasterProgression GetCasterTypeForClassOrSubclass(
        [CanBeNull] string characterClassDefinition,
        string characterSubclassDefinition)
    {
        if (characterClassDefinition != null && ClassCasterType.TryGetValue(characterClassDefinition, out var value1))
        {
            return value1;
        }

        if (characterSubclassDefinition != null &&
            SubclassCasterType.TryGetValue(characterSubclassDefinition, out var value2))
        {
            return value2;
        }

        return CasterProgression.None;
    }

    internal static int GetSingleCasterLevelContribution(CasterProgression casterType, int characterLevel)
    {
        return casterType switch
        {
            CasterProgression.Full => characterLevel,
            CasterProgression.Half when characterLevel <= 1 => 0,
            CasterProgression.Half => (characterLevel + 1) / 2,
            CasterProgression.HalfRoundUp => (characterLevel + 1) / 2,
            CasterProgression.OneThird => (characterLevel + 2) / 3,
            _ => 0
        };
    }

    // need the null check for companions who don't have repertoires
    internal static bool IsMulticaster([CanBeNull] RulesetCharacter rulesetCharacter)
    {
        return HasClassIdentity(rulesetCharacter) &&
               rulesetCharacter.SpellRepertoires
                   .Count(repertoire => repertoire.UsesSharedSpellSlots()) > 1;
    }

    // factor mystic arcanum level if Warlock repertoire
    internal static void FactorMysticArcanum(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        ref int level)
    {
        if (repertoire.spellCastingClass != Warlock || character == null)
        {
            return;
        }

        var warlockLevel = GetWarlockCasterLevel(character);

        if (warlockLevel > 0)
        {
            // Mystic Arcanum extends the displayed/known spell range beyond pact slots,
            // but spell levels themselves stop at 9.
            level = Math.Min(MaxSpellLevel, (warlockLevel + 1) / 2);
        }
    }

    // need the null check for companions who don't have repertoires
    private static int GetWarlockCasterLevel([CanBeNull] RulesetCharacter rulesetCharacter)
    {
        return GetClassLevel(rulesetCharacter, Warlock);
    }

    internal static int GetClassLevel(
        [CanBeNull] RulesetCharacter rulesetCharacter,
        [CanBeNull] CharacterClassDefinition classDefinition)
    {
        if (classDefinition == null)
        {
            return 0;
        }

        return EnumerateClassAndSubclassLevels(rulesetCharacter)
            .Where(entry => entry.ClassDefinition == classDefinition)
            .Select(entry => entry.Level)
            .FirstOrDefault();
    }

    internal static int GetWarlockSpellLevel([CanBeNull] RulesetCharacter rulesetCharacter)
    {
        var warlockLevel = GetWarlockCasterLevel(rulesetCharacter);

        return GetMaxSpellLevelFromSlots(WarlockCastingSlots, warlockLevel);
    }

    internal static int GetWarlockMaxSlots(RulesetCharacter rulesetCharacter)
    {
        if (!HasClassIdentity(rulesetCharacter))
        {
            return 0;
        }

        var warlockLevel = GetWarlockCasterLevel(rulesetCharacter);
        var warlockAdditionalSlots = rulesetCharacter
            .FeaturesByType<FeatureDefinitionMagicAffinity>()
            .Where(x => x == DatabaseHelper.FeatureDefinitionMagicAffinitys
                .MagicAffinityChitinousBoonAdditionalSpellSlot)
            .SelectMany(x => x.AdditionalSlots)
            .Sum(x => x.SlotsNumber);
        var slots = warlockLevel > 0 && warlockLevel <= WarlockCastingSlots.Count
            ? WarlockCastingSlots[warlockLevel - 1].Slots[0]
            : 0;

        return slots + warlockAdditionalSlots;
    }

    internal static int GetWarlockUsedSlots([NotNull] RulesetCharacter rulesetCharacter)
    {
        var repertoire = GetWarlockSpellRepertoire(rulesetCharacter);

        if (repertoire == null)
        {
            return 0;
        }

        var slotLevel = IsMulticaster(rulesetCharacter)
            ? PactMagicSlotsTab
            : GetWarlockSpellLevel(rulesetCharacter);

        repertoire.usedSpellsSlots.TryGetValue(slotLevel, out var warlockUsedSlots);

        return warlockUsedSlots;
    }

    [CanBeNull]
    internal static RulesetSpellRepertoire GetWarlockSpellRepertoire(
        [CanBeNull] RulesetCharacter rulesetCharacter)
    {
        return HasClassIdentity(rulesetCharacter)
            ? rulesetCharacter.GetClassSpellRepertoire(Warlock)
            : null;
    }

    internal static int GetSharedCasterLevel([CanBeNull] RulesetCharacter rulesetCharacter)
    {
        if (rulesetCharacter == null)
        {
            return 0;
        }

        var casterLevelContext = new CasterLevelContext();

        foreach (var (classDefinition, subclassDefinition, classLevel) in
                 EnumerateClassAndSubclassLevels(rulesetCharacter))
        {
            var casterType = GetCasterTypeForClassOrSubclass(
                classDefinition.Name,
                subclassDefinition?.Name);

            casterLevelContext.IncrementCasterLevel(casterType, classLevel);
        }

        return casterLevelContext.GetCasterLevel();
    }

    internal static int GetSharedSpellLevel(RulesetCharacter rulesetCharacter)
    {
        var sharedCasterLevel = GetSharedCasterLevel(rulesetCharacter);

        if (rulesetCharacter.IsSpellPointsEnabled())
        {
            return GetMaxSpellLevelFromSlots(SpellPointsContext.SpellPointsFullCastingSlots, sharedCasterLevel);
        }

        return GetMaxSpellLevelFromSlots(FullCastingSlots, sharedCasterLevel);
    }

    private static IEnumerable<(
        CharacterClassDefinition ClassDefinition,
        CharacterSubclassDefinition SubclassDefinition,
        int Level)> EnumerateClassAndSubclassLevels(RulesetCharacter character)
    {
        if (character is RulesetCharacterHero hero)
        {
            if (hero.ClassesAndLevels == null)
            {
                yield break;
            }

            foreach (var classAndLevel in hero.ClassesAndLevels)
            {
                var classDefinition = classAndLevel.Key;
                var level = classAndLevel.Value;
                CharacterSubclassDefinition subclassDefinition = null;

                hero.ClassesAndSubclasses?.TryGetValue(
                    classDefinition,
                    out subclassDefinition);

                yield return (classDefinition, subclassDefinition, level);
            }

            yield break;
        }

        var simulacrum = character as RulesetCharacterSimulacrum ??
                         character?.OriginalFormCharacter as RulesetCharacterSimulacrum;

        if (simulacrum == null ||
            !SimulacrumBehavior.TryGetClassLevels(simulacrum, out var classLevels))
        {
            yield break;
        }

        foreach (var classLevel in classLevels)
        {
            SimulacrumBehavior.TryGetPrimarySubclass(
                simulacrum,
                classLevel.ClassDefinition,
                out var subclassDefinition);

            yield return (classLevel.ClassDefinition, subclassDefinition, classLevel.Level);
        }
    }

    private static bool HasClassIdentity(RulesetCharacter character)
    {
        return character is RulesetCharacterHero or RulesetCharacterSimulacrum ||
               character?.OriginalFormCharacter is RulesetCharacterSimulacrum;
    }

    private static int GetMaxSpellLevelFromSlots(IReadOnlyList<SlotsByLevelDuplet> table, int casterLevel)
    {
        if (table == null ||
            casterLevel <= 0 ||
            casterLevel > table.Count)
        {
            return 0;
        }

        var slots = table[casterLevel - 1]?.Slots;

        if (slots == null || slots.Count == 0)
        {
            return 0;
        }

        var firstZero = slots.IndexOf(0);

        return firstZero < 0 ? slots.Count : firstZero;
    }

    internal static void LateLoad()
    {
        PatchMaxSpellLevelOfSpellCastingLevel();
        EnumerateSlotsPerLevel(CasterProgression.Full, FullCastingSlots);
        EnumerateSlotsPerLevel(CasterProgression.Half, HalfCastingSlots);
        EnumerateSlotsPerLevel(CasterProgression.HalfRoundUp, HalfRoundUpCastingSlots);
        EnumerateSlotsPerLevel(CasterProgression.OneThird, OneThirdCastingSlots);
    }

    private static void PatchMaxSpellLevelOfSpellCastingLevel()
    {
        const BindingFlags PrivateBinding = BindingFlags.Instance | BindingFlags.NonPublic;

        var harmony = new Harmony("SolastaUnfinishedBusiness");
        var transpiler =
            new Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>(SharedSpellsTranspiler).Method;
        var methods = new[]
        {
            typeof(CharacterBuildingManager).GetMethod("ApplyFeatureCastSpell", PrivateBinding),
            typeof(GuiCharacter).GetMethod("DisplayUniqueLevelSpellSlots"),
            typeof(ItemMenuModal).GetMethod("SetupFromItem"),
            typeof(RulesetCharacter).GetMethod("EnumerateUsableSpells", PrivateBinding),
            typeof(RulesetCharacterHero).GetMethod("EnumerateUsableRitualSpells"),
            typeof(RulesetSpellRepertoire).GetMethod("HasKnowledgeOfSpell")
        };

        foreach (var method in methods)
        {
            try
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
            }
            catch
            {
                Main.Error($"Failed to apply SharedSpellsTranspiler patch to {method.DeclaringType}.{method.Name}");
            }
        }
    }

    internal static int MaxSpellLevelOfSpellCastingLevel(RulesetSpellRepertoire rulesetSpellRepertoire)
    {
        UseMaxSpellLevelOfSpellCastingLevelDefaultBehavior = true;

        try
        {
            return rulesetSpellRepertoire.MaxSpellLevelOfSpellCastingLevel;
        }
        finally
        {
            UseMaxSpellLevelOfSpellCastingLevelDefaultBehavior = false;
        }
    }

    [NotNull]
    private static IEnumerable<CodeInstruction> SharedSpellsTranspiler(
        [NotNull] IEnumerable<CodeInstruction> instructions)
    {
        var maxSpellLevelOfSpellCastLevelMethod =
            typeof(RulesetSpellRepertoire).GetMethod("get_MaxSpellLevelOfSpellCastingLevel");
        var myMaxSpellLevelOfSpellCastLevelMethod =
            new Func<RulesetSpellRepertoire, int>(MaxSpellLevelOfSpellCastingLevel).Method;

        return instructions.ReplaceCalls(maxSpellLevelOfSpellCastLevelMethod,
            "SharedSpellsContext.SharedSpellsTranspiler",
            new CodeInstruction(OpCodes.Call, myMaxSpellLevelOfSpellCastLevelMethod));
    }

    private static int GetSharedCasterLevelContribution(CasterProgression casterType, int characterLevel)
    {
        return casterType switch
        {
            CasterProgression.Full => characterLevel,
            CasterProgression.Half => characterLevel / 2,
            CasterProgression.HalfRoundUp => (characterLevel + 1) / 2,
            CasterProgression.OneThird => characterLevel / 3,
            _ => 0
        };
    }

    #region Caster Level Context

    private sealed class CasterLevelContext
    {
        private readonly Dictionary<CasterProgression, int> _levels;

        internal CasterLevelContext()
        {
            _levels = new Dictionary<CasterProgression, int>
            {
                { CasterProgression.None, 0 },
                { CasterProgression.Full, 0 },
                { CasterProgression.Half, 0 },
                { CasterProgression.HalfRoundUp, 0 },
                { CasterProgression.OneThird, 0 }
            };
        }

        internal void IncrementCasterLevel(CasterProgression casterProgression, int increment)
        {
            _levels[casterProgression] += increment;
        }

        internal int GetCasterLevel()
        {
            return _levels.Sum(level => GetSharedCasterLevelContribution(level.Key, level.Value));
        }
    }

    #endregion

    #region Slots Definitions

    internal static readonly List<SlotsByLevelDuplet> InitiateCastingSlots =
    [
        new() { Slots = [1], Level = 01 },
        new() { Slots = [1], Level = 02 },
        new() { Slots = [1], Level = 03 },
        new() { Slots = [1], Level = 04 },
        new() { Slots = [1], Level = 05 },
        new() { Slots = [1], Level = 06 },
        new() { Slots = [1], Level = 07 },
        new() { Slots = [1], Level = 08 },
        new() { Slots = [1], Level = 09 },
        new() { Slots = [1], Level = 10 },
        new() { Slots = [1], Level = 11 },
        new() { Slots = [1], Level = 12 },
        new() { Slots = [1], Level = 13 },
        new() { Slots = [1], Level = 14 },
        new() { Slots = [1], Level = 15 },
        new() { Slots = [1], Level = 16 },
        new() { Slots = [1], Level = 17 },
        new() { Slots = [1], Level = 18 },
        new() { Slots = [1], Level = 19 },
        new() { Slots = [1], Level = 20 }
    ];

    internal static readonly List<SlotsByLevelDuplet> RaceCastingSlots =
    [
        new() { Slots = [0, 0], Level = 01 },
        new() { Slots = [0, 0], Level = 02 },
        new() { Slots = [1, 0], Level = 03 },
        new() { Slots = [1, 0], Level = 04 },
        new() { Slots = [1, 1], Level = 05 },
        new() { Slots = [1, 1], Level = 06 },
        new() { Slots = [1, 1], Level = 07 },
        new() { Slots = [1, 1], Level = 08 },
        new() { Slots = [1, 1], Level = 09 },
        new() { Slots = [1, 1], Level = 10 },
        new() { Slots = [1, 1], Level = 11 },
        new() { Slots = [1, 1], Level = 12 },
        new() { Slots = [1, 1], Level = 13 },
        new() { Slots = [1, 1], Level = 14 },
        new() { Slots = [1, 1], Level = 15 },
        new() { Slots = [1, 1], Level = 16 },
        new() { Slots = [1, 1], Level = 17 },
        new() { Slots = [1, 1], Level = 18 },
        new() { Slots = [1, 1], Level = 19 },
        new() { Slots = [1, 1], Level = 20 }
    ];

    internal static readonly List<SlotsByLevelDuplet> RaceEmptyCastingSlots =
    [
        new() { Slots = [0], Level = 01 },
        new() { Slots = [0], Level = 02 },
        new() { Slots = [0], Level = 03 },
        new() { Slots = [0], Level = 04 },
        new() { Slots = [0], Level = 05 },
        new() { Slots = [0], Level = 06 },
        new() { Slots = [0], Level = 07 },
        new() { Slots = [0], Level = 08 },
        new() { Slots = [0], Level = 09 },
        new() { Slots = [0], Level = 10 },
        new() { Slots = [0], Level = 11 },
        new() { Slots = [0], Level = 12 },
        new() { Slots = [0], Level = 13 },
        new() { Slots = [0], Level = 14 },
        new() { Slots = [0], Level = 15 },
        new() { Slots = [0], Level = 16 },
        new() { Slots = [0], Level = 17 },
        new() { Slots = [0], Level = 18 },
        new() { Slots = [0], Level = 19 },
        new() { Slots = [0], Level = 20 }
    ];

    // game uses IndexOf(0) on these sub lists reason why the last 0 there
    private static readonly List<SlotsByLevelDuplet> WarlockCastingSlots =
    [
        new()
        {
            Slots =
            [
                1,
                0,
                0,
                0,
                0,
                0
            ],
            Level = 01
        },

        new()
        {
            Slots =
            [
                2,
                0,
                0,
                0,
                0,
                0
            ],
            Level = 02
        },

        new()
        {
            Slots =
            [
                2,
                2,
                0,
                0,
                0,
                0
            ],
            Level = 03
        },

        new()
        {
            Slots =
            [
                2,
                2,
                0,
                0,
                0,
                0
            ],
            Level = 04
        },

        new()
        {
            Slots =
            [
                2,
                2,
                2,
                0,
                0,
                0
            ],
            Level = 05
        },

        new()
        {
            Slots =
            [
                2,
                2,
                2,
                0,
                0,
                0
            ],
            Level = 06
        },

        new()
        {
            Slots =
            [
                2,
                2,
                2,
                2,
                0,
                0
            ],
            Level = 07
        },

        new()
        {
            Slots =
            [
                2,
                2,
                2,
                2,
                0,
                0
            ],
            Level = 08
        },

        new()
        {
            Slots =
            [
                2,
                2,
                2,
                2,
                2,
                0
            ],
            Level = 09
        },

        new()
        {
            Slots =
            [
                2,
                2,
                2,
                2,
                2,
                0
            ],
            Level = 10
        },

        new()
        {
            Slots =
            [
                3,
                3,
                3,
                3,
                3,
                0
            ],
            Level = 11
        },

        new()
        {
            Slots =
            [
                3,
                3,
                3,
                3,
                3,
                0
            ],
            Level = 12
        },

        new()
        {
            Slots =
            [
                3,
                3,
                3,
                3,
                3,
                0
            ],
            Level = 13
        },

        new()
        {
            Slots =
            [
                3,
                3,
                3,
                3,
                3,
                0
            ],
            Level = 14
        },

        new()
        {
            Slots =
            [
                3,
                3,
                3,
                3,
                3,
                0
            ],
            Level = 15
        },

        new()
        {
            Slots =
            [
                3,
                3,
                3,
                3,
                3,
                0
            ],
            Level = 16
        },

        new()
        {
            Slots =
            [
                4,
                4,
                4,
                4,
                4,
                0
            ],
            Level = 17
        },

        new()
        {
            Slots =
            [
                4,
                4,
                4,
                4,
                4,
                0
            ],
            Level = 18
        },

        new()
        {
            Slots =
            [
                4,
                4,
                4,
                4,
                4,
                0
            ],
            Level = 19
        },

        new()
        {
            Slots =
            [
                4,
                4,
                4,
                4,
                4,
                0
            ],
            Level = 20
        }
    ];

    internal static readonly List<SlotsByLevelDuplet> FullCastingSlots = [];
    internal static readonly List<SlotsByLevelDuplet> HalfCastingSlots = [];
    internal static readonly List<SlotsByLevelDuplet> HalfRoundUpCastingSlots = [];
    internal static readonly List<SlotsByLevelDuplet> OneThirdCastingSlots = [];

    // additional spells supporting collections
    internal static readonly List<int> WarlockKnownSpells =
    [
        2,
        3,
        4,
        5,
        6,
        7,
        8,
        9,
        10,
        10,
        11,
        11,
        12,
        12,
        13,
        13,
        14,
        14,
        15,
        15
    ];

    #endregion
}
