using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using UnityEngine.AddressableAssets;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionAttributeModifiers;

namespace SolastaUnfinishedBusiness.Feats;

internal static class SkillFeats
{
    internal const string SkilledName = "FeatSkilled";
    internal const string SkillExpertGroupName = "FeatGroupSkillExpert";

    internal static FeatureDefinitionCustomInvocationPool ExpertisePool { get; private set; }

    internal static bool IsRepeatable(FeatDefinition feat)
    {
        return feat != null && feat.HasSubFeatureOfType<RepeatableFeat>();
    }

    internal static void CreateFeats(List<FeatDefinition> feats)
    {
        foreach (var skill in DatabaseRepository.GetDatabase<SkillDefinition>())
        {
            BuildProficiencyChoice(InvocationPoolTypeCustom.Pools.FeatSkilled, skill, ProficiencyType.Skill);
            BuildProficiencyChoice(InvocationPoolTypeCustom.Pools.FeatSkillExpertSkill, skill, ProficiencyType.Skill);
            BuildProficiencyChoice(InvocationPoolTypeCustom.Pools.FeatSkillExpertExpertise, skill,
                ProficiencyType.Expertise);
        }

        foreach (var tool in DatabaseRepository.GetDatabase<ToolTypeDefinition>()
                     .Where(tool => !tool.GuiPresentation.Hidden))
        {
            BuildProficiencyChoice(InvocationPoolTypeCustom.Pools.FeatSkilled, tool, ProficiencyType.Tool);
        }

        // Feat tooltips are text-only; selection artwork belongs to the proficiency invocations.
        var skilled = FeatDefinitionBuilder
            .Create(SkilledName)
            .SetGuiPresentation(Category.Feat)
            .SetFeatures(BuildPool(InvocationPoolTypeCustom.Pools.FeatSkilled, 3))
            .AddCustomSubFeatures(RepeatableFeat.Marker)
            .AddToDB();

        var skillPool = BuildPool(InvocationPoolTypeCustom.Pools.FeatSkillExpertSkill);
        ExpertisePool = BuildPool(InvocationPoolTypeCustom.Pools.FeatSkillExpertExpertise);
        var abilityModifiers = new[]
        {
            AttributeModifierCreed_Of_Einar,
            AttributeModifierCreed_Of_Misaye,
            AttributeModifierCreed_Of_Arun,
            AttributeModifierCreed_Of_Pakri,
            AttributeModifierCreed_Of_Maraike,
            AttributeModifierCreed_Of_Solasta
        };

        var variants = abilityModifiers.Select(modifier => FeatDefinitionWithPrerequisitesBuilder
                .Create($"FeatSkillExpert{modifier.ModifiedAttribute}")
                .SetGuiPresentation($"Feat/&FeatSkillExpert{modifier.ModifiedAttribute}Title",
                    "Feat/&FeatSkillExpertDescription")
                .SetFeatFamily("SkillExpert")
                .SetFeatures(modifier, skillPool, ExpertisePool)
                .SetValidators((feat, hero) => ValidateAbilityIncrease(feat, hero, modifier.ModifiedAttribute))
                .AddToDB())
            .ToArray();
        var skillExpert = GroupFeats.MakeGroup(SkillExpertGroupName, "SkillExpert", variants);

        feats.Add(skilled);
        feats.AddRange(variants);
        GroupFeats.FeatGroupOrigin.AddFeats(skilled);
        GroupFeats.FeatGroupSkills.AddFeats(skilled, skillExpert);
        GroupFeats.FeatGroupTools.AddFeats(skilled);
    }

    private static (bool result, string output) ValidateAbilityIncrease(
        FeatDefinition feat,
        RulesetCharacterHero hero,
        string attribute)
    {
        if (FeatsContext.IsKnownFeatPrerequisiteDisplay(feat, hero))
        {
            return (true, string.Empty);
        }

        // A preview already includes the selected feat's +1. Do not invalidate that choice on refresh.
        var selected = GetBuildingData(hero)?.LevelupTrainedFeats.Values
            .SelectMany(feats => feats)
            .Any(trained => trained == feat) == true;
        var valid = selected || hero.TryGetAttributeValue(attribute) < 20;

        return (valid, valid ? string.Empty : Gui.Format("Tooltip/&SkillExpertAbilityMaximum",
            Gui.Localize($"Attribute/&{attribute}Title")));
    }

