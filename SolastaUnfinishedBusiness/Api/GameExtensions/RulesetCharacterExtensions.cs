using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Validators;
using static RuleDefinitions;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class RulesetCharacterExtensions
{
#if false
    internal static bool IsWearingLightArmor([NotNull] this RulesetCharacter _)
    {
        return false;
    }

    internal static bool IsWieldingTwoHandedWeapon([NotNull] this RulesetCharacter _)
    {
        return false;
    }
#endif

    internal static RulesetCharacter GetEffectControllerOrSelf(this RulesetCharacter rulesetCharacter)
    {
        if (rulesetCharacter is not RulesetCharacterEffectProxy effectProxy)
        {
            return rulesetCharacter;
        }

        var controllerCharacter = EffectHelpers.GetCharacterByGuid(effectProxy.ControllerGuid);

        return controllerCharacter ?? rulesetCharacter;
    }

    internal static IEnumerable<T> GetUsableSpellSubFeaturesByType<T>(this RulesetCharacter rulesetCharacter)
        where T : class
    {
        return rulesetCharacter.UsableSpells
            .SelectMany(spell => spell.GetAllSubFeaturesOfType<T>())
            .Distinct();
    }

    internal static int GetSubclassLevel(
        this RulesetCharacter character, CharacterClassDefinition klass, string subclass)
    {
        var provider = character.GetSubFeaturesByType<ISubclassLevelProvider>().FirstOrDefault();

        if (provider != null)
        {
            return provider.GetSubclassLevel(character, klass, subclass);
        }

        var hero = character.GetOriginalHero();

        if (hero == null
            || !hero.ClassesAndSubclasses.TryGetValue(klass, out var characterSubclassDefinition)
            || characterSubclassDefinition.Name != subclass)
        {
            return 0;
        }

        return hero.GetClassLevel(klass);
    }

    internal static DieType GetMonkDieType(this RulesetCharacter character)
    {
        var monkLevel = character.GetClassLevel(DatabaseHelper.CharacterClassDefinitions.Monk);
        var dieType = DatabaseHelper.FeatureDefinitionAttackModifiers.AttackModifierMonkMartialArtsImprovedDamage
            .DieTypeByRankTable
            .Find(x => x.Rank == monkLevel)?.DieType ?? DieType.D1;

        return dieType;
    }

    internal static RulesetItem GetMainWeapon(this RulesetCharacter hero)
    {
        return hero.GetItemInSlot(EquipmentDefinitions.SlotTypeMainHand);
    }

    internal static RulesetItem GetOffhandWeapon(this RulesetCharacter hero)
    {
        return hero.GetItemInSlot(EquipmentDefinitions.SlotTypeOffHand);
    }

    internal static bool IsWearingMediumArmor([NotNull] this RulesetCharacter character)
    {
        if (character is not RulesetCharacterHero &&
            character is not RulesetCharacterSimulacrum)
        {
            return false;
        }

        var inventory = character.CharacterInventory;
        RulesetInventorySlot torsoSlot = null;

        inventory?.InventorySlotsByName?.TryGetValue(
            EquipmentDefinitions.SlotTypeTorso,
            out torsoSlot);

        var equipedItem = torsoSlot?.EquipedItem;

        if (equipedItem == null || !equipedItem.ItemDefinition.IsArmor)
        {
            return false;
        }

        var armorDescription = equipedItem.ItemDefinition.ArmorDescription;
        var element = DatabaseRepository.GetDatabase<ArmorTypeDefinition>().GetElement(armorDescription.ArmorType);

        return DatabaseRepository.GetDatabase<ArmorCategoryDefinition>()
                   .GetElement(element.ArmorCategory).IsPhysicalArmor
               && element.ArmorCategory == EquipmentDefinitions.MediumArmorCategory;
    }

    internal static bool IsValid(this RulesetCharacter instance, [NotNull] params IsCharacterValidHandler[] validators)
    {
        return validators.All(v => v(instance));
    }

    internal static bool IsValid(this RulesetCharacter instance,
        [NotNull] IEnumerable<IsCharacterValidHandler> validators)
    {
        return validators.All(v => v(instance));
    }

    internal static bool HasPower(
        this RulesetCharacter instance,
        [CanBeNull] FeatureDefinitionPower power)
    {
        return instance.GetPowerFromDefinition(power) != null && instance.HasAnyFeature(power);
    }

    internal static bool CanSeeAndUseAtLeastOnePower(this RulesetCharacter character, ActionType type, bool battle)
    {
        var usablePowers = character.UsablePowers;
        var overridenPowers = new List<FeatureDefinitionPower>();

        foreach (var power in usablePowers.Where(x => x.PowerDefinition.OverriddenPower))
        {
            overridenPowers.TryAdd(power.PowerDefinition.OverriddenPower);
        }

        foreach (var usablePower in usablePowers)
        {
            var power = usablePower.PowerDefinition;
            if (power.DelegatedToAction)
            {
                continue;
            }

            if (overridenPowers.Contains(power))
            {
                continue;
            }

            var activationTime = power.ActivationTime;

            if (activationTime is not (ActivationTime.Action
                or ActivationTime.BonusAction
                or ActivationTime.NoCost
                or ActivationTime.Reaction
                or ActivationTime.Minute1
                or ActivationTime.Minute10
                or ActivationTime.Hours1
                or ActivationTime.Hours24
                or ActivationTime.Rest
                or ActivationTime.Permanent
                or ActivationTime.PermanentUnlessIncapacitated))
            {
                continue;
            }

            if (battle)
            {
                if (!CastingTimeToActionDefinition.ContainsKey(activationTime))
                {
                    continue;
                }

                var activation = CastingTimeToActionDefinition[activationTime];

                if (activation != type)
                {
                    continue;
                }
            }

            if (ModifyPowerVisibility.IsPowerHidden(character, power, type))
            {
                continue;
            }

            if (power.GuiPresentation.Hidden)
            {
                continue;
            }

            if (!character.CanUsePower(power, true, true) &&
                !character.CanDisplayPowerWhenUnavailable(power, true))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    internal static bool CanDisplayPowerWhenUnavailable(
        this RulesetCharacter instance,
        [CanBeNull] FeatureDefinitionPower power,
        bool considerHaving = false)
    {
        if (!power ||
            !ModifyPowerVisibility.ShouldKeepVisibleWhenUnavailable(power) ||
            (considerHaving && !instance.HasPower(power)))
        {
            return false;
        }

        return instance is not RulesetCharacterSimulacrum duplicate ||
               SimulacrumBehavior.IsPowerCurrentlyActive(duplicate, power);
    }

    /**Checks if power has enough uses and that all validators are OK*/
    internal static bool CanUsePower(this RulesetCharacter instance,
        [CanBeNull] FeatureDefinitionPower power,
        bool considerUses = true,
        bool considerHaving = false)
    {
        if (!power)
        {
            return false;
        }

        if (instance is RulesetCharacterSimulacrum duplicate &&
            !SimulacrumBehavior.IsPowerCurrentlyActive(duplicate, power))
        {
            return false;
        }

        if (considerHaving && !instance.HasPower(power))
        {
            return false;
        }

        if (considerUses && instance.GetRemainingPowerUses(power) <= 0)
        {
            return false;
        }

        return power.GetAllSubFeaturesOfType<IValidatePowerUse>()
            .All(v => v.CanUsePower(instance, power));
    }

    internal static bool CanCastCantrip(this RulesetCharacter character,
        SpellDefinition cantrip,
        [CanBeNull] out RulesetSpellRepertoire spellRepertoire)
    {
        spellRepertoire = null;

        foreach (var repertoire in character.spellRepertoires.Where(repertoire => repertoire.KnownCantrips.Any(Matches)
                     || repertoire.ExtraSpellsByTag.SelectMany(x => x.Value).Any(Matches)))
        {
            spellRepertoire = repertoire;

            return true;
        }

        return false;

        bool Matches(SpellDefinition knownCantrip)
        {
            return knownCantrip == cantrip ||
                   (knownCantrip.SpellsBundle && knownCantrip.SubspellsList.Contains(cantrip));
        }
    }

#if false
    [NotNull]
    internal static List<RulesetAttackMode> GetAttackModesByActionType([NotNull] this RulesetCharacter instance,
        ActionDefinitions.ActionType actionType)
    {
        return instance.AttackModes
            .Where(a => !a.AfterChargeOnly && a.ActionType == actionType)
            .ToList();
    }

    internal static bool CanAddAbilityBonusToOffhand(this RulesetCharacter instance)
    {
        return instance.GetSubFeaturesByType<IAttackModificationProvider>()
            .Any(p => p.CanAddAbilityBonusToSecondary);
    }
#endif

    [CanBeNull]
    internal static RulesetItem GetItemInSlot([CanBeNull] this RulesetCharacter instance, string slot)
    {
        var inventorySlot = instance?.CharacterInventory?.InventorySlotsByName?[slot];

        return inventorySlot?.EquipedItem;
    }

    [CanBeNull]
    internal static RulesetSpellRepertoire GetClassSpellRepertoire(
        this RulesetCharacter instance,
        string className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return instance.GetClassSpellRepertoire();
        }

        var classDefinition = DatabaseHelper.GetDefinition<CharacterClassDefinition>(className);

        return instance.GetClassSpellRepertoire(classDefinition);
    }

    [CanBeNull]
    internal static RulesetSpellRepertoire GetClassSpellRepertoire(
        this RulesetCharacter instance,
        CharacterClassDefinition classDefinition)
    {
        var className = !classDefinition ? string.Empty : classDefinition.name;
        var rulesetCharacter = instance.GetClassFeatureStatsOwner();

        if (string.IsNullOrEmpty(className) ||
            rulesetCharacter is not RulesetCharacterHero &&
            rulesetCharacter is not RulesetCharacterSimulacrum)
        {
            return rulesetCharacter.GetClassSpellRepertoire();
        }

        CharacterSubclassDefinition subclassDefinition = null;

        if (rulesetCharacter is RulesetCharacterHero hero && classDefinition)
        {
            hero.ClassesAndSubclasses.TryGetValue(classDefinition, out subclassDefinition);
        }
        else if (rulesetCharacter is RulesetCharacterSimulacrum simulacrum && classDefinition)
        {
            SimulacrumBehavior.TryGetPrimarySubclass(
                simulacrum,
                classDefinition,
                out subclassDefinition);
        }

        return rulesetCharacter.SpellRepertoires.FirstOrDefault(r =>
            (r.SpellCastingFeature.SpellCastingOrigin == FeatureDefinitionCastSpell.CastingOrigin.Class &&
             r.SpellCastingClass == classDefinition) ||
             (r.SpellCastingFeature.SpellCastingOrigin == FeatureDefinitionCastSpell.CastingOrigin.Subclass &&
             (r.SpellCastingClass == classDefinition ||
              subclassDefinition != null && r.SpellCastingSubclass == subclassDefinition)));
    }

    internal static bool IsSpellOnClassOrSubclassSpellList(
        this RulesetCharacter instance,
        SpellDefinition spellDefinition,
        CharacterClassDefinition classDefinition)
    {
        if (instance == null || !spellDefinition || !classDefinition)
        {
            return false;
        }

        if (SpellsContext.SpellsChildMaster.TryGetValue(spellDefinition, out var masterSpell))
        {
            spellDefinition = masterSpell;
        }

        var featureOwner = instance.GetFeatureOwnerOrSelf();

        if (featureOwner == null || featureOwner.GetClassLevel(classDefinition) == 0)
        {
            return false;
        }

        var spellRepertoire = featureOwner.GetClassSpellRepertoire(classDefinition);

        if (spellRepertoire?.SpellCastingFeature?.HasAccessToSpell(spellDefinition) == true)
        {
            return true;
        }

        var classLevel = featureOwner.GetClassLevel(classDefinition);
        HashSet<FeatureDefinition> visitedFeatures = [];

        return EnumerateClassSpellListFeatures(featureOwner, classDefinition)
            .Any(feature => GrantsClassSpellAccess(
                feature,
                spellDefinition,
                classDefinition,
                classLevel,
                visitedFeatures));
    }

    internal static bool IsSpellCastAsClassOrSubclassSpell(
        this RulesetCharacter instance,
        RulesetEffectSpell spellEffect,
        CharacterClassDefinition classDefinition)
    {
        if (spellEffect == null)
        {
            return false;
        }

        var associatedRepertoire = spellEffect.GetClassOrSubclassSpellAssociation();

        if (associatedRepertoire == null && !spellEffect.UsesSpellListClassification())
        {
            return false;
        }

        return instance.IsSpellCastAsClassOrSubclassSpell(
            spellEffect.SpellRepertoire,
            spellEffect.SpellDefinition,
            classDefinition,
            associatedRepertoire == null);
    }

    internal static bool IsSpellCastAsClassOrSubclassSpell(
        this RulesetCharacter instance,
        RulesetSpellRepertoire spellRepertoire,
        SpellDefinition spellDefinition,
        CharacterClassDefinition classDefinition,
        bool useSpellListClassification)
    {
        if (!instance.IsSpellOnClassOrSubclassSpellList(spellDefinition, classDefinition))
        {
            return false;
        }

        if (useSpellListClassification)
        {
            return true;
        }

        var castingFeature = spellRepertoire?.SpellCastingFeature;

        if (castingFeature == null)
        {
            return false;
        }

        if (castingFeature.SpellCastingOrigin != FeatureDefinitionCastSpell.CastingOrigin.Class &&
            castingFeature.SpellCastingOrigin != FeatureDefinitionCastSpell.CastingOrigin.Subclass)
        {
            return false;
        }

        if (castingFeature.SpellCastingOrigin == FeatureDefinitionCastSpell.CastingOrigin.Subclass &&
            spellRepertoire.SpellCastingSubclass)
        {
            return LevelUpHelper.GetClassForSubclass(spellRepertoire.SpellCastingSubclass) == classDefinition;
        }

        return spellRepertoire.GetCastingClass() == classDefinition;
    }

    internal static RulesetSpellRepertoire GetClassOrSubclassSpellAssociation(
        this RulesetEffectSpell spellEffect)
    {
        if (spellEffect == null ||
            spellEffect.OriginItem != null ||
            spellEffect is RulesetEffectSpellWithOrigin
            {
                Mode: not RulesetEffectSpellWithOrigin.OriginMode.None
            })
        {
            return null;
        }

        var spellRepertoire = spellEffect.SpellRepertoire;
        var castingOrigin = spellRepertoire?.SpellCastingFeature?.SpellCastingOrigin;

        return castingOrigin is FeatureDefinitionCastSpell.CastingOrigin.Class or
            FeatureDefinitionCastSpell.CastingOrigin.Subclass
            ? spellRepertoire
            : null;
    }

    internal static bool UsesSpellListClassification(this RulesetEffectSpell spellEffect)
    {
        if (spellEffect == null)
        {
            return false;
        }

        if (spellEffect.OriginItem != null)
        {
            return true;
        }

        var castingOrigin = spellEffect.SpellRepertoire?.SpellCastingFeature?.SpellCastingOrigin;

        if (spellEffect is RulesetEffectSpellWithOrigin
            {
                Mode: not RulesetEffectSpellWithOrigin.OriginMode.None
            })
        {
            return castingOrigin is FeatureDefinitionCastSpell.CastingOrigin.Class or
                FeatureDefinitionCastSpell.CastingOrigin.Subclass or
                FeatureDefinitionCastSpell.CastingOrigin.Race;
        }

        return castingOrigin == FeatureDefinitionCastSpell.CastingOrigin.Race;
    }

    private static IEnumerable<FeatureDefinition> EnumerateClassSpellListFeatures(
        RulesetCharacter featureOwner,
        CharacterClassDefinition classDefinition)
    {
        if (featureOwner is RulesetCharacterSimulacrum simulacrum)
        {
            SimulacrumBehavior.TryGetPrimarySubclass(
                simulacrum,
                classDefinition,
                out var simulacrumSubclass);

            var classLevel = simulacrum.GetClassLevel(classDefinition);
            var unlockedFeatures = classDefinition.FeatureUnlocks
                .Where(unlock => unlock.Level <= classLevel)
                .Select(unlock => unlock.FeatureDefinition)
                .Concat(simulacrumSubclass
                    ? simulacrumSubclass.FeatureUnlocks
                        .Where(unlock => unlock.Level <= classLevel)
                        .Select(unlock => unlock.FeatureDefinition)
                    : []);
            var copiedActiveFeatures = SimulacrumBehavior.GetCurrentlyActiveFeatures(simulacrum)
                .Where(feature => IsSimulacrumClassOrSubclassFeature(
                    simulacrum,
                    feature,
                    classDefinition));

            return unlockedFeatures.Concat(copiedActiveFeatures).Distinct();
        }

        if (featureOwner is not RulesetCharacterHero hero)
        {
            return [];
        }

        hero.ClassesAndSubclasses.TryGetValue(classDefinition, out var subclassDefinition);

        return Enumerable.Range(1, hero.GetClassLevel(classDefinition))
            .SelectMany(level => EnumerateClassSpellListFeaturesAtLevel(
                hero,
                classDefinition,
                subclassDefinition,
                level));
    }

    private static IEnumerable<FeatureDefinition> EnumerateClassSpellListFeaturesAtLevel(
        RulesetCharacterHero hero,
        CharacterClassDefinition classDefinition,
        CharacterSubclassDefinition subclassDefinition,
        int level)
    {
        if (hero.ActiveFeatures.TryGetValue(
                AttributeDefinitions.GetClassTag(classDefinition, level),
                out var classFeatures))
        {
            foreach (var feature in classFeatures)
            {
                yield return feature;
            }
        }

        if (subclassDefinition &&
            hero.ActiveFeatures.TryGetValue(
                AttributeDefinitions.GetSubclassTag(classDefinition, level, subclassDefinition),
                out var subclassFeatures))
        {
            foreach (var feature in subclassFeatures)
            {
                yield return feature;
            }
        }
    }

    private static bool GrantsClassSpellAccess(
        FeatureDefinition feature,
        SpellDefinition spellDefinition,
        CharacterClassDefinition classDefinition,
        int classLevel,
        HashSet<FeatureDefinition> visitedFeatures)
    {
        if (!feature || !visitedFeatures.Add(feature))
        {
            return false;
        }

        switch (feature)
        {
            case FeatureDefinitionCastSpell castSpell:
                return castSpell.HasAccessToSpell(spellDefinition);

            case FeatureDefinitionMagicAffinity { ExtendedSpellList: not null } magicAffinity:
                return magicAffinity.ExtendedSpellList.ContainsSpell(spellDefinition);

            case FeatureDefinitionAutoPreparedSpells autoPreparedSpells
                when autoPreparedSpells.SpellcastingClass == classDefinition:
                return autoPreparedSpells.AutoPreparedSpellsGroups.Any(group =>
                    group.ClassLevel <= classLevel && group.SpellsList.Contains(spellDefinition));

            case FeatureDefinitionBonusCantrips bonusCantrips:
                return bonusCantrips.BonusCantrips.Contains(spellDefinition);

            case FeatureDefinitionFeatureSet { Mode: FeatureDefinitionFeatureSet.FeatureSetMode.Union } featureSet:
                return featureSet.FeatureSet.Any(child => GrantsClassSpellAccess(
                    child,
                    spellDefinition,
                    classDefinition,
                    classLevel,
                    visitedFeatures));

            default:
                return false;
        }
    }

    private static bool IsSimulacrumClassOrSubclassFeature(
        RulesetCharacterSimulacrum simulacrum,
        FeatureDefinition feature,
        CharacterClassDefinition classDefinition)
    {
        if (simulacrum?.FeaturesOrigin == null ||
            feature == null ||
            !simulacrum.FeaturesOrigin.TryGetValue(feature, out var origin))
        {
            return false;
        }

        return origin.source switch
        {
            CharacterClassDefinition sourceClass => sourceClass == classDefinition,
            CharacterSubclassDefinition sourceSubclass =>
                LevelUpHelper.GetClassForSubclass(sourceSubclass) == classDefinition,
            _ => false
        };
    }

    /**@returns true if item holds an infusion created by this character*/
    internal static bool HoldsMyInfusion(this RulesetCharacter instance, RulesetItem item)
    {
        if (item == null)
        {
            return false;
        }

        return instance.IsMyInfusion(item.SourceSummoningEffectGuid)
               || item.dynamicItemProperties.Any(property => instance.IsMyInfusion(property.SourceEffectGuid));
    }

    /**@returns true if effect with this guid is an infusion created by this character*/
    private static bool IsMyInfusion(this RulesetCharacter instance, ulong guid)
    {
        if (instance == null || guid == 0)
        {
            return false;
        }

        var (caster, definition) = EffectHelpers.GetCharacterAndSourceDefinitionByEffectGuid(guid);

        if (caster == null || !definition)
        {
            return false;
        }

        return caster == instance
               //detecting if this item is from infusion by checking if it has infusion limiter
               && definition.GetAllSubFeaturesOfType<ILimitEffectInstances>().Contains(InventorClass.InfusionLimiter);
    }

    /**@returns character who summoned this creature, or null*/
    internal static GameLocationCharacter GetMySummoner(this RulesetCharacter instance)
    {
        if (instance == null)
        {
            return null;
        }

        if (!instance.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagConjure,
                ConditionConjuredCreature, out var conjured))
        {
            return null;
        }

        return RulesetEntity.TryGetEntity<RulesetCharacter>(conjured.SourceGuid, out var actor)
            ? GameLocationCharacter.GetFromActor(actor)
            : null;
    }

    internal static int GetClassLevel(this RulesetCharacter instance, CharacterClassDefinition classDefinition)
    {
        var provider = instance.GetSubFeaturesByType<IClassLevelProvider>().FirstOrDefault();

        if (provider != null)
        {
            return provider.GetClassLevel(instance, classDefinition);
        }

        var hero = instance.GetOriginalHero();

        return hero?.GetClassLevel(classDefinition) ?? 0;
    }

    internal static int GetClassLevel(this RulesetCharacter instance, string className)
    {
        if (DatabaseHelper.TryGetDefinition<CharacterClassDefinition>(className, out var classDefinition))
        {
            return instance.GetClassLevel(classDefinition);
        }

        return 0;
    }

    internal static bool HasActiveInvocation(this RulesetCharacter self, InvocationDefinition invocation)
    {
        return self?.Invocations.Any(i => i.InvocationDefinition == invocation && i.Active) == true;
    }

    internal static bool KnowsAnyInvocationOfActionId(this RulesetCharacter instance,
        Id actionId,
        ActionScope scope)
    {
        if (instance.Invocations.Count == 0)
        {
            return false;
        }

        foreach (var invocation in instance.Invocations)
        {
            bool isValid;
            var definition = invocation.invocationDefinition;

            if (scope == ActionScope.Battle)
            {
                isValid = definition.GetActionId() == actionId;
            }
            else
            {
                isValid = definition.GetMainActionId() == actionId;
            }

            if (isValid)
            {
                return true;
            }
        }

        return false;
    }

    internal static void ShowDieRoll(
        this RulesetCharacter character,
        DieType dieType,
        int roll1,
        int roll2 = 0,
        string title = "",
        bool displayOutcome = false,
        RollOutcome outcome = RollOutcome.Neutral,
        bool displayModifier = false,
        int modifier = 0,
        AdvantageType advantage = AdvantageType.None)
    {
        if (Gui.GameLocation.FiniteStateMachine.CurrentState is LocationState_NarrativeSequence or LocationState_Map)
        {
            return;
        }

        var labelScreen = Gui.GuiService.GetScreen<GameLocationLabelScreen>();

        if (!labelScreen)
        {
            return;
        }

        var worldChar = labelScreen.characterLabelsMap.Keys
            .FirstOrDefault(x => x.gameCharacter.RulesetCharacter == character);

        if (!worldChar)
        {
            return;
        }

        var roll = advantage switch
        {
            AdvantageType.Advantage => Math.Max(roll1, roll2),
            AdvantageType.Disadvantage => Math.Min(roll1, roll2),
            _ => roll1
        };

        var label = labelScreen.characterLabelsMap[worldChar];

        var info = new DieRollModule.RollInfo(
            title,
            dieType,
            DieRollModule.RollType.Attack,
            roll,
            advantage,
            roll1,
            modifier,
            roll2,
            outcome,
            displayOutcome: displayOutcome,
            side: character.Side,
            displayModifier: displayModifier) { rollImmediatly = false };

        label.dieRollModule.RollDie(info);
    }

    internal static bool IsToggleEnabled(this RulesetCharacter rulesetCharacter, Id actionId)
    {
        var toggleName = actionId.ToString();
        var reverse = CustomActionIdContext.IsReverseToggleId(actionId);

        return reverse ^ rulesetCharacter.ToggledPowersOn.Contains(toggleName);
    }

    internal static void DisableToggle(this RulesetCharacter rulesetCharacter, Id actionId)
    {
        var reverse = CustomActionIdContext.IsReverseToggleId(actionId);
        rulesetCharacter.SetToggle(actionId, reverse);
    }

    internal static void EnableToggle(this RulesetCharacter rulesetCharacter, Id actionId)
    {
        var reverse = CustomActionIdContext.IsReverseToggleId(actionId);
        rulesetCharacter.SetToggle(actionId, !reverse);
    }

    private static void SetToggle(this RulesetCharacter rulesetCharacter, Id actionId, bool value)
    {
        var toggleName = actionId.ToString();

        if (value)
        {
            rulesetCharacter.ToggledPowersOn.Add(toggleName);
        }
        else
        {
            rulesetCharacter.ToggledPowersOn.Remove(toggleName);
        }
    }

    internal static RulesetAttackMode TryRefreshAttackMode(
        this RulesetCharacter character,
        ActionType actionType,
        ItemDefinition itemDefinition,
        WeaponDescription weaponDescription,
        bool freeOffHand,
        bool canAddAbilityDamageBonus,
        string slotName,
        List<IAttackModificationProvider> attackModifiers,
        Dictionary<FeatureDefinition, FeatureOrigin> featuresOrigin,
        RulesetItem weapon = null)
    {
        return character switch
        {
            RulesetCharacterHero hero => hero.RefreshAttackMode(
                actionType,
                itemDefinition,
                weaponDescription,
                freeOffHand,
                canAddAbilityDamageBonus,
                slotName,
                attackModifiers,
                featuresOrigin,
                weapon),
            RulesetCharacterMonster monster => monster.RefreshAttackMode(
                actionType,
                itemDefinition,
                weaponDescription,
                freeOffHand,
                canAddAbilityDamageBonus,
                slotName,
                attackModifiers,
                featuresOrigin,
                weapon),
            _ => null
        };
    }

    internal static bool IsMyFavoriteEnemy(this RulesetCharacter me, RulesetCharacter enemy)
    {
        if (me == null || enemy == null)
        {
            return false;
        }

        return DatabaseHelper.FeatureDefinitionFeatureSets.AdditionalDamageRangerFavoredEnemyChoice.FeatureSet
            .OfType<FeatureDefinitionAdditionalDamage>()
            .Intersect(me.FeaturesByType<FeatureDefinitionAdditionalDamage>())
            .Any(x => x.RequiredCharacterFamily.Name == enemy.CharacterFamily);
    }
#if false
    internal static void ShowLabel(this RulesetCharacter character, string text, string color = Gui.ColorBrokenWhite)
    {
        if (character == null)
        {
            return;
        }

        if (!ServiceRepository.GetService<IWorldLocationEntityFactoryService>()
                .TryFindWorldCharacter(character, out var worldCharacter))
        {
            return;
        }

        var labels = Gui.GuiService.GetScreen<GameLocationLabelScreen>();
        if (!labels.characterLabelsMap.TryGetValue(worldCharacter, out var label))
        {
            return;
        }

        label.EnqueueCaption(new CharacterLabel.CaptionInfo { caption = text, colorString = color });
    }
#endif
    [CanBeNull]
    internal static RulesetCharacterHero GetOriginalHero(this RulesetCharacter character)
    {
        return character switch
        {
            RulesetCharacterHero hero => hero,
            RulesetCharacterSimulacrum => null,
            _ => character?.OriginalFormCharacter as RulesetCharacterHero
        };
    }

    [CanBeNull]
    internal static RulesetCharacter GetFeatureOwnerOrSelf(this RulesetCharacter character)
    {
        return character switch
        {
            RulesetCharacterHero hero => hero,
            RulesetCharacterSimulacrum simulacrum => simulacrum,
            RulesetCharacterMonster
            {
                OriginalFormCharacter: RulesetCharacterSimulacrum simulacrum
            } => simulacrum,
            _ when character.TryGetShapeChangeOriginalHero(out var hero) => hero,
            _ => null
        };
    }

    [CanBeNull]
    internal static RulesetCharacter GetClassFeatureStatsOwner(this RulesetCharacter character)
    {
        if (character == null)
        {
            return null;
        }

        var featureOwner = character.GetFeatureOwnerOrSelf();

        if (featureOwner != null)
        {
            return featureOwner;
        }

        return character.HasSubFeatureOfType<IUseOwnStatsWhenSummoned>()
            ? character
            : character.GetMySummoner()?.RulesetCharacter ?? character;
    }

    internal static bool TryGetShapeChangeOriginalHero(
        this RulesetCharacter character,
        out RulesetCharacterHero hero)
    {
        hero = character is RulesetCharacterSimulacrum
            ? null
            : character?.OriginalFormCharacter as RulesetCharacterHero;

        return hero != null && hero != character;
    }

    internal static bool HasTemporaryConditionOfType(this RulesetCharacter character, string conditionName)
    {
        return character.ConditionsByCategory
            .SelectMany(x => x.Value)
            .Any(condition => condition.ConditionDefinition.IsSubtypeOf(conditionName) &&
                              condition.DurationType != DurationType.Permanent);
    }

    internal static IEnumerable<RulesetItemDevice> EnumerateInventoryDevices(
        this RulesetCharacter character,
        bool includeContainer,
        bool ignoreActivationTimeChecks = false)
    {
        List<RulesetItemDevice> devices = [];
        var inBattle = ServiceRepository.GetService<IGameLocationBattleService>() is
        {
            IsBattleInProgress: true
        };

        if (character is RulesetCharacterHero { UsableDeviceFromMenu: { } selectedDevice })
        {
            devices.TryAdd(selectedDevice);

            return devices;
        }

        var inventory = character.CharacterInventory;

        if (inventory == null)
        {
            return devices;
        }

        foreach (var slotName in new[]
                 {
                     EquipmentDefinitions.SlotTypeMainHand,
                     EquipmentDefinitions.SlotTypeOffHand
                 })
        {
            if (!inventory.InventorySlotsByName.TryGetValue(slotName, out var slot) ||
                slot?.EquipedItem is not RulesetItemDevice
                {
                    HasUsableFunctions: true,
                    ItemDefinition: { } itemDefinition
                } device ||
                !itemDefinition.SlotsWhereActive.Contains(slotName) ||
                character is RulesetCharacterSimulacrum &&
                itemDefinition.RequiresAttunement ||
                slotName != EquipmentDefinitions.SlotTypeOffHand &&
                !device.IsAnyFunctionAvailable(
                    character,
                    inBattle,
                    false,
                    false,
                    ignoreActivationTimeChecks))
            {
                continue;
            }

            devices.TryAdd(device);
        }

        foreach (var pair in inventory.InventorySlotsByType.Where(pair =>
                     pair.Key != EquipmentDefinitions.SlotTypeMainHand &&
                     pair.Key != EquipmentDefinitions.SlotTypeOffHand))
        {
            foreach (var slot in pair.Value)
            {
                if (slot.EquipedItem is not RulesetItemDevice
                    {
                        HasUsableFunctions: true,
                        ItemDefinition: { } itemDefinition
                    } device ||
                    !itemDefinition.SlotsWhereActive.Contains(pair.Key) ||
                    character is RulesetCharacterSimulacrum &&
                    itemDefinition.RequiresAttunement ||
                    !device.IsAnyFunctionAvailable(
                        character,
                        inBattle,
                        false,
                        false,
                        ignoreActivationTimeChecks))
                {
                    continue;
                }

                devices.TryAdd(device);
            }
        }

        if (includeContainer)
        {
            foreach (var slot in inventory.PersonalContainer.InventorySlots)
            {
                if (slot?.EquipedItem is not RulesetItemDevice
                    {
                        HasUsableFunctions: true,
                        ItemDefinition: { } itemDefinition
                    } device ||
                    character is RulesetCharacterSimulacrum &&
                    itemDefinition.RequiresAttunement ||
                    !device.IsAnyFunctionAvailable(
                        character,
                        inBattle,
                        false,
                        false,
                        ignoreActivationTimeChecks))
                {
                    continue;
                }

                devices.TryAdd(device);
            }
        }

        if (character is RulesetCharacterHero or RulesetCharacterSimulacrum)
        {
            foreach (var device in character.GetSubFeaturesByType<PowerPoolDevice>()
                         .Select(provider => provider.GetDevice(character)))
            {
                devices.TryAdd(device);
            }
        }

        return devices;
    }
}
