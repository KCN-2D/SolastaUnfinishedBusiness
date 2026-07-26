using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Validators;
using UnityEngine;
using UnityEngine.UI;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.Diagnostics;

// Temporary diagnostics for the Simulacrum playtest. Keep all formatting and
// duplicate suppression here so removing diagnostics does not alter behavior.
internal static class SimulacrumDiagnostics
{
    // Set this to false to disable all temporary Player.log diagnostics without
    // touching the production call sites during playtest comparison.
    private static readonly bool Enabled = true;
    private const string Prefix = "[UB-SIM-DIAG]";
    private const string ProbeRevision =
        "simulacrum-runtime-parity-r31";
    private const int MaximumDefinitionNames = 80;
    private const int MaximumEventKeys = 1024;
    private const int MaximumStateKeys = 512;
    private const int MaximumValidationKeys = 512;
    private static readonly HashSet<string> EventKeys = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> LastStates = new(StringComparer.Ordinal);
    private static readonly HashSet<string> InvocationValidationKeys = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SpellValidationKeys = new(StringComparer.Ordinal);
    private static long _damageRollSequence;
    private static long _spellActivationSequence;
    private static bool RevisionReported;

    internal static void RecordInitialAttributes(
        RulesetCharacterSimulacrum character,
        ulong sourceGuid,
        string stage,
        int expectedCount)
    {
        if (character == null)
        {
            Write(
                "initial-attributes",
                $"stage={stage} source={sourceGuid} character=null expected={expectedCount}");

            return;
        }

        character.TryGetAttribute(
            AttributeDefinitions.ProficiencyBonus,
            out var proficiencyBonus);

        var detail =
            $"stage={stage} source={sourceGuid} guid={character.Guid} " +
            $"expected={expectedCount} actual={character.Attributes.Count} " +
            $"hasPb={proficiencyBonus != null} pbBase={proficiencyBonus?.BaseValue ?? -1} " +
            $"pbCurrent={proficiencyBonus?.CurrentValue ?? -1}";

        WriteChanged(
            "initial-attributes",
            $"{sourceGuid}|{character.Guid}|{stage}",
            detail);
    }

    internal static void RecordInventory(
        RulesetCharacterSimulacrum character,
        string stage,
        CharacterInspectionScreen screen = null)
    {
        if (stage.StartsWith("before-", StringComparison.Ordinal) ||
            stage is
                "bind-enter" or
                "character-plate-bound" or
                "inventory-screen-prepared" or
                "inventory-panel-bound" or
                "bind-complete" or
                "after-screen-show" or
                "character-viewport-bound" or
                "equipment-layout-bound" or
                "shortcuts-bound" or
                "stop-drag-complete" or
                "management-context-bound" or
                "treasury-bound" or
                "unbind-start")
        {
            return;
        }

        if (character == null)
        {
            Write("inventory", $"stage={stage} character=null");

            return;
        }

        var inventory = character.CharacterInventory;
        var items = new List<RulesetItem>();

        inventory?.EnumerateAllItems(items, true, false);
        var disabledSlots = inventory?.InventorySlotsByName?.Values.Count(slot => slot.Disabled) ?? -1;
        RulesetInventorySlot mainHand = null;
        RulesetInventorySlot offHand = null;

        inventory?.InventorySlotsByName?.TryGetValue(
            EquipmentDefinitions.SlotTypeMainHand,
            out mainHand);
        inventory?.InventorySlotsByName?.TryGetValue(
            EquipmentDefinitions.SlotTypeOffHand,
            out offHand);
        var itemDetails = string.Join(
            ",",
            items
                .Where(item => item?.ItemDefinition != null)
                .OrderBy(item => item.ItemDefinition.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Guid)
                .Take(MaximumDefinitionNames)
                .Select(DescribeInventoryItem));
        var mainTwoHanded = mainHand?.EquipedItem?.ItemDefinition?
            .WeaponDescription?.WeaponTags.Contains(TagsDefinitions.WeaponTagTwoHanded) == true;
        character.ComputeEncumbranceThresholds(out _, out _, out var maximumWeight);

        var detail =
            $"stage={stage} guid={character.Guid} lifecycle={character.LifecycleState} " +
            $"bearer={inventory?.BearerGuid ?? 0} items={items.Count} " +
            $"slots={inventory?.InventorySlotsByName?.Count ?? 0} " +
            $"disabledSlots={disabledSlots} maxCarry={maximumWeight:0.###} " +
            $"main={DescribeInventorySlot(mainHand)} off={DescribeInventorySlot(offHand)} " +
            $"mainTwoHanded={mainTwoHanded} itemDetails={itemDetails} " +
            $"canCarryOne={character.CanCarryWeight(1f)} " +
            $"toggleChildren={screen?.toggleGroup?.transform?.childCount ?? -1} " +
            $"spellPanelChildren={screen?.spellPanelsContainer?.childCount ?? -1} " +
            $"inspected={screen?.InspectedCharacter?.RulesetCharacter?.Guid ?? 0} " +
            $"screenActive={screen?.gameObject?.activeSelf.ToString() ?? "<null>"} " +
            $"screenVisible={screen?.Visible.ToString() ?? "<null>"} " +
            $"panelActive={screen?.InventoryPanel?.gameObject?.activeSelf.ToString() ?? "<null>"} " +
            $"panelVisible={screen?.InventoryPanel?.Visible.ToString() ?? "<null>"} " +
            $"panelShowing={screen?.InventoryPanel?.Showing.ToString() ?? "<null>"} " +
            $"panelHiding={screen?.InventoryPanel?.Hiding.ToString() ?? "<null>"} " +
            $"abilitiesActive={screen?.abilityScoresListingPanel?.gameObject?.activeSelf.ToString() ?? "<null>"} " +
            $"abilitiesVisible={screen?.abilityScoresListingPanel?.gameObject?.activeInHierarchy.ToString() ?? "<null>"} " +
            $"statsActive={screen?.characterStatsPanel?.gameObject?.activeSelf.ToString() ?? "<null>"} " +
            $"statsVisible={screen?.characterStatsPanel?.gameObject?.activeInHierarchy.ToString() ?? "<null>"} " +
            $"personalContainer={screen?.InventoryPanel?.MainContainerPanel?.Container?.Guid ?? 0} " +
            $"personalSlots={screen?.InventoryPanel?.MainContainerPanel?.BoundSlotBoxes?.Count ?? -1}";

        WriteChanged("inventory", $"{character.Guid}|{stage}", detail);
        RecordInspectionPanels(character, stage, screen);
    }

    private static string DescribeInventoryItem(RulesetItem item)
    {
        return item?.ItemDefinition == null
            ? "<empty>"
            : $"{item.ItemDefinition.Name}#{item.Guid}@{item.BearerGuid}";
    }

    private static string DescribeInventorySlot(RulesetInventorySlot slot)
    {
        if (slot == null)
        {
            return "<missing>";
        }

        var shadow = slot.ShadowedSlot;

        return $"{DescribeInventoryItem(slot.EquipedItem)}" +
               $"[disabled={slot.Disabled},reason={slot.DisabledReason}," +
               $"shadow={shadow?.Name ?? "<none>"}:{DescribeInventoryItem(shadow?.EquipedItem)}]";
    }

    internal static void RecordInspectionPanels(
        RulesetCharacter character,
        string stage,
        CharacterInspectionScreen screen)
    {
        if (character == null || !screen)
        {
            return;
        }

        var abilities = screen.abilityScoresListingPanel;
        var abilityRect = abilities.rectTransform;
        var abilityCanvas = abilities.CanvasGroup;
        var abilityBoxes = string.Join(
            ",",
            abilities.abilityScoreBoxes.Select((box, index) =>
                $"{index}:{box?.AbilityScore?.Name ?? "<null>"}:" +
                $"{box?.gameObject.activeSelf.ToString() ?? "<null>"}/" +
                $"{box?.gameObject.activeInHierarchy.ToString() ?? "<null>"}:" +
                $"{box?.canvasGroup?.alpha.ToString("0.##") ?? "<null>"}"));
        var attacks = screen.attackModesPanel;
        var attackRect = attacks.rectTransform;
        var attackCanvas = attacks.CanvasGroup;
        var attackModes = string.Join(
            ",",
            attacks.relevantAttackModes
                .Where(mode => mode != null)
                .Select(mode =>
                    $"{mode.SourceDefinition?.Name ?? "<null>"}:{mode.ActionType}"));
        var activeAttackBoxes = attacks.attackModesTable
            ? attacks.attackModesTable.Cast<Transform>()
                .Count(child => child.gameObject.activeSelf)
            : -1;
        var detail =
            $"stage={stage} guid={character.Guid} runtime={character.GetType().Name} " +
            $"abilities={abilities.gameObject.activeSelf}/" +
            $"{abilities.gameObject.activeInHierarchy} " +
            $"abilityPanel={abilities.Visible}/{abilities.Showing}/{abilities.Hiding} " +
            $"abilityAlpha={abilityCanvas?.alpha.ToString("0.##") ?? "<null>"} " +
            $"abilityRect={abilityRect.rect.width:0.#}x{abilityRect.rect.height:0.#}@" +
            $"{abilityRect.anchoredPosition.x:0.#},{abilityRect.anchoredPosition.y:0.#} " +
            $"abilityScale={abilityRect.localScale.x:0.##}," +
            $"{abilityRect.localScale.y:0.##},{abilityRect.localScale.z:0.##} " +
            $"abilityBoxes={abilityBoxes} " +
            $"attacks={attacks.gameObject.activeSelf}/{attacks.gameObject.activeInHierarchy} " +
            $"attackPanel={attacks.Visible}/{attacks.Showing}/{attacks.Hiding} " +
            $"attackAlpha={attackCanvas?.alpha.ToString("0.##") ?? "<null>"} " +
            $"attackRect={attackRect.rect.width:0.#}x{attackRect.rect.height:0.#}@" +
            $"{attackRect.anchoredPosition.x:0.#},{attackRect.anchoredPosition.y:0.#} " +
            $"attackChildren={attacks.attackModesTable?.childCount ?? -1} " +
            $"attackActiveChildren={activeAttackBoxes} modes={attackModes}";

        WriteChanged(
            "inspection-panels",
            $"{character.Guid}|{stage}",
            detail);
    }

    internal static void RecordCharacter(
        RulesetCharacterSimulacrum character,
        string stage)
    {
        if (character == null)
        {
            Write("character", $"stage={stage} character=null");

            return;
        }

        var languages = new List<string>();

        character.EnumerateKnownLanguages(languages);
        SimulacrumBehavior.TryGetClassLevels(character, out var classLevels);
        var subclasses = classLevels?
            .Select(entry =>
            {
                SimulacrumBehavior.TryGetPrimarySubclass(
                    character,
                    entry.ClassDefinition,
                    out var subclass);

                return subclass == null
                    ? null
                    : $"{entry.ClassDefinition?.Name}:{subclass.Name}";
            })
            .Where(entry => entry != null);

        var detail =
            $"stage={stage} guid={character.Guid} lifecycle={character.LifecycleState} " +
            $"hp={character.CurrentHitPoints}/" +
            $"{character.TryGetAttributeValue(AttributeDefinitions.HitPoints)} " +
            $"ac={character.TryGetAttributeValue(AttributeDefinitions.ArmorClass)} " +
            $"level={character.TryGetAttributeValue(AttributeDefinitions.CharacterLevel)} " +
            $"pb={character.TryGetAttributeValue(AttributeDefinitions.ProficiencyBonus)} " +
            $"attacksAttribute={character.TryGetAttributeValue(AttributeDefinitions.AttacksNumber)} " +
            $"attributes={character.Attributes.Count} activeFeatures={character.ActiveFeatures.Count} " +
            $"powers={character.UsablePowers.Count} invocations={character.Invocations.Count} " +
            $"repertoires={character.SpellRepertoires.Count} attacks={character.AttackModes.Count} " +
            $"move={string.Join(",", character.MoveModes.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"))} " +
            $"senses={string.Join(",", character.SenseModes.Select(x => $"{x.SenseType}:{x.SenseRange}:{x.StealthBreakerRange}"))} " +
            $"maxSense={character.MaxSenseRange} impairedSight={character.ImpairedSight} " +
            $"visionHeight={character.VisionHeight:0.###} " +
            $"visionHeightFactor={character.VisionHeightFactor:0.###} " +
            $"requiredSenses={string.Join(",", character.RequiredSenseTypesToPerceive.OrderBy(x => x))} " +
            $"saves={GetSavingThrowSummary(character)} " +
            $"classes={string.Join(",", classLevels?.Select(x => $"{x.ClassDefinition?.Name}:{x.Level}") ?? [])} " +
            $"subclasses={string.Join(",", subclasses ?? [])} " +
            $"languages={string.Join(",", languages.OrderBy(x => x, StringComparer.Ordinal))} " +
            $"inventoryAppearance={character.UsesInventoryAppearanceSeed} " +
            $"body={character.BodyAssetPrefix ?? "<null>"} " +
            $"armor={character.ArmorAssetPrefix ?? "<null>"}";

        WriteChanged("character", $"{character.Guid}|{stage}", detail);

