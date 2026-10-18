using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Builders;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace SolastaUnfinishedBusiness.CustomUI;

internal class CustomInvocationSelectionPanel : CharacterStagePanel
{
    private const float ScrollDuration = 0.3f;
    private const float SpellsByLevelMargin = 10.0f;

    private readonly List<FeaturePool> _allPools = [];
    private readonly List<(string, FeatureDefinitionCustomInvocationPool)> _gainedCustomFeatures = [];

    private readonly Dictionary<PoolId, List<InvocationDefinitionCustom>> _learnedInvocations = [];

    private readonly Comparison<FeaturePool> _poolCompare = (a, b) =>
    {
        var r = a.Type.SelectionOrder.CompareTo(b.Type.SelectionOrder);

        if (r != 0)
        {
            return r;
        }

        r = string.CompareOrdinal(a.Id.Tag, b.Id.Tag);

        if (r != 0)
        {
            return r;
        }

        if (a.IsUnlearn == b.IsUnlearn)
        {
            return string.CompareOrdinal(a.Id.Name, b.Id.Name);
        }

        if (a.IsUnlearn)
        {
            return -1;
        }

        return 1;
    };

    private int _currentLearnStep;
    private int _gainedCharacterLevel;
    private CharacterClassDefinition _gainedClass;
    private int _gainedClassLevel;
    private CharacterSubclassDefinition _gainedSubclass;
    private bool _wasClicked;
    private RulesetCharacter _selectionCharacter;
    private InvocationPoolTypeCustom _selectionPool;
    private string _selectionTitle;
    private Func<InvocationDefinitionCustom, bool> _canSelectInvocation;
    private Action<InvocationDefinitionCustom> _invocationSelected;
    private bool _standaloneSelection;
    private InvocationDefinitionCustom _pendingInvocation;
    private InvocationReplacementPresentation _replacementPresentation;

    internal static void SelectInvocation(RulesetCharacter character, InvocationPoolTypeCustom pool, string title,
        InvocationReplacementPresentation presentation,
        Func<InvocationDefinitionCustom, bool> canSelect, Action<InvocationDefinitionCustom> selected)
    {
        InvocationSelectionModal.Get().Bind(character, pool, title, presentation, canSelect, selected);
    }

    internal sealed class InvocationReplacementPresentation(
        InvocationDefinitionCustom source, string sourceTitle, string replacementTitle, string description,
        string confirmTitle, Func<InvocationDefinitionCustom, string> formatDetails)
    {
        internal InvocationDefinitionCustom Source => source;
        internal string SourceTitle => sourceTitle;
        internal string ReplacementTitle => replacementTitle;
        internal string Description => description;
        internal string ConfirmTitle => confirmTitle;
        internal string FormatDetails(InvocationDefinitionCustom invocation) => formatDetails(invocation);
    }

    private bool IsFinalStep => _currentLearnStep >= _allPools.Count;

    private void OnFeatureSelected(SpellBox spellBox)
    {
        if (_selectionCharacter != null)
        {
            var selected = spellBox.GetFeature();
            if (!_wasClicked && selected != null && _canSelectInvocation(selected))
            {
                _pendingInvocation = selected;
                InvocationSelectionModal.Get().RefreshSelection();
            }
            return;
        }

        if (_wasClicked || IsFinalStep)
        {
            return;
        }

        var feature = spellBox.GetFeature();
        var pool = _allPools[_currentLearnStep];

        if (feature == null || feature.PoolType != pool.Type)
        {
            return;
        }

        var learned = GetOrMakeLearnedList(pool.Id);

        if (!pool.IsUnlearn && !learned.Contains(feature) &&
            (pool.Remaining <= 0 ||
             currentHero.TrainedInvocations.Contains(feature) ||
             !PowerBundle.ValidatePrerequisites(currentHero, feature, feature.Validators, out _)))
        {
            return;
        }

        _wasClicked = true;

        if (learned.Contains(feature))
        {
            pool.Used--;
            learned.Remove(feature);

            if (pool.IsUnlearn)
            {
                var poolById = GetPoolById(new PoolId(pool.Id.Name, pool.Id.Tag, false));

                if (poolById != null)
                {
                    poolById.Max--;
                }
            }
        }
        else
        {
            pool.Used++;
            learned.Add(feature);

            if (pool.IsUnlearn)
            {
                GetOrAddPoolById(new PoolId(pool.Id.Name, pool.Id.Tag, false), pool.Type).Max++;
            }
        }

        GrantAcquiredFeatures(() =>
        {
            CommonData.AbilityScoresListingPanel.RefreshNow();
            CommonData.CharacterStatsPanel.RefreshNow();

            // don't use ? on Unity Objects
#pragma warning disable IDE0031
            if (CommonData.AttackModesPanel)
#pragma warning restore IDE0031
            {
                CommonData.AttackModesPanel.RefreshNow();
            }

            // don't use ? on Unity Objects
#pragma warning disable IDE0031
            if (CommonData.PersonalityMapPanel)
#pragma warning restore IDE0031
            {
                CommonData.PersonalityMapPanel.RefreshNow();
            }

            OnPreRefresh();
            RefreshNow();

            if (pool.Remaining == 0)
            {
                MoveToNextLearnStep();
            }

            ResetWasClickedFlag();
        });
    }

    private void BuildLearnSteps()
    {
        // Register all steps
        if (_allPools is not { Count: > 0 })
        {
            return;
        }

        while (_learnStepsTable.childCount < _allPools.Count)
        {
            Gui.GetPrefabFromPool(_learnStepPrefab, _learnStepsTable);
        }

        for (var i = 0; i < _learnStepsTable.childCount; i++)
        {
            var child = _learnStepsTable.GetChild(i);

            if (i < _allPools.Count)
            {
                var learnStepItem = child.GetComponent<LearnStepItem>();

                child.gameObject.SetActive(true);
                learnStepItem.CustomBind(i, _allPools[i],
                    OnLearnBack,
                    OnLearnReset,
                    OnSkipRemaining
                );
            }
            else
            {
                child.gameObject.SetActive(false);
            }
        }
    }


    private void OnLearnBack()
    {
        if (_wasClicked)
        {
            return;
        }

        _wasClicked = true;

        MoveToPreviousLearnStep();
    }

    private void OnLearnReset()
    {
        if (_wasClicked)
        {
            return;
        }

        _wasClicked = true;

        if (IsFinalStep)
        {
            _currentLearnStep = _allPools.Count - 1;
        }

        ResetLearnings(_currentLearnStep, () =>
        {
            OnPreRefresh();
            RefreshNow();
            ResetWasClickedFlag();
        });
    }

    private bool CanSkipRemaining(FeaturePool pool)
    {
        if (pool.IsUnlearn)
        {
            return true;
        }

        if (!pool.Type.AllowExhaustedSelection || pool.Used >= pool.Max)
        {
            return false;
        }

        // A shortage of eligible proficiencies may be skipped, but available choices must be used first.
        var selected = new HashSet<InvocationDefinitionCustom>(_learnedInvocations
            .Where(entry => !entry.Key.Unlearn)
            .SelectMany(entry => entry.Value));

        return !pool.Type.AllLevels
            .SelectMany(pool.Type.GetLevelFeatures)
            .Any(feature => !selected.Contains(feature) &&
                            !currentHero.TrainedInvocations.Contains(feature) &&
                            PowerBundle.ValidatePrerequisites(currentHero, feature, feature.Validators, out _));
    }

    private void OnSkipRemaining()
    {
        if (_wasClicked || IsFinalStep || !CanSkipRemaining(_allPools[_currentLearnStep]))
        {
            return;
        }

        _wasClicked = true;
        _allPools[_currentLearnStep].Skipped = true;
        MoveToNextLearnStep();
        ResetWasClickedFlag();
    }

    private void ResetLearnings(int stepNumber, Action onDone = null)
    {
        if (stepNumber < 0 || stepNumber >= _allPools.Count)
        {
            onDone?.Invoke();
            return;
        }

        var pool = _allPools[stepNumber];

        pool.Used = 0;
        pool.Skipped = false;
        GetOrMakeLearnedList(pool.Id).Clear();
        GrantAcquiredFeatures(onDone);
    }

