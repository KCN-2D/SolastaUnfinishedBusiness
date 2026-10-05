using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Validators;
using TA;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Subclasses.Builders.MetamagicBuilders;

namespace SolastaUnfinishedBusiness.Models;

internal static class MetamagicContext
{
    internal const string FeatMetamagicAdeptPointPoolTag = "PointPoolFeatMetamagicAdept";

    internal static HashSet<MetamagicOptionDefinition> Metamagic { get; private set; } = [];

    private const string MetamagicSeekingSpell = "MetamagicSeekingSpell";
    private const string MetamagicCarefulSpell = "MetamagicCarefullSpell";
    private const string MetamagicExtendedSpell = "MetamagicExtendedSpell";
    private const string MetamagicHeightenedSpell = "MetamagicHeightenedSpell";
    private const string MetamagicQuickenedSpell = "MetamagicQuickenedSpell";
    private const string MetamagicTwinnedSpell = "MetamagicTwinnedSpell";
    private const string MetamagicSeekingSpellDescription = "Feature/&MetamagicSeekingSpellDescription";
    private const string MetamagicSeekingSpell2024Description = "Feature/&MetamagicSeekingSpell2024Description";
    private const string MetamagicOptionExtendedSpellTitle = "Rules/&MetamagicOptionExtendedSpellTitle";
    private const string MetamagicCarefulSpell2024Description =
        "Rules/&MetamagicOptionCarefulSpell2024Description";
    private const string MetamagicExtendedSpell2024Description =
        "Rules/&MetamagicOptionExtendedSpell2024Description";
    private const string MetamagicQuickenedSpell2024Description =
        "Rules/&MetamagicOptionQuickenedSpell2024Description";
    private const string MetamagicTwinnedSpell2024Description =
        "Rules/&MetamagicOptionTwinnedSpell2024Description";
    private const string LeveledSpellCastThisTurn = "Metamagic2024LeveledSpellCastThisTurn";
    private const string QuickenedSpellCastThisTurn = "Metamagic2024QuickenedSpellCastThisTurn";
    private const string ConditionCarefulSpell2024 = "ConditionMetamagicCarefulSpell2024Protected";
    private const string ConditionExtendedSpell2024 = "ConditionMetamagicExtendedSpell2024Concentration";
    internal const string FailureFlagTwinnedSpell2024InvalidTargetAdvancement =
        "Failure/&FailureFlagTwinnedSpell2024InvalidTargetAdvancement";
    internal const string FailureFlagQuickenedSpell2024AlreadyCastLeveledSpell =
        "Failure/&FailureFlagQuickenedSpell2024AlreadyCastLeveledSpell";
    internal const string FailureFlagQuickenedSpell2024CantripsOnly =
        "Failure/&FailureFlagQuickenedSpell2024CantripsOnly";

    private static readonly Dictionary<string, MetamagicCostState> LegacyCostStates = [];
    private static readonly Dictionary<string, string> LegacyDescriptionKeys = [];

    private static bool _rules2024SubFeaturesInstalled;
    private static ConditionDefinition _conditionCarefulSpell2024;
    private static ConditionDefinition _conditionExtendedSpell2024;

    internal static void LateLoad()
    {
        var metamagicOptions = new[]
        {
            BuildMetamagicAltruisticSpell(),
            BuildMetamagicFocusedSpell(),
            BuildMetamagicPowerfulSpell(),
            BuildMetamagicSeekingSpell(),
            BuildMetamagicTransmutedSpell(),
            BuildMetamagicWidenedSpell()
        };

        foreach (var metamagicOption in metamagicOptions)
        {
            LoadMetamagic(metamagicOption);
        }

        // sorting
        Metamagic = Metamagic.OrderBy(x => x.FormatTitle()).ToHashSet();

        // settings paring
        foreach (var name in Main.Settings.MetamagicEnabled
                     .Where(name => Metamagic.All(x => x.Name != name))
                     .ToArray())
        {
            Main.Settings.MetamagicEnabled.Remove(name);
        }
    }

    private static readonly ConditionalWeakTable<MetamagicSelectionPanel, SelectionFlow> Selections = new();
    private static readonly ConditionalWeakTable<RulesetEffectSpell, ReactionSelection> ReactionSelections = new();
    private static readonly ConditionalWeakTable<ReactionModal, ReactionSelection> ReactionModals = new();

    // The native reaction RPC already transports a suboption integer on every peer.
    // Keep its original choice in the low bits and encode a stable definition index above it.
    private const int ReactionMetamagicMarker = 1 << 30;
    private const int ReactionSuboptionBits = 12;
    private const int ReactionSuboptionMask = (1 << ReactionSuboptionBits) - 1;

    private sealed class ReactionSelection(
        ReactionRequest request, CharacterActionParams preview, ReactionModal modal,
        MetamagicSelectionPanel panel, int suboption, Action confirmed)
    {
        internal ReactionRequest Request { get; } = request;
        internal ReactionModal Modal { get; } = modal;
        internal MetamagicSelectionPanel Panel { get; } = panel;
        internal Action Confirmed { get; } = confirmed;
        internal int Suboption { get; } = suboption;
        internal CharacterActionParams Preview { get; } = preview;
    }

    private static bool IsReactionSpell(CharacterActionParams actionParams) =>
        actionParams?.RulesetEffect is RulesetEffectSpell &&
        actionParams.ActionDefinition?.Id is ActionDefinitions.Id.CastReaction or ActionDefinitions.Id.CastReadied;

    internal static bool TrySelectReactionMetamagic(
        ReactionRequest request, ReactionModal modal, int suboption, Action confirmed = null)
    {
        if (request == null || request.Processed || request.Automated ||
            request.Character?.RulesetCharacter is not { } caster ||
            ReplaceMetamagicOption.GetSelectionOptions(caster).Count == 0)
        {
            return false;
        }

        if (!TryBuildReactionPreview(request, suboption, out var preview) ||
            preview.RulesetEffect is not RulesetEffectSpell { MetamagicOption: null } spell)
        {
            return false;
        }

        var panel = Gui.GuiService.GetScreen<MetamagicSelectionPanel>();
        if (!panel || panel.Visible || ReactionModals.TryGetValue(modal, out _))
        {
            return false;
        }

        var selection = new ReactionSelection(request, preview, modal, panel, suboption, confirmed);
        ReactionSelections.Remove(spell);
        ReactionSelections.Add(spell, selection);
        var service = ServiceRepository.GetService<IRulesetImplementationService>();
        if (!ReplaceMetamagicOption.GetSelectionOptions(caster)
                .Any(option => IsSelectionOptionAvailable(service, spell, caster, option, out _, out _)))
        {
            ReactionSelections.Remove(spell);
            return false;
        }

        panel.Unbind();
        ReactionModals.Add(modal, selection);
        panel.Bind(request.Character, spell,
            (_, _, option) => CompleteReactionSelection(selection, option),
            () => CompleteReactionSelection(selection, null));
        // Keep the native modal alive: hiding it would release its requests and resume game time.
        modal.mainPanel.Hide(true);
        panel.Show(true);
        return true;
    }

