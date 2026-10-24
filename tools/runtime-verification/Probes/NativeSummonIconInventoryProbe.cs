using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;

// Exports existing definitions/artwork and resident UI state. It never binds an
// actor, changes a definition or invokes a power/spell/action.
public static class NativeSummonIconInventoryProbe
{
    const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    [Serializable] public sealed class Entry
    {
        public string kind, name, title, spriteGuid, subObject, activation, forms, summonKinds, summonTargets;
        public string battleActionId, reactionName, reactionContext, triggeringPower, overriddenPower;
        public int contentPack;
        public bool native, hidden, directSummon, summonFamily, validSprite, delegatedToAction;
        public string[] children;
        public Owner[] owners;
        public ActionUse[] actionUses;
        public string export;
    }
    [Serializable] public sealed class Owner
    {
        public string kind, name, title, featurePath;
        public int contentPack, level;
    }
    [Serializable] public sealed class ActionUse
    {
        public string name, title, actionType, actionScope, spriteGuid, subObject;
        public int contentPack;
        public bool hidden, displayPowerTooltip;
    }
    [Serializable] public sealed class Art
    {
        public string file, canvasFile, guid, subObject, spriteName, packingMode, packingRotation;
        public int width, height, textureWidth, textureHeight, transparent, translucent, opaque, coloredVisiblePixels;
        public int canvasWidth, canvasHeight, canvasTransparent, canvasTranslucent, canvasOpaque;
        public bool packed, customArtwork;
        public float[] originalRect, atlasRect, atlasOffset, pivot, border;
        public float meanVisibleChroma;
    }
    [Serializable] public sealed class Inventory
    {
        public string status, scope;
        public int nativePowerCount, nativeSpellCount, directNativeSummons, nativeSummonFamilySpells;
        public Entry[] definitions;
        public Art[] artwork;
    }

    public static IEnumerator Run(Action<string> log)
    {
        var root = Path.Combine(RuntimeVerificationConfiguration.EvidenceDirectory, "NativeSummonIcons");
        var pictures = Path.Combine(root, "Artwork");
        Directory.CreateDirectory(pictures);
        var powers = DatabaseRepository.GetDatabase<FeatureDefinitionPower>().ToArray();
        var spells = DatabaseRepository.GetDatabase<SpellDefinition>().ToArray();
        var entries = new List<Entry>();
        var sources = new Dictionary<Entry, BaseDefinition>();
        foreach (var power in powers)
        {
            var entry = Describe(power, power.EffectDescription, power.ActivationTime.ToString(), "Power", new string[0]);
            entry.delegatedToAction = power.DelegatedToAction;
            entry.battleActionId = power.BattleActionId.ToString();
            entry.reactionName = power.ReactionName;
            entry.reactionContext = power.ReactionContext.ToString();
            entry.triggeringPower = power.TriggeringPower ? power.TriggeringPower.Name : "";
            entry.overriddenPower = power.OverriddenPower ? power.OverriddenPower.Name : "";
            entries.Add(entry); sources.Add(entry, power);
        }
        foreach (var spell in spells)
        {
            var entry = Describe(spell, spell.EffectDescription, spell.ActivationTime.ToString(), "Spell",
                                 spell.SubspellsList == null ? new string[0] : spell.SubspellsList.Select(value => value.Name).ToArray());
            entries.Add(entry); sources.Add(entry, spell);
        }
        var byPower = entries.Where(value => value.kind == "Power").ToDictionary(value => value.name);
        DescribeOwnership(byPower);
        foreach (var action in DatabaseRepository.GetDatabase<ActionDefinition>())
        {
            var power = action.ActivatedPower;
            Entry entry;
            if (!power || !byPower.TryGetValue(power.Name, out entry)) continue;
            var reference = action.GuiPresentation.SpriteReference;
            entry.actionUses = entry.actionUses.Concat(new[] { new ActionUse
            {
                name = action.Name, title = Gui.Localize(action.GuiPresentation.Title), contentPack = (int)action.ContentPack,
                hidden = action.GuiPresentation.Hidden, displayPowerTooltip = action.DisplayPowerTooltip,
                actionType = action.ActionType.ToString(), actionScope = action.ActionScope.ToString(),
                spriteGuid = reference == null ? "" : reference.AssetGUID, subObject = reference == null ? "" : reference.SubObjectName
            } }).ToArray();
        }
        // Include the entire native summon selection tree, not just leaf effects.
        var bySpell = entries.Where(value => value.kind == "Spell").ToDictionary(value => value.name);
        bool changed;
        do
        {
            changed = false;
            foreach (var entry in bySpell.Values.Where(value => !value.summonFamily))
                if (entry.children.Any(name => bySpell.ContainsKey(name) && bySpell[name].summonFamily))
                { entry.summonFamily = true; changed = true; }
        } while (changed);

        var exported = new Dictionary<string, Art>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var entry in entries.Where(value => value.native && (value.kind == "Power" || value.summonFamily) ||
                                                     value.name.StartsWith("PowerInnovationArtillerist", StringComparison.Ordinal) &&
                                                     (value.name.Contains("Activate") || value.name.Contains("Summon"))))
        {
            if (!entry.validSprite) continue;
            var key = entry.spriteGuid + "/" + entry.subObject;
            Art art = null;
            if (!exported.TryGetValue(key, out art))
            {
                try
                {
                    var reference = sources[entry].GuiPresentation.SpriteReference;
                    art = Export(reference, pictures, entry.kind + "-" + entry.name);
                    exported.Add(key, art);
                }
                catch (Exception exception)
                {
                    failures.Add(entry.kind + "/" + entry.name + ": " + exception);
                }
                yield return null;
            }
            if (art != null) entry.export = "Artwork/" + art.file;
        }

