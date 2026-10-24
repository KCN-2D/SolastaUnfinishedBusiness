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
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using I2.Loc;
using JetBrains.Annotations;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using Random = System.Random;

namespace SolastaUnfinishedBusiness.Models;

internal static partial class SpeechContext
{
    internal const int MaxHeroes = 6;
    internal const string AutomaticVoice = "Automatic";
    internal const string DefaultNpcVoice = AutomaticVoice;

    internal const string DefaultVoice = "No Voice";
    internal const float DefaultScale = 0.8f;

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

    // Unity's Mono runtime cannot activate COM objects. Use the installed Windows PowerShell host for SAPI.
    // Requests are UTF-8 JSON on stdin; campaign text never becomes executable script or command arguments.
    private const string WindowsSpeechScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        function ReleaseSpeechObject($value) {
            if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                try {
                    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
                } catch {
                    [Console]::Error.WriteLine($_.Exception.Message)
                }
            }
        }
        $voice = $null
        $tokens = $null
        $token = $null
        $stream = $null
        $format = $null
        try {
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            $voice = New-Object -ComObject SAPI.SpVoice
            if ($request.operation -eq 'voices') {
                $tokens = $voice.GetVoices()
                $items = @()
                for ($i = 0; $i -lt $tokens.Count; $i++) {
                    try {
                        $token = $tokens.Item($i)
                        $items += [pscustomobject]@{
                            id = $token.Id
                            language = $token.GetAttribute('Language')
                            gender = $token.GetAttribute('Gender')
                            description = $token.GetDescription(0)
                        }
                    } catch {
                        [Console]::Error.WriteLine($_.Exception.Message)
                    } finally {
                        ReleaseSpeechObject $token
                        $token = $null
                    }
                }
                ConvertTo-Json -InputObject $items -Compress
            } elseif ($request.operation -eq 'synthesize') {
                $token = New-Object -ComObject SAPI.SpObjectToken
                [void]$token.SetId([string]$request.voice, '', $false)
                $voice.Voice = $token
                $voice.Rate = [int]$request.rate
                $stream = New-Object -ComObject SAPI.SpMemoryStream
                $format = $stream.Format
                $format.Type = 22
                $voice.AllowAudioOutputFormatChangesOnNextSet = $false
                $voice.AudioOutputStream = $stream
                [void]$voice.Speak([string]$request.text, 16)
                [byte[]]$bytes = $stream.GetData()
                [Console]::OpenStandardOutput().Write($bytes, 0, $bytes.Length)
            } else {
                throw 'Unknown Windows speech operation.'
            }
        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        } finally {
            ReleaseSpeechObject $format
            ReleaseSpeechObject $stream
            ReleaseSpeechObject $token
            ReleaseSpeechObject $tokens
            ReleaseSpeechObject $voice
        }
        """;

    private static readonly Regex RemoveNpcSpeechTags =
        new(@"<[bci/].*?>|\*.+?\*|\(.+?\)|\[.+?\]|\{.+?\}", RegexOptions.Compiled);
    private static readonly Regex RemoveSpeechNameTags = new(@"<[^>]*>", RegexOptions.Compiled);

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

    private static readonly object SpeechLock = new();
    private static readonly object SpeechProcessLock = new();

    private static readonly Dictionary<string, SpeechVoiceInfo> VoiceInfos = new(StringComparer.Ordinal);
    private static readonly List<Process> SpeechProcesses = [];
    private static readonly Dictionary<HttpWebRequest, int?> SpeechHttpRequests = [];
    private static readonly List<SpeechVoiceInfo> VoiceVoxVoices = [];
    private static volatile int VoiceVoxRefreshVersion;
    private static volatile int VoiceVoxSessionVersion;
    private static bool VoiceVoxSessionEnabled;

    internal static bool VoiceVoxRefreshInProgress { get; private set; }
    internal static bool VoiceVoxEngineAvailable { get; private set; }
    internal static int AvailableVoiceVoxVoiceCount => VoiceVoxEngineAvailable ? VoiceVoxVoices.Count : 0;
    internal static int VoiceCatalogRevision { get; private set; }

    private static SpeechPlayback CurrentPlayback;
    private static SpeechRequest CurrentSpeechRequest;
    private static bool PiperInitializationAttempted;
    private static bool VoiceDataInitialized;
    private static bool CampaignVoiceDataInitialized;
    private static volatile bool Unloading;
    private static volatile int SpeechGeneration;

    private static readonly Queue<SpeechRequest> SpeechQueue = [];
    private static readonly AsyncLocal<SpeechRequest> SynthesizingSpeech = new();

    internal enum SpeechEngine
    {
        LegacyPiper,
        PiperPlus,
        WindowsSapi,
        VoiceVox
    }

    internal enum Gender
    {
        Male,
        Female
    }

    [Flags]
    internal enum SpeechScript
    {
        None = 0,
        Latin = 1,
        Kana = 2,
        Han = 4,
        Cyrillic = 8,
        Hangul = 16
    }

    internal sealed class SpeechLanguageProfile(
        string languageCode,
        string displayNameKey,
        string cliLanguageArgument,
        string sampleText,
        SpeechScript scripts)
    {
        internal string LanguageCode { get; } = languageCode;
        internal string DisplayNameKey { get; } = displayNameKey;
        internal string CliLanguageArgument { get; } = cliLanguageArgument;
        internal string SampleText { get; } = sampleText;
        internal SpeechScript Scripts { get; } = scripts;

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
        SpeechLanguageProfile languageProfile = null,
        int speakerId = 0,
        string configArgument = null,
        string displayName = null)
    {
        internal string Name { get; } = name;
        internal SpeechEngine Engine { get; } = engine;
        internal string Language { get; } = language;
        internal string ModelArgument { get; } = modelArgument;
        internal Gender? Gender { get; } = gender;
        internal int SampleRate { get; } = sampleRate;
        internal float NoiseScale { get; } = noiseScale;
        internal SpeechLanguageProfile LanguageProfile { get; } = languageProfile;
        internal int SpeakerId { get; } = speakerId;
        internal string ConfigArgument { get; } = configArgument;
        internal string DisplayName { get; } = displayName;
        internal bool AutomaticVoice { get; set; } = true;
        internal string[] SupportedLanguages { get; set; } = [language];
        internal int AutomaticPriority { get; set; }
        internal int AutomaticWeight { get; set; } = 1;
        internal string PersonaKey { get; set; } = name;

        internal bool SupportsLanguage(SpeechLanguageProfile profile)
        {
            return profile != null && SupportedLanguages.Contains(profile.LanguageCode, StringComparer.Ordinal);
        }

        internal SpeechVoiceInfo ForLanguage(SpeechLanguageProfile profile)
        {
            if (profile == null || Language == profile.LanguageCode)
            {
                return this;
            }

            // The catalog identity stays stable; only this request's model language changes.
            return new SpeechVoiceInfo(Name, Engine, profile.LanguageCode, ModelArgument, Gender, SampleRate,
                NoiseScale, profile, SpeakerId, ConfigArgument, DisplayName)
            {
                SupportedLanguages = SupportedLanguages,
                AutomaticVoice = AutomaticVoice,
                AutomaticPriority = AutomaticPriority,
                AutomaticWeight = AutomaticWeight,
                PersonaKey = PersonaKey
            };
        }
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

    private enum SpeechSource
    {
        Actor,
        Npc,
        Interaction,
        Preview
    }

    private sealed class SpeechRequest(
        SpeechVoiceInfo voiceInfo, float scale, float pitch, string cleanedText, int generation)
    {
        internal SpeechVoiceInfo VoiceInfo { get; } = voiceInfo;
        internal float Scale { get; } = scale;
        internal float Pitch { get; } = pitch;
        internal string CleanedText { get; } = cleanedText;
        internal int Generation { get; } = generation;
        internal float Intonation { get; set; } = 1f;
        internal int VoiceVoxSession { get; } = VoiceVoxSessionVersion;
        internal object Owner { get; set; }
        internal SpeechSource Source { get; set; }
        internal volatile bool Canceled;
    }

    private sealed class SpeechPlayback : IDisposable
    {
        private readonly SpeechAudio _audio;
        private readonly WaveStream _waveStream;
        private bool _disposed;

        internal WaveOutEvent Output { get; } = new();
        internal SpeechRequest Request { get; }

        internal SpeechPlayback(SpeechAudio audio, SpeechRequest request)
        {
            _audio = audio;
            Request = request;

            try
            {
                audio.AudioStream.Position = 0;
                _waveStream = audio.IsWaveFile
                    ? new WaveFileReader(audio.AudioStream)
                    : new RawSourceWaveStream(audio.AudioStream, new WaveFormat(audio.SampleRate, 1));
                var samples = new SampleChannel(_waveStream) { Volume = Main.Settings.SpeechVolume };
                Output.Init(ApplySpeechPitch(samples,
                    request.VoiceInfo.Engine == SpeechEngine.VoiceVox ? 0f : request.Pitch));
                Output.PlaybackStopped += PlaybackStopped;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void PlaybackStopped(object sender, StoppedEventArgs e)
        {
            OnSpeechPlaybackStopped(this, e);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Output.PlaybackStopped -= PlaybackStopped;
            try
            {
                Output.Dispose();
            }
            finally
            {
                try
                {
                    _waveStream?.Dispose();
                }
                finally
                {
                    _audio.Dispose();
                }
            }
        }
    }

    private static readonly SpeechLanguageProfile JapaneseLanguageProfile = new(
        "ja",
        "ModUi/&SpeechLanguageJapanese",
        "ja",
        "こんにちは。今日は良い天気ですね。",
        SpeechScript.Kana | SpeechScript.Han);

    private static SpeechLanguageProfile CreateLanguageProfile(string code, string sample, SpeechScript script)
    {
        return new SpeechLanguageProfile(code, null, code, sample, script);
    }

    private static readonly SpeechLanguageProfile EnglishLanguageProfile = CreateLanguageProfile(
        "en", "Hello. Let us begin our adventure.", SpeechScript.Latin);

    private static readonly SpeechLanguageProfile[] LanguageProfiles =
    [
        EnglishLanguageProfile,
        CreateLanguageProfile("de", "Hallo. Beginnen wir unser Abenteuer.", SpeechScript.Latin),
        CreateLanguageProfile("es", "Hola. Comencemos nuestra aventura.", SpeechScript.Latin),
        CreateLanguageProfile("fr", "Bonjour. Commençons notre aventure.", SpeechScript.Latin),
        CreateLanguageProfile("it", "Ciao. Iniziamo la nostra avventura.", SpeechScript.Latin),
        JapaneseLanguageProfile,
        CreateLanguageProfile("ko", "안녕하세요. 모험을 시작합시다.", SpeechScript.Hangul),
        CreateLanguageProfile("pt", "Olá. Vamos começar nossa aventura.", SpeechScript.Latin),
        CreateLanguageProfile("ru", "Здравствуйте. Начнём наше приключение.", SpeechScript.Cyrillic),
        CreateLanguageProfile("zh", "你好。让我们开始冒险吧。", SpeechScript.Han)
    ];

    private static readonly Dictionary<string, SpeechLanguageProfile> AdditionalLanguageProfiles =
        new(StringComparer.Ordinal);

    private static SpeechLanguageProfile GetLanguageProfile(string language)
    {
        var code = NormalizeLanguageCode(language);
        var known = LanguageProfiles.FirstOrDefault(x => x.LanguageCode == code);
        if (known != null)
        {
            return known;
        }

        if (!AdditionalLanguageProfiles.TryGetValue(code, out var profile))
        {
            profile = CreateLanguageProfile(code, string.Empty, SpeechScript.None);
            AdditionalLanguageProfiles[code] = profile;
        }

        return profile;
    }

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

    internal static IReadOnlyList<SpeechLanguageProfile> DownloadableLanguageProfiles { get; } =
        PiperPlusVoiceProfiles.Select(x => x.LanguageProfile).Distinct().ToArray();

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

    private static readonly Dictionary<string, (string, float)> CampaignVoices = [];
    private static readonly Dictionary<string, string> AutomaticNpcVoices = new(StringComparer.Ordinal);
    private static string AutomaticActorSignature;
    private static Dictionary<int, string> AutomaticActorVoices = [];
    private static object NpcCatalogCampaign;
    private static object NpcCatalogSource;
    private static int NpcCatalogCount = -1;
    private static string NpcCatalogLanguage;
    private static IReadOnlyList<(string InternalName, string DisplayName)> NpcCatalog =
        Array.Empty<(string, string)>();

    internal static string[] Choices => new[] { Gui.Localize("ModUi/&SpeechNarrator") }
        .Concat(Enumerable.Range(1, MaxHeroes)
            .Select(n => Gui.Format("ModUi/&SpeechHero", n.ToString(CultureInfo.InvariantCulture)))).ToArray();

    internal static string[] VoiceNames { get; private set; } = [AutomaticVoice, DefaultVoice];

    internal static string GetVoiceDisplayName(string voiceName)
    {
        if (voiceName == DefaultNpcVoice)
        {
            return Gui.Localize("ModUi/&SpeechAutomatic");
        }

        if (voiceName == DefaultVoice)
        {
            return Gui.Localize("ModUi/&SpeechNoVoice");
        }

        var baseName = voiceName.Split('#')[0];
        if (baseName.StartsWith("windows-sapi:", StringComparison.Ordinal))
        {
            baseName = baseName.Substring(baseName.LastIndexOf('\\') + 1);
        }
        var label = baseName switch
        {
            "ja_JP-tsukuyomi-chan-medium" => Gui.Localize("ModUi/&SpeechVoiceTsukuyomi"),
            "ja_JP-css10-medium" => Gui.Localize("ModUi/&SpeechVoiceCss10"),
            _ => baseName
        };

        if (!VoiceInfos.TryGetValue(voiceName, out var voiceInfo))
        {
            return Gui.Format("ModUi/&SpeechVoiceUnavailable", label);
        }

        if (!string.IsNullOrEmpty(voiceInfo.DisplayName))
        {
            label = voiceInfo.DisplayName;
        }
        else if (voiceName.Contains("#"))
        {
            label = Gui.Format("ModUi/&SpeechVoiceSpeaker", label,
                (voiceInfo.SpeakerId + 1).ToString(CultureInfo.InvariantCulture));
        }

        return IsVoiceAvailable(voiceInfo) ? label : Gui.Format("ModUi/&SpeechVoiceUnavailable", label);
    }

    internal static string[] GetSelectableVoiceNames(bool allLanguages, string selectedVoice)
    {
        EnsureVoiceDataInitialized();
        var language = NormalizeLanguageCode(LocalizationManager.CurrentLanguageCode);
        var matchingVoices = VoiceNames.Where(x => VoiceInfos.TryGetValue(x, out var info) &&
                                                 info.SupportedLanguages.Contains(language)).ToArray();
        var voices = allLanguages
            ? VoiceNames
            : new[] { AutomaticVoice, DefaultVoice }.Concat(matchingVoices).ToArray();

        // Keep a saved choice visible even while its model is missing or a language filter is active.
        return string.IsNullOrEmpty(selectedVoice) || voices.Contains(selectedVoice)
            ? voices
            : voices.Concat(new[] { selectedVoice }).ToArray();
    }

    internal static bool IsVoiceVoxVoice(string voiceName)
    {
        return voiceName?.StartsWith("voicevox:", StringComparison.Ordinal) == true;
    }

    private static string GetVoiceGenderOverrideKey(string voiceName)
    {
        if (!IsVoiceVoxVoice(voiceName))
        {
            return voiceName;
        }

        var parts = voiceName.Split(':');
        return parts.Length == 3 && Guid.TryParse(parts[1], out var uuid)
            ? "voicevox:" + uuid.ToString("D")
            : voiceName;
    }

    internal static int GetVoiceGenderOverride(string voiceName)
    {
        EnsureSpeechVoiceSettings();
        var key = GetVoiceGenderOverrideKey(voiceName);
        return key != null && Main.Settings.SpeechVoiceGenderOverrides.TryGetValue(key, out var choice) &&
               choice is >= 1 and <= 3
            ? choice
            : 0;
    }

    internal static void SetVoiceGenderOverride(string voiceName, int choice)
    {
        EnsureSpeechVoiceSettings();
        var key = GetVoiceGenderOverrideKey(voiceName);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        choice = Math.Min(3, Math.Max(0, choice));
        if (choice == 0)
        {
            Main.Settings.SpeechVoiceGenderOverrides.Remove(key);
        }
        else
        {
            Main.Settings.SpeechVoiceGenderOverrides[key] = choice;
        }

        UpdateAvailableVoices();
    }

    private static Gender? GetVoiceGender(SpeechVoiceInfo info)
    {
        if (Main.Settings.SpeechVoiceGenderOverrides?.TryGetValue(GetVoiceGenderOverrideKey(info.Name),
                out var choice) == true)
        {
            switch (choice)
            {
                case 1: return Gender.Male;
                case 2: return Gender.Female;
                case 3: return null;
            }
        }

        return info.Gender;
    }

    internal static string[] GetSelectableNpcVoiceNames(bool allLanguages, string selectedVoice)
    {
        return new[] { DefaultNpcVoice }.Concat(GetSelectableVoiceNames(allLanguages, selectedVoice))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<(string InternalName, string DisplayName)> GetCurrentCampaignNpcs()
    {
        var campaign = Gui.Session?.UserCampaign;
        if (Gui.Game?.CampaignDefinition?.IsUserCampaign != true ||
            string.IsNullOrEmpty(campaign?.InternalName) || campaign.UserNpcs == null)
        {
            return Array.Empty<(string, string)>();
        }

        var language = LocalizationManager.CurrentLanguageCode;
        if (ReferenceEquals(NpcCatalogCampaign, campaign) && ReferenceEquals(NpcCatalogSource, campaign.UserNpcs) &&
            NpcCatalogCount == campaign.UserNpcs.Count && NpcCatalogLanguage == language)
        {
            return NpcCatalog;
        }

        NpcCatalogCampaign = campaign;
        NpcCatalogSource = campaign.UserNpcs;
        NpcCatalogCount = campaign.UserNpcs.Count;
        NpcCatalogLanguage = language;
        NpcCatalog = campaign.UserNpcs.Where(x => x != null && !string.IsNullOrEmpty(x.InternalName))
            .GroupBy(x => x.InternalName, StringComparer.Ordinal).Select(x => x.First())
            .Select(x => (x.InternalName, string.IsNullOrWhiteSpace(x.DisplayTitle) ? x.InternalName : x.DisplayTitle))
            .OrderBy(x => x.Item2, StringComparer.CurrentCulture).ToArray();
        return NpcCatalog;
    }

    private static string GetNpcOverrideKey(string internalName)
    {
        var campaignName = Gui.Session?.UserCampaign?.InternalName;
        if (Gui.Game?.CampaignDefinition?.IsUserCampaign != true ||
            string.IsNullOrEmpty(campaignName) || string.IsNullOrEmpty(internalName))
        {
            return null;
        }

        return BuildNpcOverrideKey(campaignName, internalName);
    }

    private static string BuildNpcOverrideKey(string campaignName, string internalName)
    {
        // Length prefixes keep both identities unambiguous, regardless of separators in their names.
        return campaignName.Length.ToString(CultureInfo.InvariantCulture) + ":" + campaignName +
               internalName.Length.ToString(CultureInfo.InvariantCulture) + ":" + internalName;
    }

    internal static (string Voice, float Scale, float Pitch, float Intonation) GetNpcSpeechSettings(string internalName)
    {
        EnsureSpeechVoiceSettings();
        var key = GetNpcOverrideKey(internalName);
        return key != null && Main.Settings.SpeechNpcVoiceOverrides.TryGetValue(key, out var settings)
            ? NormalizeNpcSpeechSettings(settings)
            : (DefaultNpcVoice, Main.Settings.SpeechVoices[0].Item2, 0f,
                Main.Settings.SpeechVoiceIntonations[0]);
    }

    internal static void SetNpcSpeechSettings(string internalName, string voice, float scale, float pitch,
        float intonation)
    {
        EnsureSpeechVoiceSettings();
        var key = GetNpcOverrideKey(internalName);
        if (key == null || !GetCurrentCampaignNpcs().Any(x => x.InternalName == internalName))
        {
            return;
        }

        if (string.IsNullOrEmpty(voice) || voice == DefaultNpcVoice)
        {
            Main.Settings.SpeechNpcVoiceOverrides.Remove(key);
        }
        else
        {
            Main.Settings.SpeechNpcVoiceOverrides[key] = NormalizeNpcSpeechSettings((voice, scale, pitch, intonation));
        }
        AutomaticNpcVoices.Clear();
    }

    private static float ClampSpeechValue(float value, float minimum, float maximum, float defaultValue)
    {
        return float.IsNaN(value) || float.IsInfinity(value)
            ? defaultValue
            : Math.Min(maximum, Math.Max(minimum, value));
    }

    private static (string Voice, float Scale, float Pitch, float Intonation) NormalizeNpcSpeechSettings(
        (string Voice, float Scale, float Pitch, float Intonation) settings)
    {
        return (string.IsNullOrEmpty(settings.Voice) ? DefaultNpcVoice : settings.Voice,
            ClampSpeechValue(settings.Scale, 0.5f, 2f, DefaultScale),
            ClampSpeechValue(settings.Pitch, -6f, 6f, 0f),
            ClampSpeechValue(settings.Intonation, 0f, 2f, 1f));
    }

    private static string GetModelConfigPath(string modelPath)
    {
        return File.Exists(modelPath + ".json")
            ? modelPath + ".json"
            : Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty, "config.json");
    }

    private static string NormalizeLanguageCode(string language)
    {
        return (language ?? string.Empty).Split('_', '-')[0].ToLowerInvariant();
    }

    private static void RegisterModelVoices(
        string voiceName, SpeechEngine engine, string language, string modelPath, string configPath,
        Gender? gender, int sampleRate, float noiseScale, SpeechLanguageProfile languageProfile,
        List<string> voiceNames)
    {
        try
        {
            if (!File.Exists(configPath) || new FileInfo(configPath).Length > 1024 * 1024)
            {
                return;
            }

            var config = JObject.Parse(File.ReadAllText(configPath));
            var configuredLanguage = config["language"]?["code"]?.Value<string>();
            var supportedLanguages = Array.Empty<string>();
            if (!string.IsNullOrEmpty(configuredLanguage) && configuredLanguage != "multilingual")
            {
                language = NormalizeLanguageCode(configuredLanguage);
            }
            else if (config["language_id_map"] is JObject languageIds)
            {
                supportedLanguages = languageIds.Properties().Select(x => NormalizeLanguageCode(x.Name))
                    .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.Ordinal).ToArray();
                language = supportedLanguages.FirstOrDefault() ?? language;
            }

            language = NormalizeLanguageCode(language);
            if (string.IsNullOrEmpty(language))
            {
                return;
            }
            languageProfile = GetLanguageProfile(language);
            if (supportedLanguages.Length == 0)
            {
                supportedLanguages = [language];
            }
            sampleRate = config["audio"]?["sample_rate"]?.Value<int>() ?? sampleRate;
            var speakerCount = config["num_speakers"]?.Value<int>() ?? 1;

            if (sampleRate is < 8000 or > 96000 || speakerCount is < 1 or > 4096)
            {
                return;
            }

            var speakerMap = config["speaker_id_map"] as JObject;
            var speakerIds = speakerCount == 1
                ? new[] { 0 }
                : (speakerMap?.HasValues == true
                    ? speakerMap.Properties().Select(x => x.Value.Value<int>())
                    : Enumerable.Range(0, speakerCount))
                .Distinct().OrderBy(x => x).ToArray();

            if (speakerIds.Length == 0 || speakerIds.Any(x => x < 0 || x >= speakerCount))
            {
                return;
            }

            foreach (var speakerId in speakerIds)
            {
                var name = speakerCount == 1 ? voiceName : $"{voiceName}#{speakerId}";
                voiceNames.Add(name);
                VoiceInfos[name] = new SpeechVoiceInfo(
                    name, engine, language, modelPath, gender, sampleRate, noiseScale, languageProfile,
                    speakerId, configPath) { SupportedLanguages = supportedLanguages };
            }
        }
        catch (Exception ex)
        {
            Main.Info($"Cannot read voice configuration {Path.GetFileName(configPath)}: {ex.Message}");
        }
    }

    private static void RegisterAdditionalPiperPlusVoices(List<string> voiceNames)
    {
        if (!TryGetPiperPlusExecutablePath(out _) || !Directory.Exists(PiperPlusModelsFolder))
        {
            return;
        }

        var registeredModels = new HashSet<string>(
            VoiceInfos.Values.Where(x => x.Engine == SpeechEngine.PiperPlus).Select(x => x.ModelArgument),
            StringComparer.OrdinalIgnoreCase);

        foreach (var modelPath in Directory.EnumerateFiles(PiperPlusModelsFolder, "*.onnx", SearchOption.AllDirectories)
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            if (registeredModels.Contains(modelPath))
            {
                continue;
            }

            var relativePath = modelPath.Substring(PiperPlusModelsFolder.Length + 1)
                .Replace(Path.DirectorySeparatorChar, '/');
            RegisterModelVoices(
                "piper-plus:" + relativePath, SpeechEngine.PiperPlus, string.Empty, modelPath,
                GetModelConfigPath(modelPath), null, 22050, 0.667f, null, voiceNames);
        }
    }

    private static string[] ParseWindowsVoiceLanguages(string attribute)
    {
        var languages = new List<string>();
        // SAPI's Language attribute is a semicolon-delimited list of hexadecimal LANGIDs.
        // https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ms717036(v=vs.85)
        foreach (var entry in (attribute ?? string.Empty).Split(';'))
        {
            if (!int.TryParse(entry.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lcid) ||
                lcid is <= 0 or > ushort.MaxValue)
            {
                continue;
            }

            try
            {
                var language = NormalizeLanguageCode(CultureInfo.GetCultureInfo(lcid).TwoLetterISOLanguageName);
                if (!languages.Contains(language, StringComparer.Ordinal))
                {
                    languages.Add(language);
                }
            }
            catch (CultureNotFoundException)
            {
                // One bad optional locale must not discard the token's remaining supported languages.
            }
        }

        return languages.ToArray();
    }

    private static void RegisterWindowsVoices(List<string> voiceNames)
    {
        using var process = CreateWindowsSpeechProcess();
        if (process == null)
        {
            return;
        }

        try
        {
            if (!TryStartAndRegisterSpeechProcess(process))
            {
                return;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var input = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { operation = "voices" }));
            process.StandardInput.BaseStream.Write(input, 0, input.Length);
            process.StandardInput.Close();

            if (!process.WaitForExit(10000))
            {
                process.Kill();
                process.WaitForExit();
                Main.Info("Windows speech voice detection timed out.");
                return;
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(error))
            {
                Main.Info($"Windows speech voice detection: {error.Trim()}");
            }

            if (process.ExitCode != 0)
            {
                return;
            }

            foreach (var token in JArray.Parse(output))
            {
                try
                {
                    var tokenId = token["id"]?.Value<string>();
                    var languages = ParseWindowsVoiceLanguages(token["language"]?.Value<string>());
                    if (string.IsNullOrEmpty(tokenId) || languages.Length == 0)
                    {
                        continue;
                    }

                    var language = languages[0];
                    var genderText = token["gender"]?.Value<string>();
                    Gender? gender = string.Equals(genderText, "Female", StringComparison.OrdinalIgnoreCase)
                        ? Gender.Female
                        : string.Equals(genderText, "Male", StringComparison.OrdinalIgnoreCase) ? Gender.Male : null;
                    var name = "windows-sapi:" + tokenId;
                    var description = token["description"]?.Value<string>();
                    voiceNames.Add(name);
                    VoiceInfos[name] = new SpeechVoiceInfo(
                        name, SpeechEngine.WindowsSapi, language, tokenId, gender, 22050, 0f,
                        GetLanguageProfile(language), displayName: description) { SupportedLanguages = languages };
                }
                catch (Exception ex)
                {
                    Main.Info($"Cannot register Windows speech voice: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Main.Info($"Windows speech voices are unavailable: {ex.Message}");
        }
        finally
        {
            UnregisterSpeechProcess(process);
        }
    }

    internal static void Load()
    {
        Unloading = false;
        EnsureSpeechVoiceSettings();

        //A fix for UB issue that was setting global game volume when any generated speech was played
        //A one-off change to not force confused users to go into windows sound mixer and change there
        if (!Main.Settings.FixGameVolume)
        {
            Main.Settings.FixGameVolume = true;

            if (CurrentPlayback != null)
            {
                try
                {
                    CurrentPlayback.Output.Volume = 1;
                }
                catch (Exception)
                {
                    // calling this setter crashes on Linux
                }
            }
        }
    }

    internal static void EnsureVoiceDataInitialized()
    {
        if (VoiceDataInitialized)
        {
            return;
        }

        RefreshAvailableVoices();
    }

    private static void EnsurePiperInitialized()
    {
        if (PiperInitializationAttempted)
        {
            return;
        }

        PiperInitializationAttempted = true;
        InitPiper();
        PiperInitializationAttempted = TryGetLegacyPiperExecutablePath(out _);
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

        try
        {
            if (Directory.Exists(PiperFolder))
            {
                return;
            }

            using var wc = new WebClient();

            wc.DownloadFile(url, fullZipFile);
            ZipFile.ExtractToDirectory(fullZipFile, Main.ModFolder);
            File.Delete(fullZipFile);

            if (!TryGetLegacyPiperExecutablePath(out _))
            {
                message = "Piper successfully downloaded but failed to extract executable.";
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
        EnsureSpeechVoiceSettings();
        UpdateVoiceVoxSession();

        if (!Directory.Exists(VoicesFolder))
        {
            Directory.CreateDirectory(VoicesFolder);
        }

        VoiceInfos.Clear();

        var voiceNames = new List<string> { AutomaticVoice, DefaultVoice };

        foreach (var file in (TryGetLegacyPiperExecutablePath(out _)
                     ? Directory.EnumerateFiles(VoicesFolder, "*.onnx", SearchOption.TopDirectoryOnly)
                     : Enumerable.Empty<string>())
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            var voiceName = Path.GetFileNameWithoutExtension(file);

            if (string.IsNullOrEmpty(voiceName))
            {
                continue;
            }

            RegisterModelVoices(
                voiceName, SpeechEngine.LegacyPiper, "en", file, GetModelConfigPath(file),
                TryGetSuggestedVoiceGender(voiceName, out var gender) ? gender : null,
                22050, 0.667f, null, voiceNames);
        }

        foreach (var profile in PiperPlusVoiceProfiles)
        {
            if (!IsVoiceProfileAvailable(profile))
            {
                continue;
            }

            if (TryGetVoiceProfileModel(profile, out var modelPath))
            {
                RegisterModelVoices(
                    profile.VoiceName, profile.Engine, profile.LanguageProfile.LanguageCode,
                    modelPath, GetModelConfigPath(modelPath), profile.Gender, profile.SampleRate,
                    profile.NoiseScale, profile.LanguageProfile, voiceNames);
            }
        }

        RegisterAdditionalPiperPlusVoices(voiceNames);
        RegisterWindowsVoices(voiceNames);
        if (Main.Settings.EnableVoiceVoxSpeech)
        {
            foreach (var voice in VoiceVoxVoices)
            {
                VoiceInfos[voice.Name] = voice;
                voiceNames.Add(voice.Name);
            }
        }
        VoiceNames = voiceNames.Distinct(StringComparer.Ordinal).ToArray();
        UpdateAvailableVoices();
        VoiceDataInitialized = true;
        CampaignVoiceDataInitialized = false;
        NpcCatalogCampaign = null;
        NpcCatalogSource = null;
        NpcCatalogCount = -1;
        NpcCatalog = Array.Empty<(string, string)>();
        _ = RefreshVoiceVoxVoicesAsync();
    }

    private static bool UpdateVoiceVoxSession()
    {
        var enabled = Main.Settings.EnableVoiceVoxSpeech && !Unloading;
        if (VoiceVoxSessionEnabled != enabled)
        {
            VoiceVoxSessionEnabled = enabled;
            VoiceVoxSessionVersion++;
            if (!enabled)
            {
                VoiceVoxRefreshVersion++;
                VoiceVoxRefreshInProgress = false;
                VoiceVoxEngineAvailable = false;
                CancelVoiceVoxSpeech();
                StopManagedVoiceVox();
                UpdateAvailableVoices();
            }
        }
        return enabled;
    }

    private static async Task RefreshVoiceVoxVoicesAsync()
    {
        var enabled = UpdateVoiceVoxSession();
        var version = ++VoiceVoxRefreshVersion;
        VoiceVoxRefreshInProgress = enabled;
        if (!VoiceVoxRefreshInProgress)
        {
            VoiceVoxEngineAvailable = false;
            return;
        }
        AbortSpeechHttpRequests(refreshOnly: true);
        ConnectedVoiceVoxVersion = null;
        InstalledVoiceVoxAppVersion = FindInstalledVoiceVoxAppVersion();
        if (Main.Settings.CheckVoiceVoxUpdatesAutomatically)
        {
            _ = RefreshVoiceVoxUpdateAsync(false);
        }

        // Started on Unity's thread: the continuation updates its voice catalog on the same context.
        try
        {
            var bytes = await ConnectVoiceVoxAsync();
            if (version != VoiceVoxRefreshVersion || Unloading || !Main.Settings.EnableVoiceVoxSpeech)
            {
                return;
            }

            var speakers = JArray.Parse(Encoding.UTF8.GetString(bytes));
            var voices = ParseVoiceVoxVoices(speakers);
            VoiceVoxVoices.Clear();
            VoiceVoxVoices.AddRange(voices);
            foreach (var name in VoiceInfos.Keys.Where(IsVoiceVoxVoice).ToArray())
            {
                VoiceInfos.Remove(name);
            }
            foreach (var voice in voices)
            {
                VoiceInfos[voice.Name] = voice;
            }
            VoiceNames = VoiceNames.Where(x => !IsVoiceVoxVoice(x)).Concat(voices.Select(x => x.Name))
                .Distinct(StringComparer.Ordinal).ToArray();
            VoiceVoxEngineAvailable = voices.Count > 0;
            UpdateAvailableVoices();
            CampaignVoiceDataInitialized = false;
            // Optional version discovery does not decide whether the voice catalogue is usable.
            var engineVersion = await ReadVoiceVoxEngineVersionAsync();
            if (version == VoiceVoxRefreshVersion && !Unloading && Main.Settings.EnableVoiceVoxSpeech)
            {
                ConnectedVoiceVoxVersion = engineVersion;
            }
        }
        catch (Exception)
        {
            if (version == VoiceVoxRefreshVersion && !Unloading)
            {
                // Missing optional engines must leave installed voices and saved assignments intact.
                VoiceVoxEngineAvailable = false;
                UpdateAvailableVoices();
            }
        }
        finally
        {
            if (version == VoiceVoxRefreshVersion)
            {
                VoiceVoxRefreshInProgress = false;
            }
        }
    }

    private static List<SpeechVoiceInfo> ParseVoiceVoxVoices(JArray speakers)
    {
        var result = new List<SpeechVoiceInfo>();
        foreach (var speaker in speakers.OfType<JObject>())
        {
            var uuid = speaker["speaker_uuid"]?.Value<string>();
            var name = speaker["name"]?.Value<string>();
            if (!Guid.TryParse(uuid, out var speakerGuid) || string.IsNullOrWhiteSpace(name) ||
                speaker["styles"] is not JArray styles)
            {
                continue;
            }
            var talkStyles = styles.OfType<JObject>().Where(x => x["type"] == null ||
                x["type"].Value<string>() is "talk" or "streaming_talk").ToArray();
            var defaultStyle = talkStyles.FirstOrDefault(x => x["name"]?.Value<string>() is "ノーマル" or "ふつう")
                               ?? talkStyles.FirstOrDefault();
            foreach (var style in talkStyles)
            {
                if (!int.TryParse(style["id"]?.ToString(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var id) || id < 0 ||
                    string.IsNullOrWhiteSpace(style["name"]?.Value<string>()))
                {
                    continue;
                }
                var voiceName = "voicevox:" + speakerGuid.ToString("D") + ":" + id.ToString(CultureInfo.InvariantCulture);
                var voice = new SpeechVoiceInfo(voiceName, SpeechEngine.VoiceVox, "ja",
                    speakerGuid.ToString("D"), GetVoiceVoxSuggestedGender(speakerGuid.ToString("D"), id),
                    24000, 0f, JapaneseLanguageProfile, id, null,
                    name + "（" + style["name"].Value<string>() + "） - VOICEVOX")
                {
                    AutomaticVoice = style == defaultStyle,
                    AutomaticPriority = 1,
                    PersonaKey = "voicevox:" + speakerGuid.ToString("D")
                };
                voice.AutomaticVoice &= !IsUnsuitableGeneralNpcVoice(voice);
                voice.AutomaticWeight = IsPreferredAdultVoice(voice) ? 2 : 1;
                result.Add(voice);
            }
        }
        return result.GroupBy(x => x.Name, StringComparer.Ordinal).Select(x => x.First()).ToList();
    }

    private static Gender? GetVoiceVoxSuggestedGender(string speakerUuid, int styleId)
    {
        // /speakers provides no gender metadata. Official speaker personas and voice descriptions
        // provide adjustable casting defaults; a speaker's fictional age does not determine voice age.
        // UUIDs are from https://github.com/VOICEVOX/voicevox_resource/tree/0.25.2/character_info
        // Documented neutral voices and unknown/custom speakers remain unclassified.
        return speakerUuid switch
        {
            "c30dc15a-0992-4f8d-8bb8-ad3b314e6a6f" => Gender.Male,   // 玄野武宏: young man's voice
            "4f51116a-d9ee-4516-925d-21f183e2afad" => Gender.Male,   // 青山龍星
            "0acebdee-a4a5-4e12-a695-e19609728e30" => Gender.Male,   // 雀松朱司
            "7d1e7ba7-f957-40e5-a3fc-da49f769ab65" => Gender.Male,   // 麒ヶ島宗麟: older man's voice
            "e5020595-5c5d-4e87-b849-270a518d0dcf" => Gender.Male,   // 白上虎太郎: adolescent boy's voice
            "67d5d8da-acd7-4207-bb10-b5542d3a663b" => Gender.Female, // WhiteCUL
            "044830d2-f23b-44d6-ac0d-b5d733caa900" => Gender.Female, // No.7
            "7ffcb7ce-00ec-4bdc-82cd-45a8889e43ff" => Gender.Female, // 四国めたん
            "35b2c544-660e-401e-b503-0e14c635303a" => Gender.Female, // 春日部つむぎ
            "3474ee95-c274-47f9-aa1a-8322163d96f1" => Gender.Female, // 雨晴はう
            "481fb609-6446-4870-9f46-90c4dd623403" => Gender.Female, // 九州そら
            "0693554c-338e-4790-8982-b9c6d476dc69" => Gender.Female, // 櫻歌ミコ: girl's voice
            "0f56c2f2-644c-49c9-8989-94e11f7129d0" when styleId == 27 => Gender.Female,
            _ => null
        };
    }

    private static bool IsPreferredAdultVoice(SpeechVoiceInfo voice)
    {
        if (voice.Engine != SpeechEngine.VoiceVox)
        {
            return false;
        }

        // Young adult / older adult / adult voices are described explicitly on the official pages.
        // 後鬼's plush style has a different voice and must not inherit the human style's profile.
        // https://voicevox.hiroshiba.jp/product/kyushu_sora/
        return voice.ModelArgument is "c30dc15a-0992-4f8d-8bb8-ad3b314e6a6f" or
                   "7d1e7ba7-f957-40e5-a3fc-da49f769ab65" or "481fb609-6446-4870-9f46-90c4dd623403" ||
               voice.ModelArgument == "0f56c2f2-644c-49c9-8989-94e11f7129d0" && voice.SpeakerId == 27;
    }

    private static bool IsUnsuitableGeneralNpcVoice(SpeechVoiceInfo voice)
    {
        // Explicit child-like / electronic voice descriptions are not suitable defaults when an
        // NPC's speaking age or role is unknown. Manual NPC and author assignments still allow them.
        // https://voicevox.hiroshiba.jp/product/zundamon/
        // https://voicevox.hiroshiba.jp/product/voidoll/
        return voice.Engine == SpeechEngine.VoiceVox && voice.ModelArgument is
            "388f246b-8c41-4ac1-8e2d-5d79f3ff56d9" or "0ebe2c7d-96f3-4f0e-a2e3-ae13fe27c403";
    }

    private static void EnsureSpeechVoiceSettings()
    {
        Main.Settings.SpeechVoices ??= [];
        Main.Settings.SpeechChoice = Math.Min(Math.Max(Main.Settings.SpeechChoice, 0), MaxHeroes);

        // remove any invalid key
        Main.Settings.SpeechVoices.Keys
            .Where(x => x is < 0 or > MaxHeroes)
            .ToArray()
            .Do(x => Main.Settings.SpeechVoices.Remove(x));

        for (var i = 0; i <= MaxHeroes; i++)
        {
            Main.Settings.SpeechVoices.TryAdd(i, (AutomaticVoice, DefaultScale));
            var (voice, scale) = Main.Settings.SpeechVoices[i];
            Main.Settings.SpeechVoices[i] = (string.IsNullOrEmpty(voice) ? AutomaticVoice : voice,
                float.IsNaN(scale) || float.IsInfinity(scale) ? DefaultScale : Math.Min(2f, Math.Max(0.5f, scale)));
        }

        Main.Settings.SpeechVoicePitches ??= [];
        Main.Settings.SpeechVoiceIntonations ??= [];
        Main.Settings.SpeechNpcVoiceOverrides ??= [];
        Main.Settings.SpeechVoiceGenderOverrides ??= [];
        Main.Settings.SpeechVoicePitches.Keys.Where(x => x is < 0 or > MaxHeroes).ToArray()
            .Do(x => Main.Settings.SpeechVoicePitches.Remove(x));
        Main.Settings.SpeechVoiceIntonations.Keys.Where(x => x is < 0 or > MaxHeroes).ToArray()
            .Do(x => Main.Settings.SpeechVoiceIntonations.Remove(x));

        for (var i = 0; i <= MaxHeroes; i++)
        {
            Main.Settings.SpeechVoicePitches.TryGetValue(i, out var pitch);
            Main.Settings.SpeechVoicePitches[i] = float.IsNaN(pitch) || float.IsInfinity(pitch)
                ? 0f
                : Math.Min(6f, Math.Max(-6f, pitch));
            Main.Settings.SpeechVoiceIntonations.TryAdd(i, 1f);
            Main.Settings.SpeechVoiceIntonations[i] =
                ClampSpeechValue(Main.Settings.SpeechVoiceIntonations[i], 0f, 2f, 1f);
        }
    }

    internal static void UpdateAvailableVoices()
    {
        EnsureSpeechVoiceSettings();
        // Voice refreshes and actor choices change the automatic assignment's candidate pool.
        AutomaticNpcVoices.Clear();
        AutomaticActorSignature = null;
        VoiceCatalogRevision++;
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
        return TryGetVoiceProfileModel(profile, out _);
    }

    private static bool TryGetVoiceProfileModel(SpeechVoiceProfile profile, out string modelPath)
    {
        modelPath = null;
        if (!Directory.Exists(PiperPlusModelsFolder))
        {
            return false;
        }

        try
        {
            // Prefer the profile's own configuration directory over older flat installations.
            var profileFolder = Path.Combine(PiperPlusModelsFolder, profile.ModelArgument);
            modelPath = Directory.EnumerateFiles(PiperPlusModelsFolder, profile.ModelSearchPattern,
                    SearchOption.AllDirectories)
                .Where(x => File.Exists(GetModelConfigPath(x)))
                .OrderByDescending(x => string.Equals(Path.GetDirectoryName(x), profileFolder,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x, StringComparer.Ordinal)
                .FirstOrDefault();
            return modelPath != null;
        }
        catch
        {
            return false;
        }
    }

    internal static void CollectCustomCampaignVoiceData()
    {
        const string UB_VOICE_DATA = "UB_VOICE_DATA";

        CampaignVoices.Clear();
        AutomaticNpcVoices.Clear();
        CampaignVoiceDataInitialized = false;

        if (!Gui.GameCampaign ||
            Gui.Game?.CampaignDefinition?.IsUserCampaign != true)
        {
            return;
        }

        if (!Main.Settings.EnableSpeech &&
            !Main.Settings.EnableSpeechOnNpcs &&
            !Main.Settings.EnableSpeechOnInteractions)
        {
            return;
        }

        EnsureVoiceDataInitialized();

        var userCampaign = Gui.Session?.UserCampaign;
        var voiceData = userCampaign?.UserItems?.FirstOrDefault(x =>
            x.ReferenceItemDefinition?.IsDocument == true &&
            x.DocumentFragments is { Count: > 0 } &&
            x.InternalName == UB_VOICE_DATA);

        if (voiceData == null)
        {
            CampaignVoiceDataInitialized = true;
            return;
        }

        CampaignVoiceDataInitialized = true;
        var userNpcs = userCampaign.UserNpcs;

        if (userNpcs == null)
        {
            return;
        }

        var validVoices = new HashSet<string>(VoiceNames, StringComparer.Ordinal);
        var validNpcs = new HashSet<string>(
            userNpcs.Select(x => x.InternalName),
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
                if (!float.TryParse(arr[2], NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
                {
                    Main.Info($"Failed to parse voice scale data: [{fragment}]");
                    scale = DefaultScale;
                }
            }

            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0.5 || scale > 2)
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
            ResetSpeechNoLock();
        }
        AbortSpeechHttpRequests();
    }

    private static void ResetSpeechNoLock()
    {
        SpeechGeneration++;
        SpeechQueue.Clear();
        CurrentSpeechRequest = null;
        StopSpeechPlaybackNoLock();
    }

    internal static bool HasPendingSpeech(object owner)
    {
        if (owner == null)
        {
            return false;
        }

        lock (SpeechLock)
        {
            var request = CurrentSpeechRequest != null && ReferenceEquals(CurrentSpeechRequest.Owner, owner)
                ? CurrentSpeechRequest
                : SpeechQueue.FirstOrDefault(item => ReferenceEquals(item.Owner, owner));
            if (request == null)
            {
                return false;
            }

            if (Unloading || IsSpeechRequestCanceled(request) ||
                ServiceRepository.GetService<IAudioSettingsService>()?.MasterEnabled != true)
            {
                CancelSpeech(owner);
                return false;
            }

            return true;
        }
    }

    internal static void CancelSpeech(object owner)
    {
        if (owner == null)
        {
            return;
        }

        lock (SpeechLock)
        {
            CancelSpeechRequestsNoLock(request => ReferenceEquals(request.Owner, owner));
        }
    }

    private static void CancelSpeechRequestsNoLock(Func<SpeechRequest, bool> matches)
    {
        // Keep unmatched requests in their original order, and mark every removed
        // request so in-flight providers observe the same cancellation state.
        var count = SpeechQueue.Count;
        for (var i = 0; i < count; i++)
        {
            var request = SpeechQueue.Dequeue();
            if (matches(request))
            {
                request.Canceled = true;
            }
            else
            {
                SpeechQueue.Enqueue(request);
            }
        }

        var current = CurrentSpeechRequest;
        if (current == null || !matches(current))
        {
            return;
        }

        current.Canceled = true;
        StopSpeechPlaybackNoLock();
        CompleteSpeechRequestNoLock(current);
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
            ResetSpeechNoLock();
        }

        VoiceVoxRefreshVersion++;
        VoiceVoxSessionVersion++;
        VoiceVoxSessionEnabled = false;
        VoiceVoxRefreshInProgress = false;
        VoiceVoxEngineAvailable = false;
        VoiceVoxVoices.Clear();
        AbortSpeechHttpRequests(all: true);
        StopManagedVoiceVox();

        StopSpeechProcesses();
        PiperPlusVoiceDownloader.Unload(destroyUnityObjects);
        VoicesDownloader.Unload(destroyUnityObjects);
        PiperInitializationAttempted = false;
        VoiceDataInitialized = false;
        CampaignVoiceDataInitialized = false;
        CampaignVoices.Clear();
        AutomaticNpcVoices.Clear();
        AutomaticActorSignature = null;
        AutomaticActorVoices = [];
        NpcCatalogCampaign = null;
        NpcCatalogSource = null;
        NpcCatalogCount = -1;
        NpcCatalog = Array.Empty<(string, string)>();
    }

    private static string StripXmlTagsAndNarration(string str)
    {
        return RemoveNpcSpeechTags.Replace(str, string.Empty);
    }

    private static void EnsureCampaignVoiceDataInitialized()
    {
        if (CampaignVoiceDataInitialized)
        {
            return;
        }

        if (!Gui.GameCampaign)
        {
            return;
        }

        CollectCustomCampaignVoiceData();
    }

    private static bool IsSpeechSourceEnabled(SpeechSource source) => source switch
    {
        SpeechSource.Actor => Main.Settings.EnableSpeech,
        SpeechSource.Npc => Main.Settings.EnableSpeechOnNpcs,
        SpeechSource.Interaction => Main.Settings.EnableSpeechOnInteractions,
        SpeechSource.Preview => Main.Settings.EnableSpeech || Main.Settings.EnableSpeechOnNpcs ||
                                Main.Settings.EnableSpeechOnInteractions,
        _ => false
    };

    private static void QueueSpeechRequest(SpeechVoiceInfo voiceInfo, float scale, string cleanedText, float pitch,
        float intonation, object owner, SpeechSource source)
    {
        if (voiceInfo == null ||
            string.IsNullOrWhiteSpace(cleanedText) ||
            Unloading)
        {
            return;
        }

        lock (SpeechLock)
        {
            if (Unloading)
            {
                return;
            }

            SpeechQueue.Enqueue(new SpeechRequest(voiceInfo, scale, pitch, cleanedText, SpeechGeneration)
            {
                Intonation = ClampSpeechValue(intonation, 0f, 2f, 1f),
                Owner = owner,
                Source = source
            });
            StartNextSpeechNoLock();
        }
    }

    private static void StartNextSpeechNoLock()
    {
        // The current request owns the queue while either synthesizing or playing.
        if (Unloading || CurrentSpeechRequest != null)
        {
            return;
        }

        while (SpeechQueue.Count > 0)
        {
            var request = SpeechQueue.Dequeue();
            if (IsSpeechRequestCanceled(request) ||
                ServiceRepository.GetService<IAudioSettingsService>()?.MasterEnabled != true)
            {
                request.Canceled = true;
                continue;
            }

            CurrentSpeechRequest = request;
            _ = SynthesizeAndPlaySpeechAsync(request);
            break;
        }
    }

    private static async Task SynthesizeAndPlaySpeechAsync(SpeechRequest request)
    {
        SpeechAudio speechAudio = null;
        var previous = SynthesizingSpeech.Value;
        SynthesizingSpeech.Value = request;

        try
        {
            speechAudio = await SynthesizeSpeechRequestForSessionAsync(
                request.VoiceInfo, request.Scale, request.CleanedText, request.Generation,
                request.Pitch, request.Intonation, request.VoiceVoxSession);

            lock (SpeechLock)
            {
                if (Unloading || CurrentSpeechRequest != request)
                {
                    return;
                }

                if (IsSpeechRequestCanceled(request) || speechAudio == null)
                {
                    CompleteSpeechRequestNoLock(request);
                    return;
                }

                var playbackAudio = speechAudio;
                speechAudio = null; // Ownership passes to the playback, including failure and stop cleanup.
                PlaySpeechNoLock(playbackAudio, request);
            }
        }
        catch (Exception e)
        {
            lock (SpeechLock)
            {
                // A failed old synthesis must not dispose a newer request's playback.
                if (CurrentSpeechRequest == request)
                {
                    StopSpeechPlaybackNoLock();
                    CompleteSpeechRequestNoLock(request);
                }
            }

            if (!IsSpeechRequestCanceled(request))
            {
                Main.Error(e);
            }
        }
        finally
        {
            SynthesizingSpeech.Value = previous;
            speechAudio?.Dispose();
        }
    }

    private static void CompleteSpeechRequestNoLock(SpeechRequest request)
    {
        if (CurrentSpeechRequest != request)
        {
            return;
        }

        CurrentSpeechRequest = null;
        StartNextSpeechNoLock();
    }

    private static void OnSpeechPlaybackStopped(SpeechPlayback playback, StoppedEventArgs e)
    {
        lock (SpeechLock)
        {
            // A stopped output owns only its own streams, even if a new request is already playing.
            try
            {
                playback.Dispose();
            }
            catch (Exception ex)
            {
                Main.Error(ex);
            }

            if (CurrentPlayback != playback)
            {
                return;
            }

            CurrentPlayback = null;
            if (e.Exception != null)
            {
                Main.Error(e.Exception);
            }

            CompleteSpeechRequestNoLock(playback.Request);
        }
    }

    private static void StopSpeechPlaybackNoLock()
    {
        var playback = CurrentPlayback;
        CurrentPlayback = null;

        if (playback == null)
        {
            return;
        }

        if (playback.Output.PlaybackState == PlaybackState.Stopped)
        {
            playback.Dispose();
        }
        else
        {
            // WaveOutEvent.Stop does not join its playback thread. Keep streams alive until its callback.
            playback.Output.Stop();
        }
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

    private static SpeechScript GetTextScript(string text)
    {
        var counts = new int[5];
        foreach (var c in text ?? string.Empty)
        {
            if (c is >= '\u3040' and <= '\u30ff' or >= '\uff65' and <= '\uff9f')
            {
                counts[1]++;
            }
            else if (c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff')
            {
                counts[2]++;
            }
            else if (c is >= '\u0400' and <= '\u052f')
            {
                counts[3]++;
            }
            else if (c is >= '\uac00' and <= '\ud7af' or >= '\u1100' and <= '\u11ff')
            {
                counts[4]++;
            }
            else if (char.IsLetter(c) && c <= '\u024f')
            {
                counts[0]++;
            }
        }

        // Kana is decisive within Japanese, where most other letters may be shared Han characters.
        if (counts[1] > 0 && counts[1] + counts[2] >= counts[0])
        {
            return SpeechScript.Kana;
        }

        var maximum = counts.Max();
        return maximum == 0 ? SpeechScript.None : (SpeechScript)(1 << Array.IndexOf(counts, maximum));
    }

    private static SpeechLanguageProfile DetectTextLanguageProfile(string cleanedText)
    {
        return DetectSpeechLanguage(cleanedText, null);
    }

    private static SpeechLanguageProfile DetectSpeechLanguage(string text, string preferredVoice)
    {
        var script = GetTextScript(text);
        var ui = GetLanguageProfile(LocalizationManager.CurrentLanguageCode);
        var preferred = VoiceInfos.TryGetValue(preferredVoice ?? string.Empty, out var info)
            ? info.LanguageProfile : null;
        if (script == SpeechScript.None || ui.Scripts == SpeechScript.None || (ui.Scripts & script) != 0)
        {
            return ui;
        }

        if (preferred != null && (preferred.Scripts & script) != 0)
        {
            return preferred;
        }

        return LanguageProfiles.FirstOrDefault(x => x.Scripts == script) ??
               LanguageProfiles.FirstOrDefault(x => (x.Scripts & script) != 0) ?? ui;
    }

    private static SpeechVoiceInfo ResolveVoiceForText(
        string preferredVoice,
        string cleanedText,
        bool allowLanguageFallback,
        Gender? preferredGender,
        string speakerIdentity = null)
    {
        if (preferredVoice == DefaultVoice)
        {
            return null;
        }

        var languageProfile = DetectSpeechLanguage(cleanedText, preferredVoice);
        // A preview deliberately speaks the selected voice's sample, independently of the UI language.
        if (!allowLanguageFallback && VoiceInfos.TryGetValue(preferredVoice, out var previewVoice) &&
            !previewVoice.SupportsLanguage(languageProfile))
        {
            var script = GetTextScript(cleanedText);
            if (script == SpeechScript.None || (previewVoice.LanguageProfile?.Scripts & script) != 0)
            {
                languageProfile = previewVoice.LanguageProfile ?? languageProfile;
            }
        }
        if (VoiceInfos.TryGetValue(preferredVoice, out var preferredVoiceInfo) &&
            preferredVoiceInfo.SupportsLanguage(languageProfile) && IsVoiceAvailable(preferredVoiceInfo))
        {
            return preferredVoiceInfo.ForLanguage(languageProfile);
        }

        if (!allowLanguageFallback && preferredVoice != AutomaticVoice)
        {
            return null;
        }

        var automaticVoice = string.IsNullOrEmpty(speakerIdentity)
            ? GetAutomaticVoice("narrator", languageProfile, preferredGender, Array.Empty<string>())
            : GetAutomaticNpcVoice(speakerIdentity, languageProfile, preferredGender);
        return VoiceInfos.TryGetValue(automaticVoice, out var assignedVoice)
            ? assignedVoice.ForLanguage(languageProfile) : null;
    }

    private static bool IsVoiceAvailable(SpeechVoiceInfo voice)
    {
        return voice.Engine != SpeechEngine.VoiceVox ||
               Main.Settings.EnableVoiceVoxSpeech && VoiceVoxEngineAvailable;
    }

    internal static void SpeakQuote()
    {
        EnsureVoiceDataInitialized();

        var (selectedVoice, _) = Main.Settings.SpeechVoices[Main.Settings.SpeechChoice];

        if (TryGetVoiceLanguageProfile(selectedVoice, out var profile))
        {
            Speak(profile.SampleText, Main.Settings.SpeechChoice, false);
            return;
        }

        if (selectedVoice == AutomaticVoice)
        {
            profile = GetLanguageProfile(LocalizationManager.CurrentLanguageCode);
            Speak(profile.SampleText, Main.Settings.SpeechChoice, false);
            return;
        }

        var quoteNumber = Quoteziner.Next(0, Quotes.Length);
        var subjects = new[] { "Chuck Norris", "Zappa" };
        var subject = subjects[Quoteziner.Next(0, subjects.Length)];
        var quote = Quotes[quoteNumber].Replace("{Subject}", subject);

        Speak(quote, Main.Settings.SpeechChoice, false);
    }

    internal static void Speak(string inputText, GameLocationCharacter character, object owner = null)
    {
        if (character == null)
        {
            return;
        }

        var gameCampaign = Gui.GameCampaign;
        if (!gameCampaign)
        {
            return;
        }
        var index = gameCampaign.Party.CharactersList
            .FindIndex(x => x.RulesetCharacter == character.RulesetCharacter);

        if (index < 0)
        {
            return;
        }

        Speak(inputText, index + 1, owner: owner);
    }

    // heroId zero is the Narrator and 1-6 map to heroes in party
    internal static void Speak(string inputText, int heroId, bool forceUseCampaign = true, object owner = null)
    {
        SpeakActor(inputText, heroId, forceUseCampaign, owner,
            forceUseCampaign ? SpeechSource.Actor : SpeechSource.Preview);
    }

    internal static bool SpeakInteraction(string inputText, object owner)
    {
        if (owner == null || Gui.Game?.CampaignDefinition?.IsUserCampaign != true)
        {
            return false;
        }

        SpeakActor(inputText, 0, true, owner, SpeechSource.Interaction);
        return HasPendingSpeech(owner);
    }

    private static void SpeakActor(string inputText, int heroId, bool forceUseCampaign, object owner,
        SpeechSource source)
    {
        if (string.IsNullOrWhiteSpace(inputText))
        {
            return;
        }

        try
        {
            // only if audio enabled
            var audioSettingsService = ServiceRepository.GetService<IAudioSettingsService>();

            if (audioSettingsService?.MasterEnabled != true)
            {
                return;
            }

            if (!IsSpeechSourceEnabled(source) || heroId < 0 || heroId > MaxHeroes)
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

            EnsureVoiceDataInitialized();
            EnsureCampaignVoiceDataInitialized();

            var (voice, scale) = GetSpeechActorSettings(heroId, forceUseCampaign);

            // Interaction prose includes stage directions and parenthetical descriptions.
            // Remove display markup without removing those words as spoken dialogue does.
            var cleanedText = source == SpeechSource.Interaction
                ? RemoveSpeechNameTags.Replace(inputText, string.Empty)
                : StripXmlTagsAndNarration(inputText);
            var language = DetectSpeechLanguage(cleanedText, voice);
            var hero = GetSpeechHero(heroId);
            var gender = hero == null ? (Gender?)null : GetNpcGender(hero.Sex);
            if (forceUseCampaign || voice == AutomaticVoice)
            {
                voice = GetAutomaticActorVoices(language)[heroId];
            }
            var voiceInfo = ResolveVoiceForText(voice, cleanedText, forceUseCampaign, gender,
                GetSpeechActorIdentity(heroId, hero));

            if (voiceInfo == null)
            {
                return;
            }

            QueueSpeechRequest(voiceInfo, scale, cleanedText, Main.Settings.SpeechVoicePitches[heroId],
                Main.Settings.SpeechVoiceIntonations[heroId], owner, source);
        }
        catch (Exception e)
        {
            Main.Error(e);
        }
    }

    internal static void SpeakNpc(string inputText, GameLocationCharacter character, object owner = null)
    {
        if (character?.RulesetCharacter is not RulesetCharacterMonster monster || monster.MonsterDefinition == null)
        {
            return;
        }

        SpeakNpcWithIdentity(inputText, monster.MonsterDefinition.Name, GetNpcGender(monster.Sex), owner);
    }

    private static void SpeakNpcWithIdentity(string inputText, string internalName, Gender? preferredGender,
        object owner)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(inputText) || string.IsNullOrEmpty(internalName))
            {
                return;
            }

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
                if (Gui.GameCampaign.campaignDefinition?.IsUserCampaign != true)
                {
                    return;
                }
            }

            EnsureVoiceDataInitialized();
            EnsureCampaignVoiceDataInitialized();

            var cleanedText = StripXmlTagsAndNarration(inputText);
            var languageProfile = DetectTextLanguageProfile(cleanedText);
            var settings = ResolveNpcSpeechSettings(internalName, languageProfile, preferredGender);
            var voiceInfo = ResolveVoiceForText(settings.Voice, cleanedText, true, preferredGender, internalName);

            if (voiceInfo == null)
            {
                return;
            }

            QueueSpeechRequest(voiceInfo, settings.Scale, cleanedText, settings.Pitch, settings.Intonation,
                owner, SpeechSource.Npc);
        }
        catch (Exception e)
        {
            Main.Error(e);
        }
    }

    internal static bool SpeakBanter(string inputText, GameLocationCharacter character, object owner)
    {
        if (owner == null || character == null || Gui.Game?.CampaignDefinition?.IsUserCampaign != true)
        {
            return false;
        }

        if (character.RulesetCharacter is RulesetCharacterHero)
        {
            Speak(inputText, character, owner);
        }
        else
        {
            SpeakNpc(inputText, character, owner);
        }

        return HasPendingSpeech(owner);
    }

    internal static bool SpeakBanterConversationLine(string inputText, string speakerName, object owner)
    {
        if (owner == null || string.IsNullOrWhiteSpace(inputText) || string.IsNullOrWhiteSpace(speakerName) ||
            Gui.Game?.CampaignDefinition?.IsUserCampaign != true)
        {
            return false;
        }

        // Decorative conversation actors may have only GraphicsCharacter data. Resolve
        // an unambiguous NPC/hero first so its individual settings and sex are retained.
        // A shared display name must not arbitrarily inherit another NPC's override.
        var speaker = RemoveSpeechNameTags.Replace(speakerName, string.Empty).Trim();
        bool Matches(string name) => !string.IsNullOrWhiteSpace(name) &&
                                     string.Equals(RemoveSpeechNameTags.Replace(name, string.Empty).Trim(), speaker,
                                         StringComparison.OrdinalIgnoreCase);
        var npcs = GetCurrentCampaignNpcs().Where(npc => Matches(npc.InternalName) || Matches(npc.DisplayName))
            .ToArray();
        var npcIdentities = new HashSet<string>(npcs.Select(npc => npc.InternalName), StringComparer.Ordinal);
        var service = ServiceRepository.GetService<IGameLocationCharacterService>();
        var live = (service?.ValidCharacters ?? new List<GameLocationCharacter>()).Where(character =>
            character?.RulesetCharacter is RulesetCharacterHero hero
                ? Matches(hero.Name) || Matches($"{hero.Name} {hero.SurName}")
                : character?.RulesetCharacter is RulesetCharacterMonster monster &&
                  (Matches(monster.MonsterDefinition?.Name) ||
                   npcIdentities.Contains(monster.MonsterDefinition?.Name ?? string.Empty))).ToArray();
        var heroes = live.Where(character => character.RulesetCharacter is RulesetCharacterHero).ToArray();
        foreach (var character in live.Where(character => character.RulesetCharacter is RulesetCharacterMonster))
        {
            npcIdentities.Add(((RulesetCharacterMonster)character.RulesetCharacter).MonsterDefinition.Name);
        }

        // Catalog-only actors are still candidates. A single live NPC must not
        // inherit an ambiguous decorative actor's voice just because it is present.
        var identityCount = npcIdentities.Count + heroes.Length;
        if (identityCount == 1 && live.Length == 1)
        {
            return SpeakBanter(inputText, live[0], owner);
        }

        if (identityCount == 1 && npcIdentities.Count == 1)
        {
            var identity = npcIdentities.Single();
            SpeakNpcWithIdentity(inputText, identity, GetNpcPreviewGender(identity), owner);
        }
        else
        {
            // Matching definitions can establish a common sex even when several NPCs
            // share a name. Do not infer it from the spelling of a character's name.
            var genders = npcIdentities.Select(GetNpcPreviewGender)
                .Concat(heroes.Select(character => GetNpcGender(character.RulesetCharacter.Sex))).Distinct().ToArray();
            SpeakNpcWithIdentity(inputText, "banter:" + speaker, genders.Length == 1 ? genders[0] : null, owner);
        }

        return HasPendingSpeech(owner);
    }

    private static (string Voice, float Scale, float Pitch, float Intonation) ResolveNpcSpeechSettings(
        string internalName, SpeechLanguageProfile languageProfile, Gender? preferredGender)
    {
        var settings = GetNpcSpeechSettings(internalName);
        // A player's explicit per-campaign override wins. ForceMod skips only the campaign author's binding.
        if (settings.Voice != DefaultNpcVoice)
        {
            return settings;
        }
        if (!Main.Settings.ForceModSpeechOnNpcs && CampaignVoices.TryGetValue(internalName, out var campaignVoice))
        {
            return (campaignVoice.Item1, campaignVoice.Item2, 0f, Main.Settings.SpeechVoiceIntonations[0]);
        }
        return (GetAutomaticNpcVoice(internalName, languageProfile, preferredGender),
            Main.Settings.SpeechVoices[0].Item2, GetAutomaticNpcPitch(internalName),
            Main.Settings.SpeechVoiceIntonations[0]);
    }

    private static Gender? GetNpcGender(RuleDefinitions.CreatureSex sex)
    {
        return sex switch
        {
            RuleDefinitions.CreatureSex.Male => Gender.Male,
            RuleDefinitions.CreatureSex.Female => Gender.Female,
            _ => null
        };
    }

    private static Gender? GetNpcPreviewGender(string internalName)
    {
        var service = ServiceRepository.GetService<IGameLocationCharacterService>();
        var live = (service?.ValidCharacters ?? new List<GameLocationCharacter>())
            .Concat(service?.DeadCharacters ?? new List<GameLocationCharacter>())
            .Select(x => x?.RulesetCharacter).OfType<RulesetCharacterMonster>()
            .Where(x => x.MonsterDefinition?.Name == internalName)
            .Select(x => GetNpcGender(x.Sex)).Distinct().ToArray();
        if (live.Length > 0)
        {
            return live.Length == 1 ? live[0] : null;
        }

        var npc = Gui.Session?.UserCampaign?.UserNpcs?.FirstOrDefault(x => x?.InternalName == internalName);
        var definition = npc?.ReferenceMonsterDefinition;
        if (definition == null && !string.IsNullOrEmpty(npc?.ReferenceDefinition))
        {
            DatabaseRepository.GetDatabase<MonsterDefinition>().TryGetElement(npc.ReferenceDefinition, out definition);
        }

        return GetNpcDefinitionGender(definition);
    }

    private static Gender? GetNpcDefinitionGender(MonsterDefinition definition)
    {
        var presentation = definition?.MonsterPresentation;
        var genders = (presentation?.HumanoidMonsterPresentationDefinitions ??
                       Array.Empty<HumanoidMonsterPresentationDefinition>())
            .Where(x => x != null).Select(x => GetNpcGender(x.Sex)).Distinct().ToArray();
        if (genders.Length == 0 && presentation?.HasPrefabVariants == true)
        {
            genders = (presentation.MonsterPresentationDefinitions ?? Array.Empty<MonsterPresentationDefinition>())
                .Where(x => x != null).Select(x => GetNpcGender(x.Sex)).Distinct().ToArray();
        }

        // Several presentations may share one campaign NPC ID. A preview must not select the
        // first presentation's sex when spawning can select another one.
        return genders.Length == 1 ? genders[0] : null;
    }

    internal static void SpeakNpcQuote(string internalName)
    {
        if (!Main.Settings.EnableSpeechOnNpcs ||
            ServiceRepository.GetService<IAudioSettingsService>()?.MasterEnabled != true ||
            !GetCurrentCampaignNpcs().Any(x => x.InternalName == internalName))
        {
            return;
        }
        EnsureVoiceDataInitialized();
        EnsureCampaignVoiceDataInitialized();
        var profile = GetLanguageProfile(LocalizationManager.CurrentLanguageCode);
        var preferredGender = GetNpcPreviewGender(internalName);
        var settings = ResolveNpcSpeechSettings(internalName, profile, preferredGender);
        if (TryGetVoiceLanguageProfile(settings.Voice, out var voiceProfile))
        {
            profile = voiceProfile;
        }
        var text = profile?.SampleText ?? "Hello. Let us begin our adventure.";
        var voiceInfo = ResolveVoiceForText(settings.Voice, text, false, preferredGender, internalName);
        QueueSpeechRequest(voiceInfo, settings.Scale, text, settings.Pitch, settings.Intonation, null,
            SpeechSource.Preview);
    }

    private static string GetAutomaticNpcVoice(
        string internalName, SpeechLanguageProfile languageProfile, Gender? preferredGender)
    {
        languageProfile ??= GetLanguageProfile(LocalizationManager.CurrentLanguageCode);
        var actorVoices = GetAutomaticActorVoices(languageProfile);
        var language = languageProfile.LanguageCode;
        // A preview without resolved sex must not determine the later live character's casting.
        var cacheKey = language + ":" + (preferredGender?.ToString() ?? "Unknown") + ":" + internalName;
        if (AutomaticNpcVoices.TryGetValue(cacheKey, out var assigned) &&
            VoiceInfos.TryGetValue(assigned, out var cached) && cached.SupportsLanguage(languageProfile) &&
            cached.AutomaticVoice && IsVoiceAvailable(cached) &&
            (preferredGender == null || GetVoiceGender(cached) == preferredGender || GetVoiceGender(cached) == null))
        {
            return assigned;
        }

        assigned = GetAutomaticVoice(internalName, languageProfile, preferredGender, actorVoices.Values);
        AutomaticNpcVoices[cacheKey] = assigned;
        return assigned;
    }

    private static string GetAutomaticVoice(string identity, SpeechLanguageProfile languageProfile,
        Gender? preferredGender, IEnumerable<string> reservedVoices)
    {
        var candidates = VoiceInfos.Values.Where(x => x.SupportsLanguage(languageProfile) && x.AutomaticVoice &&
                                                      IsVoiceAvailable(x))
            .ToArray();
        if (preferredGender != null)
        {
            var matchingGender = candidates.Where(x => GetVoiceGender(x) == preferredGender).ToArray();
            candidates = matchingGender.Length > 0
                ? matchingGender
                : candidates.Where(x => GetVoiceGender(x) == null).ToArray();
        }

        if (candidates.Length == 0)
        {
            return DefaultVoice;
        }

        var reserved = new HashSet<string>(reservedVoices.Where(VoiceInfos.ContainsKey)
            .Select(x => VoiceInfos[x].PersonaKey), StringComparer.Ordinal);
        var unused = candidates.Where(x => !reserved.Contains(x.PersonaKey)).ToArray();
        if (unused.Length > 0)
        {
            candidates = unused;
        }

        // Prefer the configured provider among unused personas. Exhausting its voices
        // must not collapse distinct actors while other compatible personas exist.
        var priority = candidates.Max(x => x.AutomaticPriority);
        candidates = candidates.Where(x => x.AutomaticPriority == priority).ToArray();

        var ordered = candidates.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        // A modest preference keeps adult voices common without discarding the other matching
        // speakers. Character age ranges and portrait morphotype sliders are not speaking ages.
        var slot = GetStableSpeakerHash(identity) % (uint)ordered.Sum(x => x.AutomaticWeight);
        foreach (var voice in ordered)
        {
            var weight = (uint)voice.AutomaticWeight;
            if (slot < weight)
            {
                return voice.Name;
            }

            slot -= weight;
        }

        return ordered[0].Name;
    }

    private static RulesetCharacterHero GetSpeechHero(int heroId)
    {
        var campaign = Gui.GameCampaign;
        var party = campaign ? campaign.Party?.CharactersList : null;
        return heroId > 0 && party != null && heroId <= party.Count
            ? party[heroId - 1]?.RulesetCharacter as RulesetCharacterHero : null;
    }

    private static string GetSpeechActorIdentity(int heroId, RulesetCharacterHero hero)
    {
        return heroId == 0 ? "narrator" : hero == null ? "hero:" + heroId :
            "hero:" + (hero.Guid == 0 ? hero.Name : hero.Guid.ToString(CultureInfo.InvariantCulture));
    }

    private static string GetSpeechActorCastingIdentity(int heroId, RulesetCharacterHero hero)
    {
        // VoiceID is the saved identity selected by character creation's Voice 1-3
        // controls. Keep this provider-independent; custom native voice definitions
        // participate without an English/Japanese-specific mapping table.
        return hero != null && !string.IsNullOrWhiteSpace(hero.VoiceID)
            ? "hero-voice:" + hero.VoiceID
            : GetSpeechActorIdentity(heroId, hero);
    }

    private static (string Voice, float Scale) GetSpeechActorSettings(int heroId, bool useCampaign)
    {
        var settings = Main.Settings.SpeechVoices[heroId];
        return useCampaign && heroId == 0 && settings.Item1 == AutomaticVoice &&
               CampaignVoices.TryGetValue(NarratorVoiceKey, out var campaignVoice)
            ? campaignVoice : settings;
    }

    private static Dictionary<int, string> GetAutomaticActorVoices(SpeechLanguageProfile language)
    {
        EnsureSpeechVoiceSettings();
        var actors = Enumerable.Range(0, MaxHeroes + 1).Select(i =>
        {
            var hero = GetSpeechHero(i);
            return (Slot: i, Identity: GetSpeechActorIdentity(i, hero),
                CastingIdentity: GetSpeechActorCastingIdentity(i, hero),
                Gender: hero == null ? (Gender?)null : GetNpcGender(hero.Sex),
                Voice: GetSpeechActorSettings(i, true).Voice);
        }).ToArray();
        var signatureBuilder = new StringBuilder(language.LanguageCode).Append(':').Append(VoiceCatalogRevision);
        foreach (var actor in actors)
        {
            signatureBuilder.Append(':').Append(actor.Identity.Length).Append(':').Append(actor.Identity)
                .Append(':').Append(actor.CastingIdentity.Length).Append(':').Append(actor.CastingIdentity)
                .Append(':').Append(actor.Gender?.ToString()).Append(':').Append(actor.Voice.Length)
                .Append(':').Append(actor.Voice);
        }
        var signature = signatureBuilder.ToString();
        if (signature == AutomaticActorSignature)
        {
            return AutomaticActorVoices;
        }

        bool IsCompatibleVoice(string voice) => VoiceInfos.TryGetValue(voice, out var info) &&
                                                 info.SupportsLanguage(language) && IsVoiceAvailable(info);
        var result = actors.ToDictionary(x => x.Slot, x => x.Voice);
        var reserved = actors.Where(x => IsCompatibleVoice(x.Voice)).Select(x => x.Voice).ToList();
        var nativeVoiceGroups = new Dictionary<(string Identity, Gender? Gender), string>();
        // Native voice identities preserve character-creation choices and casting
        // across party reordering. Heroes with no native voice retain the GUID fallback.
        foreach (var actor in actors.Where(x => x.Voice != DefaultVoice && !IsCompatibleVoice(x.Voice))
                     .OrderBy(x => x.Slot == 0 ? 0 : 1).ThenBy(x => x.CastingIdentity, StringComparer.Ordinal)
                     .ThenBy(x => x.Identity, StringComparer.Ordinal))
        {
            var group = (actor.CastingIdentity, actor.Gender);
            if (!nativeVoiceGroups.TryGetValue(group, out var voice))
            {
                voice = GetAutomaticVoice(actor.CastingIdentity, language, actor.Gender, reserved);
                nativeVoiceGroups.Add(group, voice);
                reserved.Add(voice);
            }
            result[actor.Slot] = voice;
        }

        AutomaticActorSignature = signature;
        AutomaticActorVoices = result;
        AutomaticNpcVoices.Clear();
        return result;
    }

    internal static string GetEffectiveSpeechVoice(int heroId)
    {
        EnsureVoiceDataInitialized();
        if (heroId is < 0 or > MaxHeroes)
        {
            return DefaultVoice;
        }

        EnsureCampaignVoiceDataInitialized();
        return GetAutomaticActorVoices(GetLanguageProfile(LocalizationManager.CurrentLanguageCode))[heroId];
    }

    private static uint GetStableSpeakerHash(string internalName)
    {
        // String.GetHashCode is not stable across runtimes; saved campaign identities must be.
        var hash = 2166136261u;
        foreach (var c in internalName)
        {
            hash = unchecked((hash ^ c) * 16777619u);
        }

        return hash;
    }

    private static float GetAutomaticNpcPitch(string internalName)
    {
        return (int)((GetStableSpeakerHash(internalName) >> 8) % 5) - 2;
    }

    private static void PlaySpeechNoLock(SpeechAudio speechAudio, SpeechRequest request)
    {
        StopSpeechPlaybackNoLock();
        CurrentPlayback = new SpeechPlayback(speechAudio, request);
        CurrentPlayback.Output.Play();
    }

    private static ISampleProvider ApplySpeechPitch(ISampleProvider samples, float pitch)
    {
        if (float.IsNaN(pitch) || Math.Abs(pitch) <= 0.001f)
        {
            return samples;
        }

        const int fftSize = 4096;
        // Flush both the input FIFO and overlapping output windows after the source ends.
        // A single FFT frame still cuts off pitch-shifted audio at the end of an utterance.
        var paddedSamples = new OffsetSampleProvider(samples)
        {
            LeadOutSamples = 2 * fftSize * samples.WaveFormat.Channels
        };

        return new SmbPitchShiftingSampleProvider(paddedSamples, fftSize, 4L,
            (float)Math.Pow(2, pitch / 12));
    }

    private static Task<SpeechAudio> SynthesizeSpeechAsync(
        SpeechVoiceInfo voiceInfo,
        float scale,
        string cleanedText,
        int? generation = null)
    {
        return SynthesizeSpeechRequestAsync(voiceInfo, scale, cleanedText, generation, 0f, 1f);
    }

    private static Task<SpeechAudio> SynthesizeSpeechRequestAsync(
        SpeechVoiceInfo voiceInfo, float scale, string cleanedText, int? generation, float pitch, float intonation)
    {
        return SynthesizeSpeechRequestForSessionAsync(voiceInfo, scale, cleanedText, generation, pitch,
            intonation, VoiceVoxSessionVersion);
    }

    private static Task<SpeechAudio> SynthesizeSpeechRequestForSessionAsync(
        SpeechVoiceInfo voiceInfo, float scale, string cleanedText, int? generation, float pitch, float intonation,
        int voiceVoxSession)
    {
        return voiceInfo.Engine switch
        {
            SpeechEngine.VoiceVox => SynthesizeVoiceVoxSpeechAsync(voiceInfo, scale, cleanedText, generation,
                pitch, intonation, voiceVoxSession),
            SpeechEngine.PiperPlus => SynthesizePiperPlusSpeechAsync(voiceInfo, scale, cleanedText, generation),
            SpeechEngine.WindowsSapi => SynthesizeWindowsSpeechAsync(voiceInfo, scale, cleanedText, generation),
            _ => SynthesizeLegacyPiperSpeechAsync(voiceInfo, scale, cleanedText, generation)
        };
    }

    private static async Task<SpeechAudio> SynthesizeLegacyPiperSpeechAsync(
        SpeechVoiceInfo voiceInfo,
        float scale,
        string cleanedText,
        int? generation)
    {
        if (IsSpeechGenerationCanceled(generation) || !TryGetLegacyPiperExecutablePath(out var executablePath))
        {
            return null;
        }

        var modelFileName = voiceInfo.ModelArgument;

        if (!File.Exists(modelFileName))
        {
            return null;
        }

        using var piper = CreateProcess(
            executablePath,
            $"--model {QuoteArgument(modelFileName)} --config {QuoteArgument(voiceInfo.ConfigArgument)} " +
            $"--length_scale {FormatScale(scale)} --speaker {voiceInfo.SpeakerId} --output-raw",
            true);

        var audioStream = await RunSpeechProcessAsync(piper, cleanedText, generation);
        if (audioStream == null)
        {
            return null;
        }
        if (audioStream.Length == 0)
        {
            audioStream.Dispose();
            return null;
        }

        return new SpeechAudio(audioStream, false, voiceInfo.SampleRate);
    }

    private static async Task<SpeechAudio> SynthesizeVoiceVoxSpeechAsync(SpeechVoiceInfo voiceInfo,
        float scale, string cleanedText, int? generation, float pitch, float intonation, int session)
    {
        if (IsVoiceVoxSessionCanceled(session, generation))
        {
            return null;
        }

        var style = voiceInfo.SpeakerId.ToString(CultureInfo.InvariantCulture);
        var queryBytes = await SendVoiceVoxRequestAsync(
            "audio_query?speaker=" + style + "&text=" + Uri.EscapeDataString(cleanedText),
            Array.Empty<byte>(), generation, 30000, session);
        if (IsVoiceVoxSessionCanceled(session, generation))
        {
            return null;
        }

        // Preserve the full engine query, including accent phrases and fields from newer engine versions.
        var query = JObject.Parse(Encoding.UTF8.GetString(queryBytes));
        query["speedScale"] = 1f / ClampSpeechValue(scale, 0.5f, 2f, DefaultScale);
        query["pitchScale"] = ClampSpeechValue(pitch, -6f, 6f, 0f) / 12f;
        query["intonationScale"] = ClampSpeechValue(intonation, 0f, 2f, 1f);
        var sampleRate = query["outputSamplingRate"]?.Value<int>() ?? voiceInfo.SampleRate;
        if (sampleRate is < 8000 or > 96000)
        {
            throw new InvalidDataException("VOICEVOX returned an invalid audio sampling rate.");
        }
        var wave = await SendVoiceVoxRequestAsync("synthesis?speaker=" + style +
            "&enable_interrogative_upspeak=true", Encoding.UTF8.GetBytes(query.ToString(Formatting.None)),
            generation, 60000, session);
        if (IsVoiceVoxSessionCanceled(session, generation))
        {
            return null;
        }
        return new SpeechAudio(new MemoryStream(wave), true, sampleRate);
    }

    private static async Task<byte[]> SendVoiceVoxRequestAsync(string path, byte[] body, int? generation,
        int timeoutMilliseconds, int? session = null)
    {
        if (!Main.Settings.EnableVoiceVoxSpeech || IsSpeechGenerationCanceled(generation) ||
            session.HasValue && session.Value != VoiceVoxSessionVersion)
        {
            throw new OperationCanceledException();
        }

        // A fixed literal loopback address, no proxy and no redirects keep campaign text on this computer.
        var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + VoiceVoxPort + "/" + path);
        request.Proxy = null;
        request.AllowAutoRedirect = false;
        request.Method = body == null ? "GET" : "POST";
        request.Timeout = timeoutMilliseconds;
        request.ReadWriteTimeout = timeoutMilliseconds;
        if (body != null)
        {
            request.ContentType = "application/json";
            request.ContentLength = body.Length;
        }
        lock (SpeechProcessLock)
        {
            if (Unloading || !Main.Settings.EnableVoiceVoxSpeech || IsSpeechGenerationCanceled(generation) ||
                session.HasValue && session.Value != VoiceVoxSessionVersion)
            {
                throw new OperationCanceledException();
            }
            SpeechHttpRequests.Add(request, generation);
        }

        var task = SendVoiceVoxRequestCoreAsync(request, body);
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (IsSpeechGenerationCanceled(generation) ||
                session.HasValue && session.Value != VoiceVoxSessionVersion)
            {
                request.Abort();
                _ = ObserveSpeechHttpTaskAsync(task);
                throw new OperationCanceledException();
            }

            if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                request.Abort();
                _ = ObserveSpeechHttpTaskAsync(task);
                throw new TimeoutException("The local VOICEVOX engine did not respond in time.");
            }

            await Task.WhenAny(task, Task.Delay(50)).ConfigureAwait(false);
        }
        return await task.ConfigureAwait(false);
    }

    private static async Task ObserveSpeechHttpTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A timed-out request owns its eventual cleanup; observe its aborted task's exception.
        }
    }

    private static async Task<byte[]> SendVoiceVoxRequestCoreAsync(HttpWebRequest request, byte[] body)
    {
        try
        {
            if (body != null && body.Length > 0)
            {
                using var input = await request.GetRequestStreamAsync().ConfigureAwait(false);
                await input.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            }
            using var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidDataException("The local VOICEVOX engine returned an unsuccessful response.");
            }
            var limit = request.RequestUri.AbsolutePath == "/synthesis" ? 32 * 1024 * 1024 : 1024 * 1024;
            if (response.ContentLength > limit)
            {
                throw new InvalidDataException("The local VOICEVOX response exceeds its allowed size.");
            }
            using var output = new MemoryStream();
            using var stream = response.GetResponseStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > limit)
                {
                    throw new InvalidDataException("The local VOICEVOX response exceeds its allowed size.");
                }
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally
        {
            lock (SpeechProcessLock)
            {
                SpeechHttpRequests.Remove(request);
            }
        }
    }

    private static void AbortSpeechHttpRequests(bool refreshOnly = false, bool all = false)
    {
        HttpWebRequest[] requests;
        lock (SpeechProcessLock)
        {
            requests = SpeechHttpRequests.Where(x => all || (refreshOnly
                    ? !x.Value.HasValue
                    : x.Value.HasValue && x.Value.Value != SpeechGeneration))
                .Select(x => x.Key).ToArray();
        }
        foreach (var request in requests)
        {
            request.Abort();
        }
    }

    private static bool IsVoiceVoxSessionCanceled(int session, int? generation)
    {
        return session != VoiceVoxSessionVersion || !Main.Settings.EnableVoiceVoxSpeech ||
               IsSpeechGenerationCanceled(generation);
    }

    private static bool IsSpeechRequestCanceled(SpeechRequest request)
    {
        return request.Canceled || Unloading || request.Generation != SpeechGeneration ||
               !IsSpeechSourceEnabled(request.Source) ||
               request.VoiceInfo.Engine == SpeechEngine.VoiceVox &&
               (!Main.Settings.EnableVoiceVoxSpeech || request.VoiceVoxSession != VoiceVoxSessionVersion);
    }

    private static void CancelVoiceVoxSpeech()
    {
        AbortSpeechHttpRequests(all: true);
        lock (SpeechLock)
        {
            CancelSpeechRequestsNoLock(request => request.VoiceInfo.Engine == SpeechEngine.VoiceVox);
        }
    }

    private static async Task<SpeechAudio> SynthesizePiperPlusSpeechAsync(
        SpeechVoiceInfo voiceInfo,
        float scale,
        string cleanedText,
        int? generation)
    {
        if (IsSpeechGenerationCanceled(generation) || !TryGetPiperPlusExecutablePath(out var executablePath))
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
                $"--json-input --model {QuoteArgument(voiceInfo.ModelArgument)} " +
                $"--config {QuoteArgument(voiceInfo.ConfigArgument)} --model-dir {QuoteArgument(PiperPlusModelsFolder)} " +
                $"--language {QuoteArgument(cliLanguage)} --length-scale {FormatScale(scale)} " +
                $"--noise-scale {FormatScale(voiceInfo.NoiseScale)} --quiet",
                true);

            piper.StartInfo.EnvironmentVariables["PIPER_MODEL_DIR"] = PiperPlusModelsFolder;

            var payload = JsonConvert.SerializeObject(new
            {
                text = cleanedText,
                speaker_id = voiceInfo.SpeakerId,
                output_file = tempWav
            });
            using var output = await RunSpeechProcessAsync(piper, payload + Environment.NewLine, generation);
            if (output == null || IsSpeechGenerationCanceled(generation) || !File.Exists(tempWav))
            {
                return null;
            }

            var wavBytes = File.ReadAllBytes(tempWav);
            return wavBytes.Length == 0 ? null : new SpeechAudio(new MemoryStream(wavBytes), true, voiceInfo.SampleRate);
        }
        finally
        {
            TryDeleteFile(tempFile);
            TryDeleteFile(tempWav);
        }
    }

    private static async Task<SpeechAudio> SynthesizeWindowsSpeechAsync(
        SpeechVoiceInfo voiceInfo, float scale, string cleanedText, int? generation)
    {
        if (IsSpeechGenerationCanceled(generation))
        {
            return null;
        }

        using var process = CreateWindowsSpeechProcess();
        if (process == null)
        {
            return null;
        }

        try
        {
            var payload = JsonConvert.SerializeObject(new
            {
                operation = "synthesize",
                voice = voiceInfo.ModelArgument,
                rate = Math.Min(10, Math.Max(-10, (int)Math.Round(-Math.Log(scale, 2) * 5))),
                text = cleanedText
            });
            var audioStream = await RunSpeechProcessAsync(process, payload, generation);
            if (audioStream == null)
            {
                return null;
            }
            if (audioStream.Length == 0)
            {
                audioStream.Dispose();
                return null;
            }

            return new SpeechAudio(audioStream, false, voiceInfo.SampleRate);
        }
        catch (Exception ex)
        {
            if (!IsSpeechGenerationCanceled(generation))
            {
                Main.Info($"Cannot synthesize Windows speech: {ex.Message}");
            }

            return null;
        }
    }

    private static Process CreateWindowsSpeechProcess()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable))
        {
            return null;
        }

        var script = Convert.ToBase64String(Encoding.Unicode.GetBytes(WindowsSpeechScript));
        var process = CreateProcess(executable,
            "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + script, true);
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        return process;
    }

    private static bool IsSpeechGenerationCanceled(int? generation)
    {
        return Unloading || generation.HasValue &&
            (generation.Value != SpeechGeneration || SynthesizingSpeech.Value?.Canceled == true);
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

    private static async Task<MemoryStream> RunSpeechProcessAsync(Process process, string inputText, int? generation)
    {
        if (!TryStartAndRegisterSpeechProcess(process, generation))
        {
            return null;
        }

        Task<MemoryStream> outputTask = null;
        Task<string> errorTask = null;
        Task<bool> exitTask = null;
        var retainOutput = false;

        try
        {
            outputTask = CopyToMemoryStreamAsync(process.StandardOutput.BaseStream);
            errorTask = process.StandardError.ReadToEndAsync();
            // Watch cancellation before writing stdin: a stalled engine may not read a long utterance.
            exitTask = WaitForSpeechProcessExitAsync(process, generation);
            await WriteUtf8ToStandardInputAsync(process, inputText);
            var completed = await exitTask;
            var output = await outputTask;
            var error = await errorTask;

            if (!completed || IsSpeechGenerationCanceled(generation) || process.ExitCode != 0)
            {
                if (completed && !IsSpeechGenerationCanceled(generation) && !string.IsNullOrWhiteSpace(error))
                {
                    Main.Info($"Cannot synthesize speech with {Path.GetFileName(process.StartInfo.FileName)}: {error.Trim()}");
                }

                return null;
            }

            output.Position = 0;
            retainOutput = true;
            return output;
        }
        catch (Exception) when (IsSpeechGenerationCanceled(generation))
        {
            return null;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch (Exception)
            {
                // Unload may have already stopped and disposed this owned process.
            }

            // Each request owns its output. Observe and dispose it even if stdin writing was interrupted.
            if (exitTask != null)
            {
                try
                {
                    await exitTask;
                }
                catch (Exception)
                {
                    // The original process error or cancellation has already been handled above.
                }
            }
            if (!retainOutput && outputTask != null)
            {
                try
                {
                    (await outputTask).Dispose();
                }
                catch (Exception)
                {
                    // Failed output reads own their stream cleanup.
                }
            }
            if (errorTask != null)
            {
                try
                {
                    await errorTask;
                }
                catch (Exception)
                {
                    // Observe aborted stderr reads as part of the same request's cleanup.
                }
            }

            UnregisterSpeechProcess(process);
        }
    }

    private static Task<bool> WaitForSpeechProcessExitAsync(Process process, int? generation)
    {
        return Task.Run(() =>
        {
            try
            {
                while (!process.WaitForExit(50))
                {
                    if (!IsSpeechGenerationCanceled(generation))
                    {
                        continue;
                    }

                    process.Kill();
                    process.WaitForExit();
                    return false;
                }

                return !IsSpeechGenerationCanceled(generation);
            }
            catch (InvalidOperationException) when (IsSpeechGenerationCanceled(generation))
            {
                // Unload may dispose the process while its request is waiting for it to exit.
                return false;
            }
        });
    }

    private static bool TryStartAndRegisterSpeechProcess(Process process, int? generation = null)
    {
        lock (SpeechProcessLock)
        {
            if (IsSpeechGenerationCanceled(generation) || !process.Start())
            {
                return false;
            }

            SpeechProcesses.Add(process);

            return true;
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
                    process.WaitForExit(1000);
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
        try
        {
            var buffer = new byte[16384];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                audioStream.Write(buffer, 0, bytesRead);
            }

            return audioStream;
        }
        catch
        {
            audioStream.Dispose();
            throw;
        }
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

        var modelFolder = Path.Combine(PiperPlusModelsFolder, profile.ModelArgument);
        Directory.CreateDirectory(modelFolder);

        using var piper = CreateProcess(
            executablePath,
            $"--download-model {profile.ModelArgument} --model-dir {QuoteArgument(modelFolder)} --quiet",
            false);

        piper.StartInfo.EnvironmentVariables["PIPER_MODEL_DIR"] = modelFolder;

        try
        {
            if (!TryStartAndRegisterSpeechProcess(piper))
            {
                return false;
            }

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

            if (_shared._coroutine != null)
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

            if (_shared._coroutine != null)
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
            EnsurePiperInitialized();
            if (!TryGetLegacyPiperExecutablePath(out _))
            {
                _coroutine = null;
                _progress = 0f;
                yield break;
            }

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

            try
            {
                // A failed configuration download must be repairable without deleting its completed model.
                // Publish voices only when RefreshAvailableVoices can validate the complete pair.
                var downloaded = DownloadVoiceFile(wc, $"{voice}.onnx");
                downloaded |= DownloadVoiceFile(wc, $"{voice}.onnx.json");
                Main.Info(downloaded
                    ? $"Voice {voice} successfully downloaded."
                    : $"Voice {voice} already exists.");
            }
            catch
            {
                Main.Info($"Cannot download voice {voice}.");
            }
        }

        private static bool DownloadVoiceFile(WebClient client, string url)
        {
            var filename = Path.Combine(VoicesFolder, Path.GetFileName(url));
            if (File.Exists(filename))
            {
                return false;
            }

            var temporaryFilename = filename + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                client.DownloadFile(url + "?download=true", temporaryFilename);
                File.Move(temporaryFilename, filename);
                return true;
            }
            finally
            {
                TryDeleteFile(temporaryFilename);
            }
        }
    }
}
