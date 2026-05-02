using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using Random = System.Random;

namespace SolastaUnfinishedBusiness.Models;

internal static class SpeechContext
{
    internal const int MaxHeroes = 6;

    private const string DefaultVoice = "No Voice";
    private const float DefaultScale = 0.8f;

    private const string VoicesURLPrefix = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/";

    private const string PiperLinuxDownloadURL =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_linux_x86_64.tar.gz";

    private const string PiperOSXDownloadURL =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_macos_x64.tar.gz";

    private const string PiperWindowsDownloadURL =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";

    private const string PiperPlusWindowsX64DownloadURL =
        "https://github.com/ayutaz/piper-plus/releases/download/v1.11.0/piper-windows-x64.zip";

    private const string NarratorVoiceKey = "NARRATOR";

    private static readonly Regex RemoveNpcSpeechTags =
        new(@"<[bci/].*?>|\*.+?\*|\(.+?\)|\[.+?\]|\{.+?\}", RegexOptions.Compiled);

    private static readonly string PiperFolder =
        Path.Combine(
            Main.ModFolder,
            Path.Combine(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "piper_linux_x86_64" // linux unzips to piper_linux_x86_64/piper folder
                    : Path.Combine(
                        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                            ? "piper_macos_x64" // macos unzips to piper_macos_x64/piper folder
                            : "."), // windows unzips to ./piper folder
                "piper"));

    private static readonly string VoicesFolder = Path.Combine(Main.ModFolder, Path.Combine("..", "Voices"));

    private static readonly string PiperPlusFolder = Path.Combine(Main.ModFolder, "piper_plus");

    private static readonly string PiperPlusModelsFolder = Path.Combine(VoicesFolder, "PiperPlus");

    [NotNull] private static WaveOutEvent SpeechEvent => _speechEvent ??= new WaveOutEvent();

    private static readonly object SpeechLock = new();
    private static readonly object SpeechProcessLock = new();

    private static readonly Dictionary<string, SpeechVoiceInfo> VoiceInfos = new(StringComparer.Ordinal);
    private static readonly List<Process> SpeechProcesses = [];

    private static WaveOutEvent _speechEvent;
    private static MemoryStream CurrentAudioStream;
    private static WaveStream CurrentWaveStream;
    private static SpeechRequest CurrentSpeechRequest;
    private static bool SpeechEventHooked;
    private static bool SpeechPlaybackActive;
    private static bool Unloading;
    private static int SpeechGeneration;

    private static readonly Queue<SpeechRequest> SpeechQueue = [];

    internal enum SpeechEngine
    {
        LegacyPiper,
        PiperPlus
    }

    internal enum Gender
    {
        Male,
        Female
    }

    internal sealed class SpeechLanguageProfile(
        string languageCode,
        string displayNameKey,
        string cliLanguageArgument,
        string sampleText,
        Func<string, bool> matchesText)
    {
        internal string LanguageCode { get; } = languageCode;
        internal string DisplayNameKey { get; } = displayNameKey;
        internal string CliLanguageArgument { get; } = cliLanguageArgument;
        internal string SampleText { get; } = sampleText;

        internal bool MatchesText(string text)
        {
            return matchesText(text);
        }
    }

    internal sealed class SpeechVoiceProfile(
        string voiceName,
        SpeechEngine engine,
        string modelArgument,
        string modelSearchPattern,
        Gender? gender,
        int sampleRate,
        float noiseScale,
        SpeechLanguageProfile languageProfile)
    {
        internal string VoiceName { get; } = voiceName;
        internal SpeechEngine Engine { get; } = engine;
        internal string ModelArgument { get; } = modelArgument;
        internal string ModelSearchPattern { get; } = modelSearchPattern;
        internal Gender? Gender { get; } = gender;
        internal int SampleRate { get; } = sampleRate;
        internal float NoiseScale { get; } = noiseScale;
        internal SpeechLanguageProfile LanguageProfile { get; } = languageProfile;
    }

    private sealed class SpeechVoiceInfo(
        string name,
        SpeechEngine engine,
        string language,
        string modelArgument,
        Gender? gender,
        int sampleRate,
        float noiseScale,
        SpeechLanguageProfile languageProfile = null)
    {
        internal string Name { get; } = name;
        internal SpeechEngine Engine { get; } = engine;
        internal string Language { get; } = language;
        internal string ModelArgument { get; } = modelArgument;
        internal Gender? Gender { get; } = gender;
        internal int SampleRate { get; } = sampleRate;
        internal float NoiseScale { get; } = noiseScale;
        internal SpeechLanguageProfile LanguageProfile { get; } = languageProfile;
    }

    private sealed class SpeechAudio(MemoryStream audioStream, bool isWaveFile, int sampleRate) : IDisposable
    {
        internal MemoryStream AudioStream { get; } = audioStream;
        internal bool IsWaveFile { get; } = isWaveFile;
        internal int SampleRate { get; } = sampleRate;

        public void Dispose()
        {
            AudioStream.Dispose();
        }
    }

    private sealed class SpeechRequest(SpeechVoiceInfo voiceInfo, float scale, string cleanedText, int generation)
    {
        internal SpeechVoiceInfo VoiceInfo { get; } = voiceInfo;
        internal float Scale { get; } = scale;
        internal string CleanedText { get; } = cleanedText;
        internal int Generation { get; } = generation;
    }

    private static readonly SpeechLanguageProfile JapaneseLanguageProfile = new(
        "ja",
        "ModUi/&SpeechLanguageJapanese",
        "ja-en-zh-es-fr-pt",
        "こんにちは。今日は良い天気ですね。",
        MatchesJapaneseText);

    private static readonly SpeechLanguageProfile[] LanguageProfiles =
    [
        JapaneseLanguageProfile
    ];

    private static readonly SpeechVoiceProfile[] PiperPlusVoiceProfiles =
    [
        new(
            "ja_JP-tsukuyomi-chan-medium",
            SpeechEngine.PiperPlus,
            "tsukuyomi",
            "*tsukuyomi*.onnx",
            Gender.Female,
            22050,
            0.5f,
            JapaneseLanguageProfile),
        new(
            "ja_JP-css10-medium",
            SpeechEngine.PiperPlus,
            "css10",
            "*css10*.onnx",
            null,
            22050,
            0.667f,
            JapaneseLanguageProfile)
    ];

    internal static IReadOnlyList<SpeechLanguageProfile> DownloadableLanguageProfiles => LanguageProfiles;

    private static readonly (string, Gender)[] SuggestedVoicesUrls =
    [
        ($"{VoicesURLPrefix}en/en_GB/alan/medium/en_GB-alan-medium", Gender.Male),
        ($"{VoicesURLPrefix}en/en_GB/alba/medium/en_GB-alba-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_GB/aru/medium/en_GB-aru-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_GB/cori/medium/en_GB-cori-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_GB/jenny_dioco/medium/en_GB-jenny_dioco-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_GB/northern_english_male/medium/en_GB-northern_english_male-medium", Gender.Male),
        ($"{VoicesURLPrefix}en/en_GB/semaine/medium/en_GB-semaine-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_GB/vctk/medium/en_GB-vctk-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_US/amy/medium/en_US-amy-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_US/arctic/medium/en_US-arctic-medium", Gender.Male),
        ($"{VoicesURLPrefix}en/en_US/bryce/medium/en_US-bryce-medium", Gender.Male),
        ($"{VoicesURLPrefix}en/en_US/hfc_female/medium/en_US-hfc_female-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_US/hfc_male/medium/en_US-hfc_male-medium", Gender.Male),
        ($"{VoicesURLPrefix}en/en_US/joe/medium/en_US-joe-medium", Gender.Male),
        ($"{VoicesURLPrefix}en/en_US/john/medium/en_US-john-medium", Gender.Male),
        ($"{VoicesURLPrefix}en/en_US/kristin/medium/en_US-kristin-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_US/kusal/medium/en_US-kusal-medium", Gender.Male),
        ($"{VoicesURLPrefix}en/en_US/lessac/medium/en_US-lessac-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_US/libritts_r/medium/en_US-libritts_r-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_US/ljspeech/medium/en_US-ljspeech-medium", Gender.Female),
        ($"{VoicesURLPrefix}en/en_US/norman/medium/en_US-norman-medium", Gender.Male),
        ($"{VoicesURLPrefix}en/en_US/ryan/medium/en_US-ryan-medium", Gender.Male)
        //("https://huggingface.co/quarterturn/kuroki_tomoko_en_piper/resolve/main/kuroki_tomoko", Gender.Female)
    ];