        try
        {
            RecordCombatState(character, stage);
        }
        catch (Exception ex)
        {
            RecordException("combat", $"record-state-{stage}", ex);
        }
    }

    private static void RecordCombatState(
        RulesetCharacterSimulacrum character,
        string stage)
    {
        character.TryGetAttribute(AttributeDefinitions.ArmorClass, out var armorClass);

        var armorClassModifiers = armorClass?.ActiveModifiers
            .Where(modifier =>
                modifier != null &&
                !Mathf.Approximately(modifier.Value, 0))
            .Select(modifier =>
                $"{modifier.Value:0.###}:{string.Join("+", modifier.Tags ?? [])}")
            .ToArray() ?? [];
        var abilities = AttributeDefinitions.AbilityScoreNames
            .Select(ability =>
            {
                character.TryGetAttribute(ability, out var attribute);

                var equipmentModifiers = attribute?.ActiveModifiers
                    .Where(modifier =>
                        modifier.Tags?.Contains(AttributeDefinitions.TagEquipment) == true)
                    .Select(modifier =>
                        $"{modifier.Operation}:{modifier.Value:0.###}")
                    .ToArray() ?? [];

                return
                    $"{ability}:{character.TryGetAttributeValue(ability)}" +
                    $"[equipment={string.Join(",", equipmentModifiers)}]";
            })
            .ToArray();
        var attacks = character.AttackModes
            .Where(mode => mode != null)
            .Take(8)
            .Select((mode, index) =>
            {
                var damage = mode.EffectDescription?.FindFirstDamageForm();

                return
                    $"{index}[source={mode.SourceDefinition?.Name ?? "<null>"};" +
                    $"item={(mode.SourceObject as RulesetItem)?.Guid ?? 0};" +
                    $"ability={mode.AbilityScore};hit={mode.ToHitBonus};" +
                    $"hitTrends={FormatTrends(mode.ToHitBonusTrends)};" +
                    $"damage={FormatAttackDamage(mode)};" +
                    $"damageTrends={FormatTrends(damage?.DamageBonusTrends)}]";
            })
            .ToArray();
        var equipment = character.CharacterInventory?.InventorySlotsByName?
            .Where(IsActiveEquipmentSlot)
            .GroupBy(pair => pair.Value.EquipedItem.Guid)
            .Select(group =>
            {
                var item = group.First().Value.EquipedItem;
                var itemOnlyFeatures = item.ItemDefinition.StaticProperties
                    .Where(property => property?.AppliesOnItemOnly == true)
                    .Select(property => FormatItemFeature(property.FeatureDefinition))
                    .Where(summary => summary != null);
                var dynamicFeatures = item.DynamicItemProperties
                    .Select(property => FormatItemFeature(property?.FeatureDefinition))
                    .Where(summary => summary != null);

                return
                    $"{string.Join("+", group.Select(pair => pair.Key).OrderBy(name => name))}=" +
                    $"{item.ItemDefinition.Name}:{item.Guid}" +
                    $"[usable={!item.ItemDefinition.RequiresAttunement};" +
                    $"identified={item.Identified};" +
                    $"itemOnly={string.Join(",", itemOnlyFeatures)};" +
                    $"dynamic={string.Join(",", dynamicFeatures)}]";
            })
            .Take(16)
            .ToArray() ?? [];

        WriteChanged(
            "combat",
            $"{character.Guid}|{stage}",
            $"stage={stage} guid={character.Guid} " +
            $"ac={character.TryGetAttributeValue(AttributeDefinitions.ArmorClass)} " +
            $"acBase={armorClass?.BaseValue ?? 0} " +
            $"acModifiers={string.Join(",", armorClassModifiers)} " +
            $"abilities={string.Join(",", abilities)} " +
            $"move={string.Join(",", character.MoveModes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))} " +
            $"attacks={string.Join("|", attacks)} equipment={string.Join("|", equipment)}");
    }

    private static bool IsActiveEquipmentSlot(
        KeyValuePair<string, RulesetInventorySlot> pair)
    {
        var slot = pair.Value;
        var itemDefinition = slot?.EquipedItem?.ItemDefinition;
        var activeSlotName = slot?.SlotTypeDefinition?.Name ?? pair.Key;

        return itemDefinition != null &&
               !slot.ConfigSlot &&
               !slot.Disabled &&
               itemDefinition.SlotsWhereActive.Contains(activeSlotName);
    }

    private static string FormatItemFeature(FeatureDefinition feature)
    {
        return feature switch
        {
            IAttackModificationProvider attack =>
                $"{feature.Name}[hit={attack.AttackRollModifier}:{attack.AttackRollModifierMethod};" +
                $"damage={attack.DamageRollModifier}:{attack.DamageRollModifierMethod};" +
                $"dice={attack.AdditionalDamageDice};magical={attack.MagicalWeapon}]",
            FeatureDefinitionAttributeModifier
            {
                ModifiedAttribute: AttributeDefinitions.ArmorClass
            } armorClass =>
                $"{feature.Name}[ac={armorClass.ModifierValue}:{armorClass.ModifierOperation}]",
            null => null,
            _ => feature.Name
        };
    }

    private static string FormatTrends(IEnumerable<TrendInfo> trends)
    {
        return string.Join(
            ",",
            trends?.Select(trend =>
                $"{trend.value}:{trend.sourceType}:{trend.sourceName}") ?? []);
    }

    internal static void RecordLanguageValidation(
        RulesetCharacterSimulacrum character,
        string targetFamily,
        string requiredLanguage,
        IEnumerable<string> knownLanguages,
        bool valid)
    {
        if (character == null)
        {
            return;
        }

        var languages = string.Join(
            ",",
            knownLanguages
                .Where(language => !string.IsNullOrEmpty(language))
                .Distinct()
                .OrderBy(language => language, StringComparer.Ordinal));

        WriteChanged(
            "language",
            $"{character.Guid}|{targetFamily}|{requiredLanguage}",
            $"stage=target-validation guid={character.Guid} " +
            $"targetFamily={targetFamily ?? "<null>"} required={requiredLanguage ?? "<null>"} " +
            $"known={languages} valid={valid}");
    }

    internal static void RecordTimeLapseMembership(
        RulesetCharacterSimulacrum character,
        int rounds,
        int matchingEntityCount,
        bool fallbackApplied)
    {
        if (character == null)
        {
            return;
        }

        var timedSpells = character.SpellsCastByMe
            .OfType<RulesetEffectSpell>()
            .Where(effect =>
                effect?.EffectDescription != null &&
                effect.EffectDescription.DurationType != DurationType.Permanent)
            .OrderBy(effect => effect.Guid)
            .Select(effect =>
                $"{effect.SpellDefinition?.Name ?? "<null>"}:{effect.Guid}:" +
                $"{effect.EffectDescription.DurationType}:" +
                $"{effect.EffectDescription.DurationParameter}")
            .ToArray();

        WriteChanged(
            "time-lapse",
            $"{character.Guid}|manager-membership",
            $"stage=tick-rounds guid={character.Guid} rounds={rounds} " +
            $"matchingEntities={matchingEntityCount} fallbackApplied={fallbackApplied} " +
            $"timedSpells={string.Join(",", timedSpells)}");
    }

    internal static void RecordRepair(
        RulesetCharacter owner,
        string stage,
        int requestedHitPoints,
        int maximumHitPoints,
        string detail = null)
    {
        if (owner == null ||
            !AddEventKey(
                $"repair|{owner.Guid}|{stage}|{requestedHitPoints}|{maximumHitPoints}|{detail}"))
        {
            return;
        }

        Write(
            "repair",
            $"stage={stage} owner={owner.Guid} requested={requestedHitPoints} " +
            $"maximum={maximumHitPoints} detail={detail ?? "<none>"}");
    }

    internal static void RecordDismiss(
        RulesetCharacterSimulacrum duplicate,
        string stage,
        bool valid,
        RulesetEffect effect = null)
    {
        if (duplicate == null ||
            !AddEventKey(
                $"dismiss|{duplicate.Guid}|{stage}|{valid}|{effect?.Guid ?? 0}|" +
                $"{duplicate.LifecycleState}"))
        {
            return;
        }

        Write(
            "dismiss",
            $"stage={stage} guid={duplicate.Guid} valid={valid} effect={effect?.Guid ?? 0} " +
            $"lifecycle={duplicate.LifecycleState}");
    }

    internal static void RecordGrappleHands(
        RulesetCharacterSimulacrum character,
        string stage,
        int freeHands,
        bool canGrapple)
    {
        if (character == null ||
            !AddEventKey(
                $"grapple-hands|{character.Guid}|{stage}|{freeHands}|{canGrapple}|" +
                $"{character.GetMainWeapon()?.Guid ?? 0}|{character.GetOffhandWeapon()?.Guid ?? 0}|" +
                $"{GrappleContext.HasGrappleSource(character)}"))
        {
            return;
        }

        Write(
            "grapple",
            $"stage={stage} guid={character.Guid} freeHands={freeHands} " +
            $"canGrapple={canGrapple} grappled={GrappleContext.HasGrappleSource(character)} " +
            $"main={character.GetMainWeapon()?.ItemDefinition?.Name ?? "<empty>"} " +
            $"off={character.GetOffhandWeapon()?.ItemDefinition?.Name ?? "<empty>"}");
    }

    internal static void RecordSpellcastingLevel(
        RulesetCharacterSimulacrum character,
        RulesetSpellRepertoire repertoire,
        int level,
        string source)
    {
        if (character == null ||
            !AddEventKey(
                $"spellcasting-level|{character.Guid}|" +
                $"{repertoire?.SpellCastingFeature?.Name}|{level}|{source}"))
        {
            return;
        }

        Write(
            "spellcasting",
            $"stage=level-resolved guid={character.Guid} " +
            $"repertoire={repertoire?.SpellCastingFeature?.Name ?? "<null>"} " +
            $"class={repertoire?.SpellCastingClass?.Name ?? "<null>"} " +
            $"subclass={repertoire?.SpellCastingSubclass?.Name ?? "<null>"} " +
            $"level={level} source={source}");
    }

    internal static void RecordIncantation(
        RulesetCharacterSimulacrum character,
        string stage,
        SpellDefinition spell,
        RulesetSpellRepertoire repertoire,
        CharacterClassDefinition characterClass,
        string detail)
    {
        if (character == null ||
            !AddEventKey(
                $"incantation|{character.Guid}|{stage}|{spell?.Name}|" +
                $"{repertoire?.SpellCastingFeature?.Name}|{characterClass?.Name}|{detail}"))
        {
            return;
        }

        Write(
            "incantation",
            $"stage={stage} guid={character.Guid} spell={spell?.Name ?? "<null>"} " +
            $"repertoire={repertoire?.SpellCastingFeature?.Name ?? "<null>"} " +
            $"class={characterClass?.Name ?? "<null>"} detail={detail ?? "<null>"}");
    }

    internal static void RecordShillelagh(
        RulesetCharacterSimulacrum character,
        string stage,
        RulesetEffectSpell effect = null,
        bool force = false)
    {
        if (character == null)
        {
            return;
        }

        var effects = character.SpellsCastByMe
            .OfType<RulesetEffectSpell>()
            .Where(x => x?.SpellDefinition?.Name == "Shillelagh")
            .ToList();

        if (effect?.SpellDefinition?.Name == "Shillelagh" && !effects.Contains(effect))
        {
            effects.Add(effect);
        }

        if (effects.Count == 0 && !force)
        {
            return;
        }

        var effectSummary = string.Join(
            ",",
            effects.OrderBy(x => x.Guid).Select(x =>
                $"{x.Guid}:{x.EffectDescription?.DurationType}:{x.EffectDescription?.DurationParameter}:" +
                $"rounds={x.RemainingRounds}:slot={x.SlotLevel}:" +
                $"ability={x.SpellRepertoire?.SpellCastingAbility ?? "<null>"}:" +
                $"properties={string.Join("+", x.TrackedItemPropertyGuids.OrderBy(guid => guid))}"));
        var itemPropertySummary = string.Join(
            ",",
            effects
                .SelectMany(x => x.TrackedItemPropertyGuids)
                .Distinct()
                .OrderBy(guid => guid)
                .Select(guid =>
                {
                    if (!RulesetEntity.TryGetEntity(guid, out RulesetItemProperty property) ||
                        property == null)
                    {
                        return $"{guid}[missing]";
                    }

                    return $"{guid}[feature={property.FeatureDefinition?.Name ?? "<null>"};" +
                           $"item={property.TargetItemGuid};source={property.SourceEffectGuid};" +
                           $"rounds={property.RemainingRounds}]";
                }));
        var attackSummary = string.Join(
            ",",
            character.AttackModes.Select((mode, index) =>
                $"{index}[source={mode.SourceDefinition?.Name ?? "<null>"};" +
                $"ability={mode.AbilityScore};toHit={mode.ToHitBonus};" +
                $"damage={FormatAttackDamage(mode)}]").Take(16));
        var key =
            $"shillelagh|{character.Guid}|{stage}|{effectSummary}|{itemPropertySummary}|{attackSummary}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "shillelagh",
            $"stage={stage} guid={character.Guid} effects={effectSummary} " +
            $"itemProperties={itemPropertySummary} attacks={attackSummary}");
    }

    internal static void RecordSpellSlots(
        RulesetCharacter character,
        string stage,
        RulesetSpellRepertoire selectedRepertoire = null,
        int selectedSlotLevel = -1,
        bool? freeCast = null,
        SpellDefinition spell = null,
        int? displayRemaining = null,
        int? displayMaximum = null)
    {
        if (character == null ||
            character is not RulesetCharacterSimulacrum &&
            !stage.StartsWith("preflight-", StringComparison.Ordinal))
        {
            return;
        }

        var compactBinding = string.Equals(stage, "box-bind", StringComparison.Ordinal);
        IEnumerable<RulesetSpellRepertoire> repertoires = compactBinding && selectedRepertoire != null
            ? new[] { selectedRepertoire }
            : character.SpellRepertoires;
        var repertoireSummary = string.Join(
            ";",
            repertoires.Select((repertoire, index) =>
            {
                if (repertoire == null)
                {
                    return $"{index}[null]";
                }

                var indexLabel = compactBinding ? "selected" : index.ToString();
                var levels = repertoire.spellsSlotCapacities.Keys
                    .Concat(repertoire.usedSpellsSlots.Keys)
                    .Append(selectedSlotLevel)
                    .Where(level => level > 0)
                    .Distinct()
                    .OrderBy(level => level)
                    .Take(16)
                    .Select(level =>
                    {
                        repertoire.GetSlotsNumber(level, out var remaining, out var maximum);
                        repertoire.usedSpellsSlots.TryGetValue(level, out var used);
                        repertoire.spellsSlotCapacities.TryGetValue(level, out var capacity);

                        return $"{level}:{remaining}/{maximum}:" +
                               $"used={used}:" +
                               $"capacity={capacity}";
                    });

                var header =
                    $"{indexLabel}[feature={repertoire.SpellCastingFeature?.Name ?? "<null>"};" +
                    $"origin={repertoire.SpellCastingFeature?.SpellCastingOrigin.ToString() ?? "<null>"};" +
                    $"class={repertoire.SpellCastingClass?.Name ?? "<null>"};" +
                    $"selected={ReferenceEquals(repertoire, selectedRepertoire)};";

                return compactBinding
                    ? $"{header}slots={string.Join(",", levels)}]"
                    : $"{header}" +
                      $"cantrips={FormatDefinitionNames(repertoire.KnownCantrips)};" +
                      $"known={FormatDefinitionNames(repertoire.KnownSpells)};" +
                      $"prepared={FormatDefinitionNames(repertoire.PreparedSpells)};" +
                      $"auto={FormatDefinitionNames(repertoire.AutoPreparedSpells)};" +
                      $"slots={string.Join(",", levels)}]";
            }));
        var loggedSpell = compactBinding
            ? "<omitted>"
            : spell?.Name ?? "<null>";
        var summary =
            $"stage={stage} guid={character.Guid} selectedSlot={selectedSlotLevel} " +
            $"spell={loggedSpell} " +
            $"freeCast={freeCast?.ToString() ?? "<unknown>"} " +
            $"display={displayRemaining?.ToString() ?? "<unknown>"}/" +
            $"{displayMaximum?.ToString() ?? "<unknown>"} repertoires={repertoireSummary}";

        WriteChanged(
            "spell-slots",
            $"{character.Guid}|{stage}|" +
            $"{selectedRepertoire?.SpellCastingFeature?.Name}|{selectedSlotLevel}|" +
            $"{(compactBinding ? freeCast?.ToString() ?? "<unknown>" : string.Empty)}",
            summary);
    }

    internal static void RecordSpellPanelRange(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        ActionDefinitions.ActionType actionType,
        bool cantripOnly,
        int nativeMaximumLevel,
        int resolvedMaximumLevel)
    {
        if (character is not RulesetCharacterSimulacrum duplicate || repertoire == null)
        {
            return;
        }

        var knownByLevel = repertoire.KnownSpells
            .Where(spell => spell != null)
            .GroupBy(spell => spell.SpellLevel)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}:{group.Count()}");
        var detail =
            $"stage=multiline-range guid={duplicate.Guid} " +
            $"runtime={character.GetType().Name} action={actionType} cantripOnly={cantripOnly} " +
            $"feature={repertoire.SpellCastingFeature?.Name ?? "<null>"} " +
            $"class={repertoire.SpellCastingClass?.Name ?? "<null>"} " +
            $"spellcastingLevel={character.GetSpellcastingLevel(repertoire)} " +
            $"nativeMax={nativeMaximumLevel} resolvedMax={resolvedMaximumLevel} " +
            $"cantrips={repertoire.KnownCantrips.Count} " +
            $"knownByLevel={string.Join(",", knownByLevel)}";

        WriteChanged(
            "spell-ui",
            $"{duplicate.Guid}|{repertoire.SpellCastingFeature?.Name}|" +
            $"{actionType}|{cantripOnly}",
            detail);
    }

    internal static void RecordPowerResourceCopy(
        RulesetCharacterSimulacrum character,
        IEnumerable<SimulacrumBehavior.SimulacrumSnapshotRulesetCondition.SourcePowerState>
            sourcePowers,
        string stage)
    {
        if (character == null || sourcePowers == null)
        {
            return;
        }

        var comparisons = sourcePowers
            .Where(state => state?.Definition != null)
            .Select(state =>
            {
                var actual = character.UsablePowers.FirstOrDefault(power =>
                    power?.PowerDefinition == state.Definition);
                var actualMaximum = actual == null
                    ? -1
                    : PowerProvider.GetEffectiveMaxUses(character, actual);
                var actualRemaining = actual?.remainingUses ?? -1;

                return new
                {
                    State = state,
                    ActualMaximum = actualMaximum,
                    ActualRemaining = actualRemaining,
                    Matches = actualMaximum == state.EffectiveMaxUses &&
                              actualRemaining == state.RemainingUses
                };
            })
            .OrderBy(entry => entry.State.Definition.Name, StringComparer.Ordinal)
            .ToArray();
        var mismatchCount = comparisons.Count(entry => !entry.Matches);
        var displayedComparisons = comparisons
            .Where(entry => !entry.Matches)
            .Take(MaximumDefinitionNames)
            .ToArray();
        var detail = string.Join(
            ",",
            displayedComparisons.Select(entry =>
                $"{entry.State.Definition.Name}:" +
                $"{entry.State.RemainingUses}/{entry.State.EffectiveMaxUses}->" +
                $"{entry.ActualRemaining}/{entry.ActualMaximum}"));

        WriteChanged(
            "resources",
            $"{character.Guid}|{stage}",
            $"stage={stage} guid={character.Guid} sourceCount={comparisons.Length} " +
            $"mismatches={mismatchCount}" +
            (mismatchCount == 0
                ? string.Empty
                : $" displayed={displayedComparisons.Length} powers={detail}"));
    }

    internal static void RecordFightingStyleConditions(
        RulesetCharacterSimulacrum character,
        IEnumerable<FightingStyleDefinition> fightingStyles)
    {
        if (character == null)
        {
            return;
        }

        var styles = fightingStyles?
            .Where(style => style != null)
            .Distinct()
            .OrderBy(style => style.Name, StringComparer.Ordinal)
            .Select(style =>
                $"{style.Name}:{style.Condition}=" +
                $"{SimulacrumBehavior.IsFightingStyleActive(character, style)}")
            .ToArray() ?? [];
        var detail =
            $"guid={character.Guid} armor={character.IsWearingArmor()} " +
            $"heavy={character.IsWearingHeavyArmor()} shield={character.IsWearingShield()} " +
            $"grappleFreeHands={ValidatorsCharacter.GetFreeHandsForGrapple(character)} " +
            $"styles={string.Join(",", styles)}";

        WriteChanged("equipment-conditions", character.Guid.ToString(), detail);
    }

    internal static void RecordDualWieldEligibility(
        RulesetCharacterSimulacrum character,
        RulesetItem mainHand,
        RulesetItem offHand,
        bool mainLight,
        bool offLight,
        bool canDualWieldNonLight,
        bool pairPassesOffHandEquipmentRules,
        bool matchingBonusModePresent,
        bool matchingBonusModeInvalidByEquipment,
        bool bonusModeSuppressesLightWarning,
        int configurationRank,
        int currentConfigurationRank,
        bool isCurrentConfiguration,
        bool computedLightWarningMain,
        bool computedLightWarningOff,
        bool actualMainWarningActive,
        string actualMainWarningContent,
        bool actualOffWarningActive,
        string actualOffWarningContent)
    {
        if (character == null)
        {
            return;
        }

        var bonusModes = string.Join(
            ",",
            character.AttackModes
                .Where(mode => mode?.ActionType == ActionDefinitions.ActionType.Bonus)
                .Select(mode =>
                    $"{mode.SourceDefinition?.Name ?? "<null>"}:" +
                    $"{(mode.SourceObject as RulesetItem)?.Guid ?? 0}:" +
                    $"{mode.SlotName ?? "<none>"}"));
        var detail =
            $"guid={character.Guid} " +
            $"main={DescribeInventoryItem(mainHand)} mainLight={mainLight} " +
            $"mainProficient={IsProficientWeapon(character, mainHand)} " +
            $"off={DescribeInventoryItem(offHand)} offLight={offLight} " +
            $"offProficient={IsProficientWeapon(character, offHand)} " +
            $"configuration={configurationRank} current={currentConfigurationRank} " +
            $"currentIdentity={isCurrentConfiguration} " +
            $"canNonLight={canDualWieldNonLight} " +
            $"pairPassesOffHandEquipmentRules={pairPassesOffHandEquipmentRules} " +
            $"matchingBonusModePresent={matchingBonusModePresent} " +
            $"matchingBonusModeInvalidByEquipment={matchingBonusModeInvalidByEquipment} " +
            $"bonusModeSuppressesLightWarning={bonusModeSuppressesLightWarning} " +
            $"computedLightWarningMain={computedLightWarningMain} " +
            $"computedLightWarningOff={computedLightWarningOff} " +
            $"actualMainWarning={actualMainWarningActive}:" +
            $"{actualMainWarningContent ?? "<none>"} " +
            $"actualOffWarning={actualOffWarningActive}:" +
            $"{actualOffWarningContent ?? "<none>"} " +
            $"bonusModes={bonusModes}";

        WriteChanged(
            "dual-wield",
            $"{character.Guid}|{configurationRank}|" +
            $"{mainHand?.Guid ?? 0}|{offHand?.Guid ?? 0}",
            detail);
    }

    private static bool IsProficientWeapon(
        RulesetCharacterSimulacrum character,
        RulesetItem item)
    {
        return item?.ItemDefinition is { IsWeapon: true } itemDefinition &&
               character.IsProficientWithItem(itemDefinition);
    }

    internal static void RecordRefreshNotification(
        RulesetCharacterSimulacrum character,
        string source,
        int depth)
    {
        if (character == null)
        {
            return;
        }

        WriteChanged(
            "refresh",
            $"{character.Guid}|notification-deferred|{source}",
            $"stage=notification-deferred guid={character.Guid} source={source} depth={depth}");
    }

    internal static void RecordRefreshTransaction(
        RulesetCharacterSimulacrum character,
        string stage,
        bool restoredSnapshot,
        bool hadPendingNotification,
        bool published)
    {
        if (character == null)
        {
            return;
        }

        WriteChanged(
            "refresh",
            $"{character.Guid}|transaction|{stage}",
            $"stage={stage} guid={character.Guid} depth={character.RefreshAllDepth} " +
            $"restoredSnapshot={restoredSnapshot} " +
            $"pendingNotification={hadPendingNotification} published={published}");
    }

    internal static void RecordReactionSpellSlots(
        RulesetCharacterSimulacrum character,
        SpellDefinition spell,
        RulesetSpellRepertoire repertoire,
        IEnumerable<KeyValuePair<int, bool>> options,
        int selectedIndex,
        int selectedSlotLevel,
        string stage)
    {
        if (character == null)
        {
            return;
        }

        var optionSummary = string.Join(
            ",",
            options.Select(x => $"{x.Key}:{x.Value}"));

        Write(
            "reaction-slots",
            $"stage={stage} guid={character.Guid} spell={spell?.Name ?? "<null>"} " +
            $"repertoire={repertoire?.SpellCastingFeature?.Name ?? "<null>"} " +
            $"selectedIndex={selectedIndex} selectedSlotLevel={selectedSlotLevel} " +
            $"options={optionSummary}");
    }

    internal static void RecordSkillSnapshot(
        RulesetCharacter character,
        IEnumerable<string> skillNames)
    {
        if (character == null)
        {
            return;
        }

        Write(
            "skills",
            $"stage=preflight guid={character.Guid} " +
            $"proficient={string.Join(",", skillNames.OrderBy(x => x))}");
    }

    internal static void RecordSummonedItem(
        RulesetCharacterSimulacrum character,
        ItemDefinition definition,
        string stage,
        int requested,
        int stored,
        int dropped)
    {
        if (character == null)
        {
            return;
        }

        Write(
            "summon-item",
            $"stage={stage} guid={character.Guid} item={definition?.Name ?? "<null>"} " +
            $"requested={requested} stored={stored} dropped={dropped} " +
            $"unplaced={Math.Max(0, requested - stored - dropped)}");
    }

    internal static void RecordDeityMark(
        RulesetCharacterSimulacrum character,
        string stage,
        RulesetItem item,
        RulesetCharacterHero commandHero,
        bool? result = null,
        bool? interactable = null)
    {
        if (character == null)
        {
            return;
        }

        var detail =
            $"stage=deity-mark-{stage} guid={character.Guid} " +
            $"item={item?.ItemDefinition?.Name ?? "<null>"}:{item?.Guid ?? 0} " +
            $"itemMark={item?.DeityMark ?? "<none>"} " +
            $"deity={character.DeityDefinition?.Name ?? "<null>"} " +
            $"commandHero={commandHero?.Name ?? "<none>"}:{commandHero?.Guid ?? 0} " +
            $"result={result?.ToString() ?? "<n/a>"} " +
            $"interactable={interactable?.ToString() ?? "<n/a>"}";

        if (stage == "requested")
        {
            Write("inventory", detail);

            return;
        }

        WriteChanged(
            "inventory",
            $"deity-mark|{stage}|{character.Guid}|{item?.Guid ?? 0}",
            detail);
    }

    internal static void RecordEffectForm(
        RulesetCharacterSimulacrum character,
        string stage,
        RulesetEffect activeEffect,
        string detail)
    {
        if (character == null)
        {
            return;
        }

        Write(
            "effect-form",
            $"stage={stage} guid={character.Guid} " +
            $"source={activeEffect?.SourceDefinition?.Name ?? "<null>"} " +
            $"{detail ?? string.Empty}");
    }

    internal static void RecordSpellChoice(
        RulesetCharacter character,
        string spell,
        string stage,
        IEnumerable<string> candidates,
        string selected = null)
    {
        if (character is not RulesetCharacterSimulacrum duplicate)
        {
            return;
        }

        var candidateNames = string.Join(
            ",",
            candidates
                .Where(x => !string.IsNullOrEmpty(x))
                .OrderBy(x => x, StringComparer.Ordinal)
                .Take(MaximumDefinitionNames));

        Write(
            "spell-choice",
            $"stage={stage} guid={duplicate.Guid} spell={spell} " +
            $"selected={selected ?? "<none>"} candidates={candidateNames}");
    }

    private static string GetSavingThrowSummary(RulesetCharacterSimulacrum character)
    {
        return string.Join(
            ",",
            AttributeDefinitions.AbilityScoreNames.Select(ability =>
            {
                var trends = new List<TrendInfo>();
                var total = character.ComputeBaseSavingThrowBonus(ability, trends);
                character.SavingThrowProficiencies.TryGetValue(ability, out var stored);

                return $"{ability}:{total}:stored={stored}";
            }));
    }

    internal static void RecordActionState(
        RulesetCharacterSimulacrum character,
        GameLocationCharacter locationCharacter,
        string stage)
    {
        if (character == null || locationCharacter == null)
        {
            return;
        }

        var performanceSummary = Enum
            .GetValues(typeof(ActionDefinitions.ActionType))
            .Cast<ActionDefinitions.ActionType>()
            .Select(actionType =>
            {
                locationCharacter.CurrentActionRankByType.TryGetValue(actionType, out var rank);

                if (!locationCharacter.ActionPerformancesByType.TryGetValue(
                        actionType,
                        out var filters))
                {
                    return $"{actionType}:rank={rank}:filters=<missing>";
                }

                var filterSummary = string.Join(
                    ",",
                    filters.Select((filter, index) =>
                        $"{index}[can={filter.CanPerformActionType};attacks={filter.MaxAttacksNumber};" +
                        $"authorized={string.Join("+", filter.AuthorizedActions.Take(32))};" +
                        $"restricted={string.Join("+", filter.RestrictedActions.Take(32))};" +
                        $"forbidden={string.Join("+", filter.ForbiddenActions.Take(32))}]"));

                return $"{actionType}:rank={rank}:filters={filterSummary}";
            })
            .ToArray();
        var attackSummary = string.Join(
            ",",
            character.AttackModes.Select((mode, index) =>
                $"{index}[type={mode.ActionType};count={mode.AttacksNumber};" +
                $"source={mode.SourceDefinition?.Name ?? "<null>"};" +
                $"item={(mode.SourceObject as RulesetItem)?.Guid ?? 0};" +
                $"ability={mode.AbilityScore};toHit={mode.ToHitBonus};" +
                $"damage={FormatAttackDamage(mode)}]").Take(32));
        var powerSummary = string.Join(
            ",",
            character.UsablePowers
                .Where(power => power?.PowerDefinition != null)
                .OrderBy(power => power.PowerDefinition.Name, StringComparer.Ordinal)
                .Take(32)
                .Select(power =>
                    $"{power.PowerDefinition.Name}[activation={power.PowerDefinition.ActivationTime};" +
                    $"delegated={power.PowerDefinition.DelegatedToAction};" +
                    $"remaining={character.GetRemainingUsesOfPower(power)}/" +
                    $"{PowerProvider.GetEffectiveMaxUses(character, power)}]"));
        var state = string.Join("|", performanceSummary);

        if (!AddEventKey(
                $"actions|{character.Guid}|{stage}|{state}|{attackSummary}|{powerSummary}"))
        {
            return;
        }

        Write(
            "actions",
            $"stage={stage} guid={character.Guid} side={locationCharacter.Side} " +
            $"dirty={locationCharacter.dirtyActions} falling={locationCharacter.Falling} " +
            $"prone={locationCharacter.Prone} performances={state} " +
            $"attacks={attackSummary} powers={powerSummary}");
    }

    internal static void RecordInitialPlacementState(
        RulesetCharacterSimulacrum character,
        GameLocationCharacter locationCharacter,
        bool wasFalling,
        bool wasProne)
    {
        if (character == null || locationCharacter == null)
        {
            return;
        }

        Write(
            "actions",
            $"stage=initial-placement-settled guid={character.Guid} " +
            $"position={locationCharacter.LocationPosition} wasFalling={wasFalling} " +
            $"wasProne={wasProne} " +
            $"falling={locationCharacter.Falling} prone={locationCharacter.Prone}");
    }

    internal static void RecordProducedFlameAttackMode(
        RulesetCharacter character,
        RulesetAttackMode attackMode)
    {
        if (character == null ||
            attackMode?.SourceDefinition != CustomWeaponsContext.ProducedFlameDart)
        {
            return;
        }

        var item = attackMode.SourceObject as RulesetItem;
        var activeSpell =
            EffectHelpers.GetEffectByGuid(item?.SourceSummoningEffectGuid ?? 0) as
                RulesetEffectSpell;
        var repertoire = SpellCastingValidation.ResolveRepertoire(
            character,
            activeSpell?.SpellRepertoire,
            DatabaseHelper.SpellDefinitions.ProduceFlame,
            activeSpell);
        var damage = attackMode.EffectDescription?.FindFirstDamageForm();
        var abilityMatches =
            repertoire == null ||
            string.Equals(
                attackMode.AbilityScore,
                repertoire.SpellCastingAbility,
                StringComparison.Ordinal);
        var toHitMatches =
            repertoire == null ||
            attackMode.ToHitBonus == repertoire.SpellAttackBonus;

        WriteChanged(
            "produced-flame",
            $"{character.Guid}|{item?.Guid ?? 0}",
            $"guid={character.Guid} type={character.GetType().Name} " +
            $"item={item?.Guid ?? 0} heldIsWeapon=" +
            $"{item?.ItemDefinition?.IsWeapon.ToString() ?? "<null>"} " +
            $"effect={item?.SourceSummoningEffectGuid ?? 0} " +
            $"spell={activeSpell?.SpellDefinition?.Name ?? "<null>"} " +
            $"repertoire={repertoire?.SpellCastingFeature?.Name ?? "<null>"} " +
            $"action={attackMode.ActionType} slot={attackMode.SlotName ?? "<null>"} " +
            $"count={attackMode.AttacksNumber} ability={attackMode.AbilityScore ?? "<null>"} " +
            $"expectedAbility={repertoire?.SpellCastingAbility ?? "<null>"} " +
            $"toHit={attackMode.ToHitBonus} expectedToHit={repertoire?.SpellAttackBonus ?? -1} " +
            $"dice={damage?.DiceNumber ?? 0}d{damage?.DieType.ToString() ?? "<null>"} " +
            $"abilityMatches={abilityMatches} toHitMatches={toHitMatches}");
    }

    internal static void RecordGroundItems(
        RulesetCharacterSimulacrum character,
        TA.int3 position,
        int itemCount,
        bool canPickItem)
    {
        if (character == null ||
            !AddEventKey($"ground-items|{character.Guid}|{position}|{itemCount}|{canPickItem}"))
        {
            return;
        }

        Write(
            "loot",
            $"stage=action-proximity guid={character.Guid} position={position} " +
            $"radius=5 items={itemCount} canPick={canPickItem}");
    }

    internal static void RecordActionStatus(
        RulesetCharacter character,
        ActionDefinitions.Id actionId,
        ActionDefinitions.ActionScope scope,
        ActionDefinitions.ActionStatus actionTypeStatus,
        RulesetAttackMode attackMode,
        ActionDefinitions.ActionStatus result)
    {
        if (character == null)
        {
            return;
        }

        // The engine asks about every unavailable menu action while panels refresh. Repeated
        // attack-less negatives carry no information about the Simulacrum's usable modes.
        if (attackMode == null &&
            result == ActionDefinitions.ActionStatus.Unavailable &&
            actionId != ActionDefinitions.Id.CastRitual)
        {
            return;
        }

        var attack = attackMode == null
            ? "<none>"
            : $"{attackMode.ActionType}:{attackMode.SourceDefinition?.Name ?? "<null>"}";
        var locationCharacter = GameLocationCharacter.GetFromActor(character);
        var key = $"action-status|{character.Guid}|{actionId}|{scope}|" +
                  $"{actionTypeStatus}|{attack}|{result}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "actions",
            $"stage=engine-status guid={character.Guid} action={actionId} scope={scope} " +
            $"actionTypeStatus={actionTypeStatus} attack={attack} " +
            $"falling={locationCharacter?.Falling.ToString() ?? "<null>"} " +
            $"prone={locationCharacter?.Prone.ToString() ?? "<null>"} " +
            $"result={result}");
    }

    internal static void RecordLootEligibility(
        RulesetCharacterSimulacrum character,
        RulesetItem item,
        bool canCarry,
        bool canEquipOrStore,
        bool canDispatch,
        bool hasGroundPosition)
    {
        if (character == null || item?.ItemDefinition == null)
        {
            return;
        }

        var key = $"loot-eligibility|{character.Guid}|{item.Guid}|{canCarry}|" +
                  $"{canEquipOrStore}|{canDispatch}|{hasGroundPosition}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "loot",
            $"stage=eligibility guid={character.Guid} item={item.ItemDefinition.Name} " +
            $"weight={item.ComputeWeight():0.###} canCarry={canCarry} " +
            $"canEquipOrStore={canEquipOrStore} canDispatch={canDispatch} " +
            $"hasGroundPosition={hasGroundPosition}");
    }

    internal static void RecordLootGate(
        RulesetCharacterSimulacrum character,
        string stage,
        bool result,
        string details)
    {
        if (character == null)
        {
            return;
        }

        var keyDetails = stage == "action-cell" ? string.Empty : details;
        var key = $"loot-gate|{character.Guid}|{stage}|{result}|{keyDetails}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "loot",
            $"stage={stage} guid={character.Guid} result={result} {details}");
    }

    internal static void RecordActionPrerequisite(
        RulesetCharacterSimulacrum character,
        string prerequisite,
        ActionDefinitions.ActionType actionType,
        bool result)
    {
        if (character == null ||
            !AddEventKey(
                $"action-prerequisite|{character.Guid}|{prerequisite}|{actionType}|{result}"))
        {
            return;
        }

        Write(
            "actions",
            $"stage=prerequisite guid={character.Guid} type={prerequisite} " +
            $"actionType={actionType} result={result}");
    }

    internal static void RecordActionActivation(
        RulesetCharacterSimulacrum character,
        string stage,
        ActionDefinitions.Id actionId,
        ActionDefinition actionDefinition,
        ActionDefinitions.ActionStatus status,
        RulesetUsablePower usablePower = null,
        string actionClass = null)
    {
        if (character == null)
        {
            return;
        }

        var definition = actionDefinition?.Name ?? "<null>";
        var parameter = actionDefinition?.Parameter.ToString() ?? "<null>";
        var activatedPower = actionDefinition?.ActivatedPower?.Name ?? "<null>";
        var selectedPower = usablePower?.PowerDefinition?.Name ?? "<null>";
        var toggles = string.Join(
            ",",
            character.ToggledPowersOn
                .Where(x => !string.IsNullOrEmpty(x))
                .OrderBy(x => x, StringComparer.Ordinal)
                .Take(32));
        var key = $"action-activation|{character.Guid}|{stage}|{actionId}|{definition}|" +
                  $"{status}|{selectedPower}|{actionClass}|{toggles}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "actions",
            $"stage={stage} guid={character.Guid} action={actionId} definition={definition} " +
            $"parameter={parameter} status={status} activatedPower={activatedPower} " +
            $"selectedPower={selectedPower} actionClass={actionClass ?? "<null>"} " +
            $"toggles={toggles}");
    }

    private static string FormatAttackDamage(RulesetAttackMode attackMode)
    {
        var damage = attackMode?.EffectDescription?.FindFirstDamageForm();

        if (damage == null)
        {
            return "<none>";
        }

        var effectiveDie = attackMode.UseVersatileDamage && damage.Versatile
            ? damage.VersatileDieType
            : damage.DieType;

        return
            $"{damage.DiceNumber}d{effectiveDie}+{damage.BonusDamage}" +
            $"[base={damage.DieType};versatile={damage.VersatileDieType};" +
            $"useVersatile={attackMode.UseVersatileDamage};" +
            $"freeOffHand={attackMode.FreeOffHand}]";
    }

    private static string FormatDefinitionNames(IEnumerable<BaseDefinition> definitions)
    {
        return string.Join(
            ",",
            definitions?
                .Where(definition => definition != null)
                .Select(definition => definition.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Take(MaximumDefinitionNames) ?? []);
    }

    internal static void RecordDamageRoll(
        RulesetCharacterSimulacrum character,
        DamageForm damageForm,
        IReadOnlyCollection<int> rolledValues,
        int addDice,
        bool criticalSuccess,
        int additionalDamage,
        int damageRollReduction,
        float damageMultiplier,
        bool useVersatileDamage,
        bool attackModeDamage,
        int finalDamage,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams)
    {
        if (character == null || damageForm == null)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _damageRollSequence);
        var attackMode = formsParams.attackMode;
        var source = formsParams.activeEffect?.SourceDefinition?.Name ??
                     attackMode?.SourceDefinition?.Name ??
                     "<null>";
        var dieType = useVersatileDamage && damageForm.Versatile
            ? damageForm.VersatileDieType
            : damageForm.DieType;

        Write(
            "damage",
            $"stage=roll seq={sequence} guid={character.Guid} source={source} " +
            $"mode={attackMode?.ActionType.ToString() ?? "<none>"} " +
            $"item={(attackMode?.SourceObject as RulesetItem)?.Guid ?? 0} " +
            $"target={formsParams.targetCharacter?.Guid ?? 0} " +
            $"attackModeDamage={attackModeDamage} " +
            $"type={damageForm.DamageType} " +
            $"formula={damageForm.DiceNumber}d{dieType}+{damageForm.BonusDamage} " +
            $"addDice={addDice} critical={criticalSuccess} " +
            $"rolled={string.Join(",", rolledValues ?? [])} " +
            $"additional={additionalDamage} reduction={damageRollReduction} " +
            $"multiplier={damageMultiplier:0.###} final={finalDamage} " +
            $"trends={FormatTrends(damageForm.DamageBonusTrends)} " +
            $"tags={string.Join(",", formsParams.sourceTags ?? [])}");
    }

    internal static void RecordAttackUseFallback(
        RulesetCharacterSimulacrum character,
        RulesetAttackMode attackMode,
        int nativeRemainingUses,
        int correctedRemainingUses)
    {
        if (character == null || attackMode == null)
        {
            return;
        }

        var source = attackMode.SourceDefinition?.Name ?? "<null>";
        var key = $"attack-uses|{character.Guid}|{source}|{attackMode.ActionType}|" +
                  $"{nativeRemainingUses}|{correctedRemainingUses}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "actions",
            $"stage=attack-uses-corrected guid={character.Guid} source={source} " +
            $"actionType={attackMode.ActionType} native={nativeRemainingUses} " +
            $"corrected={correctedRemainingUses}");
    }

    internal static void RecordAttackAnimation(
        RulesetCharacterSimulacrum character,
        RulesetAttackMode attackMode,
        WeaponTypeDefinition weaponType,
        string animation,
        bool oneHandedVersatile)
    {
        if (character == null || attackMode == null || weaponType == null)
        {
            return;
        }

        var source = attackMode.SourceDefinition?.Name ?? "<null>";
        var detail =
            $"guid={character.Guid} source={source} slot={attackMode.SlotName} " +
            $"weaponType={weaponType.Name} versatileOneHanded={oneHandedVersatile} " +
            $"animation={animation ?? "<null>"}";

        WriteChanged(
            "animation",
            $"{character.Guid}|{source}|{attackMode.SlotName}",
            detail);
    }

    internal static void RecordWeaponStance(
        RulesetCharacterSimulacrum character,
        ItemDefinition itemDefinition,
        string animationTag,
        bool oneHandedVersatile,
        string context,
        bool useGameplayController)
    {
        if (character == null)
        {
            return;
        }

        var source = itemDefinition?.Name ?? "<unarmed>";
        var detail =
            $"stage=weapon-stance guid={character.Guid} context={context ?? "<null>"} " +
            $"gameplayController={useGameplayController} source={source} " +
            $"animationTag={animationTag ?? "<null>"} " +
            $"versatileOneHanded={oneHandedVersatile}";

        WriteChanged(
            "animation",
            $"{character.Guid}|stance|{context}",
            detail);
    }

    internal static void RecordAttackUseAcknowledgementSkipped(
        RulesetCharacterSimulacrum character,
        RulesetAttackMode attackMode,
        int remainingUses)
    {
        if (character == null || attackMode == null)
        {
            return;
        }

        var source = attackMode.SourceDefinition?.Name ?? "<null>";

        if (!AddEventKey(
                $"attack-ack|{character.Guid}|{source}|" +
                $"{attackMode.ActionType}|{remainingUses}"))
        {
            return;
        }

        Write(
            "actions",
            $"stage=attack-acknowledgement-skipped guid={character.Guid} " +
            $"source={source} actionType={attackMode.ActionType} " +
            $"remaining={remainingUses}");
    }

    internal static void RecordInventoryAttackUse(
        RulesetCharacterSimulacrum character,
        RulesetAttackMode attackMode,
        RulesetItem sourceItem,
        AttackProximity proximity,
        bool hit,
        RulesetItem droppedItem,
        string ammunitionType,
        int ammunitionBefore,
        int ammunitionAfter,
        bool refreshAttackModes)
    {
        if (character == null || attackMode == null)
        {
            return;
        }

        var source = attackMode.SourceDefinition?.Name ?? "<null>";
        var detail =
            $"stage=inventory-attack-accounted guid={character.Guid} source={source} " +
            $"item={sourceItem?.Guid ?? 0} slot={attackMode.SlotName} " +
            $"proximity={proximity} thrown={attackMode.Thrown} hit={hit} " +
            $"dropped={droppedItem?.Guid ?? 0} " +
            $"ammunition={ammunitionType ?? string.Empty} " +
            $"ammunitionCount={ammunitionBefore}->{ammunitionAfter} " +
            $"refreshAttackModes={refreshAttackModes}";

        WriteChanged(
            "actions",
            $"inventory-attack|{character.Guid}|{source}|{attackMode.SlotName}|" +
            $"{proximity}|{attackMode.Thrown}",
            detail);
    }

    internal static void RecordSourceSenses(RulesetCharacter character)
    {
        if (character == null)
        {
            return;
        }

        var senses = string.Join(
            ",",
            character.SenseModes
                .OrderBy(mode => mode.SenseType)
                .ThenBy(mode => mode.SenseRange)
                .Select(mode =>
                    $"{mode.SenseType}:{mode.SenseRange}:{mode.StealthBreakerRange}"));

        var requiredSenses = string.Join(",", character.RequiredSenseTypesToPerceive.OrderBy(x => x));

        if (!AddEventKey(
                $"source-senses|{character.Guid}|{senses}|{character.MaxSenseRange}|" +
                $"{character.ImpairedSight}|{character.VisionHeight:0.###}|{requiredSenses}"))
        {
            return;
        }

        Write(
            "vision",
            $"stage=source guid={character.Guid} type={character.GetType().Name} senses={senses} " +
            $"maxSense={character.MaxSenseRange} impairedSight={character.ImpairedSight} " +
            $"visionHeight={character.VisionHeight:0.###} " +
            $"visionHeightFactor={character.VisionHeightFactor:0.###} " +
            $"requiredSenses={requiredSenses}");
    }

    internal static void RecordPerceptionState(
        RulesetCharacterSimulacrum character,
        GameLocationCharacter locationCharacter)
    {
        if (character == null || locationCharacter == null)
        {
            return;
        }

        var allies = string.Join(
            ",",
            locationCharacter.PerceivedAllies
                .Where(x => x != null)
                .Select(x => x.Guid)
                .OrderBy(x => x));
        var foes = string.Join(
            ",",
            locationCharacter.PerceivedFoes
                .Where(x => x != null)
                .Select(x => x.Guid)
                .OrderBy(x => x));
        var lineOfSight = string.Join(
            ",",
            locationCharacter.LineOfSightRatio
                .Where(x => x.Key != null)
                .OrderBy(x => x.Key.Guid)
                .Select(x => $"{x.Key.Guid}:{x.Value:0.###}"));
        var state =
            $"{locationCharacter.PerceptionState}|{allies}|{foes}|" +
            $"{character.MaxSenseRange}|{character.ImpairedSight}";
        var detail =
            $"stage=perception guid={character.Guid} side={locationCharacter.Side} " +
            $"faction={character.CurrentFaction?.Name ?? "<null>"} " +
            $"position={locationCharacter.LocationPosition} sensor={locationCharacter.SensorPosition} " +
            $"state={locationCharacter.PerceptionState} maxSense={character.MaxSenseRange} " +
            $"impairedSight={character.ImpairedSight} visionHeight={character.VisionHeight:0.###} " +
            $"allies={allies} foes={foes} los={lineOfSight}";

        WriteChangedState(
            "vision",
            $"{character.Guid}|perception",
            state,
            detail);
    }

    internal static void RecordFailedPerception(
        RulesetCharacterSimulacrum character,
        GameLocationCharacter sensor,
        GameLocationCharacter target)
    {
        if (character == null || sensor == null || target?.RulesetCharacter == null)
        {
            return;
        }

        var hasLineOfSight = sensor.LineOfSightRatio.TryGetValue(target, out var lineOfSightRatio);
        var hasStealthRatio = sensor.StealthDetectionRatio.TryGetValue(target, out var stealthRatio);
        var requiredSenses = string.Join(
            ",",
            target.RulesetCharacter.RequiredSenseTypesToPerceive.OrderBy(x => x));
        var senses = string.Join(
            ",",
            character.SenseModes
                .OrderBy(x => x.SenseType)
                .ThenBy(x => x.SenseRange)
                .Select(x => $"{x.SenseType}:{x.SenseRange}:{x.StealthBreakerRange}"));
        var canSense = character.CanSenseTarget(target.RulesetCharacter);
        var key = $"failed-perception|{character.Guid}|{target.Guid}|{sensor.LocationPosition}|" +
                  $"{target.LocationPosition}|{hasLineOfSight}|{lineOfSightRatio:0.###}|" +
                  $"{canSense}|{target.Stealthy}|{hasStealthRatio}|{stealthRatio:0.###}|" +
                  $"{requiredSenses}|{senses}|{character.MaxSenseRange}|" +
                  $"{character.ImpairedSight}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "vision",
            $"stage=perception-failed guid={character.Guid} target={target.Guid} " +
            $"sensorPosition={sensor.LocationPosition} targetPosition={target.LocationPosition} " +
            $"hasLos={hasLineOfSight} los={lineOfSightRatio:0.###} canSense={canSense} " +
            $"requiredSenses={requiredSenses} senses={senses} targetStealthy={target.Stealthy} " +
            $"maxSense={character.MaxSenseRange} impairedSight={character.ImpairedSight} " +
            $"visionHeight={character.VisionHeight:0.###} " +
            $"hasStealthRatio={hasStealthRatio} stealthRatio={stealthRatio:0.###}");
    }

    internal static void RecordSenseValidation(
        RulesetCharacterSimulacrum sensor,
        RulesetCharacter target,
        bool result)
    {
        if (sensor == null || target == null || result)
        {
            return;
        }

        var requiredSenses = string.Join(",", target.RequiredSenseTypesToPerceive.OrderBy(x => x));
        var senses = string.Join(
            ",",
            sensor.SenseModes
                .OrderBy(x => x.SenseType)
                .ThenBy(x => x.SenseRange)
                .Select(x => $"{x.SenseType}:{x.SenseRange}:{x.StealthBreakerRange}"));
        var key = $"sense-failed|{sensor.Guid}|{target.Guid}|{sensor.MaxSenseRange}|" +
                  $"{sensor.ImpairedSight}|{requiredSenses}|{senses}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "vision",
            $"stage=sense-failed guid={sensor.Guid} target={target.Guid} " +
            $"maxSense={sensor.MaxSenseRange} impairedSight={sensor.ImpairedSight} " +
            $"visionHeight={sensor.VisionHeight:0.###} requiredSenses={requiredSenses} " +
            $"senses={senses}");
    }

    internal static void RecordFeatureState(
        RulesetCharacterSimulacrum character,
        string stage)
    {
        if (character == null)
        {
            return;
        }

        var activeFeatures = character.ActiveFeatures
            .Where(x => x != null)
            .Distinct()
            .ToArray();
        var featuresToBrowse = new List<FeatureDefinition>();
        var featuresOrigin = new Dictionary<FeatureDefinition, RuleDefinitions.FeatureOrigin>();

        character.EnumerateFeaturesToBrowse<FeatureDefinition>(
            featuresToBrowse,
            featuresOrigin);

        var missingOrigins = featuresToBrowse
            .Where(x => x != null && !featuresOrigin.ContainsKey(x))
            .Select(x => x.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(MaximumDefinitionNames)
            .ToArray();
        var missingPowerFeatures = character.UsablePowers
            .Where(x => x?.PowerDefinition != null &&
                        !activeFeatures.Contains(x.PowerDefinition))
            .Select(x => x.PowerDefinition.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(MaximumDefinitionNames)
            .ToArray();
        var behaviorCarriers = SimulacrumBehavior
            .EnumerateBehaviorCarriers(character)
            .Where(x => x != null)
            .Distinct()
            .Count();

        Write(
            "features",
            $"stage={stage} scope=definition-parity guid={character.Guid} " +
            $"active={activeFeatures.Length} behaviorCarriers={behaviorCarriers} " +
            $"enumerated={featuresToBrowse.Count} origins={featuresOrigin.Count} " +
            $"missingOrigins={string.Join(",", missingOrigins)} " +
            $"missingPowerFeatures={string.Join(",", missingPowerFeatures)}");
    }

    internal static void RecordSpellbookSnapshot(
        RulesetCharacter character,
        IEnumerable<SpellDefinition> scribedSpells)
    {
        if (character == null)
        {
            return;
        }

        var names = scribedSpells?
            .Where(spell => spell != null)
            .Select(spell => spell.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(MaximumDefinitionNames)
            .ToArray() ?? [];

        if (names.Length == 0)
        {
            var magicAffinities = new List<FeatureDefinition>();

            character.EnumerateFeaturesToBrowse<FeatureDefinitionMagicAffinity>(
                magicAffinities);

            if (magicAffinities
                .OfType<FeatureDefinitionMagicAffinity>()
                .All(affinity => affinity.RitualCasting != RitualCasting.Spellbook))
            {
                // An empty physical spellbook is only relevant to spellbook ritual casting.
                // Known/prepared casters are diagnosed by the actual ritual selection path.
                return;
            }
        }

        if (!AddEventKey($"spellbook|{character.Guid}|{string.Join(",", names)}"))
        {
            return;
        }

        Write(
            "rituals",
            $"stage=source-spellbook guid={character.Guid} count={names.Length} " +
            $"names={string.Join(",", names)}");
    }

    internal static void RecordPowerSelection(
        RulesetCharacter character,
        ActionDefinitions.ActionType actionType,
        IEnumerable<RulesetUsablePower> relevantPowers)
    {
        if (character is not RulesetCharacterSimulacrum duplicate)
        {
            return;
        }

        var names = relevantPowers?
            .Where(x => x?.PowerDefinition != null)
            .Select(x => x.PowerDefinition.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(MaximumDefinitionNames)
            .ToArray() ?? [];
        var key = $"power-selection|{duplicate.Guid}|{actionType}|{string.Join(",", names)}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "powers",
            $"stage=selection guid={duplicate.Guid} action={actionType} " +
            $"count={names.Length} names={string.Join(",", names)}");
    }

    internal static void RecordRitualSelection(
        RulesetCharacter character,
        RuleDefinitions.RitualCasting ritualCasting,
        IEnumerable<SpellDefinition> ritualSpells)
    {
        if (character == null ||
            character is not RulesetCharacterSimulacrum &&
            character.SpellRepertoires.All(repertoire =>
                repertoire?.SpellCastingClass != Warlock))
        {
            return;
        }

        var names = ritualSpells?
            .Where(x => x != null)
            .Select(x => x.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(MaximumDefinitionNames)
            .ToArray() ?? [];
        var key = $"ritual-selection|{character.Guid}|{ritualCasting}|{string.Join(",", names)}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "rituals",
            $"stage=selection guid={character.Guid} casting={ritualCasting} " +
            $"count={names.Length} names={string.Join(",", names)}");
    }

    internal static void RecordInventoryShortcuts(
        RulesetCharacterSimulacrum character,
        string stage,
        int configurationCount,
        int childCount,
        int selectorCount)
    {
        if (character == null ||
            !AddEventKey(
                $"shortcuts|{character.Guid}|{stage}|{configurationCount}|" +
                $"{childCount}|{selectorCount}"))
        {
            return;
        }

        Write(
            "inventory",
            $"stage={stage} guid={character.Guid} configurations={configurationCount} " +
            $"children={childCount} selectors={selectorCount}");
    }

    internal static void RecordHitPointCorrection(
        RulesetCharacterSimulacrum character,
        int beforeRefresh,
        int afterNativeRefresh)
    {
        if (character == null ||
            !AddEventKey(
                $"hp-refresh|{character.Guid}|{beforeRefresh}|{afterNativeRefresh}|" +
                $"{character.CurrentHitPoints}"))
        {
            return;
        }

        Write(
            "character",
            $"stage=refresh-hp-restored guid={character.Guid} " +
            $"before={beforeRefresh} native={afterNativeRefresh} " +
            $"restored={character.CurrentHitPoints} " +
            $"maximum={character.TryGetAttributeValue(AttributeDefinitions.HitPoints)}");
    }

    internal static void RecordHealthMutation(
        RulesetCharacterSimulacrum character,
        int before,
        int requested,
        int after)
    {
        if (character == null || before == after)
        {
            return;
        }

        var suspicious = before > 1 && after <= 1 && character.LifecycleState ==
            SimulacrumLifecycleState.Ready;

        if (!suspicious &&
            !AddEventKey(
                $"health|{character.Guid}|{character.LifecycleState}|{before}|{requested}|{after}|" +
                $"{character.TryGetAttributeValue(AttributeDefinitions.HitPoints)}"))
        {
            return;
        }

        var stack = suspicious
            ? Environment.StackTrace
                .Replace("\r", string.Empty)
                .Replace("\n", " > ")
            : string.Empty;

        Write(
            "health",
            $"guid={character.Guid} lifecycle={character.LifecycleState} before={before} " +
            $"requested={requested} after={after} " +
            $"maximum={character.TryGetAttributeValue(AttributeDefinitions.HitPoints)} " +
            $"suspicious={suspicious} stack={stack}");
    }

    internal static void RecordDefinitions(
        string stage,
        ulong characterGuid,
        IEnumerable<BaseDefinition> definitions)
    {
        var allNames = definitions?
            .Where(x => x != null)
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray() ?? [];
        var truncated = allNames.Length > MaximumDefinitionNames;

        Write(
            "definitions",
            $"stage={stage} guid={characterGuid} count={allNames.Length} " +
            $"names={string.Join(",", allNames.Take(MaximumDefinitionNames))}" +
            (truncated ? ",..." : string.Empty));
    }

    internal static void RecordInvocationValidation(
        RulesetCharacterSimulacrum character,
        RulesetInvocation invocation,
        FeatureDefinitionPower power,
        bool result,
        string reason)
    {
        if (character == null)
        {
            return;
        }

        var definitionName = invocation?.InvocationDefinition?.Name ?? "<null>";
        var powerName = power?.Name ?? "<null>";
        var key = $"{character.Guid}|{definitionName}|{powerName}|{invocation?.Used}|{result}|{reason}";

        if (InvocationValidationKeys.Count >= MaximumValidationKeys)
        {
            InvocationValidationKeys.Clear();
        }

        if (!InvocationValidationKeys.Add(key))
        {
            return;
        }

        Write(
            "invocation",
            $"guid={character.Guid} definition={definitionName} power={powerName} " +
            $"materialOverride={invocation?.InvocationDefinition?.OverrideMaterialComponent.ToString() ?? "<null>"} " +
            $"active={invocation?.Active.ToString() ?? "<null>"} " +
            $"used={invocation?.Used.ToString() ?? "<null>"} " +
            $"hasPower={(power != null && character.HasPower(power))} " +
            $"remaining={(power != null ? character.GetRemainingPowerUses(power) : -1)} " +
            $"result={result} reason={reason}");
    }

    internal static void RecordInvocationStates(
        RulesetCharacterSimulacrum character)
    {
        if (character == null)
        {
            return;
        }

        var states = character.Invocations
            .Where(invocation => invocation?.InvocationDefinition != null)
            .Select(invocation =>
                $"{invocation.InvocationDefinition.Name}:" +
                $"{(invocation.InvocationRepertoire == null ? -1 : character.SpellRepertoires.IndexOf(invocation.InvocationRepertoire))}:" +
                $"active={invocation.Active}:used={invocation.Used}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        WriteChanged(
            "invocation-state",
            character.Guid.ToString(),
            $"guid={character.Guid} count={states.Length} states={string.Join(",", states)}");
    }

    internal static void RecordInvocationUiAvailability(
        RulesetCharacterSimulacrum character,
        InvocationDefinition definition,
        bool validationResult,
        bool interactable,
        string failure)
    {
        if (character == null || definition == null)
        {
            return;
        }

        WriteChanged(
            "invocation-ui",
            $"{character.Guid}|{definition.Name}|availability",
            $"stage=box-bind guid={character.Guid} definition={definition.Name} " +
            $"validation={validationResult} interactable={interactable} " +
            $"mismatch={validationResult != interactable} " +
            $"failure={failure ?? string.Empty}");
    }

    internal static void RecordInvocationSubspellAvailability(
        RulesetCharacterSimulacrum character,
        InvocationDefinition definition,
        SpellDefinition spell,
        string stage,
        bool validationResult,
        bool interactable,
        string failure)
    {
        if (character == null || definition == null)
        {
            return;
        }

        WriteChanged(
            "invocation-ui",
            $"{character.Guid}|{definition.Name}|{spell?.Name}|{stage}",
            $"stage={stage} guid={character.Guid} definition={definition.Name} " +
            $"spell={spell?.Name ?? "<null>"} validation={validationResult} " +
            $"interactable={interactable} mismatch={validationResult != interactable} " +
            $"failure={failure ?? string.Empty}");
    }

    internal static void RecordSpellValidation(
        SpellCastingValidationContext context,
        bool result,
        string failure)
    {
        if (context.Caster is not { } character)
        {
            return;
        }

        var invocation = context.ActiveSpell?.RulesetInvocation?.InvocationDefinition;

        // Availability success is high-volume and only proves that a definition passed a
        // menu predicate. Keep failures and execution/bypass outcomes, which can be
        // compared directly with actual play.
        if (result &&
            context.ActiveSpell == null &&
            !context.BypassComponentsAndCastingTime &&
            !context.BypassMaterialComponent)
        {
            return;
        }

        var spell = context.SpellDefinition?.Name ?? "<null>";
        var repertoire = context.Repertoire?.SpellCastingFeature?.Name ?? "<null>";
        var focusItems = new List<RulesetItem>();

        context.Caster.CharacterInventory?.EnumerateAllItems(focusItems);
        var focusSummary = string.Join(
            ",",
            focusItems
                .Where(item => item?.ItemDefinition?.IsFocusItem == true)
                .Select(item =>
                    $"{item.ItemDefinition.Name}:" +
                    $"{item.ItemDefinition.FocusItemDescription?.FocusType.ToString() ?? "<null>"}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Take(16));
        var stage = context.ActiveSpell == null ? "availability" : "execution";
        var key = $"{stage}|{character.Guid}|{spell}|{repertoire}|{invocation?.Name}|" +
                  $"{context.BypassComponentsAndCastingTime}|{context.BypassMaterialComponent}|" +
                  $"{result}|{failure}";

        if (SpellValidationKeys.Count >= MaximumValidationKeys)
        {
            SpellValidationKeys.Clear();
        }

        if (!SpellValidationKeys.Add(key))
        {
            return;
        }

        Write(
            "spell",
            $"stage={stage} guid={character.Guid} type={character.GetType().Name} " +
            $"spell={spell} invocation={invocation?.Name ?? "<none>"} repertoire={repertoire} " +
            $"focus={context.Repertoire?.SpellCastingFeature?.FocusType.ToString() ?? "<null>"} " +
            $"material={context.SpellDefinition?.MaterialComponentType.ToString() ?? "<null>"} " +
            $"duration={context.SpellDefinition?.EffectDescription?.DurationType.ToString() ?? "<null>"}:" +
            $"{context.SpellDefinition?.EffectDescription?.DurationParameter.ToString() ?? "<null>"} " +
            $"setting={ServiceRepository.GetService<IGameSettingsService>()?.MaterialComponent.ToString() ?? "<null>"} " +
            $"focusItems={focusSummary} " +
            $"bypassAll={context.BypassComponentsAndCastingTime} " +
            $"bypassMaterial={context.BypassMaterialComponent} result={result} " +
            $"failure={failure ?? string.Empty}");
    }

    internal static void RecordSpellFocusValidation(
        RulesetCharacterSimulacrum character,
        RulesetSpellRepertoire selectedRepertoire,
        SpellDefinition spell,
        bool result,
        string reason,
        IEnumerable<RulesetSpellRepertoire> relevantRepertoires = null,
        RulesetItem matchedItem = null,
        string affinityProvider = null)
    {
        if (character == null || spell == null)
        {
            return;
        }

        var inventory = character.CharacterInventory;
        RulesetInventorySlot mainHandSlot = null;
        RulesetInventorySlot offHandSlot = null;

        inventory?.InventorySlotsByName.TryGetValue(
            EquipmentDefinitions.SlotTypeMainHand,
            out mainHandSlot);
        inventory?.InventorySlotsByName.TryGetValue(
            EquipmentDefinitions.SlotTypeOffHand,
            out offHandSlot);

        var mainHand = mainHandSlot?.EquipedItem;
        var offHand = offHandSlot?.EquipedItem;
        var repertoireSummary = string.Join(
            ",",
            (relevantRepertoires ?? [])
            .Where(candidate => candidate?.SpellCastingFeature != null)
            .Select(candidate =>
                $"{candidate.SpellCastingFeature.Name}:" +
                $"{candidate.SpellCastingFeature.FocusType}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));
        var detail =
            $"stage=focus-validation guid={character.Guid} spell={spell.Name} " +
            $"selected={selectedRepertoire?.SpellCastingFeature?.Name ?? "<null>"} " +
            $"relevant={repertoireSummary} result={result} reason={reason} " +
            $"deity={character.DeityDefinition?.Name ?? "<none>"} " +
            $"main={DescribeFocusSlot(mainHand)} off={DescribeFocusSlot(offHand)} " +
            $"matched={matchedItem?.ItemDefinition?.Name ?? "<none>"}:" +
            $"{matchedItem?.Guid ?? 0} provider={affinityProvider ?? "<none>"}";

        WriteChanged(
            "spell-focus",
            $"{character.Guid}|{spell.Name}|" +
            $"{selectedRepertoire?.SpellCastingFeature?.Name ?? "<null>"}",
            detail);
    }

    internal static void RecordRuntimeMaterialValidation(
        RulesetCharacterSimulacrum character,
        SpellDefinition spell,
        bool result,
        string failure)
    {
        if (character == null || spell == null)
        {
            return;
        }

        WriteChanged(
            "spell-material",
            $"{character.Guid}|{spell.Name}",
            $"stage=runtime-validation guid={character.Guid} spell={spell.Name} " +
            $"material={spell.MaterialComponentType} result={result} " +
            $"failure={failure ?? string.Empty}");
    }

    private static string DescribeFocusSlot(RulesetItem item)
    {
        if (item?.ItemDefinition == null)
        {
            return "<empty>";
        }

        var definition = item.ItemDefinition;

        return
            $"{definition.Name}:{item.Guid}[focus={definition.IsFocusItem}:" +
            $"{definition.FocusItemDescription?.FocusType.ToString() ?? "<none>"};" +
            $"deityMark={item.DeityMark ?? "<none>"}]";
    }

    internal static void RecordSpellActivation(
        string stage,
        RulesetCharacter caster,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        bool? interactable = null,
        bool? globallyValid = null)
    {
        if (caster == null || spell == null)
        {
            return;
        }

        var isPerInteractionStage = stage is "box-click" or "engaged-enter" or
            "engaged-rebuilt" or "engaged-rejected" or "engaged-exit" or "execute-start";
        var sequence = Interlocked.Increment(ref _spellActivationSequence);

        if (!isPerInteractionStage &&
            !AddEventKey(
                $"spell-activation|{stage}|{caster.Guid}|{repertoire?.SpellCastingFeature?.Name}|" +
                $"{spell.Name}|{interactable}|{globallyValid}"))
        {
            return;
        }

        Write(
            "spell-activation",
            $"seq={sequence} stage={stage} guid={caster.Guid} type={caster.GetType().Name} " +
            $"spell={spell.Name} repertoire={repertoire?.SpellCastingFeature?.Name ?? "<null>"} " +
            $"duration={spell.EffectDescription?.DurationType.ToString() ?? "<null>"}:" +
            $"{spell.EffectDescription?.DurationParameter.ToString() ?? "<null>"} " +
            $"interactable={interactable?.ToString() ?? "<n/a>"} " +
            $"globalValid={globallyValid?.ToString() ?? "<n/a>"}");
    }

    internal static void RecordSpellEngagement(
        string stage,
        CharacterActionPanel panel,
        RulesetSpellRepertoire repertoire,
        SpellDefinition spell,
        ActionDefinitions.Id candidateAction,
        ActionDefinitions.ActionStatus actionStatus)
    {
        if (panel?.GuiCharacter?.RulesetCharacter is not RulesetCharacterSimulacrum duplicate ||
            spell == null)
        {
            return;
        }

        var actionParams = panel.actionParams;

        Write(
            "spell-engagement",
            $"seq={Interlocked.Increment(ref _spellActivationSequence)} stage={stage} " +
            $"guid={duplicate.Guid} spell={spell.Name} " +
            $"repertoire={repertoire?.SpellCastingFeature?.Name ?? "<null>"} " +
            $"panelAction={panel.actionId} candidateAction={candidateAction} status={actionStatus} " +
            $"hasParams={actionParams != null} " +
            $"paramsActor={actionParams?.ActingCharacter?.RulesetCharacter?.Guid ?? 0} " +
            $"paramsAction={actionParams?.ActionDefinition?.Id.ToString() ?? "<null>"} " +
            $"hasEffect={actionParams?.RulesetEffect != null}");
    }

    internal static void RecordMaterialTooltip(
        ITooltip tooltip,
        Image materialMarker,
        Color invalidColor)
    {
        var character = tooltip?.Context switch
        {
            RulesetCharacter rulesetCharacter => rulesetCharacter,
            GameLocationCharacter locationCharacter => locationCharacter.RulesetCharacter,
            GuiCharacter guiCharacter => guiCharacter.RulesetCharacter,
            _ => null
        };

        if (character is not RulesetCharacterSimulacrum duplicate)
        {
            return;
        }

        var spell = tooltip.DataProvider switch
        {
            GuiSpellDefinition guiSpell => guiSpell.SpellDefinition,
            SpellDefinition spellDefinition => spellDefinition,
            _ => null
        };

        SpellCastingValidation.TryGetSelectedRepertoire(
            duplicate,
            out var repertoire);
        var markerColor = materialMarker ? materialMarker.color : default;
        var markerActive = materialMarker && materialMarker.gameObject.activeSelf;
        var invalid = markerActive &&
                      Mathf.Approximately(markerColor.r, invalidColor.r) &&
                      Mathf.Approximately(markerColor.g, invalidColor.g) &&
                      Mathf.Approximately(markerColor.b, invalidColor.b) &&
                      Mathf.Approximately(markerColor.a, invalidColor.a);
        var key = $"material-tooltip|{duplicate.Guid}|{spell?.Name}|" +
                  $"{repertoire?.SpellCastingFeature?.Name}|{markerActive}|{invalid}";

        if (!AddEventKey(key))
        {
            return;
        }

        Write(
            "tooltip",
            $"stage=material guid={duplicate.Guid} spell={spell?.Name ?? "<null>"} " +
            $"repertoire={repertoire?.SpellCastingFeature?.Name ?? "<null>"} " +
            $"material={spell?.MaterialComponentType.ToString() ?? "<null>"} " +
            $"markerActive={markerActive} " +
            $"marker={(materialMarker ? markerColor.ToString() : "<null>")} " +
            $"invalid={invalid}");
    }

    internal static void RecordWishTooltipComponentBypass(SpellDefinition spell)
    {
        var definitionName = spell?.Name ?? "<null>";

        if (!AddEventKey($"wish-tooltip|components-bypassed|{definitionName}"))
        {
            return;
        }

        Write(
            "wish-tooltip",
            $"stage=components-bypassed definition={definitionName}");
    }

    internal static void RecordVisualRefresh(
        RulesetCharacterSimulacrum character,
        string stage,
        int requestedRevision,
        int completedRevision,
        bool pending,
        string equipmentSignature)
    {
        if (character == null)
        {
            return;
        }

        WriteChanged(
            "visual-refresh",
            $"{character.Guid}|{stage}",
            $"stage={stage} guid={character.Guid} requested={requestedRevision} " +
            $"completed={completedRevision} pending={pending} " +
            $"equipment={equipmentSignature ?? "<null>"}");
    }

    internal static void RecordPreviewRefresh(
        RulesetCharacterSimulacrum character,
        string stage,
        int visualRevision,
        bool pending,
        string equipmentSignature)
    {
        if (character == null)
        {
            return;
        }

        WriteChanged(
            "preview-refresh",
            $"{character.Guid}|{stage}",
            $"stage={stage} guid={character.Guid} visualRevision={visualRevision} " +
            $"pending={pending} equipment={equipmentSignature ?? "<null>"}");
    }

    internal static void RecordAppearanceFinalizationFailure(
        RulesetCharacterSimulacrum character,
        int attempt,
        string failedStages)
    {
        if (character == null)
        {
            return;
        }

        WriteChanged(
            "appearance-finalization",
            $"{character.Guid}|failed",
            $"stage=retry-required guid={character.Guid} attempt={attempt} " +
            $"failedStages={failedStages ?? "<null>"}");
    }

    internal static void RecordAppearance(
        RulesetCharacterSimulacrum character,
        string stage,
        string renderPath)
    {
        SimulacrumBehavior.TryGetVisualRefreshState(
            character,
            out var requestedRevision,
            out var completedRevision,
            out var refreshPending,
            out var equipmentSignature);
        var torsoSlot = GetInventorySlot(character, EquipmentDefinitions.SlotTypeTorso);
        var detail =
            $"stage={stage} guid={character?.Guid ?? 0} lifecycle={character?.LifecycleState.ToString() ?? "<null>"} " +
            $"inventoryAppearance={character?.UsesInventoryAppearanceSeed.ToString() ?? "<null>"} " +
            $"visual={requestedRevision}/{completedRevision} pending={refreshPending} " +
            $"equipment={equipmentSignature ?? "<null>"} " +
            $"renderPath={renderPath ?? "<null>"} body={character?.BodyAssetPrefix ?? "<null>"} " +
            $"armor={character?.ArmorAssetPrefix ?? "<null>"} helmet={character?.HelmetAssetPrefix ?? "<null>"} " +
            $"face={character?.FaceShapeAssetPrefix ?? "<null>"} " +
            $"hair={character?.HairShapeAssetPrefix ?? "<null>"} " +
            $"torsoSlot={(torsoSlot != null)} torsoDefaultVisual=" +
            $"{torsoSlot?.SlotTypeDefinition?.HasDefaultVisual.ToString() ?? "<null>"} " +
            $"torsoItem={torsoSlot?.EquipedItem?.ItemDefinition?.Name ?? "<empty>"} " +
            $"mainItem={GetInventorySlot(character, EquipmentDefinitions.SlotTypeMainHand)?.EquipedItem?.ItemDefinition?.Name ?? "<empty>"} " +
            $"offItem={GetInventorySlot(character, EquipmentDefinitions.SlotTypeOffHand)?.EquipedItem?.ItemDefinition?.Name ?? "<empty>"}";

        WriteChanged(
            "appearance",
            $"{character?.Guid ?? 0}|{stage}|{renderPath}",
            detail);
    }

    internal static void RecordGraphicsAppearance(
        RulesetCharacterSimulacrum character,
        GraphicsCharacter graphicsCharacter,
        string stage,
        int visualRevision = -1,
        string equipmentSignature = null)
    {
        if (character == null || !graphicsCharacter)
        {
            return;
        }

        try
        {
            SimulacrumBehavior.TryGetVisualRefreshState(
                character,
                out var requestedRevision,
                out var completedRevision,
                out var refreshPending,
                out var currentEquipmentSignature);

            if (visualRevision < 0)
            {
                visualRevision = requestedRevision;
            }

            equipmentSignature ??= currentEquipmentSignature;

            string skinMorphotype = null;

            character.MorphotypeElements?.TryGetValue(
                MorphotypeElementDefinition.ElementCategory.Skin,
                out skinMorphotype);
            MorphotypeElementDefinition skinDefinition = null;

            if (!string.IsNullOrEmpty(skinMorphotype))
            {
                DatabaseHelper.TryGetDefinition(skinMorphotype, out skinDefinition);
            }

            var expectedSkinColor = skinDefinition?.MainColor;
            var skinMaterials = graphicsCharacter.SkinMaterials?
                                    .Where(material => material)
                                    .Take(8)
                                    .ToArray() ??
                                [];
            var armRenderers = GetArmRenderers(graphicsCharacter);
            var armColorComparison =
                DescribeArmColorComparison(armRenderers, expectedSkinColor);
            var detail =
                $"stage={stage} guid={character.Guid} graphics={graphicsCharacter.name} " +
                $"graphicsType={graphicsCharacter.GetType().Name} " +
                $"characterType={graphicsCharacter.CharacterType} " +
                $"gameplayController={graphicsCharacter.UseGameplayController} " +
                $"visualRevision={visualRevision} requested={requestedRevision} " +
                $"completed={completedRevision} pending={refreshPending} " +
                $"equipment={equipmentSignature ?? "<null>"} " +
                $"armorInstances=[{DescribeArmorInstances(graphicsCharacter)}] " +
                $"armorObjects=[{DescribeArmorObjects(graphicsCharacter)}] " +
                $"skinMorph={skinMorphotype ?? "<null>"} " +
                $"expectedSkin={FormatColor(expectedSkinColor)} " +
                $"skinMaterials=[{DescribeSkinMaterials(skinMaterials)}] " +
                $"armRenderers=[{DescribeArmRenderers(armRenderers)}] " +
                $"armColorObservation={armColorComparison} " +
                $"{DescribeWieldedGraphics(graphicsCharacter, EquipmentDefinitions.SlotTypeMainHand)} " +
                $"{DescribeWieldedGraphics(graphicsCharacter, EquipmentDefinitions.SlotTypeOffHand)}";

            WriteChanged(
                "graphics-appearance",
                $"{character.Guid}|{stage}",
                detail);
        }
        catch (Exception exception)
        {
            // Diagnostics must never interrupt the graphics refresh callback that schedules the
            // actual preview rebuild.
            RecordException("graphics-appearance", stage, exception);
        }
    }

    private static string DescribeArmorInstances(GraphicsCharacter graphicsCharacter)
    {
        var instances = graphicsCharacter.ArmorInstancePerBodySlot;

        return instances == null
            ? "<null>"
            : string.Join(
                ",",
                instances
                    .Select((instance, index) => instance
                        ? $"{index}:{instance.name}"
                        : null)
                    .Where(value => value != null));
    }

    private static string DescribeArmorObjects(GraphicsCharacter graphicsCharacter)
    {
        var objects = graphicsCharacter.ArmorObjectPerBodySlot;

        return objects == null
            ? "<null>"
            : string.Join(
                ",",
                objects
                    .Where(pair => pair.Value)
                    .OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}:{pair.Value.name}"));
    }

    private static string DescribeSkinMaterials(IEnumerable<Material> materials)
    {
        return string.Join(
            ",",
            materials.Select(material =>
                $"{material.name}:{(material.HasProperty("_Color")
                    ? FormatColor(material.GetColor("_Color"))
                    : "<no-_Color>")}"));
    }

    private static string FormatColor(Color? color)
    {
        return color.HasValue
            ? $"{color.Value.r:0.###}/{color.Value.g:0.###}/" +
              $"{color.Value.b:0.###}/{color.Value.a:0.###}"
            : "<null>";
    }

    private static bool ColorsMatch(Color left, Color right)
    {
        return Mathf.Abs(left.r - right.r) <= 0.01f &&
               Mathf.Abs(left.g - right.g) <= 0.01f &&
               Mathf.Abs(left.b - right.b) <= 0.01f &&
               Mathf.Abs(left.a - right.a) <= 0.01f;
    }

    private static Renderer[] GetArmRenderers(GraphicsCharacter graphicsCharacter)
    {
        return graphicsCharacter
            .GetComponentsInChildren<Renderer>(true)
            .Where(renderer =>
                renderer &&
                (renderer.gameObject.name.IndexOf(
                     "Hands_",
                     StringComparison.OrdinalIgnoreCase) >= 0 ||
                 renderer.gameObject.name.IndexOf(
                     "Forearms_",
                     StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(renderer => renderer.gameObject.name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string DescribeArmRenderers(IEnumerable<Renderer> renderers)
    {
        return string.Join(
            ",",
            renderers.Select(renderer =>
            {
                var propertyBlock = new MaterialPropertyBlock();

                renderer.GetPropertyBlock(propertyBlock);

                var materials = string.Join(
                    "+",
                    renderer.sharedMaterials
                        .Where(material => material)
                        .Select(material =>
                        {
                            var color = material.HasProperty("_Color")
                                ? FormatColor(material.GetColor("_Color"))
                                : "<no-_Color>";
                            var baseColor = material.HasProperty("_BaseColor")
                                ? FormatColor(material.GetColor("_BaseColor"))
                                : "<no-_BaseColor>";

                            return $"{material.name}[_Color={color};_BaseColor={baseColor}]";
                        }));
                var block = propertyBlock.isEmpty
                    ? "empty"
                    : $"present[_Color={FormatColor(propertyBlock.GetColor("_Color"))};" +
                      $"_BaseColor={FormatColor(propertyBlock.GetColor("_BaseColor"))}]";

                return
                    $"{renderer.gameObject.name}:{renderer.GetType().Name}:" +
                    $"{(renderer.gameObject.activeInHierarchy ? "active" : "inactive")}:" +
                    $"materials=[{materials}]:propertyBlock={block}";
            }));
    }

    private static string DescribeArmColorComparison(
        IEnumerable<Renderer> renderers,
        Color? expectedColor)
    {
        if (!expectedColor.HasValue)
        {
            return "unknown-no-expected-color";
        }

        var rendererArray = renderers.ToArray();

        if (rendererArray.Length == 0)
        {
            return "unknown-no-arm-renderer";
        }

        var observedColors = new List<Color>();

        foreach (var renderer in rendererArray)
        {
            var propertyBlock = new MaterialPropertyBlock();

            renderer.GetPropertyBlock(propertyBlock);

            if (!propertyBlock.isEmpty)
            {
                return "unknown-property-block-present";
            }

            foreach (var material in renderer.sharedMaterials.Where(material => material))
            {
                if (material.HasProperty("_BaseColor"))
                {
                    observedColors.Add(material.GetColor("_BaseColor"));
                }
                else if (material.HasProperty("_Color"))
                {
                    observedColors.Add(material.GetColor("_Color"));
                }
            }
        }

        if (observedColors.Count == 0)
        {
            return "unknown-no-color-property";
        }

        return observedColors.All(color => ColorsMatch(color, expectedColor.Value))
            ? "observed-match"
            : "observed-different";
    }

    private static RulesetInventorySlot GetInventorySlot(
        RulesetCharacterSimulacrum character,
        string slotName)
    {
        RulesetInventorySlot slot = null;

        character?.CharacterInventory?.InventorySlotsByName?.TryGetValue(slotName, out slot);

        return slot;
    }

    private static string DescribeWieldedGraphics(
        GraphicsCharacter graphicsCharacter,
        string slotName)
    {
        RulesetItem rulesetItem = null;
        GameObject instantiatedItem = null;
        GameObject prefab = null;
        Material[] cachedMaterials = null;

        graphicsCharacter.WieldedRulesetItems?.TryGetValue(slotName, out rulesetItem);
        graphicsCharacter.WieldedInstantiatedItems?.TryGetValue(slotName, out instantiatedItem);
        graphicsCharacter.WieldedItemsPrefabs?.TryGetValue(slotName, out prefab);
        graphicsCharacter.WieldedItemMaterials?.TryGetValue(slotName, out cachedMaterials);

        var itemDefinition = rulesetItem?.ItemDefinition;
        var assetGuid = itemDefinition?.ItemPresentation?.AssetReference?.AssetGUID;
        var renderers = instantiatedItem
            ? instantiatedItem.GetComponentsInChildren<Renderer>(true)
            : [];
        var rendererSummary = renderers.Length == 0
            ? "<none>"
            : string.Join(
                ",",
                renderers
                    .Take(4)
                    .Select(DescribeRenderer));
        var cachedMaterialSummary = cachedMaterials == null || cachedMaterials.Length == 0
            ? "<none>"
            : string.Join(
                ",",
                cachedMaterials
                    .Where(material => material)
                    .Take(4)
                    .Select(material => material.name));

        return
            $"{slotName}={{ruleset={itemDefinition?.Name ?? "<empty>"} " +
            $"guid={rulesetItem?.Guid.ToString() ?? "<null>"} " +
            $"asset={assetGuid ?? "<null>"} prefab={prefab?.name ?? "<null>"} " +
            $"instance={instantiatedItem?.name ?? "<null>"} " +
            $"active={instantiatedItem?.activeInHierarchy.ToString() ?? "<null>"} " +
            $"cachedMaterials=[{cachedMaterialSummary}] renderers=[{rendererSummary}]}}";
    }

    private static string DescribeRenderer(Renderer renderer)
    {
        if (!renderer)
        {
            return "<null>";
        }

        var materialSummary = string.Join(
            "+",
            renderer.sharedMaterials
                .Where(material => material)
                .Take(3)
                .Select(material =>
                {
                    var hasMainTexture = material.HasProperty("_MainTex");
                    var texture = hasMainTexture
                        ? material.GetTexture("_MainTex")
                        : null;

                    return
                        $"{material.name}/{material.shader?.name ?? "<null>"}/" +
                        $"{(hasMainTexture ? texture?.name ?? "<null>" : "<no-_MainTex>")}:" +
                        $"{(texture ? $"{texture.width}x{texture.height}" : "<null>")}";
                }));

        return
            $"{renderer.gameObject.name}:{renderer.GetType().Name}:" +
            $"{(renderer.gameObject.activeInHierarchy ? "active" : "inactive")}:" +
            $"[{materialSummary}]";
    }

    internal static void RecordPortrait(
        RulesetCharacterSimulacrum character,
        string stage,
        RawImage image,
        Texture texture = null,
        int visualRevision = -1,
        int portraitRevision = -1,
        string equipmentSignature = null)
    {
        var rect = image ? image.rectTransform.rect : default;
        var textureSize = texture
            ? $"{texture.GetType().Name}:{texture.width}x{texture.height}"
            : "<null>";
        var uv = image ? image.uvRect : default;

        if (!AddEventKey(
                $"portrait|{character?.Guid ?? 0}|{stage}|" +
                $"{visualRevision}|{portraitRevision}|" +
                $"{rect.width:0.#}x{rect.height:0.#}|{textureSize}"))
        {
            return;
        }

        Write(
            "portrait",
            $"stage={stage} guid={character?.Guid ?? 0} visualRevision={visualRevision} " +
            $"portraitRevision={portraitRevision} equipment={equipmentSignature ?? "<null>"} " +
            $"image={rect.width:0.#}x{rect.height:0.#} " +
            $"texture={textureSize} uv={uv.x:0.###},{uv.y:0.###},{uv.width:0.###},{uv.height:0.###} " +
            $"graphicsBound={HasBoundGraphics(character)}");
    }

    internal static void RecordTooltip(
        RulesetCharacterSimulacrum character,
        int hitPoints,
        int armorClass,
        string movement)
    {
        if (character == null ||
            !AddEventKey(
                $"tooltip|{character.Guid}|{hitPoints}|{armorClass}|{movement}"))
        {
            return;
        }

        Write(
            "tooltip",
            $"guid={character.Guid} hp={hitPoints} ac={armorClass} " +
            $"movement={movement ?? "<null>"} source=live-character");
    }

    internal static void RecordException(string area, string stage, Exception exception)
    {
        Write(
            area,
            $"stage={stage} exception={exception?.GetType().FullName ?? "<null>"} " +
            $"message={exception?.Message ?? string.Empty}");
    }

    internal static void Write(string area, string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (!RevisionReported)
            {
                RevisionReported = true;
                Main.Info(
                    $"{Prefix} meta packageVersion={Main.Version} " +
                    $"assemblyMvid={typeof(SimulacrumDiagnostics).Module.ModuleVersionId:N} " +
                    $"revision={ProbeRevision}");
            }

            Main.Info($"{Prefix} {area} {message}");
        }
        catch
        {
            // Diagnostics must never affect gameplay.
        }
    }

    private static bool HasBoundGraphics(RulesetCharacterSimulacrum character)
    {
        if (character == null ||
            GameLocationCharacter.GetFromActor(character) is not { } locationCharacter ||
            ServiceRepository.GetService<IWorldLocationEntityFactoryService>() is not { } entityFactory)
        {
            return false;
        }

        return entityFactory.TryFindWorldCharacter(locationCharacter, out var worldCharacter) &&
               worldCharacter?.GraphicsCharacter != null;
    }

    private static bool AddEventKey(string key)
    {
        if (EventKeys.Count >= MaximumEventKeys)
        {
            EventKeys.Clear();
        }

        return EventKeys.Add(key);
    }

    private static void WriteChanged(string area, string stateKey, string message)
    {
        WriteChangedState(area, stateKey, message, message);
    }

    private static void WriteChangedState(
        string area,
        string stateKey,
        string comparisonState,
        string message)
    {
        var key = $"{area}|{stateKey}";

        if (LastStates.TryGetValue(key, out var previous) &&
            string.Equals(previous, comparisonState, StringComparison.Ordinal))
        {
            return;
        }

        if (LastStates.Count >= MaximumStateKeys)
        {
            LastStates.Clear();
        }

        LastStates[key] = comparisonState;
        Write(area, message);
    }
}
