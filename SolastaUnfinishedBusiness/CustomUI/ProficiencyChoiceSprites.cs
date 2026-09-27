using System;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Resources = SolastaUnfinishedBusiness.Properties.Resources;

namespace SolastaUnfinishedBusiness.CustomUI;

internal static class ProficiencyChoiceSprites
{
    // Split the artwork in its transparent gutters so adjacent objects cannot leak into a card.
    private static readonly int[] ColumnEdges = [0, 256, 512, 768, 1016, 1280, 1536];
    private static readonly int[] RowEdges = [0, 264, 512, 752, 1024];
    private static readonly Vector2Int AtlasSize = new(1536, 1024);

    // Tile order in Resources/UI/ProficiencyChoices.png, left to right and top to bottom.
    private static readonly string[] DefinitionNames =
    [
        "Acrobatics", "AnimalHandling", "Arcana", "Athletics", "Deception", "History",
        "Insight", "Intimidation", "Investigation", "Medecine", "Nature", "Perception",
        "Performance", "Persuasion", "Religion", "SleightOfHand", "Stealth", "Survival",
        "MusicalInstrumentLyreType", "DisguiseKitType", "Skilled", "SkillExpert"
    ];

    internal static AssetReferenceSprite Get(string definitionName)
    {
        var index = Array.IndexOf(DefinitionNames, definitionName);

        if (index < 0)
        {
            return null;
        }

        var columns = ColumnEdges.Length - 1;
        var column = index % columns;
        var row = index / columns;

        // Atlas rows start at the top; Unity sprite rectangles start at the bottom of the texture.
        var region = new Rect(ColumnEdges[column], AtlasSize.y - RowEdges[row + 1],
            ColumnEdges[column + 1] - ColumnEdges[column], RowEdges[row + 1] - RowEdges[row]);

        return Sprites.GetSpriteFromAtlas($"ProficiencyChoice{definitionName}", "ProficiencyChoices",
            Resources.ProficiencyChoices, AtlasSize, region);
    }
}
