using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using static ConsoleStyleDuplet;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameConsolePatcher
{
    [HarmonyPatch(typeof(GuiTextFragment), nameof(GuiTextFragment.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GuiTextFragment_Bind_Patch
    {
        [UsedImplicitly]
        internal static void Prefix(
            GuiTextFragment __instance,
            ref TextBreaker.FragmentInfo fragmentInfo,
            GameRecordEntry entry)
        {
            // GuiTextFragment instances are pooled. Native Bind only writes tooltip data when
            // the new fragment has content, so reset every tooltip state before every bind.
            var tooltip = __instance.FragmentTooltip;

            tooltip?.Clear();

            if (tooltip != null)
            {
                tooltip.Disabled = false;
            }

            ResolveFragmentTooltip(
                GetParameterType(fragmentInfo, entry),
                fragmentInfo.contentValue,
                ref fragmentInfo.tooltipContent,
                ref fragmentInfo.tooltipClass);
        }

        [UsedImplicitly]
        internal static void Postfix(
            GuiTextFragment __instance,
            GameRecordEntry entry)
        {
            var tooltip = __instance.FragmentTooltip;

            if (tooltip == null)
            {
                return;
            }

            if (entry is GameConsoleEntry consoleEntry &&
                TryGetLiveFriendlyMonsterTooltipCharacter(
                    consoleEntry,
                    tooltip.Content,
                    out var monster))
            {
                Tooltips.TrySetupLiveFriendlyMonsterTooltip(tooltip, monster);
            }

            var displayable = IsDisplayableBoundTooltip(tooltip);

            // Empty interactive fragments used to open the engine's fallback "-" tooltip.
            // Non-interactive styles may still intentionally carry explanatory tooltips, so
            // Disabled follows the final content rather than the button style.
            tooltip.Disabled = !displayable;
        }
    }

    [HarmonyPatch(typeof(GameConsole), nameof(GameConsole.AddCharacterEntry))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AddCharacterEntry_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetActor character,
            GameConsoleEntry entry)
        {
            var parameters = entry?.Parameters;

            if (character is not RulesetCharacterMonster { Side: RuleDefinitions.Side.Ally } monster ||
                parameters is not { Count: > 0 })
            {
                return;
            }

            var parameterIndex = parameters.Count - 1;
            var parameter = parameters[parameterIndex];

            if ((parameter.parameterType != (int)ParameterType.Player &&
                 parameter.parameterType != (int)ParameterType.Enemy) ||
                !string.Equals(
                    parameter.tooltipClass,
                    GuiMonsterDefinition.TooltipClassMonsterDefinition,
                    StringComparison.Ordinal))
            {
                return;
            }

            var token =
                $"{LiveFriendlyMonsterTooltipTokenPrefix}{monster.Guid:X16}:{parameterIndex}";
            var bindings = LiveFriendlyMonsterTooltipBindings.GetValue(
                entry,
                _ => new Dictionary<string, LiveFriendlyMonsterConsoleBinding>(
                    StringComparer.Ordinal));

            // Native character parameters retain only the static MonsterDefinition.
            // Keep an entry-local runtime binding so two same-named summoned creatures can
            // still be rebound to their exact live RulesetCharacter at fragment bind.
            bindings[token] = new LiveFriendlyMonsterConsoleBinding(
                parameterIndex,
                monster);
        }
    }

    [HarmonyPatch(typeof(GameConsole), nameof(GameConsole.LegendaryActionUsed))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LegendaryActionUsed_Patch
    {
        private const string UnknownLegendarySpellLine =
            "Feedback/&LegendaryActionSpellCastUnknownLine";

        [UsedImplicitly]
        public static bool Prefix(
            GameConsole __instance,
            RulesetCharacter character,
            LegendaryActionDescription legendaryActionDescription)
        {
            if (character is not RulesetCharacterMonster { Side: RuleDefinitions.Side.Enemy } ||
                legendaryActionDescription?.Subaction != LegendaryActionDescription.SubactionType.Spell)
            {
                return true;
            }

            // Native logging reveals the spell name before the identification check runs.
            // Keep the legendary-action event visible, then let SpellIdentified disclose the
            // specific spell only when the subsequent identification check succeeds.
            var entry = new GameConsoleEntry(
                UnknownLegendarySpellLine,
                __instance.consoleTableDefinition)
            {
                Indent = true
            };

            __instance.AddCharacterEntry(character, entry);
            __instance.AddEntry(entry);

            return false;
        }
    }

    [HarmonyPatch(typeof(GameConsoleEntry), nameof(GameConsoleEntry.ComputeHeight))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeHeight_Patch
    {
        [UsedImplicitly]
        private static void Prefix(
            GameConsoleEntry __instance,
            out List<OriginalTooltip> __state)
        {
            // BreakdownFragments copies these temporary values into non-serialized UI
            // fragments. The finalizer restores the record parameters before returning,
            // so saves and multiplayer record payloads keep the native representation.
            __state = PrepareLiveFriendlyMonsterTooltipParameters(__instance);
        }

        [UsedImplicitly]
        private static Exception Finalizer(
            Exception __exception,
            List<OriginalTooltip> __state)
        {
            RestoreLiveFriendlyMonsterTooltipParameters(__state);

            return __exception;
        }
    }

    [HarmonyPatch(typeof(GameConsoleEntry), nameof(GameConsoleEntry.AddParameter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameConsoleEntry_AddParameter_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            ParameterType type,
            string value,
            ref string tooltipContent,
            ref string tooltipClass)
        {
            if (type is not (ParameterType.AbilityInfo or ParameterType.AttackSpellPower) ||
                IsDisplayableTooltip(tooltipContent))
            {
                return;
            }

            ResolveParameterTooltip(
                type,
                value,
                ref tooltipContent,
                ref tooltipClass);
        }
    }

    [HarmonyPatch(typeof(GameConsole), nameof(GameConsole.AttackRolled))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AttackRolled_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: setup tooltip of a power passed to `GameConsole.AttackRolled`
            var method = new Action<GameRecordTable, GameRecordEntry, int, BaseDefinition>(AddEntry).Method;
            return instructions.ReplaceCall("AddEntry", -1, "GameConsole.AttackRolled",
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Call, method));
        }

        private static void AddEntry(GameRecordTable console, GameRecordEntry entry, int insertionIndex,
            BaseDefinition definition)
        {
            if (definition is FeatureDefinitionPower)
            {
                foreach (var parameter in entry.Parameters
                             .Where(parameter =>
                                 parameter.parameterType == (int)ParameterType.AttackSpellPower)
                             .Where(parameter =>
                                 string.IsNullOrEmpty(parameter.tooltipContent) &&
                                 string.IsNullOrEmpty(parameter.tooltipClass))
                             .Where(parameter => parameter.contentValue == definition.GuiPresentation.Title))
                {
                    parameter.tooltipContent = definition.Name;
                    parameter.tooltipClass = GuiPowerDefinition.TooltipClassPowerDefinition;
                }
            }

            console.AddEntry(entry, insertionIndex);
        }
    }

    [HarmonyPatch(typeof(GameConsole), nameof(GameConsole.DamageReduced))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DamageReduced_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameConsole __instance,
            RulesetActor character,
            FeatureDefinition feature,
            int reductionAmount)
        {
            //PATCH: allow damage reduction log to show damage types and show feature description on tooltip
            var prompt = "Feedback/&DamageReducedLine";
            var types = "";
            var typeNames = "";

            if (feature is FeatureDefinitionReduceDamage { DamageTypes.Count: > 0 } reduce)
            {
                prompt = Gui.Localize("Feedback/&DamageReducedLine").Replace("{2}", "{2}{3}");
                types = string.Join("", reduce.DamageTypes.Select(x => Gui.FormatDamageType(x)));
                typeNames = string.Join("\n", reduce.DamageTypes.Select(x => Gui.FormatDamageType(x, true)));
            }

            var entry = new GameConsoleEntry(prompt, __instance.consoleTableDefinition) { Indent = true };

            var titleKey = feature?.GuiPresentation?.Title;
            var title = !string.IsNullOrEmpty(titleKey) &&
                        TranslatorContext.HasTranslation(titleKey)
                ? Gui.Localize(titleKey)
                : feature?.Name ?? string.Empty;
            var descriptionKey = feature?.GuiPresentation?.Description;

            if (CustomTooltipProvider.IsUnavailableContent(descriptionKey) ||
                !TranslatorContext.HasTranslation(descriptionKey))
            {
                descriptionKey = null;
            }

            entry.AddParameter(
                ParameterType.AttackSpellPower,
                title,
                tooltipContent: descriptionKey);
            __instance.AddCharacterEntry(character, entry);
            entry.AddParameter(ParameterType.Positive, reductionAmount.ToString());
            entry.AddParameter(ParameterType.Initiative, types, tooltipContent: typeNames);

            __instance.AddEntry(entry);

            return false;
        }
    }


    [HarmonyPatch(typeof(GameConsole), nameof(GameConsole.ItemUsed))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ItemUsed_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameConsole __instance,
            RulesetCharacter character,
            RulesetItemDevice usableDevice,
            RulesetDeviceFunction deviceFunction,
            int additionalCharges)
        {
            var itemTitle = GuiItemTweaks.FormatTitle(usableDevice.ItemDefinition);
            if (deviceFunction.DeviceFunctionDescription.Type == DeviceFunctionDescription.FunctionType.Spell)
            {
                var entry = new GameConsoleEntry(GameConsole.ItemUsedSpellCastLine, __instance.consoleTableDefinition);
                __instance.AddCharacterEntry(character, entry);
                var spell = deviceFunction.DeviceFunctionDescription.SpellDefinition;

                entry.AddParameter(ParameterType.AttackSpellPower, itemTitle,
                    tooltipContent: usableDevice.ItemDefinition.Name,
                    tooltipClass: GuiItemDefinition.TooltipClassItemDefinition);
                entry.AddParameter(ParameterType.AttackSpellPower, spell.GuiPresentation.Title,
                    tooltipContent: spell.Name, tooltipClass: GuiSpellDefinition.TooltipClassSpellDefinition);
                __instance.AddEntry(entry);
            }
            else
            {
                var entry = new GameConsoleEntry(GameConsole.ItemUsedLine, __instance.consoleTableDefinition);
                __instance.AddCharacterEntry(character, entry);
                entry.AddParameter(ParameterType.AttackSpellPower, itemTitle,
                    tooltipContent: usableDevice.ItemDefinition.Name,
                    tooltipClass: GuiItemDefinition.TooltipClassItemDefinition);
                __instance.AddEntry(entry);
            }

            return false;
        }
    }

    private static void ResolveParameterTooltip(
        ParameterType type,
        string value,
        ref string tooltipContent,
        ref string tooltipClass)
    {
        try
        {
            ResolveParameterTooltipCore(
                type,
                value,
                ref tooltipContent,
                ref tooltipClass);
        }
        catch (Exception exception)
        {
            Trace.LogException(
                new Exception("Failed to resolve a game console tooltip.", exception));

            return;
        }

    }

    private static void ResolveParameterTooltipCore(
        ParameterType type,
        string value,
        ref string tooltipContent,
        ref string tooltipClass)
    {
        if (type == ParameterType.AbilityInfo &&
            TryResolveAbilityDefinition(value, out var definition) &&
            TryGetDefinitionDescription(definition, out var description))
        {
            tooltipContent = description;
            tooltipClass = null;

            return;
        }

        if (type == ParameterType.AttackSpellPower)
        {
            var definitionResolution = ResolveDefinitionTooltipByTitle(
                value,
                ref tooltipContent,
                ref tooltipClass);

            if (definitionResolution is not DefinitionTitleResolution.Unresolved)
            {
                return;
            }
        }

        if (type == ParameterType.AttackSpellPower &&
            TryResolveLocalizedDescription(value, out var localizedDescription))
        {
            tooltipContent = localizedDescription;
            tooltipClass = null;
        }
    }

    private static void ResolveFragmentTooltip(
        ParameterType? parameterType,
        string value,
        ref string tooltipContent,
        ref string tooltipClass)
    {
        if (IsDisplayableTooltip(tooltipContent))
        {
            return;
        }

        if (parameterType is not { } type)
        {
            return;
        }

        ResolveParameterTooltip(
            type,
            value,
            ref tooltipContent,
            ref tooltipClass);
    }

    private static ParameterType? GetParameterType(
        TextBreaker.FragmentInfo fragmentInfo,
        GameRecordEntry entry)
    {
        if (entry is not GameConsoleEntry consoleEntry)
        {
            return null;
        }

        var table = consoleEntry.ConsoleTableDefinition;

        if (ReferenceEquals(
                fragmentInfo.style,
                table.GetStyle((int)ParameterType.AbilityInfo)))
        {
            return ParameterType.AbilityInfo;
        }

        return ReferenceEquals(
            fragmentInfo.style,
            table.GetStyle((int)ParameterType.AttackSpellPower))
            ? ParameterType.AttackSpellPower
            : null;
    }

    private static bool TryResolveAbilityDefinition(
        string value,
        out BaseDefinition definition)
    {
        if (IsPresentationOnlyValue(value))
        {
            definition = null;

            return false;
        }

        definition = DatabaseRepository.GetDatabase<SkillDefinition>()
            .FirstOrDefault(skill =>
                string.Equals(
                    value,
                    Gui.FormatAbilityScoreAndProficiency(skill.AbilityScore, skill.Name),
                    StringComparison.Ordinal));

        if (definition != null)
        {
            return true;
        }

        foreach (var tool in DatabaseRepository.GetDatabase<ToolTypeDefinition>())
        {
            if (MatchesAbilityOrTool(value, tool.MainAbilityScore, tool.Name))
            {
                definition = tool;

                return true;
            }

            if (AllAbilityScores.Any(ability => MatchesAbilityOrTool(value, ability, tool.Name)))
            {
                definition = tool;

                return true;
            }
        }

        var attributes = DatabaseRepository.GetDatabase<SmartAttributeDefinition>();

        definition = AllAbilityScores
            .Select(attributeName => attributes.GetElement(attributeName))
            .FirstOrDefault(attribute =>
                attribute != null &&
                (string.Equals(
                     value,
                     Gui.FormatAbilityScoreLong(attribute.Name),
                     StringComparison.Ordinal) ||
                 MatchesAbilityOrTool(value, attribute.Name, string.Empty)));

        return definition != null;
    }

    private static DefinitionTitleResolution ResolveDefinitionTooltipByTitle(
        string title,
        ref string tooltipContent,
        ref string tooltipClass)
    {
        if (IsPresentationOnlyValue(title))
        {
            return DefinitionTitleResolution.Unresolved;
        }

        if (!DefinitionsByTitleCache.TryGetValue(title, out var definitions))
        {
            definitions =
            [
                .. FindDefinitionsByTitle<FeatureDefinitionPower>(title),
                .. FindDefinitionsByTitle<FeatureDefinition>(title),
                .. FindDefinitionsByTitle<FeatDefinition>(title),
                .. FindDefinitionsByTitle<InvocationDefinition>(title),
                .. FindDefinitionsByTitle<FightingStyleDefinition>(title),
                .. FindDefinitionsByTitle<MetamagicOptionDefinition>(title),
                .. FindDefinitionsByTitle<SpellDefinition>(title),
                .. FindDefinitionsByTitle<ItemDefinition>(title),
                .. FindDefinitionsByTitle<MonsterDefinition>(title),
                .. FindDefinitionsByTitle<ConditionDefinition>(title)
            ];
            definitions = definitions.Distinct().ToArray();

            if (DefinitionsByTitleCache.Count >= MaximumDefinitionTitleCacheEntries)
            {
                DefinitionsByTitleCache.Clear();
            }

            DefinitionsByTitleCache[title] = definitions;
        }

        if (definitions.Length == 1 &&
            TrySetDefinitionTooltip(definitions[0], ref tooltipContent, ref tooltipClass))
        {
            return DefinitionTitleResolution.Resolved;
        }

        if (definitions.Length <= 1)
        {
            return DefinitionTitleResolution.Unresolved;
        }

        // A localized title cannot identify a definition type when several definitions
        // reuse the same GuiPresentation. Only expose a common description in that case;
        // never guess a typed data provider from database ordering.
        var descriptions = new List<string>(definitions.Length);

        foreach (var definition in definitions)
        {
            if (!TryGetDefinitionDescription(definition, out var description))
            {
                return DefinitionTitleResolution.Ambiguous;
            }

            descriptions.Add(description);
        }

        var sharedDescriptions = descriptions
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (sharedDescriptions.Length != 1)
        {
            return DefinitionTitleResolution.Ambiguous;
        }

        tooltipContent = sharedDescriptions[0];
        tooltipClass = null;

        return DefinitionTitleResolution.Resolved;
    }

    private static IEnumerable<T> FindDefinitionsByTitle<T>(string title)
        where T : BaseDefinition
    {
        if (string.IsNullOrEmpty(title))
        {
            return [];
        }

        return DatabaseRepository.GetDatabase<T>()
            .Where(candidate => MatchesDefinitionTitle(candidate, title));
    }

    private static bool TrySetDefinitionTooltip(
        BaseDefinition definition,
        ref string tooltipContent,
        ref string tooltipClass)
    {
        if (!TryGetDefinitionDescription(definition, out var description))
        {
            return false;
        }

        switch (definition)
        {
            case SpellDefinition:
                tooltipContent = definition.Name;
                tooltipClass = GuiSpellDefinition.TooltipClassSpellDefinition;
                break;
            case FeatureDefinitionPower:
                tooltipContent = definition.Name;
                tooltipClass = GuiPowerDefinition.TooltipClassPowerDefinition;
                break;
            case ItemDefinition:
                tooltipContent = definition.Name;
                tooltipClass = GuiItemDefinition.TooltipClassItemDefinition;
                break;
            case MonsterDefinition:
                tooltipContent = definition.Name;
                tooltipClass = GuiMonsterDefinition.TooltipClassMonsterDefinition;
                break;
            default:
                tooltipContent = description;
                tooltipClass = null;
                break;
        }

        return true;
    }

    private static bool MatchesDefinitionTitle(BaseDefinition definition, string title)
    {
        var titleKey = definition?.GuiPresentation?.Title;

        if (CustomTooltipProvider.IsUnavailableContent(titleKey))
        {
            return false;
        }

        if (string.Equals(titleKey, title, StringComparison.Ordinal))
        {
            return true;
        }

        return TranslatorContext.HasTranslation(titleKey) &&
               string.Equals(Gui.Localize(titleKey), title, StringComparison.Ordinal);
    }

    private static bool TryGetDefinitionDescription(
        BaseDefinition definition,
        out string description)
    {
        var descriptionKey = definition?.GuiPresentation?.Description;

        if (CustomTooltipProvider.IsUnavailableContent(descriptionKey) ||
            !TranslatorContext.HasTranslation(descriptionKey))
        {
            description = null;

            return false;
        }

        description = definition.FormatDescription();

        return IsDisplayableTooltip(description);
    }

    private static bool IsPresentationOnlyValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.All(character =>
                   char.IsDigit(character) ||
                   char.IsWhiteSpace(character) ||
                   character is
                       '+' or '-' or '−' or '.' or ',' or '/' or
                       '×' or 'x' or 'X' or 'd' or 'D' or '(' or ')');
    }

    private static bool MatchesAbilityOrTool(
        string value,
        string ability,
        string proficiency)
    {
        return !string.IsNullOrEmpty(ability) &&
               string.Equals(
                   value,
                   Gui.FormatAbilityScoreAndProficiency(ability, proficiency),
                   StringComparison.Ordinal);
    }

    private static bool TryResolveLocalizedDescription(
        string titleKey,
        out string description)
    {
        description = null;

        if (CustomTooltipProvider.IsUnavailableContent(titleKey))
        {
            return false;
        }

        var titleIndex = titleKey.LastIndexOf("Title", StringComparison.Ordinal);

        if (titleIndex < 0)
        {
            return false;
        }

        var suffix = titleKey.Substring(titleIndex + "Title".Length);

        if (suffix.Any(character => !char.IsDigit(character)))
        {
            return false;
        }

        var prefix = titleKey.Substring(0, titleIndex);
        var candidates = string.IsNullOrEmpty(suffix)
            ? new[] { $"{prefix}Description" }
            : new[] { $"{prefix}Description{suffix}", $"{prefix}Description" };

        foreach (var candidate in candidates)
        {
            if (!TranslatorContext.HasTranslation(candidate))
            {
                continue;
            }

            var localized = Gui.Localize(candidate);

            if (!IsDisplayableTooltip(localized))
            {
                continue;
            }

            description = localized;

            return true;
        }

        return false;
    }

    private static bool IsDisplayableTooltip(string content)
    {
        if (CustomTooltipProvider.IsUnavailableContent(content))
        {
            return false;
        }

        for (var index = 0; index <= 9; index++)
        {
            if (content.Contains($"{{{index}}}"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDisplayableBoundTooltip(GuiTooltip tooltip)
    {
        if (!IsDisplayableTooltip(tooltip?.Content))
        {
            return false;
        }

        if (string.Equals(
                tooltip.TooltipClass,
                LiveFriendlyMonsterTooltipClass,
                StringComparison.Ordinal))
        {
            return false;
        }

        return tooltip.TooltipClass switch
        {
            GuiSpellDefinition.TooltipClassSpellDefinition or
                GuiPowerDefinition.TooltipClassPowerDefinition or
                GuiItemDefinition.TooltipClassItemDefinition or
                GuiMonsterDefinition.TooltipClassMonsterDefinition =>
                tooltip.DataProvider != null,
            _ => true
        };
    }

    private static bool TryGetLiveFriendlyMonsterTooltipCharacter(
        GameConsoleEntry entry,
        string token,
        out RulesetCharacterMonster monster)
    {
        monster = null;

        if (string.IsNullOrEmpty(token) ||
            !token.StartsWith(
                LiveFriendlyMonsterTooltipTokenPrefix,
                StringComparison.Ordinal) ||
            !LiveFriendlyMonsterTooltipBindings.TryGetValue(entry, out var bindings) ||
            !bindings.TryGetValue(token, out var binding))
        {
            return false;
        }

        return binding.TryGetCharacter(out monster) &&
               monster is { Side: RuleDefinitions.Side.Ally };
    }

    private static List<OriginalTooltip> PrepareLiveFriendlyMonsterTooltipParameters(
        GameConsoleEntry entry)
    {
        if (entry == null ||
            !LiveFriendlyMonsterTooltipBindings.TryGetValue(entry, out var bindings))
        {
            return null;
        }

        var originals = new List<OriginalTooltip>(bindings.Count);

        try
        {
            foreach (var binding in bindings)
            {
                var parameterIndex = binding.Value.ParameterIndex;

                if (parameterIndex < 0 ||
                    parameterIndex >= entry.Parameters.Count)
                {
                    continue;
                }

                var parameter = entry.Parameters[parameterIndex];

                if (!string.Equals(
                        parameter.tooltipClass,
                        GuiMonsterDefinition.TooltipClassMonsterDefinition,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                originals.Add(
                    new OriginalTooltip(
                        parameter,
                        parameter.tooltipContent,
                        parameter.tooltipClass));
                parameter.tooltipContent = binding.Key;
                parameter.tooltipClass = LiveFriendlyMonsterTooltipClass;
            }

            return originals;
        }
        catch (Exception exception)
        {
            RestoreLiveFriendlyMonsterTooltipParameters(originals);
            Trace.LogException(
                new Exception("Failed to bind a live friendly monster tooltip.", exception));

            return null;
        }
    }

    private static void RestoreLiveFriendlyMonsterTooltipParameters(
        IEnumerable<OriginalTooltip> originals)
    {
        if (originals == null)
        {
            return;
        }

        foreach (var original in originals)
        {
            original.Parameter.tooltipContent = original.Content;
            original.Parameter.tooltipClass = original.TooltipClass;
        }
    }

    private static readonly string[] AllAbilityScores =
    [
        AttributeDefinitions.Strength,
        AttributeDefinitions.Dexterity,
        AttributeDefinitions.Constitution,
        AttributeDefinitions.Intelligence,
        AttributeDefinitions.Wisdom,
        AttributeDefinitions.Charisma
    ];

    private const int MaximumDefinitionTitleCacheEntries = 256;
    private const string LiveFriendlyMonsterTooltipClass = "UBLiveFriendlyMonster";
    private const string LiveFriendlyMonsterTooltipTokenPrefix = "UBLiveFriendlyMonster:";

    private static readonly Dictionary<string, BaseDefinition[]> DefinitionsByTitleCache =
        new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<
        GameConsoleEntry,
        Dictionary<string, LiveFriendlyMonsterConsoleBinding>> LiveFriendlyMonsterTooltipBindings = new();

    private readonly struct OriginalTooltip(
        TextParameter parameter,
        string content,
        string tooltipClass)
    {
        internal TextParameter Parameter { get; } = parameter;
        internal string Content { get; } = content;
        internal string TooltipClass { get; } = tooltipClass;
    }

    private readonly struct LiveFriendlyMonsterConsoleBinding
    {
        private readonly WeakReference<RulesetCharacterMonster> character;

        internal LiveFriendlyMonsterConsoleBinding(
            int parameterIndex,
            RulesetCharacterMonster character)
        {
            ParameterIndex = parameterIndex;
            this.character = new WeakReference<RulesetCharacterMonster>(character);
        }

        internal int ParameterIndex { get; }

        internal bool TryGetCharacter(out RulesetCharacterMonster monster)
        {
            return character.TryGetTarget(out monster);
        }
    }

    private enum DefinitionTitleResolution
    {
        Unresolved,
        Resolved,
        Ambiguous
    }
}
