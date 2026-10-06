using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameManagerPatcher
{
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.Update))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Update_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameManager __instance)
        {
            LoadingContext.UpdateLoadingState(__instance, __instance.gameCoroutine, __instance.sceneCoroutine);
        }

        [UsedImplicitly]
        public static void Postfix(GameManager __instance)
        {
            LoadingContext.UpdateLoadingState(__instance, __instance.gameCoroutine, __instance.sceneCoroutine);
        }

        [UsedImplicitly]
        public static Exception Finalizer(GameManager __instance, Exception __exception)
        {
            if (__exception != null)
            {
                LoadingContext.Release(__instance);
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.Shutdown))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Shutdown_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameManager __instance)
        {
            LoadingContext.Release(__instance);
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.BindPostDatabase))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BindPostDatabase_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameManager __instance)
        {
            //PATCH: loads all mod contexts
            BootContext.Startup(__instance);
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.BindServiceSettings))]
    internal static class BindServiceSettings_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameManager __instance)
        {
            //PATCH: add unofficial languages before game tries to load the game settings xml
            var languageByCode = __instance.languageByCode;

            if (languageByCode == null)
            {
                return;
            }

            foreach (var language in TranslatorContext.Languages
                         .Where(language => !languageByCode.ContainsKey(language.Code)))
            {
                languageByCode.Add(language.Code, language.Text);
            }
        }
    }
}
