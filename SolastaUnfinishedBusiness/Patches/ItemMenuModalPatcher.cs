using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using static ActionDefinitions;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class ItemMenuModalPatcher
{
    private static RulesetCharacterHero FindDeityMarkHero(
        ItemMenuModal itemMenuModal,
        RulesetCharacterSimulacrum duplicate)
    {
        var deity = duplicate?.DeityDefinition;

        if (deity == null)
        {
            return null;
        }

        var transport = SimulacrumEquipmentPanel.GetTransportHero(
            itemMenuModal?.GuiCharacter);

        if (HasSameDeity(transport, deity))
        {
            return transport;
        }

        return Gui.GameCampaign?.Party?.CharactersList
            .Select(character => character?.RulesetCharacter)
            .OfType<RulesetCharacterHero>()
            .FirstOrDefault(hero => HasSameDeity(hero, deity));

        static bool HasSameDeity(
            RulesetCharacter character,
            DeityDefinition expectedDeity)
        {
            return string.Equals(
                character?.DeityDefinition?.Name,
                expectedDeity?.Name,
                StringComparison.Ordinal);
        }
    }

    internal static bool IsUnsupportedSimulacrumDocument(ItemDefinition itemDefinition)
    {
        return itemDefinition is
        {
            IsDocument: true,
            DocumentDescription: { } document
        } &&
               (document.RecipeDefinition != null || document.DestroyAfterReading);
    }

    [HarmonyPatch(typeof(ItemMenuModal), nameof(ItemMenuModal.ActivateFunction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ActivateFunction_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: do not use Bonus Action to use Potion/Poison if 2024 BA item use is active
            //usually if character has ability to use BA for item manipulation, game will try to use BA first
            //But with this option poisons and potions get separate menu action to use BA

            var oldMethod = AccessTools.Method(typeof(CharacterActionPanel),
                nameof(CharacterActionPanel.OnActivateActionExternal));
            var newMethod = new Action<
                CharacterActionPanel,
                Id,
                GameLocationCharacter,
                RulesetItemDevice,
                RulesetDeviceFunction
            >(PatchedOnActivateActionExternal).Method;

            return instructions.ReplaceCalls(oldMethod, "ItemMenuModal.ActivateFunction",
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Call, newMethod));
        }

        private static void PatchedOnActivateActionExternal(
            CharacterActionPanel panel,
            Id actionId,
            GameLocationCharacter externalCharacter,
            RulesetItemDevice device,
            RulesetDeviceFunction function)
        {
            if (Gui.Battle != null)
            {
                var tags = device.UsableDeviceDescription.usableDeviceTags;
                if (Main.Settings.EnablePotionsBonusAction2024 && tags.Contains(GameConstants.TagPotion)
                    || Main.Settings.EnablePoisonsBonusAction2024 && tags.Contains(GameConstants.TagPoison))
                {
                    var power = function.DeviceFunctionDescription.FeatureDefinitionPower;
                    actionId = power.ActivationTime == ActivationTime.BonusAction
                        ? Id.UseItemBonus
                        : Id.UseItemMain;
                }
            }

            panel.OnActivateActionExternal(actionId, externalCharacter);
        }
    }

    [HarmonyPatch(typeof(ItemMenuModal), nameof(ItemMenuModal.SetupFromItem))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetupFromItem_Patch
    {
        private static readonly MethodInfo RegisterButtonMethod = AccessTools.Method(
            typeof(ItemMenuModal),
            "RegisterButton",
            [typeof(ItemMenuButton.ItemButtonInfo)]);

#if false
        private static bool IsSpellDefinitionOnRepertoire(
            RulesetActor rulesetActor,
            RulesetSpellRepertoire spellRepertoire,
            SpellDefinition spellDefinition)
        {
            if (spellRepertoire.SpellCastingFeature.HasAccessToSpell(spellDefinition) ||
                spellRepertoire.AutoPreparedSpells.Contains(spellDefinition) ||
                spellRepertoire.IsSpellDefinitionInExtraSpells(spellDefinition))
            {
                return true;
            }

            // only exceptional case a Wizard in game can get additional spells
            return rulesetActor.HasAnyFeature(MagicAffinityGreenmageGreenMagicList) &&
                   MagicAffinityGreenmageGreenMagicList.ExtendedSpellList.ContainsSpell(spellDefinition);
        }
#endif

        //PATCH: allows mark deity to work with MC heroes (Multiclass)
        private static bool RequiresDeity(ItemMenuModal itemMenuModal)
        {
            if (itemMenuModal.GuiCharacter?.RulesetCharacter is
                RulesetCharacterSimulacrum duplicate)
            {
                // The duplicate can be a Paladin/Cleric copied by a caster with no
                // deity. Keep the native synchronized command, but only expose it
                // when a real Hero with the duplicate's snapshotted deity can carry
                // that command's deity identity.
                var deityHero = FindDeityMarkHero(itemMenuModal, duplicate);
                var eligible = deityHero != null;

                return eligible;
            }

            return GetTransportHero(itemMenuModal.GuiCharacter)
                ?.ClassesHistory.Exists(x => x.RequiresDeity) == true;
        }

        //PATCH: only allow to scribe spells the scriber class can do
        private static List<RulesetSpellRepertoire> SpellRepertoires(
            RulesetCharacterHero rulesetCharacterHero,
            GuiEquipmentItem guiEquipmentItem)
        {
            if (rulesetCharacterHero == null)
            {
                return [];
            }

            if (guiEquipmentItem.EquipementItem is not RulesetItemDevice rulesetItemDevice ||
                rulesetItemDevice.UsableFunctions[0] is null)
            {
                return rulesetCharacterHero.SpellRepertoires;
            }

            return rulesetCharacterHero.SpellRepertoires
                .Where(x => x.SpellCastingFeature.SpellKnowledge == SpellKnowledge.Spellbook)
                .ToList();
        }

        private static void SetInventorySubject(
            ItemMenuModal itemMenuModal,
            GuiCharacter transportCharacter,
            GuiEquipmentItem guiEquipmentItem)
        {
            if (SimulacrumEquipmentPanel.TryGetActiveCharacter(out var duplicate) &&
                guiEquipmentItem?.EquipementItem?.BearerGuid == duplicate.Guid)
            {
                var locationCharacter = GameLocationCharacter.GetFromActor(duplicate);

                itemMenuModal.GuiCharacter = locationCharacter != null
                    ? new GuiCharacter(locationCharacter)
                    : new GuiCharacter(duplicate);

                return;
            }

            itemMenuModal.GuiCharacter = transportCharacter;
        }

        private static RulesetCharacterHero GetTransportHero(GuiCharacter guiCharacter)
        {
            return SimulacrumEquipmentPanel.GetTransportHero(guiCharacter);
        }

        private static DeityDefinition GetInventorySubjectDeity(
            RulesetCharacter commandCharacter,
            ItemMenuModal itemMenuModal)
        {
            return itemMenuModal.GuiCharacter?.RulesetCharacter is
                RulesetCharacterSimulacrum duplicate
                ? duplicate.DeityDefinition
                : commandCharacter?.DeityDefinition;
        }

        private static void RegisterButtonForInventorySubject(
            ItemMenuModal itemMenuModal,
            ItemMenuButton.ItemButtonInfo buttonInfo)
        {
            if (itemMenuModal.GuiCharacter?.RulesetCharacter is RulesetCharacterSimulacrum)
            {
                if ((buttonInfo.type == ItemMenuButton.ItemButtonType.Examine &&
                     IsUnsupportedSimulacrumDocument(
                         itemMenuModal.GuiEquipmentItem?.EquipementItem?.ItemDefinition)) ||
                    buttonInfo.type is not ItemMenuButton.ItemButtonType.UseFunction and
                        not ItemMenuButton.ItemButtonType.Examine and
                        not ItemMenuButton.ItemButtonType.Drop and
                        not ItemMenuButton.ItemButtonType.Split and
                        not ItemMenuButton.ItemButtonType.MarkDeity)
                {
                    return;
                }
            }

            RegisterButtonMethod.Invoke(itemMenuModal, [buttonInfo]);
        }

        private static ActionStatus PatchedActionStatus(GameLocationCharacter character,
            ActionType actionType, ActionScope actionScope, bool ignoreMovePoints,
            RulesetDeviceFunction usableFunction)
        {
            //PATCH: allow action if this is extra bonus function enabled from 2024 item usage rules
            var power = usableFunction.DeviceFunctionDescription?.featureDefinitionPower;
            if (power != null && Tabletop2024Context.ItemBonusPowers.ContainsValue(power))
            {
                return ActionStatus.Available;
            }

            return character.GetActionTypeStatus(actionType, actionScope, ignoreMovePoints);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var requiresDeityMethod = typeof(CharacterClassDefinition).GetMethod("get_RequiresDeity");
            var myRequiresDeityMethod = new Func<ItemMenuModal, bool>(RequiresDeity).Method;

            var spellRepertoiresMethod = typeof(RulesetCharacter).GetMethod("get_SpellRepertoires");
            var mySpellRepertoiresMethod =
                new Func<RulesetCharacterHero, GuiEquipmentItem, List<RulesetSpellRepertoire>>(SpellRepertoires).Method;

            var oldActionStatus = AccessTools.Method(typeof(GameLocationCharacter),
                nameof(GameLocationCharacter.GetActionTypeStatus));
            var newActionStatus = typeof(SetupFromItem_Patch).GetMethod(nameof(PatchedActionStatus),
                BindingFlags.Static | BindingFlags.NonPublic);
            var guiCharacterSetter = AccessTools.PropertySetter(
                typeof(ItemMenuModal),
                nameof(ItemMenuModal.GuiCharacter));
            var setInventorySubject = new Action<
                ItemMenuModal,
                GuiCharacter,
                GuiEquipmentItem>(SetInventorySubject).Method;
            var rulesetCharacterHeroGetter = AccessTools.PropertyGetter(
                typeof(GuiCharacter),
                nameof(GuiCharacter.RulesetCharacterHero));
            var getTransportHero = new Func<GuiCharacter, RulesetCharacterHero>(GetTransportHero).Method;
            var deityDefinitionGetter = AccessTools.PropertyGetter(
                typeof(RulesetCharacter),
                nameof(RulesetCharacter.DeityDefinition));
            var getInventorySubjectDeity = new Func<
                RulesetCharacter,
                ItemMenuModal,
                DeityDefinition>(GetInventorySubjectDeity).Method;
            var registerButton = AccessTools.Method(
                typeof(ItemMenuModal),
                "RegisterButton",
                [typeof(ItemMenuButton.ItemButtonInfo)]);
            var registerButtonForInventorySubject = new Action<
                ItemMenuModal,
                ItemMenuButton.ItemButtonInfo>(RegisterButtonForInventorySubject).Method;

            return instructions
                .ReplaceCall(guiCharacterSetter, 1, "ItemMenuModal.SetupFromItem.InventorySubject",
                    new CodeInstruction(OpCodes.Ldarg_3),
                    new CodeInstruction(OpCodes.Call, setInventorySubject))
                .ReplaceCalls(rulesetCharacterHeroGetter, "ItemMenuModal.SetupFromItem.TransportHero",
                    new CodeInstruction(OpCodes.Call, getTransportHero))
                .ReplaceCalls(deityDefinitionGetter, "ItemMenuModal.SetupFromItem.InventorySubjectDeity",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, getInventorySubjectDeity))
                .ReplaceCalls(registerButton, "ItemMenuModal.SetupFromItem.FilterButtons",
                    new CodeInstruction(OpCodes.Call, registerButtonForInventorySubject))
                .ReplaceCalls(requiresDeityMethod, "ItemMenuModal.SetupFromItem1",
                    new CodeInstruction(OpCodes.Pop),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, myRequiresDeityMethod))
                .ReplaceCalls(spellRepertoiresMethod, "ItemMenuModal.SetupFromItem2",
                    new CodeInstruction(OpCodes.Ldarg_3),
                    new CodeInstruction(OpCodes.Call, mySpellRepertoiresMethod))
                .ReplaceCall(oldActionStatus, 1, "ItemMenuModal.SetupFromItem3",
                    new CodeInstruction(OpCodes.Ldloc_S, 7),
                    new CodeInstruction(OpCodes.Call, newActionStatus));
        }
    }

    [HarmonyPatch(typeof(ItemMenuModal), "ItemButtonClicked")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ItemButtonClicked_Patch
    {
        [UsedImplicitly]
        private static bool Prefix(
            ItemMenuModal __instance,
            ItemMenuButton.ItemButtonInfo __0)
        {
            if (__instance.GuiCharacter?.RulesetCharacter is not
                RulesetCharacterSimulacrum duplicate)
            {
                return true;
            }

            var item = __instance.GuiEquipmentItem?.EquipementItem;

            if (__0.type == ItemMenuButton.ItemButtonType.MarkDeity)
            {
                var deityHero = FindDeityMarkHero(__instance, duplicate);
                var inventoryCommands =
                    ServiceRepository.GetService<IInventoryCommandService>();

                if (deityHero != null && item != null && inventoryCommands != null)
                {
                    inventoryCommands.MarkDeity(deityHero, item);
                    __instance.Hide(false);
                }

                return false;
            }

            if (__0.type == ItemMenuButton.ItemButtonType.Examine &&
                IsUnsupportedSimulacrumDocument(item?.ItemDefinition))
            {
                return false;
            }

            return true;
        }

        [NotNull]
        [UsedImplicitly]
        private static IEnumerable<CodeInstruction> Transpiler(
            [NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var rulesetCharacterHeroGetter = AccessTools.PropertyGetter(
                typeof(GuiCharacter),
                nameof(GuiCharacter.RulesetCharacterHero));
            var getTransportHero = AccessTools.Method(
                typeof(SimulacrumEquipmentPanel),
                nameof(SimulacrumEquipmentPanel.GetTransportHero));

            return instructions.ReplaceCalls(
                rulesetCharacterHeroGetter,
                "ItemMenuModal.ItemButtonClicked.TransportHero",
                new CodeInstruction(OpCodes.Call, getTransportHero));
        }
    }

}
