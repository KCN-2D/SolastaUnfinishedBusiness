using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine.AddressableAssets;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionAttributeModifiers;

namespace SolastaUnfinishedBusiness.Feats;

internal static class SkillFeats
{
    internal const string SkilledName = "FeatSkilled";
    internal const string SkillExpertGroupName = "FeatGroupSkillExpert";

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
        var expertisePool = BuildPool(InvocationPoolTypeCustom.Pools.FeatSkillExpertExpertise);
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
                .SetFeatures(modifier, skillPool, expertisePool)
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
        // A preview already includes the selected feat's +1. Do not invalidate that choice on refresh.
        var selected = hero.GetHeroBuildingData()?.LevelupTrainedFeats.Values
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
        var buildingData = hero.GetHeroBuildingData();
        var selected = hero.TrainedInvocations.Contains(invocation) ||
                       buildingData?.LevelupTrainedInvocations.Values.Any(choices => choices.Contains(invocation)) == true;
        var service = ServiceRepository.GetService<ICharacterBuildingService>();
        var skill = DatabaseRepository.GetDatabase<SkillDefinition>().GetElement(name, true);
        var proficient = skill != null &&
                         (hero.SkillProficiencies.Contains(name) ||
                          hero.TrainedSkills.Contains(skill) ||
                          (buildingData != null && service?.IsSkillKnownOrTrained(buildingData, skill) == true));
        bool valid;

        if (proficiency.ProficiencyType == ProficiencyType.Expertise)
        {
            // Keep a selected expertise valid after RefreshHero has applied its own granted feature.
            valid = proficient && (selected ||
                                    (!hero.ExpertiseProficiencies.Contains(name) &&
                                     !hero.TrainedExpertises.Contains(name) &&
                                     (buildingData == null ||
                                      service?.IsExpertiseKnownOrTrained(buildingData, name) != true)));
            requirement = valid ? null : Gui.Localize("Tooltip/&SkillFeatRequiresExpertise");
        }
        else
        {
            var tool = DatabaseRepository.GetDatabase<ToolTypeDefinition>().GetElement(name, true);
            var known = proficiency.ProficiencyType == ProficiencyType.Skill
                ? proficient
                : hero.ToolTypeProficiencies.Contains(name) ||
                  (tool != null && hero.TrainedToolTypes.Contains(tool)) ||
                  (tool != null && buildingData != null &&
                   service?.IsToolTypeKnownOrTrained(buildingData, tool) == true);

            valid = selected || !known;
            requirement = valid ? null : Gui.Localize("Tooltip/&SkillFeatRequiresUntrainedProficiency");
        }

        return valid;
    }

    internal sealed class RepeatableFeat
    {
        internal static readonly RepeatableFeat Marker = new();

        private RepeatableFeat()
        {
        }
    }
}