    private void MoveToNextLearnStep()
    {
        _currentLearnStep++;

        while (!IsFinalStep && _allPools[_currentLearnStep].Remaining == 0)
        {
            _currentLearnStep++;
        }

        LevelSelected(0);
        OnPreRefresh();
        RefreshNow();
    }

    private void MoveToPreviousLearnStep()
    {
        var heroBuildingCommandService = ServiceRepository.GetService<IHeroBuildingCommandService>();

        if (_currentLearnStep > 0)
        {
            if (!IsFinalStep)
            {
                ResetLearnings(_currentLearnStep);
            }

            _currentLearnStep--;
            ResetLearnings(_currentLearnStep);

            if (IsUnlearnStep(_currentLearnStep))
            {
                heroBuildingCommandService.AcknowledgePreviousCharacterBuildingCommandLocally(() =>
                {
                    CollectTags();
                    BuildLearnSteps();
                });
            }
        }

        heroBuildingCommandService.AcknowledgePreviousCharacterBuildingCommandLocally(() =>
        {
            LevelSelected(0);
            OnPreRefresh();
            RefreshNow();
            ResetWasClickedFlag();
        });
    }


    private void GrantAcquiredFeatures(Action onDone = null)
    {
        var hero = currentHero;
        var heroBuildingData = hero.GetHeroBuildingData();
        var command = ServiceRepository.GetService<IHeroBuildingCommandService>();

        //remove all trained custom invocations
        var entries = heroBuildingData.levelupTrainedInvocations.ToArray();

        foreach (var entry in entries)
        {
            var currentTag = entry.Key;
            var invocations = new List<InvocationDefinition>(entry.Value);

            foreach (var invocation in invocations)
            {
                if (invocation is not InvocationDefinitionCustom custom)
                {
                    continue;
                }

                command.UntrainCharacterFeature(hero, currentTag, custom.Name,
                    HeroDefinitions.PointsPoolType.Invocation);
            }
        }

        //remove all unlearned custom invocations
        entries = [.. heroBuildingData.unlearnedInvocations];

        foreach (var entry in entries)
        {
            var currentTag = entry.Key;
            var invocations = new List<InvocationDefinition>(entry.Value);

            foreach (var invocation in invocations)
            {
                if (invocation is not InvocationDefinitionCustom custom)
                {
                    continue;
                }

                command.UndoUnlearnInvocation(hero, currentTag, custom.Name);
            }
        }

        //add all learned/unlearned custom invocations
        foreach (var keyValuePair in _learnedInvocations)
        {
            var poolId = keyValuePair.Key;

            foreach (var invocation in keyValuePair.Value)
            {
                if (poolId.Unlearn)
                {
                    command.UnlearnInvocation(hero, poolId.Tag, invocation.Name);
                }
                else
                {
                    command.TrainCharacterFeature(hero, poolId.Tag, invocation.Name,
                        HeroDefinitions.PointsPoolType.Invocation);
                }
            }
        }

        command.RefreshHero(currentHero);
        command.AcknowledgePreviousCharacterBuildingCommandLocally(() =>
        {
            RemoveInvalidFeatures(onDone);
        });
    }

    private void RemoveInvalidFeatures(Action onDone = null)
    {
        var dirty = false;

        foreach (var e in _learnedInvocations)
        {
            var id = e.Key;
            var learned = e.Value;
            var pool = GetPoolById(id);

            for (var i = 0; i < learned.Count;)
            {
                var feature = learned[i];

                if (!PowerBundle.ValidatePrerequisites(currentHero, feature, feature.Validators, out _))
                {
                    dirty = true;
                    learned.RemoveAt(i);
                    pool.Used--;
                }
                else
                {
                    i++;
                }
            }
        }

        if (dirty)
        {
            GrantAcquiredFeatures(onDone);
        }
        else
        {
            onDone?.Invoke();
        }
    }

    private FeaturePool GetPoolById(PoolId id)
    {
        return _allPools.FirstOrDefault(p => p.Id.Equals(id));
    }

    private FeaturePool GetOrAddPoolById(PoolId id, InvocationPoolTypeCustom type)
    {
        var pool = GetPoolById(id);

        if (pool != null)
        {
            return pool;
        }

        pool = new FeaturePool(id) { Type = type, Max = 0, Used = 0 };

        _allPools.Add(pool);
        _allPools.Sort(_poolCompare);
        BuildLearnSteps();

        return pool;
    }

    private bool IsUnlearnStep(int step)
    {
        return _allPools[step].IsUnlearn;
    }

    private List<InvocationDefinitionCustom> GetOrMakeLearnedList(PoolId id)
    {
        if (_learnedInvocations.TryGetValue(id, out var value))
        {
            return value;
        }

        var learned = new List<InvocationDefinitionCustom>();

        _learnedInvocations.Add(id, learned);

        return learned;
    }

    private List<InvocationDefinitionCustom> GetOrMakeUnlearnedList(PoolId id)
    {
        return GetOrMakeLearnedList(new PoolId(id.Name, id.Tag, true));
    }


    private void UpdateGainedClassDetails()
    {
        // Determine the last class and level
        CharacterBuildingService.GetLastAssignedClassAndLevel(currentHero, out _gainedClass, out _gainedClassLevel);
        _gainedCharacterLevel = currentHero.TryGetAttributeValue(AttributeDefinitions.CharacterLevel);

        if (!_gainedClass)
        {
            return;
        }

        // Was there already a subclass?
        _gainedSubclass = null;

        if (currentHero.ClassesAndSubclasses.TryGetValue(_gainedClass, out var subclass))
        {
            _gainedSubclass = subclass;
        }
    }

    private string GetClassTag()
    {
        return AttributeDefinitions.GetClassTag(_gainedClass, _gainedClassLevel);
    }

    private string GetSubClassTag()
    {
        return !_gainedSubclass
            ? null
            : AttributeDefinitions.GetSubclassTag(_gainedClass, _gainedClassLevel, _gainedSubclass);
    }

    private void CollectTags()
    {
        Dictionary<PoolId, FeaturePool> tags = [];

        UpdateGrantedFeatures();
        _allPools.Clear();

        foreach (var (poolTag, featureSet) in _gainedCustomFeatures)
        {
            var poolId = new PoolId(featureSet.PoolType.Name, poolTag, featureSet.IsUnlearn);

            if (!tags.TryGetValue(poolId, out var value))
            {
                value = new FeaturePool(poolId) { Max = featureSet.Points, Used = 0, Type = featureSet.PoolType };

                tags.Add(poolId, value);
                _allPools.Add(value);
            }
            else
            {
                value.Max += featureSet.Points;
            }
        }

        _allPools.Sort(_poolCompare);

        initialized = true;
    }

