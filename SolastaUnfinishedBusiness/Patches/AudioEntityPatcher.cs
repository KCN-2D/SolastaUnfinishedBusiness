using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class AudioEntityPatcher
{
    [HarmonyPatch(typeof(AudioEntity), nameof(AudioEntity.AnimationEvent))]
    [UsedImplicitly]
    public static class AnimationEvent_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(AnimationEventDynamicParameters dynamicParameters) =>
            CustomModels.UsesNativeAnimationAudio(dynamicParameters.graphicsCharacter);
    }
}
