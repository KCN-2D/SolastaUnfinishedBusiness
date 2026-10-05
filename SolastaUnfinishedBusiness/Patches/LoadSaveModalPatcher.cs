using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class LoadSaveModalPatcher
{
    [HarmonyPatch(typeof(LoadSaveModal), nameof(LoadSaveModal.OnTextInputChangedCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnTextInputChangedCb_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // Typing changes selection and name validity, not the save descriptors. Keep
            // native sanitization/button handling without rebinding thumbnails and content.
            return instructions.ReplaceCalls(
                AccessTools.Method(typeof(LoadSaveModal), nameof(LoadSaveModal.RefreshLines)),
                1,
                "LoadSaveModal.OnTextInputChangedCb",
                new CodeInstruction(OpCodes.Call, new Action<LoadSaveModal>(RefreshNameState).Method));
        }

        private static void RefreshNameState(LoadSaveModal modal)
        {
            // Load mode retains native gamepad navigation and selection semantics.
            if (modal.LoadMode)
            {
                modal.RefreshLines();
                return;
            }

            var name = modal.saveLineTextField.text;
            var visibleCount = 0;

            foreach (UnityEngine.Transform child in modal.loadSaveLinesTable)
            {
                if (!child.gameObject.activeSelf || !child.TryGetComponent<LoadSaveLine>(out var line))
                {
                    continue;
                }

                visibleCount++;
                var selected = string.Equals(name, line.Name, StringComparison.Ordinal);

                if (line.Selected != selected)
                {
                    line.Selected = selected;
                }
            }

            var canSave = modal.IsSaveNameValid();

            // Native limits count filtered visible saves, but permits overwriting any
            // existing descriptor at the limit. Preserve both predicates exactly.
            if (visibleCount >= 50)
            {
                canSave = canSave && ServiceRepository.GetService<IGameSerializationService>()
                    .SaveDescriptors.Exists(descriptor => descriptor.Filename == name);
            }

            modal.baseSaveBtnInteractable = canSave;
            modal.saveButton.interactable = canSave;
        }
    }
}