    private void UpdateGrantedFeatures()
    {
        UpdateGainedClassDetails();

        _gainedCustomFeatures.Clear();

        if (!_gainedClass)
        {
            return;
        }

        var poolTag = GetClassTag();
        
        var hero = currentHero;

        if (hero == null)
        {
            return;
        }

        // note we assume pools from feats are merged on class tags
        var heroBuildingData = hero.GetHeroBuildingData();

        // add from feats
        _gainedCustomFeatures.AddRange(heroBuildingData.LevelupTrainedFeats
            .SelectMany(x => x.Value)
            .SelectMany(f => f.Features)
            .OfType<FeatureDefinitionCustomInvocationPool>()
            .Where(x => x.PoolType != null)
            .Select(f => (poolTag, f)));

        if (poolTag != null)
        {
            _gainedCustomFeatures.AddRange(RulesetActorExtensions.FlattenFeatureList(_gainedClass.FeatureUnlocks
                    .Where(f => f.Level == _gainedClassLevel)
                    .Select(f => f.FeatureDefinition))
                .OfType<FeatureDefinitionCustomInvocationPool>()
                .Where(x => x.PoolType != null)
                .Select(f => (poolTag, f))
            );

            // add from class FS
            if (hero.TrainedFightingStyles.Count > 0 &&
                hero.ActiveFeatures.TryGetValue(poolTag, out var featureDefinitions) &&
                featureDefinitions
                    .OfType<FeatureDefinitionFightingStyleChoice>()
                    .Any())
            {
                _gainedCustomFeatures.AddRange(
                    hero.TrainedFightingStyles.Last().Features
                        .OfType<FeatureDefinitionCustomInvocationPool>()
                        .Where(x => x.PoolType != null)
                        .Select(f => (poolTag, f))
                );
            }
        }

        poolTag = GetSubClassTag();

        if (poolTag != null)
        {
            _gainedCustomFeatures.AddRange(RulesetActorExtensions.FlattenFeatureList(_gainedSubclass.FeatureUnlocks
                    .Where(f => f.Level == _gainedClassLevel)
                    .Select(f => f.FeatureDefinition))
                .OfType<FeatureDefinitionCustomInvocationPool>()
                .Where(x => x.PoolType != null)
                .Select(f => (poolTag, f))
            );

            // add from subclass FS
            if (hero.TrainedFightingStyles.Count > 0 &&
                hero.ActiveFeatures.TryGetValue(poolTag, out var featureDefinitions) &&
                featureDefinitions
                    .OfType<FeatureDefinitionFightingStyleChoice>()
                    .Any())
            {
                _gainedCustomFeatures.AddRange(
                    hero.TrainedFightingStyles.Last().Features
                        .OfType<FeatureDefinitionCustomInvocationPool>()
                        .Where(x => x.PoolType != null)
                        .Select(f => (poolTag, f))
                );
            }
        }

        var gainedRace = currentHero.RaceDefinition;

        if (gainedRace)
        {
            _gainedCustomFeatures.AddRange(RulesetActorExtensions.FlattenFeatureList(gainedRace.FeatureUnlocks
                    .Where(f => f.Level == currentHero.ClassesHistory.Count)
                    .Select(f => f.FeatureDefinition))
                .OfType<FeatureDefinitionCustomInvocationPool>()
                .Where(x => x.PoolType != null)
                .Select(f => (AttributeDefinitions.TagRace, f))
            );
        }

        InvocationPoolTypeCustom.RefreshAll();
    }

    internal class FeaturePool
    {
        internal bool Skipped;
        internal FeaturePool(PoolId id) { Id = id; }
        internal PoolId Id { get; }
        internal int Max { get; set; }
        internal int Used { get; set; }
        internal int Remaining => Skipped ? 0 : Max - Used;
        internal InvocationPoolTypeCustom Type { get; set; }
        internal bool IsUnlearn => Id.Unlearn;
    }

    internal class PoolId
    {
        internal PoolId(string name, string tag, bool unlearn)
        {
            Name = name;
            Tag = tag;
            Unlearn = unlearn;
        }

        internal string Name { get; }
        internal string Tag { get; }
        internal bool Unlearn { get; }

        public override bool Equals(object obj)
        {
            if (obj is not PoolId pool)
            {
                return false;
            }

            return Name == pool.Name && Tag == pool.Tag && Unlearn == pool.Unlearn;
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

        public override string ToString()
        {
            return $"<{Name}:[{Tag}]:{Unlearn}>";
        }
    }

    #region Fields from CharacterStageSpellSelectionPanel

    private RectTransform _spellsByLevelTable;
    private GameObject _spellsByLevelPrefab;
    private ScrollRect _spellsScrollRect;
    private RectTransform _learnStepsTable;
    private GameObject _learnStepPrefab;
    private AssetReferenceSprite _backdropReference;
    private Image _backdrop;
    private AnimationCurve _curve;
    private RectTransform _levelButtonsTable;
    private GameObject _levelButtonPrefab;
    private GuiLabel _stageTitleLabel;
    private GuiLabel _rightFeaturesLabel;
    private GuiLabel _rightFeaturesDescription;

    #endregion

    #region Initial Setup

    internal static void InsertPanel(CharacterEditionScreen screen)
    {
        switch (screen)
        {
            case CharacterCreationScreen:
            {
                var customFeatureSelection = GetPanel(screen);
                var last = screen.stagePanelsByName.ElementAt(screen.stagePanelsByName.Count - 1);

                screen.stagePanelsByName.Remove(last.Key);
                screen.stagePanelsByName.Add(customFeatureSelection.Name, customFeatureSelection);
                screen.stagePanelsByName.Add(last.Key, last.Value);
                break;
            }
            case CharacterLevelUpScreen:
            {
                var customFeatureSelection = GetPanel(screen);

                screen.stagePanelsByName.Add(customFeatureSelection.Name, customFeatureSelection);
                break;
            }
        }
    }

    [NotNull]
    private static CustomInvocationSelectionPanel GetPanel([NotNull] CharacterEditionScreen screen)
    {
        return GetPanel(screen.StagesPanelContainer);
    }

    private static CustomInvocationSelectionPanel GetPanel(Transform parent)
    {
        var characterCreationScreen = Gui.GuiService.GetScreen<CharacterCreationScreen>();
        var stagePanelPrefabs = characterCreationScreen.stagePanelPrefabs;
        var gameObject = Gui.GetPrefabFromPool(stagePanelPrefabs[8], parent);
        // OnEnable applies the scroll settings, so copy the native controls before the
        // replacement component can become enabled. Character stages and the in-game
        // chooser both activate this panel through their normal Show lifecycle.
        gameObject.SetActive(false);
        var customFeatureSelection = gameObject.AddComponent<CustomInvocationSelectionPanel>();

        customFeatureSelection.Setup();

        return customFeatureSelection;
    }

    private void Setup()
    {
        var spellsPanel = GetComponent<CharacterStageSpellSelectionPanel>();

        stageDefinition = spellsPanel.StageDefinition;
        _spellsByLevelTable = spellsPanel.spellsByLevelTable;
        _spellsByLevelPrefab = spellsPanel.spellsByLevelPrefab;
        _spellsScrollRect = spellsPanel.spellsScrollRect;
        _learnStepsTable = spellsPanel.learnStepsTable;
        _learnStepPrefab = spellsPanel.learnStepPrefab;
        _backdropReference = spellsPanel.backdropReference;
        _backdrop = spellsPanel.backdrop;
        _curve = spellsPanel.curve;
        _levelButtonsTable = spellsPanel.levelButtonsTable;
        _levelButtonPrefab = spellsPanel.levelButtonPrefab;
        _stageTitleLabel = spellsPanel.RectTransform.FindChildRecursive("ChoiceTitle").GetComponent<GuiLabel>();
        _rightFeaturesLabel =
            spellsPanel.RectTransform.FindChildRecursive("SpellsInfoTitle").GetComponent<GuiLabel>();
        _rightFeaturesDescription = spellsPanel.RectTransform.FindChildRecursive("ProficienciesIntroDescription")
            .GetComponent<GuiLabel>();
        CharacterBuildingService = ServiceRepository.GetService<ICharacterBuildingService>();
        currentHero = spellsPanel.currentHero;

        Destroy(spellsPanel);
    }

    #endregion

    #region Base Class Overrides

    public override string Name => "CustomFeatureSelection";
    public override string Title => "UI/&CustomFeatureSelectionStageTitle";
    public override string Description => "UI/&CustomFeatureSelectionStageDescription";

    public override void SetScrollSensitivity(float scrollSensitivity)
    {
        if (_spellsScrollRect)
        {
            _spellsScrollRect.scrollSensitivity = -scrollSensitivity;
        }
    }

    // A standalone chooser only borrows the stage's card presenter. Its containing
    // screen owns input and navigation; there is no character-building parent to refresh.
    public override void HandleInputControlSchemeChangedForShow(string previousScheme, string newScheme)
    {
        if (!_standaloneSelection)
        {
            base.HandleInputControlSchemeChangedForShow(previousScheme, newScheme);
        }
    }

    public override void SelectDefaultControlImpl()
    {
        if (!_standaloneSelection)
        {
            base.SelectDefaultControlImpl();
        }
    }

    public override void RegisterCommands()
    {
        if (!_standaloneSelection)
        {
            base.RegisterCommands();
        }
    }

    public override void UnregisterCommands()
    {
        if (!_standaloneSelection)
        {
            base.UnregisterCommands();
        }
    }

    public override void UpdateRelevance()
    {
        UpdateGrantedFeatures();
        IsRelevant = _gainedCustomFeatures.Count != 0;
    }

