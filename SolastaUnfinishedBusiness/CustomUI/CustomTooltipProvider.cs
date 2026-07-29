using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using UnityEngine;
using UnityEngine.UI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.CustomUI;

internal class CustomTooltipProvider : GuiBaseDefinitionWrapper, ISubTitleProvider, IPrerequisitesProvider
{
    internal const string RequireCharacterLevel = "Requirement/&FeatureSelectionRequireCharacterLevel";
    internal const string RequireClassLevel = "Requirement/&FeatureSelectionRequireClassLevel";

    private readonly GuiPresentation _guiPresentation;
    private string _description;
    private string _prerequisites = string.Empty;
    private string _subtitle;
    private string _title;

    internal CustomTooltipProvider(BaseDefinition baseDefinition, GuiPresentation guiPresentation) : base(
        baseDefinition)
    {
        _guiPresentation = guiPresentation;
        _subtitle = GetDefaultSubtitle();
    }

    public override string TooltipClass => "FeatDefinition";

    public override string Title =>
        string.IsNullOrEmpty(_title)
            ? FormatPresentation(BaseDefinition.GuiPresentation?.Title, BaseDefinition.FormatTitle)
            : NormalizeContent(_title);

    public override string Description =>
        string.IsNullOrEmpty(_description)
            ? FormatDescription(BaseDefinition)
            : NormalizeContent(_description);

    internal static string FormatDescription(BaseDefinition definition)
    {
        return definition == null
            ? string.Empty
            : FormatPresentation(
                definition.GuiPresentation?.Description,
                definition.FormatDescription);
    }

    internal static string GetActivationContent(BaseDefinition definition)
    {
        if (definition == null)
        {
            return string.Empty;
        }

        var description = NormalizeContent(definition.GuiPresentation?.Description);

        if (!string.IsNullOrEmpty(description))
        {
            return description;
        }

        var title = NormalizeContent(definition.GuiPresentation?.Title);

        return string.IsNullOrEmpty(title) ? definition.Name : title;
    }

    internal static bool IsUnavailableContent(string content)
    {
        return string.IsNullOrWhiteSpace(content) ||
               string.Equals(content.Trim(), "-", StringComparison.Ordinal) ||
               string.Equals(content, Gui.NoLocalization, StringComparison.Ordinal) ||
               string.Equals(content, Gui.EmptyContent, StringComparison.Ordinal) ||
               string.Equals(content, GuiPresentationBuilder.EmptyString, StringComparison.Ordinal);
    }

    public string EnumeratePrerequisites(RulesetCharacterHero hero)
    {
        return _prerequisites;
    }

    public string Subtitle =>
        _subtitle ??=
            GetDefaultSubtitle(); //Just in case. This is actually set in constructor + check for null in the setter.

    private string GetDefaultSubtitle()
    {
        return BaseDefinition switch
        {
            FeatureDefinitionPower => "UI/&CustomFeatureSelectionTooltipTypePower",
            FeatureDefinitionBonusCantrips => "UI/&CustomFeatureSelectionTooltipTypeCantrip",
            FeatureDefinitionProficiency => "UI/&CustomFeatureSelectionTooltipTypeProficiency",
            InvocationDefinitionCustom f => $"UI/&CustomFeatureSelectionTooltipType{f.PoolType.Name}",
            _ => "UI/&CustomFeatureSelectionTooltipTypeFeature"
        };
    }

    public override void SetupSprite(Image image, object context = null)
    {
        if (image.sprite)
        {
            ReleaseSprite(image);
            image.sprite = null;
        }

        if (_guiPresentation is { SpriteReference: not null } && _guiPresentation.SpriteReference.RuntimeKeyIsValid())
        {
            image.gameObject.SetActive(true);
            image.sprite = Gui.LoadAssetSync<Sprite>(_guiPresentation.SpriteReference);
        }
        else
        {
            image.gameObject.SetActive(false);
        }
    }

    internal void SetPrerequisites(params string[] missingRequirements)
    {
        SetPrerequisites(missingRequirements.ToList());
    }

    internal void SetPrerequisites(List<string> missingRequirements)
    {
        _prerequisites = missingRequirements == null || missingRequirements.Count == 0
            ? string.Empty
            : string.Join("\n", missingRequirements.Select(e => Gui.Localize(e)));
    }