    private static readonly HashSet<string> FemaleNpcs = new(StringComparer.Ordinal)
    {
        "Aristocrat_Adria",
        "Aristocrat_Lyria",
        "Atima_Bladeburn",
        "Beryl_Stonebeard",
        "Bitterroot",
        "Caer_Cyflen_Guard",
        "CaerCyflenCityGuard_NewEmpire_Female",
        "Captain_Verissa_Ironshell",
        "Ceiwad_Silverflower",
        "Circe",
        "Council_Trooper",
        "CultistGuard",
        "Daliat_Sunbird",
        "DLC1_Complex_NPC_Guard_Recruit",
        "DLC1_Complex_NPC_Guard_Watcher",
        "DLC1_Complex_NPC_Trainer_01",
        "DLC1_NPC_2_Marches_Helia_Fairblade",
        "DLC1_NPC_3_Marches_Rogue_Leyrin_Catpaw",
        "DLC1_NPC_Armorer_Gail_Hunt",
        "DLC1_NPC_CityGuard_Captain_ThePeople04",
        "DLC1_NPC_Finaliel",
        "DLC1_NPC_Forge_Ravener",
        "DLC1_NPC_Malariel",
        "DLC1_NPC_Mask_CafrainShadow",
        "DLC1_NPC_Merchant_Mask_Yasmin",
        "DLC1_NPC_Rebelion_Ellaria_Anfarel",
        "DLC1_NPC_Rebellion_Blue",
        "DLC1_NPC_Rebellion_Red",
        "DLC1_NPC_Rebellion_Sima_Temple",
        "DLC1_NPC_ThePeople_Karelia",
        "DLC1_NPC_ThePeople_Reya",
        "DLC1_NPC_ThePeople_Rose",
        "DLC1_NPC_ThePeople_Tortured",
        "DLC1_NPC_Witch_Neutral",
        "DLC1_Orc_Shaman_Leader",
        "DLC1_Valley_NPC_Samko_Flint",
        "DLC3_Berghild_StrongSpine",
        "DLC3_Beryl_Stonebeard",
        "DLC3_Council_Trooper_1",
        "DLC3_ElvenClans_DragonbornIntermediate",
        "DLC3_ElvenClans_ElfAdvisor2",
        "DLC3_ElvenClans_Leralyn",
        "DLC3_Gallivan_Royals_TheCousin",
        "DLC3_Gallivan_Royals_TheQueen",
        "DLC3_Gallivan_Suspect01",
        "DLC3_Gallivan_Suspect02",
        "DLC3_GarradSoldier02",
        "DLC3_GarradSoldier03",
        "DLC3_GarradSoldier0C",
        "DLC3_Grimhild_DarkHead",
        "DLC3_Kara_WiseHead",
        "DLC3_Lena_Switfhand",
        "DLC3_Lisbath_Townsend",
        "DLC3_Misouk",
        "DLC3_NPC_Crowd8_DLC3_Ending",
        "DLC3_NPC_Einareum_Merchant_General",
        "DLC3_NPC_Einareum_Merchant_Weapons",
        "DLC3_NPC_ElvenClans_Greybear_Hunter",
        "DLC3_NPC_ElvenClans_Guard",
        "DLC3_NPC_ElvenClans_GuardCaptain",
        "DLC3_NPC_GenericScavengerScout",
        "DLC3_NPC_Helia_Fairblade",
        "DLC3_NPC_HumanClans_Guard",
        "DLC3_NPC_HumanClans1_DLC3_Ending",
        "DLC3_NPC_HumanClansLeader",
        "DLC3_NPC_Narrator_DLC3_Ending",
        "DLC3_NPC_NorthernClans_Merchant_Ingredients",
        "DLC3_NPC_SouthernClans_Caretaker",
        "DLC3_NPC_SouthernClans_Cousin_Kaikonnen",
        "DLC3_NPC_SouthernClans_Innkeeper",
        "DLC3_NPC_SouthernClans_Merchant_Clan",
        "DLC3_NPC_SouthernClans_Merchant_General",
        "DLC3_NPC_SouthernClans_Merchant_Ingredients",
        "DLC3_NPC_SouthernClans_Merchant_Scavenger",
        "DLC3_NPC_WhiteCity_Guard_Captain",
        "DLC3_NPC_WhiteCity_Trapper_Family_01",
        "DLC3_Undermountain_EttivenGuard",
        "DLC3_Undermountain_Investigation_Informant",
        "DLC3_Undermountain_PerlevinnGuard_Banter",
        "DLC3_Vigdis_Kaikonnen",
        "DLC3_Violet_Goodcheer",
        "DLC3_WhiteCity_MotherYoungDwarf",
        "DLC3_WhiteCity_YoungDwarf",
        "Heather_Merran",
        "Hertha_Gormsdottir",
        "Joriel_Foxeye",
        "Kebra",
        "Kythaela",
        "Leira_Kean",
        "Lena",
        "Lisbath_Townsend",
        "Maddy_Greenisle",
        "Maid_Coparann",
        "Mayor_Kiaradth_Bright-Spark",
        "Merchant_Annie_Bagmordah",
        "Merchant_Gorim_Ironsoot",
        "Milan",
        "Mildred_Warmhearth",
        "Philosopher_Illoreth",
        "Priestess_Of_Pakri_Elaine_Velasco"
    };