    private static FeatureDefinitionCustomInvocationPool BuildPool(InvocationPoolTypeCustom pool, int points = 1)
    {
        return CustomInvocationPoolDefinitionBuilder
            .Create($"InvocationPool{pool.Name}")
            .SetGuiPresentationNoContent(true)
            .Setup(pool, points)
            .AddToDB();
    }

    private static void BuildProficiencyChoice(
        InvocationPoolTypeCustom pool,
        BaseDefinition proficiency,
        ProficiencyType type)
    {
        var feature = FeatureDefinitionProficiencyBuilder
            .Create($"Proficiency{pool.Name}{proficiency.Name}")
            .SetGuiPresentationNoContent(true)
            .SetProficiencies(type, proficiency.Name)
            .AddToDB();

        _ = CustomInvocationDefinitionBuilder
            .Create($"CustomInvocation{pool.Name}{proficiency.Name}")
            .SetGuiPresentation(proficiency.GuiPresentation.Title, proficiency.GuiPresentation.Description,
                GetProficiencySprite(proficiency, pool))
            .SetPoolType(pool)
            .SetGrantedFeature(feature)
            .AddValidators(ValidateProficiencyChoice)
            .AddCustomSubFeatures(ModifyInvocationVisibility.Marker)
            .AddToDB();
    }

    private static AssetReferenceSprite GetProficiencySprite(BaseDefinition proficiency, InvocationPoolTypeCustom pool)
    {
        var sprite = proficiency.GuiPresentation.SpriteReference;

        if (sprite?.RuntimeKeyIsValid() == true)
        {
            return sprite;
        }

        if (proficiency is ToolTypeDefinition)
        {
            // Tool types may have no illustration even when their inventory items do.
            sprite = DatabaseRepository.GetDatabase<ItemDefinition>()
                .Where(item => item.IsTool && item.ToolDescription.ToolType == proficiency.Name)
                .OrderByDescending(item => item.ItemTags.Contains(TagsDefinitions.ItemTagStandard))
                .ThenBy(item => item.Name)
                .Select(item => item.GuiPresentation.SpriteReference)
                .FirstOrDefault(reference => reference?.RuntimeKeyIsValid() == true);

            if (sprite?.RuntimeKeyIsValid() != true && proficiency.Name == "GamingSetDiceType")
            {
                sprite = DatabaseRepository.GetDatabase<ItemDefinition>()
                    .GetElement("Art_Item_25_GP_EngraveBoneDice", true)?.GuiPresentation.SpriteReference;
            }
        }

        // Missing artwork must identify the proficiency, not borrow an unrelated tool or spell illustration.
        return sprite?.RuntimeKeyIsValid() == true
            ? sprite
            : ProficiencyChoiceSprites.Get(proficiency.Name) ?? pool.Sprite;
    }