    public override void EnterStage()
    {
        _stageTitleLabel.Text = Title;
        _rightFeaturesLabel.Text = "UI/&CustomFeatureSelectionStageFeatures";
        _rightFeaturesDescription.Text = Description;
        _currentLearnStep = 0;
        initialized = false;
        CollectTags();
        OnEnterStageDone();
    }

    public override void OnBeginShow(bool instant = false)
    {
        base.OnBeginShow(instant);

        if (_selectionCharacter != null)
        {
            _learnStepsTable.gameObject.SetActive(false);
            _levelButtonsTable.gameObject.SetActive(false);
            _stageTitleLabel.Text = _selectionTitle;
            _rightFeaturesLabel.Text = _selectionPool.FormatTitle(false);
            _rightFeaturesDescription.Text = _replacementPresentation.Description;
            LayoutInvocationSelection();
            _spellsScrollRect.normalizedPosition = Vector2.zero;
            RefreshNow();
            return;
        }

        _backdrop.SetAddressableSprite(_backdropReference);
        CommonData.CharacterStatsPanel.Show(CharacterStatsPanel.ArmorClassFlag |
                                            CharacterStatsPanel.InitiativeFlag | CharacterStatsPanel.MoveFlag |
                                            CharacterStatsPanel.ProficiencyFlag |
                                            CharacterStatsPanel.HitPointMaxFlag |
                                            CharacterStatsPanel.HitDiceFlag);

        for (var i = _currentLearnStep; i >= 0; i--)
        {
            ResetLearnings(i);
        }

        BuildLearnSteps();
        _spellsScrollRect.normalizedPosition = Vector2.zero;
        OnPreRefresh();
        RefreshNow();
    }

    public override void OnEndHide()
    {
        _learnedInvocations.Clear();

        for (var i = 0; i < _spellsByLevelTable.childCount; i++)
        {
            var child = _spellsByLevelTable.GetChild(i);

            if (!child.gameObject.activeSelf)
            {
                continue;
            }

            var group = child.GetComponent<SpellsByLevelGroup>();
            group.CustomUnbind();
        }

        Gui.ReleaseChildrenToPool(_spellsByLevelTable);
        Gui.ReleaseChildrenToPool(_learnStepsTable);
        Gui.ReleaseChildrenToPool(_levelButtonsTable);

        base.OnEndHide();

        if (!_backdrop.sprite)
        {
            return;
        }

        _backdrop.ClearAddressableSprite();
    }

    public override bool CanProceedToNextStage(out string failureString)
    {
        if (!IsFinalStep ||
            !initialized ||
            _allPools.Any(pool => pool.Remaining > 0 || (pool.Skipped && !CanSkipRemaining(pool))))
        {
            failureString = Gui.Localize("UI/&CustomFeatureSelectionStageNotDone");
            return false;
        }

        failureString = string.Empty;

        return true;
    }

    public override void CancelStage()
    {
        initialized = false;

        _currentLearnStep = 0;

        foreach (var pool in _allPools)
        {
            pool.Used = 0;
            pool.Skipped = false;
        }

        _learnedInvocations.Clear();
        GrantAcquiredFeatures(OnCancelStageDone);
    }

