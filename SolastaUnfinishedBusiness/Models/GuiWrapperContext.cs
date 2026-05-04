namespace SolastaUnfinishedBusiness.Models;

internal static class GuiWrapperContext
{
    private static bool GuiWrapperRuntimeLoaded;
    private static bool PendingFullRecache;
    private static bool PendingFeatRecache;
    private static bool PendingInvocationRecache;

    internal static void Recache()
    {
        var guiWrapperService = ServiceRepository.GetService<IGuiWrapperService>();
        var runtimeService = ServiceRepository.GetService<IRuntimeService>();

        if (guiWrapperService is not GuiWrapperManager guiWrapperManager)
        {
            return;
        }

        if (!GuiWrapperRuntimeLoaded)
        {
            PendingFullRecache = true;
#if DEBUG
            LogRecacheState("full", guiWrapperManager, true);
#endif
            return;
        }

        if (runtimeService?.Runtime == null)
        {
            return;
        }

        PendingFullRecache = false;

#if DEBUG
        LogRecacheState("full", guiWrapperManager, false);
#endif

        guiWrapperManager.classDefinitionsMap.Clear();
        guiWrapperManager.featDefinitionsMap.Clear();
        guiWrapperManager.raceDefinitionsMap.Clear();
        guiWrapperManager.monsterDefinitionsMap.Clear();
        guiWrapperManager.merchantDefinitionsMap.Clear();
        guiWrapperManager.itemDefinitionsMap.Clear();
        guiWrapperManager.invocationDefinitionsMap.Clear();
        guiWrapperManager.spellDefinitionsMap.Clear();
        guiWrapperManager.effectProxyDefinitionsMap.Clear();
        guiWrapperManager.powerDefinitionsMap.Clear();
        guiWrapperManager.toolTypeDefinitionsMap.Clear();
        guiWrapperManager.recipeDefinitionsMap.Clear();
        guiWrapperManager.factionDefinitionsMap.Clear();
        guiWrapperManager.environmentEffectDefinitionsMap.Clear();

        guiWrapperManager.RuntimeLoaded(runtimeService.Runtime);
    }

    internal static void RecacheFeats()
    {
        if (ServiceRepository.GetService<IGuiWrapperService>() is not GuiWrapperManager guiWrapperManager)
        {
            return;
        }

        if (!GuiWrapperRuntimeLoaded)
        {
            PendingFeatRecache = true;
#if DEBUG
            LogRecacheState("feats", guiWrapperManager, true);
#endif
            return;
        }

        if (guiWrapperManager.featDefinitionsMap == null)
        {
            return;
        }

        PendingFeatRecache = false;

#if DEBUG
        LogRecacheState("feats", guiWrapperManager, false);
#endif

        guiWrapperManager.featDefinitionsMap.Clear();
        guiWrapperManager.LoadFeatDefinitions();
    }

    internal static void RecacheInvocations()
    {
        if (ServiceRepository.GetService<IGuiWrapperService>() is not GuiWrapperManager guiWrapperManager)
        {
            return;
        }

        if (!GuiWrapperRuntimeLoaded)
        {
            PendingInvocationRecache = true;
#if DEBUG
            LogRecacheState("invocations", guiWrapperManager, true);
#endif
            return;
        }

        if (guiWrapperManager.invocationDefinitionsMap == null)
        {
            return;
        }

        PendingInvocationRecache = false;

#if DEBUG
        LogRecacheState("invocations", guiWrapperManager, false);
#endif

        guiWrapperManager.invocationDefinitionsMap.Clear();
        guiWrapperManager.LoadInvocationDefinitions();
    }

    internal static void OnGuiWrapperRuntimeLoaded(GuiWrapperManager guiWrapperManager)
    {
        GuiWrapperRuntimeLoaded = true;
        var doFullRecache = PendingFullRecache;
        var doFeatRecache = PendingFeatRecache;
        var doInvocationRecache = PendingInvocationRecache;

#if DEBUG
        Main.Log(
            $"GuiWrapper runtime loaded: pendingFull={doFullRecache}, pendingFeats={doFeatRecache}, " +
            $"pendingInvocations={doInvocationRecache}, featCount={guiWrapperManager?.featDefinitionsMap?.Count ?? -1}");
#endif

        PendingFullRecache = false;
        PendingFeatRecache = false;
        PendingInvocationRecache = false;

        if (doFullRecache)
        {
            Recache();

            return;
        }

        if (doFeatRecache)
        {
            RecacheFeats();
        }

        if (doInvocationRecache)
        {
            RecacheInvocations();
        }
    }

    internal static void Unload()
    {
        GuiWrapperRuntimeLoaded = false;
        PendingFullRecache = false;
        PendingFeatRecache = false;
        PendingInvocationRecache = false;
    }

#if DEBUG
    private static void LogRecacheState(string kind, GuiWrapperManager guiWrapperManager, bool deferred)
    {
        Main.Log(
            $"GuiWrapper recache requested: kind={kind}, deferred={deferred}, runtimeLoaded={GuiWrapperRuntimeLoaded}, " +
            $"pendingFull={PendingFullRecache}, pendingFeats={PendingFeatRecache}, pendingInvocations={PendingInvocationRecache}, " +
            $"featCount={guiWrapperManager?.featDefinitionsMap?.Count ?? -1}, invocationCount={guiWrapperManager?.invocationDefinitionsMap?.Count ?? -1}");
    }
#endif
}
