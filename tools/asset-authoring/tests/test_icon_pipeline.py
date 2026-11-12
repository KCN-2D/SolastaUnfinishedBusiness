import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageDraw

spec = importlib.util.spec_from_file_location("icon_pipeline", Path(__file__).parents[1] / "icon_pipeline.py")
pipeline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pipeline)


class IconPipelineTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.native = pipeline.native_reference("HoldPerson")
        self.capture = self.root / "capture.json"
        self.contract = self.root / "contract.json"
        self.brief = self.root / "brief.json"
        self.add_capture([self.native], [(128, 128)])
        self.data = {
            "subject": "transformation", "composition": "single side profile",
            "artDirection": "Use flat reference silhouettes and broad negative spaces.",
            "outputSize": [128, 128], "transparentBackground": False, "references": [self.native],
            "usages": [{"id": "selection", "role": "spell selection", "screen": "SpellSelectionPanel",
                        "binding": "SpellBox.Bind", "size": [64, 64], "preserveAspect": True,
                        "evidence": [str(Path(pipeline.__file__).resolve())],
                        "states": [{"name": "normal", "tint": [255, 255, 255, 255],
                                    "background": [20, 25, 35, 255]}]}]}
        pipeline.write_json(self.contract, self.data)

    def add_capture(self, refs, sizes):
        rows = []
        for index, (native, size) in enumerate(zip(refs, sizes)):
            path = self.root / f"native{index}.png"
            Image.new("RGBA", size, "white").save(path)
            rows.append(native | {"path": str(path), "width": size[0], "height": size[1]})
        pipeline.write_json(self.capture, {"status": "PASS_NATIVE_ICON_REFERENCES", "images": rows})

    def prepare(self):
        pipeline.write_json(self.contract, self.data)
        return pipeline.prepare(self.capture, self.contract, self.brief)

    def generated(self, size=(1024, 1024), transparent=False):
        source = self.root / "generated.png"
        image = Image.new("RGBA", size, (0, 0, 0, 0) if transparent else "white")
        if transparent:
            ImageDraw.Draw(image).rectangle((size[0] // 4, size[1] // 4, size[0] * 3 // 4, size[1] * 3 // 4), fill="white")
        image.save(source)
        return source

    def test_rejects_non_native_sprite(self):
        data = pipeline.read_json(self.capture)
        data["images"][0]["assetGuid"] = "0" * 32
        pipeline.write_json(self.capture, data)
        with self.assertRaisesRegex(ValueError, "identity mismatch"):
            self.prepare()

    def test_mixed_definition_types_and_non_gui_sprite_properties(self):
        selectors = [
            ("CircleBalance", "CharacterSubclassDefinition", None),
            ("Archery", "FightingStyleDefinition", None),
            ("Shove", "ActionDefinition", None),
            ("StunningStrikeToggle", "ActionDefinition", None),
            ("PowerFighterSecondWind", "FeatureDefinitionPower", None),
            ("ConditionRestrained", "ConditionDefinition", None),
            ("ProxyArcaneSword", "EffectProxyDefinition", ["portraitSpriteReference"]),
        ]
        refs = [pipeline.native_reference(name, kind, sprite_path=path) for name, kind, path in selectors]
        self.add_capture(refs, [(256, 256), (256, 256), (80, 80), (80, 80), (256, 128), (32, 32), (128, 192)])
        self.data["references"] = refs
        brief = self.prepare()
        self.assertEqual(len(brief["references"]), 7)
        pipeline.write_json(self.root / "spec.json", {"references": refs})
        pipeline.capture_source(self.root / "spec.json", self.root / "Native.cs")
        source = (self.root / "Native.cs").read_text()
        self.assertIn("class NativeIconReferences", source)
        self.assertIn("portraitSpriteReference", source)
        self.assertIn("void Observe(Image image", source)
        self.assertIn('["status"]="PASS_NATIVE_ICON_REFERENCES"', source)
        self.assertNotIn("__ICON_SOURCE_ROWS__", source)

    def test_definition_kind_does_not_decide_output_shape_or_style(self):
        self.native = pipeline.native_reference("ConditionRestrained", "ConditionDefinition")
        self.add_capture([self.native], [(32, 32)])
        self.data.update(references=[self.native], outputSize=[192, 96], transparentBackground=True,
                         artDirection="A monochrome horizontal glyph, without magical glow.")
        self.data["usages"][0]["size"] = [96, 48]
        brief = self.prepare()
        self.assertEqual(brief["contract"]["outputSize"], [192, 96])
        self.assertNotIn("spell icon", brief["prompt"])
        source = self.generated((1024, 512), transparent=True)
        pipeline.finish(self.brief, source, self.root / "out.png", self.root / "review.png")
        with Image.open(self.root / "out.png") as image:
            self.assertEqual(image.size, (192, 96))

    def test_catalog_discovers_lists_aliases_and_names_with_punctuation(self):
        root = self.root / "Blueprints"
        folder = root / "ItemDefinition"
        folder.mkdir(parents=True)
        sprite = {"$type": "UnityEngine.AddressableAssets.AssetReferenceSprite, Unity.Addressables",
                  "m_AssetGUID": "a" * 32, "m_SubObjectName": "Sword"}
        pipeline.write_json(folder / "Sword+1.json",
                            {"$type": "ItemDefinition, Assembly-CSharp",
                             "presentations": [{"sprite": sprite}, {"sprite": sprite}]})
        pipeline.catalog(root, self.root / "catalog.json")
        data = pipeline.read_json(self.root / "catalog.json")
        self.assertEqual(len(data["references"]), 2)
        self.assertEqual(data["references"][0]["spritePath"], ["presentations", 0, "sprite"])
        self.assertEqual(len(data["sharedSprites"]), 1)

    def test_rejects_definition_traversal_and_non_sprite_property(self):
        with self.assertRaisesRegex(ValueError, "Invalid icon definition"):
            pipeline.native_reference("ConditionRestrained", "../SpellDefinition")
        with self.assertRaisesRegex(ValueError, "not an AssetReferenceSprite"):
            pipeline.native_reference("HoldPerson", sprite_path=["guiPresentation"])

    def test_rejects_pinned_reference_changed_after_generation(self):
        brief = self.prepare()
        Image.new("RGBA", (128, 128), "black").save(brief["references"][0]["path"])
        with self.assertRaisesRegex(ValueError, "reference changed"):
            pipeline.finish(self.brief, self.generated(), self.root / "out.png", self.root / "review.png")

    def test_brief_survives_retirement_of_capture_and_original_blueprint_copy(self):
        brief = self.prepare()
        (self.root / "native0.png").unlink()
        self.capture.unlink()
        self.contract.unlink()
        source = self.generated()
        pipeline.finish(self.brief, source, self.root / "out.png", self.root / "review.png")
        self.assertTrue(Path(brief["references"][0]["path"]).is_file())

    def test_finish_preserves_source_and_pins_each_native_ui_usage(self):
        second = json.loads(json.dumps(self.data["usages"][0]))
        second.update(id="tooltip", role="tooltip header", screen="TooltipPanel", size=[32, 24])
        second["states"] = [{"name": "disabled", "tint": [128, 128, 128, 128], "background": [90, 80, 70, 255]}]
        self.data["usages"].append(second)
        self.prepare()
        source = self.generated()
        original = pipeline.digest(source)
        target, review = self.root / "icon.png", self.root / "review.png"
        pipeline.finish(self.brief, source, target, review)
        self.assertEqual(pipeline.digest(source), original)
        report = pipeline.read_json(review.with_suffix(".json"))
        self.assertEqual(len(report["usages"]), 2)
        self.assertIn("requires-visual-and-native-ui-review", report["status"])

    def test_preview_applies_native_tint_alpha_and_aspect(self):
        image = Image.new("RGBA", (8, 4), "white")
        usage = {"size": [8, 8], "preserveAspect": True}
        state = {"tint": [128, 64, 32, 128], "background": [0, 0, 0, 255]}
        tile = pipeline.preview(image, usage, state)
        self.assertEqual(tile.getpixel((0, 0)), (0, 0, 0, 255))
        self.assertEqual(tile.getpixel((0, 2)), (64, 32, 16, 255))

    def test_review_keeps_long_usage_labels_visible_at_the_actual_icon_size(self):
        self.data["usages"][0]["id"] = "native-usage-" + "A" * 150
        self.prepare()
        review = self.root / "review.png"
        pipeline.finish(self.brief, self.generated(), self.root / "out.png", review)
        label = self.data["usages"][0]["id"] + "/normal"
        with Image.open(review) as image:
            self.assertGreaterEqual(image.width, 250 + ImageDraw.Draw(image).textlength(label) + 16)

    def test_rejects_source_overwrite_aspect_mismatch_and_opaque_glyph(self):
        self.data["transparentBackground"] = True
        self.prepare()
        source = self.generated()
        with self.assertRaisesRegex(ValueError, "Preserve"):
            pipeline.finish(self.brief, source, source, self.root / "review.png")
        with self.assertRaisesRegex(ValueError, "transparent"):
            pipeline.finish(self.brief, source, self.root / "out.png", self.root / "review.png")
        with self.assertRaisesRegex(ValueError, "aspect"):
            pipeline.finish(self.brief, self.generated((512, 256)), self.root / "out.png", self.root / "review.png")

    def test_requires_traceable_ui_binding_and_explicit_art_direction(self):
        self.data["usages"][0]["evidence"] = []
        with self.assertRaisesRegex(ValueError, "Trace each UI binding"):
            self.prepare()
        self.data["artDirection"] = ""
        with self.assertRaisesRegex(ValueError, "artDirection"):
            self.prepare()


if __name__ == "__main__":
    unittest.main()