    public override void Refresh()
    {
        if (_selectionCharacter != null)
        {
            RefreshInvocationSelection();
            return;
        }

        if (_allPools.Count == 0)
        {
            base.Refresh();
            return;
        }

        var currentPoolIndex = 0;

        for (var i = 0; i < _learnStepsTable.childCount; i++)
        {
            var child = _learnStepsTable.GetChild(i);

            if (!child.gameObject.activeSelf)
            {
                continue;
            }

            var stepItem = child.GetComponent<LearnStepItem>();

            LearnStepItem.Status status;

            if (i == _currentLearnStep)
            {
                status = LearnStepItem.Status.InProgress;
            }
            else if (i == _currentLearnStep - 1)
            {
                status = LearnStepItem.Status.Previous;
            }
            else
            {
                status = LearnStepItem.Status.Locked;
            }

            stepItem.ignoreAvailable = CanSkipRemaining(_allPools[i]);
            stepItem.CustomRefresh(status, _allPools[i]);

            if (status == LearnStepItem.Status.InProgress)
            {
                currentPoolIndex = i;
            }
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(_learnStepsTable);

        if (IsFinalStep)
        {
            currentPoolIndex = _allPools.Count - 1;
        }

        var currentPoolId = _allPools[currentPoolIndex].Id;
        var isUnlearnStep = IsUnlearnStep(currentPoolIndex);
        var featurePool = GetPoolById(currentPoolId);
        var allLevels = featurePool.Type.AllLevels;
        var requiredGroups = allLevels.Count;

        while (_spellsByLevelTable.childCount < requiredGroups)
        {
            Gui.GetPrefabFromPool(_spellsByLevelPrefab, _spellsByLevelTable);
        }

        float totalWidth = 0;
        float lastWidth = 0;
        var layout = _spellsByLevelTable.GetComponent<HorizontalLayoutGroup>();

        layout.padding.left = (int)SpellsByLevelMargin;

        for (var i = 0; i < _spellsByLevelTable.childCount; i++)
        {
            var child = _spellsByLevelTable.GetChild(i);

            child.gameObject.SetActive(i < requiredGroups);

            if (i >= requiredGroups)
            {
                continue;
            }

            var group = child.GetComponent<SpellsByLevelGroup>();
            var featureLevel = allLevels[i];
            var lowLevel = !isUnlearnStep && featureLevel > (featurePool.Type.RequireClassLevels != null
                ? Math.Max(1,
                    InvocationPoolTypeCustom.GetClassOrSubclassLevel(currentHero, featurePool.Type.RequireClassLevels))
                : _gainedCharacterLevel);

            group.Selected = !IsFinalStep && !lowLevel;

            var unlearnedFeatures = isUnlearnStep
                ? GetOrMakeLearnedList(featurePool.Id)
                : GetOrMakeUnlearnedList(featurePool.Id);

            group.CustomFeatureBind(
                currentHero,
                featurePool.Type,
                GetOrMakeLearnedList(featurePool.Id),
                featureLevel,
                unlearnedFeatures,
                group.Selected,
                isUnlearnStep,
                OnFeatureSelected
            );

            lastWidth = group.RectTransform.rect.width + layout.spacing;
            totalWidth += lastWidth;
        }

        // Compute manually the table width, adding a reserve of fluff for the scroll view
        totalWidth += _spellsScrollRect.GetComponent<RectTransform>().rect.width - lastWidth;
        _spellsByLevelTable.sizeDelta = new Vector2(totalWidth, _spellsByLevelTable.sizeDelta.y);

        // Spell Level Buttons
        while (_levelButtonsTable.childCount < requiredGroups)
        {
            Gui.GetPrefabFromPool(_levelButtonPrefab, _levelButtonsTable);
        }

        // Bind the required group, once for each spell level
        for (var i = 0; i < requiredGroups; i++)
        {
            var child = _levelButtonsTable.GetChild(i);
            child.gameObject.SetActive(true);
            var button = child.GetComponent<SpellLevelButton>();
            button.CustomBind(allLevels[i], LevelSelected);
        }

        // Hide remaining useless groups
        for (var i = requiredGroups; i < _levelButtonsTable.childCount; i++)
        {
            var child = _levelButtonsTable.GetChild(i);
            child.gameObject.SetActive(false);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(_spellsByLevelTable);

        base.Refresh();
    }

    #endregion

    private void RefreshInvocationSelection()
    {
        InvocationPoolTypeCustom.RefreshAll();
        var levels = _selectionPool.AllLevels;
        while (_spellsByLevelTable.childCount < levels.Count)
        {
            Gui.GetPrefabFromPool(_spellsByLevelPrefab, _spellsByLevelTable);
        }

        var width = 0f;
        var layout = _spellsByLevelTable.GetComponent<HorizontalLayoutGroup>();
        layout.padding.left = (int)SpellsByLevelMargin;
        for (var i = 0; i < _spellsByLevelTable.childCount; i++)
        {
            var child = _spellsByLevelTable.GetChild(i);
            child.gameObject.SetActive(i < levels.Count);
            if (i >= levels.Count)
            {
                continue;
            }

            var group = child.GetComponent<SpellsByLevelGroup>();
            group.Selected = true;
            group.CustomFeatureBind(null, _selectionPool, [], levels[i], [], true, false,
                OnFeatureSelected, _selectionCharacter, _canSelectInvocation);
            group.slotStatusTable.gameObject.SetActive(false);
            width += group.RectTransform.rect.width + layout.spacing;
        }

        _spellsByLevelTable.sizeDelta = new Vector2(width + SpellsByLevelMargin, _spellsByLevelTable.sizeDelta.y);
        LayoutRebuilder.ForceRebuildLayoutImmediate(_spellsByLevelTable);
    }

    private void LayoutInvocationSelection()
    {
        Place(_stageTitleLabel.RectTransform, RectTransform, new Vector2(0.01f, 0.91f),
            new Vector2(0.63f, 0.99f));
        _stageTitleLabel.Text = _replacementPresentation.Description;
        _stageTitleLabel.TMP_Text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        _stageTitleLabel.TMP_Text.fontSize = 22f;
        Place(_spellsScrollRect.GetComponent<RectTransform>(), RectTransform, new Vector2(0.01f, 0.01f),
            new Vector2(0.63f, 0.9f));

        // Retain only the native card viewport; the modal supplies the header and comparison.
        foreach (Transform child in RectTransform)
        {
            if (child == _stageTitleLabel.transform || child == _spellsScrollRect.transform)
            {
                continue;
            }
            foreach (var graphic in child.GetComponentsInChildren<Graphic>(true))
            {
                graphic.enabled = false;
            }
        }
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.gameObject.layer = parent.gameObject.layer;
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return rect;
    }

    private static void Place(RectTransform rect, Transform parent, Vector2 minimum, Vector2 maximum)
    {
        rect.SetParent(parent, false);
        rect.anchorMin = minimum;
        rect.anchorMax = maximum;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
    }

    private sealed class InvocationDescriptionLayout : MonoBehaviour, ILayoutElement
    {
        internal TMPro.TMP_Text Text;
        public float minWidth => 0f;
        public float preferredWidth => 0f;
        public float flexibleWidth => 1f;
        public float minHeight => preferredHeight;
        public float preferredHeight => Text.GetPreferredValues(Text.text,
            ((RectTransform)transform).rect.width, Mathf.Infinity).y;
        public float flexibleHeight => -1f;
        public int layoutPriority => 10;
        public void CalculateLayoutInputHorizontal() { }
        public void CalculateLayoutInputVertical() { }
    }

    // Only the native card presenter is shared with character creation. This screen owns
    // the in-game selection, input, confirmation and lifetime independently of hero building.
    private sealed class InvocationSelectionModal : GuiGameScreen
    {
        private static InvocationSelectionModal _instance;
        private CustomInvocationSelectionPanel _panel;
        private GuiLabel _title;
        private Button _confirmButton;
        private Button _cancelButton;
        private GuiLabel _confirmLabel;
        private InvocationComparisonEntry _sourceEntry;
        private InvocationComparisonEntry _replacementEntry;
        private IGameService _selectionGameService;

        public override bool GrabsCameraControl => true;
        public override string ActionMap => "UI";
        public override bool AutoRecomputeNavigation => true;

        internal static InvocationSelectionModal Get() =>
            _instance ? _instance : _instance = new GameObject("InvocationSelectionModal")
                .AddComponent<InvocationSelectionModal>();

        public override void Awake()
        {
            gameObject.AddComponent<RectTransform>();
            gameObject.AddComponent<CanvasRenderer>();
            gameObject.AddComponent<CanvasGroup>();
            base.Awake();
        }

        public override void Initialize()
        {
            if (Initialized)
            {
                return;
            }

            base.Initialize();
            if (Gui.GuiService is GuiManager manager)
            {
                manager.screensByType[GetType()] = this;
            }
            loaded = true;
            gameObject.layer = LayerMask.NameToLayer("UI");
            var levelUp = Gui.GuiService.GetScreen<CharacterLevelUpScreen>();
            transform.SetParent(levelUp.transform.parent, false);
            SortIndex = levelUp.SortIndex + 1;
            transform.SetSiblingIndex(levelUp.transform.GetSiblingIndex() + 1);
            Place(RectTransform, transform.parent, Vector2.zero, Vector2.one);

            var message = Gui.GuiService.GetScreen<MessageModal>();
            var backdrop = Instantiate(message.transform.Find("Background"), transform, false);
            backdrop.name = "InvocationSelectionBackdrop";
            var frame = CreateRect("InvocationSelectionFrame", transform);
            Place(frame, transform, new Vector2(0.03f, 0.09f), new Vector2(0.97f, 0.94f));
            // Copy the actual modal shadow, inset frame and ornament instead of drawing substitutes.
            foreach (var name in new[] { "Shadow", "CombinationBackgroundPanel", "PanelBackground" })
            {
                var decoration = Instantiate(message.panel.transform.Find(name), frame, false);
                decoration.name = name;
                decoration.gameObject.SetActive(true);
            }
            _title = Instantiate(message.titleLabel, frame, false);
            _title.name = "InvocationSelectionTitle";
            Place(_title.RectTransform, frame, new Vector2(0.05f, 0.9f), new Vector2(0.95f, 1f));
            _title.gameObject.SetActive(true);
            var ornament = (RectTransform)Instantiate(message.panel.transform.Find("SeparatorGroup"), frame, false);
            ornament.name = "SeparatorGroup";
            Place(ornament, frame, new Vector2(0.05f, 0.9f), new Vector2(0.95f, 0.9f));
            ornament.gameObject.SetActive(true);

            var body = CreateRect("InvocationSelectionBody", frame);
            Place(body, frame, new Vector2(0.025f, 0.1f), new Vector2(0.975f, 0.88f));
            _panel = GetPanel(body);
            _panel._standaloneSelection = true;
            _panel.Initialize();
            Place(_panel.RectTransform, body, Vector2.zero, Vector2.one);

            _sourceEntry = new InvocationComparisonEntry(body, message, _panel._spellsByLevelPrefab,
                "InvocationSource", new Vector2(0.67f, 0.53f), new Vector2(0.99f, 0.99f));
            _replacementEntry = new InvocationComparisonEntry(body, message, _panel._spellsByLevelPrefab,
                "InvocationReplacement", new Vector2(0.67f, 0.02f), new Vector2(0.99f, 0.48f));

            var buttons = (RectTransform)Instantiate(message.cancelButton.transform.parent, frame, false);
            buttons.name = "InvocationSelectionButtons";
            buttons.anchorMin = buttons.anchorMax = new Vector2(0.5f, 0.035f);
            buttons.pivot = new Vector2(0.5f, 0.5f);
            buttons.anchoredPosition = Vector2.zero;
            buttons.GetComponent<HorizontalLayoutGroup>().spacing = 20f;
            _cancelButton = buttons.Find(message.cancelButton.name).GetComponent<Button>();
            _confirmButton = buttons.Find(message.validateButton.name).GetComponent<Button>();
            ConfigureButton(_cancelButton, "CancelInvocationSelection", "Message/&MessageCancelTitle", Close);
            ConfigureButton(_confirmButton, "ConfirmInvocationSelection", null, ConfirmSelection);
            _confirmLabel = _confirmButton.GetComponentInChildren<GuiLabel>(true);
            buttons.gameObject.SetActive(true);
        }

        private static void ConfigureButton(Button button, string name, string caption, UnityEngine.Events.UnityAction action)
        {
            button.name = name;
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(action);
            var layout = button.GetComponent<LayoutElement>();
            layout.minWidth = 180f;
            layout.preferredWidth = 220f;
            layout.minHeight = 44f;
            layout.preferredHeight = 44f;
            button.GetComponent<RectTransform>().SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 44f);
            if (caption != null)
            {
                button.GetComponentInChildren<GuiLabel>(true).Text = caption;
            }
            button.gameObject.SetActive(true);
            button.GetComponentInChildren<GuiLabel>(true).TMP_Text.fontSize = 22f;
        }

        internal void Bind(RulesetCharacter character, InvocationPoolTypeCustom pool, string title,
            InvocationReplacementPresentation presentation,
            Func<InvocationDefinitionCustom, bool> canSelect, Action<InvocationDefinitionCustom> selected)
        {
            Initialize();
            if (Visible)
            {
                Hide(true);
            }
            _panel._selectionCharacter = character;
            _panel._selectionPool = pool;
            _panel._selectionTitle = title;
            _panel._replacementPresentation = presentation;
            _panel._canSelectInvocation = canSelect;
            _panel._invocationSelected = selected;
            _panel._pendingInvocation = null;
            _panel._wasClicked = false;
            Show();
        }

        public override void OnBeginShow(bool instant = false)
        {
            base.OnBeginShow(instant);
            _selectionGameService = ServiceRepository.GetService<IGameService>();
            if (_selectionGameService != null)
            {
                _selectionGameService.GameDestroying += OnSelectionGameDestroying;
            }
            _title.Text = _panel._selectionTitle;
            _confirmLabel.Text = _panel._replacementPresentation.ConfirmTitle;
            ApplyActionButtonTextSize();
            _panel.Show(instant);
            RefreshSelection();
            ServiceRepository.GetService<ITooltipService>()?.HideTooltip();
        }

        internal void RefreshSelection()
        {
            var presentation = _panel._replacementPresentation;
            _sourceEntry.Bind(_panel, presentation.SourceTitle, presentation.Source);
            _replacementEntry.Bind(_panel, presentation.ReplacementTitle, _panel._pendingInvocation);
            foreach (var card in _panel._spellsByLevelTable.GetComponentsInChildren<SpellBox>(false))
            {
                var invocation = card.GetFeature();
                card.CustomRefreshLearningInProgress(_panel._canSelectInvocation(invocation),
                    invocation == _panel._pendingInvocation,
                    _panel._selectionCharacter.Invocations.Any(known => known.InvocationDefinition == invocation));
            }
            _confirmButton.interactable = _panel._pendingInvocation != null &&
                                          _panel._canSelectInvocation(_panel._pendingInvocation);
        }

        private void ConfirmSelection()
        {
            var selection = _panel._pendingInvocation;
            if (_panel._wasClicked || selection == null || !_panel._canSelectInvocation(selection))
            {
                return;
            }
            _panel._wasClicked = true;
            var handler = _panel._invocationSelected;
            Hide(true);
            handler?.Invoke(selection);
        }

        public override void OnBeginHide(bool instant = false)
        {
            if (_selectionGameService != null)
            {
                _selectionGameService.GameDestroying -= OnSelectionGameDestroying;
                _selectionGameService = null;
            }
            _panel.Hide(instant);
            base.OnBeginHide(instant);
        }

        public override void OnEndHide()
        {
            // A parent without its own transition may finish before the native stage fade.
            // Complete the child lifecycle while it is still active so its cards and input
            // subscriptions are released before the containing screen is deactivated.
            _panel.Hide(true);
            _sourceEntry.Unbind();
            _replacementEntry.Unbind();
            _panel._selectionCharacter = null;
            _panel._selectionPool = null;
            _panel._selectionTitle = null;
            _panel._replacementPresentation = null;
            _panel._pendingInvocation = null;
            _panel._canSelectInvocation = null;
            _panel._invocationSelected = null;
            base.OnEndHide();
        }

        private void ApplyActionButtonTextSize()
        {
            _confirmLabel.TMP_Text.fontSize = 22f;
            _cancelButton.GetComponentInChildren<GuiLabel>(true).TMP_Text.fontSize = 22f;
        }

        public override void HandleInputControlSchemeChangedForShow(string previousScheme, string newScheme)
        {
            base.HandleInputControlSchemeChangedForShow(previousScheme, newScheme);
            ApplyActionButtonTextSize();
        }

        private void OnSelectionGameDestroying() => Hide(true);
        private void Close() => Hide();

        public override bool HandleInput(InputCommands.Id command)
        {
            if (command == InputCommands.Id.Cancel)
            {
                Close();
                return true;
            }
            return false;
        }

        public override void SelectDefaultControlImpl()
        {
            if (!Gui.GamepadActive || Gui.InputService.CurrentSelectedGameObject)
            {
                return;
            }
            var card = _panel._spellsByLevelTable.GetComponentsInChildren<SpellBox>(false)
                .FirstOrDefault(candidate => candidate.Interactive);
            if (card)
            {
                Gui.InputService.SelectCurrentSelectable(card.Selectable);
            }
        }

        private void CancelPerformed(InputAction.CallbackContext context) => Close();

        public override void RegisterCommands() =>
            Gui.InputService.InputActionAsset.FindActionMap(ActionMap).FindAction("Cancel").performed += CancelPerformed;

        public override void UnregisterCommands() =>
            Gui.InputService.InputActionAsset.FindActionMap(ActionMap).FindAction("Cancel").performed -= CancelPerformed;
    }

    private sealed class InvocationComparisonEntry
    {
        private readonly GuiLabel _heading;
        private readonly SpellBox _card;
        private readonly GuiLabel _details;
        private readonly ScrollRect _scroll;

        internal InvocationComparisonEntry(Transform parent, MessageModal message, GameObject groupPrefab,
            string name, Vector2 minimum, Vector2 maximum)
        {
            var root = CreateRect(name, parent);
            Place(root, parent, minimum, maximum);
            _heading = Instantiate(message.titleLabel, root, false);
            _heading.name = name + "Title";
            Place(_heading.RectTransform, root, new Vector2(0f, 0.8f), Vector2.one);
            _heading.TMP_Text.fontSize = 22f;
            _heading.TMP_Text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            _heading.gameObject.SetActive(true);

            var cardPrefab = groupPrefab.GetComponent<SpellsByLevelGroup>().spellPrefab;
            _card = Instantiate(cardPrefab, root, false).GetComponent<SpellBox>();
            _card.name = name + "Card";
            _card.Initialize();
            var cardRect = _card.RectTransform;
            cardRect.anchorMin = cardRect.anchorMax = new Vector2(0f, 0.77f);
            cardRect.pivot = new Vector2(0f, 1f);
            cardRect.anchoredPosition = Vector2.zero;
            cardRect.sizeDelta = new Vector2(112f, 164f);

            var scrollRoot = CreateRect(name + "Details", root);
            Place(scrollRoot, root, new Vector2(0f, 0f), new Vector2(1f, 0.76f));
            scrollRoot.offsetMin = new Vector2(132f, 0f);
            _scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.inertia = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 36f;
            var viewport = CreateRect("Viewport", scrollRoot);
            viewport.offsetMax = new Vector2(-16f, 0f);
            viewport.gameObject.AddComponent<Image>().color = Color.clear;
            viewport.gameObject.AddComponent<RectMask2D>();
            _scroll.viewport = viewport;
            var content = CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1f);
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll.content = content;
            _details = Instantiate(message.contentLabel, content, false);
            _details.name = name + "Description";
            _details.TMP_Text.alignment = TMPro.TextAlignmentOptions.TopLeft;
            _details.TMP_Text.enableWordWrapping = true;
            _details.TMP_Text.overflowMode = TMPro.TextOverflowModes.Overflow;
            _details.RectTransform.gameObject.AddComponent<InvocationDescriptionLayout>().Text = _details.TMP_Text;
            _details.gameObject.SetActive(true);

            var nativeBar = Gui.GuiService.GetScreen<CharacterInspectionScreen>()
                .GetComponentsInChildren<Scrollbar>(true)
                .First(bar => bar.direction == Scrollbar.Direction.BottomToTop);
            var scrollbar = Instantiate(nativeBar, scrollRoot, false);
            scrollbar.onValueChanged = new Scrollbar.ScrollEvent();
            Place(scrollbar.GetComponent<RectTransform>(), scrollRoot, new Vector2(1f, 0f), Vector2.one);
            scrollbar.GetComponent<RectTransform>().offsetMin = new Vector2(-12f, 0f);
            _scroll.verticalScrollbar = scrollbar;
            _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scrollbar.gameObject.SetActive(true);
        }

        internal void Bind(CustomInvocationSelectionPanel panel, string heading, InvocationDefinitionCustom invocation)
        {
            _heading.Text = heading;
            _card.gameObject.SetActive(invocation != null);
            if (invocation != null)
            {
                _card.CustomFeatureBind(invocation, false, SpellBox.BindMode.Learning, null);
                _card.SetupUI(panel._selectionCharacter, panel._selectionPool.Sprite, []);
                _card.CustomRefreshLearningInProgress(false, false, true);
                _card.availableToLearnGroup.gameObject.SetActive(false);
                _details.Text = panel._replacementPresentation.FormatDetails(invocation);
            }
            else
            {
                _card.CustomUnbind();
                _details.Text = panel._replacementPresentation.Description;
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(_scroll.content);
            _scroll.verticalNormalizedPosition = 1f;
        }

        internal void Unbind()
        {
            _card.CustomUnbind();
            _details.Text = string.Empty;
        }
    }

    #region UI helpers

    private void ResetWasClickedFlag()
    {
        _wasClicked = false;
    }

    private void LevelSelected(int level)
    {
        StartCoroutine(BlendToLevelGroup(level));
    }

    // ReSharper disable Unity.PerformanceAnalysis
    private IEnumerator BlendToLevelGroup(int level)
    {
        SpellsByLevelGroup group = null;
        var duration = ScrollDuration;
        var shouldAssignGroup = true;

        foreach (Transform child in _spellsByLevelTable)
        {
            var spellByLevelGroup = child.GetComponent<SpellsByLevelGroup>();

            if (shouldAssignGroup)
            {
                shouldAssignGroup = false;
                group = spellByLevelGroup;
            }

            if (spellByLevelGroup.SpellLevel == level)
            {
                group = spellByLevelGroup;
            }
        }

        if (!group)
        {
            yield break;
        }

        var initialX = _spellsByLevelTable.anchoredPosition.x;
        var finalX = -group.RectTransform.anchoredPosition.x + SpellsByLevelMargin;

        while (duration > 0)
        {
            _spellsByLevelTable.anchoredPosition = new Vector2(
                Mathf.Lerp(initialX, finalX, _curve.Evaluate((ScrollDuration - duration) / ScrollDuration)), 0);
            duration -= Gui.SystemDeltaTime;
            yield return null;
        }

        _spellsByLevelTable.anchoredPosition = new Vector2(finalX, 0);
    }

    #endregion
}

#region Widget Extensions

internal static class LearnStepItemExtension
{
    internal static void CustomBind(
        this LearnStepItem instance,
        int rank,
        CustomInvocationSelectionPanel.FeaturePool pool,
        LearnStepItem.ButtonActivatedHandler onBackOneStepActivated,
        LearnStepItem.ButtonActivatedHandler onResetActivated,
        LearnStepItem.ButtonActivatedHandler onAutoSelectActivated)
    {
        instance.Tag = pool.Id.Tag;
        instance.PoolType = pool.IsUnlearn
            ? HeroDefinitions.PointsPoolType.SpellUnlearn
            : HeroDefinitions.PointsPoolType.Irrelevant;
        instance.rank = rank;
        instance.ignoreAvailable = pool.IsUnlearn;
        instance.autoLearnAvailable = false;

        var header = pool.Type.FormatTitle(pool.IsUnlearn);

        instance.headerLabelActive.Text = header;
        instance.headerLabelInactive.Text = header;
        instance.OnBackOneStepActivated = onBackOneStepActivated;
        instance.OnResetActivated = onResetActivated;
        instance.OnAutoSelectActivated = onAutoSelectActivated;
    }