    private static void CompleteReactionSelection(ReactionSelection selection, MetamagicOptionDefinition option)
    {
        var request = selection.Request;
        var modal = selection.Modal;
        ClearReactionSelection(selection);
        if (modal && modal.Visible)
        {
            modal.mainPanel.Show(true);
        }

        if (!request.Processed && request.IsStillValid &&
            selection.Preview.RulesetEffect is RulesetEffectSpell spell &&
            IsReactionOptionAvailable(request, spell, option, out _, selection.Preview))
        {
            var encoded = EncodeReactionSelection(selection.Suboption, option);
            if (encoded != int.MinValue)
            {
                selection.Confirmed?.Invoke();
                ServiceRepository.GetService<ICommandService>().ProcessReactionRequest(request, true, encoded);
            }
        }

        // Ignoring metamagic while outside the normal range returns to the pending reaction.
        modal?.CheckPanelRelevance();
    }

    private static void ClearReactionSelection(ReactionSelection selection)
    {
        if (selection.Preview.RulesetEffect is RulesetEffectSpell spell)
        {
            ReactionSelections.Remove(spell);
        }
        ReactionModals.Remove(selection.Modal);
    }

    private static bool TryBuildReactionPreview(
        ReactionRequest request, int suboption, out CharacterActionParams preview)
    {
        preview = null;
        var parameters = request.ReactionParams;
        var caster = request.Character.RulesetCharacter;
        var choice = suboption >= 0 ? suboption : request.SelectedSubOption;
        if (suboption < -1 || suboption >= request.SubOptionsAvailability.Count ||
            choice >= 0 && request.SubOptionsAvailability.Count > 0 &&
            (choice >= request.SubOptionsAvailability.Count || !request.SubOptionsAvailability.ElementAt(choice).Value))
        {
            return false;
        }

        SpellDefinition definition;
        RulesetSpellRepertoire repertoire;
        int level;
        if (request is ReactionRequestWarcaster warcaster)
        {
            if (!warcaster.TryGetSpellChoice(choice, out definition, out repertoire))
            {
                return false;
            }
            level = definition.SpellLevel;
        }
        else if (IsReactionSpell(parameters) && parameters.RulesetEffect is RulesetEffectSpell original)
        {
            if (original.MetamagicOption != null)
            {
                return false;
            }
            definition = original.SpellDefinition;
            repertoire = original.SpellRepertoire;
            level = original.SlotLevel;
            if (SpellCastingResourceContext.TryGetOption(request, choice, out var resource))
            {
                if (!resource.IsAvailable(caster))
                {
                    return false;
                }
                definition = resource.Spell;
                repertoire = resource.CastingRepertoire;
                level = resource.SlotLevel;
            }
            else if (suboption >= 0)
            {
                level = request is ReactionRequestSpendSpellSlotExtended
                    ? request.SubOptionsAvailability.ElementAt(choice).Key
                    : definition.SpellLevel + choice;
            }
        }
        else
        {
            return false;
        }

        var effect = new RulesetEffectSpell(caster, repertoire, definition, level);
        preview = new CharacterActionParams(parameters.ActingCharacter, ActionDefinitions.Id.CastReaction)
        {
            RulesetEffect = effect,
            SpellRepertoire = repertoire,
            IntParameter = level,
            StringParameter = definition.Name,
            IsReactionEffect = true,
            TargetAction = parameters.TargetAction
        };
        preview.TargetCharacters.AddRange(parameters.TargetCharacters);
        preview.ActionModifiers.AddRange(parameters.ActionModifiers.Select(modifier => modifier.Clone()));
        if (request is ReactionRequestWarcaster)
        {
            // Match native cantrip repetition while retaining the real request's weapon/effect unchanged.
            var targets = effect.ComputeTargetParameter();
            if (effect.EffectDescription.IsSingleTarget && targets > 0 && preview.TargetCharacters.Count > 0)
            {
                var target = preview.TargetCharacters[0];
                var modifier = preview.ActionModifiers.FirstOrDefault() ?? new ActionModifier();
                preview.TargetCharacters.Clear();
                preview.ActionModifiers.Clear();
                for (var index = 0; index < targets; index++)
                {
                    preview.TargetCharacters.Add(target);
                    preview.ActionModifiers.Add(modifier.Clone());
                }
            }
        }
        return true;
    }

    internal static void CancelReactionSelection(ReactionModal modal)
    {
        if (!ReactionModals.TryGetValue(modal, out var selection))
        {
            return;
        }
        ClearReactionSelection(selection);
        selection.Panel.Unbind();
        selection.Panel.Hide(true);
    }

    internal static IEnumerator PauseReactionTimer(IEnumerator values, ReactionModal modal)
    {
        try
        {
            while (true)
            {
                while (ReactionModals.TryGetValue(modal, out _))
                {
                    yield return null;
                }
                if (!values.MoveNext())
                {
                    yield break;
                }
                yield return values.Current;
            }
        }
        finally
        {
            (values as IDisposable)?.Dispose();
        }
    }

    private static MetamagicOptionDefinition[] GetReactionCommandOptions() =>
        DatabaseRepository.GetDatabase<MetamagicOptionDefinition>()
            .OrderBy(option => option.Name, StringComparer.Ordinal).ToArray();

    internal static int EncodeReactionSelection(int suboption, MetamagicOptionDefinition option)
    {
        if (option == null)
        {
            return suboption;
        }
        var index = Array.IndexOf(GetReactionCommandOptions(), option);
        if (suboption < -1 || suboption >= ReactionSuboptionMask || index < 0 ||
            index >= (ReactionMetamagicMarker >> ReactionSuboptionBits))
        {
            return int.MinValue;
        }
        return ReactionMetamagicMarker | (index << ReactionSuboptionBits) | (suboption + 1);
    }

