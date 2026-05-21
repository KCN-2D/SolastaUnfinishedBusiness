using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using static EffectForm;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiEffectsFormaterPatcher
{
    [HarmonyPatch(typeof(GuiEffectsFormater), nameof(GuiEffectsFormater.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ref List<EffectForm> effectForms)
        {
            if (effectForms is not { Count: > 1 })
            {
                return;
            }

            var filteredEffectForms = FilterDuplicateRemoveDarknessForms(effectForms);

            if (filteredEffectForms != null)
            {
                effectForms = filteredEffectForms;
            }
        }

        private static List<EffectForm> FilterDuplicateRemoveDarknessForms(List<EffectForm> effectForms)
        {
            HashSet<string> removeDarknessDescriptions = null;
            List<EffectForm> filteredEffectForms = null;

            for (var i = 0; i < effectForms.Count; i++)
            {
                var effectForm = effectForms[i];

                if (TryFormatRemoveDarknessEffectForm(effectForm, out var description) &&
                    !string.IsNullOrEmpty(description))
                {
                    removeDarknessDescriptions ??= new HashSet<string>(StringComparer.Ordinal);

                    if (!removeDarknessDescriptions.Add(description))
                    {
                        filteredEffectForms ??= CopyEffectFormsUntil(effectForms, i);

                        continue;
                    }
                }

                filteredEffectForms?.Add(effectForm);
            }

            return filteredEffectForms;
        }

        private static List<EffectForm> CopyEffectFormsUntil(List<EffectForm> effectForms, int count)
        {
            var filteredEffectForms = new List<EffectForm>(effectForms.Count);

            for (var i = 0; i < count; i++)
            {
                filteredEffectForms.Add(effectForms[i]);
            }

            return filteredEffectForms;
        }

        private static bool TryFormatRemoveDarknessEffectForm(EffectForm effectForm, out string description)
        {
            description = null;

            switch (effectForm.FormType)
            {
                case EffectFormType.Alteration
                    when effectForm.AlterationForm.AlterationType == AlterationForm.Type.RemoveDarkness:
                    description = Gui.FormatAlterationForm(effectForm.AlterationForm);

                    return true;

                case EffectFormType.Counter
                    when effectForm.CounterForm.Type == CounterForm.CounterType.RemoveDarkness:
                    description = Gui.FormatCounterForm(effectForm.CounterForm);

                    return true;

                default:
                    return false;
            }
        }
    }
}
