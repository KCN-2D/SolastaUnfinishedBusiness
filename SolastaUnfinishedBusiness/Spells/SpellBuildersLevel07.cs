using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Properties;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionDamageAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ItemDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;

namespace SolastaUnfinishedBusiness.Spells;

internal static partial class SpellBuilders
{
    #region Simulacrum

    internal const string SimulacrumName = "Simulacrum";

    internal static SpellDefinition Simulacrum { get; private set; }

    internal static SpellDefinition BuildSimulacrum()
    {
        if (Simulacrum)
        {
            return Simulacrum;
        }

        var simulacrumSprite = Sprites.GetSprite(
            SimulacrumName,
            Resources.Simulacrum,
            128);
        var simulacrumConditionSprite = Sprites.GetSprite(
            $"Condition{SimulacrumName}",
            Resources.ConditionSimulacrum,
            32);
        var rubyMaterialCost =
            EquipmentDefinitions.GetApproximateCostInGold(Ingredient_Enchant_Blood_Gem.Costs);

        if (!Ingredient_Enchant_Blood_Gem.ItemTags.Contains(SimulacrumBehavior.RubyMaterialTag))
        {
            Ingredient_Enchant_Blood_Gem.ItemTags.Add(SimulacrumBehavior.RubyMaterialTag);
        }

        var simulacrumPresentations = BuildSimulacrumPresentations(simulacrumSprite);

        SimulacrumBehavior.BindPresentations(simulacrumPresentations);

        var repairPower = BuildSimulacrumRepairPower(simulacrumSprite);
        var dismissPower = BuildSimulacrumDismissPower(simulacrumSprite);

        var ownerCondition = ConditionDefinitionBuilder
            .Create($"Condition{SimulacrumName}Owner")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetFeatures(repairPower)
            .AddToDB();

        ownerCondition.AddCustomSubFeatures(AddUsablePowersFromCondition.Marker);
        ownerCondition.AddCustomSubFeatures(SimulacrumBehavior.OwnerReconciliationMarker);

        var snapshotCondition = ConditionDefinitionBuilder
            .Create($"Condition{SimulacrumName}Snapshot")
            .SetGuiPresentation(SimulacrumName, Category.Condition, simulacrumConditionSprite)
            .SetPossessive()
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetFeatures(FeatureDefinitionHealingModifiers.HealingModifierChilledByTouch)
            .AddToDB();

        snapshotCondition.AddCustomSubFeatures(
            SimulacrumBehavior.SnapshotBindingMarker,
            SimulacrumBehavior.RuntimeRestrictionsMarker);

        var initiativeMarker = FeatureDefinitionBuilder
            .Create($"Feature{SimulacrumName}Initiative")
            .SetGuiPresentationNoContent(true)
            .AddCustomSubFeatures(ForceInitiativeToSummoner.Mark)
            .AddToDB();

        var shellsBySize = DatabaseRepository
            .GetDatabase<CharacterSizeDefinition>()
            .Where(size => size != null)
            .OrderBy(size => size.Name)
            .ToDictionary(
                size => size.Name,
                size => BuildSimulacrumShell(
                    size,
                    initiativeMarker,
                    dismissPower,
                    simulacrumPresentations.Values.ToArray(),
                    simulacrumSprite));
        var definitionPresentationShells = BuildDefinitionPresentationShells(
            initiativeMarker,
            dismissPower,
            simulacrumPresentations.Values.ToArray(),
            simulacrumSprite);
        var defaultShell = shellsBySize[CharacterSizeDefinitions.Medium.Name];
        var behavior = new SimulacrumBehavior(
            shellsBySize.ToDictionary(pair => pair.Key, pair => pair.Value.Name),
            definitionPresentationShells);

        Simulacrum = SpellDefinitionBuilder
            .Create(SimulacrumName)
            .SetGuiPresentation(Category.Spell, simulacrumSprite)
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolIllusion)
            .SetSpellLevel(7)
            .SetCastingTime(ActivationTime.Hours1)
            .SetSpecificMaterialComponent(
                SimulacrumBehavior.RubyMaterialTag,
                rubyMaterialCost,
                true)
            .SetSomaticComponent(true)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Buff)
            .SetUniqueInstance()
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Permanent)
                    .SetTargetingData(
                        Side.Ally,
                        RangeType.Touch,
                        1,
                        TargetType.IndividualsUnique)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetSummonCreatureForm(1, defaultShell.Name)
                            .Build())
                    .SetParticleEffectParameters(SpellDefinitions.MirrorImage)
                    .Build())
            .AddToDB();

        Simulacrum.AddCustomSubFeatures(
            behavior,
            new CustomSpellCastingTime(
                12 * 60 * 60,
                "Rules/&ActivationTypeHours12Title"),
            SimulacrumBehavior.StackedMaterialRequirement,
            RestrictEffectToNotTerminateWhileUnconscious.Marker,
            SkipEffectRemovalOnLocationChange.Always);

        SimulacrumBehavior.BindDefinitions(
            Simulacrum,
            snapshotCondition,
            ownerCondition);
        SimulacrumBehavior.BindPowers(repairPower);

        return Simulacrum;
    }

    private static MonsterDefinition BuildSimulacrumShell(
        CharacterSizeDefinition sizeDefinition,
        FeatureDefinition initiativeMarker,
        FeatureDefinitionPower dismissPower,
        HumanoidMonsterPresentationDefinition[] presentations,
        UnityEngine.AddressableAssets.AssetReferenceSprite sprite,
        string shellName = null,
        MonsterDefinition presentationSource = null)
    {
        var builder = MonsterDefinitionBuilder
            .Create(
                GetDefinition<MonsterDefinition>("CultistGuard"),
                shellName ?? $"{SimulacrumName}Shell{sizeDefinition.Name}")
            .SetGuiPresentation(SimulacrumName, Category.Monster, sprite)
            .SetFeatures(initiativeMarker, dismissPower)
            .ClearAttackIterations()
            .SetAbilityScores(10, 10, 10, 10, 10, 10)
            .SetArmorClass(10, EquipmentDefinitions.EmptyMonsterArmor)
            .SetHitDice(DieType.D8, 1)
            .SetStandardHitPoints(1)
            .SetSizeDefinition(sizeDefinition)
            .SetAlignment(MonsterDefinitionBuilder.NeutralAlignment)
            .SetCharacterFamily(CharacterFamilyDefinitions.Construct.Name)
            .SetChallengeRating(0)
            .SetDroppedLootDefinition(null)
            .SetFullyControlledWhenAllied(true)
            .SetDefaultFaction(FactionDefinitions.Party)
            .SetBestiaryEntry(BestiaryDefinitions.BestiaryEntry.None)
            .SetDungeonMakerPresence(MonsterDefinition.DungeonMaker.None)
            .SetMonsterPresentation(
                presentationSource?.MonsterPresentation ??
                BuildSimulacrumMonsterPresentation(presentations))
            .NoExperienceGain();

        if (presentationSource != null)
        {
            builder
                .SetHeight(presentationSource.Height)
                .SetPresentationRuntimeData(presentationSource);
        }

        var shell = builder.AddToDB();

        shell.AddCustomSubFeatures(RulesetCharacterSimulacrum.FactoryMarker);
        shell.stealableLootDefinition = null;
        shell.bestiaryLootOptions = [];

        return shell;
    }

    private static IReadOnlyDictionary<string, string> BuildDefinitionPresentationShells(
        FeatureDefinition initiativeMarker,
        FeatureDefinitionPower dismissPower,
        HumanoidMonsterPresentationDefinition[] presentations,
        UnityEngine.AddressableAssets.AssetReferenceSprite sprite)
    {
        var shells = new Dictionary<string, string>();

        foreach (var source in DatabaseRepository
                     .GetDatabase<MonsterDefinition>()
                     .Where(source =>
                         source != null &&
                         source.CharacterFamily is "Beast" or "Humanoid" &&
                         source.SizeDefinition != null &&
                         source.MonsterPresentation is
                         {
                             HasPrefabVariants: false,
                             UseHumanoidMonsterPresentationName: false
                         })
                     .OrderBy(source => source.Name)
                     .ToArray())
        {
            if (source.MonsterPresentation
                    .GetPrefabReference(CreatureSex.Female)?.RuntimeKeyIsValid() != true &&
                source.MonsterPresentation
                    .GetPrefabReference(CreatureSex.Male)?.RuntimeKeyIsValid() != true)
            {
                continue;
            }

            var shell = BuildSimulacrumShell(
                source.SizeDefinition,
                initiativeMarker,
                dismissPower,
                presentations,
                sprite,
                $"{SimulacrumName}Shell{source.SizeDefinition.Name}For{source.Name}",
                source);

            shells[source.Name] = shell.Name;
        }

        return shells;
    }

    private static MonsterPresentation BuildSimulacrumMonsterPresentation(
        HumanoidMonsterPresentationDefinition[] presentations)
    {
        var source = GetDefinition<MonsterDefinition>("CultistGuard").MonsterPresentation;

        return new MonsterPresentation
        {
            useHumanoidMonsterPresentationName = true,
            humanoidMonsterPresentationDefinitions = presentations,
            useCustomMaterials = false,
            customMaterials = [],
            customShaderReference = source.customShaderReference,
            mutantFleshDirection = source.mutantFleshDirection,
            overrideCharacterShaderColors = false,
            firstCharacterShaderColor = source.firstCharacterShaderColor,
            secondCharacterShaderColor = source.secondCharacterShaderColor,
            hasPhantomDistortion = source.hasPhantomDistortion,
            hasPhantomFadingFeet = source.hasPhantomFadingFeet,
            hasPhantomVertexAnimation = source.hasPhantomVertexAnimation,
            attachedParticlesReference = source.attachedParticlesReference,
            bestiaryAttachedParticlesReference = source.bestiaryAttachedParticlesReference,
            hasPrefabVariants = false,
            monsterPresentationDefinitions = [],
            malePrefabReference = source.malePrefabReference,
            maleModelScale = source.maleModelScale,
            femalePrefabReference = source.femalePrefabReference,
            femaleModelScale = source.femaleModelScale,
            wieldedItemsScale = source.wieldedItemsScale,
            hideWieldedItemsWhenPassive = source.hideWieldedItemsWhenPassive,
            hideDuringCutscene = source.hideDuringCutscene,
            hasLightingCutscene = source.hasLightingCutscene,
            canGeneratePortrait = true,
            needMerchantPortrait = false,
            hasMonsterPortraitBackground = false,
            portraitCameraFollowOffset = source.portraitCameraFollowOffset,
            portraitCameraLookAtScreenOffset = source.portraitCameraLookAtScreenOffset,
            portraitCameraFOV = source.portraitCameraFOV,
            portraitCameraLightingOffset = source.portraitCameraLightingOffset
        };
    }

    private static IReadOnlyDictionary<string, HumanoidMonsterPresentationDefinition>
        BuildSimulacrumPresentations(
            UnityEngine.AddressableAssets.AssetReferenceSprite simulacrumSprite)
    {
        var presentations = new Dictionary<string, HumanoidMonsterPresentationDefinition>();
        var template = GetDefinition<MonsterDefinition>("CultistGuard")
            .MonsterPresentation
            .humanoidMonsterPresentationDefinitions
            .First(x => x != null);

        foreach (var race in DatabaseRepository
                     .GetDatabase<CharacterRaceDefinition>()
                     .Where(x => x != null)
                     .OrderBy(x => x.Name))
        {
            BuildForRaceAndSubrace(race, null);

            foreach (var subRace in race.SubRaces
                         .Where(x => x != null)
                         .OrderBy(x => x.Name))
            {
                BuildForRaceAndSubrace(race, subRace);
            }
        }

        return presentations;

        void BuildForRaceAndSubrace(
            CharacterRaceDefinition race,
            CharacterRaceDefinition subRace)
        {
            foreach (var sex in new[] { CreatureSex.Female, CreatureSex.Male })
            {
                var key = SimulacrumBehavior.GetPresentationKey(race, subRace, sex);

                if (presentations.ContainsKey(key))
                {
                    continue;
                }

                var suffix = subRace?.Name ?? "Base";
                var definition = HumanoidMonsterPresentationDefinitionBuilder
                    .Create(
                        template,
                        $"SimulacrumPresentation_{race.Name}_{suffix}_{sex}")
                    .SetGuiPresentation(SimulacrumName, Category.Monster, simulacrumSprite)
                    .SetCharacterAppearance(race, subRace, sex, ClothesCommon.Name)
                    .AddToDB();

                presentations.Add(key, definition);
            }
        }
    }

    private static FeatureDefinitionPower BuildSimulacrumRepairPower(
        UnityEngine.AddressableAssets.AssetReferenceSprite sprite)
    {
        var power = FeatureDefinitionPowerBuilder
            .Create(SimulacrumBehavior.RepairPowerName)
            .SetGuiPresentation(Category.Feature, sprite)
            .SetUsesFixed(ActivationTime.Rest, RechargeRate.LongRest)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms()
                    .Build())
            .AddToDB();

        power.AddCustomSubFeatures(
            ModifyPowerVisibility.Hidden,
            SimulacrumBehavior.CreateRepairPowerMarker(),
            SimulacrumBehavior.RepairRestPowerSelectionMarker);

        RestActivityDefinitionBuilder
            .Create($"RestActivity{SimulacrumName}Repair")
            .SetGuiPresentation(SimulacrumBehavior.RepairPowerName, Category.Feature, sprite)
            .SetRestData(
                RestDefinitions.RestStage.AfterRest,
                RestType.LongRest,
                RestActivityDefinition.ActivityCondition.CanUsePower,
                PowerBundleContext.UseCustomRestPowerFunctorName,
                SimulacrumBehavior.RepairPowerName)
            .AddToDB();

        return power;
    }

    private static FeatureDefinitionPower BuildSimulacrumDismissPower(
        UnityEngine.AddressableAssets.AssetReferenceSprite sprite)
    {
        var power = FeatureDefinitionPowerBuilder
            .Create(SimulacrumBehavior.DismissPowerName)
            .SetGuiPresentation(Category.Feature, sprite)
            .SetUsesFixed(ActivationTime.NoCost)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms()
                    .Build())
            .AddToDB();

        power.AddCustomSubFeatures(SimulacrumBehavior.CreateDismissPowerMarker());

        return power;
    }

    #endregion

    #region Reverse Gravity

    internal static SpellDefinition BuildReverseGravity()
    {
        const string NAME = "ReverseGravity";

        return SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.ReverseGravity, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolTransmutation)
            .SetSpellLevel(7)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetSomaticComponent(true)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Attack)
            .SetRequiresConcentration(true)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.All, RangeType.Distance, 20, TargetType.Cylinder, 10, 10)
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, true,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetConditionForm(
                                ConditionDefinitionBuilder
                                    .Create(ConditionDefinitions.ConditionLevitate, "ConditionReverseGravity")
                                    .SetOrUpdateGuiPresentation(Category.Condition)
                                    .SetConditionType(ConditionType.Neutral)
                                    .SetParentCondition(ConditionDefinitions.ConditionFlying)
                                    .SetFeatures(
                                        FeatureDefinitionActionAffinitys.ActionAffinityConditionRestrained,
                                        FeatureDefinitionMovementAffinitys.MovementAffinityConditionRestrained)
                                    .AddToDB(),
                                ConditionForm.ConditionOperation.Add)
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .Build(),
                        EffectFormBuilder
                            .Create()
                            .SetMotionForm(MotionForm.MotionType.Levitate, 10)
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .Build())
                    .SetRecurrentEffect(RecurrentEffect.OnActivation | RecurrentEffect.OnEnter)
                    .Build())
            .AddToDB();
    }

    #endregion

    #region Draconic Transformation

    internal static SpellDefinition BuildDraconicTransformation()
    {
        const string NAME = "DraconicTransformation";

        var sprite = Sprites.GetSprite(NAME, Resources.DraconicTransformation, 128);

        var conditionMark = ConditionDefinitionBuilder
            .Create($"Condition{NAME}Mark")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetSpecialInterruptions(ConditionInterruption.AnyBattleTurnEnd)
            .AddToDB();

        var power = FeatureDefinitionPowerBuilder
            .Create($"Power{NAME}")
            .SetGuiPresentation(Category.Feature, sprite)
            .SetUsesFixed(ActivationTime.BonusAction)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Cone, 12)
                    .SetSavingThrowData(false,
                        AttributeDefinitions.Dexterity,
                        false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDamageForm(DamageTypeForce, 6, DieType.D8)
                            .Build())
                    .SetParticleEffectParameters(ConeOfCold)
                    .SetCasterEffectParameters(GravitySlam)
                    .SetImpactEffectParameters(EldritchBlast)
                    .Build())
            .AddToDB();

        power.disableIfConditionIsOwned = conditionMark;

        var condition = ConditionDefinitionBuilder
            .Create(ConditionDefinitions.ConditionFlyingAdaptive, $"Condition{NAME}")
            .SetGuiPresentation(NAME, Category.Spell, ConditionDefinitions.ConditionFlying)
            .SetPossessive()
            .SetParentCondition(ConditionDefinitions.ConditionFlying)
            .SetFeatures(
                power,
                FeatureDefinitionMoveModes.MoveModeFly12,
                FeatureDefinitionSenses.SenseBlindSight6)
            .AddCustomSubFeatures(AddUsablePowersFromCondition.Marker)
            .AddToDB();

        condition.GuiPresentation.description = Gui.EmptyContent;

        return SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, sprite)
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolTransmutation)
            .SetSpellLevel(7)
            .SetCastingTime(ActivationTime.BonusAction)
            .SetMaterialComponent(MaterialComponentType.Specific)
            .SetSpecificMaterialComponent(TagsDefinitions.ItemTagDiamond, 500, false)
            .SetSomaticComponent(true)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Buff)
            .SetRequiresConcentration(true)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Cone, 12)
                    .SetSavingThrowData(false,
                        AttributeDefinitions.Dexterity,
                        false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder.ConditionForm(
                            conditionMark,
                            ConditionForm.ConditionOperation.Add, true, true),
                        EffectFormBuilder.ConditionForm(
                            condition,
                            ConditionForm.ConditionOperation.Add, true, true),
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDamageForm(DamageTypeForce, 6, DieType.D8)
                            .Build())
                    .SetParticleEffectParameters(ConeOfCold)
                    .SetCasterEffectParameters(GravitySlam)
                    .SetImpactEffectParameters(EldritchBlast)
                    .Build())
            .AddToDB();
    }

    #endregion

    #region Rescue the Dying

    internal static SpellDefinition BuildRescueTheDying()
    {
        const string RescueTheDyingName = "RescueTheDying";

        var condition = ConditionDefinitionBuilder
            .Create($"Condition{RescueTheDyingName}")
            .SetGuiPresentation(RescueTheDyingName, Category.Spell, ConditionDefinitions.ConditionMagicallyArmored)
            .SetPossessive()
            .SetFeatures(
                DamageAffinityAcidResistance,
                DamageAffinityBludgeoningResistanceTrue,
                DamageAffinityColdResistance,
                DamageAffinityFireResistance,
                DamageAffinityForceDamageResistance,
                DamageAffinityLightningResistance,
                DamageAffinityNecroticResistance,
                DamageAffinityPiercingResistanceTrue,
                DamageAffinityPoisonResistance,
                DamageAffinityPsychicResistance,
                DamageAffinityRadiantResistance,
                DamageAffinitySlashingResistanceTrue,
                DamageAffinityThunderResistance)
            .SetSpecialInterruptions(ExtraConditionInterruption.AfterWasAttacked)
            .AddToDB();

        condition.GuiPresentation.description = Gui.EmptyContent;

        var spell = SpellDefinitionBuilder
            .Create(RescueTheDyingName)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(RescueTheDyingName, Resources.RescueTheDying, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolTransmutation)
            .SetSpellLevel(7)
            .SetCastingTime(ActivationTime.Reaction)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetSomaticComponent(false)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Buff)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1, TurnOccurenceType.EndOfSourceTurn)
                    .SetTargetingData(Side.All, RangeType.Distance, 18, TargetType.IndividualsUnique)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel, additionalDicePerIncrement: 2)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetHealingForm(HealingComputation.Dice, 30, DieType.D10, 4, false,
                                HealingCap.MaximumHitPoints)
                            .Build(),
                        EffectFormBuilder.ConditionForm(condition))
                    .SetParticleEffectParameters(Resurrection)
                    .Build())
            .AddToDB();

        spell.AddCustomSubFeatures(new CustomBehaviorRescueTheDying(spell));

        return spell;
    }

    internal sealed class CustomBehaviorRescueTheDying : IPowerOrSpellInitiatedByMe, IPowerOrSpellFinishedByMe
    {
        private static SpellDefinition _rescueTheDying;

        internal CustomBehaviorRescueTheDying(SpellDefinition rescueTheDying)
        {
            _rescueTheDying = rescueTheDying;
        }

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            var rulesetTarget = action.ActionParams.TargetCharacters[0].RulesetCharacter;

            rulesetTarget.HealingReceived -= HealingReceivedHandler;

            yield break;
        }

        public IEnumerator OnPowerOrSpellInitiatedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            var rulesetTarget = action.ActionParams.TargetCharacters[0].RulesetCharacter;

            rulesetTarget.HealingReceived += HealingReceivedHandler;

            yield break;
        }

        internal static IEnumerator HandleRescueTheDyingReaction(
            GameLocationBattleManager battleManager,
            GameLocationCharacter waiter,
            GameLocationCharacter defender)
        {
            var locationCharacterService = ServiceRepository.GetService<IGameLocationCharacterService>();
            var contenders = locationCharacterService.PartyCharacters.Union(locationCharacterService.GuestCharacters)
                .Where(x =>
                    x.Side == defender.Side &&
                    x.CanReact() &&
                    x.IsWithinRange(defender, 18) &&
                    x.CanPerceiveTarget(defender) &&
                    x.RulesetCharacter.UsableSpells.Contains(_rescueTheDying) &&
                    x.RulesetCharacter.AreSpellComponentsValid(_rescueTheDying))
                .ToArray();

            foreach (var contender in contenders)
            {
                yield return contender
                    .MyReactToCastSpell(_rescueTheDying, defender, waiter, battleManager: battleManager);
            }
        }

        private static void HealingReceivedHandler(
            RulesetCharacter character,
            int healing,
            ulong sourceGuid,
            HealingCap healingCaps,
            IHealingModificationProvider healingModificationProvider)
        {
            character.ReceiveTemporaryHitPoints(
                healing / 2, DurationType.Round, 1, TurnOccurenceType.EndOfSourceTurn, sourceGuid);
        }
    }

    #endregion

    #region Crown of Stars

    internal static SpellDefinition BuildCrownOfStars()
    {
        const string NAME = "CrownOfStars";

        var sprite = Sprites.GetSprite($"Power{NAME}", Resources.CrownOfStars, 128);
        var powerCrownOfStars = FeatureDefinitionPowerBuilder
            .Create($"Power{NAME}")
            .SetGuiPresentation(Category.Feature, sprite)
            .SetUsesFixed(ActivationTime.BonusAction, RechargeRate.None, 1, 7)
            .SetUseSpellAttack()
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Enemy, RangeType.RangeHit, 24, TargetType.IndividualsUnique)
                    .SetEffectForms(EffectFormBuilder.DamageForm(DamageTypeRadiant, 4, DieType.D12))
                    .SetParticleEffectParameters(ShadowDagger)
                    .SetParticleEffectParameters(GuidingBolt)
                    .SetCasterEffectParameters(PowerPaladinAuraOfCourage)
                    .Build())
            .AddToDB();

        var conditionCrownOfStars = ConditionDefinitionBuilder
            .Create($"Condition{NAME}")
            .SetGuiPresentation($"Power{NAME}", Category.Feature, ConditionGuided)
            .SetPossessive()
            .SetConditionType(ConditionType.Beneficial)
            .SetFeatures(powerCrownOfStars)
            .AddCustomSubFeatures(
                // order matters
                new ConditionAddedOrRemovedCrownOfStars(),
                AddUsablePowersFromCondition.Marker)
            .CopyParticleReferences(DeathWard)
            .AddToDB();

        conditionCrownOfStars.GuiPresentation.description = Gui.NoLocalization;

        var lightSourceForm = Light.EffectDescription
            .GetFirstFormOfType(EffectForm.EffectFormType.LightSource).LightSourceForm;

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, sprite)
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolEvocation)
            .SetSpellLevel(7)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.None)
            .SetVerboseComponent(true)
            .SetSomaticComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Buff)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(Light)
                    .SetDurationData(DurationType.Hour, 1)
                    .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel)
                    .SetEffectForms(
                        EffectFormBuilder.ConditionForm(conditionCrownOfStars),
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .SetLightSourceForm(
                                LightSourceType.Basic, 6, 6, lightSourceForm.Color,
                                lightSourceForm.graphicsPrefabReference)
                            .Build())
                    .SetCasterEffectParameters(Sparkle)
                    .SetEffectEffectParameters(PowerOathOfJugementPurgeCorruption)
                    .Build())
            .AddToDB();

        var customBehavior = new CustomBehaviorCrownOfStars(spell, powerCrownOfStars, conditionCrownOfStars);

        spell.AddCustomSubFeatures(
            customBehavior,
            CustomSpellAdvancementTooltip.FormattedQuantity(
                "Tooltip/&AdvancementGainCrownOfStarsMotesFormat",
                2));

        powerCrownOfStars.AddCustomSubFeatures(
            new ModifyPowerPoolAmount
            {
                PowerPool = powerCrownOfStars,
                Type = PowerPoolBonusCalculationType.ConditionAmount,
                Attribute = conditionCrownOfStars.Name
            },
            customBehavior);

        return spell;
    }

    private static int CrownOfStarsAdditionalMotes(int effectLevel)
    {
        return effectLevel > 7 ? (effectLevel - 7) * 2 : 0;
    }

    private sealed class ConditionAddedOrRemovedCrownOfStars : IOnConditionAddedOrRemoved
    {
        public void OnConditionAdded(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            rulesetCondition.Amount = CrownOfStarsAdditionalMotes(rulesetCondition.EffectLevel);
        }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition rulesetCondition)
        {
            // empty
        }
    }

    private sealed class CustomBehaviorCrownOfStars(
        SpellDefinition spellCrownOfStars,
        FeatureDefinitionPower powerMotes,
        ConditionDefinition conditionCrownOfStars) : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            var rulesetCharacter = action.ActingCharacter.RulesetCharacter;

            if (baseDefinition == spellCrownOfStars)
            {
                SyncMotePoolAfterSpellCast(action, rulesetCharacter);

                yield break;
            }

            if (baseDefinition != powerMotes)
            {
                yield break;
            }

            // must use GetRemainingPowerUses for convenience
            var remainingUses = rulesetCharacter.GetRemainingPowerUses(powerMotes);

            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (remainingUses == 0)
            {
                TerminateCrownOfStars(rulesetCharacter);
            }
            else if (remainingUses < 4)
            {
                DimCrownOfStarsLight(rulesetCharacter);
            }

            yield break;
        }

        private RulesetEffectSpell FindActiveCrownOfStarsEffect(RulesetCharacter rulesetCharacter)
        {
            return rulesetCharacter.SpellsCastByMe
                .FirstOrDefault(x => x.SpellDefinition == spellCrownOfStars);
        }

        private void TerminateCrownOfStars(RulesetCharacter rulesetCharacter)
        {
            var activeSpell = FindActiveCrownOfStarsEffect(rulesetCharacter);

            if (activeSpell != null)
            {
                rulesetCharacter.TerminateSpell(activeSpell);

                return;
            }

            if (rulesetCharacter.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, conditionCrownOfStars.Name, out var activeCondition))
            {
                rulesetCharacter.RemoveCondition(activeCondition);
            }
        }

        private void DimCrownOfStarsLight(RulesetCharacter rulesetCharacter)
        {
            var activeSpell = FindActiveCrownOfStarsEffect(rulesetCharacter);

            if (!HasTrackedPersonalLightSource(rulesetCharacter, activeSpell))
            {
                return;
            }

            rulesetCharacter.PersonalLightSource.brightRange = 0;
        }

        private static bool HasTrackedPersonalLightSource(
            RulesetCharacter rulesetCharacter,
            RulesetEffect activeEffect)
        {
            var personalLightSource = rulesetCharacter.PersonalLightSource;

            return personalLightSource != null &&
                   activeEffect is { TrackedLightSourceGuids.Count: > 0 } &&
                   activeEffect.TrackedLightSourceGuids.Contains(personalLightSource.Guid);
        }

        private void SyncMotePoolAfterSpellCast(CharacterActionMagicEffect action, RulesetCharacter rulesetCharacter)
        {
            if (action.Countered ||
                action.ExecutionFailed ||
                !rulesetCharacter.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, conditionCrownOfStars.Name, out var activeCondition))
            {
                return;
            }

            activeCondition.Amount = CrownOfStarsAdditionalMotes(action.ActionParams.RulesetEffect.EffectLevel);

            var usablePower = GetOrCreateMotePowerPool(rulesetCharacter);

            usablePower.remainingUses = rulesetCharacter.GetMaxUsesOfPower(usablePower);
        }

        private RulesetUsablePower GetOrCreateMotePowerPool(RulesetCharacter rulesetCharacter)
        {
            var usablePower = rulesetCharacter.UsablePowers
                .FirstOrDefault(x => x.PowerDefinition == powerMotes);

            if (usablePower != null)
            {
                return usablePower;
            }

            usablePower = PowerProvider.Get(powerMotes, rulesetCharacter);
            rulesetCharacter.UsablePowers.Add(usablePower);

            return usablePower;
        }
    }

    #endregion
}