    internal static void RestoreReactionSelection(ReactionRequest request, ref bool validated, ref int suboption)
    {
        if (suboption < -1)
        {
            validated = false;
            suboption = -1;
            return;
        }
        if (suboption >= 0 && (suboption & ReactionMetamagicMarker) != 0)
        {
            var index = (suboption & ~ReactionMetamagicMarker) >> ReactionSuboptionBits;
            var choice = (suboption & ReactionSuboptionMask) - 1;
            var options = GetReactionCommandOptions();
            suboption = -1;
            if (!validated || !IsReactionSpell(request.ReactionParams) && request is not ReactionRequestWarcaster ||
                index < 0 || index >= options.Length ||
                choice >= request.SubOptionsAvailability.Count ||
                choice >= 0 && !request.SubOptionsAvailability.ElementAt(choice).Value)
            {
                validated = false;
                return;
            }
            if (!TryBuildReactionPreview(request, choice, out var preview) ||
                preview.RulesetEffect is not RulesetEffectSpell previewSpell ||
                !IsReactionOptionAvailable(request, previewSpell, options[index], out _, preview))
            {
                validated = false;
                return;
            }
            if (choice >= 0)
            {
                request.SelectSubOption(choice);
            }
            else if (request.SubOptionsAvailability.Count > 0 &&
                     (request.SelectedSubOption < 0 ||
                      request.SelectedSubOption >= request.SubOptionsAvailability.Count ||
                      !request.SubOptionsAvailability.ElementAt(request.SelectedSubOption).Value))
            {
                validated = false;
                return;
            }
            if (request.ReactionParams.RulesetEffect is not RulesetEffectSpell spell ||
                !IsReactionOptionAvailable(request, spell, options[index], out _))
            {
                validated = false;
                return;
            }
            spell.MetamagicOption = options[index];
        }
        else if (validated && (IsReactionSpell(request.ReactionParams) || request is ReactionRequestWarcaster))
        {
            var parameters = TryBuildReactionPreview(request, suboption, out var preview)
                ? preview
                : request.ReactionParams;
            if (parameters.RulesetEffect is RulesetEffectSpell spell &&
                !AreReactionTargetsInRange(parameters, spell, spell.MetamagicOption))
            {
                validated = false;
                suboption = -1;
            }
        }
    }

    private static bool IsReactionOptionAvailable(
        ReactionRequest request, RulesetEffectSpell spell, MetamagicOptionDefinition option, out string failure,
        CharacterActionParams preview = null)
    {
        failure = string.Empty;
        if (option != null)
        {
            var caster = request.Character.RulesetCharacter;
            var known = ReplaceMetamagicOption.GetOptions(caster);
            if (CombinedMetamagic.HasType(option, MetamagicType.QuickenedSpell) ||
                CombinedMetamagic.Enumerate(option).Any(component => !known.Contains(component)) ||
                !ServiceRepository.GetService<IRulesetImplementationService>()
                    .IsMetamagicOptionAvailable(spell, caster, option, out failure, out _))
            {
                failure = string.IsNullOrEmpty(failure) ? "Failure/&FailureFlagInvalidSpellActionType" : failure;
                return false;
            }

            var previous = spell.MetamagicOption;
            var original = spell.EffectDescription;
            try
            {
                spell.MetamagicOption = option;
                var modified = spell.EffectDescription;
                if (modified.TargetType != original.TargetType || modified.TargetSide != original.TargetSide ||
                    modified.TargetExcludeCaster != original.TargetExcludeCaster ||
                    spell.ComputeTargetParameter() > (preview ?? request.ReactionParams).TargetCharacters.Count &&
                    CombinedMetamagic.HasType(option, MetamagicType.TwinnedSpell))
                {
                    failure = "Failure/&FailureFlagInvalidSingleTarget";
                    return false;
                }
            }
            finally
            {
                spell.MetamagicOption = previous;
            }
        }
        if (AreReactionTargetsInRange(preview ?? request.ReactionParams, spell, option))
        {
            return true;
        }
        failure = "Failure/&FailureFlagTargetOutOfRange";
        return false;
    }

    internal static int GetSpellRange(EffectDescription effect, MetamagicOptionDefinition option = null)
    {
        if (effect.RangeType == RangeType.Self)
        {
            return 0;
        }
        var touch = effect.RangeType is RangeType.Touch or RangeType.MeleeHit;
        if (CombinedMetamagic.HasType(option, MetamagicType.DistantSpell))
        {
            return touch ? CombinedMetamagic.GetDistantRange(option) : effect.RangeParameter * 2;
        }
        return touch ? Math.Max(1, effect.RangeParameter) : effect.RangeParameter;
    }

    internal static bool AreReactionTargetsInRange(
        CharacterActionParams actionParams, RulesetEffectSpell spell, MetamagicOptionDefinition option)
    {
        var caster = actionParams.ActingCharacter;
        var previous = spell.MetamagicOption;
        try
        {
            spell.MetamagicOption = option;
            var description = spell.EffectDescription;
            if (description.RangeType == RangeType.Self)
            {
                return true;
            }
            var range = GetSpellRange(description, option);
            return actionParams.TargetCharacters.All(target => target != null && caster.IsWithinRange(target, range));
        }
        finally
        {
            spell.MetamagicOption = previous;
        }
    }

    internal static int GetReactionSpellRange(SpellDefinition spell, GameLocationCharacter caster)
    {
        var range = GetSpellRange(spell.EffectDescription);
        var ruleCaster = caster?.RulesetCharacter;
        if (ruleCaster == null)
        {
            return range;
        }
        var slotLevel = ruleCaster.GetLowestSlotLevelAndRepertoireToCastSpell(spell, out var repertoire);
        if (repertoire == null || slotLevel < spell.SpellLevel)
        {
            return range;
        }
        // Construct a preview without registering an active effect or spending a resource.
        var effect = new RulesetEffectSpell(ruleCaster, repertoire, spell, slotLevel);
        range = GetSpellRange(effect.EffectDescription);
        var service = ServiceRepository.GetService<IRulesetImplementationService>();
        foreach (var option in ReplaceMetamagicOption.GetOptions(ruleCaster)
                     .Where(option => CombinedMetamagic.HasType(option, MetamagicType.DistantSpell)))
        {
            if (service.IsMetamagicOptionAvailable(effect, ruleCaster, option, out _, out _))
            {
                range = Math.Max(range, GetSpellRange(effect.EffectDescription, option));
            }
        }
        return range;
    }