    internal void SetSubtitle(string subtitle)
    {
        _subtitle = string.IsNullOrEmpty(subtitle)
            ? GetDefaultSubtitle()
            : subtitle;
    }

    internal void SetDescription(string description)
    {
        _description = description;
    }

    internal void SetTitle(string title)
    {
        _title = title;
    }

    private static string FormatPresentation(string key, Func<string> formatter)
    {
        return IsUnavailableContent(key)
            ? string.Empty
            : formatter();
    }

    private static string NormalizeContent(string content)
    {
        return IsUnavailableContent(content) ? string.Empty : content;
    }
}

internal class CustomItemTooltipProvider : CustomTooltipProvider,
    IArmorParametersProvider,
    IWeaponParametersProvider,
    IAmmunitionParametersProvider,
    IStarterPackParametersProvider,
    ILightSourceParametersProvider,
    IContainerParametersProvider,
    ISpellbookParametersProvider,
    IStackableParametersProvider,
    IDurationProvider,
    IItemDefinitionProvider,
    ITagsProvider,
    IEffectFormsProvider,
    IDeviceParametersProvider,
    IDeviceFunctionsEnumeratorProvider,
    IItemPropertiesEnumeratorProvider
{
    internal const string ItemWithPreReqsTooltip = "ItemWithPrereqsDefinition";

    [NotNull] private readonly GuiItemDefinition _guiItem;

    internal CustomItemTooltipProvider(BaseDefinition baseDefinition, GuiPresentation guiPresentation,
        ItemDefinition item)
        : base(baseDefinition, guiPresentation)
    {
        _guiItem = new GuiItemDefinition(item);
    }

    public override string TooltipClass => ItemWithPreReqsTooltip;
    public string AmmunitionDescription => _guiItem.AmmunitionDescription;

    //IArmorParametersProvider
    public string ArmorDescription => _guiItem.ArmorDescription;
    public bool IsContainer => _guiItem.IsContainer;
    public string ContainerWeightCapacityMultiplier => _guiItem.ContainerWeightCapacityMultiplier;

    public string FormatFunctionDescription(RulesetDeviceFunction function, RulesetCharacter character, bool inCombat)
    {
        return _guiItem.FormatFunctionDescription(function, character, inCombat);
    }

    public bool FunctionListIsKnown => _guiItem.FunctionListIsKnown;
    public bool HasUsableFunctions => _guiItem.HasUsableFunctions;
    public List<DeviceFunctionDescription> FunctionDescriptions => _guiItem.FunctionDescriptions;
    public List<RulesetDeviceFunction> UsableFunctions => _guiItem.UsableFunctions;

    public bool CanAccessDeviceParameters => _guiItem.CanAccessDeviceParameters;
    public EquipmentDefinitions.ItemUsage Usage => _guiItem.Usage;
    public string UsageText => _guiItem.UsageText;
    public string Charges => _guiItem.Charges;
    public string Recharge => _guiItem.Recharge;

    public string AttunementInfo => _guiItem.AttunementInfo;
    public bool DynamicDuration => _guiItem.DynamicDuration;
    public string DurationDescription => _guiItem.DurationDescription;
    public bool VersatileOnFirstDamage => _guiItem.VersatileOnFirstDamage;
    public bool HasSavingThrow => _guiItem.HasSavingThrow;
    public string EffectsHeader => _guiItem.EffectsHeader;
    public int RangeParameter => _guiItem.RangeParameter;
    public bool ForceTight => _guiItem.ForceTight;
    public EffectApplication EffectApplication => _guiItem.EffectApplication;
    public string SpecialFormsDescription => _guiItem.SpecialFormsDescription;
    public List<EffectForm> EffectForms => _guiItem.EffectForms;
    public ItemDefinition ItemDefinition => _guiItem.ItemDefinition;
    public string BaseDamageType => _guiItem.BaseDamageType;

    public bool IsAttunementValid(RulesetCharacter character)
    {
        return _guiItem.IsAttunementValid(character);
    }

    public bool HasProperties => _guiItem.HasProperties;
    public bool PropertyListIsKnown => _guiItem.PropertyListIsKnown;
    public bool IsUsableDevice => _guiItem.IsUsableDevice;

    public List<ItemPropertyDescription> PropertiesList => _guiItem.PropertiesList;
    public string LightSourceDescription => _guiItem.LightSourceDescription;
    public string SpellbookDescription => _guiItem.SpellbookDescription;
    public string StackableDescription => _guiItem.StackableDescription;
    public string StarterPackDescription => _guiItem.StarterPackDescription;

    public Dictionary<string, TagsDefinitions.Criticity> EnumerateTags(object context)
    {
        return _guiItem.EnumerateTags(context);
    }

    //IWeaponParametersProvider
    public bool IsWeapon => _guiItem.IsWeapon;
    public string WeaponInfoHeader => _guiItem.WeaponInfoHeader;
    public float ReachDistance => _guiItem.ReachDistance;
    public float CloseRangeDistance => _guiItem.CloseRangeDistance;
    public float MaxRangeDistance => _guiItem.MaxRangeDistance;
    public int AttackRollModifier => _guiItem.AttackRollModifier;
    public int DamageRollModifier => _guiItem.DamageRollModifier;
}

