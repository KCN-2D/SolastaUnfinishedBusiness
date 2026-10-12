using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Photon.Pun;
using Photon.Realtime;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace SolastaUnfinishedBusiness.Models;

internal static class MultiplayerSettingsContext
{
    internal const string HostProperty = "UB.Settings.v1";
    internal const string ClientProperty = "UB.Settings.Client.v1";
    private const int MaxCompressedBytes = 64 * 1024;
    internal const int MaxXmlBytes = 1024 * 1024;
    private const int MaxBase64Characters = (MaxCompressedBytes + 2) / 3 * 4;
    private const int MaxEnvelopeCharacters = MaxBase64Characters + 256;
    private const double RefreshSeconds = 2;
    private const double ReportWaitSeconds = 10;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Dictionary<string, PropertyInfo> PropertiesByElement = typeof(Settings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0 &&
                           !property.IsDefined(typeof(XmlIgnoreAttribute), true))
        .ToDictionary(GetElementName, StringComparer.Ordinal);

    private static double nextRefresh;
    private static double roomEnteredAt;
    private static string roomName;
    private static int masterActor;
    private static string buildDigest;
    private static string localProfile;
    private static string localProfileDigest;
    private static string localEnvelopeReport;
    private static string localHostEnvelope;
    private static string hostEnvelope;
    private static string hostProfile;
    private static string hostBuildDigest;
    private static string hostProfileDigest;
    private static HostState hostState;
    private static bool refreshing;
    private static bool comparisonError;
    private static string lastError;

    internal static bool IsOnlineRoom => Main.Enabled && !Main.IsApplicationQuitting &&
                                         PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode &&
                                         PhotonNetwork.CurrentRoom != null;

    internal static bool HasHostProfile => IsOnlineRoom && !PhotonNetwork.IsMasterClient &&
                                          hostState == HostState.Valid && !comparisonError &&
                                          !string.IsNullOrEmpty(hostProfile) &&
                                          ServiceRepository.GetService<INetworkingService>()?.SessionStatus ==
                                          NetworkingDefinitions.MultiplayerSessionStatus.Lobby;

    internal static void Invalidate()
    {
        nextRefresh = 0;
        roomName = null;
        masterActor = 0;
        hostEnvelope = null;
        ClearHostProfile(HostState.Waiting);
        localEnvelopeReport = null;
        localHostEnvelope = null;
        comparisonError = false;
        lastError = null;
    }

    internal static void Refresh(bool force = false)
    {
        if (refreshing || !force && Clock.Elapsed.TotalSeconds < nextRefresh)
        {
            return;
        }

        nextRefresh = Clock.Elapsed.TotalSeconds + RefreshSeconds;

        if (!IsOnlineRoom)
        {
            if (roomName != null)
            {
                Invalidate();
            }

            return;
        }

        refreshing = true;

        try
        {
            var room = PhotonNetwork.CurrentRoom;

            if (roomName != room.Name || masterActor != room.MasterClientId)
            {
                Invalidate();
                roomName = room.Name;
                masterActor = room.MasterClientId;
                roomEnteredAt = Clock.Elapsed.TotalSeconds;
                nextRefresh = Clock.Elapsed.TotalSeconds + RefreshSeconds;
            }

            buildDigest ??= ComputeBuildDigest();
            localProfile = CreateSharedSettingsXml(Main.Settings);
            localProfileDigest = ComputeDigest(Utf8.GetBytes(localProfile));

            var report = string.Join("|", "1", masterActor.ToString(CultureInfo.InvariantCulture),
                buildDigest, localProfileDigest);
            var localPlayer = PhotonNetwork.LocalPlayer;

            if (!PropertyEquals(localPlayer.CustomProperties, ClientProperty, report))
            {
                localPlayer.SetCustomProperties(new Hashtable { [ClientProperty] = report });
            }

            if (PhotonNetwork.IsMasterClient)
            {
                if (localEnvelopeReport != report)
                {
                    localHostEnvelope = string.Join("|", report, Compress(localProfile));
                    localEnvelopeReport = report;
                }

                if (!PropertyEquals(room.CustomProperties, HostProperty, localHostEnvelope))
                {
                    room.SetCustomProperties(new Hashtable { [HostProperty] = localHostEnvelope });
                }

                hostProfile = localProfile;
                hostBuildDigest = buildDigest;
                hostProfileDigest = localProfileDigest;
                hostState = HostState.Valid;
            }
            else
            {
                ReadHostProfile(room);
            }

            comparisonError = false;
            lastError = null;
        }
        catch (Exception ex)
        {
            comparisonError = true;

            if (lastError != ex.Message)
            {
                Main.Error(ex);
                lastError = ex.Message;
            }
        }
        finally
        {
            refreshing = false;
        }
    }