    internal static bool CanReachReactionSpellTarget(
        GameLocationCharacter caster, SpellDefinition spell, GameLocationCharacter target) =>
        target != null && caster.IsWithinRange(target, GetReactionSpellRange(spell, caster));

    internal static bool CanAttackWithReactionMetamagic(
        GameLocationBattleManager battle, BattleDefinitions.AttackEvaluationParams attackParams, bool readiedAttack)
    {
        if (readiedAttack
                ? battle.IsValidAttackForReadiedAction(attackParams, false, CoverType.ThreeQuarter)
                : battle.CanAttack(attackParams, false))
        {
            return true;
        }
        if (attackParams.attacker?.RulesetCharacter is not { } caster ||
            !DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(attackParams.effectName, out var spell))
        {
            return false;
        }
        var slotLevel = caster.GetLowestSlotLevelAndRepertoireToCastSpell(spell, out var repertoire);
        if (repertoire == null || slotLevel < spell.SpellLevel)
        {
            return false;
        }
        var effect = new RulesetEffectSpell(caster, repertoire, spell, slotLevel);
        var service = ServiceRepository.GetService<IRulesetImplementationService>();
        foreach (var option in ReplaceMetamagicOption.GetOptions(caster)
                     .Where(option => CombinedMetamagic.HasType(option, MetamagicType.DistantSpell)))
        {
            if (!service.IsMetamagicOptionAvailable(effect, caster, option, out _, out _))
            {
                continue;
            }
            var preview = attackParams;
            preview.attackModifier = attackParams.attackModifier.Clone();
            preview.metamagicOption = option;
            preview.maxRange = GetSpellRange(effect.EffectDescription, option) + 0.1f;
            if (readiedAttack
                    ? battle.IsValidAttackForReadiedAction(preview, false, CoverType.ThreeQuarter)
                    : battle.CanAttack(preview, false))
            {
                return true;
            }
        }
        return false;
    }

    internal static IEnumerable<CodeInstruction> PatchReactionSpellRangeChecks(
        IEnumerable<CodeInstruction> instructions, MethodBase iterator, string casterFieldName = null)
    {
        var code = instructions.ToList();
        var spellDescription = AccessTools.PropertyGetter(typeof(SpellDefinition), nameof(SpellDefinition.EffectDescription));
        var rangeParameter = AccessTools.PropertyGetter(typeof(EffectDescription), nameof(EffectDescription.RangeParameter));
        var getCharacter = AccessTools.PropertyGetter(typeof(GameLocationCharacter), nameof(GameLocationCharacter.RulesetCharacter));
        var replacement = AccessTools.Method(typeof(MetamagicContext), nameof(GetReactionSpellRange));
        CodeInstruction casterLoad = null;
        var casterField = casterFieldName == null ? null : AccessTools.Field(iterator.DeclaringType, casterFieldName);
        var patched = 0;
        for (var index = 0; index < code.Count; index++)
        {
            if (casterField == null && code[index].operand is MethodInfo { Name: nameof(RulesetCharacter.CanCastCounterSpell) })
            {
                for (var previous = index - 1; previous >= Math.Max(1, index - 8); previous--)
                {
                    if (Equals(code[previous].operand, getCharacter))
                    {
                        casterLoad = code[previous - 1];
                        break;
                    }
                }
            }
            if (index == 0 || !Equals(code[index].operand, rangeParameter) ||
                !Equals(code[index - 1].operand, spellDescription) || casterLoad == null && casterField == null)
            {
                continue;
            }

            // Only spell ranges are replaced; power counters and spell identification keep their native rules.
            code[index - 1].opcode = casterField == null ? casterLoad.opcode : OpCodes.Ldarg_0;
            code[index - 1].operand = casterField == null ? casterLoad.operand : null;
            if (casterField != null)
            {
                code.Insert(index, new CodeInstruction(OpCodes.Ldfld, casterField));
                index++;
            }
            code[index].opcode = OpCodes.Call;
            code[index].operand = replacement;
            patched++;
        }
        if (patched != 1)
        {
            Main.Error($"Expected one reaction spell range check in {iterator.DeclaringType?.FullName}, found {patched}.");
        }
        return code;
    }

    private sealed class SelectionFlow(RulesetEffectSpell spell, Action back = null)
    {
        internal RulesetEffectSpell Spell { get; } = spell;
        internal Action Back { get; } = back;
        internal List<SelectionPage> Pages { get; } = [];
        internal SelectionPage Current => Pages[Pages.Count - 1];
    }

    private sealed class SelectionPage(
        MetamagicOptionDefinition first = null, MetamagicOptionDefinition replacement = null)
    {
        internal MetamagicOptionDefinition First { get; } = first;
        internal MetamagicOptionDefinition Replacement { get; } = replacement;
    }

    private static SelectionFlow GetSelection(MetamagicSelectionPanel panel)
    {
        if (!panel || !Selections.TryGetValue(panel, out var flow))
        {
            return null;
        }

        if (flow.Spell == panel.SpellEffect)
        {
            return flow;
        }

        Selections.Remove(panel);
        return null;
    }

    internal static MetamagicOptionDefinition GetFirstSelection(MetamagicSelectionPanel panel) =>
        GetSelection(panel)?.Current.First;

    internal static MetamagicOptionDefinition GetReplacementSelection(MetamagicSelectionPanel panel) =>
        GetSelection(panel)?.Current.Replacement;

    internal static List<MetamagicOptionDefinition> GetOptions(
        RulesetCharacter caster, MetamagicSelectionPanel panel)
    {
        var page = GetSelection(panel)?.Current;
        var options = page?.Replacement?.GetFirstSubFeatureOfType<ReplaceMetamagicOption>()?.Options.ToList() ??
                      ReplaceMetamagicOption.GetSelectionOptions(caster);
        if (page?.First is not { } first)
        {
            return options;
        }

        var pairs = options.Select(option => SorceryIncarnateContext.GetCombinedOption(first, option))
            .Where(option => option != null).ToList();
        if (page.Replacement == null)
        {
            pairs.Insert(0, first);
        }

        return pairs;
    }

    private static MetamagicOptionDefinition GetPendingReplacement(MetamagicOptionDefinition option) =>
        CombinedMetamagic.Enumerate(option).FirstOrDefault(component =>
            component.GetFirstSubFeatureOfType<ReplaceMetamagicOption>()?.RequiresSelection == true);

