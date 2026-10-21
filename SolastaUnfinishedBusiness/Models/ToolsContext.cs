using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Interfaces;
using TA;
using UnityEngine;
using UnityEngine.UI;
using static Gui.LocalizationSpeakerGender;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class ToolsContext
{
    internal const int GamePartySize = 4;

    internal const int MinPartySize = 1;
    internal const int MaxPartySize = 6;

    internal const float CustomScale = 0.85f;

    internal const int DungeonMinLevel = 1;
    internal const int DungeonMaxLevel = 20;

    private const RestActivityDefinition.ActivityCondition ActivityConditionDisabled =
        (RestActivityDefinition.ActivityCondition)(-1001);

    private const string RespecName = "RestActivityRespec";

    private static RestActivityDefinition RestActivityRespec { get; } = RestActivityDefinitionBuilder
        .Create(RespecName)
        .SetGuiPresentation(Category.RestActivity)
        .SetRestData(
            RestDefinitions.RestStage.AfterRest,
            RestType.LongRest,
            RestActivityDefinition.ActivityCondition.None,
            RespecName,
            string.Empty)
        .AddToDB();

    internal static string GetCharacterFilename(string name)
    {
        var pool = ServiceRepository.GetService<ICharacterPoolService>();
        var custom = pool.BuildCharacterFilename(name, false);

        return File.Exists(custom) ? custom : pool.BuildCharacterFilename(name, true);
    }

    internal static void Load()
    {
        ServiceRepository.GetService<IFunctorService>().RegisterFunctor(RespecName, new FunctorRespec());
        SwitchRespec();
    }

    internal static void SwitchRespec()
    {
        RestActivityRespec.condition = Main.Settings.EnableRespecAction
            ? RestActivityDefinition.ActivityCondition.None
            : ActivityConditionDisabled;
    }


    public static void Rebase(Transform parent, int max)
    {
        while (Main.Settings.DefaultPartyHeroes.Count > max)
        {
            var heroToDelete = Main.Settings.DefaultPartyHeroes.ElementAt(0);

            var child = parent.FindChildRecursive(heroToDelete);

            if (child)
            {
                child.GetComponentInChildren<Toggle>().isOn = false;
            }
        }
    }

    public static Transform CreateHeroCheckbox(Component character)
    {
        // ReSharper disable once Unity.UnknownResource
        var settingCheckboxItem = Resources.Load<GameObject>("Gui/Prefabs/Modal/Setting/SettingCheckboxItem");
        var smallToggleNoFrame = settingCheckboxItem.transform.Find("SmallToggleNoFrame");
        var checkBox = UnityEngine.Object.Instantiate(smallToggleNoFrame, character.transform);
        var checkBoxRect = checkBox.GetComponent<RectTransform>();

        checkBox.name = "DefaultHeroToggle";
        checkBox.gameObject.SetActive(true);
        checkBox.Find("Background").gameObject.AddComponent<GuiTooltip>();

        checkBoxRect.anchoredPosition = new Vector2(160, 40);

        return checkBox;
    }

    internal static void Disable(RectTransform charactersTable)
    {
        for (var i = 0; i < charactersTable.childCount; i++)
        {
            var character = charactersTable.GetChild(i);
            var checkBoxToggle = character.GetComponentInChildren<Toggle>();

            if (checkBoxToggle)
            {
                checkBoxToggle.gameObject.SetActive(false);
            }
        }
    }

    internal sealed class FunctorRespec : Functor
    {
        private static RespecSession _session;

        internal static bool IsRespecing => _session != null;

        internal static bool CanStartFromCharacterPool(string filename)
        {
            if (IsRespecing || Gui.Game || string.IsNullOrEmpty(filename) || !File.Exists(filename))
            {
                return false;
            }

            var pool = ServiceRepository.GetService<ICharacterPoolService>();

            if (pool == null || !pool.Pool.TryGetValue(filename, out var snapshot))
            {
                return false;
            }

            var level = snapshot.Levels?.Sum() ?? 0;
            var maxLevel = Main.Settings.EnableLevel20 ? Level20Context.ModMaxLevel : Level20Context.GameMaxLevel;

            if (level < 1 || level > maxLevel)
            {
                return false;
            }

            var destination = pool.BuildCharacterFilename(snapshot.Name, false);

            // Built-in heroes become custom heroes; an existing custom hero owns its name.
            return snapshot.BuiltIn
                ? !File.Exists(destination)
                : string.Equals(Path.GetFullPath(filename), Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static IEnumerator StartFromCharacterPool(string filename, GuiScreen originScreen)
        {
            if (!CanStartFromCharacterPool(filename) || !originScreen)
            {
                yield break;
            }

            var pool = ServiceRepository.GetService<ICharacterPoolService>();
            RulesetCharacterHero originalHero = null;
            RespecSession session = null;

            try
            {
                pool.LoadCharacter(filename, out originalHero, out _);
                session = new RespecSession(originalHero, true);
                _session = session;
                originScreen.Hide(true);
                yield return RebuildCharacter(session, null);
            }
            finally
            {
                ReleaseTransientHero(originalHero);
                if (_session == session)
                {
                    _session = null;
                }
                originScreen.Show();
            }
        }

        public override IEnumerator Execute(
            FunctorParametersDescription functorParameters, FunctorExecutionContext context)
        {
            var guiConsoleScreen = Gui.GuiService.GetScreen<GuiConsoleScreen>();
            var explorationScreen = Gui.GuiService.GetScreen<GameLocationScreenExploration>();
            var restModal = Gui.GuiService.GetScreen<RestModal>();

            if (IsRespecing || functorParameters.RestingHero == null ||
                !guiConsoleScreen || !explorationScreen || !restModal)
            {
                yield break;
            }

            if (!explorationScreen.Visible || Global.IsMultiplayer ||
                ServiceRepository.GetService<INetworkingService>()?.InOnlineRoom == true)
            {
                Gui.GuiService.ShowMessage(
                    MessageModal.Severity.Informative1,
                    "RestActivity/&RestActivityRespecTitle", "Message/&RespecCampaignUnavailableDescription",
                    "Message/&MessageOkTitle", string.Empty, null, null);
                yield break;
            }

            var keepCurrentState = restModal.KeepCurrentState;
            var session = new RespecSession(functorParameters.RestingHero, false);
            _session = session;

            try
            {
                guiConsoleScreen.Hide(true);
                explorationScreen.Hide(true);
                restModal.KeepCurrentState = true;
                restModal.Hide(true);
                yield return RebuildCharacter(session, restModal);
            }
            finally
            {
                if (_session == session)
                {
                    _session = null;
                }
                restModal.Show();
                restModal.Refresh();
                restModal.KeepCurrentState = keepCurrentState;
                guiConsoleScreen.Show();
                explorationScreen.Show();
            }
        }

        private static IEnumerator RebuildCharacter(
            RespecSession session, GuiScreen originScreen)
        {
            var building = ServiceRepository.GetService<ICharacterBuildingService>();
            var creation = Gui.GuiService.GetScreen<CharacterCreationScreen>();
            var levelUp = Gui.GuiService.GetScreen<CharacterLevelUpScreen>();
            var originalHero = session.OriginalHero;
            var fromCharacterPool = session.FromCharacterPool;
            var adopted = false;

            try
            {
                session.Hero = building.CreateNewCharacter().HeroCharacter;
                if (!fromCharacterPool)
                {
                    session.Hero.Register(true);
                }
                CopyRespecIdentityData(originalHero, session.Hero);

                if (!Main.Settings.ForcePointBuyAbilityScores)
                {
                    CopyRespecAbilityScoresForDiceRollMode(originalHero, session.Hero);
                }

                if (creation.StagePanelsByName["AbilityScores"] is CharacterStageAbilityScoresPanel abilities)
                {
                    abilities.currentMethod = Main.Settings.ForcePointBuyAbilityScores
                        ? CharacterStageAbilityScoresPanel.AbilityScoreMethod.PointSystem
                        : CharacterStageAbilityScoresPanel.AbilityScoreMethod.DiceRolls;
                }

                // The session restores the origin only after every required level is complete.
                creation.OriginScreen = null;
                creation.CurrentHero = session.Hero;
                creation.Show();
                yield return WaitForEdition(creation, session);

                if (session.Aborted || session.FinalizedLevel != 1)
                {
                    yield break;
                }

                if (fromCharacterPool)
                {
                    while (session.FinalizedLevel < session.TargetLevel && !session.Aborted)
                    {
                        building.LevelUpCharacter(session.Hero, true, true);
                        levelUp.OriginScreen = null;
                        levelUp.CurrentHero = session.Hero;
                        levelUp.Show();
                        var previousLevel = session.FinalizedLevel;
                        yield return WaitForEdition(levelUp, session);

                        if (session.FinalizedLevel <= previousLevel)
                        {
                            session.Aborted = true;
                        }
                    }

                    if (session.Aborted)
                    {
                        yield break;
                    }

                    PreserveCharacterPoolData(originalHero, session.Hero);
                    yield return SaveCharacterPoolHero(session);
                }
                else
                {
                    adopted = FinalizeCampaignRespec(originalHero, session.Hero);
                }
            }
            finally
            {
                foreach (var coroutine in session.StageCoroutines)
                {
                    creation.StopCoroutine(coroutine);
                }

                session.RestoreIdentityControls();
                CloseEdition(creation, session.Hero);
                CloseEdition(levelUp, session.Hero);

                if (session.Hero != null)
                {
                    Tabletop2024Context.ClearPendingFeatSelections(session.Hero);
                    LevelUpHelper.UnregisterHero(session.Hero);
                    building.ReleaseCharacter(session.Hero, true);

                    if (!adopted)
                    {
                        ReleaseTransientHero(session.Hero);
                    }
                }

                creation.OriginScreen = originScreen;
                _session = null;
            }
        }

        private static IEnumerator WaitForEdition(CharacterEditionScreen screen, RespecSession session)
        {
            while (screen.currentHero == session.Hero)
            {
                yield return null;
            }

            while (screen.Visible || screen.Hiding)
            {
                yield return null;
            }
        }

        private static void CloseEdition(CharacterEditionScreen screen, RulesetCharacterHero hero)
        {
            if (hero == null || screen.currentHero != hero)
            {
                return;
            }

            screen.Hide(true);
            screen.CurrentHero = null;
        }

        internal static void PrepareFinish(CharacterEditionScreen screen, ref bool continueLevelUp)
        {
            if (_session != null && _session.Hero == screen.currentHero)
            {
                continueLevelUp = false;
            }
        }

        internal static void CharacterFinalized(RulesetCharacterHero hero)
        {
            if (_session != null && _session.Hero == hero)
            {
                _session.FinalizedLevel = hero.ClassesHistory.Count;
            }
        }

        internal static void Abort(CharacterEditionScreen screen)
        {
            if (_session != null && _session.Hero == screen.currentHero)
            {
                _session.Aborted = true;
            }
        }

        internal static bool ShouldSuppressCharacterPoolSave(RulesetCharacterHero hero) =>
            _session is { FromCharacterPool: true, Saving: false } session && session.Hero == hero;

        internal static bool IsOriginalName(RulesetCharacterHero hero, string name) =>
            _session != null && _session.Hero == hero && string.Equals(name, _session.OriginalHero.Name, StringComparison.Ordinal);

        internal static void StageEntered(CharacterEditionScreen screen, CharacterStagePanel panel)
        {
            var session = _session;

            if (session == null || session.Hero != screen.currentHero || screen is not CharacterCreationScreen)
            {
                return;
            }

            session.StageCoroutines.Add(screen.StartCoroutine(InitializeStage(screen, panel, session)));
        }

        private static IEnumerator InitializeStage(
            CharacterEditionScreen screen, CharacterStagePanel panel, RespecSession session)
        {
            // StageEntered precedes native transition and binding. Wait for that same stage's display.
            do
            {
                yield return null;

                if (_session != session || session.Aborted || screen.currentHero != session.Hero ||
                    screen.CurrentStagePanel != panel)
                {
                    yield break;
                }
            } while (!panel.Visible || panel.Showing);

            if (!session.InitializedStages.Add(panel))
            {
                if (panel is CharacterStageIdentityDefinitionPanel identity && session.FromCharacterPool)
                {
                    identity.firstNameInputField.text = session.Hero.Name = session.OriginalHero.Name;
                    identity.lastNameInputField.text = session.Hero.SurName = session.OriginalHero.SurName;
                    session.LockIdentityControls(identity);
                }

                yield break;
            }

            switch (panel)
            {
                case CharacterStageRaceSelectionPanel race:
                    RestoreRace(race, session.OriginalHero);
                    break;
                case CharacterStageClassSelectionPanel classes:
                    var classIndex = classes.compatibleClasses.IndexOf(session.OriginalHero.ClassesHistory.FirstOrDefault());
                    if (classIndex >= 0)
                    {
                        classes.selectedClass = classIndex;
                        classes.Refresh();
                    }
                    break;
                case CharacterStageBackgroundSelectionPanel background:
                    RestoreBackground(background, session.OriginalHero);
                    break;
                case CharacterStageAbilityScoresPanel abilities when !Main.Settings.ForcePointBuyAbilityScores:
                    RestoreRolledAbilityScores(abilities, session.OriginalHero);
                    break;
                case CharacterStageIdentityDefinitionPanel identity:
                    RestoreIdentity(identity, session.OriginalHero);
                    if (session.FromCharacterPool)
                    {
                        session.LockIdentityControls(identity);
                    }
                    break;
            }
        }

        private static void CopyRespecIdentityData(RulesetCharacterHero oldHero, RulesetCharacterHero newHero)
        {
            newHero.sex = oldHero.sex;
            newHero.SurName = oldHero.SurName;
            newHero.Name = oldHero.Name;
            newHero.raceDefinition = oldHero.RaceDefinition;
            newHero.subRaceDefinition = oldHero.SubRaceDefinition;
        }

        private static void CopyRespecAbilityScoresForDiceRollMode(
            RulesetCharacterHero oldHero, RulesetCharacterHero newHero)
        {
            foreach (var ability in AttributeDefinitions.AbilityScoreNames)
            {
                if (!oldHero.TryGetAttribute(ability, out _) || !newHero.TryGetAttribute(ability, out var score))
                {
                    continue;
                }

                var preserved = GetRespecAbilityScore(oldHero, ability);
                score.BaseValue = preserved.BaseValue;
                score.MaxValue = Math.Max(score.MaxValue, preserved.MaxValue);
                score.MaxEditableValue = score.MaxEditableValue <= 0 || preserved.MaxEditableValue <= 0
                    ? 0 : Math.Max(score.MaxEditableValue, preserved.MaxEditableValue);
                score.Refresh();
            }
        }

        private static void RestoreRace(CharacterStageRaceSelectionPanel panel, RulesetCharacterHero original)
        {
            var index = panel.eligibleRaces.IndexOf(original.RaceDefinition);

            if (index < 0)
            {
                return;
            }

            panel.selectedRace = index;

            if (panel.sortedSubRaces.TryGetValue(original.RaceDefinition, out var subraces))
            {
                var subrace = subraces.IndexOf(original.SubRaceDefinition);
                if (subrace >= 0)
                {
                    panel.selectedSubRace[index] = subrace;
                }
            }

            panel.Refresh();
            panel.CommonData.CharacterPlate.RefreshNameAndRace();
        }

        private static void RestoreBackground(
            CharacterStageBackgroundSelectionPanel panel, RulesetCharacterHero original)
        {
            var index = panel.compatibleBackgrounds.IndexOf(original.BackgroundDefinition);

            if (index < 0)
            {
                return;
            }

            panel.selectedBackground = index;
            var flags = panel.selectedBackgroundPersonalityFlagsMap[original.BackgroundDefinition];
            flags.Clear();
            flags.AddRange(original.BackgroundOptionalPersonalityFlags);
            panel.selectedAlignmentPersonalityFlags.Clear();
            panel.selectedAlignmentPersonalityFlags.AddRange(original.AlignmentOptionaPersonalityFlags);
            panel.Refresh();
        }

        private static void RestoreRolledAbilityScores(
            CharacterStageAbilityScoresPanel panel, RulesetCharacterHero original)
        {
            var scores = AttributeDefinitions.AbilityScoreNames
                .Select(ability => GetRespecBaseAbilityScore(original, ability)).OrderByDescending(score => score).ToArray();
            panel.rollValues.SetRange(scores);
            var available = scores.ToArray();
            var priorities = panel.guiCharacter.MainClassDefinition.AbilityScoresPriority;

            for (var i = 0; i < AttributeDefinitions.AbilityScoreNames.Length; i++)
            {
                var box = panel.abilityScoresTable.GetChild(i).GetComponent<AbilityRollBox>();
                var ability = box.AbilityScore;
                ability.BaseValue = GetRespecBaseAbilityScore(original, ability.Name);
                ability.Refresh(false);
                var index = Array.IndexOf(available, ability.BaseValue);
                available[index] = int.MinValue;
                panel.diceRollsTable.GetChild(index).GetComponent<AbilityRollBox>().Bind(index, 0, null);
                box.Bind(ability, index, ability.CurrentValue, priorities?.IndexOf(ability.Name) is >= 0 and < 3,
                    panel.OnScoreBoxDragStarted);
            }

            panel.Refresh();
        }

        private static void RestoreIdentity(
            CharacterStageIdentityDefinitionPanel panel, RulesetCharacterHero original)
        {
            panel.firstNameInputField.text = panel.currentHero.Name = original.Name;
            panel.lastNameInputField.text = panel.currentHero.SurName = original.SurName;
            panel.backstoryInputField.text = original.AdditionalBackstory;
            var options = panel.customizationOptionsBySelectedOrigin[
                panel.selectedOptionsByPart[MorphotypeElementDefinition.ElementCategory.Origin]];

            for (var i = 0; i < panel.modifiers.Count; i++)
            {
                var modifier = panel.modifiers[i];
                var category = MorphotypeElementDefinition.CommonCategories[i];

                if (!modifier.gameObject.activeSelf || !original.MorphotypeElements.TryGetValue(category, out var value))
                {
                    continue;
                }

                if (modifier.sliderGroup.gameObject.activeSelf &&
                    original.MorphotypeElementAdditionalValues.TryGetValue(category, out var amount))
                {
                    modifier.slider.value = amount;
                    modifier.OnSliderCb();
                    modifier.OnSliderDragEndCb();
                    continue;
                }

                if (!options.TryGetValue(category, out var values))
                {
                    continue;
                }

                var index = values.IndexOf(value);
                if (index < 0)
                {
                    continue;
                }

                if (modifier.colorsGroup.gameObject.activeSelf)
                {
                    modifier.OnColorSelected(index);
                }
                else if (index < modifier.valuesList.Count)
                {
                    if (modifier.selectorGroup.gameObject.activeSelf || modifier.gamepadSelector.gameObject.activeSelf)
                    {
                        // An unavailable morph must not trap the UI in an unbounded selector loop.
                        for (var step = 0; step < modifier.valuesList.Count && modifier.currentValue != index; step++)
                        {
                            modifier.OnSelectNext();
                        }
                    }
                    else if (modifier.imagesGroup.gameObject.activeSelf)
                    {
                        modifier.OnImageSelected(index);
                    }
                }
            }

            var voice = panel.compatibleVoices.FindIndex(definition => definition.Name == original.voiceID);
            if (voice >= 0)
            {
                panel.voiceModifier.OnLabelSelected(voice);
            }

            var pronoun = new List<Gui.LocalizationSpeakerGender> { Female, Male, NonBinary }.IndexOf(original.pronoun);
            if (pronoun >= 0)
            {
                panel.pronounModifier.OnLabelSelected(pronoun);
            }

            panel.CommonData.CharacterPlate.CommonBind(new GuiCharacter(panel.currentHero));
            panel.Refresh();
        }

        private static void PreserveCharacterPoolData(RulesetCharacterHero original, RulesetCharacterHero hero)
        {
            var inventory = original.CharacterInventory;
            PreserveRebuiltSpellbooks(inventory, hero.CharacterInventory);
            original.CharacterInventory = hero.CharacterInventory;
            hero.CharacterInventory = inventory;
            hero.CharacterInventory.BearerGuid = hero.Guid;
            var items = new List<RulesetItem>();
            inventory.EnumerateAllItems(items);
            foreach (var item in items.Distinct())
            {
                item.BearerGuid = hero.Guid;
            }

            foreach (var repertoire in hero.SpellRepertoires)
            {
                repertoire.CharacterInventory = inventory;
            }

            hero.BuiltIn = false;
            hero.Name = original.Name;
            hero.SurName = original.SurName;
            CopyPersistentHeroData(original, hero);
            hero.RefreshAll();
            hero.currentHitPoints = hero.TryGetAttributeValue(AttributeDefinitions.HitPoints);
        }

        private static void CopyPersistentHeroData(RulesetCharacterHero original, RulesetCharacterHero hero)
        {
            hero.Tags.AddRange(original.Tags.Where(tag => !hero.Tags.Contains(tag)));
            var experience = hero.GetAttribute(AttributeDefinitions.Experience);
            experience.BaseValue = original.GetAttribute(AttributeDefinitions.Experience).BaseValue;
            experience.Refresh();
            hero.criticalHits = original.criticalHits;
            hero.criticalFailures = original.criticalFailures;
            hero.inflictedDamage = original.inflictedDamage;
            hero.slainEnemies = original.slainEnemies;
            hero.sustainedInjuries = original.sustainedInjuries;
            hero.restoredHealth = original.restoredHealth;
            hero.usedMagicAndPowers = original.usedMagicAndPowers;
            hero.knockOuts = original.knockOuts;
        }

        private static void PreserveRebuiltSpellbooks(RulesetInventory inventory, RulesetInventory rebuiltInventory)
        {
            var items = new List<RulesetItem>();
            inventory.EnumerateAllItems(items);
            var books = items.OfType<RulesetItemSpellbook>().ToList();
            rebuiltInventory.EnumerateAllItems(items);
            var rebuiltBooks = items.OfType<RulesetItemSpellbook>().Except(books).ToArray();
            var factory = ServiceRepository.GetService<IRulesetItemFactoryService>();

            foreach (var rebuilt in rebuiltBooks)
            {
                foreach (var spell in rebuilt.ScribedSpells)
                {
                    if (books.Any(book => book.ScribedSpells.Contains(spell)))
                    {
                        continue;
                    }

                    var destination = books.FirstOrDefault(book => book.CanScribeSpell(spell));
                    if (destination == null)
                    {
                        destination = (RulesetItemSpellbook)factory.CreateStandardItem(rebuilt.ItemDefinition);
                        inventory.PersonalContainer.AddSubItem(destination);
                        books.Add(destination);
                    }

                    destination.ScribeSpell(spell);
                }
            }
        }

        private static IEnumerator SaveCharacterPoolHero(RespecSession session)
        {
            // SaveCharacter requests only a cached photo; creation releases that cache when it closes.
            var photoReady = false;
            ServiceRepository.GetService<IGraphicsCharacterPhotoService>().RequestCharacterPhoto(
                session.Hero, _ => photoReady = true, false, 256, 384,
                default(GraphicsCharacterPhotoManager.PhotoParameters));

            while (!photoReady)
            {
                yield return null;
            }

            var pool = ServiceRepository.GetService<ICharacterPoolService>();
            var completed = false;
            void Saved() => completed = true;
            pool.CharacterPoolRefreshed += Saved;

            try
            {
                session.Saving = true;
                pool.SaveCharacter(session.Hero, true);

                while (!completed)
                {
                    yield return null;
                }
            }
            finally
            {
                pool.CharacterPoolRefreshed -= Saved;
                session.Saving = false;
            }
        }

        private static bool FinalizeCampaignRespec(RulesetCharacterHero oldHero, RulesetCharacterHero newHero)
        {
            var characterManager = ServiceRepository.GetService<IGameLocationCharacterService>() as GameLocationCharacterManager;
            var gameLocationCharacter = GameLocationCharacter.GetFromActor(oldHero);
            var campaignCharacters = Gui.GameCampaign?.Party.CharactersList;
            var campaignCharacter = campaignCharacters?.Find(character => character.RulesetCharacter == oldHero);

            if (!characterManager || gameLocationCharacter == null || campaignCharacter == null)
            {
                return false;
            }

            var oldGuid = oldHero.Guid;
            var newGuid = newHero.Guid;
            EffectHelpers.GetAllEffectsBySourceGuid(oldGuid).ForEach(effect => effect.DoTerminate(oldHero));
            EffectHelpers.GetAllEffectsBySourceGuid(newGuid).ForEach(effect => effect.SetGuid(oldGuid));
            EffectHelpers.GetAllConditionsBySourceGuid(newGuid).ForEach(condition => condition.sourceGuid = oldGuid);
            newHero.Unregister();
            ServiceRepository.GetService<IRulesetEntityService>().SwapEntities(oldHero, newHero);
            newHero.Register(false);
            CopyPersistentHeroData(oldHero, newHero);
            TransferConditionsOfCategory(oldHero, newHero, AttributeDefinitions.TagEffect);
            CleanupOldHeroConditions(oldHero, newHero);
            DropInventoryOnFloor(gameLocationCharacter);
            campaignCharacter.RulesetCharacter = newHero;
            UpdateRestPanelUi(campaignCharacters);
            gameLocationCharacter.SetRuleset(newHero);
            var factory = ServiceRepository.GetService<IWorldLocationEntityFactoryService>();
            if (factory.TryFindWorldCharacter(gameLocationCharacter, out var worldCharacter))
            {
                worldCharacter.GraphicsCharacter.RulesetCharacter = newHero;
            }

            characterManager.dirtyParty = true;
            characterManager.RefreshAllCharacters();
            Gui.GuiService.ShowMessage(
                MessageModal.Severity.Informative1,
                "RestActivity/&RestActivityRespecTitle", "Message/&RespecSuccessfulDescription",
                "Message/&MessageOkTitle", string.Empty, null, null);
            return true;
        }

        private static void ReleaseTransientHero(RulesetCharacterHero hero)
        {
            if (hero == null)
            {
                return;
            }

            var entities = ServiceRepository.GetService<IRulesetEntityService>()?.RulesetEntities;
            if (entities == null)
            {
                return;
            }

            var owned = new List<RulesetEntity> { hero, hero.PersonalLightSource };
            owned.AddRange(hero.AllConditions);
            var inventory = hero.CharacterInventory;
            if (inventory != null)
            {
                owned.Add(inventory.PersonalContainer);
                owned.AddRange(inventory.EnumerateAllSlots(true, false, false));
                var items = new List<RulesetItem>();
                inventory.EnumerateAllItems(items);
                owned.AddRange(items);
            }

            // Unregister only this transient graph, including native-created slots with a zero-guid owner.
            foreach (var entity in owned.Where(entity => entity != null).Distinct())
            {
                if (entities.TryGetValue(entity.Guid, out var registered) && ReferenceEquals(registered, entity))
                {
                    ServiceRepository.GetService<IRulesetEntityService>().UnregisterEntity(entity);
                }
            }
        }

        private sealed class RespecSession(RulesetCharacterHero originalHero, bool fromCharacterPool)
        {
            internal readonly RulesetCharacterHero OriginalHero = originalHero;
            internal readonly bool FromCharacterPool = fromCharacterPool;
            internal readonly int TargetLevel = Math.Max(1, originalHero.ClassesHistory.Count);
            internal readonly List<UnityEngine.Coroutine> StageCoroutines = [];
            internal readonly HashSet<CharacterStagePanel> InitializedStages = [];
            internal RulesetCharacterHero Hero;
            internal int FinalizedLevel;
            internal bool Aborted;
            internal bool Saving;
            private CharacterStageIdentityDefinitionPanel _identity;
            private (bool FirstReadOnly, bool LastReadOnly, bool FirstRoll, bool LastRoll, bool FirstEdit, bool LastEdit) _identityControls;

            internal void LockIdentityControls(CharacterStageIdentityDefinitionPanel panel)
            {
                if (!_identity)
                {
                    _identity = panel;
                    _identityControls = (panel.firstNameInputField.readOnly, panel.lastNameInputField.readOnly,
                        panel.firstNameRollButton.interactable, panel.lastNameRollButton.interactable,
                        panel.firstNameEditButton.interactable, panel.lastNameEditButton.interactable);
                }

                panel.firstNameInputField.readOnly = true;
                panel.lastNameInputField.readOnly = true;
                panel.firstNameRollButton.interactable = false;
                panel.lastNameRollButton.interactable = false;
                panel.firstNameEditButton.interactable = false;
                panel.lastNameEditButton.interactable = false;
            }

            internal void RestoreIdentityControls()
            {
                if (!_identity)
                {
                    return;
                }

                _identity.firstNameInputField.readOnly = _identityControls.FirstReadOnly;
                _identity.lastNameInputField.readOnly = _identityControls.LastReadOnly;
                _identity.firstNameRollButton.interactable = _identityControls.FirstRoll;
                _identity.lastNameRollButton.interactable = _identityControls.LastRoll;
                _identity.firstNameEditButton.interactable = _identityControls.FirstEdit;
                _identity.lastNameEditButton.interactable = _identityControls.LastEdit;
            }
        }

        private static int GetRespecBaseAbilityScore(RulesetCharacterHero hero, string ability) =>
            GetRespecAbilityScore(hero, ability).BaseValue;

        private static (int BaseValue, int MaxValue, int MaxEditableValue) GetRespecAbilityScore(
            RulesetCharacterHero hero, string ability)
        {
            if (!hero.TryGetAttribute(ability, out var attribute))
            {
                return (10, 0, 0);
            }

            // Recreating a hero grants construction bonuses again. Keep external
            // base and cap changes, but leave those construction increments behind.
            var increases = hero.GetSubFeaturesByType<ICustomLevelUpState>().Distinct()
                .Select(state => state.GetAttributeIncreases(hero, ability)).ToArray();
            return (attribute.BaseValue - increases.Sum(value => value.BaseValue),
                attribute.MaxValue - increases.Sum(value => value.MaxValue),
                attribute.MaxEditableValue - increases.Sum(value => value.MaxEditableValue));
        }

        private static void TransferConditionsOfCategory(RulesetActor oldHero, RulesetActor newHero, string category)
        {
            if (!oldHero.ConditionsByCategory.TryGetValue(category, out var conditions))
            {
                return;
            }

            // Construction markers belong to the old feature application. The
            // new hero recreates them while learning its own features.
            var rebuiltConditions = oldHero is RulesetCharacterHero hero
                ? hero.GetSubFeaturesByType<ICustomLevelUpState>().Distinct()
                    .SelectMany(state => state.GetStateConditions(hero)).ToHashSet()
                : [];
            var transferredConditions = conditions.Where(condition => !rebuiltConditions.Contains(condition)).ToArray();

            newHero.AddConditionCategoryAsNeeded(category);
            newHero.AllConditions.AddRange(transferredConditions);
            newHero.ConditionsByCategory[category].AddRange(transferredConditions);
        }

        private static void CleanupOldHeroConditions(RulesetCharacterHero oldHero, RulesetCharacterHero newHero)
        {
            //Unregister all conditions that are not present in new hero
            oldHero.ConditionsByCategory
                .SelectMany(x => x.Value)
                .Where(c => !newHero.ConditionsByCategory.SelectMany(x => x.Value).Contains(c))
                .ToArray()
                .Do(c => c.Unregister());
            oldHero.AllConditions.Clear();
            oldHero.ConditionsByCategory.Clear();
        }

        private static void DropInventoryOnFloor([NotNull] GameLocationCharacter oldCharacter)
        {
            var oldHero = oldCharacter.RulesetCharacter;
            var position = oldCharacter.LocationPosition;
            var personalSlots = oldHero.CharacterInventory.PersonalContainer.InventorySlots;
            var slotsByName = oldHero.CharacterInventory.InventorySlotsByName;

            foreach (var equipedItem in personalSlots.Select(i => i.EquipedItem).Where(i => i != null))
            {
                DropItem(equipedItem, position);
            }

            foreach (var equipedItem in slotsByName.Select(s => s.Value.EquipedItem).Where(i => i != null))
            {
                DropItem(equipedItem, position);
            }
        }

        private static void DropItem(RulesetItem equipedItem, int3 position)
        {
            var inventoryCommandService = ServiceRepository.GetService<IInventoryCommandService>();

            equipedItem.AttunedToCharacter = string.Empty;

            if (equipedItem is RulesetItemSpellbook spellbook)
            {
                foreach (var scrollDefinition in spellbook.ScribedSpells
                             .Select(spellDefinition =>
                                 DatabaseRepository.GetDatabase<ItemDefinition>()
                                     .FirstOrDefault(item =>
                                         item.IsUsableDevice &&
                                         item.UsableDeviceDescription.UsableDeviceTags.Contains("Scroll") &&
                                         item.UsableDeviceDescription.DeviceFunctions.Any(function =>
                                             function.SpellDefinition == spellDefinition)))
                             .Where(scrollDefinition => scrollDefinition))
                {
                    var rulesetItem = new RulesetItem(scrollDefinition);

                    inventoryCommandService.CreateItemAtPosition(rulesetItem, position);
                }
            }
            else
            {
                inventoryCommandService.CreateItemAtPosition(equipedItem, position);
            }
        }

        private static void UpdateRestPanelUi(List<GameCampaignCharacter> gameCampaignCharacters)
        {
            var restModalScreen = Gui.GuiService.GetScreen<RestModal>();
            var restAfterPanel = restModalScreen.restAfterPanel;
            var characterPlatesTable = restAfterPanel.characterPlatesTable;

            for (var index = 0; index < characterPlatesTable.childCount; ++index)
            {
                var child = characterPlatesTable.GetChild(index);
                var component = child.GetComponent<CharacterPlateGame>();

                component.Unbind();

                if (index < gameCampaignCharacters.Count)
                {
                    component.Bind(gameCampaignCharacters[index].RulesetCharacter,
                        TooltipDefinitions.AnchorMode.BOTTOM_CENTER);
                    component.Refresh();
                }

                child.gameObject.SetActive(index < gameCampaignCharacters.Count);
            }
        }
    }
}