    internal static void CustomRefresh(
        this LearnStepItem instance,
        LearnStepItem.Status status,
        CustomInvocationSelectionPanel.FeaturePool pool)
    {
        MulticlassGameUi.RestoreLearnStepLayout(instance);
        var usedPoints = pool.Used;
        var maxPoints = pool.Max;
        var ignoreAvailable = instance.ignoreAvailable;
        var choiceLabel = instance.choicesLabel;
        var activeGroup = instance.activeGroup;
        var inactiveGroup = instance.inactiveGroup;
        var autoButton = instance.autoButton;
        var ignoreButton = instance.ignoreButton;
        var resetButton = instance.resetButton;

        activeGroup.gameObject.SetActive(status == LearnStepItem.Status.InProgress);
        inactiveGroup.gameObject.SetActive(status != LearnStepItem.Status.InProgress);
        instance.activeBackground.gameObject.SetActive(status != LearnStepItem.Status.Locked);
        instance.inactiveBackground.gameObject.SetActive(status == LearnStepItem.Status.Locked);
        instance.backOneStepButton.gameObject.SetActive(status == LearnStepItem.Status.Previous);
        resetButton.gameObject.SetActive(status == LearnStepItem.Status.InProgress);
        autoButton.gameObject.SetActive(false);
        ignoreButton.gameObject.SetActive(ignoreAvailable);

        if (status == LearnStepItem.Status.InProgress)
        {
            instance.pointsLabelActive.Text = Gui.FormatCurrentOverMax(usedPoints, maxPoints);
            instance.remainingPointsGaugeActive.fillAmount = (float)usedPoints / maxPoints;
            choiceLabel.Text = pool.Type.FormatDescription(pool.IsUnlearn);
            LayoutRebuilder.ForceRebuildLayoutImmediate(choiceLabel.RectTransform);
            var sizeDelta = new Vector2(activeGroup.sizeDelta.x,
                (float)(choiceLabel.RectTransform.rect.height - choiceLabel.RectTransform.anchoredPosition.y +
                        12.0));
            activeGroup.sizeDelta = sizeDelta;
            instance.RectTransform.sizeDelta = sizeDelta;
            resetButton.interactable = usedPoints < maxPoints;
            autoButton.interactable = false;
        }
        else
        {
            instance.pointsLabelInactive.Text = Gui.FormatCurrentOverMax(usedPoints, maxPoints);
            instance.remainingPointsGaugeInactive.fillAmount = (float)usedPoints / maxPoints;
            instance.RectTransform.sizeDelta = inactiveGroup.sizeDelta;
            instance.backOneStepButton.interactable = true;
        }

        MulticlassGameUi.ApplyLearnStepLayout(instance, status == LearnStepItem.Status.InProgress);
    }
}

internal static class SpellLevelButtonExtension
{
    internal static void CustomBind(
        this SpellLevelButton instance,
        int level,
        SpellLevelButton.LevelSelectedHandler levelSelected)
    {
        instance.Level = level;
        instance.LevelSelected = levelSelected;
        instance.label.Text = level > 0 ? Gui.ToRoman(level) : $"{level}";
    }
}

internal static class SpellsByLevelGroupExtensions
{
    internal static void CustomFeatureBind(this SpellsByLevelGroup instance,
        RulesetCharacterHero hero,
        InvocationPoolTypeCustom pool,
        List<InvocationDefinitionCustom> learned,
        int featureLevel,
        List<InvocationDefinitionCustom> unlearned,
        bool canAcquireFeatures,
        bool unlearn,
        SpellBox.SpellBoxChangedHandler spellBoxChanged,
        RulesetCharacter selectionCharacter = null,
        Func<InvocationDefinitionCustom, bool> canSelect = null)
    {
        instance.name = $"Feature[{pool.Name}]";
        instance.SpellLevel = featureLevel;
        UiTextHelpers.BeginSpellBoxGridLayout(instance);

        var allFeatures = pool.GetLevelFeatures(featureLevel);

        allFeatures.Sort(Sorting.CompareTitle);

        var spellsTable = instance.spellsTable;
        var spellPrefab = instance.spellPrefab;

        while (spellsTable.childCount < allFeatures.Count)
        {
            Gui.GetPrefabFromPool(spellPrefab, spellsTable);
        }

        var component1 = spellsTable.GetComponent<GridLayoutGroup>();

        component1.constraintCount = Mathf.Max(3, Mathf.CeilToInt(allFeatures.Count / 4f));

        for (var index = 0; index < allFeatures.Count; ++index)
        {
            var feature = allFeatures[index];

            spellsTable.GetChild(index).gameObject.SetActive(true);

            var box = spellsTable.GetChild(index).GetComponent<SpellBox>();
            var isUnlearned = unlearned.Contains(feature);
            var bindMode = unlearn ? SpellBox.BindMode.Unlearn : SpellBox.BindMode.Learning;

            box.CustomFeatureBind(feature, isUnlearned, bindMode, spellBoxChanged);
        }

        //disable unneeded spell boxes
        for (var count = allFeatures.Count; count < spellsTable.childCount; ++count)
        {
            spellsTable.GetChild(count).gameObject.SetActive(false);
        }

        var x = (float)((component1.constraintCount * (double)component1.cellSize.x) +
                        ((component1.constraintCount - 1) * (double)component1.spacing.x));

        spellsTable.sizeDelta = new Vector2(x, spellsTable.sizeDelta.y);
        instance.RectTransform.sizeDelta = new Vector2(x, instance.RectTransform.sizeDelta.y);

        instance.slotStatusTable.Bind(null, featureLevel, false, null, false);

        if (selectionCharacter != null && canSelect != null)
        {
            foreach (Transform child in spellsTable)
            {
                if (!child.gameObject.activeSelf)
                {
                    continue;
                }
                var box = child.GetComponent<SpellBox>();
                var feature = box.GetFeature();
                box.SetupUI(selectionCharacter, pool.Sprite, []);
                box.CustomRefreshLearningInProgress(canSelect(feature), false,
                    selectionCharacter.Invocations.Any(known => known.InvocationDefinition == feature));
            }
        }
        else if (unlearn)
        {
            instance.RefreshUnlearning(hero, pool, unlearned, canAcquireFeatures);
        }
        else
        {
            instance.RefreshLearning(hero, pool, learned, unlearned, canAcquireFeatures);
        }

        UiTextHelpers.EndSpellBoxGridLayout(instance);
    }