    // Family tiles preview concrete choices; only a resolved option reaches the casting action.
    internal static bool IsSelectionOptionAvailable(
        IRulesetImplementationService service, RulesetEffectSpell spell, RulesetCharacter caster,
        MetamagicOptionDefinition option, out string failure, out int cost)
    {
        var parent = GetPendingReplacement(option);
        if (parent == null)
        {
            var available = service.IsMetamagicOptionAvailable(spell, caster, option, out failure, out cost);
            return available && (!ReactionSelections.TryGetValue(spell, out var reaction) ||
                                 IsReactionOptionAvailable(reaction.Request, spell, option, out failure, reaction.Preview));
        }

        var first = CombinedMetamagic.Enumerate(option).FirstOrDefault(component => component != parent);
        failure = string.Empty;
        cost = 0;
        foreach (var child in parent.GetFirstSubFeatureOfType<ReplaceMetamagicOption>().Options)
        {
            var candidate = first == null ? child : SorceryIncarnateContext.GetCombinedOption(first, child);
            if (candidate != null && service.IsMetamagicOptionAvailable(spell, caster, candidate, out failure, out cost) &&
                (!ReactionSelections.TryGetValue(spell, out var reaction) ||
                 IsReactionOptionAvailable(reaction.Request, spell, candidate, out failure, reaction.Preview)))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool Select(MetamagicSelectionPanel panel, MetamagicOptionDefinition option)
    {
        var first = GetFirstSelection(panel);
        var service = ServiceRepository.GetService<IRulesetImplementationService>();
        if (option == first || !IsSelectionOptionAvailable(service, panel.SpellEffect,
                panel.Caster.RulesetCharacter, option, out _, out _))
        {
            return false;
        }

        if (GetPendingReplacement(option) is { } parent)
        {
            var flow = GetSelection(panel) ?? new SelectionFlow(panel.SpellEffect);
            if (flow.Pages.Count == 0)
            {
                flow.Pages.Add(new SelectionPage());
            }
            flow.Pages.Add(new SelectionPage(first, parent));
            Rebind(panel, flow);
            return false;
        }

        if (first != null)
        {
            Selections.Remove(panel);
            return true;
        }

        if (TrySelectAdditional(panel, panel.Caster, panel.SpellEffect, option,
                panel.MetamagicOptionSelected, panel.MetamagicOptionIgnored))
        {
            return false;
        }

        Selections.Remove(panel);
        return true;
    }

    internal static bool TrySelectAdditional(
        MetamagicSelectionPanel panel, GameLocationCharacter caster, RulesetEffectSpell spell,
        MetamagicOptionDefinition first,
        MetamagicSelectionPanel.MetamagicOptionSelectedHandler selected,
        MetamagicSelectionPanel.MetamagicOptionIgnoredHandler ignored, Action back = null)
    {
        if (!SorceryIncarnateContext.CanCombine(caster?.RulesetCharacter))
        {
            return false;
        }

        var service = ServiceRepository.GetService<IRulesetImplementationService>();
        if (!ReplaceMetamagicOption.GetOptions(caster.RulesetCharacter)
                .Select(candidate => SorceryIncarnateContext.GetCombinedOption(first, candidate))
                .Any(pair => pair != null && IsSelectionOptionAvailable(service,
                    spell, caster.RulesetCharacter, pair, out _, out _)))
        {
            return false;
        }

        var flow = GetSelection(panel) ?? new SelectionFlow(spell, back);
        if (flow.Pages.Count == 0 && back == null)
        {
            flow.Pages.Add(new SelectionPage());
        }
        flow.Pages.Add(new SelectionPage(first));
        panel.Unbind();
        Selections.Add(panel, flow);
        panel.Bind(caster, spell, selected, ignored);
        return true;
    }

    internal static void ConfirmFirstSelection(MetamagicSelectionPanel panel)
    {
        if (GetReplacementSelection(panel) != null || GetFirstSelection(panel) is not { } first)
        {
            return;
        }

        var service = ServiceRepository.GetService<IRulesetImplementationService>();
        if (!IsSelectionOptionAvailable(service, panel.SpellEffect, panel.Caster.RulesetCharacter, first, out _, out _))
        {
            return;
        }

        Selections.Remove(panel);
        panel.MetamagicOptionSelected?.Invoke(panel.Caster, panel.SpellEffect, first);
        panel.Hide();
    }

    internal static bool Ignore(MetamagicSelectionPanel panel)
    {
        var flow = GetSelection(panel);
        if (flow == null || (flow.Pages.Count == 1 && flow.Back == null))
        {
            Selections.Remove(panel);
            return true;
        }

        if (flow.Pages.Count == 1)
        {
            panel.Unbind();
            panel.Hide(true);
            flow.Back();
        }
        else
        {
            flow.Pages.RemoveAt(flow.Pages.Count - 1);
            Rebind(panel, flow);
        }
        return false;
    }

    private static void Rebind(MetamagicSelectionPanel panel, SelectionFlow flow)
    {
        var caster = panel.Caster;
        var spell = panel.SpellEffect;
        var selected = panel.MetamagicOptionSelected;
        var ignored = panel.MetamagicOptionIgnored;
        panel.Unbind();
        Selections.Add(panel, flow);
        panel.Bind(caster, spell, selected, ignored);
    }

    internal static void Unbind(MetamagicSelectionPanel panel) => Selections.Remove(panel);

    internal static void SwitchSorcererMetamagicRules2024()
    {
        EnsureRules2024SubFeatures();

        var enabled = Main.Settings.EnableSorcererMetamagic2024;

        if (TryGetMetamagic(MetamagicHeightenedSpell, out var heightenedSpell))
        {
            SetOrRestoreFixedCost(heightenedSpell, 2, enabled);
        }

        if (TryGetMetamagic(MetamagicTwinnedSpell, out var twinnedSpell))
        {
            SetOrRestoreFixedCost(twinnedSpell, 1, enabled);
        }

        if (TryGetMetamagic(MetamagicSeekingSpell, out var seekingSpell))
        {
            SetOrRestoreFixedCost(seekingSpell, GetSeekingSpellCost(), enabled);
            seekingSpell.GuiPresentation.description = enabled
                ? MetamagicSeekingSpell2024Description
                : MetamagicSeekingSpellDescription;
        }

        SetOrRestoreDescription(MetamagicCarefulSpell, MetamagicCarefulSpell2024Description, enabled);
        SetOrRestoreDescription(MetamagicExtendedSpell, MetamagicExtendedSpell2024Description, enabled);
        SetOrRestoreDescription(MetamagicQuickenedSpell, MetamagicQuickenedSpell2024Description, enabled);
        SetOrRestoreDescription(MetamagicTwinnedSpell, MetamagicTwinnedSpell2024Description, enabled);
    }

    internal static int GetSeekingSpellCost()
    {
        return Main.Settings.EnableSorcererMetamagic2024 ? 1 : 2;
    }

    internal static string GetSeekingSpellReactionDescription(string defenderName)
    {
        var key = Main.Settings.EnableSorcererMetamagic2024
            ? "CustomReactionMetamagicSeekingSpell2024Description"
            : "CustomReactionMetamagicSeekingSpellDescription";

        return key.Formatted(Category.Reaction, defenderName);
    }

    internal static bool HasLeveledSpellCastThisTurn(GameLocationCharacter character)
    {
        return Main.Settings.EnableSorcererMetamagic2024 &&
               IsCurrentTurn(character) &&
               character.UsedSpecialFeatures.ContainsKey(LeveledSpellCastThisTurn);
    }

    internal static bool HasQuickenedSpellCastThisTurn(GameLocationCharacter character)
    {
        return Main.Settings.EnableSorcererMetamagic2024 &&
               IsCurrentTurn(character) &&
               character.UsedSpecialFeatures.ContainsKey(QuickenedSpellCastThisTurn);
    }

    internal static bool CanUseQuickenedSpell2024(GameLocationCharacter character)
    {
        return !Main.Settings.EnableSorcererMetamagic2024 ||
               !HasLeveledSpellCastThisTurn(character);
    }

    internal static void RestrictToCantripsAfterQuickenedSpell2024(
        GameLocationCharacter character,
        ref bool cantripOnly)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            !HasQuickenedSpellCastThisTurn(character))
        {
            return;
        }

        cantripOnly = true;
    }

