using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class TooltipFeatureHeaderPatcher
{
    [HarmonyPatch(typeof(TooltipFeatureHeader), nameof(TooltipFeatureHeader.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var width = AccessTools.PropertyGetter(typeof(Texture), nameof(Texture.width));
            var height = AccessTools.PropertyGetter(typeof(Texture), nameof(Texture.height));

            // Native cover sizing uses the entire texture, which distorts sprites cut from an atlas.
            // Replace both dimensions together, keeping the native mask, anchoring and cover branches.
            if (code.Count(instruction => instruction.Calls(width)) != 1 ||
                code.Count(instruction => instruction.Calls(height)) != 1)
            {
                Main.Error("Failed to apply transpiler patch [TooltipFeatureHeader.Bind]: " +
                           "expected one texture width and height calculation.");

                return code;
            }

            return code
                .ReplaceCalls(width, "TooltipFeatureHeader.Bind.SpriteWidth",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(Bind_Patch), nameof(GetSpriteWidth))))
                .ReplaceCalls(height, "TooltipFeatureHeader.Bind.SpriteHeight",
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(Bind_Patch), nameof(GetSpriteHeight))));
        }

        private static float GetSpriteWidth(Texture texture, TooltipFeatureHeader header)
        {
            var sprite = header.image ? header.image.sprite : null;

            return sprite ? sprite.rect.width : texture ? texture.width : 1f;
        }

        private static float GetSpriteHeight(Texture texture, TooltipFeatureHeader header)
        {
            var sprite = header.image ? header.image.sprite : null;

            return sprite ? sprite.rect.height : texture ? texture.height : 1f;
        }
    }
}
