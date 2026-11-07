using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using System;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageIdentityDefinitionPanelPatcher
{
    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), "AfterMorphotypeChanged")]
    [UsedImplicitly]
    public static class AfterMorphotypeChanged_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            CharacterStageIdentityDefinitionPanel __instance,
            MorphotypeElementDefinition.ElementCategory elementCategory)
        {
            if (elementCategory != MorphotypeElementDefinition.ElementCategory.Musculature)
            {
                return true;
            }

            var viewport = __instance.CommonData.CharacterViewportCustomization;

            viewport.ActivateCamera(1, CharacterViewport.Target.Center);
            viewport.ReturnsToInitialPosition = false;

            return false;
        }
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.Refresh))]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        private static void SetFirstName(RulesetCharacterHero hero, string value) =>
            hero.Name = ToolsContext.AppearanceEdition.ResolveName(hero,
                ToolsContext.FunctorRespec.ResolveIdentityInputName(hero, value, false), false);

        private static void SetLastName(RulesetCharacterHero hero, string value) =>
            hero.SurName = ToolsContext.AppearanceEdition.ResolveName(hero,
                ToolsContext.FunctorRespec.ResolveIdentityInputName(hero, value, true), true);

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.PropertySetter(typeof(RulesetEntity), nameof(RulesetEntity.Name)),
                    1, "CharacterStageIdentityDefinitionPanel.Refresh.Name",
                    new CodeInstruction(OpCodes.Call, new Action<RulesetCharacterHero, string>(SetFirstName).Method))
                .ReplaceCalls(AccessTools.PropertySetter(typeof(RulesetCharacterHero), nameof(RulesetCharacterHero.SurName)),
                    1, "CharacterStageIdentityDefinitionPanel.Refresh.SurName",
                    new CodeInstruction(OpCodes.Call, new Action<RulesetCharacterHero, string>(SetLastName).Method));

        [UsedImplicitly]
        public static void Postfix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.RefreshIdentityNames(__instance);
            ToolsContext.AppearanceEdition.RefreshIdentity(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.EnumerateValidationGroups))]
    [UsedImplicitly]
    public static class EnumerateValidationGroups_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            CharacterStageIdentityDefinitionPanel __instance,
            out (int Index, GuiGamepadValidationGroup Selected) __state)
        {
            __state = ToolsContext.AppearanceEdition.CaptureIdentityValidationGroup(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(
            CharacterStageIdentityDefinitionPanel __instance,
            (int Index, GuiGamepadValidationGroup Selected) __state) =>
            ToolsContext.AppearanceEdition.FilterIdentityValidationGroups(__instance, __state);
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.EnterStage))]
    [UsedImplicitly]
    public static class EnterStage_Patch
    {
        [UsedImplicitly]
        internal static void Prefix(
            CharacterStageIdentityDefinitionPanel __instance,
            out UiHelpers.NativePanelInitialization __state)
        {
            __state = null;
            ToolsContext.FunctorRespec.RestoreAppearance(__instance.CharacterBuildingService, __instance.currentHero);
            var screen = __instance.GetComponentInParent<CharacterEditionScreen>();
            CancelCustomizationPools(screen);
            __state = UiHelpers.BeginNativePanelInitialization(screen, __instance,
                () => screen.currentStagePanel == __instance && __instance.currentHero != null);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var setActive = AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive));
            var done = AccessTools.Method(typeof(CharacterStagePanel), "OnEnterStageDone");
            var sites = Enumerable.Range(0, Math.Max(0, code.Count - 2)).Where(index =>
                code[index].Calls(setActive) && code[index + 1].opcode == OpCodes.Ldarg_0 &&
                code[index + 2].Calls(done)).ToArray();
            if (sites.Length != 1)
            {
                Main.Error("CharacterStageIdentityDefinitionPanel.EnterStage native initialization site differs.");
                return code;
            }

            code[sites[0]].opcode = OpCodes.Call;
            code[sites[0]].operand = new Action<GameObject, bool>(UiHelpers.RestorePreparedPanelActive).Method;
            return code;
        }

        [UsedImplicitly]
        internal static Exception Finalizer(UiHelpers.NativePanelInitialization __state, Exception __exception) =>
            __state == null ? __exception : __state.Finish(__exception);
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.OnBeginShow))]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.CaptureIdentityAppearanceBaseline(__instance);
            ToolsContext.AppearanceEdition.RefreshIdentity(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.CancelStage))]
    [UsedImplicitly]
    public static class CancelStage_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.PreserveIdentityEdits(__instance);
        }
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel),
        nameof(CharacterStageIdentityDefinitionPanel.OnSexToggleChangedCb))]
    [UsedImplicitly]
    public static class OnSexToggleChangedCb_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.PreserveIdentityEdits(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(CharacterStageIdentityDefinitionPanel __instance)
        {
            ToolsContext.FunctorRespec.RestoreIdentityNames(__instance);
            ToolsContext.FunctorRespec.CaptureIdentityAppearanceBaseline(__instance);
            ToolsContext.AppearanceEdition.RefreshIdentity(__instance);
        }
    }

    //PATCH: avoids issues during RESPEC if a hero with same name is in the pool
    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel),
        nameof(CharacterStageIdentityDefinitionPanel.CanProceedToNextStage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanProceedToNextStage_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            CharacterStageIdentityDefinitionPanel __instance, ref string failureString, ref bool __result)
        {
            var name = __instance.currentHero.Name;

            if (__result || !(ToolsContext.FunctorRespec.IsOriginalName(__instance.currentHero, name) ||
                              ToolsContext.AppearanceEdition.IsOriginalName(__instance.currentHero, name)) ||
                failureString != Gui.Localize("Stage/&IdentityDefinitionStageFailDuplicateName"))
            {
                return;
            }

            failureString = string.Empty;
            __result = true;
        }
    }

    [HarmonyPatch]
    [UsedImplicitly]
    public static class BindCustomizationModifier_Patch
    {
        [ThreadStatic]
        private static ModifierInitialization _modifierInitialization;

        [UsedImplicitly]
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(
            typeof(CharacterCustomizationModifier), nameof(CharacterCustomizationModifier.Bind),
            [typeof(RulesetCharacterHero), typeof(MorphotypeElementDefinition.ElementCategory),
                typeof(MorphotypeCategoryDefinition), typeof(List<string>), typeof(int),
                typeof(CharacterCustomizationModifier.ValueChangedHandler), typeof(bool)]);

        [UsedImplicitly]
        private static void Prefix(CharacterCustomizationModifier __instance, RulesetCharacterHero __0,
            out ModifierInitialization __state)
        {
            __state = null;
            var owner = __instance.GetComponentInParent<CharacterStageIdentityDefinitionPanel>();
            if (owner && ReferenceEquals(owner.currentHero, __0))
            {
                __state = new ModifierInitialization(__instance);
            }
        }

        private static void SetGroupActive(GameObject target, bool active)
        {
            if (_modifierInitialization?.DeferGroup(target, active) != true)
            {
                target.SetActive(active);
            }
        }

        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.Method(typeof(GameObject), nameof(GameObject.SetActive)),
                17, "CharacterCustomizationModifier.Bind.Groups",
                new CodeInstruction(OpCodes.Call, new Action<GameObject, bool>(SetGroupActive).Method));

        [UsedImplicitly]
        private static void Postfix(ModifierInitialization __state)
        {
            var error = __state?.Finish(null);
            if (error != null)
            {
                throw error;
            }
        }

        [UsedImplicitly]
        private static Exception Finalizer(ModifierInitialization __state, Exception __exception) =>
            __state?.Finish(__exception) ?? __exception;

        private sealed class ModifierInitialization
        {
            private readonly ModifierInitialization _previous;
            private readonly GameObject _colors;
            private readonly GameObject _images;
            private readonly GameObject _labels;
            private int _pending;
            private bool _finished;

            internal ModifierInitialization(CharacterCustomizationModifier modifier)
            {
                _colors = modifier.colorsGroup.gameObject;
                _images = modifier.imagesGroup.gameObject;
                _labels = modifier.labelsGroup.gameObject;
                _previous = _modifierInitialization;
                _modifierInitialization = this;
            }

            internal bool DeferGroup(GameObject target, bool active)
            {
                var group = target == _colors ? 1 : target == _images ? 2 : target == _labels ? 4 : 0;
                if (group == 0)
                {
                    return false;
                }

                _pending = active ? _pending | group : _pending & ~group;
                if (!active)
                {
                    return false;
                }

                // Native color/image branches hide before binding their children.
                // Labels may remain active after Unbind; keep them inactive too.
                if (target.activeSelf)
                {
                    target.SetActive(false);
                }

                return true;
            }

            internal Exception Finish(Exception originalException)
            {
                if (_finished)
                {
                    return originalException;
                }

                _finished = true;
                if (!ReferenceEquals(_modifierInitialization, this))
                {
                    return Combine(originalException,
                        new InvalidOperationException("Native modifier initialization scope mismatch."));
                }

                // Enabling a group can invoke native UI callbacks. A reentrant
                // Bind must receive its own scope after this call has finished.
                _modifierInitialization = _previous;
                while (_modifierInitialization is { _finished: true })
                {
                    _modifierInitialization = _modifierInitialization._previous;
                }

                foreach (var group in new[] { (_colors, 1), (_images, 2), (_labels, 4) })
                {
                    if ((_pending & group.Item2) == 0 || !group.Item1)
                    {
                        continue;
                    }

                    try
                    {
                        group.Item1.SetActive(true);
                    }
                    catch (Exception error)
                    {
                        originalException = Combine(originalException, error);
                    }
                }

                return originalException;
            }

            private static Exception Combine(Exception originalException, Exception cleanupException) =>
                originalException == null ? cleanupException : new AggregateException(originalException, cleanupException);
        }
    }

    [ThreadStatic]
    private static bool _releasingCustomizationChoices;

    private static bool OwnsCustomizationModifiers(CharacterStageIdentityDefinitionPanel panel)
    {
        if (!panel || !panel.modifiersTable || panel.modifiers.Count != panel.modifiersTable.childCount)
        {
            return false;
        }

        for (var index = 0; index < panel.modifiers.Count; index++)
        {
            var modifier = panel.modifiers[index];
            if (!modifier || modifier.transform != panel.modifiersTable.GetChild(index) ||
                modifier.GetComponentInParent<CharacterStageIdentityDefinitionPanel>() != panel)
            {
                return false;
            }
        }

        return true;
    }

    private static bool OwnsChoiceGroup(CharacterCustomizationModifier modifier, Transform group,
        System.Collections.IList choices)
    {
        var panel = modifier ? modifier.GetComponentInParent<CharacterStageIdentityDefinitionPanel>() : null;
        if (_releasingCustomizationChoices || !panel || !group || choices == null ||
            choices.Count != group.childCount ||
            (!panel.modifiers.Contains(modifier) && panel.voiceModifier != modifier && panel.backstoryModifier != modifier))
        {
            return false;
        }

        for (var index = 0; index < choices.Count; index++)
        {
            if (choices[index] is not ChoiceToggle choice || !choice || choice.transform != group.GetChild(index))
            {
                return false;
            }
        }

        return true;
    }

    private static void ClearUnboundChoices<T>(List<T> choices, CharacterCustomizationModifier modifier)
        where T : ChoiceToggle
    {
        var group = ReferenceEquals(choices, modifier.colorChoiceToggles) ? modifier.colorsGroup :
            ReferenceEquals(choices, modifier.imageChoiceToggles) ? modifier.imagesGroup :
            ReferenceEquals(choices, modifier.labelChoiceToggles) ? modifier.labelsGroup : null;
        if (!OwnsChoiceGroup(modifier, group, choices))
        {
            choices.Clear();
        }
    }

    private static void ReleaseUnboundChoiceChildren(Transform group)
    {
        var modifier = group ? group.GetComponentInParent<CharacterCustomizationModifier>() : null;
        System.Collections.IList choices = !modifier ? null :
            group == modifier.colorsGroup ? modifier.colorChoiceToggles :
            group == modifier.imagesGroup ? modifier.imageChoiceToggles :
            group == modifier.labelsGroup ? modifier.labelChoiceToggles : null;
        if (OwnsChoiceGroup(modifier, group, choices))
        {
            // Native Unbind has already cleared every leaf callback and sprite.
            // Keep its native-owned rows unregistered and inactive for the next Bind.
            foreach (Transform child in group)
            {
                child.gameObject.SetActive(false);
            }

            return;
        }

        Gui.ReleaseChildrenToPool(group);
    }

    private static void ClearCustomizationModifiers(List<CharacterCustomizationModifier> modifiers,
        CharacterStageIdentityDefinitionPanel panel)
    {
        if (ReferenceEquals(modifiers, panel.modifiers) && OwnsCustomizationModifiers(panel))
        {
            return;
        }

        // An unexpected hierarchy keeps native release behavior, including any
        // leaf rows retained by the completed Unbind calls above.
        foreach (var modifier in modifiers)
        {
            if (!modifier || modifier.GetComponentInParent<CharacterStageIdentityDefinitionPanel>() != panel)
            {
                continue;
            }

            modifier.colorChoiceToggles.Clear();
            modifier.imageChoiceToggles.Clear();
            modifier.labelChoiceToggles.Clear();
            Gui.ReleaseChildrenToPool(modifier.colorsGroup);
            Gui.ReleaseChildrenToPool(modifier.imagesGroup);
            Gui.ReleaseChildrenToPool(modifier.labelsGroup);
        }

        modifiers.Clear();
    }

    private static void ReleaseUnboundModifierChildren(Transform table,
        CharacterStageIdentityDefinitionPanel panel)
    {
        if (table == panel.modifiersTable && OwnsCustomizationModifiers(panel))
        {
            foreach (Transform child in table)
            {
                child.gameObject.SetActive(false);
            }

            return;
        }

        Gui.ReleaseChildrenToPool(table);
    }

    internal static Exception ReleaseCustomizationChoices(CharacterStageIdentityDefinitionPanel panel,
        Exception originalException = null)
    {
        if (!panel)
        {
            return originalException;
        }

        var previous = _releasingCustomizationChoices;
        _releasingCustomizationChoices = true;
        try
        {
            var modifiers = panel.modifiers.Concat(new[] { panel.voiceModifier, panel.backstoryModifier })
                .Where(modifier => modifier).Distinct().ToArray();
            foreach (var modifier in modifiers)
            {
                try
                {
                    modifier.Unbind();
                }
                catch (Exception error)
                {
                    originalException = originalException == null ? error :
                        new AggregateException(originalException, error);
                }
            }

            try
            {
                if (panel.modifiersTable)
                {
                    Gui.ReleaseChildrenToPool(panel.modifiersTable);
                }

                panel.modifiers.Clear();
            }
            catch (Exception error)
            {
                originalException = originalException == null ? error :
                    new AggregateException(originalException, error);
            }
        }
        finally
        {
            _releasingCustomizationChoices = previous;
        }

        return originalException;
    }

    [HarmonyPatch(typeof(CharacterCustomizationModifier), nameof(CharacterCustomizationModifier.Unbind))]
    [UsedImplicitly]
    public static class UnbindCustomizationModifier_Patch
    {
        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.Method(typeof(List<ColorChoiceToggle>),
                    nameof(List<ColorChoiceToggle>.Clear)),
                1, "CharacterCustomizationModifier.Unbind.ColorChoices",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    new Action<List<ColorChoiceToggle>, CharacterCustomizationModifier>(ClearUnboundChoices).Method))
            .ReplaceCalls(AccessTools.Method(typeof(List<ImageChoiceToggle>),
                    nameof(List<ImageChoiceToggle>.Clear)),
                1, "CharacterCustomizationModifier.Unbind.ImageChoices",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    new Action<List<ImageChoiceToggle>, CharacterCustomizationModifier>(ClearUnboundChoices).Method))
            .ReplaceCalls(AccessTools.Method(typeof(List<LabelChoiceToggle>),
                    nameof(List<LabelChoiceToggle>.Clear)),
                1, "CharacterCustomizationModifier.Unbind.LabelChoices",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    new Action<List<LabelChoiceToggle>, CharacterCustomizationModifier>(ClearUnboundChoices).Method))
            .ReplaceCalls(AccessTools.Method(typeof(Gui), nameof(Gui.ReleaseChildrenToPool)),
                3, "CharacterCustomizationModifier.Unbind.OwnedChoices",
                new CodeInstruction(OpCodes.Call, new Action<Transform>(ReleaseUnboundChoiceChildren).Method));
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.OnEndHide))]
    [UsedImplicitly]
    public static class OnEndHide_Patch
    {
        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            instructions.ReplaceCalls(AccessTools.Method(typeof(List<CharacterCustomizationModifier>),
                    nameof(List<CharacterCustomizationModifier>.Clear)), 1,
                "CharacterStageIdentityDefinitionPanel.OnEndHide.Modifiers",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    new Action<List<CharacterCustomizationModifier>, CharacterStageIdentityDefinitionPanel>(
                        ClearCustomizationModifiers).Method))
            .ReplaceCalls(AccessTools.Method(typeof(Gui), nameof(Gui.ReleaseChildrenToPool)), 1,
                "CharacterStageIdentityDefinitionPanel.OnEndHide.OwnedModifiers",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,
                    new Action<Transform, CharacterStageIdentityDefinitionPanel>(ReleaseUnboundModifierChildren).Method));
    }

    [HarmonyPatch(typeof(CharacterStageIdentityDefinitionPanel), nameof(CharacterStageIdentityDefinitionPanel.Unload))]
    [UsedImplicitly]
    public static class Unload_Patch
    {
        [UsedImplicitly]
        private static void Postfix(CharacterStageIdentityDefinitionPanel __instance,
            ref System.Collections.IEnumerator __result)
        {
            CancelCustomizationPools(__instance.GetComponentInParent<CharacterEditionScreen>());
            __result = ReleaseAfterUnload(__instance, __result);
        }

        private static System.Collections.IEnumerator ReleaseAfterUnload(
            CharacterStageIdentityDefinitionPanel panel, System.Collections.IEnumerator native)
        {
            Exception failure = null;
            try
            {
                while (true)
                {
                    bool next;
                    object current = null;
                    try
                    {
                        next = native.MoveNext();
                        if (next)
                        {
                            current = native.Current;
                        }
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        break;
                    }

                    if (!next)
                    {
                        break;
                    }

                    yield return current;
                }
            }
            finally
            {
                try
                {
                    (native as IDisposable)?.Dispose();
                }
                catch (Exception error)
                {
                    failure = failure == null ? error : new AggregateException(failure, error);
                }

                failure = ReleaseCustomizationChoices(panel, failure);
                if (failure != null)
                {
                    throw failure;
                }
            }
        }
    }

    private static WeakReference<CustomizationPoolPreparation> _customizationPoolPreparation;

    private static CustomizationPoolPreparation ActiveCustomizationPoolPreparation() =>
        _customizationPoolPreparation != null && _customizationPoolPreparation.TryGetTarget(out var preparation)
            ? preparation : null;

    internal static void PrepareCustomizationPools(CharacterEditionScreen screen, CharacterStagePanel entered)
    {
        if (screen is not CharacterCreationScreen || screen.currentHero == null ||
            !CustomizationPoolPreparation.BeforeIdentity(screen, entered))
        {
            CancelCustomizationPools(screen);
            return;
        }

        var active = ActiveCustomizationPoolPreparation();
        if (active?.Owner == screen)
        {
            return;
        }

        if (active != null)
        {
            CancelCustomizationPools(active.Owner);
        }

        var identity = screen.RelevantStagePanels.OfType<CharacterStageIdentityDefinitionPanel>().SingleOrDefault();
        if (!identity || !identity.modifierPrefab || !ReferenceEquals(identity.currentHero, screen.currentHero))
        {
            return;
        }

        var preparation = new CustomizationPoolPreparation(screen, identity, screen.currentHero);
        _customizationPoolPreparation = new WeakReference<CustomizationPoolPreparation>(preparation);
        preparation.Routine = screen.StartCoroutine(preparation.Run());
    }

    internal static void CancelCustomizationPools(CharacterEditionScreen screen)
    {
        var preparation = ActiveCustomizationPoolPreparation();
        if (preparation == null || !ReferenceEquals(preparation.Owner, screen))
        {
            return;
        }

        _customizationPoolPreparation = null;
        preparation.Cancelled = true;
        if (screen && preparation.Routine != null)
        {
            screen.StopCoroutine(preparation.Routine);
        }
    }

    private static List<string> ReadCustomizationPoolOptions(ICharacterBuildingService service,
        CustomizationPoolPreparation.Selection selection, MorphotypeElementDefinition.ElementCategory category)
    {
        return service.BuildMorphotypeOptionsList(category, selection.Sex, selection.Race, selection.SubRace,
            category == MorphotypeElementDefinition.ElementCategory.Origin ? string.Empty : selection.Origin,
            selection.Face, selection.Subclass, true);
    }

    private static void GrowCustomizationPool(GameObject prefab, PrefabPool pool)
    {
        // Native growth leaves each new element inactive and owned by its pool.
        pool.IncreasePool(prefab, 1);
    }

    private sealed class CustomizationPoolPreparation(
        CharacterEditionScreen owner, CharacterStageIdentityDefinitionPanel panel, RulesetCharacterHero hero)
    {
        internal readonly CharacterEditionScreen Owner = owner;
        internal Coroutine Routine;
        internal bool Cancelled;

        internal struct Selection
        {
            internal CharacterRaceDefinition Race, SubRace;
            internal CharacterClassDefinition Class;
            internal CharacterSubclassDefinition Subclass;
            internal RuleDefinitions.CreatureSex Sex;
            internal string Origin, Face;
            internal int Level;

            internal bool Same(Selection other) => ReferenceEquals(Race, other.Race) &&
                ReferenceEquals(SubRace, other.SubRace) && ReferenceEquals(Class, other.Class) &&
                ReferenceEquals(Subclass, other.Subclass) && Sex == other.Sex && Level == other.Level &&
                string.Equals(Origin, other.Origin, StringComparison.Ordinal) &&
                string.Equals(Face, other.Face, StringComparison.Ordinal);
        }

        private sealed class Demand(GameObject prefab, int count)
        {
            internal readonly GameObject Prefab = prefab;
            internal readonly int Count = count;
            internal PrefabPool Pool;
            internal int Limit;
        }

        internal static bool BeforeIdentity(CharacterEditionScreen screen, CharacterStagePanel stage)
        {
            var stages = screen.RelevantStagePanels;
            var current = stages.IndexOf(stage);
            var classIndex = -1;
            var identityIndex = -1;
            for (var index = 0; index < stages.Count; index++)
            {
                if (stages[index] is CharacterStageClassSelectionPanel) classIndex = index;
                if (stages[index] is CharacterStageIdentityDefinitionPanel) identityIndex = index;
            }

            return classIndex >= 0 && current > classIndex && identityIndex > current;
        }

        private bool Current() => !Cancelled && Owner && Owner.Visible && !Owner.Hiding &&
            Owner.gameObject.activeInHierarchy && panel && ReferenceEquals(Owner.currentHero, hero) &&
            ReferenceEquals(panel.currentHero, hero) && BeforeIdentity(Owner, Owner.currentStagePanel);

        private bool ReadSelection(ICharacterBuildingService service, out Selection selection)
        {
            selection = default;
            if (!hero.RaceDefinition || !hero.MorphotypeElements.TryGetValue(
                    MorphotypeElementDefinition.ElementCategory.Origin, out var origin) ||
                string.IsNullOrEmpty(origin)) return false;
            service.GetLastAssignedClassAndLevel(hero, out var @class, out var level);
            if (!@class || level <= 0) return false;
            hero.ClassesAndSubclasses.TryGetValue(@class, out var subclass);
            if (!subclass)
            {
                foreach (var unlock in @class.FeatureUnlocks)
                {
                    if (unlock.Level <= level && unlock.FeatureDefinition is FeatureDefinitionSubclassChoice)
                        return false;
                }
            }

            string face = null;
            if (hero.RaceDefinition.RacePresentation.HasSpecificBodyDecorationForFaceshape &&
                (!hero.MorphotypeElements.TryGetValue(MorphotypeElementDefinition.ElementCategory.FaceShape,
                     out face) || string.IsNullOrEmpty(face))) return false;
            selection = new Selection { Race = hero.RaceDefinition, SubRace = hero.SubRaceDefinition,
                Sex = hero.Sex, Class = @class, Subclass = subclass, Level = level, Origin = origin, Face = face };
            return true;
        }

        private List<Demand> BuildDemand(Selection selection,
            Dictionary<MorphotypeElementDefinition.ElementCategory, List<string>> options)
        {
            if (!OwnsCustomizationModifiers(panel)) return null;
            var prototype = panel.modifierPrefab.GetComponent<CharacterCustomizationModifier>();
            if (!prototype) return null;
            var needed = new Dictionary<GameObject, int>();
            void Add(GameObject prefab, int count)
            {
                if (!prefab || count <= 0) return;
                needed.TryGetValue(prefab, out var existing);
                needed[prefab] = checked(existing + count);
            }

            var categories = MorphotypeElementDefinition.CommonCategories;
            Add(panel.modifierPrefab, Math.Max(0, categories.Length - panel.modifiers.Count));
            for (var index = 0; index < categories.Length; index++)
            {
                var category = categories[index];
                if (MorphotypeElementDefinition.IsModifiedBySlider(category) ||
                    !MorphotypeElementDefinition.IsAvailableForRace(category, selection.Race, selection.SubRace) ||
                    !options.TryGetValue(category, out var values) || values.Count <= 1) continue;
                if (!DatabaseRepository.GetDatabase<MorphotypeCategoryDefinition>()
                        .TryGetElement(category.ToString(), out var definition)) return null;
                if (definition.HasDependency)
                {
                    if (!options.TryGetValue(definition.Dependency, out var dependency)) return null;
                    if (dependency.Count == 0 || dependency.Count == 1 &&
                        dependency[0] == definition.DependencyNoneEntry) continue;
                }

                var modifier = index < panel.modifiers.Count ? panel.modifiers[index] : prototype;
                var owned = index < panel.modifiers.Count;
                switch (definition.ChoiceForm)
                {
                    case MorphotypeCategoryDefinition.Form.Color:
                        Add(modifier.colorChoicePrefab, Math.Max(0, values.Count -
                            (owned && OwnsChoiceGroup(modifier, modifier.colorsGroup, modifier.colorChoiceToggles)
                                ? modifier.colorChoiceToggles.Count : 0)));
                        break;
                    case MorphotypeCategoryDefinition.Form.Image:
                        Add(modifier.imageChoicePrefab, Math.Max(0, values.Count -
                            (owned && OwnsChoiceGroup(modifier, modifier.imagesGroup, modifier.imageChoiceToggles)
                                ? modifier.imageChoiceToggles.Count : 0)));
                        break;
                    case MorphotypeCategoryDefinition.Form.Label:
                        Add(modifier.labelChoicePrefab, Math.Max(0, values.Count -
                            (owned && OwnsChoiceGroup(modifier, modifier.labelsGroup, modifier.labelChoiceToggles)
                                ? modifier.labelChoiceToggles.Count : 0)));
                        break;
                }
            }

            return needed.Select(pair => new Demand(pair.Key, pair.Value)).ToList();
        }

        internal System.Collections.IEnumerator Run()
        {
            try
            {
                yield return null;
                if (!Current()) yield break;
                var service = Owner.CharacterBuildingService;
                var categories = MorphotypeElementDefinition.CommonCategories;
                var options = new Dictionary<MorphotypeElementDefinition.ElementCategory, List<string>>();
                Selection selection = default;
                var selected = false;
                var categoryIndex = 0;
                List<Demand> demand = null;
                var demandIndex = 0;
                while (Current())
                {
                    if (Owner.Showing || Owner.InTransition)
                    {
                        yield return null;
                        continue;
                    }

                    if (!ReadSelection(service, out var now))
                    {
                        options.Clear(); demand = null; selected = false; categoryIndex = demandIndex = 0;
                        yield return null;
                        continue;
                    }

                    if (!selected || !selection.Same(now))
                    {
                        selection = now; selected = true; options.Clear(); demand = null;
                        categoryIndex = demandIndex = 0;
                    }

                    if (categoryIndex < categories.Length)
                    {
                        var category = categories[categoryIndex++];
                        options[category] = MorphotypeElementDefinition.IsModifiedBySlider(category)
                            ? new List<string> { category.ToString() }
                            : ReadCustomizationPoolOptions(service, selection, category);
                        if (options[category] == null) yield break;
                        yield return null;
                        continue;
                    }

                    if (demand == null)
                    {
                        demand = BuildDemand(selection, options);
                        if (demand == null) yield break;
                        yield return null;
                        continue;
                    }

                    if (demandIndex < demand.Count)
                    {
                        var item = demand[demandIndex];
                        if (!item.Prefab) yield break;
                        if (!item.Pool)
                        {
                            // A seed borrow can itself create one native instance. Return it
                            // before yielding, and leave any further growth for another frame.
                            var manager = Gui.GuiService.PrefabPoolManager;
                            GameObject seed = null;
                            try
                            {
                                seed = manager.GetElement(item.Prefab, false);
                                item.Pool = seed.GetComponent<PrefabPoolElement>().Pool;
                            }
                            finally
                            {
                                if (seed) manager.ReturnElement(seed);
                            }

                            item.Limit = checked(item.Pool.PoolSize + Math.Max(0, item.Count - item.Pool.Count));
                        }
                        else if (item.Pool.Count < item.Count && item.Pool.PoolSize < item.Limit)
                        {
                            GrowCustomizationPool(item.Prefab, item.Pool);
                        }
                        else demandIndex++;
                    }

                    if (demandIndex == demand.Count)
                    {
                        yield break;
                    }

                    yield return null;
                }
            }
            finally
            {
                if (ReferenceEquals(ActiveCustomizationPoolPreparation(), this)) _customizationPoolPreparation = null;
            }
        }
    }
}
