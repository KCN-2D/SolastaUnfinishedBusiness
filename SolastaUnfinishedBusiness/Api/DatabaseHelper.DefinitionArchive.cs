using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Api;

internal static partial class DatabaseHelper
{
    internal static IReadOnlyList<T> RestoreDefinitionArchive<T>(string resourceName) where T : BaseDefinition
    {
        var assembly = typeof(DatabaseHelper).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName) ??
                           throw new InvalidDataException("Definition archive resource is missing: " + resourceName);
        using var reader = new StreamReader(stream);
        return RestoreDefinitionArchive<T>(ReadDefinitionArchive(reader),
            definition => DefinitionBuilder.RegisterDefinition(definition),
            (definition, behaviors) => definition.AddCustomSubFeatures(behaviors),
            definitions => DefinitionBuilder.ValidateDefinitionRegistration(definitions));
    }

    internal static JObject ReadDefinitionArchive(TextReader reader)
    {
        using var json = new JsonTextReader(reader) { DateParseHandling = DateParseHandling.None };
        var archive = JObject.Load(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        while (json.Read())
            if (json.TokenType != JsonToken.Comment) throw new InvalidDataException("Unexpected content after the definition archive.");
        return archive;
    }

    // This is a definition contract, not the broader DEBUG diagnostics projection.
    internal static IEnumerable<FieldInfo> SerializedDefinitionFields(Type type)
    {
        ValidatePersistentTypeContract(type);
        for (; type != null && type != typeof(object) && type != typeof(UnityEngine.Object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!field.IsStatic && !field.IsInitOnly && !field.IsNotSerialized &&
                    (field.IsPublic || field.IsDefined(typeof(SerializeField), false)))
                    yield return field;
        }
    }

    private static void ValidatePersistentTypeContract(Type type)
    {
        for (; type != null && type != typeof(object) && type != typeof(UnityEngine.Object); type = type.BaseType)
        {
            if (type.Assembly != typeof(DatabaseHelper).Assembly) continue;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                         BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || field.IsNotSerialized) continue;
                if (field.IsInitOnly || !field.IsPublic && !field.IsDefined(typeof(SerializeField), false))
                    throw new InvalidDataException("The existing MOD type has undeclared persistent instance state: " +
                                                   type.FullName + "/" + field.Name);
            }
        }
    }

    internal static JObject CaptureDefinitionArchive<T>(IEnumerable<T> definitions) where T : BaseDefinition =>
        CaptureDefinitionArchive(definitions, definition => definition.GetCustomSubFeatures().Select(feature =>
            feature is FormattedDefinitionText text && text.TryGetDeclaration(out var declaration)
                ? declaration : throw new InvalidDataException("Unsupported or undeclared definition behavior.")));

    internal static JObject CaptureDefinitionArchive<T>(IEnumerable<T> definitions,
        Func<BaseDefinition, IEnumerable<FormattedDefinitionText.Declaration>> behaviors) where T : BaseDefinition
    {
        if (definitions == null || behaviors == null) throw new ArgumentNullException();
        var selected = definitions.Cast<BaseDefinition>().ToArray();
        ValidateDefinitionIdentities<T>(selected);
        return new JObject
        {
            ["schemaVersion"] = 1,
            ["rootType"] = typeof(T).FullName,
            ["nativeModuleVersionId"] = typeof(BaseDefinition).Module.ModuleVersionId.ToString(),
            ["definitions"] = new JArray(selected.OrderBy(value => value.GUID, StringComparer.Ordinal).Select(value =>
                new JObject
                {
                    ["identity"] = DefinitionIdentity(value),
                    ["state"] = CaptureFields(value, new HashSet<object>(new LanguageExtensions.ReferenceEqualityComparer())),
                    ["behaviors"] = new JArray(behaviors(value).Select(CaptureTextDeclaration))
                }))
        };
    }

    internal static IReadOnlyList<T> RestoreDefinitionArchive<T>(JObject archive, Action<BaseDefinition> register,
        Action<BaseDefinition, object[]> attachBehaviors, Action<IReadOnlyList<T>> validateRegistration) where T : BaseDefinition
    {
        if (archive == null || register == null || attachBehaviors == null || validateRegistration == null) throw new ArgumentNullException();
        RequireKeys(archive, "schemaVersion", "rootType", "nativeModuleVersionId", "definitions");
        if ((int)archive["schemaVersion"] != 1 || (string)archive["rootType"] != typeof(T).FullName)
            throw new InvalidDataException("Unsupported definition archive/native schema.");
        var records = RequireArray(archive["definitions"]).Cast<JObject>().ToArray();
        ValidateDeclaredTextCycles(records);
        var byGuid = new Dictionary<string, BaseDefinition>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<T>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registrationStarted = false;
        try
        {
            // Allocate all identities first. Nothing enters a native database during validation/population.
            foreach (var record in records)
            {
                RequireKeys(record, "identity", "state", "behaviors");
                var identity = RequireObject(record["identity"]);
                var type = DefinitionType<T>(identity);
                ValidatePersistentTypeContract(type);
                var name = (string)identity["name"];
                var guid = (string)identity["guid"];
                if (!names.Add(name) || byGuid.ContainsKey(guid))
                    throw new InvalidDataException("Duplicate archived definition identity.");
                var active = FindDefinition(identity);
                if (active)
                {
                    // Active definitions own their current identity. Never overwrite them from history.
                    byGuid.Add(guid, active);
                    continue;
                }
                var value = (T)ScriptableObject.CreateInstance(type);
                value.name = name;
                byGuid.Add(guid, value);
                pending.Add(value);
            }

            BaseDefinition Resolve(JObject identity)
            {
                DefinitionType<BaseDefinition>(identity);
                if (byGuid.TryGetValue((string)identity["guid"], out var value))
                {
                    if (value.name != (string)identity["name"] || value.GetType().FullName != (string)identity["type"])
                        throw new InvalidDataException("Archived reference identity/type differs.");
                    return value;
                }
                return FindDefinition(identity) ?? throw new InvalidDataException("Unresolved archived definition reference.");
            }

            // Populate every pending state and resolve its complete reference graph before registration.
            var declarations = new Dictionary<BaseDefinition, object[]>();
            foreach (var record in records)
            {
                var identity = RequireObject(record["identity"]);
                var value = byGuid[(string)identity["guid"]];
                var isPending = pending.Contains((T)value);
                ValidateFields(record["state"], value.GetType(), Resolve);
                var texts = RequireArray(record["behaviors"]).Select(item => RestoreTextDeclaration(item, Resolve))
                    .Select(text => (object)FormattedDefinitionText.FromDeclaration(text)).ToArray();
                if (!isPending) continue;
                PopulateFields(value, RequireObject(record["state"]), Resolve);
                if (value.Name != (string)identity["name"] ||
                    !string.Equals(value.GUID, (string)identity["guid"], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Archived state changed its definition identity.");
                declarations.Add(value, texts);
            }
            validateRegistration(pending);
            foreach (var value in pending) attachBehaviors(value, declarations[value]);
            registrationStarted = true;
            foreach (var value in pending) register(value);
            return pending;
        }
        catch
        {
            // No caller should enable gameplay after failed startup registration.
            if (!registrationStarted) foreach (var value in pending) if (value) UnityEngine.Object.Destroy(value);
            throw;
        }
    }

    internal static JObject DefinitionIdentity(BaseDefinition definition)
    {
        if (!definition || string.IsNullOrWhiteSpace(definition.Name) || !Guid.TryParse(definition.GUID, out _))
            throw new InvalidDataException("Invalid definition identity.");
        return new JObject { ["type"] = definition.GetType().FullName, ["name"] = definition.Name, ["guid"] = definition.GUID };
    }

    private static void ValidateDefinitionIdentities<T>(BaseDefinition[] definitions) where T : BaseDefinition
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in definitions)
        {
            var identity = DefinitionIdentity(value);
            DefinitionType<T>(identity);
            if (!names.Add(value.Name) || !guids.Add(value.GUID))
                throw new InvalidDataException("Duplicate definition identity in export.");
        }
    }

    private static Type ExistingDefinitionType(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var matches = new[] { typeof(BaseDefinition).Assembly, typeof(DatabaseHelper).Assembly }.Distinct()
            .Select(assembly => assembly.GetType(name, false)).Where(type => type != null).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Ambiguous existing definition/value type.");
        return matches.SingleOrDefault();
    }

    private static Type DefinitionType<T>(JObject identity) where T : BaseDefinition
    {
        RequireKeys(identity, "type", "name", "guid");
        var type = ExistingDefinitionType((string)identity["type"]);
        if (type == null || type.IsAbstract || !typeof(T).IsAssignableFrom(type) ||
            string.IsNullOrWhiteSpace((string)identity["name"]) || !Guid.TryParse((string)identity["guid"], out _))
            throw new InvalidDataException("Definition type is outside the archive root capability.");
        return type;
    }

    internal static BaseDefinition FindDefinition(JObject identity)
    {
        var definitionType = DefinitionType<BaseDefinition>(identity);
        var method = typeof(DatabaseRepository).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(value => value.Name == "GetDatabase" && value.IsGenericMethodDefinition && value.GetParameters().Length == 0);
        var name = (string)identity["name"];
        var guid = (string)identity["guid"];
        BaseDefinition found = null;
        var availableDatabases = 0;
        var matchingDatabases = 0;
        for (var type = definitionType; type != typeof(BaseDefinition); type = type.BaseType)
        {
            var database = method.MakeGenericMethod(type).Invoke(null, null);
            if (database == null) continue;
            availableDatabases++;
            var hasName = (bool)database.GetType().GetMethod("HasElement").Invoke(database, new object[] { name, true });
            var hasGuid = (bool)database.GetType().GetMethod("HasElementByGuid").Invoke(database, new object[] { guid });
            if (!hasName && !hasGuid) continue;
            if (!hasName || !hasGuid) throw new InvalidDataException("Native definition name/GUID collision.");
            var value = (BaseDefinition)database.GetType().GetMethod("GetElement", new[] { typeof(string), typeof(bool) })
                .Invoke(database, new object[] { name, false });
            if (!value || value.GetType() != definitionType || !string.Equals(value.GUID, guid, StringComparison.OrdinalIgnoreCase) ||
                found && !ReferenceEquals(found, value))
                throw new InvalidDataException("Native definition identity/type collision.");
            found = value;
            matchingDatabases++;
        }
        if (found && availableDatabases != matchingDatabases) throw new InvalidDataException("Incomplete definition registration across matching databases.");
        return found;
    }

    private static string FieldKey(FieldInfo field) => field.DeclaringType.FullName + "/" + field.Name;

    private static JObject CaptureFields(object value, HashSet<object> ancestors)
    {
        if (!ancestors.Add(value)) throw new InvalidDataException("Inline serialized value cycle.");
        try
        {
            var members = new JObject();
            foreach (var field in SerializedDefinitionFields(value.GetType()).OrderBy(FieldKey, StringComparer.Ordinal))
                members.Add(FieldKey(field), CaptureValue(field.GetValue(value), field.FieldType, ancestors));
            return new JObject { ["type"] = value.GetType().FullName, ["fields"] = members };
        }
        finally { ancestors.Remove(value); }
    }

    private static JToken CaptureValue(object value, Type declared, HashSet<object> ancestors)
    {
        if (value == null) return JValue.CreateNull();
        if (value is BaseDefinition definition) return new JObject { ["reference"] = DefinitionIdentity(definition) };
        if (value is UnityEngine.Object) throw new InvalidDataException("Unsupported non-definition Unity object reference.");
        if (declared.IsEnum)
        {
            var token = new JValue(Convert.ChangeType(value, Enum.GetUnderlyingType(declared), CultureInfo.InvariantCulture));
            RestoreEnum(token, declared);
            return token;
        }
        if (declared.IsPrimitive || declared == typeof(string) || declared == typeof(decimal))
        {
            var token = new JValue(value);
            RestorePrimitive(token, declared);
            return token;
        }
        if (value is IList list)
        {
            var element = declared.IsArray ? declared.GetElementType() : declared.GetGenericArguments().Single();
            return new JArray(list.Cast<object>().Select(item => CaptureValue(item, element, ancestors)));
        }
        ValidateValueType(value.GetType(), declared);
        return CaptureFields(value, ancestors);
    }

    private static void ValidateValueType(Type actual, Type declared)
    {
        if (!declared.IsAssignableFrom(actual) || typeof(Delegate).IsAssignableFrom(actual) ||
            actual.IsAbstract || actual.IsPointer || actual.IsInterface || actual == typeof(object) ||
            !typeof(BaseDefinition).IsAssignableFrom(actual) && !actual.IsValueType && !actual.IsSerializable ||
            !SerializedDefinitionFields(actual).Any() ||
            actual != declared && actual.Assembly != typeof(BaseDefinition).Assembly && actual.Assembly != typeof(DatabaseHelper).Assembly ||
            actual != declared && !actual.IsSerializable)
            throw new InvalidDataException("Unsupported serialized value type.");
    }

    private static Type SerializedValueType(JObject token, Type declared)
    {
        RequireKeys(token, "type", "fields");
        var name = (string)token["type"];
        var type = name == declared.FullName ? declared : ExistingDefinitionType(name);
        if (type == null) throw new InvalidDataException("Unknown serialized value type.");
        ValidateValueType(type, declared);
        return type;
    }

    private static void ValidateFields(JToken token, Type type, Func<JObject, BaseDefinition> resolve)
    {
        var state = RequireObject(token);
        if (SerializedValueType(state, type) != type) throw new InvalidDataException("Root state type mismatch.");
        var fields = RequireObject(state["fields"]);
        var schema = SerializedDefinitionFields(type).ToDictionary(FieldKey);
        if (fields.Count != schema.Count || fields.Properties().Any(field => !schema.ContainsKey(field.Name)))
            throw new InvalidDataException("Serialized member schema differs.");
        foreach (var field in schema) RestoreValue(fields[field.Key], field.Value.FieldType, resolve, true);
    }

    private static object RestoreValue(JToken value, Type declared, Func<JObject, BaseDefinition> resolve, bool validateOnly)
    {
        if (value == null) throw new InvalidDataException("Serialized value missing.");
        if (value.Type == JTokenType.Null)
        {
            if (declared.IsValueType) throw new InvalidDataException("Null serialized value type.");
            return null;
        }
        if (typeof(BaseDefinition).IsAssignableFrom(declared))
        {
            var reference = RequireObject(value); RequireKeys(reference, "reference");
            var definition = resolve(RequireObject(reference["reference"]));
            if (!declared.IsInstanceOfType(definition)) throw new InvalidDataException("Reference field type mismatch.");
            return definition;
        }
        if (declared.IsEnum) return RestoreEnum(value, declared);
        if (declared.IsPrimitive || declared == typeof(string) || declared == typeof(decimal))
            return RestorePrimitive(value, declared);
        if (declared.IsArray || typeof(IList).IsAssignableFrom(declared))
        {
            var values = RequireArray(value);
            var element = declared.IsArray ? declared.GetElementType() : declared.GetGenericArguments().Single();
            var items = values.Select(item => RestoreValue(item, element, resolve, validateOnly)).ToArray();
            if (validateOnly) return null;
            var list = declared.IsArray ? (IList)Array.CreateInstance(element, items.Length) : (IList)Activator.CreateInstance(declared);
            for (var index = 0; index < items.Length; index++) if (declared.IsArray) list[index] = items[index]; else list.Add(items[index]);
            return list;
        }
        var state = RequireObject(value);
        var actual = SerializedValueType(state, declared);
        ValidateFields(state, actual, resolve);
        if (validateOnly) return null;
        var result = FormatterServices.GetUninitializedObject(actual);
        PopulateFields(result, state, resolve);
        return result;
    }

    private static object RestoreEnum(JToken token, Type type)
    {
        if (token.Type != JTokenType.Integer) throw new InvalidDataException("Expected the enum's integral underlying value.");
        return Enum.ToObject(type, RestorePrimitive(token, Enum.GetUnderlyingType(type)));
    }

    private static object RestorePrimitive(JToken token, Type type)
    {
        if (type == typeof(string))
        {
            if (token.Type != JTokenType.String) throw new InvalidDataException("Expected a string token.");
            return (string)token;
        }
        if (type == typeof(char))
        {
            if (token.Type != JTokenType.String || ((string)token).Length != 1)
                throw new InvalidDataException("Expected one character.");
            return ((string)token)[0];
        }
        var expected = type == typeof(bool) ? JTokenType.Boolean :
            type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? JTokenType.Float : JTokenType.Integer;
        if (token.Type != expected) throw new InvalidDataException("Primitive token type differs from its captured format.");
        try
        {
            var result = Convert.ChangeType(((JValue)token).Value, type, CultureInfo.InvariantCulture);
            if (result is float single && (float.IsInfinity(single) || float.IsNaN(single)) ||
                result is double number && (double.IsInfinity(number) || double.IsNaN(number)))
                throw new InvalidDataException("Non-finite serialized number.");
            return result;
        }
        catch (Exception error) when (error is OverflowException || error is FormatException || error is InvalidCastException)
        {
            throw new InvalidDataException("Serialized primitive is outside its declared type.", error);
        }
    }

    private static void PopulateFields(object target, JObject state, Func<JObject, BaseDefinition> resolve)
    {
        var fields = RequireObject(state["fields"]);
        foreach (var field in SerializedDefinitionFields(target.GetType()))
            field.SetValue(target, RestoreValue(fields[FieldKey(field)], field.FieldType, resolve, false));
    }

    private static void ValidateDeclaredTextCycles(JObject[] records)
    {
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var guid = (string)RequireObject(record["identity"])["guid"];
            var behaviors = RequireArray(record["behaviors"]);
            if (behaviors.Count > 1) throw new InvalidDataException("Ambiguous archived text behavior.");
            foreach (var item in behaviors.Cast<JObject>())
                foreach (var member in new[] { "title", "description" })
                {
                    var text = item[member];
                    if (text == null || text.Type == JTokenType.Null) continue;
                    var edges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var expression in new[] { text }.Concat(text is JContainer container ? container.Descendants() : Enumerable.Empty<JToken>()).OfType<JObject>())
                    {
                        var kind = (string)expression["kind"];
                        if (kind != nameof(FormattedDefinitionText.TextKind.DefinitionTitle) &&
                            kind != nameof(FormattedDefinitionText.TextKind.DefinitionDescription)) continue;
                        var target = RequireObject(expression["definition"]);
                        edges.Add((string)target["guid"] + "/" + (kind == nameof(FormattedDefinitionText.TextKind.DefinitionTitle) ? "title" : "description"));
                    }
                    if (graph.ContainsKey(guid + "/" + member)) throw new InvalidDataException("Duplicate archived text binding.");
                    graph.Add(guid + "/" + member, edges);
                }
        }
        // Only declared text edges participate; FeatureSet gameplay references may legitimately cycle.
        var incoming = graph.Keys.ToDictionary(key => key, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var edges in graph.Values) foreach (var edge in edges) if (incoming.ContainsKey(edge)) incoming[edge]++;
        var queue = new Queue<string>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (queue.Count != 0)
        {
            var key = queue.Dequeue(); visited++;
            foreach (var target in graph[key]) if (incoming.ContainsKey(target) && --incoming[target] == 0) queue.Enqueue(target);
        }
        if (visited != graph.Count) throw new InvalidDataException("Cyclic declared definition text.");
    }

    private static JObject CaptureTextDeclaration(FormattedDefinitionText.Declaration declaration) => new()
    {
        ["kind"] = "formatted-definition-text",
        ["title"] = CaptureText(declaration.Title), ["description"] = CaptureText(declaration.Description)
    };

    private static JToken CaptureText(FormattedDefinitionText.Text text)
    {
        if (text == null) return JValue.CreateNull();
        text.Validate();
        return new JObject
        {
            ["kind"] = text.Kind.ToString(), ["term"] = text.Term == null ? JValue.CreateNull() : new JValue(text.Term),
            ["value"] = text.Value == null ? JValue.CreateNull() : new JValue(text.Value),
            ["definition"] = text.Definition ? DefinitionIdentity(text.Definition) : null,
            ["arguments"] = new JArray(text.Arguments.Select(CaptureText))
        };
    }

    internal static FormattedDefinitionText.Declaration RestoreTextDeclaration(JToken token,
        Func<JObject, BaseDefinition> resolve)
    {
        var item = RequireObject(token); RequireKeys(item, "kind", "title", "description");
        if ((string)item["kind"] != "formatted-definition-text") throw new InvalidDataException("Unsupported archived behavior.");
        return new FormattedDefinitionText.Declaration(RestoreText(item["title"], resolve), RestoreText(item["description"], resolve));
    }

    private static FormattedDefinitionText.Text RestoreText(JToken token, Func<JObject, BaseDefinition> resolve)
    {
        if (token.Type == JTokenType.Null) return null;
        var item = RequireObject(token); RequireKeys(item, "kind", "term", "value", "definition", "arguments");
        var kind = (string)item["kind"];
        var args = RequireArray(item["arguments"]);
        if (kind == nameof(FormattedDefinitionText.TextKind.Translation))
        {
            if (item["definition"].Type != JTokenType.Null || item["value"].Type != JTokenType.Null)
                throw new InvalidDataException("Unexpected translation payload.");
            return FormattedDefinitionText.Text.Translation((string)RestorePrimitive(item["term"], typeof(string)), args.Select(arg => RestoreText(arg, resolve)).ToArray());
        }
        if (kind == nameof(FormattedDefinitionText.TextKind.Literal))
        {
            if (item["term"].Type != JTokenType.Null || item["definition"].Type != JTokenType.Null || args.Count != 0)
                throw new InvalidDataException("Unexpected literal text payload.");
            return FormattedDefinitionText.Text.Literal((string)RestorePrimitive(item["value"], typeof(string)));
        }
        if (item["term"].Type != JTokenType.Null || item["value"].Type != JTokenType.Null || args.Count != 0)
            throw new InvalidDataException("Unexpected definition text payload.");
        var definition = resolve(RequireObject(item["definition"]));
        if (kind == nameof(FormattedDefinitionText.TextKind.DefinitionTitle)) return FormattedDefinitionText.Text.DefinitionTitle(definition);
        if (kind == nameof(FormattedDefinitionText.TextKind.DefinitionDescription)) return FormattedDefinitionText.Text.DefinitionDescription(definition);
        throw new InvalidDataException("Unknown archived text kind.");
    }

    private static JObject RequireObject(JToken token) => token as JObject ?? throw new InvalidDataException("Expected an archive object.");
    private static JArray RequireArray(JToken token) => token as JArray ?? throw new InvalidDataException("Expected an archive array.");
    private static void RequireKeys(JObject value, params string[] expected)
    {
        if (value.Count != expected.Length || expected.Any(key => value.Property(key, StringComparison.Ordinal) == null))
            throw new InvalidDataException("Unknown or missing archive field.");
    }
}