    private static void RefreshLearning(this SpellsByLevelGroup instance,
        RulesetCharacterHero hero,
        InvocationPoolTypeCustom pool,
        // ReSharper disable once SuggestBaseTypeForParameter
        List<InvocationDefinitionCustom> learned,
        // ReSharper disable once SuggestBaseTypeForParameter
        List<InvocationDefinitionCustom> unlearnedFeatures,
        bool canAcquireFeatures)
    {
        foreach (Transform transform in instance.spellsTable)
        {
            if (!transform.gameObject.activeSelf)
            {
                continue;
            }

            var box = transform.GetComponent<SpellBox>();
            var boxFeature = box.GetFeature();
            var alreadyHas = hero.TrainedInvocations.Contains(boxFeature);
            var selected = learned.Contains(boxFeature);
            var isUnlearned = unlearnedFeatures != null && unlearnedFeatures.Contains(boxFeature);
            var isValid = PowerBundle.ValidatePrerequisites(
                hero, boxFeature, boxFeature.Validators, out var requirements);
            var canLearn = !selected && !alreadyHas && isValid;

            box.SetupUI(hero, pool.Sprite, requirements);

            if (canAcquireFeatures)
            {
                box.CustomRefreshLearningInProgress((canLearn || selected) && !isUnlearned, selected,
                    alreadyHas);
            }
            else
            {
                box.RefreshLearningInactive((selected || alreadyHas) && !isUnlearned);
            }
        }
    }