    private static readonly string[] Quotes =
    [
        "{Subject} does in fact use a stunt double, but only for crying scenes.",
        "{Subject} doesn't flush the toilet, he scares the crap out of it.",
        "{Subject} went skydiving and his parachute didn't open, he took it back for a refund.",
        "{Subject} was awarded the Nobel Peace Prize, for letting so many people live.",
        "{Subject}'s computer doesn't have a backspace key.",
        "{Subject} once had a fight with Superman. The loser had to wear his underpants on the outside.",
        "{Subject} once won a game of Connect Four in three moves.",
        "{Subject} can make sticks by rubbing two fires together.",
        "{Subject} once took a lie detector test. The machine confessed everything.",
        "{Subject} can fold airplanes into paper.",
        "{Subject} has no chin, under his beard is just another fist with an equally powerful beard.",
        "{Subject} can gargle peanut butter.",
        "{Subject} picked an apple from an orange tree and made lemonade.",
        "{Subject} is so fast he can run around the world and punch himself in the back of the head.",
        "{Subject} can put a plane in reverse.",
        "{Subject} is able to build a snowman out of water.",
        "{Subject} didn't call the wrong number, you answered the wrong phone.",
        "{Subject} didn't cheat death, he won fairly and squarely.",
        "{Subject} walked into chemistry class and ripped the Periodic Table of Elements off of the wall. Why? Because the only element {Subject} needs is the element of surprise.",
        "{Subject} once wrestled a bear, an alligator, and a tiger all at once. He won by tying them together with an anaconda.",
        "{Subject} was once bitten by a poisonous snake. And after a week of excruciating pain, the snake died.",
        "There are no streets named after {Subject} because no one would ever cross {Subject}",
        "{Subject}'s mother tried to have an abortion. The procedure resulted in the doctor being knocked unconscious by {Subject}.",
        "When alexander graham bell first invented the telephone he had three missed calls from {Subject}",
        "{Subject} doesn't worry about gas prices, his vehicles run on fear.",
        "{Subject} doesn't pay taxes, taxes pay {Subject}.",
        "{Subject} once had an arm wrestling contest with superman. I'm not going to say who won, but the loser had to wear his underwear on the outside for the rest of his life.",
        "When {Subject} was born the doctor asked him to name his parents.",
        "The laws of physics always bend the rules for {Subject}.",
        "{Subject} didn't get a Covid-19 vaccine. Covid-19 got a {Subject} vaccine.",
        "{Subject} eats his meat so rare that he only eats unicorns and dragons.",
        "{Subject} once played Russian Roulette with a fully loaded gun and won.",
        "Whenever {Subject} peels onions, the onions always cry.",
        "{Subject} can pull a wheelie when riding a unicycle.",
        "{Subject} was born with two umbilical cords, one red and one blue. The bomb squad cut the wrong cord.",
        "{Subject} makes a lot of money selling his urine, it is called Red Bull.",
        "{Subject} is able to slam a revolving door.",
        "The day after {Subject} was born he drove his mother home, he wanted her to get some rest.",
        "{Subject} built the hospital that he was born in.",
        "{Subject} knows exactly what to do with the drunken sailors early in the morning.",
        "{Subject} played a game of rock, paper scissors against his reflection, and won.",
        "When {Subject} went to Burger King and ordered a big mac, they made it for him, perfectly.",
        "The Swiss Army uses {Subject} Knives.",
        "A condom puts on protection to avoid becoming impregnated by {Subject} on date night.",
        "{Subject} is able to start a fire using an extinguisher.",
        "{Subject} doesn't need to throw out the trash, it always throws itself out.",
        "{Subject} has to carry a concealed weapons permit when he wears his regular clothes.",
        "When {Subject} once roundhouse kicked a coal mine and turned it into a diamond mine.",
        "{Subject} doesn't strike gold, gold is the byproduct of {Subject} roundhouse kicking rocks.",
        "When {Subject} lifts weights, the weights get in shape.",
        "{Subject} is able to strangle people using a cordless phone.",
        "{Subject} is the reason that Wally is always hiding.",
        "When {Subject} falls from a great height, the ground has it's life flash before it's eyes.",
        "When {Subject} enters a building that is on fire, the {Subject} alarm rings.",
        "When Thanos snapped his fingers he disappeared. {Subject} doesn't like snapping.",
        "The sun has to wear sunglasses when {Subject} glances at it.",
        "When {Subject} looked into the abyss, the abyss looked the other way.",
        "The Grand Canyon was formed when {Subject} was doing a triathlon.",
        "Bigfoot is still hiding because he once saw {Subject} walking in the mountains.",
        "When {Subject} drops the soap in prison, he picks it up successfully.",
        "The Loch Ness Monster claims to have seen {Subject}.",
        "{Subject} can drink a whole glass of beer. Yep, even the glass.",
        "When {Subject} uses the internet he can skip ads whenever he wants, ads are not able to skip {Subject}.",
        "{Subject} doesn't negotiate with terrorists.",
        "The terrorists negotiate with {Subject}.",
        "{Subject} won an arm wrestling tournament, with both arms tied behind his back.",
        "{Subject} got a divorce and was asked to give half his assets and property away. {Subject} proceeded to chop the entire universe in half with his bare hands.",
        "The Flash discovered how to run at the speed of light when he discovered {Subject} was looking for him.",
        "When {Subject} goes bowling he doesn't get every pin with a single bowl he gets every pin in the bowling alley.",
        "The reason why people say it's pointless for Trump to build a wall is because {Subject} walks to Mexico and back once a month.",
        "Ghosts tell {Subject} stories at the campfire.",
        "{Subject} mines bitcoin with a pen and paper.",
        "When {Subject} goes to a restaurant, the waiter tips him.",
        "Tornadoes don't exist, {Subject} just really doesn't like trailer parks.",
        "{Subject} was born May 6th 1945. The Nazis surrendered May 7th 1945, this is not a coincidence.",
        "{Subject} has counted to infinity more than once. Then he counted backward from infinity.",
        "{Subject} has a bear rug on his lounge floor. The bear is still alive, it is just afraid to move.",
        "{Subject} doesn't go to the gym, instead he goes shop lifting.",
        "If {Subject} was on The Titanic the iceberg would have dodged the ship.",
        "{Subject} is able to make other people walk in his sleep.",
        "{Subject} once raced the earth around the sun and won by three years.",
        "{Subject} was asked to fire someone once, that is how hell was invented.",
        "When {Subject} jumps on the Tempur-Pedic mattress, the wine glass falls over.",
        "When {Subject} was a child at school, his teachers would raise their hands in order to talk to him.",
        "When {Subject}'s parents had nightmares, they would come to his bedroom.",
        "When {Subject} crosses the road, vehicles look both ways.",
        "{Subject} once missed two days of school. Those two days are now called the weekend.",
        "{Subject} doesn't pop his collar, his shirts are stimulated from touching his shoulders.",
        "{Subject} once threw a grenade and killed 100 men, after that the grenade exploded.",
        "{Subject} was able to smell a gas leak before they added the scent to gas.",
        "{Subject} has a diary, it is called the Guinness Book Of World Records.",
        "Hi there, I heard that you are a huge fan of when {Subject} does push ups the earth moves, we call this phenomenon an earthquakes.",
        "{Subject} uses pepper spray to season his meat.",
        "{Subject} is able to sketch your portrait using an eraser.",
        "The dinosaurs once looked at {Subject} the wrong way, that is why we no longer have dinosaurs.",
        "{Subject} had a staring competition with the sun and won.",
        "{Subject} once spun a ball on his finger, to this day planet earth continues to turn.",
        "{Subject} doesn't climb trees, he just pushed them over and walks over them.",
        "{Subject} can kill 2 stones with one bird.",
        "{Subject} doesn't need to wear a watch, he simply decides what time it is."
    ];

    private static readonly Random Quoteziner = new();

    private static readonly List<string> AvailableFemaleVoices = [];

    private static readonly List<string> AvailableMaleVoices = [];

    private static readonly Dictionary<string, (string, float)> CampaignVoices = [];
    private static bool CampaignVoiceDataMissingReported;

    internal static readonly string[] Choices = new List<string> { "Narrator" }
        .Union(Enumerable.Range(1, MaxHeroes).Select(n => $"Hero {n}")).ToArray();

    internal static string[] VoiceNames { get; private set; }

    internal static void Load()
    {
        Unloading = false;
        EnsureSpeechEventHooked();
        InitPiper();
        RefreshAvailableVoices();
        InitVoiceAssignments();
        UpdateAvailableVoices();

        //A fix for UB issue that was setting global game volume when any generated speech was played
        //A one-off change to not force confused users to go into windows sound mixer and change there
        if (!Main.Settings.FixGameVolume)
        {
            Main.Settings.FixGameVolume = true;
            try
            {
                SpeechEvent.Volume = 1;
            }
            catch (Exception)
            {
                // calling this setter crashes on Linux
            }

        }
    }