internal interface ILiveMonsterAttacksProvider
{
    IReadOnlyList<RulesetAttackMode> LiveAttackModes { get; }
}

internal sealed class LiveFriendlyMonsterTooltipProvider(
    GuiMonsterDefinition definition,
    RulesetCharacterMonster character,
    IImageProvider imageProvider) :
    ITitleProvider,
    IImageProvider,
    IDescriptionProvider,
    ISubTitleProvider,
    IMonsterBasicInfoProvider,
    IMonsterAttacksProvider,
    ILiveMonsterAttacksProvider
{
    private readonly Dictionary<Image, bool> _originalPreserveAspect = [];

    internal RulesetCharacterMonster Character { get; } = character;
    internal IImageProvider ImageProvider { get; } = imageProvider;

    public string Title =>
        Character is RulesetCharacterSimulacrum duplicate &&
        SimulacrumBehavior.TryGetDisplayName(duplicate, out var displayName)
            ? displayName
            : definition.Title;
    public string Subtitle => definition.Subtitle;
    public string Description => definition.Description;
    public int ArmorClass => Character.TryGetAttributeValue(AttributeDefinitions.ArmorClass);
    public int HitPoints => Character.TryGetAttributeValue(AttributeDefinitions.HitPoints);
    public int HitPointsUnaltered => HitPoints;
    public string MoveModesString => FormatLiveMoveModes();
    public float ChallengeRating => definition.ChallengeRating;
    public int KnowledgeLevel => 4;
    public List<MonsterAttackIteration> AttackIterations => definition.AttackIterations;
    public IReadOnlyList<RulesetAttackMode> LiveAttackModes => Character.AttackModes;

    public string GetDisplayName(object context)
    {
        return Title;
    }

    private string FormatLiveMoveModes()
    {
        var orderedMoveModes = new Dictionary<int, int>();

        foreach (var moveMode in definition.MonsterDefinition.Features
                     .OfType<FeatureDefinitionMoveMode>())
        {
            var key = (int)moveMode.MoveMode;

            if (Character.MoveModes.TryGetValue(key, out var speed))
            {
                orderedMoveModes[key] = speed;
            }
        }

        foreach (var pair in Character.MoveModes)
        {
            orderedMoveModes[pair.Key] = pair.Value;
        }

        return Gui.FormatMoveModes(orderedMoveModes, Character, false, -1);
    }

    public void SetupSprite(Image image, object context)
    {
        if (image && !_originalPreserveAspect.ContainsKey(image))
        {
            _originalPreserveAspect.Add(image, image.preserveAspect);
        }

        if (ImageProvider != null)
        {
            ImageProvider.SetupSprite(image, context);
        }
        else
        {
            definition.SetupSprite(image, context);
        }

        if (image)
        {
            image.preserveAspect = true;
        }
    }

    public void ReleaseSprite(Image image)
    {
        if (ImageProvider != null)
        {
            ImageProvider.ReleaseSprite(image);
        }
        else
        {
            definition.ReleaseSprite(image);
        }

        if (image && _originalPreserveAspect.TryGetValue(image, out var preserveAspect))
        {
            image.preserveAspect = preserveAspect;
            _originalPreserveAspect.Remove(image);
        }
    }

    public bool CanAccess(BestiaryDefinitions.BestiaryAccess access)
    {
        return true;
    }
}