    private static void RefreshUnlearning(
        this SpellsByLevelGroup instance,
        RulesetCharacterHero hero,
        InvocationPoolTypeCustom pool,
        List<InvocationDefinitionCustom> unlearnedSpells,
        bool canUnlearnInvocations)
    {
        foreach (Transform transform in instance.spellsTable)
        {
            if (!transform.gameObject.activeSelf)
            {
                continue;
            }

            var box = transform.GetComponent<SpellBox>();
            var boxFeature = box.GetFeature();
            var isUnlearned = unlearnedSpells != null && unlearnedSpells.Contains(boxFeature);
            var alreadyHas = hero.TrainedInvocations.Contains(boxFeature);
            var canUnlearn = !isUnlearned && alreadyHas;

            PowerBundle.ValidatePrerequisites(hero, boxFeature, boxFeature.Validators, out var requirements);

            box.SetupUI(hero, pool.Sprite, requirements);

            if (canUnlearnInvocations)
            {
                box.RefreshUnlearnInProgress(canUnlearn || isUnlearned, isUnlearned);
            }
            else
            {
                box.RefreshUnlearnInactive(isUnlearned);
            }
        }
    }

    internal static void CustomUnbind(this SpellsByLevelGroup instance)
    {
        instance.SpellRepertoire = null;

        var spellsTable = instance.spellsTable;

        foreach (Component component in spellsTable)
        {
            component.GetComponent<SpellBox>().CustomUnbind();
        }

        Gui.ReleaseChildrenToPool(spellsTable);
        instance.slotStatusTable.Unbind();
        // The group also returns to native spell-learning and inspection screens,
        // whose Bind methods do not reactivate this container themselves.
        instance.slotStatusTable.gameObject.SetActive(true);
    }

    // ReSharper disable once SuggestBaseTypeForParameter
}

internal static class SpellBoxExtensions
{
    private static readonly Dictionary<SpellBox, InvocationDefinitionCustom> Features = new();

    internal static InvocationDefinitionCustom GetFeature(this SpellBox box)
    {
        return Features.TryGetValue(box, out var result) ? result : null;
    }

    internal static void CustomFeatureBind(
        this SpellBox instance,
        InvocationDefinitionCustom feature,
        bool unlearned,
        SpellBox.BindMode bindMode,
        SpellBox.SpellBoxChangedHandler spellBoxChanged)
    {
        // Cards share the spell pool. Release the previous spell wrapper and clear its source label
        // through Unbind before a custom feature bypasses the normal spell Bind path.
        instance.Unbind();
        Features.AddOrReplace(instance, feature);

        instance.bindMode = bindMode;
        instance.SpellBoxChanged = spellBoxChanged;
        instance.hovered = false;
        instance.ritualSpell = false;
        instance.autoPrepared = false;
        instance.extraSpell = false;
        instance.unlearnedSpell = unlearned;
        instance.spellImage.color = Color.white;
        instance.transform.localScale = new Vector3(1f, 1f, 1f);

        var component = instance.availableToLearnGroup.GetComponent<GuiModifier>();

        component.ForwardStartDelay = Random.Range(0.0f, component.Duration);

        instance.prepared = false;
        instance.canLearn = false;
        instance.selectedToLearn = false;
        instance.canPrepare = false;
        instance.known = false;
        instance.canUnlearn = false;

        instance.name = feature.Name;
    }

    internal static void CustomRefreshLearningInProgress(
        this SpellBox instance, bool canLearn, bool selected, bool known)
    {
        var autoPrepared = instance.autoPrepared;

        instance.interactive = canLearn && !autoPrepared;
        instance.canLearn = canLearn && !autoPrepared;
        instance.selectedToLearn = selected;
        instance.known = known; //TODO: try to experiment with auto prepared tags to signify known features
        instance.Refresh();
    }

    internal static void SetupUI(
        this SpellBox instance, RulesetCharacter hero, AssetReferenceSprite sprite, List<string> requirements)
    {
        var title = instance.titleLabel;
        var image = instance.spellImage;
        var tooltip = instance.tooltip;
        var feature = instance.GetFeature();
        var gui = new GuiPresentationBuilder(feature.GuiPresentation).Build();
        var item = feature.Item;
        var dataProvider = !item
            ? new CustomTooltipProvider(feature, gui)
            : new CustomItemTooltipProvider(feature, gui, item);

        dataProvider.SetPrerequisites(requirements);
        tooltip.TooltipClass = dataProvider.TooltipClass;
        tooltip.Content = CustomTooltipProvider.GetActivationContent(feature);
        tooltip.Context = hero;
        tooltip.DataProvider = dataProvider;

        if (gui.SpriteReference?.RuntimeKeyIsValid() != true)
        {
            gui.spriteReference = sprite;
        }

        title.Text = gui.Title;
        UiTextHelpers.KeepSpellBoxTextInside(instance);

        image.SetupSprite(gui.spriteReference, true);

        if (!image.sprite && gui.SpriteReference != sprite && sprite?.RuntimeKeyIsValid() == true)
        {
            gui.spriteReference = sprite;
            image.SetupSprite(sprite, true);
        }

        // A pooled card must not retain a white Image when neither the definition nor the pool can supply artwork.
        image.gameObject.SetActive(image.sprite);
    }

    internal static void CustomUnbind(this SpellBox instance)
    {
        Features.Remove(instance);
        instance.Unbind();
    }
}

#endregion
