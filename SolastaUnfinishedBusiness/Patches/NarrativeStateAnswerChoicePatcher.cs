using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NarrativeStateAnswerChoicePatcher
{
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    [HarmonyPatch(typeof(NarrativeStateAnswerChoice), nameof(NarrativeStateAnswerChoice.Begin))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Begin_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NarrativeStateAnswerChoice __instance)
        {
            if (!Global.IsMultiplayer ||
                !Main.Settings.EnableAlternateVotingSystem ||
                !Main.Settings.EnableSumD20OnAlternateVotingSystem)
            {
                return;
            }

            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

            var actorLine = string.Empty;

            if (__instance.narrativeSequence.AdventureLogInfos.Count > 0)
            {
                actorLine = __instance.narrativeSequence.AdventureLogInfos.Last().ActorLine;
            }

            var console = Gui.Game.GameConsole;
            var entry = new GameConsoleEntry(actorLine, console.consoleTableDefinition) { Indent = false };

            console.AddEntry(entry);

            for (var voterHeroIndex = 0; voterHeroIndex < characterService.PartyCharacters.Count; voterHeroIndex++)
            {
                var gameLocationCharacter = characterService.PartyCharacters[voterHeroIndex];
                var dieRoll = ComputeStableVotingD20(__instance, gameLocationCharacter, voterHeroIndex);

                entry = new GameConsoleEntry("Feedback/&NarrativeChoiceRoll",
                    console.consoleTableDefinition) { Indent = true };

                console.AddCharacterEntry(gameLocationCharacter.RulesetCharacter, entry);
                entry.AddParameter(
                    ConsoleStyleDuplet.ParameterType.Positive, Gui.FormatDieTitle(RuleDefinitions.DieType.D20));
                entry.AddParameter(
                    ConsoleStyleDuplet.ParameterType.Positive, dieRoll.ToString());
                console.AddEntry(entry);
            }
        }
    }

    [HarmonyPatch(typeof(NarrativeStateAnswerChoice), nameof(NarrativeStateAnswerChoice.GetVoteWinner))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetVoteWinner_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(NarrativeStateAnswerChoice __instance, ref int selectedIndex, ref bool everyoneVoted)
        {
            if (!Global.IsMultiplayer ||
                !Main.Settings.EnableAlternateVotingSystem)
            {
                return true;
            }

            // compute weights using charisma modifier
            var computedVotes = 0;
            var networkingService = ServiceRepository.GetService<INetworkingService>();
            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
            var playersInRoom = networkingService.GetPlayersInRoom();
            var votes = new Dictionary<int, int>();
            var partyCharacters = characterService.PartyCharacters;

            foreach (var current in __instance.playerVotesPerChoice.OrderBy(x => x.Key))
            {
                var choiceIndex = current.Key;
                var voterHeroIndexes = current.Value;

                if (choiceIndex < 0 ||
                    voterHeroIndexes == null)
                {
                    continue;
                }

                votes.TryAdd(choiceIndex, 0);

                foreach (var voterHeroIndex in voterHeroIndexes)
                {
                    if (voterHeroIndex < 0 ||
                        voterHeroIndex >= partyCharacters.Count)
                    {
                        continue;
                    }

                    var hero = partyCharacters[voterHeroIndex];
                    var charismaModifier = AttributeDefinitions.ComputeAbilityScoreModifier(
                        hero.RulesetCharacter.TryGetAttributeValue(AttributeDefinitions.Charisma));

                    ++computedVotes;

                    votes[choiceIndex] += charismaModifier;

                    if (Main.Settings.EnableSumD20OnAlternateVotingSystem)
                    {
                        votes[choiceIndex] += ComputeStableVotingD20(__instance, hero, voterHeroIndex);
                    }
                }
            }

            // determine highest selection
            var maxWeight = int.MinValue;

            selectedIndex = 0;

            foreach (var vote in votes.OrderBy(x => x.Key))
            {
                var choiceIndex = vote.Key;
                var weight = vote.Value;

                if (weight <= maxWeight)
                {
                    continue;
                }

                maxWeight = weight;
                selectedIndex = choiceIndex;
            }

            everyoneVoted = playersInRoom.Count <= computedVotes;

            return false;
        }
    }

    private static int ComputeStableVotingD20(
        NarrativeStateAnswerChoice instance,
        GameLocationCharacter gameLocationCharacter,
        int voterHeroIndex)
    {
        var networkingService = ServiceRepository.GetService<INetworkingService>();
        var hash = FnvOffsetBasis;

        AddHash(ref hash, networkingService?.RoomRandomSeed ?? 0);
        AddHash(ref hash, gameLocationCharacter.Guid);
        AddHash(ref hash, voterHeroIndex);
        AddHash(ref hash, instance.narrativeSequence.AdventureLogInfos.Count);

        return (int)(hash % 20) + 1;
    }

    private static void AddHash(ref uint hash, int value)
    {
        AddHash(ref hash, unchecked((uint)value));
    }

    private static void AddHash(ref uint hash, ulong value)
    {
        for (var i = 0; i < sizeof(ulong); i++)
        {
            AddHashByte(ref hash, (byte)(value >> (i * 8)));
        }
    }

    private static void AddHash(ref uint hash, uint value)
    {
        for (var i = 0; i < sizeof(uint); i++)
        {
            AddHashByte(ref hash, (byte)(value >> (i * 8)));
        }
    }

    private static void AddHashByte(ref uint hash, byte value)
    {
        unchecked
        {
            hash ^= value;
            hash *= FnvPrime;
        }
    }
}