    internal static void OnRoomPropertiesChanged(Hashtable properties)
    {
        if (properties?.ContainsKey(HostProperty) == true)
        {
            Refresh(true);
        }
    }

    internal static void OnPlayerPropertiesChanged(Hashtable properties)
    {
        if (properties?.ContainsKey(ClientProperty) == true)
        {
            Refresh(true);
        }
    }

    internal static string GetStatus()
    {
        Refresh();

        if (!IsOnlineRoom)
        {
            return Localize("Offline");
        }

        if (comparisonError)
        {
            return Localize("Error");
        }

        if (!PhotonNetwork.IsMasterClient)
        {
            return Localize(hostState switch
            {
                HostState.Unsupported => "Unsupported",
                HostState.Invalid => "Invalid",
                HostState.VersionMismatch => "VersionMismatch",
                HostState.Valid when localProfileDigest == hostProfileDigest => "Match",
                HostState.Valid => "Mismatch",
                _ => "Waiting"
            });
        }

        var lines = new List<string> { Localize("Host") };

        foreach (var player in PhotonNetwork.CurrentRoom.Players.Values
                     .Where(player => !player.IsLocal && !player.IsInactive)
                     .OrderBy(player => player.ActorNumber))
        {
            var status = "PeerWaiting";

            if (TryReadReport(player.CustomProperties, out var peerBuild, out var peerProfile))
            {
                status = peerBuild != buildDigest ? "PeerVersionMismatch" :
                    peerProfile != localProfileDigest ? "PeerMismatch" : "PeerMatch";
            }

            // Player names come from the network and must remain plain text in rich labels.
            var name = (player.NickName ?? player.ActorNumber.ToString(CultureInfo.InvariantCulture))
                .Replace('<', '＜').Replace('>', '＞').Replace('\r', ' ').Replace('\n', ' ');
            lines.Add(string.Format(CultureInfo.CurrentCulture, Localize(status), name));
        }

        return string.Join("\n", lines);
    }

    internal static bool TrySaveHostSettings()
    {
        Refresh(true);

        if (!HasHostProfile)
        {
            return false;
        }

        return Main.SaveSharedSettingsProfile(hostProfile);
    }

    internal static string CreateSharedSettingsXml(Settings settings)
    {
        if (!HasValidValues(settings))
        {
            throw new InvalidDataException("Invalid shared UB settings values.");
        }

        var serializer = new XmlSerializer(typeof(Settings));
        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(string.Empty, string.Empty);
        var builder = new StringBuilder();

        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            serializer.Serialize(writer, settings, namespaces);
        }

        var document = ReadDocument(builder.ToString());
        NormalizeDocument(document);
        var xml = document.DocumentElement?.OuterXml ?? throw new InvalidDataException("Missing settings root.");

        if (Utf8.GetByteCount(xml) > MaxXmlBytes)
        {
            throw new InvalidDataException("Shared UB settings exceed the size limit.");
        }