        var uiRows = new List<string> { "Widget\tResidentPath\tScene\tActive\tImagePath\tSprite\tRect\tTint\tCanvasScale\tPreserveAspect\tImageType\tFitter" };
        DescribeWidgets<UsablePowerBox>(uiRows);
        DescribeWidgets<SpellBox>(uiRows);
        DescribeWidgets<SubpowerItem>(uiRows);
        DescribeWidgets<SubspellItem>(uiRows);
        DescribeWidgets<CharacterActionItem>(uiRows);
        var inventory = new Inventory
        {
            status = failures.Count == 0 ? "PASS" : "ERROR",
            scope = "All power metadata, feature ownership paths, action delegation and native power artwork. Spells are a separate inventory, with native SummonForm selection-family artwork. Ownership and resident widgets do not establish availability or normal navigation.",
            nativePowerCount = entries.Count(value => value.native && value.kind == "Power"),
            nativeSpellCount = entries.Count(value => value.native && value.kind == "Spell"),
            directNativeSummons = entries.Count(value => value.native && value.directSummon),
            nativeSummonFamilySpells = entries.Count(value => value.native && value.kind == "Spell" && value.summonFamily),
            definitions = entries.ToArray(), artwork = exported.Values.ToArray()
        };
        File.WriteAllText(Path.Combine(root, "Inventory.json"), Json(inventory));
        File.WriteAllLines(Path.Combine(root, "ResidentUi.tsv"), uiRows);
        File.WriteAllLines(Path.Combine(root, "Errors.txt"), failures);
        yield return new WaitForEndOfFrame();
        var capture = ScreenCapture.CaptureScreenshotAsTexture();
        try { File.WriteAllBytes(Path.Combine(root, "Screen.png"), capture.EncodeToPNG()); }
        finally { UnityEngine.Object.Destroy(capture); }
        if (failures.Count != 0) throw new InvalidOperationException("Native artwork export errors=" + failures.Count + "; see NativeSummonIcons/Errors.txt");
        log("PASS: read-only native icon inventory; nativePowers=" + inventory.nativePowerCount + "; nativeSpells=" + inventory.nativeSpellCount +
            "; directNativeSummons=" + inventory.directNativeSummons + "; summonFamilySpells=" + inventory.nativeSummonFamilySpells +
            "; uniqueArtwork=" + inventory.artwork.Length + "; residentUiRows=" + (uiRows.Count - 1));
    }

    static Entry Describe(BaseDefinition definition, EffectDescription effect, string activation, string kind, string[] children)
    {
        var forms = effect == null || effect.EffectForms == null ? new EffectForm[0] : effect.EffectForms.ToArray();
        var summons = forms.Where(value => value.FormType == EffectForm.EffectFormType.Summon)
            .Select(value => (SummonForm)typeof(EffectForm).GetField("summonForm", Fields).GetValue(value)).Where(value => value != null).ToArray();
        var reference = definition.GuiPresentation.SpriteReference;
        return new Entry
        {
            kind = kind, name = definition.Name, title = Gui.Localize(definition.GuiPresentation.Title),
            contentPack = (int)definition.ContentPack, native = (int)definition.ContentPack != 9999,
            hidden = definition.GuiPresentation.Hidden, activation = activation,
            spriteGuid = reference == null ? "" : reference.AssetGUID,
            subObject = reference == null ? "" : reference.SubObjectName,
            validSprite = reference != null && reference.RuntimeKeyIsValid(),
            forms = string.Join("|", forms.Select(value => value.FormType.ToString()).Distinct().ToArray()),
            summonKinds = string.Join("|", summons.Select(value => value.SummonType.ToString()).Distinct().ToArray()),
            summonTargets = string.Join("|", summons.Select(value => value.MonsterDefinitionName + ":" + value.EffectProxyDefinitionName + ":" +
                (value.ItemDefinition ? value.ItemDefinition.Name : "")).ToArray()),
            directSummon = summons.Length != 0, summonFamily = summons.Length != 0, children = children,
            owners = new Owner[0], actionUses = new ActionUse[0]
        };
    }

    static void DescribeOwnership(Dictionary<string, Entry> powers)
    {
        foreach (var definition in DatabaseRepository.GetDatabase<CharacterClassDefinition>())
            foreach (var unlock in definition.FeatureUnlocks)
                VisitFeature(powers, definition, "Class", unlock.FeatureDefinition, unlock.Level, "", new HashSet<string>());
        foreach (var definition in DatabaseRepository.GetDatabase<CharacterSubclassDefinition>())
            foreach (var unlock in definition.FeatureUnlocks)
                VisitFeature(powers, definition, "Subclass", unlock.FeatureDefinition, unlock.Level, "", new HashSet<string>());
        foreach (var definition in DatabaseRepository.GetDatabase<CharacterRaceDefinition>())
            foreach (var unlock in definition.FeatureUnlocks)
                VisitFeature(powers, definition, "Race", unlock.FeatureDefinition, unlock.Level, "", new HashSet<string>());
        foreach (var definition in DatabaseRepository.GetDatabase<FeatDefinition>())
            foreach (var feature in definition.Features)
                VisitFeature(powers, definition, "Feat", feature, 0, "", new HashSet<string>());
        foreach (var definition in DatabaseRepository.GetDatabase<FightingStyleDefinition>())
            foreach (var feature in definition.Features)
                VisitFeature(powers, definition, "FightingStyle", feature, 0, "", new HashSet<string>());
        foreach (var definition in DatabaseRepository.GetDatabase<InvocationDefinition>())
            VisitFeature(powers, definition, "Invocation", definition.GrantedFeature, 0, "", new HashSet<string>());
        foreach (var definition in DatabaseRepository.GetDatabase<MonsterDefinition>())
            foreach (var feature in definition.Features)
                VisitFeature(powers, definition, "Monster", feature, 0, "", new HashSet<string>());
        foreach (var definition in DatabaseRepository.GetDatabase<ConditionDefinition>())
            foreach (var feature in definition.Features)
                VisitFeature(powers, definition, "Condition", feature, 0, "", new HashSet<string>());
        foreach (var definition in DatabaseRepository.GetDatabase<ItemDefinition>())
        {
            foreach (var property in definition.StaticProperties)
                VisitFeature(powers, definition, "ItemProperty", property.FeatureDefinition, 0, "", new HashSet<string>());
            if (!definition.IsUsableDevice || definition.UsableDeviceDescription == null) continue;
            foreach (var function in definition.UsableDeviceDescription.DeviceFunctions)
                VisitFeature(powers, definition, "DeviceFunction", function.FeatureDefinitionPower, 0, "", new HashSet<string>());
        }
        // Unowned feature sets can be referenced by scripts or runtime-only paths;
        // retain their membership without asserting a player-facing owner.
        foreach (var definition in DatabaseRepository.GetDatabase<FeatureDefinitionFeatureSet>())
            VisitFeature(powers, definition, "FeatureSetMembership", definition, 0, "", new HashSet<string>());
    }

    static void VisitFeature(Dictionary<string, Entry> powers, BaseDefinition owner, string kind,
        FeatureDefinition feature, int level, string path, HashSet<string> visited)
    {
        if (!feature || !visited.Add(feature.Name)) return;
        path = path.Length == 0 ? feature.Name : path + "/" + feature.Name;
        var power = feature as FeatureDefinitionPower;
        Entry entry;
        if (power && powers.TryGetValue(power.Name, out entry))
        {
            if (!entry.owners.Any(value => value.kind == kind && value.name == owner.Name && value.level == level && value.featurePath == path))
                entry.owners = entry.owners.Concat(new[] { new Owner
                {
                    kind = kind, name = owner.Name, title = Gui.Localize(owner.GuiPresentation.Title),
                    contentPack = (int)owner.ContentPack, level = level, featurePath = path
                } }).ToArray();
        }
        var set = feature as FeatureDefinitionFeatureSet;
        if (set)
            foreach (var child in set.FeatureSet)
                VisitFeature(powers, owner, kind, child, level, path + "[" + set.Mode + "]", visited);
        visited.Remove(feature.Name);
    }

    static Art Export(AssetReferenceSprite reference, string output, string name)
    {
        if (reference == null || !reference.RuntimeKeyIsValid()) throw new InvalidOperationException("Invalid sprite reference: " + name);
        var sprite = Gui.LoadAssetSync<Sprite>(reference);
        if (!sprite) throw new InvalidOperationException("Unavailable sprite: " + name);
        var source = sprite.texture; var rect = sprite.textureRect; var original = sprite.rect;
        var offset = sprite.textureRectOffset; var rotation = sprite.packingRotation; var previous = RenderTexture.active;
        var render = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var image = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
        Texture2D canvas = null;
        try
        {
            Graphics.Blit(source, render); RenderTexture.active = render; image.ReadPixels(rect, 0, 0); image.Apply();
            var pixels = image.GetPixels32(); var visible = pixels.Where(value => value.a != 0).ToArray();
            var invalid = Path.GetInvalidFileNameChars();
            var file = new string(name.Select(value => invalid.Contains(value) ? '-' : value).ToArray()) + ".png";
            File.WriteAllBytes(Path.Combine(output, file), image.EncodeToPNG());
            var canvasWidth = Mathf.RoundToInt(original.width); var canvasHeight = Mathf.RoundToInt(original.height);
            var paddingX = Mathf.RoundToInt(offset.x); var paddingY = Mathf.RoundToInt(offset.y);
            if (paddingX < 0 || paddingY < 0 || paddingX + image.width > canvasWidth || paddingY + image.height > canvasHeight)
                throw new InvalidOperationException("Sprite trimmed rect lies outside its original canvas: " + name);
            if (rotation != SpritePackingRotation.None && rotation != SpritePackingRotation.FlipHorizontal &&
                rotation != SpritePackingRotation.FlipVertical && rotation != SpritePackingRotation.Rotate180)
                throw new InvalidOperationException("Unsupported atlas rotation: " + rotation + ": " + name);
            var canvasPixels = new Color32[canvasWidth * canvasHeight];
            for (var y = 0; y < image.height; y++)
            for (var x = 0; x < image.width; x++)
            {
                var sourceX = rotation == SpritePackingRotation.FlipHorizontal || rotation == SpritePackingRotation.Rotate180 ? image.width - 1 - x : x;
                var sourceY = rotation == SpritePackingRotation.FlipVertical || rotation == SpritePackingRotation.Rotate180 ? image.height - 1 - y : y;
                canvasPixels[(paddingY + y) * canvasWidth + paddingX + x] = pixels[sourceY * image.width + sourceX];
            }
            canvas = new Texture2D(canvasWidth, canvasHeight, TextureFormat.RGBA32, false);
            canvas.SetPixels32(canvasPixels); canvas.Apply();
            var canvasFile = Path.GetFileNameWithoutExtension(file) + "-canvas.png";
            File.WriteAllBytes(Path.Combine(output, canvasFile), canvas.EncodeToPNG());
            return new Art
            {
                file = file, canvasFile = canvasFile, guid = reference.AssetGUID, subObject = reference.SubObjectName, spriteName = sprite.name,
                width = image.width, height = image.height, textureWidth = source.width, textureHeight = source.height, packed = sprite.packed,
                canvasWidth = canvasWidth, canvasHeight = canvasHeight,
                canvasTransparent = canvasPixels.Count(value => value.a == 0),
                canvasTranslucent = canvasPixels.Count(value => value.a != 0 && value.a != 255),
                canvasOpaque = canvasPixels.Count(value => value.a == 255),
                originalRect = new[] { original.x, original.y, original.width, original.height },
                atlasRect = new[] { rect.x, rect.y, rect.width, rect.height }, atlasOffset = new[] { offset.x, offset.y },
                pivot = new[] { sprite.pivot.x, sprite.pivot.y }, border = new[] { sprite.border.x, sprite.border.y, sprite.border.z, sprite.border.w },
                packingMode = sprite.packingMode.ToString(), packingRotation = rotation.ToString(),
                customArtwork = sprite.name.StartsWith("_CE_", StringComparison.Ordinal),
                transparent = pixels.Count(value => value.a == 0), translucent = pixels.Count(value => value.a != 0 && value.a != 255),
                opaque = pixels.Count(value => value.a == 255),
                coloredVisiblePixels = visible.Count(value => Mathf.Max(value.r, Mathf.Max(value.g, value.b)) - Mathf.Min(value.r, Mathf.Min(value.g, value.b)) > 8),
                meanVisibleChroma = visible.Length == 0 ? 0 : visible.Average(value => (float)(Mathf.Max(value.r, Mathf.Max(value.g, value.b)) - Mathf.Min(value.r, Mathf.Min(value.g, value.b))))
            };
        }
        finally
        {
            RenderTexture.active = previous; RenderTexture.ReleaseTemporary(render);
            if (canvas) UnityEngine.Object.Destroy(canvas);
            UnityEngine.Object.Destroy(image); Gui.ReleaseAddressableAsset(sprite);
        }
    }

    static void DescribeWidgets<T>(List<string> rows) where T : Component
    {
        foreach (var widget in UnityEngine.Resources.FindObjectsOfTypeAll<T>())
        foreach (var image in widget.GetComponentsInChildren<Image>(true))
        {
            var fitter = image.GetComponent<AspectRatioFitter>();
            rows.Add(typeof(T).Name + "\t" + PathOf(widget.transform) + "\t" + widget.gameObject.scene.name + "\t" +
                widget.gameObject.activeInHierarchy + "\t" + PathOf(image.transform) + "\t" + (image.sprite ? image.sprite.name : "none") + "\t" +
                image.rectTransform.rect + "\t" + image.color + "\t" + (image.canvas ? image.canvas.scaleFactor : 0) + "\t" +
                image.preserveAspect + "\t" + image.type + "\t" + (fitter ? fitter.aspectMode + ":" + fitter.aspectRatio : "none"));
        }
    }
    static string PathOf(Transform item)
    {
        var result = item.name;
        while (item.parent) { item = item.parent; result = item.name + "/" + result; }
        return result;
    }

    static string Json(object value)
    {
        if (value == null) return "null";
        var text = value as string;
        if (text != null)
        {
            var escaped = new StringBuilder("\"");
            foreach (var character in text)
            {
                if (character == '"') escaped.Append("\\\"");
                else if (character == '\\') escaped.Append("\\\\");
                else if (character < ' ') escaped.Append("\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else escaped.Append(character);
            }
            return escaped.Append('"').ToString();
        }
        if (value is bool) return (bool)value ? "true" : "false";
        var formattable = value as IFormattable;
        if (formattable != null) return formattable.ToString(null, CultureInfo.InvariantCulture);
        var sequence = value as IEnumerable;
        if (sequence != null) return "[" + string.Join(",", sequence.Cast<object>().Select(Json).ToArray()) + "]";
        if (value.GetType().DeclaringType != typeof(NativeSummonIconInventoryProbe))
            throw new InvalidOperationException("Unexpected inventory record type: " + value.GetType().FullName);
        return "{" + string.Join(",", value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(field => field.MetadataToken).Select(field => Json(field.Name) + ":" + Json(field.GetValue(value))).ToArray()) + "}";
    }
}
