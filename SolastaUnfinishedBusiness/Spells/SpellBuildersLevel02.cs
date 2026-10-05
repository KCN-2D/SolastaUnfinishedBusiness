using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Properties;
using SolastaUnfinishedBusiness.Validators;
using UnityEngine.AddressableAssets;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterFamilyDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ConditionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionActionAffinitys;
using MirrorImage = SolastaUnfinishedBusiness.Behaviors.Specific.MirrorImage;

namespace SolastaUnfinishedBusiness.Spells;

internal static partial class SpellBuilders
{
    #region Aganazzar's Scorcher

    internal static SpellDefinition BuildAganazzarScorcher()
    {
        const string NAME = "AganazzarScorcher";

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.AganazzarScorcher, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolEvocation)
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetVerboseComponent(true)
            .SetSomaticComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Attack)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Line, 6)
                    .ExcludeCaster()
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel, additionalDicePerIncrement: 1)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDamageForm(DamageTypeFire, 3, DieType.D10)
                            .Build())
                    .SetParticleEffectParameters(PowerSessrothBreath)
                    .SetCasterEffectParameters(FlameBlade)
                    .SetImpactEffectParameters(ScorchingRay)
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Battle Familiar

    private static readonly Dictionary<MonsterDefinition, MonsterDefinition> BattleFamiliarOriginals = [];
    private static readonly Dictionary<(MonsterDefinition Original, string Form), MonsterDefinition>
        BattleFamiliarEmpoweredForms = [];
    private static readonly HarmonyLib.AccessTools.FieldRef<RulesetCharacterMonster, MonsterDefinition>
        BattleFamiliarMonsterDefinition = HarmonyLib.AccessTools.FieldRefAccess<RulesetCharacterMonster,
            MonsterDefinition>("monsterDefinition");
    private static MonsterAttackDefinition _battleFamiliarRend;
    private static FeatureDefinitionMoveMode _battleFamiliarLegacySwim;
    private static readonly HashSet<RulesetEffect> BattleFamiliarTerminatingEffects = [];
    private static ConditionDefinition _battleFamiliarProwl;
    private static ConditionDefinition _battleFamiliarRetainedTemporaryHitPoints;

    internal static SpellDefinition BuildBattleFamiliar()
    {
        var sprite = Sprites.GetSprite("BattleFamiliar", Resources.BattleFamiliar, 1254);
        var empoweredSprite = Sprites.GetSprite("ConditionBattleFamiliarEmpowered",
            Resources.ConditionBattleFamiliarEmpowered, 128);
        var summonedSprite = Sprites.GetSprite("ConditionBattleFamiliarSummoned",
            Resources.ConditionBattleFamiliarSummoned, 128);
        _battleFamiliarRend = MonsterAttackDefinitionBuilder
            .Create(MonsterDefinitions.Eagle_Matriarch.AttackIterations[0].MonsterAttackDefinition,
                "AttackBattleFamiliarRend")
            .SetGuiPresentation("Feature/&BattleFamiliarRendTitle", "Feature/&BattleFamiliarRendDescription",
                Sprites.GetSprite("ActionBattleFamiliarRend", Resources.ActionBattleFamiliarRend, 128))
            .SetToHitBonus(0)
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetEffectForms(EffectFormBuilder.DamageForm(DamageTypeForce, 1, DieType.D8, 5))
                .Build())
            .AddToDB();
        _battleFamiliarRend.magical = true;

        _battleFamiliarRetainedTemporaryHitPoints = ConditionDefinitionBuilder
            .Create("ConditionBattleFamiliarRetainedTemporaryHitPoints")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddToDB();

        _battleFamiliarProwl = ConditionDefinitionBuilder.Create("ConditionBattleFamiliarProwl")
            .SetGuiPresentation("Feature/&BattleFamiliarProwlTitle", "Feature/&BattleFamiliarProwlDescription",
                Sprites.GetSprite("ConditionBattleFamiliarProwl", Resources.ConditionBattleFamiliarProwl, 128))
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetFeatures(
                FeatureDefinitionMovementAffinityBuilder.Create("MovementAffinityBattleFamiliarProwl")
                    .SetGuiPresentationNoContent(true)
                    .SetBaseSpeedMultiplicativeModifier(0.5f)
                    .AddToDB(),
                FeatureDefinitionActionAffinityBuilder.Create("ActionAffinityBattleFamiliarProwl")
                    .SetGuiPresentationNoContent(true)
                    .SetAuthorizedActions(ActionDefinitions.Id.HideBonus)
                    .AddToDB())
            .AddToDB();

        var originals = DatabaseRepository.GetDatabase<MonsterDefinition>()
            .Where(monster => monster.Features.Any(feature =>
                feature.GetFirstSubFeatureOfType<FamiliarConnectionBehavior>() != null)).ToArray();
        var normalVision = FeatureDefinitionSenses.SenseNormalVision;
        var walk = FeatureDefinitionMoveModeBuilder.Create("MoveModeBattleFamiliarWalk")
            .SetGuiPresentationNoContent(true).SetMode(MoveMode.Walk, 8).AddToDB();
        // Native swimming also grants airborne placement and paths. Retain the saved
        // definition name as an inert walking mode, without granting unsupported swimming.
        _battleFamiliarLegacySwim = FeatureDefinitionMoveModeBuilder.Create("MoveModeBattleFamiliarSwim")
            .SetGuiPresentationNoContent(true).SetMode(MoveMode.Walk, 0).AddToDB();
        var fly = FeatureDefinitionMoveModeBuilder.Create("MoveModeBattleFamiliarFly")
            .SetGuiPresentationNoContent(true).SetMode(MoveMode.Fly, 6).AddToDB();
        var summons = new List<SpellDefinition>();
        var empowerments = new List<SpellDefinition>();

        var spell = SpellDefinitionBuilder.Create("BattleFamiliar")
            .SetGuiPresentation(Category.Spell, sprite)
            .SetSpellLevel(2)
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolConjuration)
            .SetCastingTime(ActivationTime.Action)
            .SetSpecificMaterialComponent(TagsDefinitions.ItemTagIngredient, 25, false)
            .SetSomaticComponent(true)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Buff)
            .SetUniqueInstance()
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetDurationData(DurationType.Hour, 1)
                .SetTargetingData(Side.Ally, RangeType.Distance, 2, TargetType.Position)
                .SetParticleEffectParameters(ConjureAnimalsOneBeast)
                .Build())
            .AddToDB();

        foreach (var form in new[] { "Brute", "Flyer", "Stalker" })
        {
            var behavior = new CustomBehaviorBattleFamiliar(form);
            var model = form switch
            {
                "Brute" => GetDefinition<MonsterDefinition>("TundraTiger_MonsterDefinition"),
                "Flyer" => MonsterDefinitions.Eagle_Matriarch,
                _ => GetDefinition<MonsterDefinition>("BadlandsSpider")
            };
            var modelScale = form switch
            {
                "Brute" => 0.4f,
                "Flyer" => 0.7f,
                _ => 0.2f
            };
            FeatureDefinition[] movement = form == "Flyer" ? [normalVision, walk, fly] :
                [normalVision, walk];
            behavior.BuildConditions(empoweredSprite, summonedSprite);

            foreach (var original in originals)
            {
                // Preserve the original body, HP and spell-granted abilities, replacing its intrinsic
                // movement and senses with the combat form without changing a shared definition.
                var empowered = MonsterDefinitionBuilder.Create(original, $"BattleFamiliar{form}{original.Name}")
                    .SetFeatures(movement.Concat(original.Features.Where(FamiliarSpellFeatures.Contains))
                        .ToArray())
                    .SetSkillScores()
                    .SetSavingThrowScores()
                    .SetAttackIterations((1, _battleFamiliarRend))
                    .AddToDB();
                BattleFamiliarOriginals[empowered] = original;
                BattleFamiliarEmpoweredForms[(original, form)] = empowered;
            }

            foreach (var family in new[] { "Celestial", "Fey", "Fiend" })
            {
                var presentation = new MonsterPresentation(model.MonsterPresentation)
                {
                    hasPhantomDistortion = true,
                    hasPhantomFadingFeet = true,
                    hasPrefabVariants = false,
                    maleModelScale = modelScale,
                    femaleModelScale = modelScale,
                    hasMonsterPortraitBackground = true,
                    canGeneratePortrait = true
                };
                var monster = MonsterDefinitionBuilder
                    .Create(model, $"MonsterBattleFamiliar{form}{family}")
                    .SetGuiPresentation($"Monster/&BattleFamiliar{form}Title",
                        $"Spell/&BattleFamiliar{form}Description", model.GuiPresentation.SpriteReference)
                    .SetMonsterPresentation(presentation)
                    .SetAbilityScores(16, 16, 12, 8, 13, 10)
                    .SetArmorClass(form == "Brute" ? 15 : 13)
                    .SetHitDice(DieType.D8, 4)
                    .SetStandardHitPoints(GetBattleFamiliarHitPoints(form))
                    .SetSkillScores()
                    .SetSavingThrowScores()
                    .SetFeatures(movement)
                    .SetAttackIterations((1, _battleFamiliarRend))
                    .SetSizeDefinition(CharacterSizeDefinitions.Medium)
                    .SetAlignment("Neutral")
                    .SetCharacterFamily(family)
                    .SetChallengeRating(0)
                    .SetDroppedLootDefinition(null)
                    .NoExperienceGain()
                    .SetFullyControlledWhenAllied(true)
                    .SetDefaultFaction(FactionDefinitions.Party)
                    .SetBestiaryEntry(BestiaryDefinitions.BestiaryEntry.None)
                    .SetDungeonMakerPresence(MonsterDefinition.DungeonMaker.None)
                    .AddToDB();
                var variant = SpellDefinitionBuilder.Create(spell, $"BattleFamiliar{form}{family}")
                    .SetGuiPresentation($"Spell/&BattleFamiliar{form}{family}Title",
                        $"Spell/&BattleFamiliar{form}Description", sprite)
                    .SetEffectDescription(EffectDescriptionBuilder.Create(spell.EffectDescription)
                        .SetEffectForms(EffectFormBuilder.Create().SetSummonCreatureForm(1, monster.Name).Build())
                        .Build())
                    .AddCustomSubFeatures(behavior)
                    .AddToDB();
                summons.Add(variant);
            }

            var empowerment = SpellDefinitionBuilder.Create(spell, $"BattleFamiliarEmpower{form}")
                .SetGuiPresentation($"Spell/&BattleFamiliarEmpower{form}Title",
                    $"Spell/&BattleFamiliarEmpower{form}Description", sprite)
                .SetEffectDescription(EffectDescriptionBuilder.Create(spell.EffectDescription)
                    .SetTargetingData(Side.Ally, RangeType.Distance, 2, TargetType.Individuals, 1)
                    .SetEffectForms(EffectFormBuilder.Create()
                        .SetSummonCreatureForm(0, $"MonsterBattleFamiliar{form}Fey").Build())
                    .Build())
                .AddCustomSubFeatures(behavior.ForEmpowerment(), new FilterTargetingBattleFamiliar(),
                    SkipEffectRemovalOnLocationChange.Always)
                .AddToDB();
            empowerment.EffectDescription.specialFormsDescription = empowerment.GuiPresentation.Description;
            empowerments.Add(empowerment);
        }

        var summonSelection = SpellDefinitionBuilder.Create(spell, "BattleFamiliarSummon")
            .SetGuiPresentation("Spell/&BattleFamiliarSummonTitle", "Spell/&BattleFamiliarSummonDescription", sprite)
            .SetEffectDescription(EffectDescriptionBuilder.Create(summons[1].EffectDescription).Build())
            .SetSubSpells(summons.ToArray())
            .AddToDB();
        summonSelection.EffectDescription.specialFormsDescription = summonSelection.GuiPresentation.Description;

        var empowerSelection = SpellDefinitionBuilder.Create(spell, "BattleFamiliarEmpower")
            .SetGuiPresentation("Spell/&BattleFamiliarEmpowerTitle", "Spell/&BattleFamiliarEmpowerDescription", sprite)
            .SetEffectDescription(EffectDescriptionBuilder.Create(empowerments[0].EffectDescription).Build())
            .SetSubSpells(empowerments.ToArray())
            .AddToDB();
        empowerSelection.EffectDescription.specialFormsDescription = empowerSelection.GuiPresentation.Description;

        spell.spellsBundle = true;
        spell.SubspellsList.SetRange([summonSelection, empowerSelection]);
        spell.EffectDescription.EffectForms.SetRange(summons[1].EffectDescription.EffectForms);
        spell.EffectDescription.specialFormsDescription = spell.GuiPresentation.Description;
        spell.AddCustomSubFeatures(summons[1].GetFirstSubFeatureOfType<CustomBehaviorBattleFamiliar>());
        ForceGlobalUniqueEffects.AddToGroup(ForceGlobalUniqueEffects.Group.Familiar,
            new BaseDefinition[] { spell, summonSelection, empowerSelection }
                .Concat(summons).Concat(empowerments).ToArray());
        return spell;
    }

    private static int GetBattleFamiliarHitPoints(string form)
    {
        return form == "Brute" ? 30 : 20;
    }

    private static RulesetCondition GetBattleFamiliarCondition(RulesetCharacter character)
    {
        return character.ConditionsByCategory.Values.SelectMany(conditions => conditions)
            .FirstOrDefault(condition => condition.ConditionDefinition
                .GetFirstSubFeatureOfType<CustomBehaviorBattleFamiliar>() != null);
    }

    private static void SetBattleFamiliarDefinition(RulesetCharacterMonster character, MonsterDefinition definition)
    {
        if (!definition.MonsterPresentation.HasPrefabVariants)
        {
            // A saved source-model variant otherwise overrides the current body's scale.
            FamiliarMonsterPresentation(character) = null;
        }

        var previous = character.MonsterDefinition;
        var intrinsicFeatures = previous.Features.Append(_battleFamiliarLegacySwim);
        if (BattleFamiliarOriginals.TryGetValue(definition, out var original))
        {
            // Old saves can already name the empowered definition while retaining the original
            // active feature list. Reconcile those intrinsic features without refilling resources.
            intrinsicFeatures = intrinsicFeatures.Concat(original.Features);
        }
        var obsoleteFeatures = intrinsicFeatures.Where(feature => !definition.Features.Contains(feature)).ToArray();
        var skills = definition.SkillScores.ToDictionary(score => score.SkillName, score => score.Bonus);
        var saves = definition.SavingThrowScores.ToDictionary(score => score.AbilityScoreName, score => score.Bonus);
        if (previous != definition || character.ActiveFeatures.Any(obsoleteFeatures.Contains) ||
            definition.Features.Any(feature => !character.ActiveFeatures.Contains(feature)) ||
            NeedsSynchronization(character.SkillProficiencies, skills) ||
            NeedsSynchronization(character.SavingThrowProficiencies, saves))
        {
            var hitPoints = character.CurrentHitPoints;
            BattleFamiliarMonsterDefinition(character) = definition;
            character.ActiveFeatures.RemoveAll(obsoleteFeatures.Contains);
            foreach (var feature in definition.Features)
            {
                if (!character.ActiveFeatures.Contains(feature))
                {
                    character.ActiveFeatures.Add(feature);
                }
            }
            character.SkillProficiencies.Clear();
            foreach (var skill in skills)
            {
                character.SkillProficiencies[skill.Key] = skill.Value;
            }
            character.SavingThrowProficiencies.Clear();
            foreach (var save in saves)
            {
                character.SavingThrowProficiencies[save.Key] = save.Value;
            }
            if (previous != definition)
            {
                character.remainingAttackUses.Clear();
                character.InitializeAttackUses();
            }
            character.RefreshAll();
            character.CurrentHitPoints = System.Math.Min(hitPoints,
                character.TryGetAttributeValue(AttributeDefinitions.HitPoints));
        }
        else
        {
            CombatAnimationContext.RefreshMovementState(character);
        }

        static bool NeedsSynchronization(Dictionary<string, int> current, Dictionary<string, int> expected)
        {
            return current.Count != expected.Count ||
                   expected.Any(pair => !current.TryGetValue(pair.Key, out var value) || value != pair.Value);
        }
    }

    private sealed class BattleFamiliarInvocationContext(
        RulesetEffectSpell effect, RulesetCharacterMonster familiar) : ICustomSummonInvocationContext
    {
        internal readonly RulesetEffectSpell Effect = effect;
        internal readonly RulesetCharacterMonster Familiar = familiar;
        internal RulesetCharacterMonster Summoned;
    }

    private sealed class FilterTargetingBattleFamiliar : IFilterTargetingCharacter
    {
        public bool EnforceFullSelection => true;

        public bool IsValid(CursorLocationSelectTarget cursor, GameLocationCharacter target)
        {
            var character = target.RulesetCharacter;
            var isValid = character is RulesetCharacterMonster && IsFamiliar(character) &&
                          character.CurrentHitPoints > 0 &&
                          EffectHelpers.GetSummoner(character) == cursor.ActionParams.ActingCharacter.RulesetCharacter;
            if (!isValid)
            {
                cursor.actionModifier.FailureFlags.Add("Failure/&BattleFamiliarRequiresOwnFamiliar");
            }
            return isValid;
        }
    }

    private sealed class CustomBehaviorBattleFamiliar(string form, bool empowerExisting = false) :
        ICustomSummonFormHandler, IUniqueEffectTerminationFilter, IModifyWeaponAttackMode,
        IOnConditionAddedOrRemoved, IOnLocationCharacterRestored, IEffectCharacterChange,
        IOnBeforeEffectTerminated, IActionFinishedByMe, ICharacterTurnStartListener, IIgnoreAoOOnMe
    {
        private ConditionDefinition _condition;
        private ConditionDefinition _summonedCondition;
        public int Priority => 0;

        internal CustomBehaviorBattleFamiliar ForEmpowerment()
        {
            return new CustomBehaviorBattleFamiliar(form, true) { _condition = _condition };
        }

        internal void BuildConditions(AssetReferenceSprite empoweredSprite, AssetReferenceSprite summonedSprite)
        {
            var abilities = new[] { AttributeDefinitions.Strength, AttributeDefinitions.Dexterity,
                AttributeDefinitions.Constitution, AttributeDefinitions.Intelligence,
                AttributeDefinitions.Wisdom, AttributeDefinitions.Charisma };
            var scores = new[] { 16, 16, 12, 8, 13, 10 };
            var features = abilities.Select((ability, index) => (FeatureDefinition)FeatureDefinitionAttributeModifierBuilder
                .Create($"AttributeModifierBattleFamiliar{form}{ability}")
                .SetGuiPresentationNoContent(true)
                .SetModifier(FeatureDefinitionAttributeModifier.AttributeModifierOperation.ForceAnyway,
                    ability, scores[index]).AddToDB()).ToList();
            var name = $"BattleFamiliar{form}";
            features.AddRange(new FeatureDefinition[]
            {
                FeatureDefinitionAttributeModifierBuilder.Create($"AttributeModifier{name}ArmorClass")
                    .SetGuiPresentationNoContent(true)
                    .SetModifier(FeatureDefinitionAttributeModifier.AttributeModifierOperation.Set,
                        AttributeDefinitions.ArmorClass, form == "Brute" ? 15 : 13).AddToDB(),
                FeatureDefinitionAbilityCheckAffinityBuilder.Create($"AbilityCheckAffinity{name}Talented")
                    .SetGuiPresentationNoContent(true)
                    .BuildAndSetAffinityGroups(CharacterAbilityCheckAffinity.None, DieType.D1, 1,
                        AbilityCheckGroupOperation.AddDie,
                        abilities.Select(ability => (ability, string.Empty)).ToArray()).AddToDB(),
                FeatureDefinitionSavingThrowAffinityBuilder.Create($"SavingThrowAffinity{name}Talented")
                    .SetGuiPresentationNoContent(true)
                    .SetModifiers(FeatureDefinitionSavingThrowAffinity.ModifierType.AddDice,
                        DieType.D1, 1, false, abilities).AddToDB(),
                GetDefinition<FeatureDefinitionConditionAffinity>("ConditionAffinityCharmImmunity"),
                FeatureDefinitionConditionAffinitys.ConditionAffinityFrightenedImmunity
            });
            if (form == "Flyer")
            {
                features.Add(FeatureDefinitionConditionAffinitys.ConditionAffinityProneImmunity);
            }
            _condition = ConditionDefinitionBuilder.Create($"Condition{name}")
                .SetGuiPresentation("Condition/&BattleFamiliarEmpoweredTitle",
                    "Condition/&BattleFamiliarEmpoweredDescription", empoweredSprite)
                .SetConditionType(ConditionType.Beneficial)
                .SetAmountOrigin(ConditionDefinition.OriginOfAmount.None)
                .SetPossessive()
                .SetFeatures(features.ToArray())
                .AddCustomSubFeatures(this)
                .AddToDB();
            _condition.terminateWhenRemoved = true;
            _summonedCondition = ConditionDefinitionBuilder.Create($"Condition{name}Summoned")
                .SetGuiPresentation($"Monster/&BattleFamiliar{form}Title",
                    "Condition/&BattleFamiliarSummonedDescription", summonedSprite)
                .SetConditionType(ConditionType.Beneficial)
                .SetPossessive()
                .SetFeatures(features.Where(feature => feature is not FeatureDefinitionAttributeModifier).ToArray())
                .AddCustomSubFeatures(this)
                .AddToDB();
            _summonedCondition.terminateWhenRemoved = true;
        }
        public bool ShouldTerminateExistingEffect(
            RulesetCharacter character, RulesetEffect incoming, RulesetEffect existing)
        {
            // Only the explicitly selected empowerment preserves its permanent familiar's effect.
            return !empowerExisting || !EffectHelpers.GetSummonedCreatures(existing).Any(IsFamiliar);
        }

        public bool TryPrepare(EffectForm effectForm,
            ref RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            out ICustomSummonInvocationContext invocationContext, out string failureFeedback)
        {
            failureFeedback = null;
            var effect = formsParams.activeEffect as RulesetEffectSpell;
            if (effect == null)
            {
                invocationContext = null;
                return false;
            }
            var familiar = empowerExisting ? formsParams.targetCharacter as RulesetCharacterMonster : null;
            if (empowerExisting && (familiar == null || !IsFamiliar(familiar) ||
                                    familiar.CurrentHitPoints <= 0 ||
                                    EffectHelpers.GetSummoner(familiar)?.Guid != effect.SourceGuid))
            {
                invocationContext = null;
                failureFeedback = "Failure/&BattleFamiliarRequiresOwnFamiliar";
                return false;
            }
            invocationContext = new BattleFamiliarInvocationContext(effect, familiar);
            if (familiar != null)
            {
                effectForm.SummonForm.number = 0;
            }
            return true;
        }

        public string GetMonsterDefinitionName(EffectForm effectForm,
            RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            ICustomSummonInvocationContext invocationContext)
        {
            return effectForm.SummonForm.MonsterDefinitionName;
        }

        public void InitializeSummonedCharacter(RulesetCharacterMonster character,
            ICustomSummonInvocationContext invocationContext)
        {
            if (invocationContext is BattleFamiliarInvocationContext context)
            {
                context.Summoned = character;
            }
        }

        public void AfterApply(EffectForm effectForm, RulesetImplementationDefinitions.ApplyFormsParams formsParams,
            ICustomSummonInvocationContext invocationContext)
        {
            if (invocationContext is not BattleFamiliarInvocationContext context ||
                (context.Familiar ?? context.Summoned) is not { } target)
            {
                return;
            }
            var effect = context.Effect;
            var source = EffectHelpers.GetCharacterByGuid(effect.SourceGuid);
            var conditionDefinition = context.Familiar != null ? _condition : _summonedCondition;
            var condition = target.InflictCondition(conditionDefinition.Name, DurationType.Hour, 1,
                TurnOccurenceType.EndOfTurn, AttributeDefinitions.TagEffect, source.Guid,
                source.CurrentFaction.Name, 2, effect.SourceDefinition.Name,
                0, 0, 0);
            condition.Amount = effect.MagicAttackBonus;
            if (context.Familiar != null)
            {
                target.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect,
                    ConditionTemporaryHitPoints, out var previousTemporaryHitPoints);
                var previousTemporaryHitPointsGuid = previousTemporaryHitPoints?.Guid ?? 0;
                target.ReceiveTemporaryHitPoints(GetBattleFamiliarHitPoints(form),
                    DurationType.Hour, 1, TurnOccurenceType.EndOfTurn, source.Guid);
                if (target.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect,
                        ConditionTemporaryHitPoints, out var temporaryHitPoints))
                {
                    if (temporaryHitPoints.Guid == previousTemporaryHitPointsGuid)
                    {
                        // Native temporary HP keeps the larger pool. Persist ownership so ending this
                        // empowerment cannot delete temporary HP granted by an unrelated effect.
                        var retained = target.InflictCondition(_battleFamiliarRetainedTemporaryHitPoints.Name,
                            DurationType.Permanent, 0, TurnOccurenceType.EndOfTurn, AttributeDefinitions.TagEffect,
                            source.Guid, source.CurrentFaction.Name, 2, effect.SourceDefinition.Name, 0, 0, 0);
                        effect.TrackCondition(source, source.Guid, target, target.Guid,
                            retained, AttributeDefinitions.TagEffect);
                    }
                    else
                    {
                        effect.TrackCondition(source, source.Guid, target, target.Guid,
                            temporaryHitPoints, AttributeDefinitions.TagEffect);
                    }
                }
            }
            effect.TrackCondition(source, source.Guid, target, target.Guid, condition, AttributeDefinitions.TagEffect);
            target.RefreshAll();
        }

        public void OnConditionAdded(RulesetCharacter target, RulesetCondition condition)
        {
            if (target is not RulesetCharacterMonster familiar)
            {
                return;
            }
            if (IsFamiliar(familiar))
            {
                var original = BattleFamiliarOriginals.TryGetValue(familiar.MonsterDefinition, out var restored)
                    ? restored : familiar.MonsterDefinition;
                if (BattleFamiliarEmpoweredForms.TryGetValue((original, form), out var definition))
                {
                    SetBattleFamiliarDefinition(familiar, definition);
                }
            }
            else
            {
                SetBattleFamiliarDefinition(familiar, familiar.MonsterDefinition);
            }
        }

        public void OnConditionRemoved(RulesetCharacter target, RulesetCondition condition)
        {
            if (target is RulesetCharacterMonster familiar &&
                BattleFamiliarOriginals.TryGetValue(familiar.MonsterDefinition, out var original))
            {
                SetBattleFamiliarDefinition(familiar, original);
            }
            if (target.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect,
                    _battleFamiliarProwl.Name, out var prowl))
            {
                target.RemoveCondition(prowl);
            }
        }

        public void ModifyWeaponAttackMode(RulesetCharacter character, RulesetAttackMode attackMode,
            RulesetItem weapon, bool canAddAbilityDamageBonus)
        {
            var condition = GetBattleFamiliarCondition(character);
            if (condition == null || attackMode.SourceDefinition != _battleFamiliarRend)
            {
                return;
            }
            attackMode.AttacksNumber = 1;
            attackMode.ToHitBonus = condition.Amount;
            attackMode.EffectDescription.FindFirstDamageForm().bonusDamage = 5;
        }

        public bool CanIgnoreAoOOnSelf(RulesetCharacter defender, RulesetCharacter attacker)
        {
            return form == "Flyer" &&
                   GameLocationCharacter.GetFromActor(defender)?.CurrentMoveMode == MoveMode.Fly;
        }

        public IEnumerator OnActionFinishedByMe(CharacterAction action)
        {
            if (form == "Stalker" && action.ActionId == ActionDefinitions.Id.DisengageMain)
            {
                var character = action.ActingCharacter.RulesetCharacter;
                character.InflictCondition(_battleFamiliarProwl.Name, DurationType.Round, 0,
                    TurnOccurenceType.EndOfTurn, AttributeDefinitions.TagEffect, character.Guid,
                    character.CurrentFaction.Name, 1, _battleFamiliarProwl.Name, 0, 0, 0);
            }
            yield break;
        }

        public void OnCharacterChanged(RulesetEffect effect, RulesetCharacter character)
        {
            Validate(effect);
        }

        public void OnCharacterTurnStarted(GameLocationCharacter locationCharacter)
        {
            OnLocationCharacterRestored(locationCharacter.RulesetCharacter);
        }

        public void OnLocationCharacterRestored(RulesetCharacter character)
        {
            var condition = GetBattleFamiliarCondition(character);
            if (condition != null)
            {
                OnConditionAdded(character, condition);
            }
            foreach (var effect in EffectCharacterChange.EnumerateEffectsInvolving(character)
                         .Where(effect => effect.GetSourceDefinitionSafe()
                             .GetFirstSubFeatureOfType<CustomBehaviorBattleFamiliar>() != null).ToArray())
            {
                Validate(effect);
            }
        }

        private static void Validate(RulesetEffect effect)
        {
            if (effect.Terminated || BattleFamiliarTerminatingEffects.Contains(effect) ||
                ServiceRepository.GetService<IGameSerializationService>()?.Loading == true)
            {
                return;
            }
            var caster = EffectHelpers.GetCharacterByGuid(effect.SourceGuid);
            if (caster?.IsDead == true)
            {
                effect.DoTerminate(caster);
                return;
            }
            foreach (var guid in effect.TrackedConditionGuids.ToArray())
            {
                if (!RulesetEntity.TryGetEntity<RulesetCondition>(guid, out var condition) ||
                    condition.ConditionDefinition.GetFirstSubFeatureOfType<CustomBehaviorBattleFamiliar>() == null ||
                    EffectHelpers.GetCharacterByGuid(condition.TargetGuid) is not { } target)
                {
                    continue;
                }
                if (target.CurrentHitPoints <= 0 || target is RulesetCharacterMonster familiar &&
                    BattleFamiliarOriginals.ContainsKey(familiar.MonsterDefinition) &&
                    (target.TemporaryHitPoints <= 0 ||
                     !target.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect,
                         ConditionTemporaryHitPoints, out var temporaryHitPoints) ||
                     !effect.TrackedConditionGuids.Contains(temporaryHitPoints.Guid) &&
                     (!target.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect,
                          _battleFamiliarRetainedTemporaryHitPoints.Name, out var retained) ||
                      !effect.TrackedConditionGuids.Contains(retained.Guid))))
                {
                    effect.DoTerminate(caster);
                    return;
                }
            }
        }

        public void OnBeforeEffectTerminated(RulesetEffect effect)
        {
            if (!BattleFamiliarTerminatingEffects.Add(effect))
            {
                return;
            }
            try
            {
                foreach (var guid in effect.TrackedConditionGuids.ToArray())
                {
                    if (!RulesetEntity.TryGetEntity<RulesetCondition>(guid, out var condition) ||
                        condition.ConditionDefinition.GetFirstSubFeatureOfType<CustomBehaviorBattleFamiliar>() == null ||
                        EffectHelpers.GetCharacterByGuid(condition.TargetGuid) is not { } target ||
                        !target.TryGetConditionOfCategoryAndType(AttributeDefinitions.TagEffect,
                            ConditionTemporaryHitPoints, out var temporaryHitPoints) ||
                        !effect.TrackedConditionGuids.Contains(temporaryHitPoints.Guid))
                    {
                        continue;
                    }
                    target.RemoveCondition(temporaryHitPoints);
                    target.TemporaryHitPoints = 0;
                }
            }
            finally
            {
                BattleFamiliarTerminatingEffects.Remove(effect);
            }
        }
    }

    #endregion
    #region Binding Ice

    internal static SpellDefinition BuildBindingIce()
    {
        const string NAME = "BindingIce";

        var spriteReference = Sprites.GetSprite(NAME, Resources.BindingIce, 128);
        var battlePackage = AiHelpers.BuildDecisionPackageBreakFree(
            "ConditionGrappledRestrainedIceBound", AiHelpers.RandomType.RandomMedium);

        var conditionGrappledRestrainedIceBound = ConditionDefinitionBuilder
            .Create("ConditionGrappledRestrainedIceBound")
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionRestrained)
            .SetConditionType(ConditionType.Detrimental)
            .SetParentCondition(ConditionDefinitions.ConditionRestrained)
            .SetFixedAmount((int)AiHelpers.BreakFreeType.DoNoCheckAndRemoveCondition)
            .SetBrain(battlePackage, true)
            .SetFeatures(ActionAffinityGrappled)
            .AddToDB();

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, spriteReference)
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolEvocation)
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetVerboseComponent(false)
            .SetSomaticComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Attack)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Cone, 6)
                    .ExcludeCaster()
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel, additionalDicePerIncrement: 1)
                    .SetSavingThrowData(
                        false,
                        AttributeDefinitions.Constitution,
                        true,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetDamageForm(DamageTypeCold, 3, DieType.D8)
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .Build(),
                        EffectFormBuilder
                            .Create()
                            .SetConditionForm(conditionGrappledRestrainedIceBound, ConditionForm.ConditionOperation.Add)
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .Build())
                    .SetParticleEffectParameters(ConeOfCold)
                    .SetConditionEffectParameters(PowerDomainElementalHeraldOfTheElementsCold)
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Cloud of Daggers

    internal static SpellDefinition BuildCloudOfDaggers()
    {
        const string Name = "CloudOfDaggers";

        var spell = SpellDefinitionBuilder
            .Create(Name)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(Name, Resources.CloudOfDaggers, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolConjuration)
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetVerboseComponent(true)
            .SetSomaticComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Debuff)
            .SetRequiresConcentration(true)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.All, RangeType.Distance, 12, TargetType.Cube, 2)
                    .SetEffectAdvancement(
                        EffectIncrementMethod.PerAdditionalSlotLevel, additionalDicePerIncrement: 2)
                    .SetRecurrentEffect(RecurrentEffect.OnTurnStart | RecurrentEffect.OnEnter)
                    .SetEffectForms(
                        EffectFormBuilder.DamageForm(DamageTypeSlashing, 4, DieType.D4),
                        EffectFormBuilder
                            .Create()
                            .SetTopologyForm(TopologyForm.Type.DangerousZone, true)
                            .Build())
                    .SetParticleEffectParameters(BladeBarrierWallLine)
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Color Burst

    internal static SpellDefinition BuildColorBurst()
    {
        const string NAME = "ColorBurst";

        var spell = SpellDefinitionBuilder
            .Create(ColorSpray, NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.ColorBurst, 128))
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetVerboseComponent(true)
            .SetSomaticComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Attack)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(ColorSpray)
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Cube, 5)
                    .ExcludeCaster()
                    .SetParticleEffectParameters(HypnoticPattern)
                    .Build())
            .AddToDB();

        spell.EffectDescription.EffectParticleParameters.impactParticleReference =
            spell.EffectDescription.EffectParticleParameters.zoneParticleReference;
        spell.EffectDescription.EffectParticleParameters.zoneParticleReference = new AssetReference();

        return spell;
    }

    #endregion

    #region Kinetic Jaunt

    internal static SpellDefinition BuildKineticJaunt()
    {
        const string NAME = "KineticJaunt";

        var condition = ConditionDefinitionBuilder
            .Create($"Condition{NAME}")
            .SetGuiPresentation(NAME, Category.Spell, ConditionFreedomOfMovement)
            .SetPossessive()
            .SetFeatures(
                FeatureDefinitionMovementAffinityBuilder
                    .Create($"MovementAffinity{NAME}")
                    .SetGuiPresentationNoContent(true)
                    .SetBaseSpeedAdditiveModifier(2)
                    .AddToDB(),
                FeatureDefinitionMoveThroughEnemyModifierBuilder
                    .Create($"MoveThroughEnemyModifier{NAME}")
                    .SetGuiPresentation(Category.Feature)
                    .SetMinSizeDifference(0)
                    .AddToDB(),
                FeatureDefinitionCombatAffinitys.CombatAffinityDisengaging)
            .SetConditionParticleReference(ConditionMonkSlowFall)
            .AddToDB();

        condition.GuiPresentation.description = Gui.EmptyContent;

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.KineticJaunt, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolTransmutation)
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.BonusAction)
            .SetMaterialComponent(MaterialComponentType.None)
            .SetVerboseComponent(false)
            .SetSomaticComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Attack)
            .SetRequiresConcentration(true)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Self)
                    .SetEffectForms(EffectFormBuilder.ConditionForm(condition))
                    .SetParticleEffectParameters(Haste)
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Noxious Spray

    internal static SpellDefinition BuildNoxiousSpray()
    {
        const string NAME = "NoxiousSpray";

        var actionAffinityNoxiousSpray = FeatureDefinitionActionAffinityBuilder
            .Create($"ActionAffinity{NAME}")
            .SetGuiPresentationNoContent(true)
            .SetAllowedActionTypes(false, move: false)
            .AddToDB();

        var battlePackage =
            AiHelpers.BuildDecisionPackageBreakFree($"Condition{NAME}", AiHelpers.RandomType.NoRandom);

        var conditionNoxiousSpray = ConditionDefinitionBuilder
            .Create(ConditionPheromoned, $"Condition{NAME}")
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionDiseased)
            .SetConditionType(ConditionType.Detrimental)
            .SetPossessive()
            .SetFixedAmount((int)AiHelpers.BreakFreeType.DoNoCheckAndRemoveCondition)
            .SetBrain(battlePackage, true)
            .SetFeatures(actionAffinityNoxiousSpray, ActionAffinityGrappled)
            .AddToDB();

        conditionNoxiousSpray.specialDuration = false;

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.NoxiousSpray, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolEvocation)
            .SetSpellLevel(2)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetSomaticComponent(true)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Attack)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1)
                    .SetTargetingData(Side.Enemy, RangeType.RangeHit, 12, TargetType.IndividualsUnique)
                    .SetSavingThrowData(false, AttributeDefinitions.Constitution, false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel,
                        additionalTargetsPerIncrement: 1)
                    .AddImmuneCreatureFamilies(Construct, Elemental, Undead)
                    .SetEffectForms(
                        EffectFormBuilder.DamageForm(DamageTypePoison, 4, DieType.D6),
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .SetConditionForm(conditionNoxiousSpray, ConditionForm.ConditionOperation.Add)
                            .Build())
                    .SetParticleEffectParameters(PowerDomainOblivionMarkOfFate)
                    .SetCasterEffectParameters(PoisonSpray)
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Protect Threshold

    [NotNull]
    internal static SpellDefinition BuildProtectThreshold()
    {
        const string NAME = "ProtectThreshold";

        var proxyProtectThreshold = EffectProxyDefinitionBuilder
            .Create(EffectProxyDefinitions.ProxyGuardianOfFaith, $"Proxy{NAME}")
            .SetOrUpdateGuiPresentation(NAME, Category.Spell)
            .AddToDB();

        var spell = SpellDefinitionBuilder
            .Create(GuardianOfFaith, NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.ProtectThreshold, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolAbjuration)
            .SetSpellLevel(2)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetSomaticComponent(true)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Debuff)
            .SetRequiresConcentration(false)
            .SetRitualCasting(ActivationTime.Minute10)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(SpikeGrowth.EffectDescription)
                    .SetTargetingData(Side.All, RangeType.Distance, 6, TargetType.Sphere, 3)
                    .SetDurationData(DurationType.Minute, 10)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel, additionalDicePerIncrement: 1)
                    .SetRecurrentEffect(RecurrentEffect.OnEnter)
                    .SetSavingThrowData(
                        false,
                        AttributeDefinitions.Wisdom,
                        false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetSummonEffectProxyForm(proxyProtectThreshold)
                            .Build(),
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDamageForm(DamageTypePsychic, 4, DieType.D6)
                            .Build(),
                        EffectFormBuilder.TopologyForm(TopologyForm.Type.DangerousZone, true),
                        EffectFormBuilder.TopologyForm(TopologyForm.Type.DifficultThrough, true))
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Snilloc's Snowball Storm

    internal static SpellDefinition BuildSnillocSnowballStorm()
    {
        const string NAME = "SnillocSnowballStorm";

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.SnillocSnowballStorm, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolEvocation)
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetVerboseComponent(true)
            .SetSomaticComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Attack)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.All, RangeType.Distance, 18, TargetType.Cube, 3)
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel, additionalDicePerIncrement: 1)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDamageForm(DamageTypeCold, 3, DieType.D8)
                            .Build())
                    .SetParticleEffectParameters(FreezingSphere)
                    .SetCasterEffectParameters(SleetStorm)
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Web

    internal static SpellDefinition BuildWeb()
    {
        const string NAME = "SpellWeb";

        var battlePackage = AiHelpers.BuildDecisionPackageBreakFree($"ConditionGrappledRestrained{NAME}");

        var conditionRestrainedBySpellWeb = ConditionDefinitionBuilder
            .Create($"ConditionGrappledRestrained{NAME}")
            .SetGuiPresentation(Category.Condition, ConditionDefinitions.ConditionRestrained)
            .SetConditionType(ConditionType.Detrimental)
            .SetParentCondition(ConditionDefinitions.ConditionRestrained)
            .SetFixedAmount((int)AiHelpers.BreakFreeType.DoStrengthCheckAgainstCasterDC)
            .SetBrain(battlePackage, true)
            .SetFeatures(ActionAffinityGrappled)
            .AddFeatures([.. LightingAndObscurementContext.ConditionLightlyObscured.Features])
            .AddToDB();

        var conditionAffinityGrappledRestrainedSpellWebImmunity = FeatureDefinitionConditionAffinityBuilder
            .Create($"ConditionAffinityGrappledRestrained{NAME}Immunity")
            .SetGuiPresentationNoContent(true)
            .SetConditionType(conditionRestrainedBySpellWeb)
            .SetConditionAffinityType(ConditionAffinityType.Immunity)
            .AddToDB();

        foreach (var monsterDefinition in DatabaseRepository.GetDatabase<MonsterDefinition>()
                     .Where(x => x.Name.Contains("Spider") || x.Name.Contains("spider")))
        {
            monsterDefinition.Features.Add(conditionAffinityGrappledRestrainedSpellWebImmunity);
        }

        ItemDefinitions.CloakOfArachnida.StaticProperties.Add(ItemPropertyDescriptionBuilder
            .From(conditionAffinityGrappledRestrainedSpellWebImmunity, false,
                EquipmentDefinitions.KnowledgeAffinity.InactiveAndHidden).Build());

        var proxyWeb = EffectProxyDefinitionBuilder
            .Create(EffectProxyDefinitions.ProxyEntangle, $"Proxy{NAME}")
            .SetOrUpdateGuiPresentation(NAME, Category.Spell)
            .AddToDB();

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.Web, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolConjuration)
            .SetSpellLevel(2)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetSomaticComponent(true)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Debuff)
            .SetRequiresConcentration(true)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(Grease)
                    .SetDurationData(DurationType.Hour, 1)
                    .SetTargetingData(Side.All, RangeType.Distance, 12, TargetType.Cube, 4, 1)
                    .SetRecurrentEffect(RecurrentEffect.OnTurnStart | RecurrentEffect.OnEnter)
                    .SetSavingThrowData(
                        false,
                        AttributeDefinitions.Dexterity,
                        false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetConditionForm(conditionRestrainedBySpellWeb, ConditionForm.ConditionOperation.Add)
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .Build(),
                        EffectFormBuilder
                            .Create()
                            .SetSummonEffectProxyForm(proxyWeb)
                            .Build(),
                        EffectFormBuilder.TopologyForm(TopologyForm.Type.DangerousZone, false),
                        EffectFormBuilder.TopologyForm(TopologyForm.Type.DifficultThrough, false))
                    .SetConditionEffectParameters(Entangle)
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Psychic Whip

    internal static SpellDefinition BuildPsychicWhip()
    {
        const string NAME = "PsychicWhip";

        var actionAffinityPsychicWhipNoReaction = FeatureDefinitionActionAffinityBuilder
            .Create($"ActionAffinity{NAME}NoReaction")
            .SetGuiPresentationNoContent(true)
            .SetAllowedActionTypes(reaction: false)
            .SetForbiddenActions(ActionDefinitions.Id.DashBonus, ActionDefinitions.Id.DashMain)
            .AddToDB();

        var conditionPsychicWhipNoReaction = ConditionDefinitionBuilder
            .Create(ConditionConfused, $"Condition{NAME}NoReaction")
            .SetOrUpdateGuiPresentation(Category.Condition)
            .SetPossessive()
            .SetConditionType(ConditionType.Detrimental)
            .SetFeatures(actionAffinityPsychicWhipNoReaction)
            .AddCustomSubFeatures(new ActionFinishedByMeCheckBonusOrMainOrMove())
            .AddToDB();

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.PsychicWhip, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolEnchantment)
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.None)
            .SetSomaticComponent(false)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Defense)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Round, 1)
                    .SetTargetingData(Side.Enemy, RangeType.Distance, 18, TargetType.IndividualsUnique)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel,
                        additionalTargetsPerIncrement: 1)
                    .SetSavingThrowData(false, AttributeDefinitions.Intelligence, true,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .SetDamageForm(DamageTypePsychic, 3, DieType.D6)
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .Build(),
                        EffectFormBuilder
                            .Create()
                            .SetConditionForm(conditionPsychicWhipNoReaction, ConditionForm.ConditionOperation.Add)
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .Build())
                    .SetParticleEffectParameters(GravitySlam)
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Mirror Image

    internal static readonly ConditionDefinition ConditionMirrorImageMark = ConditionDefinitionBuilder
        .Create("ConditionMirrorImageMark")
        .SetGuiPresentation(MirrorImage.Condition.Name, Category.Condition)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .CopyParticleReferences(ConditionBlurred)
        .AddCustomSubFeatures(MirrorImage.DuplicateProvider.Mark)
        .AddToDB();

    [NotNull]
    internal static SpellDefinition BuildMirrorImage()
    {
        //Use Condition directly, instead of ConditionName to guarantee it gets built
        var spell = SpellDefinitions.MirrorImage;

        spell.contentPack = CeContentPackContext.CeContentPack; // required otherwise it messes up spells UI
        spell.implemented = true;
        spell.uniqueInstance = true;
        spell.schoolOfMagic = SchoolIllusion;
        spell.verboseComponent = true;
        spell.somaticComponent = true;
        spell.vocalSpellSemeType = VocalSpellSemeType.Defense;
        spell.materialComponentType = MaterialComponentType.None;
        spell.castingTime = ActivationTime.Action;
        spell.effectDescription = EffectDescriptionBuilder.Create()
            .SetDurationData(DurationType.Minute, 1)
            .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
            .SetEffectForms(
                EffectFormBuilder
                    .Create()
                    .SetConditionForm(ConditionMirrorImageMark, ConditionForm.ConditionOperation.Add)
                    .Build())
            .SetParticleEffectParameters(Blur)
            .Build();

        return spell;
    }

    #endregion

    #region Borrowed Knowledge

    private static bool HasSkillProficiency(
        RulesetCharacter character,
        SkillDefinition skill)
    {
        return GetSkillProficiencyRank(character, skill) > 0;
    }

    private static bool HasSkillExpertise(
        RulesetCharacter character,
        SkillDefinition skill)
    {
        return GetSkillProficiencyRank(character, skill) >= 2;
    }

    private static int GetSkillProficiencyRank(
        RulesetCharacter character,
        SkillDefinition skill)
    {
        if (character == null || skill == null)
        {
            return 0;
        }

        if (character is RulesetCharacterSimulacrum simulacrum)
        {
            return simulacrum.GetSkillProficiencyRank(skill.Name);
        }

        if (character is RulesetCharacterHero hero)
        {
            if (hero.ExpertiseProficiencies.Contains(skill.Name) ||
                hero.TrainedExpertises.Contains(skill.Name))
            {
                return 2;
            }

            return hero.SkillProficiencies.Contains(skill.Name) ||
                   hero.TrainedSkills.Contains(skill)
                ? 1
                : 0;
        }

        var rank = character is RulesetCharacterMonster monster &&
                   monster.SkillProficiencies.ContainsKey(skill.Name)
            ? 1
            : 0;
        var matchingProficiencies = character
            .FeaturesByType<FeatureDefinitionProficiency>()
            .Where(feature => feature.Proficiencies.Contains(skill.Name))
            .ToArray();

        if (matchingProficiencies.Any(feature =>
                feature.ProficiencyType == ProficiencyType.Skill))
        {
            rank = System.Math.Max(rank, 1);
        }

        if (matchingProficiencies.Any(feature =>
                feature.ProficiencyType == ProficiencyType.Expertise))
        {
            rank = 2;
        }

        foreach (var _ in matchingProficiencies.Where(feature =>
                     feature.ProficiencyType == ProficiencyType.SkillOrExpertise))
        {
            rank = rank > 0 ? 2 : 1;
        }

        return rank;
    }

    internal static SpellDefinition BuildBorrowedKnowledge()
    {
        const string NAME = "BorrowedKnowledge";

        LimitEffectInstances limiter = new("BorrowedKnowledge", _ => 1);

        var skillsDb = DatabaseRepository.GetDatabase<SkillDefinition>();
        var powers = new List<FeatureDefinitionPower>();
        var powerPool = FeatureDefinitionPowerBuilder
            .Create($"Power{NAME}")
            .SetGuiPresentationNoContent(true)
            .SetUsesFixed(ActivationTime.NoCost)
            .AddToDB();

        foreach (var skill in skillsDb)
        {
            var power = FeatureDefinitionPowerSharedPoolBuilder
                .Create($"Power{NAME}{skill.Name}")
                .SetGuiPresentation(skill.GuiPresentation.Title, skill.GuiPresentation.Description)
                .SetSharedPool(ActivationTime.NoCost, powerPool)
                .SetShowCasting(false)
                .SetEffectDescription(
                    EffectDescriptionBuilder
                        .Create()
                        .SetDurationData(DurationType.Hour, 1)
                        .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                        .SetEffectForms(
                            EffectFormBuilder.ConditionForm(
                                ConditionDefinitionBuilder
                                    .Create($"Condition{NAME}{skill.Name}")
                                    .SetGuiPresentation(
                                        skill.GuiPresentation.Title, Gui.NoLocalization, ConditionBullsStrength)
                                    .SetPossessive()
                                    .SetFeatures(
                                        FeatureDefinitionProficiencyBuilder
                                            .Create($"Proficiency{NAME}{skill.Name}")
                                            .SetGuiPresentation(skill.GuiPresentation)
                                            .SetProficiencies(ProficiencyType.Skill, skill.Name)
                                            .AddToDB())
                                    .AddToDB()))
                        .Build())
                .AddCustomSubFeatures(limiter)
                .AddToDB();

            power.GuiPresentation.hidden = true;

            powers.Add(power);
        }

        PowerBundle.RegisterPowerBundle(powerPool, false, powers);

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.BorrowedKnowledge, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolDivination)
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetVerboseComponent(true)
            .SetSomaticComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Buff)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetDurationData(DurationType.Hour, 1)
                    .SetTargetingData(Side.All, RangeType.Self, 0, TargetType.Self)
                    .SetCasterEffectParameters(TrueSeeing)
                    .SetEffectEffectParameters(PowerPaladinCleansingTouch)
                    .Build())
            .AddCustomSubFeatures(new PowerOrSpellFinishedByMeBorrowedKnowledge(powerPool, [.. powers]))
            .AddToDB();

        return spell;
    }

    private sealed class PowerOrSpellFinishedByMeBorrowedKnowledge(
        FeatureDefinitionPower powerPool,
        params FeatureDefinitionPower[] powers) : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            if (action.Countered || action.ExecutionFailed)
            {
                yield break;
            }

            var actingCharacter = action.ActingCharacter;
            var rulesetCharacter = actingCharacter.RulesetCharacter;

            var usablePowers = new List<RulesetUsablePower>();
            var skillsDb = DatabaseRepository.GetDatabase<SkillDefinition>();

            foreach (var power in powers)
            {
                var skillName = power.Name.Replace("PowerBorrowedKnowledge", string.Empty);

                if (!skillsDb.TryGetElement(skillName, out var skill))
                {
                    continue;
                }

                if (HasSkillProficiency(rulesetCharacter, skill))
                {
                    continue;
                }

                var up = PowerProvider.Get(power, rulesetCharacter);

                usablePowers.Add(up);
                rulesetCharacter.UsablePowers.Add(up);
            }

            var usablePower = PowerProvider.Get(powerPool, rulesetCharacter);

            yield return actingCharacter.MyReactToSpendPowerBundle(
                usablePower,
                [actingCharacter],
                actingCharacter,
                "BorrowedKnowledge",
                reactionValidated: ReactionValidated);

            rulesetCharacter.UsablePowers.Remove(usablePower);
            usablePowers.ForEach(x => rulesetCharacter.UsablePowers.Remove(x));

            yield break;

            void ReactionValidated(ReactionRequestSpendBundlePower reactionRequest)
            {
                var selectedPower =
                    (reactionRequest.ReactionParams.RulesetEffect as RulesetEffectPower)?.PowerDefinition;

                if (!selectedPower ||
                    usablePowers.All(x => x.PowerDefinition != selectedPower))
                {
                    return;
                }

                foreach (var skill in skillsDb)
                {
                    var conditionName = $"ConditionBorrowedKnowledge{skill.Name}";

                    if (rulesetCharacter.TryGetConditionOfCategoryAndType(
                            AttributeDefinitions.TagEffect, conditionName, out var activeCondition) &&
                        activeCondition.SourceGuid == actingCharacter.Guid &&
                        !selectedPower.Name.Contains(skill.Name))
                    {
                        rulesetCharacter.RemoveCondition(activeCondition);
                    }
                }
            }
        }
    }

    #endregion

    #region Petal Storm

    internal static readonly EffectProxyDefinition ProxyPetalStorm = EffectProxyDefinitionBuilder
        .Create(EffectProxyDefinitions.ProxyInsectPlague, "ProxyPetalStorm")
        .SetGuiPresentation("PetalStorm", Category.Spell, WindWall)
        .SetPortrait(WindWall.GuiPresentation.SpriteReference)
        .SetActionId(ExtraActionId.ProxyPetalStorm)
        .SetAttackMethod(ProxyAttackMethod.ReproduceDamageForms)
        .SetAdditionalFeatures(FeatureDefinitionMoveModes.MoveModeMove6)
        .SetCanMove()
        .AddToDB();

    internal static SpellDefinition BuildPetalStorm()
    {
        const string NAME = "PetalStorm";

        var spell = SpellDefinitionBuilder
            .Create(InsectPlague, NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.PetalStorm, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolConjuration)
            .SetSpellLevel(2)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetSomaticComponent(true)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Attack)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(InsectPlague.EffectDescription)
                    .SetTargetingData(Side.All, RangeType.Distance, 12, TargetType.Cube, 3)
                    .SetDurationData(DurationType.Minute, 1)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel, additionalDicePerIncrement: 2)
                    .SetRecurrentEffect(
                        RecurrentEffect.OnActivation | RecurrentEffect.OnEnter | RecurrentEffect.OnTurnStart)
                    .SetSavingThrowData(
                        false,
                        AttributeDefinitions.Strength,
                        false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.Negates)
                            .SetDamageForm(DamageTypeSlashing, 3, DieType.D4)
                            .Build(),
                        EffectFormBuilder.ConditionForm(ConditionHeavilyObscured),
                        EffectFormBuilder.TopologyForm(TopologyForm.Type.SightImpaired, true),
                        EffectFormBuilder
                            .Create()
                            .SetSummonEffectProxyForm(ProxyPetalStorm)
                            .Build())
                    .Build())
            .AddToDB();

        return spell;
    }

    #endregion

    #region Shadowblade

    [NotNull]
    internal static SpellDefinition BuildShadowBlade()
    {
        const string NAME = "ShadowBlade";

        var itemShadowBlade = ItemDefinitionBuilder
            .Create(ItemDefinitions.FlameBlade, $"Item{NAME}")
            .SetOrUpdateGuiPresentation(Category.Item, ItemDefinitions.Enchanted_Dagger_Souldrinker)
            .SetItemTags(TagsDefinitions.ItemTagConjured)
            .MakeMagical()
            .SetStaticProperties(ItemPropertyDescriptionBuilder.From(FeatureDefinitionBuilder
                        .Create($"Feature{NAME}")
                        .SetGuiPresentation($"Feature{NAME}", Category.Feature)
                        .AddToDB(),
                    knowledgeAffinity: EquipmentDefinitions.KnowledgeAffinity.ActiveAndVisible)
                .Build())
            .HideFromDungeonEditor()
            .AddToDB();

        itemShadowBlade.activeTags.Clear();
        itemShadowBlade.isLightSourceItem = false;
        itemShadowBlade.itemPresentation.assetReference = ItemDefinitions.ScimitarPlus2.ItemPresentation.AssetReference;
        itemShadowBlade.weaponDefinition.EffectDescription.EffectParticleParameters.impactParticleReference =
            EffectProxyDefinitions.ProxyArcaneSword.attackImpactParticle;

        var weaponDescription = itemShadowBlade.WeaponDescription;

        weaponDescription.closeRange = 4;
        weaponDescription.maxRange = 12;
        weaponDescription.weaponType = WeaponTypeDefinitions.DaggerType.Name;
        weaponDescription.weaponTags.Add(TagsDefinitions.WeaponTagThrown);

        var damageForm = weaponDescription.EffectDescription.FindFirstDamageForm();

        damageForm.damageType = DamageTypePsychic;
        damageForm.dieType = DieType.D8;
        damageForm.diceNumber = 2;

        var spell = SpellDefinitionBuilder
            .Create(FlameBlade, NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.ShadeBlade, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolIllusion)
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.BonusAction)
            .SetMaterialComponent(MaterialComponentType.None)
            .SetSomaticComponent(true)
            .SetVerboseComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Buff)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create(FlameBlade)
                    .SetDurationData(DurationType.Minute, 1)
                    .Build())
            .AddToDB();

        var summonForm = spell.EffectDescription.EffectForms[0].SummonForm;

        summonForm.itemDefinition = itemShadowBlade;

        var itemPropertyForm = spell.EffectDescription.EffectForms[1].ItemPropertyForm;

        itemPropertyForm.featureBySlotLevel.Clear();
        itemPropertyForm.featureBySlotLevel.Add(BuildShadowBladeFeatureBySlotLevel(2, 0));
        itemPropertyForm.featureBySlotLevel.Add(BuildShadowBladeFeatureBySlotLevel(3, 1));
        itemPropertyForm.featureBySlotLevel.Add(BuildShadowBladeFeatureBySlotLevel(5, 2));
        itemPropertyForm.featureBySlotLevel.Add(BuildShadowBladeFeatureBySlotLevel(7, 3));

        var conditionShadowBlade = ConditionDefinitionBuilder
            .Create($"Condition{NAME}")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .AddToDB();

        conditionShadowBlade.AddCustomSubFeatures(
            new ModifyAttackActionModifierShadowBlade(itemShadowBlade));

        spell.EffectDescription.EffectForms.Add(
            EffectFormBuilder
                .Create()
                .SetConditionForm(conditionShadowBlade, ConditionForm.ConditionOperation.Add, true)
                .Build());

        return spell;
    }

    private static FeatureUnlockByLevel BuildShadowBladeFeatureBySlotLevel(int level, int damageDice)
    {
        var attackModifierShadowBladeLevel = FeatureDefinitionAttackModifierBuilder
            .Create(FeatureDefinitionAttackModifiers.AttackModifierFlameBlade2, $"AttackModifierShadowBlade{level}")
            .AddToDB();

        attackModifierShadowBladeLevel.guiPresentation.description
            = damageDice > 0
                ? Gui.Format("Feature/&AttackModifierShadowBladeNDescription", damageDice.ToString())
                : "Feature/&AttackModifierShadowBlade0Description";
        attackModifierShadowBladeLevel.additionalDamageDice = damageDice;
        attackModifierShadowBladeLevel.impactParticleReference =
            ShadowDagger.EffectDescription.EffectParticleParameters.impactParticleReference;
        attackModifierShadowBladeLevel.abilityScoreReplacement = AbilityScoreReplacement.None;
        return new FeatureUnlockByLevel(attackModifierShadowBladeLevel, level);
    }

    private sealed class ModifyAttackActionModifierShadowBlade(ItemDefinition itemShadowBlade)
        : IModifyAttackActionModifier
    {
        private readonly TrendInfo _trendInfo =
            new(1, FeatureSourceType.Equipment, itemShadowBlade.Name, itemShadowBlade);

        public void OnAttackComputeModifier(
            RulesetCharacter myself,
            RulesetCharacter defender,
            BattleDefinitions.AttackProximity attackProximity,
            RulesetAttackMode attackMode,
            string effectName,
            ref ActionModifier attackModifier)
        {
            if (myself is not { IsDeadOrDyingOrUnconscious: false } ||
                defender is not { IsDeadOrDyingOrUnconscious: false })
            {
                return;
            }

            if (attackMode?.SourceDefinition != itemShadowBlade)
            {
                return;
            }

            if (!ValidatorsCharacter.IsNotInBrightLight(defender))
            {
                return;
            }

            attackModifier.AttackAdvantageTrends.Add(_trendInfo);
        }
    }

    #endregion

    #region Wither and Bloom

    internal static SpellDefinition BuildWitherAndBloom()
    {
        const string NAME = "WitherAndBloom";

        var conditionSpellCastingBonus = ConditionDefinitionBuilder
            .Create($"Condition{NAME}")
            .SetGuiPresentationNoContent(true)
            .SetSilent(Silent.WhenAddedOrRemoved)
            .SetAmountOrigin(ConditionDefinition.OriginOfAmount.Fixed)
            .SetSpecialInterruptions(ConditionInterruption.AnyBattleTurnEnd)
            .AddToDB();

        conditionSpellCastingBonus.AddCustomSubFeatures(
            new ModifyDiceRollHitDiceWitherAndBloom(conditionSpellCastingBonus));

        var spell = SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.WitherAndBloom, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolNecromancy)
            .SetSpellLevel(2)
            .SetCastingTime(ActivationTime.Action)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetVerboseComponent(true)
            .SetSomaticComponent(true)
            .SetVocalSpellSameType(VocalSpellSemeType.Attack)
            .SetEffectDescription(
                EffectDescriptionBuilder
                    .Create()
                    .SetTargetingData(Side.Ally, RangeType.Distance, 12, TargetType.IndividualsUnique)
                    .SetSavingThrowData(
                        false, AttributeDefinitions.Constitution, false,
                        EffectDifficultyClassComputation.SpellCastingFeature)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel, additionalDicePerIncrement: 1)
                    .SetEffectForms(
                        EffectFormBuilder
                            .Create()
                            .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                            .SetDamageForm(DamageTypeNecrotic, 2, DieType.D6)
                            .Build())
                    .SetCasterEffectParameters(FalseLife)
                    .Build())
            .AddToDB();

        spell.AddCustomSubFeatures(new CustomBehaviorWitherAndBloom(spell, conditionSpellCastingBonus));

        return spell;
    }

    private sealed class ModifyDiceRollHitDiceWitherAndBloom(ConditionDefinition conditionWitherAndBloom)
        : IModifyDiceRollHitDice
    {
        public void BeforeRoll(
            RulesetCharacterHero __instance,
            ref DieType die,
            ref int modifier,
            ref AdvantageType advantageType,
            ref bool healKindred,
            ref bool isBonus)
        {
            if (__instance.TryGetConditionOfCategoryAndType(
                    AttributeDefinitions.TagEffect, conditionWitherAndBloom.Name, out var activeCondition))
            {
                modifier = activeCondition.amount;
            }
        }
    }

    private sealed class CustomBehaviorWitherAndBloom(
        SpellDefinition spellWitherAndBloom,
        ConditionDefinition conditionWitherAndBloom) : IPowerOrSpellInitiatedByMe, IPowerOrSpellFinishedByMe
    {
        private int _spellCastingAbilityModifier;
        private GameLocationCharacter _target;

        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            if (action.Countered || action.ExecutionFailed)
            {
                yield break;
            }

            if (action.ActionParams.activeEffect is not RulesetEffectSpell rulesetEffectSpell ||
                _target == null)
            {
                yield break;
            }

            var rulesetTarget = _target.RulesetCharacter.GetOriginalHero();

            if (rulesetTarget == null)
            {
                yield break;
            }

            var attacker = action.ActingCharacter;
            var hasHealed = false;
            var effectLevel = rulesetEffectSpell.EffectLevel;
            var passed = false;

            rulesetTarget.HitDieRolled += HitDieRolled;

            while (--effectLevel > 0 &&
                   rulesetTarget.RemainingHitDiceCount() > 0 &&
                   rulesetTarget.MissingHitPoints > 0)
            {
                var maxHitPoints = rulesetTarget.TryGetAttributeValue(AttributeDefinitions.HitPoints);
                var remainingHitPoints = maxHitPoints - rulesetTarget.MissingHitPoints;

                yield return _target.MyReactToDoNothing(
                    ExtraActionId.DoNothingFree,
                    attacker,
                    "WitherAndBloom",
                    "CustomReactionWitherAndBloomDescription".Formatted(Category.Reaction,
                        remainingHitPoints.ToString(), maxHitPoints.ToString(), attacker.Name,
                        _spellCastingAbilityModifier.ToString()),
                    ReactionValidated,
                    ReactionNotValidated,
                    target: _target, effectDefinition: spellWitherAndBloom);

                if (passed)
                {
                    break;
                }
            }

            rulesetTarget.HitDieRolled -= HitDieRolled;

            if (hasHealed)
            {
                EffectHelpers.StartVisualEffect(attacker, _target, CureWounds, EffectHelpers.EffectType.Effect);
            }

            yield break;

            void ReactionValidated()
            {
                hasHealed = true;
                rulesetTarget.RollHitDie();
            }

            void ReactionNotValidated()
            {
                passed = true;
            }
        }

        public IEnumerator OnPowerOrSpellInitiatedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            _target = null;

            if (action.ActionParams.activeEffect is not RulesetEffectSpell rulesetEffectSpell ||
                action.ActionParams.TargetCharacters.Count == 0)
            {
                yield break;
            }

            _target = action.ActionParams.TargetCharacters[0];

            action.ActionParams.TargetCharacters.SetRange(
                (Gui.Battle?.AllContenders ?? [])
                .Where(x =>
                    _target.IsWithinRange(x, 2) &&
                    _target.IsOppositeSide(x.Side)));

            action.ActionParams.ActionModifiers.Clear();

            for (var i = 0; i < action.ActionParams.TargetCharacters.Count; i++)
            {
                action.ActionParams.ActionModifiers.Add(new ActionModifier());
            }

            var rulesetAttacker = action.ActingCharacter.RulesetCharacter;

            _spellCastingAbilityModifier = rulesetEffectSpell.ComputeSourceAbilityBonus(rulesetAttacker);

            var rulesetTarget = _target.RulesetCharacter;

            rulesetTarget.InflictCondition(
                conditionWitherAndBloom.Name,
                DurationType.Round,
                0,
                TurnOccurenceType.EndOfTurn,
                AttributeDefinitions.TagEffect,
                rulesetTarget.guid,
                rulesetTarget.CurrentFaction.Name,
                1,
                conditionWitherAndBloom.Name,
                _spellCastingAbilityModifier,
                0,
                0);
        }

        private void HitDieRolled(
            RulesetCharacter character,
            DieType dieType,
            int value,
            AdvantageType advantageType,
            int roll1,
            int roll2,
            int modifier,
            bool isBonus)
        {
            // reuse translation string from other feat
            const string BASE_LINE = "Feedback/&DwarvenFortitudeHitDieRolled";

            character.ShowDieRoll(
                dieType, roll1, roll2, advantage: advantageType, title: spellWitherAndBloom.GuiPresentation.Title);

            character.LogCharacterActivatesAbility(
                Gui.NoLocalization, BASE_LINE, true,
                extra:
                [
                    (ConsoleStyleDuplet.ParameterType.AbilityInfo, Gui.FormatDieTitle(dieType)),
                    (ConsoleStyleDuplet.ParameterType.Positive,
                        $"{value - modifier}+{modifier}"),
                    (ConsoleStyleDuplet.ParameterType.Positive, $"{value}")
                ]);
        }
    }

    #endregion

    #region Dragons Breath

    internal static SpellDefinition BuildDragonsBreath()
    {
        const string NAME = "DragonsBreathSpell";

        var subSpells = new List<SpellDefinition>();

        var powers = DatabaseRepository.GetDatabase<FeatureDefinitionPower>()
            .Where(x => x.Name.StartsWith("PowerDragonbornBreathWeapon"))
            .ToArray();

        foreach (var (damageType, magicEffect) in DamagesAndEffects)
        {
            if (damageType == DamageTypeThunder) { continue; } // Spell doesn't include thunder damage

            var dragonbornBreathPower = powers.First(
                x => x.EffectDescription.FindFirstDamageFormOfType([damageType]) != null);
            var powerBreathAttack = FeatureDefinitionPowerBuilder
                .Create($"Power{NAME}{damageType}")
                .SetGuiPresentation($"Power{NAME}", Category.Feature,
                    dragonbornBreathPower.GuiPresentation.SpriteReference)
                .SetUsesFixed(ActivationTime.Action)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetTargetingData(Side.All, RangeType.Self, 1, TargetType.Cone, 3)
                    .SetEffectAdvancement(EffectIncrementMethod.None)
                    .SetEffectForms(EffectFormBuilder.Create()
                        .SetDamageForm(damageType, 3, DieType.D6)
                        .HasSavingThrow(EffectSavingThrowType.HalfDamage)
                        .Build())
                    .SetSavingThrowData(false, AttributeDefinitions.Dexterity, false,
                        EffectDifficultyClassComputation.FixedValue)
                    .SetCasterEffectParameters(dragonbornBreathPower)
                    .SetImpactEffectParameters(dragonbornBreathPower)
                    .SetParticleEffectParameters(dragonbornBreathPower)
                    .SetAnimationMagicEffect(dragonbornBreathPower.EffectDescription.AnimationMagicEffect)
                    .Build())
                .AddCustomSubFeatures(new FormattedDefinitionText(
                    title: () => Gui.Format($"Feature/&Power{NAME}Title",
                        Gui.Localize($"Tooltip/&Tag{damageType}Title")),
                    description: () => Gui.Format($"Feature/&Power{NAME}Description",
                        Gui.Localize($"Tooltip/&Tag{damageType}Title"))))
                .AddToDB();

            var conditionDragonsBreath = ConditionDefinitionBuilder
                .Create($"Condition{NAME}{damageType}")
                .SetGuiPresentation($"Condition{NAME}", Category.Condition,
                    ConditionSorcererDraconicElementalResistance.GuiPresentation.SpriteReference)
                .SetPossessive()
                .SetConditionType(ConditionType.Beneficial)
                .AddFeatures(powerBreathAttack)
                .SetConditionParticleReference(dragonbornBreathPower)
                .AddCustomSubFeatures(
                    AddUsablePowersFromCondition.Marker,
                    new FormattedDefinitionText(description: () => Gui.Format(
                        $"Condition/&Condition{NAME}Description",
                        Gui.Localize($"Tooltip/&Tag{damageType}Title"))))
                .AddToDB();

            powerBreathAttack.AddCustomSubFeatures(
                new ModifySaveDCDragonsBreath(conditionDragonsBreath, powerBreathAttack));

            var spell = SpellDefinitionBuilder
                .Create(NAME + damageType)
                .SetGuiPresentation($"Tooltip/&Tag{damageType}Title", $"Spell/&SubSpell{NAME}Description",
                    dragonbornBreathPower.GuiPresentation.SpriteReference)
                .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolTransmutation)
                .SetSpellLevel(2)
                .SetCastingTime(ActivationTime.BonusAction)
                .SetVerboseComponent(true)
                .SetSomaticComponent(true)
                .SetMaterialComponent(MaterialComponentType.Mundane)
                .SetVocalSpellSameType(VocalSpellSemeType.Buff)
                .SetRequiresConcentration(true)
                .SetEffectDescription(EffectDescriptionBuilder.Create()
                    .SetDurationData(DurationType.Minute, 1)
                    .SetTargetingData(Side.Ally, RangeType.Touch, 0, TargetType.IndividualsUnique)
                    .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel)
                    .SetEffectForms(EffectFormBuilder.AddConditionForm(conditionDragonsBreath))
                    .SetCasterEffectParameters(Heroism)
                    .SetParticleEffectParameters(magicEffect)
                    .Build())
                .AddCustomSubFeatures(
                    new PowerOrSpellFinishedByMeDragonsBreath(conditionDragonsBreath),
                    new FormattedDefinitionText(description: () => Gui.Format(
                        $"Spell/&SubSpell{NAME}Description",
                        Gui.Localize($"Tooltip/&Tag{damageType}Title"))))
                .AddToDB();

            subSpells.Add(spell);
        }

        return SpellDefinitionBuilder
            .Create(NAME)
            .SetGuiPresentation(Category.Spell, Sprites.GetSprite(NAME, Resources.DragonsBreath, 128))
            .SetSchoolOfMagic(SchoolOfMagicDefinitions.SchoolTransmutation)
            .SetSpellLevel(2)
            .SetVerboseComponent(true)
            .SetSomaticComponent(true)
            .SetMaterialComponent(MaterialComponentType.Mundane)
            .SetVocalSpellSameType(VocalSpellSemeType.Buff)
            .SetCastingTime(ActivationTime.BonusAction)
            .SetRequiresConcentration(true)
            .SetSubSpells([.. subSpells])
            .SetEffectDescription(EffectDescriptionBuilder.Create()
                .SetDurationData(DurationType.Minute, 1)
                .SetTargetingData(Side.Ally, RangeType.Touch, 0, TargetType.IndividualsUnique)
                .SetEffectAdvancement(EffectIncrementMethod.PerAdditionalSlotLevel)
                .Build())
            .AddToDB();
    }

    private sealed class PowerOrSpellFinishedByMeDragonsBreath(ConditionDefinition conditionDragonsBreath)
        : IPowerOrSpellFinishedByMe
    {
        public IEnumerator OnPowerOrSpellFinishedByMe(CharacterActionMagicEffect action, BaseDefinition baseDefinition)
        {
            if (action is not CharacterActionCastSpell actionCastSpell ||
                actionCastSpell.Countered ||
                actionCastSpell.ExecutionFailed)
            {
                yield break;
            }

            var rulesetCaster = action.ActingCharacter.RulesetCharacter;

            // need to loop over target characters to support twinned metamagic scenarios
            foreach (var rulesetTarget in action.ActionParams.TargetCharacters
                         .Select(target => target.RulesetCharacter))
            {
                if (!rulesetTarget.TryGetConditionOfCategoryAndType(
                        AttributeDefinitions.TagEffect,
                        conditionDragonsBreath.Name,
                        out var activeCondition))
                {
                    continue;
                }

                // store info from spell Level
                activeCondition.effectLevel = actionCastSpell.ActiveSpell.SlotLevel;
                // store info for caster Spell Save DC
                activeCondition.Amount = rulesetCaster.ComputeSaveDC(actionCastSpell.activeSpell.SpellRepertoire);
            }
        }
    }

    private sealed class ModifySaveDCDragonsBreath(
        ConditionDefinition conditionDragonsBreath,
        FeatureDefinitionPower powerDragonsBreath)
        : IModifyEffectDescription
    {
        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            character.TryGetConditionOfCategoryAndType(
                AttributeDefinitions.TagEffect,
                conditionDragonsBreath.Name,
                out var activeCondition);

            //set upcast damage dice: 3 + ( slot Lv - base spell Lv(2) )
            effectDescription.FindFirstDamageForm().diceNumber = 1 + activeCondition.effectLevel;
            var dc = activeCondition.Amount;
            effectDescription.FixedSavingThrowDifficultyClass = dc; 
            //set Spell Save DC
            if (rulesetEffect is RulesetEffectPower rulesetEffectPower)
            {
                rulesetEffectPower.usablePower.saveDC = dc;
            }

            return effectDescription;
        }

        public bool IsValid(
            BaseDefinition definition,
            RulesetCharacter character,
            EffectDescription effectDescription)
        {
            return character.HasConditionOfType(conditionDragonsBreath) &&
                   definition == powerDragonsBreath;
        }
    }

    #endregion
}
