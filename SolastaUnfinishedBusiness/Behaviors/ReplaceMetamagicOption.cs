using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;

namespace SolastaUnfinishedBusiness.Behaviors;

public class ReplaceMetamagicOption
{
    private readonly List<MetamagicOptionDefinition> _options = [];

    public ReplaceMetamagicOption(params MetamagicOptionDefinition[] options)
    {
        _options.AddRange(options);
    }

    private static List<MetamagicOptionDefinition> GetOptions(RulesetCharacterHero hero)
    {
        return ApplyReplacements(hero?.TrainedMetamagicOptions);
    }

    internal static List<MetamagicOptionDefinition> GetOptions(RulesetCharacter character)
    {
        if (character is RulesetCharacterHero hero)
        {
            return GetOptions(hero);
        }

        if (character is not RulesetCharacterSimulacrum &&
            character?.OriginalFormCharacter is not RulesetCharacterSimulacrum)
        {
            return [];
        }

        return ApplyReplacements(
            SimulacrumBehavior.EnumerateTrainedMetamagicOptions(character).ToList());
    }

    private static List<MetamagicOptionDefinition> ApplyReplacements(
        List<MetamagicOptionDefinition> sourceOptions)
    {
        List<MetamagicOptionDefinition> list = null;
        var options = sourceOptions?
            .Where(option => option != null)
            .ToList() ?? [];

        foreach (var option in options)
        {
            var replacer = option.GetFirstSubFeatureOfType<ReplaceMetamagicOption>();

            if (replacer == null)
            {
                continue;
            }

            list ??= [..options];
            list.Remove(option);
            list.AddRange(replacer._options.Where(replacement => replacement != null));
        }

        return list ??
               (sourceOptions != null && options.Count == sourceOptions.Count
                   ? sourceOptions
                   : options);
    }

    public static IEnumerable<CodeInstruction> PatchMetamagicGetter(IEnumerable<CodeInstruction> instructions,
        string context)
    {
        var getter = typeof(RulesetCharacterHero)
            .GetProperty(nameof(RulesetCharacterHero.TrainedMetamagicOptions))
            ?.GetGetMethod();

        var hidden = typeof(GuiPresentation)
            .GetProperty(nameof(GuiPresentation.Hidden))
            ?.GetGetMethod();

        var customHidden = new Func<GuiPresentation, bool>(Hidden).Method;
        var customGetter = new Func<RulesetCharacterHero, List<MetamagicOptionDefinition>>(GetOptions).Method;

        return instructions
            //Replace getter with custom one that changes metamagic options
            .ReplaceCalls(getter, context + ".Getter", new CodeInstruction(OpCodes.Call, customGetter))
            //Ensure hidden metamagic are processed
            .ReplaceCalls(hidden, context + ".Hidden", new CodeInstruction(OpCodes.Call, customHidden));
    }

    internal static IEnumerable<CodeInstruction> PatchMetamagicGetterFromCharacter(
        IEnumerable<CodeInstruction> instructions,
        string context,
        params CodeInstruction[] characterLoader)
    {
        var getter = typeof(RulesetCharacterHero)
            .GetProperty(nameof(RulesetCharacterHero.TrainedMetamagicOptions))
            ?.GetGetMethod();

        var hidden = typeof(GuiPresentation)
            .GetProperty(nameof(GuiPresentation.Hidden))
            ?.GetGetMethod();

        var customHidden = new Func<GuiPresentation, bool>(Hidden).Method;
        var customGetter =
            new Func<RulesetCharacter, List<MetamagicOptionDefinition>>(GetOptions).Method;
        var replacement = new List<CodeInstruction>
        {
            new(OpCodes.Pop)
        };

        replacement.AddRange(characterLoader);
        replacement.Add(new CodeInstruction(OpCodes.Call, customGetter));

        return instructions
            .ReplaceCalls(getter, context + ".Getter", replacement.ToArray())
            .ReplaceCalls(hidden, context + ".Hidden", new CodeInstruction(OpCodes.Call, customHidden));
    }

    private static bool Hidden(GuiPresentation gui)
    {
        return false;
    }
}
