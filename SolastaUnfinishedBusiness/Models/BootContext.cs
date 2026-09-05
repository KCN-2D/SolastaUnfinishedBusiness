using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Displays;
using SolastaUnfinishedBusiness.ItemCrafting;
#if DEBUG
using SolastaUnfinishedBusiness.DataMiner;
#endif

namespace SolastaUnfinishedBusiness.Models;

internal static class BootContext
{
    private static bool RuntimeInitialized { get; set; }
    private static bool StartupStarted { get; set; }
    private static GameManager CoroutineHost { get; set; }
    private static IRuntimeService RuntimeService { get; set; }
    private static Runtime.RuntimeLoadedHandler RuntimeLoadedHandler { get; set; }
    private static CancellationTokenSource UserCampaignAuditCancellation { get; set; }

    internal static void Startup(GameManager gameManager)
    {
        if (!gameManager || StartupStarted || Main.Enable == null)
        {
            return;
        }

        StartupStarted = true;
        CoroutineHost = gameManager;

#if DEBUG
        ItemDefinitionVerification.Load();
        EffectFormVerification.Load();
#endif
        CampaignsContext.ModifyActionMaps();

        // STEP 0: Cache TA definitions for diagnostics and export
        DiagnosticsContext.CacheTaDefinitions();

        // Load Portraits, Translations and Resources Locator after
        SpeechContext.Load();
        TranslatorContext.Load();
        ResourceLocatorHelper.Load();

        // Fixes spell slots and progressions early on
        FixesContext.Load();

        // Create our Content Pack for anything that gets further created
        CeContentPackContext.Load();
        CustomActionIdContext.Load();

        // Cache all Merchant definitions and what item types they sell
        MerchantTypeContext.Load();

        // Custom Conditions must load as early as possible
        CustomConditionsContext.Load();

        //
        // custom stuff that can be loaded in any order
        //

        CustomReactionsContext.Load();
        CustomWeaponsContext.Load();
        CustomItemsContext.Load();
        PowerBundleContext.Load();
        ToolsContext.Load();
        CharacterExportContext.Load();
        DungeonMakerContext.Load();
        CampaignsContext.Load();
        InputContext.Load();

        // only bootstrap for now
        FeatsContext.Load();

        // Fighting Styles must be loaded before feats to allow feats to generate corresponding fighting style ones.
        FightingStyleContext.Load();

        // Backgrounds may rely on spells and powers being in the DB before they can properly load.
        BackgroundsContext.Load();

        // Races may rely on spells and powers being in the DB before they can properly load.
        RacesContext.Load();

        // Classes may rely on spells and powers being in the DB before they can properly load.
        ClassesContext.Load();

        // Subclasses may rely on spells and powers being in the DB before they can properly load.
        SubclassesContext.Load();

        // Level 20 must always load after classes and subclasses
        Level20Context.Load();

        // Item Options must be loaded after Item Crafting
        ItemCraftingMerchantContext.Load();
        RecipeHelper.AddRecipeIcons();

        MerchantContext.Load();

        RuntimeService = ServiceRepository.GetService<IRuntimeService>();

        if (RuntimeService == null)
        {
            Main.Error("Runtime service is unavailable during mod startup.");

            return;
        }

        RuntimeLoadedHandler = OnRuntimeLoaded;
        RuntimeService.RuntimeLoaded += RuntimeLoadedHandler;
    }