        return xml;
    }

    internal static bool TryValidateSharedSettingsXml(string xml, out string normalizedXml)
    {
        normalizedXml = null;

        try
        {
            if (string.IsNullOrEmpty(xml) || Utf8.GetByteCount(xml) > MaxXmlBytes)
            {
                return false;
            }

            var document = ReadDocument(xml);
            var root = document.DocumentElement;

            if (root is not { Name: "Settings", NamespaceURI.Length: 0 })
            {
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var element in root.ChildNodes.OfType<XmlElement>().ToArray())
            {
                if (element.NamespaceURI.Length != 0 || !PropertiesByElement.TryGetValue(element.Name, out var property))
                {
                    return false;
                }

                if (property.IsDefined(typeof(MultiplayerLocalAttribute), true))
                {
                    root.RemoveChild(element);
                    continue;
                }

                if (!names.Add(element.Name))
                {
                    return false;
                }
            }

            NormalizeDocument(document);
            var expected = root.OuterXml;
            var serializer = new XmlSerializer(typeof(Settings));
            var events = new XmlDeserializationEvents
            {
                OnUnknownElement = (_, _) => throw new InvalidDataException("Unknown shared settings element."),
                OnUnknownAttribute = (_, _) => throw new InvalidDataException("Unknown shared settings attribute.")
            };
            using var reader = new XmlNodeReader(document);

            if (serializer.Deserialize(reader, events) is not Settings settings)
            {
                return false;
            }

            normalizedXml = CreateSharedSettingsXml(settings);

            // A typed round trip also validates nested IXmlSerializable dictionaries, whose
            // own key/value serializers do not forward the parent unknown-element callbacks.
            if (normalizedXml != expected)
            {
                normalizedXml = null;
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            normalizedXml = null;
            return false;
        }
    }

    internal static Settings ReadSettingsProfile(string xml, Settings localSettings)
    {
        var document = ReadDocument(xml);
        if (document.DocumentElement is not { Name: "Settings", NamespaceURI.Length: 0 } root)
        {
            throw new InvalidDataException("Invalid settings profile root.");
        }

        var serializer = new XmlSerializer(typeof(Settings));
        using var currentText = new StringWriter();
        serializer.Serialize(currentText, localSettings);
        var current = new XmlDocument { XmlResolver = null };
        current.LoadXml(currentText.ToString());

        // Sharing rules must not replace language, translation endpoints, or other personal preferences.
        foreach (var entry in PropertiesByElement.Where(entry =>
                     entry.Value.IsDefined(typeof(MultiplayerLocalAttribute), true)))
        {
            var incoming = root[entry.Key];
            while (incoming != null)
            {
                root.RemoveChild(incoming);
                incoming = root[entry.Key];
            }

            var local = current.DocumentElement?[entry.Key];
            if (local != null)
            {
                root.AppendChild(document.ImportNode(local, true));
            }
        }

        using var profileReader = new XmlNodeReader(document);
        if (serializer.Deserialize(profileReader) is not Settings settings || !HasValidValues(settings))
        {
            throw new InvalidDataException("Invalid settings profile.");
        }

        return settings;
    }

    private static void ClearHostProfile(HostState state)
    {
        hostProfile = null;
        hostBuildDigest = null;
        hostProfileDigest = null;
        hostState = state;
    }

    private static void ReadHostProfile(Room room)
    {
        if (!room.CustomProperties.TryGetValue(HostProperty, out var value))
        {
            hostEnvelope = null;
            ClearHostProfile(Clock.Elapsed.TotalSeconds - roomEnteredAt < ReportWaitSeconds
                ? HostState.Waiting : HostState.Unsupported);
            return;
        }

        if (value is not string envelope || envelope.Length > MaxEnvelopeCharacters)
        {
            hostEnvelope = null;
            ClearHostProfile(HostState.Invalid);
            return;
        }

        if (envelope == hostEnvelope)
        {
            if (hostState == HostState.Waiting && Clock.Elapsed.TotalSeconds - roomEnteredAt >= ReportWaitSeconds)
            {
                hostState = HostState.Unsupported;
            }

            return;
        }

        hostEnvelope = envelope;
        ClearHostProfile(HostState.Invalid);
        var parts = envelope.Split('|');

        if (parts.Length != 5 || parts[0] != "1" ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var actor) ||
            !IsDigest(parts[2]) || !IsDigest(parts[3]))
        {
            return;
        }

        // Room properties survive a master switch; the old host's snapshot must not be imported.
        if (actor != masterActor)
        {
            hostState = Clock.Elapsed.TotalSeconds - roomEnteredAt < ReportWaitSeconds
                ? HostState.Waiting : HostState.Unsupported;
            return;
        }

        hostBuildDigest = parts[2];
        hostProfileDigest = parts[3];

        if (hostBuildDigest != buildDigest)
        {
            hostState = HostState.VersionMismatch;
            return;
        }

        if (!TryDecompress(parts[4], out var xml) ||
            !TryValidateSharedSettingsXml(xml, out var normalizedXml) ||
            ComputeDigest(Utf8.GetBytes(normalizedXml)) != hostProfileDigest)
        {
            return;
        }

        hostProfile = normalizedXml;
        hostState = HostState.Valid;
    }

    private static bool TryReadReport(Hashtable properties, out string peerBuild, out string peerProfile)
    {
        peerBuild = null;
        peerProfile = null;

        if (!properties.TryGetValue(ClientProperty, out var value) || value is not string report || report.Length > 160)
        {
            return false;
        }

        var parts = report.Split('|');

        if (parts.Length != 4 || parts[0] != "1" ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var actor) ||
            actor != masterActor || !IsDigest(parts[2]) || !IsDigest(parts[3]))
        {
            return false;
        }

        peerBuild = parts[2];
        peerProfile = parts[3];
        return true;
    }

    private static bool PropertyEquals(Hashtable properties, string key, string expected)
    {
        return properties.TryGetValue(key, out var value) && value is string text && text == expected;
    }

    private static string Compress(string xml)
    {
        using var stream = new MemoryStream();

        using (var compressor = new GZipStream(stream, CompressionMode.Compress, true))
        {
            var bytes = Utf8.GetBytes(xml);
            compressor.Write(bytes, 0, bytes.Length);
        }

        if (stream.Length > MaxCompressedBytes)
        {
            throw new InvalidDataException("Compressed UB settings exceed the size limit.");
        }

        return Convert.ToBase64String(stream.ToArray());
    }

    private static bool TryDecompress(string encoded, out string xml)
    {
        xml = null;

        try
        {
            if (encoded.Length > MaxBase64Characters)
            {
                return false;
            }

            var bytes = Convert.FromBase64String(encoded);

            if (bytes.Length > MaxCompressedBytes)
            {
                return false;
            }

            using var input = new MemoryStream(bytes);
            using var decompressor = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            int count;

            while ((count = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + count > MaxXmlBytes)
                {
                    return false;
                }

                output.Write(buffer, 0, count);
            }

            xml = Utf8.GetString(output.ToArray());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static XmlDocument ReadDocument(string xml)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlBytes
        });
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);
        return document;
    }

    private static void NormalizeDocument(XmlDocument document)
    {
        var root = document.DocumentElement ?? throw new InvalidDataException("Missing settings root.");

        foreach (var element in root.ChildNodes.OfType<XmlElement>().ToArray())
        {
            if (!PropertiesByElement.TryGetValue(element.Name, out var property))
            {
                continue;
            }

            if (property.IsDefined(typeof(MultiplayerLocalAttribute), true))
            {
                root.RemoveChild(element);
                continue;
            }

            if (typeof(IDictionary).IsAssignableFrom(property.PropertyType))
            {
                foreach (var item in element.ChildNodes.OfType<XmlElement>().ToArray())
                {
                    if (property.PropertyType.GenericTypeArguments.LastOrDefault() == typeof(List<string>))
                    {
                        var strings = item.SelectSingleNode("value/ArrayOfString") as XmlElement;

                        if (strings != null)
                        {
                            SortChildren(strings, child => child.InnerText);
                        }
                    }
                }

                SortChildren(element, child => child.SelectSingleNode("key")?.InnerText ?? child.OuterXml);
            }
            else if (property.PropertyType == typeof(List<string>) &&
                     (property.Name.EndsWith("Enabled", StringComparison.Ordinal) ||
                      property.Name is "CraftingInStore" or "TabletopFeats2024Initialized"))
            {
                SortChildren(element, child => child.InnerText);
            }
        }

        SortChildren(root, child => child.Name);
    }

    private static void SortChildren(XmlElement parent, Func<XmlElement, string> key)
    {
        foreach (var element in parent.ChildNodes.OfType<XmlElement>().OrderBy(key, StringComparer.Ordinal).ToArray())
        {
            parent.AppendChild(element);
        }
    }

    private static string GetElementName(PropertyInfo property)
    {
        var element = property.GetCustomAttribute<XmlElementAttribute>()?.ElementName;
        var array = property.GetCustomAttribute<XmlArrayAttribute>()?.ElementName;
        return !string.IsNullOrEmpty(element) ? element : !string.IsNullOrEmpty(array) ? array : property.Name;
    }

    private static bool HasValidValues(Settings settings)
    {
        if (settings == null || settings.OverridePartySize is < 1 or > 6 ||
            settings.MaxAllowedClasses < MulticlassContext.MinClasses ||
            settings.MaxAllowedClasses > MulticlassContext.MaxClasses)
        {
            return false;
        }

        return PropertiesByElement.Values
            .Where(property => property.PropertyType.IsEnum &&
                               !property.IsDefined(typeof(MultiplayerLocalAttribute), true))
            .All(property => Enum.IsDefined(property.PropertyType, property.GetValue(settings)));
    }

    private static string ComputeBuildDigest()
    {
        using var stream = File.OpenRead(typeof(MultiplayerSettingsContext).Assembly.Location);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string ComputeDigest(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static bool IsDigest(string digest)
    {
        return digest.Length == 64 && digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string Localize(string suffix)
    {
        return Gui.Localize($"ModUi/&MultiplayerSettings{suffix}");
    }

    private enum HostState
    {
        Waiting,
        Unsupported,
        Invalid,
        VersionMismatch,
        Valid
    }
}