    private static bool ValidateProficiencyChoice(
        RulesetCharacter character,
        BaseDefinition definition,
        out string requirement)
    {
        requirement = null;

        if (character is not RulesetCharacterHero hero ||
            definition is not InvocationDefinitionCustom invocation ||
            invocation.GrantedFeature is not FeatureDefinitionProficiency proficiency)
        {
            return false;
        }

        var name = proficiency.Proficiencies[0];
        var buildingData = GetBuildingData(hero);
        var selected = hero.TrainedInvocations.Contains(invocation) ||
                       buildingData?.LevelupTrainedInvocations.Values.Any(choices => choices.Contains(invocation)) == true;
        var skill = DatabaseRepository.GetDatabase<SkillDefinition>().GetElement(name, true);
        var pending = GetPendingProficiencies(buildingData, invocation)
            .Where(feature => feature.Proficiencies.Contains(name)).ToArray();
        var features = new List<FeatureDefinition>();
        hero.EnumerateFeaturesToBrowse<FeatureDefinitionProficiency>(features, null);
        var allProficiencies = features.OfType<FeatureDefinitionProficiency>().Concat(pending)
            .Where(feature => feature.Proficiencies.Contains(name)).Distinct().ToArray();
        var trainedSkill = skill != null &&
                           (hero.TrainedSkills.Contains(skill) ||
                            buildingData?.LevelupTrainedSkills.Values.Any(skills => skills.Contains(skill)) == true);
        var trainedExpertise = hero.TrainedExpertises.Contains(name) ||
                               buildingData?.LevelupTrainedExpertises.Values.Any(names => names.Contains(name)) == true;
        var mixedSkills = allProficiencies.Count(feature => feature.ProficiencyType == ProficiencyType.SkillOrExpertise);
        var proficient = skill != null &&
                         (hero.SkillProficiencies.Contains(name) || trainedSkill || trainedExpertise ||
                          allProficiencies.Any(feature => feature.ProficiencyType is ProficiencyType.Skill or
                              ProficiencyType.Expertise or ProficiencyType.SkillOrExpertise));
        // Match RefreshProficiencies: SkillOrExpertise ranks depend on all their
        // origins, including a skill gained during this same selection.
        var expertise = hero.ExpertiseProficiencies.Contains(name) || trainedExpertise ||
                        allProficiencies.Any(feature => feature.ProficiencyType == ProficiencyType.Expertise) ||
                        mixedSkills > 1 ||
                        (mixedSkills > 0 && (trainedSkill ||
                            allProficiencies.Any(feature => feature.ProficiencyType == ProficiencyType.Skill)));
        bool valid;

        if (proficiency.ProficiencyType == ProficiencyType.Expertise)
        {
            // Keep a selected expertise valid after RefreshHero has applied its own granted feature.
            valid = proficient && (selected || !expertise);
            requirement = valid ? null : Gui.Localize("Tooltip/&SkillFeatRequiresExpertise");
        }
        else
        {
            var tool = DatabaseRepository.GetDatabase<ToolTypeDefinition>().GetElement(name, true);
            var knownTool = hero.ToolTypeProficiencies.Contains(name) ||
                            (tool != null && (hero.TrainedToolTypes.Contains(tool) ||
                                buildingData?.LevelupTrainedToolTypes.Values.Any(tools => tools.Contains(tool)) == true)) ||
                            allProficiencies.Any(feature => feature.ProficiencyType is ProficiencyType.Tool or
                                ProficiencyType.ToolOrExpertise);
            var known = proficiency.ProficiencyType == ProficiencyType.Skill ? proficient : knownTool;

            valid = selected || !known;
            requirement = valid ? null : Gui.Localize("Tooltip/&SkillFeatRequiresUntrainedProficiency");
        }

        return valid;
    }

    private static CharacterHeroBuildingData GetBuildingData(RulesetCharacterHero hero)
    {
        return hero.TryGetHeroBuildingData(out var data) && ReferenceEquals(data.HeroCharacter, hero) ? data : null;
    }

    private static IEnumerable<FeatureDefinitionProficiency> GetPendingProficiencies(
        CharacterHeroBuildingData data,
        InvocationDefinition candidate)
    {
        if (data == null)
        {
            return [];
        }

        var features = data.LevelupTrainedFeats.Values.SelectMany(feats => feats).SelectMany(feat => feat.Features)
            .Concat(data.LevelupTrainedInvocations.Values.SelectMany(invocations => invocations)
                .Where(invocation => invocation != candidate).Select(invocation => invocation.GrantedFeature));
        var visited = new HashSet<FeatureDefinition>();
        var proficiencies = new List<FeatureDefinitionProficiency>();

        foreach (var feature in features)
        {
            Collect(feature);
        }

        return proficiencies;

        void Collect(FeatureDefinition feature)
        {
            if (feature == null || !visited.Add(feature))
            {
                return;
            }

            if (feature is FeatureDefinitionProficiency proficiency)
            {
                proficiencies.Add(proficiency);
            }
            else if (feature is FeatureDefinitionFeatureSet { Mode: FeatureDefinitionFeatureSet.FeatureSetMode.Union } set)
            {
                foreach (var child in set.FeatureSet)
                {
                    Collect(child);
                }
            }
        }
    }

    internal sealed class RepeatableFeat
    {
        internal static readonly RepeatableFeat Marker = new();

        private RepeatableFeat()
        {
        }
    }
}