    internal static bool CanCastSpellWithQuickenedSpell2024Rules(
        SpellCastingValidationContext context,
        out string failure)
    {
        failure = string.Empty;

        if (!Main.Settings.EnableSorcererMetamagic2024)
        {
            return true;
        }

        var character = GameLocationCharacter.GetFromActor(context.Caster);
        var spell = context.ActiveSpell == null
            ? context.SpellDefinition
            : RulesetEffectSpellWithOrigin.GetOriginSpell(context.ActiveSpell);

        if (CombinedMetamagic.Contains(context.ActiveSpell?.MetamagicOption, MetamagicQuickenedSpell) &&
            HasLeveledSpellCastThisTurn(character))
        {
            failure = FailureFlagQuickenedSpell2024AlreadyCastLeveledSpell;
            return false;
        }

        if (spell?.SpellLevel <= 0 ||
            !HasQuickenedSpellCastThisTurn(character))
        {
            return true;
        }

        failure = FailureFlagQuickenedSpell2024CantripsOnly;
        return false;
    }

    internal static bool CanQueueReactionSpell2024(CharacterActionParams reactionParams)
    {
        if (reactionParams?.ActionDefinition?.Id != ActionDefinitions.Id.CastReaction ||
            reactionParams.RulesetEffect is not RulesetEffectSpell activeSpell)
        {
            return true;
        }

        return CanCastLeveledSpellAfterQuickenedSpell2024(
            reactionParams.ActingCharacter?.RulesetCharacter,
            RulesetEffectSpellWithOrigin.GetOriginSpell(activeSpell));
    }

    internal static bool CanCastLeveledSpellAfterQuickenedSpell2024(
        RulesetCharacter caster,
        SpellDefinition spellDefinition)
    {
        return !Main.Settings.EnableSorcererMetamagic2024 ||
               spellDefinition?.SpellLevel <= 0 ||
               !HasQuickenedSpellCastThisTurn(GameLocationCharacter.GetFromActor(caster));
    }

