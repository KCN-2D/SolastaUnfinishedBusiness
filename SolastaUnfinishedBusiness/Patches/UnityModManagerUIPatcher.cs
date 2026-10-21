using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityModManagerNet;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class UnityModManagerUIPatcher
{
    [HarmonyPatch(typeof(UnityModManager.UI), nameof(UnityModManager.UI.ToggleWindow), typeof(bool))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ToggleWindow_Patch
    {
        //PATCH: prevents game from receiving input if Mod UI is open
        [UsedImplicitly]
        public static void Prefix(UnityModManager.UI __instance, out bool __state)
        {
            __state = __instance.Opened;
        }

        [UsedImplicitly]
        public static void Postfix(UnityModManager.UI __instance, bool __state)
        {
            ModManagerUI.Set(__state, __instance.Opened);
        }
    }

    public static class ModManagerUI
    {
        private const float InputReleaseDelay = 0.1f;
        private static float _releaseInputAt;

        public static bool IsOpen
        {
            get
            {
                var ui = UnityModManager.UI.Instance;

                return ui && (ui.Opened || Time.realtimeSinceStartup < _releaseInputAt);
            }
        }

        internal static void Set(bool wasOpen, bool isOpen)
        {
            if (isOpen)
            {
                _releaseInputAt = 0;
            }
            else if (wasOpen)
            {
                // Keep the closing input out of the game. A subsequent close must
                // own its full delay even if the window was briefly reopened.
                _releaseInputAt = Time.realtimeSinceStartup + InputReleaseDelay;
            }
        }
    }
}