    internal static void Unload()
    {
        DetachRuntimeLoadedHandler();

        var cancellation = UserCampaignAuditCancellation;

        UserCampaignAuditCancellation = null;
        CoroutineHost = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private static void DetachRuntimeLoadedHandler()
    {
        var runtimeService = RuntimeService;
        var runtimeLoadedHandler = RuntimeLoadedHandler;

        RuntimeService = null;
        RuntimeLoadedHandler = null;

        if (runtimeService != null && runtimeLoadedHandler != null)
        {
            runtimeService.RuntimeLoaded -= runtimeLoadedHandler;
        }
    }

    private static void OnRuntimeLoaded(Runtime _)
    {
        DetachRuntimeLoadedHandler();

        var enable = Main.Enable;

        if (RuntimeInitialized || enable == null)
        {
            return;
        }

        RuntimeInitialized = true;

        // There are feats that need all character classes loaded before they can properly be setup.
        FeatsContext.LateLoad();

        // Late initialized to allow feats and races from other mods
        RulesContext.LateLoad();

        // Custom invocations
        InvocationsContext.LateLoad();

        // Custom metamagic
        MetamagicContext.LateLoad();

        // Action Switching
        ActionSwitching.LateLoad();

        // Vanilla Fixes
        FixesContext.LateLoad();

        // Level 20 - patching and final configs
        Level20Context.LateLoad();

        // Multiclass - patching and final configs
        MulticlassContext.LateLoad();

        // Spells context need Level 20 and Multiclass to properly register spells
        SpellsContext.LateLoad();

        // Shared Slots - patching and final configs
        SharedSpellsContext.LateLoad();

        // Set anything on subs that depends on spells and others
        Tabletop2014Context.LateLoad();
        Tabletop2024Context.LateLoad();

        SubclassesContext.LateLoad();
        InventorClass.LateLoadSpellStoringItem();
        LightingAndObscurementContext.LateLoad();
        GrappleContext.LateLoad();
        ScrollsData.LateLoad();
        CustomizedWeaponTypesContext.LateLoad();

        // Spell Points should load closer to the bottom after all other blueprints initiated
        SpellPointsContext.LateLoad();

        // Save by location initialization depends on services to be ready
        SaveByLocationContext.LateLoad();

        MovementSuspensionContext.LateLoad();

        // Recache all gui collections
        GuiWrapperContext.Recache();

        // Cache CE definitions for diagnostics and export
        DiagnosticsContext.CacheCeDefinitions();

        // Dump documentations to mod folder when version or files changed
        DocumentationContext.DumpDocumentationIfNeeded();
        ModUi.LoadTabletopDefinitions();

        // Manages update or welcome messages
        UpdateContext.Load();

        //TODO: find a better place to implement these
        AddExtraTooltipDefinitions();

        // Enable mod before optional diagnostics parse user-authored data.
        enable();
        StartMissingReferencesAudit();
    }

    private static void AddExtraTooltipDefinitions()
    {
        if (ServiceRepository.GetService<IGuiService>() is not GuiManager gui)
        {
            return;
        }

        var definition = gui.tooltipClassDefinitions[GuiFeatDefinition.tooltipClass];

        var index = definition.tooltipFeatures.FindIndex(f =>
            f.scope == TooltipDefinitions.Scope.All &&
            f.featurePrefab.GetComponent<TooltipFeature>() is TooltipFeaturePrerequisites);

        if (index >= 0)
        {
            var custom = GuiTooltipClassDefinitionBuilder
                .Create(gui.tooltipClassDefinitions["ItemDefinition"], CustomItemTooltipProvider.ItemWithPreReqsTooltip)
                .SetGuiPresentationNoContent()
                .AddTooltipFeature(definition.tooltipFeatures[index])
                //TODO: figure out why only background widens, but not content
                // .SetPanelWidth(400f) //items have 340f by default
                .AddToDB();

            gui.tooltipClassDefinitions.Add(custom.Name, custom);
        }

        //make condition description visible on both modes
        definition = gui.tooltipClassDefinitions[GuiActiveCondition.tooltipClass];
        index = definition.tooltipFeatures.FindIndex(f =>
            f.scope == TooltipDefinitions.Scope.Simplified &&
            f.featurePrefab.GetComponent<TooltipFeature>() is TooltipFeatureDescription);

        if (index < 0)
        {
            return;
        }

        //since FeatureInfo is a struct we get here a copy
        var info = definition.tooltipFeatures[index];
        //modify it
        info.scope = TooltipDefinitions.Scope.All;
        //and then put copy back
        definition.tooltipFeatures[index] = info;
    }

    private static void StartMissingReferencesAudit()
    {
        if (!Main.Settings.EnableLoggingInvalidReferencesInUserCampaigns)
        {
            return;
        }

        var coroutineHost = CoroutineHost;

        if (!coroutineHost)
        {
            Main.Error("Cannot start the user campaign reference audit without a game manager.");

            return;
        }

        var cancellation = new CancellationTokenSource();

        UserCampaignAuditCancellation?.Cancel();
        UserCampaignAuditCancellation?.Dispose();
        UserCampaignAuditCancellation = cancellation;

        coroutineHost.StartCoroutine(LogMissingReferencesInUserCampaigns(cancellation.Token));
    }

    private static IEnumerator LogMissingReferencesInUserCampaigns(CancellationToken cancellationToken)
    {
        string[] userCampaigns = null;

        try
        {
            userCampaigns = Directory.GetFiles(TacticalAdventuresApplication.UserCampaignsDirectory);
        }
        catch
        {
            Main.Error("Cannot enumerate user campaigns for the reference audit.");
        }

        if (userCampaigns == null)
        {
            CompleteMissingReferencesAudit(cancellationToken);

            yield break;
        }

        foreach (var userCampaign in userCampaigns)
        {
            var task = Task.Run(
                () => ReadUserCampaignReferences(userCampaign, cancellationToken),
                cancellationToken);

            while (!task.IsCompleted)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                yield return null;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (task.IsFaulted || task.IsCanceled || task.Result == null)
            {
                Main.Error($"User campaign {Path.GetFileName(userCampaign)} is really messed up.");

                continue;
            }

            var references = task.Result;

            if (references.IsInvalid)
            {
                Main.Error($"User campaign {references.CampaignName} is really messed up.");

                continue;
            }

            LogMissingReferencesInUserCampaign<ItemDefinition>(
                references.CampaignName,
                references.ItemReferences,
                "item");
            LogMissingReferencesInUserCampaign<MonsterDefinition>(
                references.CampaignName,
                references.MonsterReferences,
                "monster");

            yield return null;
        }

        CompleteMissingReferencesAudit(cancellationToken);
    }

    private static void LogMissingReferencesInUserCampaign<TDefinition>(
        string campaignName,
        IEnumerable<string> references,
        string referenceKind)
        where TDefinition : BaseDefinition
    {
        var database = DatabaseRepository.GetDatabase<TDefinition>();
        var checkedReferences = new HashSet<string>();

        foreach (var referenceDefinition in references)
        {
            if (string.IsNullOrWhiteSpace(referenceDefinition) ||
                !checkedReferences.Add(referenceDefinition) ||
                database.TryGetElement(referenceDefinition, out _))
            {
                continue;
            }

            Main.Error(
                $"User campaign {campaignName} has an invalid {referenceKind} reference: {referenceDefinition}");
        }
    }

    private static UserCampaignReferenceAudit ReadUserCampaignReferences(
        string userCampaign,
        CancellationToken cancellationToken)
    {
        var campaignName = Path.GetFileName(userCampaign);

        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(userCampaign);
            using var textReader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(textReader);
            var infoJson = JObject.Load(jsonReader);

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            return new UserCampaignReferenceAudit(
                campaignName,
                GetReferenceDefinitions(infoJson, "userItems"),
                GetReferenceDefinitions(infoJson, "userMonsters"),
                false);
        }
        catch
        {
            return new UserCampaignReferenceAudit(campaignName, [], [], true);
        }
    }

    private static string[] GetReferenceDefinitions(JObject infoJson, string collectionName)
    {
        return infoJson[collectionName] is JArray references
            ? references
                .Select(reference => reference["referenceDefinition"]?.Value<string>())
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .ToArray()
            : [];
    }

    private static void CompleteMissingReferencesAudit(CancellationToken cancellationToken)
    {
        var cancellation = UserCampaignAuditCancellation;

        if (cancellation == null || cancellation.Token != cancellationToken)
        {
            return;
        }

        UserCampaignAuditCancellation = null;
        cancellation.Dispose();
    }

    private sealed class UserCampaignReferenceAudit
    {
        internal UserCampaignReferenceAudit(
            string campaignName,
            string[] itemReferences,
            string[] monsterReferences,
            bool isInvalid)
        {
            CampaignName = campaignName;
            ItemReferences = itemReferences;
            MonsterReferences = monsterReferences;
            IsInvalid = isInvalid;
        }

        internal string CampaignName { get; }
        internal string[] ItemReferences { get; }
        internal bool IsInvalid { get; }
        internal string[] MonsterReferences { get; }
    }
}