    internal static void MarkSpellCast2024(CharacterActionMagicEffect action)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            action.ActionParams?.RulesetEffect is not RulesetEffectSpell rulesetEffectSpell)
        {
            return;
        }

        var spell = RulesetEffectSpellWithOrigin.GetOriginSpell(rulesetEffectSpell);
        var actingCharacter = action.ActingCharacter;

        if (CombinedMetamagic.Contains(rulesetEffectSpell.MetamagicOption, MetamagicQuickenedSpell))
        {
            actingCharacter.UsedSpecialFeatures[QuickenedSpellCastThisTurn] = 1;
        }

        if (spell?.SpellLevel > 0)
        {
            actingCharacter.UsedSpecialFeatures[LeveledSpellCastThisTurn] = 1;
        }
    }

    internal static List<EffectForm> FilterCarefulSpell2024EffectForms(
        List<EffectForm> effectForms,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            _conditionCarefulSpell2024 == null ||
            formsParams.activeEffect is not RulesetEffectSpell rulesetEffectSpell ||
            !CombinedMetamagic.Contains(rulesetEffectSpell.MetamagicOption, MetamagicCarefulSpell) ||
            !formsParams.rolledSaveThrow ||
            formsParams.saveOutcome is not (RollOutcome.Success or RollOutcome.CriticalSuccess) ||
            !formsParams.targetCharacter.HasConditionOfCategoryAndType(
                AttributeDefinitions.TagEffect, _conditionCarefulSpell2024.Name))
        {
            return effectForms;
        }

        var filteredForms = effectForms
            .Where(effectForm =>
                effectForm.FormType != EffectForm.EffectFormType.Damage ||
                effectForm.SavingThrowAffinity != EffectSavingThrowType.HalfDamage)
            .ToList();

        return filteredForms.Count == effectForms.Count ? effectForms : filteredForms;
    }

    internal static bool TryHandleTwinnedSpell2024Availability(
        RulesetEffectSpell rulesetEffectSpell,
        MetamagicOptionDefinition metamagicOption,
        int remainingSorceryPoints,
        ref bool result,
        ref string failure)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            metamagicOption.Name != MetamagicTwinnedSpell ||
            remainingSorceryPoints < 1)
        {
            return false;
        }

        result = CanApplyTwinnedSpell2024(rulesetEffectSpell, false);
        failure = result ? string.Empty : FailureFlagTwinnedSpell2024InvalidTargetAdvancement;

        return true;
    }

    private static bool CanApplyTwinnedSpell2024(RulesetEffectSpell rulesetEffectSpell, bool requireSelectedMetamagic)
    {
        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            requireSelectedMetamagic &&
            !CombinedMetamagic.Contains(rulesetEffectSpell.MetamagicOption, MetamagicTwinnedSpell))
        {
            return false;
        }

        return ComputeTwinnedSpell2024AdditionalTargets(rulesetEffectSpell) > 0;
    }

    private static int ComputeTwinnedSpell2024AdditionalTargets(RulesetEffectSpell rulesetEffectSpell)
    {
        var effectDescription = rulesetEffectSpell.EffectDescription;

        return effectDescription is
               {
                   HasAdditionalSlotAdvancement: true,
                   TargetType: TargetType.Individuals or TargetType.IndividualsUnique
               }
            ? effectDescription.EffectAdvancement.ComputeAdditionalTargetsBySlotDelta(1)
            : 0;
    }

    private static void LoadMetamagic([NotNull] MetamagicOptionDefinition metamagicDefinition)
    {
        Metamagic.Add(metamagicDefinition);
        UpdateMetamagicVisibility(metamagicDefinition);
    }

    private static void UpdateMetamagicVisibility([NotNull] BaseDefinition metamagicDefinition)
    {
        metamagicDefinition.GuiPresentation.hidden =
            !Main.Settings.MetamagicEnabled.Contains(metamagicDefinition.Name);
    }

    internal static void SwitchMetamagic(MetamagicOptionDefinition metamagicDefinition, bool active)
    {
        if (!Metamagic.Contains(metamagicDefinition))
        {
            return;
        }

        var name = metamagicDefinition.Name;

        if (active)
        {
            Main.Settings.MetamagicEnabled.TryAdd(name);
        }
        else
        {
            Main.Settings.MetamagicEnabled.Remove(name);
        }

        UpdateMetamagicVisibility(metamagicDefinition);
    }

    internal static int CompareMetamagic(MetamagicOptionDefinition a, MetamagicOptionDefinition b)
    {
        var compare = Math.Max(a.SorceryPointsCost, 1) - Math.Max(b.SorceryPointsCost, 1);

        return compare == 0
            ? string.Compare(a.FormatTitle(), b.FormatTitle(), StringComparison.CurrentCultureIgnoreCase)
            : compare;
    }

    internal static bool IsVisibleMetamagicOption(MetamagicOptionDefinition option)
    {
        return option is { GuiPresentation.Hidden: false };
    }

    internal static List<MetamagicOptionDefinition> GetVisibleMetamagicOptions()
    {
        var metamagicDatabase = DatabaseRepository.GetDatabase<MetamagicOptionDefinition>();

        if (metamagicDatabase == null)
        {
            return [];
        }

        return metamagicDatabase
            .GetAllElements()
            .Where(IsVisibleMetamagicOption)
            .OrderBy(x => x, Comparer<MetamagicOptionDefinition>.Create(CompareMetamagic))
            .ToList();
    }

    internal static List<MetamagicOptionDefinition> GetRestrictedVisibleMetamagicOptions(
        IReadOnlyCollection<string> restrictedChoices)
    {
        var metamagicOptions = GetVisibleMetamagicOptions();

        if (restrictedChoices is not { Count: > 0 })
        {
            return metamagicOptions;
        }

        var restrictedChoiceNames = restrictedChoices.ToHashSet(StringComparer.Ordinal);

        return metamagicOptions
            .Where(option => restrictedChoiceNames.Contains(option.Name))
            .ToList();
    }

    private static void EnsureRules2024SubFeatures()
    {
        if (_rules2024SubFeaturesInstalled)
        {
            return;
        }

        _rules2024SubFeaturesInstalled = true;
        _conditionCarefulSpell2024 = BuildCarefulSpell2024Condition();
        _conditionExtendedSpell2024 = BuildExtendedSpell2024Condition();

        if (TryGetMetamagic(MetamagicCarefulSpell, out var carefulSpell))
        {
            carefulSpell.AddCustomSubFeatures(new CarefulSpell2024Behavior(_conditionCarefulSpell2024));
        }

        if (TryGetMetamagic(MetamagicQuickenedSpell, out var quickenedSpell))
        {
            quickenedSpell.AddCustomSubFeatures(
                new ValidateMetamagicApplication(ValidateQuickenedSpell2024));
        }

        if (TryGetMetamagic(MetamagicExtendedSpell, out var extendedSpell))
        {
            extendedSpell.AddCustomSubFeatures(new ExtendedSpell2024Behavior(_conditionExtendedSpell2024));
        }

        MetamagicOptionDefinitions.MetamagicTwinnedSpell.AddCustomSubFeatures(
            new ValidateMetamagicApplication(ValidateTwinnedSpell2024));
    }

    private static ConditionDefinition BuildCarefulSpell2024Condition()
    {
        return ConditionDefinitionBuilder
            .Create(ConditionCarefulSpell2024)
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddToDB();
    }

    private static ConditionDefinition BuildExtendedSpell2024Condition()
    {
        var magicAffinity = FeatureDefinitionMagicAffinityBuilder
            .Create("MagicAffinityMetamagicExtendedSpell2024Concentration")
            .SetGuiPresentation(GuiPresentationBuilder.Build(
                MetamagicOptionExtendedSpellTitle,
                MetamagicExtendedSpell2024Description,
                hidden: true))
            .SetConcentrationModifiers(ConcentrationAffinity.Advantage)
            .AddToDB();

        return ConditionDefinitionBuilder
            .Create(ConditionExtendedSpell2024)
            .SetGuiPresentation(GuiPresentationBuilder.Build(
                MetamagicOptionExtendedSpellTitle,
                MetamagicExtendedSpell2024Description,
                hidden: true))
            .SetPossessive()
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddFeatures(magicAffinity)
            .AddToDB();
    }

    private static void ValidateTwinnedSpell2024(
        RulesetCharacter caster,
        RulesetEffectSpell rulesetEffectSpell,
        MetamagicOptionDefinition metamagicOption,
        ref bool result,
        ref string failure)
    {
        _ = caster;
        _ = metamagicOption;

        if (!Main.Settings.EnableSorcererMetamagic2024)
        {
            return;
        }

        result = CanApplyTwinnedSpell2024(rulesetEffectSpell, false);
        failure = result ? string.Empty : FailureFlagTwinnedSpell2024InvalidTargetAdvancement;
    }

    private static void ValidateQuickenedSpell2024(
        RulesetCharacter caster,
        RulesetEffectSpell rulesetEffectSpell,
        MetamagicOptionDefinition metamagicOption,
        ref bool result,
        ref string failure)
    {
        _ = rulesetEffectSpell;

        if (!Main.Settings.EnableSorcererMetamagic2024 ||
            metamagicOption.Name != MetamagicQuickenedSpell ||
            CanUseQuickenedSpell2024(GameLocationCharacter.GetFromActor(caster)))
        {
            return;
        }

        result = false;
        failure = FailureFlagQuickenedSpell2024AlreadyCastLeveledSpell;
    }

    private static bool IsCurrentTurn(GameLocationCharacter character)
    {
        return character != null &&
               ServiceRepository.GetService<IGameLocationBattleService>() is
               { IsBattleInProgress: true, Battle: { } battle } &&
               ReferenceEquals(battle.ActiveContender, character);
    }

    private static bool TryGetMetamagic(string name, out MetamagicOptionDefinition metamagicOption)
    {
        return DatabaseRepository.GetDatabase<MetamagicOptionDefinition>()
            .TryGetElement(name, out metamagicOption);
    }

    private static void SetOrRestoreFixedCost(
        MetamagicOptionDefinition metamagicOption,
        int rule2024Cost,
        bool enabled)
    {
        if (!LegacyCostStates.ContainsKey(metamagicOption.Name))
        {
            LegacyCostStates[metamagicOption.Name] =
                new MetamagicCostState(metamagicOption.CostMethod, metamagicOption.SorceryPointsCost);
        }

        if (enabled)
        {
            metamagicOption.costMethod = MetamagicCostMethod.FixedValue;
            metamagicOption.sorceryPointsCost = rule2024Cost;
            return;
        }

        var legacyState = LegacyCostStates[metamagicOption.Name];

        metamagicOption.costMethod = legacyState.CostMethod;
        metamagicOption.sorceryPointsCost = legacyState.SorceryPointsCost;
    }

    private static void SetOrRestoreDescription(string metamagicName, string rule2024Description, bool enabled)
    {
        if (!TryGetMetamagic(metamagicName, out var metamagicOption))
        {
            return;
        }

        if (!LegacyDescriptionKeys.ContainsKey(metamagicOption.Name))
        {
            LegacyDescriptionKeys[metamagicOption.Name] = metamagicOption.GuiPresentation.description;
        }

        metamagicOption.GuiPresentation.description = enabled
            ? rule2024Description
            : LegacyDescriptionKeys[metamagicOption.Name];
    }

    private readonly struct MetamagicCostState(
        MetamagicCostMethod costMethod,
        int sorceryPointsCost)
    {
        internal readonly MetamagicCostMethod CostMethod = costMethod;
        internal readonly int SorceryPointsCost = sorceryPointsCost;
    }

    private sealed class CarefulSpell2024Behavior(ConditionDefinition condition)
        : IMagicEffectInitiatedByMe, IMagicEffectFinishedByMe
    {
        public IEnumerator OnMagicEffectInitiatedByMe(
            CharacterAction action,
            RulesetEffect activeEffect,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (!Main.Settings.EnableSorcererMetamagic2024 ||
                !CombinedMetamagic.Contains(activeEffect.MetamagicOption, MetamagicCarefulSpell) ||
                !activeEffect.EffectDescription.EffectForms.Any(IsHalfDamageEffectForm))
            {
                yield break;
            }

            var rulesetAttacker = attacker.RulesetCharacter;
            var charismaModifier = Math.Max(1,
                AttributeDefinitions.ComputeAbilityScoreModifier(
                    rulesetAttacker.TryGetAttributeValue(AttributeDefinitions.Charisma)));

            var protectedTargets = 0;

            foreach (var target in targets)
            {
                if (target == attacker ||
                    target.Side != attacker.Side ||
                    target.RulesetCharacter == null)
                {
                    continue;
                }

                target.RulesetCharacter.InflictCondition(
                    condition.Name,
                    DurationType.Round,
                    0,
                    TurnOccurenceType.EndOfTurn,
                    AttributeDefinitions.TagEffect,
                    rulesetAttacker.guid,
                    rulesetAttacker.CurrentFaction.Name,
                    1,
                    condition.Name,
                    0,
                    0,
                    0);

                protectedTargets++;

                if (protectedTargets >= charismaModifier)
                {
                    break;
                }
            }
        }

        public IEnumerator OnMagicEffectFinishedByMe(
            CharacterAction action,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (!Main.Settings.EnableSorcererMetamagic2024 ||
                !CombinedMetamagic.Contains(action.ActionParams?.RulesetEffect.MetamagicOption, MetamagicCarefulSpell))
            {
                yield break;
            }

            foreach (var target in targets)
            {
                if (target.RulesetCharacter == null)
                {
                    continue;
                }

                while (target.RulesetCharacter.TryGetConditionOfCategoryAndType(
                           AttributeDefinitions.TagEffect, condition.Name, out var activeCondition))
                {
                    target.RulesetCharacter.RemoveCondition(activeCondition);
                }
            }
        }

        private static bool IsHalfDamageEffectForm(EffectForm effectForm)
        {
            return effectForm is
            {
                FormType: EffectForm.EffectFormType.Damage,
                SavingThrowAffinity: EffectSavingThrowType.HalfDamage
            };
        }
    }

    private sealed class ExtendedSpell2024Behavior(ConditionDefinition condition) : IMagicEffectInitiatedByMe
    {
        public IEnumerator OnMagicEffectInitiatedByMe(
            CharacterAction action,
            RulesetEffect activeEffect,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            if (!Main.Settings.EnableSorcererMetamagic2024 ||
                activeEffect is not RulesetEffectSpell rulesetEffectSpell ||
                !CombinedMetamagic.Contains(rulesetEffectSpell.MetamagicOption, MetamagicExtendedSpell) ||
                !rulesetEffectSpell.SpellDefinition.RequiresConcentration)
            {
                yield break;
            }

            var rulesetCharacter = attacker.RulesetCharacter;

            rulesetCharacter.InflictCondition(
                condition.Name,
                activeEffect.EffectDescription.DurationType,
                activeEffect.EffectDescription.DurationParameter,
                activeEffect.EffectDescription.EndOfEffect,
                AttributeDefinitions.TagEffect,
                rulesetCharacter.guid,
                rulesetCharacter.CurrentFaction.Name,
                1,
                condition.Name,
                0,
                0,
                0);
        }
    }
}
