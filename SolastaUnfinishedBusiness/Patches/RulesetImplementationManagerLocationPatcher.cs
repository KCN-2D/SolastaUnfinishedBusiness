using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Spells;
using SolastaUnfinishedBusiness.Validators;
using TA;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RulesetImplementationManagerLocationPatcher
{
    [HarmonyPatch(
        typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.ApplyItemPropertyForm),
        typeof(EffectForm),
        typeof(RulesetImplementationDefinitions.ApplyFormsParams))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyItemPropertyForm_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            if (formsParams.targetCharacter is not RulesetCharacterSimulacrum duplicate ||
                formsParams.activeEffect is not RulesetEffect activeEffect)
            {
                return;
            }

            EnsureTrackedItemProperties(duplicate, activeEffect);
            SimulacrumBehavior.RefreshEquipment(duplicate);
        }

        private static void EnsureTrackedItemProperties(
            RulesetCharacterSimulacrum duplicate,
            RulesetEffect activeEffect)
        {
            var inventory = duplicate.CharacterInventory;
            var locationGuid = GameLocationCharacter.GetFromActor(duplicate)?.Guid ?? duplicate.Guid;

            if (inventory == null)
            {
                return;
            }

            var items = new List<RulesetItem>();

            inventory.EnumerateAllItems(items, true, false);

            foreach (var item in items)
            {
                var slot = inventory.FindSlotHoldingItem(item);

                foreach (var property in item.dynamicItemProperties
                             .Where(property =>
                                 property?.SourceEffectGuid == activeEffect.Guid &&
                                 !activeEffect.TrackedItemPropertyGuids.Contains(property.Guid))
                             .ToArray())
                {
                    activeEffect.TrackItemProperty(
                        item,
                        locationGuid,
                        slot?.Name ?? string.Empty,
                        property);
                    inventory.ItemAltered?.Invoke(inventory, slot, item);
                }
            }
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.InstantiateEffectRitual))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InstantiateEffectRitual_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var constructor = AccessTools.Constructor(
                typeof(RulesetEffectSpell),
                [typeof(RulesetCharacter), typeof(SpellDefinition)]);
            var replacement = AccessTools.Method(
                typeof(InstantiateEffectRitual_Patch),
                nameof(CreateRitualEffect));
            var replaced = 0;

            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Newobj &&
                    Equals(instruction.operand, constructor))
                {
                    replaced++;
                    var replacementInstruction = new CodeInstruction(
                        OpCodes.Call,
                        replacement);

                    replacementInstruction.labels.AddRange(instruction.labels);
                    replacementInstruction.blocks.AddRange(instruction.blocks);
                    yield return replacementInstruction;
                    continue;
                }

                yield return instruction;
            }

            if (replaced != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one ritual spell constructor, replaced {replaced}.");
            }
        }

        private static RulesetEffectSpell CreateRitualEffect(
            RulesetCharacter caster,
            SpellDefinition spellDefinition)
        {
            if (caster is not RulesetCharacterSimulacrum duplicate)
            {
                return new RulesetEffectSpell(caster, spellDefinition);
            }

            var repertoire = SimulacrumBehavior.ResolveRitualRepertoire(
                duplicate,
                spellDefinition);

            var effect = new RulesetEffectSpell(caster, spellDefinition);

            // Native ritual timing requires both SlotLevel < 0 and SpellRepertoire == null.
            // Keep the selected repertoire out-of-band so validation can still use the exact
            // spellbook without turning the ritual into the spell's normal casting time.
            SpellCastingValidation.BindEffectRepertoire(effect, repertoire);

            return effect;
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.InstantiateEffectSpell))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InstantiateEffectSpell_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            RulesetImplementationManagerLocation __instance,
            ref RulesetEffectSpell __result,
            RulesetCharacter caster,
            RulesetSpellRepertoire spellRepertoire,
            SpellDefinition spellDefinition,
            int slotLevel,
            bool delayRegistration)
        {
            return RulesetEffectSpellWithOrigin.TryInstantiate(
                __instance,
                ref __result,
                caster,
                spellRepertoire,
                spellDefinition,
                slotLevel,
                delayRegistration);
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.ApplySummonForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplySummonForm_Patch
    {
        [ThreadStatic]
        private static Stack<SummonFormState> _invocationStates;

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var constructor = AccessTools.Constructor(
                typeof(RulesetCharacterMonster),
                [
                    typeof(MonsterDefinition),
                    typeof(int),
                    typeof(SpawnOverrides),
                    typeof(GadgetDefinitions.CreatureSex),
                    typeof(RulesetCharacter),
                    typeof(bool),
                    typeof(bool),
                    typeof(bool)
                ]);
            var factory = AccessTools.Method(
                typeof(ApplySummonForm_Patch),
                nameof(CreateRulesetCharacterMonster));
            var replaced = 0;

            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Newobj &&
                    Equals(instruction.operand, constructor))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = factory;
                    replaced++;
                }

                yield return instruction;
            }

            if (replaced != 1)
            {
                throw new InvalidOperationException(
                    $"ApplySummonForm monster factory patch expected 1 constructor, found {replaced}.");
            }
        }

        [UsedImplicitly]
        public static RulesetCharacterMonster CreateRulesetCharacterMonster(
            MonsterDefinition monsterDefinition,
            int experience,
            SpawnOverrides spawnOverrides,
            GadgetDefinitions.CreatureSex sex,
            RulesetCharacter originalFormCharacter,
            bool keepMentalAbilityScores,
            bool useMentalAbilityScores,
            bool useOriginalFormConstitution)
        {
            var factory = monsterDefinition
                ?.GetFirstSubFeatureOfType<IRulesetCharacterMonsterFactory>();

            var summonedCharacter = factory?.Create(
                       monsterDefinition,
                       experience,
                       spawnOverrides,
                       sex,
                       originalFormCharacter,
                       keepMentalAbilityScores,
                       useMentalAbilityScores,
                       useOriginalFormConstitution)
                   ?? new RulesetCharacterMonster(
                       monsterDefinition,
                       experience,
                       spawnOverrides,
                       sex,
                       originalFormCharacter,
                       keepMentalAbilityScores,
                       useMentalAbilityScores,
                       useOriginalFormConstitution);

            if (_invocationStates is { Count: > 0 } &&
                _invocationStates.Peek() is { PreparationSucceeded: true } state)
            {
                foreach (var invocation in state.Invocations)
                {
                    invocation.Handler.InitializeSummonedCharacter(
                        summonedCharacter,
                        invocation.Context);
                }
            }

            return summonedCharacter;
        }

        internal static void InitializeConstructionAttributes(
            RulesetCharacterMonster summonedCharacter)
        {
            if (_invocationStates is not { Count: > 0 } ||
                _invocationStates.Peek() is not
                {
                    PreparationSucceeded: true
                } state)
            {
                return;
            }

            foreach (var invocation in state.Invocations)
            {
                if (invocation.Handler is ICustomSummonCharacterConstructionHandler handler)
                {
                    handler.InitializeConstructionAttributes(
                        summonedCharacter,
                        invocation.Context);
                }
            }
        }

        [UsedImplicitly]
        public static bool Prefix(
            ref EffectForm effectForm,
            ref RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            out object __state)
        {
            __state = null;

            if (RulesetImplementationManagerPatcher.ApplySummonForm_Patch
                .TryApplySimulacrumInventoryItem(effectForm, formsParams))
            {
                return false;
            }

            var summonForm = effectForm?.SummonForm;
            var sourceDefinition = formsParams.activeEffect?.SourceDefinition;

            if (summonForm?.SummonType != SummonForm.Type.Creature ||
                !sourceDefinition)
            {
                return true;
            }

            var handlers = sourceDefinition
                .GetAllSubFeaturesOfType<ICustomSummonFormHandler>()
                .ToArray();

            if (handlers.Length == 0)
            {
                return true;
            }

            var invocationEffectForm = new EffectForm();

            invocationEffectForm.Copy(effectForm);
            effectForm = invocationEffectForm;
            summonForm = invocationEffectForm.SummonForm;

            var state = new SummonFormState(formsParams.position);

            __state = state;

            foreach (var handler in handlers)
            {
                if (!handler.TryPrepare(
                        effectForm,
                        ref formsParams,
                        out var invocationContext,
                        out var failureFeedback))
                {
                    state.PreparationSucceeded = false;
                    formsParams.activeEffect?.DoTerminate(formsParams.sourceCharacter);

                    if (!string.IsNullOrEmpty(failureFeedback))
                    {
                        Gui.GuiService.ShowAlert(
                            failureFeedback,
                            Gui.ColorFailure,
                            2.5f);
                    }

                    return false;
                }

                state.Invocations.Add(new HandlerInvocation(handler, invocationContext));

                var monsterDefinitionName = handler.GetMonsterDefinitionName(
                    effectForm,
                    formsParams,
                    invocationContext);

                if (string.IsNullOrEmpty(monsterDefinitionName) ||
                    monsterDefinitionName == summonForm.MonsterDefinitionName)
                {
                    continue;
                }

                summonForm.monsterDefinitionName = monsterDefinitionName;

                break;
            }

            (_invocationStates ??= new Stack<SummonFormState>()).Push(state);
            state.Pushed = true;

            return true;
        }

        [UsedImplicitly]
        public static void Postfix(
            EffectForm effectForm,
            ref RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            object __state)
        {
            try
            {
                if (__state is not SummonFormState
                    {
                        PreparationSucceeded: true
                    } state)
                {
                    return;
                }

                foreach (var invocation in state.Invocations)
                {
                    invocation.Handler.AfterApply(
                        effectForm,
                        formsParams,
                        invocation.Context);
                }
            }
            finally
            {
                RestoreState(ref formsParams, __state);
            }
        }

        [UsedImplicitly]
        public static Exception Finalizer(
            Exception __exception,
            ref RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            object __state)
        {
            RestoreState(ref formsParams, __state);

            return __exception;
        }

        private static void RestoreState(
            ref RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            object state)
        {
            if (state is SummonFormState summonFormState)
            {
                formsParams.position = summonFormState.Position;
                PopInvocationState(summonFormState);
            }
        }

        private static void PopInvocationState(SummonFormState state)
        {
            if (!state.Pushed)
            {
                return;
            }

            state.Pushed = false;

            if (_invocationStates is not { Count: > 0 } ||
                !ReferenceEquals(_invocationStates.Peek(), state))
            {
                Trace.LogWarning("Custom summon invocation context stack is inconsistent.");
                _invocationStates?.Clear();

                return;
            }

            _invocationStates.Pop();
        }

        private sealed class SummonFormState(int3 position)
        {
            internal readonly List<HandlerInvocation> Invocations = [];
            internal readonly int3 Position = position;
            internal bool PreparationSucceeded = true;
            internal bool Pushed;
        }

        private sealed class HandlerInvocation(
            ICustomSummonFormHandler handler,
            ICustomSummonInvocationContext context)
        {
            internal readonly ICustomSummonInvocationContext Context = context;
            internal readonly ICustomSummonFormHandler Handler = handler;
        }
    }

    [HarmonyPatch(
        typeof(RulesetCharacterMonster),
        nameof(RulesetCharacterMonster.RegisterAttributes))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetCharacterMonsterRegisterAttributes_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RulesetCharacterMonster __instance)
        {
            ApplySummonForm_Patch.InitializeConstructionAttributes(__instance);
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.InstantiateEffectInvocation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InstantiateEffectInvocation_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetEffectSpell __result,
            RulesetInvocation invocation)
        {
            //PATCH: setup repertoire for spells cast through invocation 
            __result.spellRepertoire ??= invocation.invocationRepertoire;
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.IsMetamagicOptionAvailable))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsMetamagicOptionAvailable_Patch
    {
        private static int RemainingSorceryPoints(RulesetCharacter caster, RulesetEffectSpell rulesetEffectSpell)
        {
            return Tabletop2024Context.IsArcaneApotheosisValid(caster, rulesetEffectSpell)
                ? 9999
                : caster.RemainingSorceryPoints;
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var remainingSorceryPointsMethod = typeof(RulesetCharacter).GetMethod("get_RemainingSorceryPoints");
            var myRemainingSorceryPointsMethod =
                new Func<RulesetCharacter, RulesetEffectSpell, int>(RemainingSorceryPoints).Method;

            return instructions.ReplaceCalls(remainingSorceryPointsMethod,
                "CharacterActionCastSpell.RemoveConcentrationAsNeeded",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, myRemainingSorceryPointsMethod));
        }

        [UsedImplicitly]
        public static void Postfix(
            ref bool __result,
            RulesetEffectSpell rulesetEffectSpell,
            RulesetCharacter caster,
            MetamagicOptionDefinition metamagicOption,
            ref string failure)
        {
            if (!__result)
            {
                if (!MetamagicContext.TryHandleTwinnedSpell2024Availability(
                    rulesetEffectSpell,
                    metamagicOption,
                    RemainingSorceryPoints(caster, rulesetEffectSpell),
                    ref __result,
                    ref failure))
                {
                    return;
                }

                if (!__result)
                {
                    return;
                }
            }

            //PATCH: support for custom metamagic
            foreach (var validator in metamagicOption.GetAllSubFeaturesOfType<ValidateMetamagicApplication>())
            {
                validator.Invoke(caster, rulesetEffectSpell, metamagicOption, ref __result, ref failure);
            }
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.IsSituationalContextValid))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsSituationalContextValid_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            ref bool __result,
            RulesetImplementationDefinitions.SituationalContextParams contextParams)
        {
            //PATCH: supports custom situational context
            __result = CustomSituationalContext.IsContextValid(contextParams, __result);
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.InstantiateActiveDeviceFunction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class InstantiateActiveDeviceFunction_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            RulesetImplementationManagerLocation __instance,
            ref RulesetEffect __result,
            RulesetCharacter user,
            RulesetItemDevice usableDevice,
            RulesetDeviceFunction usableDeviceFunction,
            int addedCharges,
            bool delayRegistration,
            int subSpellIndex)
        {
            if (!RulesetEffectSpellWithOrigin.TryInstantiateDevice(
                    __instance,
                    ref __result,
                    user,
                    usableDevice,
                    usableDeviceFunction,
                    addedCharges,
                    subSpellIndex,
                    delayRegistration))
            {
                return false;
            }

            //PATCH: support `RulesetEffectPowerWithAdvancement` by creating custom instance when needed
            return RulesetEffectPowerWithAdvancement.InstantiateActiveDeviceFunction(__instance, ref __result, user,
                usableDevice, usableDeviceFunction, addedCharges, delayRegistration);
        }
    }


    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.ApplyMotionForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyMotionForm_Patch
    {
        private static void TeleportCharacter(
            IGameLocationPositioningService __instance,
            GameLocationCharacter character,
            int3 newPosition,
            LocationDefinitions.Orientation orientation)
        {
            if (Main.Settings.EnableTeleportToRemoveRestrained)
            {
                var rulesetCharacter = character.RulesetCharacter;
                var conditionsToRemove = rulesetCharacter.ConditionsByCategory
                    .SelectMany(x => x.Value)
                    .Where(x =>
                        x.ConditionDefinition.IsSubtypeOf(ConditionRestrained) &&
                        (character.Side == Side.Ally ||
                         x.ConditionDefinition.Name != SpellBuilders.ConditionTelekinesisRestrainedName))
                    .ToArray();

                foreach (var activeCondition in conditionsToRemove)
                {
                    rulesetCharacter.RemoveCondition(activeCondition);
                }
            }

            __instance.TeleportCharacter(character, newPosition, orientation);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var teleportCharacterMethod = typeof(IGameLocationPositioningService).GetMethod("TeleportCharacter");
            var myTeleportCharacterMethod =
                new Action<IGameLocationPositioningService, GameLocationCharacter, int3,
                    LocationDefinitions.Orientation>(TeleportCharacter).Method;

            return instructions.ReplaceCalls(teleportCharacterMethod,
                "CharacterStageClassSelectionPanel.Refresh",
                new CodeInstruction(OpCodes.Call, myTeleportCharacterMethod)); // checked for Call vs CallVirtual
        }

        [UsedImplicitly]
        public static bool Prefix(EffectForm effectForm, RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            //PATCH: support for `PushesFromEffectPoint`
            // allows push/grab motion effects to work relative to casting point, instead of caster's position
            // used for Grenadier's force grenades
            // if effect source definition has marker, and forms params have position, will try to push target from that point

            var useDefaultLogic = ForcePushOrDragFromEffectPoint.TryPushFromEffectTargetPoint(effectForm, formsParams);

            if (useDefaultLogic)
            {
                useDefaultLogic = CustomSwap(effectForm, formsParams);
            }

            return useDefaultLogic;
        }

        [UsedImplicitly]
        public static void Postfix(RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            GrappleContext.ValidateGrappleAfterMotion(GameLocationCharacter.GetFromActor(formsParams.sourceCharacter));
            GrappleContext.ValidateGrappleAfterMotion(GameLocationCharacter.GetFromActor(formsParams.targetCharacter));
        }

        private static bool CustomSwap(
            EffectForm effectForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            var motionForm = effectForm.MotionForm;

            if (motionForm.Type != (MotionForm.MotionType)ExtraMotionType.CustomSwap)
            {
                return true;
            }

            var actionService = ServiceRepository.GetService<IGameLocationActionService>();
            var attacker = GameLocationCharacter.GetFromActor(formsParams.sourceCharacter);
            var defender = GameLocationCharacter.GetFromActor(formsParams.targetCharacter);

            if (attacker == null || defender == null)
            {
                return true;
            }

            const ActionDefinitions.Id ACTION_ID = (ActionDefinitions.Id)ExtraActionId.PushedCustom;

            actionService.ExecuteAction(
                new CharacterActionParams(attacker, ACTION_ID, defender.LocationPosition)
                {
                    BoolParameter = false, BoolParameter4 = false, CanBeCancelled = false, CanBeAborted = false
                }, null, true);
            actionService.ExecuteAction(
                new CharacterActionParams(defender, ActionDefinitions.Id.Pushed, attacker.LocationPosition)
                {
                    BoolParameter = false, BoolParameter4 = false, CanBeCancelled = false, CanBeAborted = false
                }, null, false);

            return false;
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.IsAnyMetamagicOptionAvailable))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsAnyMetamagicOptionAvailable_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: support for `ReplaceMetamagicOption`
            return ReplaceMetamagicOption.PatchMetamagicGetter(instructions,
                "RulesetImplementationManagerLocation.IsAnyMetamagicOptionAvailable");
        }

        [UsedImplicitly]
        public static void Postfix(
            RulesetImplementationManagerLocation __instance,
            RulesetEffectSpell rulesetEffectSpell,
            RulesetCharacter caster,
            ref bool __result)
        {
            if (__result ||
                caster is not RulesetCharacterSimulacrum &&
                caster?.OriginalFormCharacter is not RulesetCharacterSimulacrum)
            {
                return;
            }

            foreach (var metamagicOption in ReplaceMetamagicOption.GetOptions(caster))
            {
                if (!__instance.IsMetamagicOptionAvailable(
                        rulesetEffectSpell,
                        caster,
                        metamagicOption,
                        out _,
                        out _))
                {
                    continue;
                }

                __result = true;

                return;
            }
        }
    }

    //PATCH: supports light and obscurement rules
    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.ApplyCounterForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyCounterForm_Patch
    {
        private static RulesetCharacter ResolveCounterCharacter(
            RulesetCharacter character)
        {
            return character is
                RulesetCharacterHero or RulesetCharacterSimulacrum
                ? character
                : null;
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var conditionDefinitionMethod = typeof(ConditionForm).GetMethod("get_ConditionDefinition");
            var myConditionDefinitionMethod =
                new Func<ConditionForm, ConditionDefinition>(LightingAndObscurementContext.CheckForDarknessCondition).Method;
            var resolveCounterCharacter = new Func<
                RulesetCharacter,
                RulesetCharacter>(ResolveCounterCharacter).Method;
            var replacedHeroGates = 0;
            var patched = new List<CodeInstruction>();

            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Isinst &&
                    instruction.operand as Type == typeof(RulesetCharacterHero))
                {
                    replacedHeroGates++;
                    var replacement = new CodeInstruction(
                        OpCodes.Call,
                        resolveCounterCharacter);

                    replacement.labels.AddRange(instruction.labels);
                    replacement.blocks.AddRange(instruction.blocks);
                    patched.Add(replacement);
                }
                else
                {
                    patched.Add(instruction);
                }
            }

            if (replacedHeroGates != 2)
            {
                throw new InvalidOperationException(
                    "Expected two ApplyCounterForm Hero bonus gates, " +
                    $"replaced {replacedHeroGates}.");
            }

            return patched.ReplaceCalls(conditionDefinitionMethod,
                "RulesetImplementationManagerLocation.ApplyCounterForm",
                new CodeInstruction(OpCodes.Call, myConditionDefinitionMethod));
        }
    }

    [HarmonyPatch(
        typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.ApplyLightSourceForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyLightSourceForm_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            var item = formsParams.targetItem;

            if (item?.RulesetLightSource == null ||
                !RulesetEntity.TryGetEntity(
                    item.BearerGuid,
                    out RulesetCharacterSimulacrum duplicate) ||
                duplicate.CharacterInventory is not { } inventory ||
                inventory.FindSlotHoldingItem(item) is not { } slot ||
                slot.SlotTypeDefinition?.CanDisplayLight != true ||
                slot.ConfigSlot &&
                inventory.IsItemInInactiveConfiguration(item) ||
                GameLocationCharacter.GetFromActor(duplicate) is not { } location ||
                ServiceRepository.GetService<IGameLocationVisibilityService>() is not
                    { } visibility)
            {
                return;
            }

            // Native item-light registration is gated to RulesetCharacterHero even
            // though the item light and effect tracking above it are generic. Complete
            // the missing creation-time registration for a Simulacrum, retaining the
            // warning here because a duplicate at this boundary is not expected.
            visibility.AddCharacterLightSource(
                location,
                item.RulesetLightSource,
                true);
        }
    }

    [HarmonyPatch(typeof(RulesetImplementationManagerLocation),
        nameof(RulesetImplementationManagerLocation.ApplyShapeChangeForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ApplyShapeChangeForm_Patch
    {
        private static readonly List<RulesetEffectPower> PowersUsedByMe = [];
        private static readonly List<RulesetEffectSpell> SpellsCastByMe = [];

        [UsedImplicitly]
        public static bool Prefix(RulesetImplementationManagerLocation __instance, EffectForm effectForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            ApplyShapeChangeForm(__instance, effectForm, formsParams);
            return false;
        }

        private static void ApplyShapeChangeForm(RulesetImplementationManagerLocation manager,
            EffectForm effectForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams)
        {
            //Mostly original code, except for PATCHES
            
            var targetCharacter = (RulesetCharacter)formsParams.targetCharacter;
            var sourceCharacter = formsParams.sourceCharacter;

            //PATCH: allow Druids to keep concentration on spells / powers with proxy summon forms

            // foreach (var rulesetEffect in targetCharacter.SpellsCastByMe)
            // {
            //     if (rulesetEffect.TrackedLightSourceGuids.Count > 0)
            //     {
            //         rulesetEffect.Terminate(false);
            //     }
            //
            //     if (rulesetEffect.TrackedSummonedItemGuids.Count > 0)
            //     {
            //         rulesetEffect.Terminate(false);
            //     }
            // }
            //
            // foreach (var rulesetEffect in targetCharacter.PowersUsedByMe)
            // {
            //     if (rulesetEffect.TrackedLightSourceGuids.Count > 0)
            //     {
            //         rulesetEffect.Terminate(false);
            //     }
            //
            //     if (rulesetEffect.TrackedSummonedItemGuids.Count > 0)
            //     {
            //         rulesetEffect.Terminate(false);
            //     }
            // }

            //END PATCH

            var service = ServiceRepository.GetService<IGameLocationCharacterService>();
            var entityImplementation = (GameLocationCharacter)targetCharacter.EntityImplementation;
            var targetSubstitute = formsParams.targetSubstitute;
            var creatureSex = targetCharacter.Sex == CreatureSex.Female
                ? GadgetDefinitions.CreatureSex.Female
                : GadgetDefinitions.CreatureSex.Male;
            var spawnOverrides = new SpawnOverrides();
            var rulesetMonster = new RulesetCharacterMonster(targetSubstitute, 0, spawnOverrides,
                creatureSex, targetCharacter, effectForm.ShapeChangeForm.KeepMentalAbilityScores);
            var sourceFaction = sourceCharacter.CurrentFaction.Name ?? string.Empty;
            var sourceAbilityBonus = formsParams.activeEffect.ComputeSourceAbilityBonus(sourceCharacter);
            var proficiencyBonus = formsParams.activeEffect.ComputeSourceProficiencyBonus(sourceCharacter);

            targetCharacter.InflictCondition(ConditionShapeChanged, DurationType.Permanent, 0,
                TurnOccurenceType.EndOfTurn, AttributeDefinitions.TagConjure, sourceCharacter.Guid,
                sourceFaction, formsParams.effectLevel, string.Empty, 0, sourceAbilityBonus, proficiencyBonus);
            var condition = rulesetMonster.InflictCondition(
                effectForm.ShapeChangeForm.SpecialSubstituteDefinition?.Name ?? ConditionSubstituteForm,
                DurationType.Round, formsParams.activeEffect.RemainingRounds, formsParams.endOfEffect,
                AttributeDefinitions.TagConjure, sourceCharacter.Guid, sourceFaction, formsParams.effectLevel,
                string.Empty, 0, sourceAbilityBonus, proficiencyBonus);
            formsParams.activeEffect.TrackCondition(sourceCharacter, sourceCharacter.Guid, rulesetMonster,
                rulesetMonster.Guid, condition, AttributeDefinitions.TagConjure);
            var character = service.CreateCharacter(entityImplementation.ControllerId, rulesetMonster,
                entityImplementation.Side, entityImplementation.BehaviourPackage);
            ServiceRepository.GetService<IGameLocationPositioningService>().PlaceCharacter(character,
                entityImplementation.LocationPosition, entityImplementation.Orientation);
            character.SetupFromShapeChangedCharacter(entityImplementation);
            character.RefreshActionPerformances();
            service.RevealCharacter(character);
            service.ReplaceCharacter(entityImplementation, character);

            //PATCH: enforces concentration on shape change spell
            if (formsParams.activeEffect is RulesetEffectSpell rulesetEffectSpell &&
                rulesetEffectSpell.SpellDefinition.Name == SpellBuilders.ShapechangeName)
            {
                rulesetMonster.concentratedSpell = rulesetEffectSpell;
            }

            //PATCH: allows shape changers to get bonuses effects defined in features / feats / etc.
            sourceAbilityBonus = formsParams.activeEffect.ComputeSourceAbilityBonus(sourceCharacter);
            proficiencyBonus = formsParams.activeEffect.ComputeSourceProficiencyBonus(sourceCharacter);
            var creatureTags = formsParams.targetSubstitute.CreatureTags;

            foreach (var summoningAffinity in sourceCharacter
                         .FeaturesByType<FeatureDefinitionSummoningAffinity>()
                         .Where(x => creatureTags.Contains(x.RequiredMonsterTag)))
            {
                foreach (var addedCondition in summoningAffinity.AddedConditions)
                {
                    var sourceAmount = 0;

                    // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                    switch (addedCondition.AmountOrigin)
                    {
                        case ConditionDefinition.OriginOfAmount.SourceHalfHitPoints:
                            sourceAmount = addedCondition.BaseAmount +
                                           (sourceCharacter.TryGetAttributeValue(AttributeDefinitions.HitPoints) / 2);
                            break;
                        case ConditionDefinition.OriginOfAmount.SourceSpellCastingAbility:
                            sourceAmount = sourceCharacter.SpellRepertoires
                                .Select(spellRepertoire => AttributeDefinitions.ComputeAbilityScoreModifier(
                                    sourceCharacter.TryGetAttributeValue(spellRepertoire.SpellCastingAbility)))
                                .Prepend(0)
                                .Max();
                            break;
                        case ConditionDefinition.OriginOfAmount.SourceSpellAttack:
                            sourceAmount = sourceCharacter.SpellRepertoires
                                .Select(spellRepertoire => spellRepertoire.SpellAttackBonus)
                                .Prepend(0)
                                .Max();
                            break;
                    }

                    rulesetMonster.InflictCondition(
                        addedCondition.Name,
                        formsParams.durationType,
                        formsParams.durationParameter,
                        formsParams.endOfEffect,
                        AttributeDefinitions.TagEffect,
                        sourceCharacter.Guid,
                        sourceCharacter.CurrentFaction.Name,
                        formsParams.effectLevel,
                        string.Empty, sourceAmount,
                        sourceAbilityBonus,
                        proficiencyBonus);

                    // we need to re-assign max hit points as we're on a postfix
                    rulesetMonster.currentHitPoints =
                        rulesetMonster.GetAttribute(AttributeDefinitions.HitPoints).MaxValue;

                    rulesetMonster.RefreshAll();
                }
            }
        }
    }
}
