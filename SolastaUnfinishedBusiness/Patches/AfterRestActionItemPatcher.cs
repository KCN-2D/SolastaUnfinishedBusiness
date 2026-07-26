using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Collections;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Diagnostics;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class AfterRestActionItemPatcher
{
    [HarmonyPatch(typeof(AfterRestActionItem), "ExecuteAsync")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteAsync_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(AfterRestActionItem __instance, ref IEnumerator __result)
        {
            if (!SimulacrumBehavior.IsRepairActivity(__instance))
            {
                return true;
            }

            __result = ExecuteRepairAsync(__instance);

            return false;
        }
    }

    [HarmonyPatch(typeof(AfterRestActionItem), nameof(AfterRestActionItem.OnExecuteCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnExecuteCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(AfterRestActionItem __instance)
        {
            var activity = __instance.RestActivityDefinition;
            var power = activity?.Functor == PowerBundleContext.UseCustomRestPowerFunctorName
                ? __instance.Hero.UsablePowers.FirstOrDefault(usablePower =>
                    usablePower.PowerDefinition.Name == activity.StringParameter)
                : null;
            var selection = power?.PowerDefinition.GetFirstSubFeatureOfType<ICustomRestPowerSelection>();

            if (selection?.TryOpen(__instance) == true)
            {
                return false;
            }

            //PATCH: replaces callback execution for bundled powers to show sub-power selection
            return PowerBundle.ExecuteAfterRestCb(__instance);
        }
    }

    [HarmonyPatch(typeof(AfterRestActionItem), nameof(AfterRestActionItem.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(AfterRestActionItem __instance)
        {
            var activity = __instance.RestActivityDefinition;
            var hero = __instance.Hero;

            if (activity.functor != PowerBundleContext.UseCustomRestPowerFunctorName)
            {
                return;
            }

            var power = hero.UsablePowers.FirstOrDefault(usablePower =>
                usablePower.PowerDefinition.Name == activity.StringParameter);

            if (power == null)
            {
                return;
            }

            //PATCH: use power tooltip for custom use power functors
            ServiceRepository.GetService<IGuiWrapperService>()
                .GetGuiPowerDefinition(power.PowerDefinition.Name)
                .SetupTooltip(__instance.GuiTooltip, hero);

            //PATCH: allow customized titles on use rest power
            var getTitle = power.PowerDefinition.GetFirstSubFeatureOfType<ModifyRestPowerTitleHandler>();

            if (getTitle != null)
            {
                __instance.titleLabel.Text = getTitle(hero);
            }
        }
    }

    private static IEnumerator ExecuteRepairAsync(AfterRestActionItem item)
    {
        item.executing = true;
        SimulacrumRepairInput.BeginExecution(item.Hero);
        var completed = false;

        try
        {
            var parameters = new FunctorParametersDescription
            {
                RestingHero = item.Hero,
                StringParameter = item.RestActivityDefinition.StringParameter
            };
            var gameRestingService = ServiceRepository.GetService<IGameRestingService>();

            yield return ServiceRepository.GetService<IFunctorService>()
                .ExecuteFunctorAsync(item.RestActivityDefinition.Functor, parameters, gameRestingService);

            yield return null;

            var actionService = ServiceRepository.GetService<IGameLocationActionService>();
            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
            bool needsToWait;

            do
            {
                needsToWait = characterService.PartyCharacters
                    .Any(partyCharacter => actionService.IsCharacterActing(partyCharacter));

                if (needsToWait)
                {
                    yield return null;
                }
            } while (needsToWait);

            if (SimulacrumRepairInput.TryTakeSuccessfulExecution(item.Hero))
            {
                SimulacrumDiagnostics.RecordRepair(item.Hero, "activity-consumed", 0, 0);
                item.AfterRestActionTaken?.Invoke();
            }
            else
            {
                SimulacrumDiagnostics.RecordRepair(item.Hero, "activity-retained", 0, 0);
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                SimulacrumRepairInput.AbortExecution(item.Hero);

                var usablePower = item.Hero.UsablePowers.FirstOrDefault(power =>
                    power.PowerDefinition.Name == item.RestActivityDefinition.StringParameter);

                if (usablePower != null && usablePower.RemainingUses < usablePower.MaxUses)
                {
                    item.Hero.RepayPowerUse(usablePower);
                }

                SimulacrumDiagnostics.RecordRepair(item.Hero, "activity-faulted", 0, 0);
            }

            SimulacrumRepairInput.TryTakeSuccessfulExecution(item.Hero);
            item.executing = false;

            if (item.button)
            {
                item.button.interactable = true;
            }
        }
    }
}