    private static void InitPiper()
    {
        if (!Directory.Exists(VoicesFolder))
        {
            Directory.CreateDirectory(VoicesFolder);
        }

        string url;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            url = PiperLinuxDownloadURL;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            url = PiperOSXDownloadURL;
        }
        else
        {
            url = PiperWindowsDownloadURL;
        }

        var message = "Piper successfully downloaded.";
        var filename = Path.GetFileName(url);
        var fullZipFile = Path.Combine(Main.ModFolder, filename);
        using var wc = new WebClient();

        try
        {
            if (Directory.Exists(PiperFolder))
            {
                message = "Piper already exists.";
            }
            else
            {
                wc.DownloadFile(url, fullZipFile);
                ZipFile.ExtractToDirectory(fullZipFile, Main.ModFolder);
                File.Delete(fullZipFile);

                if (!TryGetLegacyPiperExecutablePath(out _))
                {
                    message = "Piper successfully downloaded but failed to extract executable.";
                }
            }
        }
        catch
        {
            message = "Cannot download Piper.";
        }

        Main.Info(message);
    }

    internal static void RefreshAvailableVoices()
    {
        if (!Directory.Exists(VoicesFolder))
        {
            Directory.CreateDirectory(VoicesFolder);
        }

        VoiceInfos.Clear();

        var voiceNames = new List<string> { DefaultVoice };

        foreach (var file in new DirectoryInfo(VoicesFolder).GetFiles("*.onnx"))
        {
            var voiceName = Path.GetFileNameWithoutExtension(file.Name);

            if (string.IsNullOrEmpty(voiceName))
            {
                continue;
            }

            voiceNames.Add(voiceName);
            VoiceInfos[voiceName] = new SpeechVoiceInfo(
                voiceName,
                SpeechEngine.LegacyPiper,
                "en",
                file.FullName,
                TryGetSuggestedVoiceGender(voiceName, out var gender) ? gender : null,
                22050,
                0.667f);
        }

        foreach (var profile in PiperPlusVoiceProfiles)
        {
            if (!IsVoiceProfileAvailable(profile))
            {
                continue;
            }

            voiceNames.Add(profile.VoiceName);
            VoiceInfos[profile.VoiceName] = new SpeechVoiceInfo(
                profile.VoiceName,
                profile.Engine,
                profile.LanguageProfile.LanguageCode,
                profile.ModelArgument,
                profile.Gender,
                profile.SampleRate,
                profile.NoiseScale,
                profile.LanguageProfile);
        }

        VoiceNames = voiceNames.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void InitVoiceAssignments()
    {
        // remove any invalid key
        Main.Settings.SpeechVoices.Keys
            .Where(x => x is < 0 or > MaxHeroes)
            .Do(x => Main.Settings.SpeechVoices.Remove(x));

        for (var i = 0; i <= MaxHeroes; i++)
        {
            Main.Settings.SpeechVoices.TryAdd(i, (DefaultVoice, DefaultScale));

            if (!VoiceInfos.ContainsKey(Main.Settings.SpeechVoices[i].Item1) &&
                Main.Settings.SpeechVoices[i].Item1 != DefaultVoice)
            {
                Main.Settings.SpeechVoices[i] = (DefaultVoice, DefaultScale);
            }
        }
    }

    internal static void UpdateAvailableVoices()
    {
        var assignedVoices = new HashSet<string>(
            Main.Settings.SpeechVoices.Values.Select(x => x.Item1),
            StringComparer.Ordinal);

        AvailableFemaleVoices.Clear();
        AvailableMaleVoices.Clear();

        foreach (var voiceInfo in VoiceInfos.Values)
        {
            if (voiceInfo.Engine != SpeechEngine.LegacyPiper ||
                assignedVoices.Contains(voiceInfo.Name))
            {
                continue;
            }

            switch (voiceInfo.Gender)
            {
                case Gender.Female:
                    AvailableFemaleVoices.Add(voiceInfo.Name);
                    break;
                case Gender.Male:
                    AvailableMaleVoices.Add(voiceInfo.Name);
                    break;
            }
        }
    }

    private static bool TryGetSuggestedVoiceGender(string voiceName, out Gender gender)
    {
        foreach (var (voiceUrl, suggestedGender) in SuggestedVoicesUrls)
        {
            if (!voiceUrl.Contains(voiceName))
            {
                continue;
            }

            gender = suggestedGender;
            return true;
        }

        gender = default;
        return false;
    }

    private static bool TryGetLanguageVoiceInfo(
        SpeechLanguageProfile profile,
        Gender? preferredGender,
        out SpeechVoiceInfo voiceInfo)
    {
        voiceInfo = null;

        if (profile == null)
        {
            return false;
        }

        if (preferredGender != null &&
            TryGetLanguageVoiceInfo(profile, preferredGender, true, out voiceInfo))
        {
            return true;
        }

        if (TryGetLanguageVoiceInfo(profile, null, true, out voiceInfo))
        {
            return true;
        }

        return TryGetLanguageVoiceInfo(profile, null, false, out voiceInfo);
    }

    private static bool TryGetLanguageVoiceInfo(
        SpeechLanguageProfile profile,
        Gender? gender,
        bool requireGenderMatch,
        out SpeechVoiceInfo voiceInfo)
    {
        foreach (var voiceName in VoiceNames)
        {
            if (!VoiceInfos.TryGetValue(voiceName, out var candidate) ||
                candidate.LanguageProfile != profile)
            {
                continue;
            }

            if (requireGenderMatch && candidate.Gender != gender)
            {
                continue;
            }

            voiceInfo = candidate;
            return true;
        }

        voiceInfo = null;
        return false;
    }

    private static bool TryGetVoiceLanguageProfile(string voiceName, out SpeechLanguageProfile profile)
    {
        if (VoiceInfos.TryGetValue(voiceName, out var voiceInfo) &&
            voiceInfo.LanguageProfile != null)
        {
            profile = voiceInfo.LanguageProfile;
            return true;
        }

        foreach (var voiceProfile in PiperPlusVoiceProfiles)
        {
            if (voiceProfile.VoiceName != voiceName)
            {
                continue;
            }

            profile = voiceProfile.LanguageProfile;
            return true;
        }

        profile = null;
        return false;
    }

    private static bool IsVoiceProfileAvailable(SpeechVoiceProfile profile)
    {
        return profile.Engine switch
        {
            SpeechEngine.PiperPlus => TryGetPiperPlusExecutablePath(out _) && IsVoiceProfileModelAvailable(profile),
            _ => false
        };
    }

    private static bool IsVoiceProfileModelAvailable(SpeechVoiceProfile profile)
    {
        if (!Directory.Exists(PiperPlusModelsFolder))
        {
            return false;
        }

        try
        {
            return Directory
                .EnumerateFiles(PiperPlusModelsFolder, profile.ModelSearchPattern, SearchOption.AllDirectories)
                .Any();
        }
        catch
        {
            return false;
        }
    }

    internal static void CollectCustomCampaignVoiceData()
    {
        const string UB_VOICE_DATA = "UB_VOICE_DATA";

        if (!Gui.Game.CampaignDefinition.IsUserCampaign)
        {
            return;
        }

        CampaignVoices.Clear();

        var userCampaign = Gui.Session.UserCampaign;
        var voiceData = userCampaign?.UserItems?.FirstOrDefault(x =>
            x.ReferenceItemDefinition.IsDocument &&
            x.DocumentFragments is { Count: > 0 } &&
            x.InternalName == UB_VOICE_DATA);

        if (voiceData == null)
        {
            if (!CampaignVoiceDataMissingReported)
            {
                Main.Info("No campaign voice mapping data found.");
                CampaignVoiceDataMissingReported = true;
            }

            return;
        }

        var validVoices = new HashSet<string>(VoiceNames, StringComparer.Ordinal);
        var validNpcs = new HashSet<string>(
            userCampaign.UserNpcs.Select(x => x.InternalName),
            StringComparer.Ordinal);

        foreach (var fragment in voiceData.DocumentFragments)
        {
            var arr = fragment.Split(',').Select(x => x.Trim()).ToArray();

            if (arr.Length is < 2 or > 3)
            {
                Main.Info($"Failed to parse voice data: [{fragment}]");
                continue;
            }

            var npc = arr[0];
            var voice = arr[1];
            var scale = DefaultScale;

            if (arr.Length == 3)
            {
                try
                {
                    scale = float.Parse(arr[2], CultureInfo.InvariantCulture);
                }
                catch (FormatException ex)
                {
                    Main.Info($"Failed to parse voice scale data: [{fragment}] {ex.Message}");
                    scale = DefaultScale;
                }
            }

            if (scale < 0.5 || scale > 2)
            {
                Main.Info($"Failed to validate scale range: [{fragment}]");
                scale = DefaultScale;
            }

            if (!validVoices.Contains(voice))
            {
                Main.Info(
                    $"voice definition on campaign {userCampaign.DisplayTitle}, fragment [{fragment}], was not found");

                continue;
            }

            if (npc == NarratorVoiceKey ||
                validNpcs.Contains(npc))
            {
                CampaignVoices.AddOrReplace(npc, (voice, scale));
            }
            else
            {
                Main.Info(
                    $"NPC definition on campaign {userCampaign.DisplayTitle}, fragment [{fragment}], was not found");
            }
        }
    }

    internal static void ShutUp()
    {
        lock (SpeechLock)
        {
            SpeechGeneration++;
            SpeechQueue.Clear();
            CurrentSpeechRequest = null;
            SpeechPlaybackActive = false;
            StopSpeechPlaybackNoLock();
        }
    }

    internal static void Unload()
    {
        Unload(true);
    }

    internal static void Unload(bool destroyUnityObjects)
    {
        lock (SpeechLock)
        {
            Unloading = true;
            SpeechGeneration++;
            SpeechQueue.Clear();
            CurrentSpeechRequest = null;
            SpeechPlaybackActive = false;
            StopSpeechPlaybackNoLock();

            if (SpeechEventHooked && _speechEvent != null)
            {
                _speechEvent.PlaybackStopped -= OnSpeechPlaybackStopped;
            }

            SpeechEventHooked = false;
            _speechEvent?.Dispose();
            _speechEvent = null;
        }

        StopSpeechProcesses();
        PiperPlusVoiceDownloader.Unload(destroyUnityObjects);
        VoicesDownloader.Unload(destroyUnityObjects);
    }

    internal static void NotifyVanillaSpeechConcluded()
    {
        // Vanilla narration completion should not stop the independent UB TTS queue.
    }

    private static string StripXmlTagsAndNarration(string str)
    {
        return RemoveNpcSpeechTags.Replace(str, string.Empty);
    }

    private static void EnsureSpeechEventHooked()
    {
        if (SpeechEventHooked)
        {
            return;
        }

        SpeechEvent.PlaybackStopped += OnSpeechPlaybackStopped;
        SpeechEventHooked = true;
    }

    private static void QueueSpeech(SpeechVoiceInfo voiceInfo, float scale, string cleanedText)
    {
        if (voiceInfo == null ||
            string.IsNullOrWhiteSpace(cleanedText) ||
            Unloading)
        {
            return;
        }

        lock (SpeechLock)
        {
            SpeechQueue.Enqueue(new SpeechRequest(voiceInfo, scale, cleanedText, SpeechGeneration));
            StartNextSpeechNoLock();
        }
    }

    private static void StartNextSpeechNoLock()
    {
        if (Unloading ||
            SpeechPlaybackActive ||
            SpeechQueue.Count == 0)
        {
            return;
        }

        CurrentSpeechRequest = SpeechQueue.Dequeue();
        SpeechPlaybackActive = true;
        _ = SynthesizeAndPlaySpeechAsync(CurrentSpeechRequest);
    }

    private static async Task SynthesizeAndPlaySpeechAsync(SpeechRequest request)
    {
        SpeechAudio speechAudio = null;

        try
        {
            speechAudio = await SynthesizeSpeechAsync(request.VoiceInfo, request.Scale, request.CleanedText);
        }
        catch (Exception e)
        {
            Main.Error(e);
        }

        lock (SpeechLock)
        {
            if (Unloading ||
                CurrentSpeechRequest != request)
            {
                speechAudio?.Dispose();
                return;
            }

            if (request.Generation != SpeechGeneration ||
                speechAudio == null)
            {
                speechAudio?.Dispose();
                CurrentSpeechRequest = null;
                SpeechPlaybackActive = false;
                StartNextSpeechNoLock();
                return;
            }

            PlaySpeechNoLock(speechAudio);
        }
    }

    private static void OnSpeechPlaybackStopped(object sender, StoppedEventArgs e)
    {
        lock (SpeechLock)
        {
            DisposeSpeechStreamsNoLock();

            if (!SpeechPlaybackActive)
            {
                return;
            }

            CurrentSpeechRequest = null;
            SpeechPlaybackActive = false;
            StartNextSpeechNoLock();
        }
    }

    private static void StopSpeechPlaybackNoLock()
    {
        _speechEvent?.Stop();
        DisposeSpeechStreamsNoLock();
    }

    private static void DisposeSpeechStreamsNoLock()
    {
        CurrentWaveStream?.Dispose();
        CurrentWaveStream = null;
        CurrentAudioStream?.Dispose();
        CurrentAudioStream = null;
    }

    private static bool TryGetLegacyPiperExecutablePath(out string executablePath)
    {
        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "piper.exe" : "piper";

        executablePath = Path.Combine(PiperFolder, executable);

        return File.Exists(executablePath);
    }

    private static bool TryGetPiperPlusExecutablePath(out string executablePath)
    {
        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "piper.exe" : "piper";

        executablePath = Path.Combine(PiperPlusFolder, "bin", executable);

        if (File.Exists(executablePath))
        {
            return true;
        }

        executablePath = Path.Combine(PiperPlusFolder, executable);

        if (File.Exists(executablePath))
        {
            return true;
        }

        if (!Directory.Exists(PiperPlusFolder))
        {
            return false;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateFiles(PiperPlusFolder, executable, SearchOption.AllDirectories))
            {
                executablePath = candidate;
                return true;
            }
        }
        catch
        {
            // ignore and report unavailable
        }

        return false;
    }

    private static bool InitPiperPlus(bool logResult = true)
    {
        if (TryGetPiperPlusExecutablePath(out _))
        {
            if (logResult)
            {
                Main.Info("piper-plus already exists.");
            }

            return true;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            if (logResult)
            {
                Main.Info("piper-plus automatic download is only available on Windows x64.");
            }

            return false;
        }

        var filename = Path.GetFileName(PiperPlusWindowsX64DownloadURL);
        var fullZipFile = Path.Combine(Main.ModFolder, filename);
        using var wc = new WebClient();

        try
        {
            Directory.CreateDirectory(PiperPlusFolder);
            wc.DownloadFile(PiperPlusWindowsX64DownloadURL, fullZipFile);
            ZipFile.ExtractToDirectory(fullZipFile, PiperPlusFolder);
            File.Delete(fullZipFile);

            if (TryGetPiperPlusExecutablePath(out _))
            {
                if (logResult)
                {
                    Main.Info("piper-plus successfully downloaded.");
                }

                return true;
            }

            if (logResult)
            {
                Main.Info("piper-plus successfully downloaded but failed to find executable.");
            }
        }
        catch
        {
            if (logResult)
            {
                Main.Info("Cannot download piper-plus.");
            }
        }

        return false;
    }

    private static bool HasKana(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is >= '\u3040' and <= '\u309F' ||
                c is >= '\u30A0' and <= '\u30FF' ||
                c is >= '\uFF65' and <= '\uFF9F')
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesJapaneseText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (HasKana(text))
        {
            return true;
        }

        if (!TranslatorContext.HasCJKChar(text))
        {
            return false;
        }

        var latinLetters = 0;
        var japanesePunctuation = false;

        foreach (var c in text)
        {
            if (c is >= 'A' and <= 'Z' ||
                c is >= 'a' and <= 'z')
            {
                latinLetters++;
            }

            if ("。、、「」『』ー".IndexOf(c) >= 0)
            {
                japanesePunctuation = true;
            }
        }

        return japanesePunctuation && latinLetters <= Math.Max(2, text.Length / 10);
    }

    private static SpeechLanguageProfile DetectTextLanguageProfile(string cleanedText)
    {
        foreach (var profile in LanguageProfiles)
        {
            if (profile.MatchesText(cleanedText))
            {
                return profile;
            }
        }

        return null;
    }

    private static SpeechVoiceInfo ResolveVoiceForText(
        string preferredVoice,
        string cleanedText,
        bool allowLanguageFallback,
        Gender? preferredGender)
    {
        var languageProfile = DetectTextLanguageProfile(cleanedText);

        if (languageProfile != null)
        {
            if (preferredVoice != DefaultVoice &&
                VoiceInfos.TryGetValue(preferredVoice, out var preferredVoiceInfo) &&
                preferredVoiceInfo.LanguageProfile == languageProfile)
            {
                return preferredVoiceInfo;
            }

            return (preferredVoice != DefaultVoice || allowLanguageFallback) &&
                   TryGetLanguageVoiceInfo(languageProfile, preferredGender, out var profileVoiceInfo)
                ? profileVoiceInfo
                : null;
        }

        return preferredVoice != DefaultVoice &&
               VoiceInfos.TryGetValue(preferredVoice, out var voiceInfo)
            ? voiceInfo
            : null;
    }

    internal static void SpeakQuote()
    {
        var (selectedVoice, _) = Main.Settings.SpeechVoices[Main.Settings.SpeechChoice];

        if (TryGetVoiceLanguageProfile(selectedVoice, out var profile))
        {
            Speak(profile.SampleText, Main.Settings.SpeechChoice, false);
            return;
        }

        var quoteNumber = Quoteziner.Next(0, Quotes.Length);
        var subjects = new[] { "Chuck Norris", "Zappa" };
        var subject = subjects[Quoteziner.Next(0, subjects.Length)];
        var quote = Quotes[quoteNumber].Replace("{Subject}", subject);

        Speak(quote, Main.Settings.SpeechChoice, false);
    }

    internal static void Speak(string inputText, GameLocationCharacter character)
    {
        if (character == null)
        {
            return;
        }

        var index = Gui.Game.GameCampaign.Party.CharactersList
            .FindIndex(x => x.RulesetCharacter == character.RulesetCharacter);

        if (index < 0)
        {
            return;
        }

        Speak(inputText, index + 1);
    }

    // heroId zero is the Narrator and 1-6 map to heroes in party
    internal static void Speak(string inputText, int heroId, bool forceUseCampaign = true)
    {
        try
        {
            // only if audio enabled
            var audioSettingsService = ServiceRepository.GetService<IAudioSettingsService>();

            if (audioSettingsService?.MasterEnabled != true)
            {
                return;
            }

            if (!Main.Settings.EnableSpeech || heroId < 0 || heroId > MaxHeroes)
            {
                return;
            }

            // only custom campaigns
            if (forceUseCampaign)
            {
                // unity life check...
                if (Gui.GameCampaign)
                {
                    if (!Gui.GameCampaign.campaignDefinition.IsUserCampaign)
                    {
                        return;
                    }
                }
            }

            string voice;
            float scale;

            if (heroId == 0 &&
                CampaignVoices.TryGetValue(NarratorVoiceKey, out var voiceData))
            {
                (voice, scale) = voiceData;
            }
            else
            {
                (voice, scale) = Main.Settings.SpeechVoices[heroId];
            }

            var cleanedText = StripXmlTagsAndNarration(inputText);
            var voiceInfo = ResolveVoiceForText(voice, cleanedText, heroId == 0, null);

            if (voiceInfo == null)
            {
                return;
            }

            QueueSpeech(voiceInfo, scale, cleanedText);
        }
        catch (Exception e)
        {
            Main.Error(e);
        }
    }

    internal static void SpeakNpc(string inputText, GameLocationCharacter character)
    {
        try
        {
            // only if audio enabled
            var audioSettingsService = ServiceRepository.GetService<IAudioSettingsService>();

            if (audioSettingsService?.MasterEnabled != true)
            {
                return;
            }

            if (!Main.Settings.EnableSpeechOnNpcs)
            {
                return;
            }

            // only custom campaigns
            // unity life check...
            if (Gui.GameCampaign)
            {
                if (!Gui.GameCampaign.campaignDefinition.IsUserCampaign)
                {
                    return;
                }
            }

            if (character.RulesetCharacter is not RulesetCharacterMonster rulesetCharacterMonster)
            {
                return;
            }

            var cleanedText = StripXmlTagsAndNarration(inputText);
            var languageProfile = DetectTextLanguageProfile(cleanedText);
            var internalName = rulesetCharacterMonster.MonsterDefinition.Name;
            var preferredGender = FemaleNpcs.Contains(internalName) ? Gender.Female : Gender.Male;
            var scale = 1f;
            var voice = DefaultVoice;

            if (!Main.Settings.ForceModSpeechOnNpcs &&
                CampaignVoices.TryGetValue(internalName, out var voiceData))
            {
                (voice, scale) = voiceData;
            }

            if (voice == DefaultVoice &&
                TryGetLanguageVoiceInfo(languageProfile, preferredGender, out var languageVoiceInfo))
            {
                voice = languageVoiceInfo.Name;
                scale = Main.Settings.SpeechVoices[0].Item2;
            }
            else if (Main.Settings.ForceModSpeechOnNpcs ||
                CampaignVoices.Count == 0)
            {
                // assign dub data on a round-robin basis for campaigns without it
                if (!TryGetUserNpcIndex(internalName, out var npcId))
                {
                    return;
                }

                switch (FemaleNpcs.Contains(internalName))
                {
                    case true when AvailableFemaleVoices.Count > 0:
                    {
                        voice = AvailableFemaleVoices[npcId % AvailableFemaleVoices.Count];
                        break;
                    }
                    case false when AvailableMaleVoices.Count > 0:
                    {
                        voice = AvailableMaleVoices[npcId % AvailableMaleVoices.Count];
                        break;
                    }
                    default:
                        return;
                }

                scale = Main.Settings.SpeechVoices[0].Item2;
            }

            var voiceInfo = ResolveVoiceForText(voice, cleanedText, true, preferredGender);

            if (voiceInfo == null)
            {
                return;
            }

            QueueSpeech(voiceInfo, scale, cleanedText);
        }
        catch (Exception e)
        {
            Main.Error(e);
        }
    }

    private static bool TryGetUserNpcIndex(string internalName, out int npcId)
    {
        var userNpcs = Gui.Session.UserCampaign.UserNpcs;

        for (var i = 0; i < userNpcs.Count; i++)
        {
            if (userNpcs[i].InternalName != internalName)
            {
                continue;
            }

            npcId = i;
            return true;
        }

        npcId = -1;
        return false;
    }

    private static void PlaySpeechNoLock(SpeechAudio speechAudio)
    {
        DisposeSpeechStreamsNoLock();

        CurrentAudioStream = speechAudio.AudioStream;
        CurrentAudioStream.Position = 0;
        CurrentWaveStream = speechAudio.IsWaveFile
            ? new WaveFileReader(CurrentAudioStream)
            : new RawSourceWaveStream(CurrentAudioStream, new WaveFormat(speechAudio.SampleRate, 1));

        SpeechEvent.Init(new SampleChannel(CurrentWaveStream)
        {
            Volume = Main.Settings.SpeechVolume
        });
        SpeechEvent.Play();
    }

    private static Task<SpeechAudio> SynthesizeSpeechAsync(
        SpeechVoiceInfo voiceInfo,
        float scale,
        string cleanedText)
    {
        return voiceInfo.Engine switch
        {
            SpeechEngine.PiperPlus => SynthesizePiperPlusSpeechAsync(voiceInfo, scale, cleanedText),
            _ => SynthesizeLegacyPiperSpeechAsync(voiceInfo, scale, cleanedText)
        };
    }

    private static async Task<SpeechAudio> SynthesizeLegacyPiperSpeechAsync(
        SpeechVoiceInfo voiceInfo,
        float scale,
        string cleanedText)
    {
        if (!TryGetLegacyPiperExecutablePath(out var executablePath))
        {
            return null;
        }

        var modelFileName = Path.Combine(VoicesFolder, voiceInfo.Name + ".onnx");

        if (!File.Exists(modelFileName))
        {
            return null;
        }

        using var piper = CreateProcess(
            executablePath,
            $"--model {QuoteArgument(modelFileName)} --length_scale {FormatScale(scale)} --output-raw",
            true);

        if (!piper.Start())
        {
            return null;
        }

        RegisterSpeechProcess(piper);

        try
        {
            var outputTask = CopyToMemoryStreamAsync(piper.StandardOutput.BaseStream);
            var errorTask = piper.StandardError.ReadToEndAsync();

            await WriteUtf8ToStandardInputAsync(piper, cleanedText);
            await Task.Run(() => piper.WaitForExit());
            await errorTask;

            var audioStream = await outputTask;

            if (piper.ExitCode != 0 || audioStream.Length == 0)
            {
                audioStream.Dispose();
                return null;
            }

            audioStream.Position = 0;

            return new SpeechAudio(audioStream, false, voiceInfo.SampleRate);
        }
        finally
        {
            UnregisterSpeechProcess(piper);
        }
    }

    private static async Task<SpeechAudio> SynthesizePiperPlusSpeechAsync(
        SpeechVoiceInfo voiceInfo,
        float scale,
        string cleanedText)
    {
        if (!TryGetPiperPlusExecutablePath(out var executablePath))
        {
            return null;
        }

        var tempFile = Path.GetTempFileName();
        var tempWav = Path.ChangeExtension(tempFile, ".wav");

        try
        {
            File.Delete(tempFile);

            var cliLanguage = voiceInfo.LanguageProfile?.CliLanguageArgument ?? voiceInfo.Language;

            using var piper = CreateProcess(
                executablePath,
                $"--json-input --model {voiceInfo.ModelArgument} --model-dir {QuoteArgument(PiperPlusModelsFolder)} " +
                $"--language {cliLanguage} --length-scale {FormatScale(scale)} " +
                $"--noise-scale {FormatScale(voiceInfo.NoiseScale)} --quiet",
                true);

            piper.StartInfo.EnvironmentVariables["PIPER_MODEL_DIR"] = PiperPlusModelsFolder;

            if (!piper.Start())
            {
                return null;
            }

            RegisterSpeechProcess(piper);

            try
            {
                var outputTask = piper.StandardOutput.ReadToEndAsync();
                var errorTask = piper.StandardError.ReadToEndAsync();
                var payload = JsonConvert.SerializeObject(new
                {
                    text = cleanedText,
                    speaker_id = 0,
                    output_file = tempWav
                });

                await WriteUtf8ToStandardInputAsync(piper, payload + Environment.NewLine);
                await Task.Run(() => piper.WaitForExit());
                await outputTask;
                await errorTask;

                if (piper.ExitCode != 0 || !File.Exists(tempWav))
                {
                    return null;
                }

                var wavBytes = File.ReadAllBytes(tempWav);

                if (wavBytes.Length == 0)
                {
                    return null;
                }

                return new SpeechAudio(new MemoryStream(wavBytes), true, voiceInfo.SampleRate);
            }
            finally
            {
                UnregisterSpeechProcess(piper);
            }
        }
        finally
        {
            TryDeleteFile(tempFile);
            TryDeleteFile(tempWav);
        }
    }

    private static Process CreateProcess(string executablePath, string arguments, bool redirectInput)
    {
        var process = new Process();

        process.StartInfo.FileName = executablePath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardInput = redirectInput;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        return process;
    }

    private static void RegisterSpeechProcess(Process process)
    {
        lock (SpeechProcessLock)
        {
            SpeechProcesses.Add(process);
        }
    }

    private static void UnregisterSpeechProcess(Process process)
    {
        lock (SpeechProcessLock)
        {
            SpeechProcesses.Remove(process);
        }
    }

    private static void StopSpeechProcesses()
    {
        Process[] processes;

        lock (SpeechProcessLock)
        {
            processes = SpeechProcesses.ToArray();
            SpeechProcesses.Clear();
        }

        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // best effort cleanup
            }

            try
            {
                process.Dispose();
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private static async Task WriteUtf8ToStandardInputAsync(Process process, string inputText)
    {
        var bytes = Encoding.UTF8.GetBytes(inputText);

        await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length);
        process.StandardInput.Close();
    }

    private static async Task<MemoryStream> CopyToMemoryStreamAsync(Stream stream)
    {
        var audioStream = new MemoryStream();
        var buffer = new byte[16384];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            audioStream.Write(buffer, 0, bytesRead);
        }

        return audioStream;
    }

    private static string QuoteArgument(string argument)
    {
        return "\"" + argument.Replace("\"", "\\\"") + "\"";
    }

    private static string FormatScale(float scale)
    {
        return scale.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static void TryDeleteFile(string filename)
    {
        try
        {
            if (!string.IsNullOrEmpty(filename) && File.Exists(filename))
            {
                File.Delete(filename);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static bool DownloadProfileModel(SpeechVoiceProfile profile)
    {
        if (!TryGetPiperPlusExecutablePath(out var executablePath))
        {
            return false;
        }

        Directory.CreateDirectory(PiperPlusModelsFolder);

        using var piper = CreateProcess(
            executablePath,
            $"--download-model {profile.ModelArgument} --model-dir {QuoteArgument(PiperPlusModelsFolder)} --quiet",
            false);

        piper.StartInfo.EnvironmentVariables["PIPER_MODEL_DIR"] = PiperPlusModelsFolder;

        try
        {
            if (!piper.Start())
            {
                return false;
            }

            RegisterSpeechProcess(piper);

            try
            {
                var outputTask = piper.StandardOutput.ReadToEndAsync();
                var errorTask = piper.StandardError.ReadToEndAsync();

                piper.WaitForExit();
                Task.WaitAll(outputTask, errorTask);

                return piper.ExitCode == 0 && IsVoiceProfileModelAvailable(profile);
            }
            finally
            {
                UnregisterSpeechProcess(piper);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool DownloadLanguageVoiceModels(SpeechLanguageProfile languageProfile)
    {
        var success = true;
        var foundProfile = false;

        foreach (var profile in PiperPlusVoiceProfiles)
        {
            if (profile.LanguageProfile != languageProfile)
            {
                continue;
            }

            foundProfile = true;

            if (IsVoiceProfileModelAvailable(profile))
            {
                continue;
            }

            success &= DownloadProfileModel(profile);
        }

        return foundProfile && success;
    }

    internal sealed class PiperPlusVoiceDownloader : MonoBehaviour
    {
        private static PiperPlusVoiceDownloader _shared;
        private IEnumerator _coroutine;
        private SpeechLanguageProfile _languageProfile;
        private float _progress;

        [NotNull]
        internal static PiperPlusVoiceDownloader Shared
        {
            get
            {
                if (_shared)
                {
                    return _shared;
                }

                _shared = new GameObject().AddComponent<PiperPlusVoiceDownloader>();
                DontDestroyOnLoad(_shared.gameObject);

                _shared._coroutine = null;

                return _shared;
            }
        }

        internal string GetButtonLabel(SpeechLanguageProfile languageProfile)
        {
            var displayName = Gui.Localize(languageProfile.DisplayNameKey);

            return _coroutine != null
                ? Gui.Format("ModUi/&DownloadSpeechLanguageVoicesOngoing",
                        Gui.Localize(_languageProfile?.DisplayNameKey ?? languageProfile.DisplayNameKey),
                        $"{_progress:00.0%}")
                    .Bold()
                    .Khaki()
                : Gui.Format("ModUi/&DownloadSpeechLanguageVoices", displayName);
        }

        internal void DownloadVoice(SpeechLanguageProfile languageProfile)
        {
            if (_coroutine != null)
            {
                return;
            }

            _languageProfile = languageProfile;
            _progress = 0f;
            _coroutine = DownloadVoiceImpl(languageProfile);
            StartCoroutine(_coroutine);
        }

        internal static void Unload(bool destroyUnityObject)
        {
            if (!_shared)
            {
                return;
            }

            if (destroyUnityObject && _shared._coroutine != null)
            {
                _shared.StopCoroutine(_shared._coroutine);
                _shared._coroutine = null;
            }

            _shared._languageProfile = null;
            _shared._progress = 0f;

            if (destroyUnityObject)
            {
                Destroy(_shared.gameObject);
                _shared = null;
            }
        }

        private IEnumerator DownloadVoiceImpl(SpeechLanguageProfile languageProfile)
        {
            Directory.CreateDirectory(PiperPlusModelsFolder);
            Main.Info($"Downloading piper-plus voices for {languageProfile.LanguageCode}.");

            _progress = 0.1f;
            yield return null;

            var task = Task.Run(() => InitPiperPlus(false) && DownloadLanguageVoiceModels(languageProfile));

            while (!task.IsCompleted)
            {
                _progress = Math.Min(0.95f, _progress + 0.05f);
                yield return null;
            }

            if (task.Status == TaskStatus.RanToCompletion && task.Result)
            {
                Main.Info($"piper-plus voices for {languageProfile.LanguageCode} successfully downloaded.");
                RefreshAvailableVoices();
                UpdateAvailableVoices();
            }
            else
            {
                Main.Info($"Cannot download piper-plus voices for {languageProfile.LanguageCode}.");
            }

            StopCoroutine(_coroutine);
            _coroutine = null;
            _languageProfile = null;
            _progress = 0f;
        }
    }

    internal sealed class VoicesDownloader : MonoBehaviour
    {
        private static VoicesDownloader _shared;
        private IEnumerator _coroutine;

        private float _progress;

        [NotNull]
        internal static VoicesDownloader Shared
        {
            get
            {
                if (_shared)
                {
                    return _shared;
                }

                _shared = new GameObject().AddComponent<VoicesDownloader>();
                DontDestroyOnLoad(_shared.gameObject);

                _shared._coroutine = null;

                return _shared;
            }
        }

        internal string GetButtonLabel()
        {
            return _coroutine != null
                ? Gui.Format("ModUi/&DownloadVoiceOngoing", $"{_progress:00.0%}").Bold().Khaki()
                : Gui.Localize("ModUi/&DownloadVoice");
        }

        internal static void Unload(bool destroyUnityObject)
        {
            if (!_shared)
            {
                return;
            }

            if (destroyUnityObject && _shared._coroutine != null)
            {
                _shared.StopCoroutine(_shared._coroutine);
                _shared._coroutine = null;
            }

            _shared._progress = 0f;

            if (destroyUnityObject)
            {
                Destroy(_shared.gameObject);
                _shared = null;
            }
        }

        private void UpdateProgress(ref int loaded, int total)
        {
            if (total <= 0)
            {
                _progress = 0.0f;
                return;
            }

            _progress = loaded++ / (float)total;
        }

        private IEnumerator DownloadVoicesImpl()
        {
            if (!Directory.Exists(VoicesFolder))
            {
                Directory.CreateDirectory(VoicesFolder);
            }

            var current = 0;
            var total = SuggestedVoicesUrls.Length;

            foreach (var (voice, _) in SuggestedVoicesUrls)
            {
                yield return null;

                UpdateProgress(ref current, total);
                DownloadVoice(voice);
            }

            RefreshAvailableVoices();
            StopCoroutine(_coroutine);
            _coroutine = null;
            _progress = 0f;
        }

        internal void DownloadVoices()
        {
            if (_coroutine != null)
            {
                return;
            }

            _progress = 0f;
            _coroutine = DownloadVoicesImpl();
            StartCoroutine(_coroutine);
        }

        private static void DownloadVoice(string voice)
        {
            using var wc = new WebClient();

            var message = $"Voice {voice} successfully downloaded";
            var model = $"{voice}.onnx";
            var modelFilename = Path.GetFileName(model);
            var fullModelFilename = Path.Combine(VoicesFolder, modelFilename);
            var modelUrl = $"{model}?download=true";

            try
            {
                if (!File.Exists(fullModelFilename))
                {
                    wc.DownloadFile(modelUrl, fullModelFilename);

                    var json = $"{voice}.onnx.json";
                    var jsonFilename = Path.GetFileName(json);
                    var fullJsonFilename = Path.Combine(VoicesFolder, jsonFilename);
                    var jsonUrl = $"{json}?download=true";

                    var voiceNames = VoiceNames;

                    Array.Resize(ref voiceNames, VoiceNames.Length + 1);
                    VoiceNames = voiceNames;
                    VoiceNames[VoiceNames.Length - 1] = Path.GetFileNameWithoutExtension(modelFilename);

                    if (!File.Exists(fullJsonFilename))
                    {
                        wc.DownloadFile(jsonUrl, fullJsonFilename);
                    }
                    else
                    {
                        message = $"Voice settings {voice} already exists.";
                    }
                }
                else
                {
                    message = $"Voice {voice} already exists.";
                }
            }
            catch
            {
                message = $"Cannot download voice {voice}.";
            }

            Main.Info(message);
        }
    }
}
