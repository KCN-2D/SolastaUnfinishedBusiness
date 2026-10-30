"""Pure naming/path contracts; no Blender, Unity, files, or game scenarios."""
import importlib.util
from pathlib import Path
import sys
import unittest

_PATH = Path(__file__).resolve().parents[1] / 'asset_names.py'
_SPEC = importlib.util.spec_from_file_location('asset_names', _PATH)
names = importlib.util.module_from_spec(_SPEC)
sys.modules[_SPEC.name] = names
_SPEC.loader.exec_module(names)


class ResourceNameContracts(unittest.TestCase):
    def test_native_label_and_opaque_identity_are_separate(self):
        key = 'a' * 64
        result = names.resource_names([names.ResourceName(key, 'Assets/Characters/Source Style.prefab', suffix='Color')], '.png')
        self.assertEqual(result[key], 'Source_Style_Color.png')
        self.assertNotIn(key, result[key])

    def test_semantic_mesh_parts_resolve_a_shared_style(self):
        rows = [names.ResourceName('source-b', 'Source_Style', 'Source_PartB', 'LOD0'),
                names.ResourceName('source-a', 'Source_Style', 'Source_PartA', 'LOD0')]
        expected = {'source-a': 'Source_Style_PartA_LOD0.binding.json',
                    'source-b': 'Source_Style_PartB_LOD0.binding.json'}
        self.assertEqual(names.resource_names(rows, '.binding.json'), expected)
        self.assertEqual(names.resource_names(reversed(rows), '.binding.json'), expected)

    def test_casefold_collision_uses_semantic_details(self):
        rows = [names.ResourceName('one', 'Style', 'PartA'), names.ResourceName('two', 'style', 'PartB')]
        result = names.resource_names(rows, '.bundle', lowercase=True)
        self.assertEqual(result, {'one': 'style_parta.bundle', 'two': 'style_partb.bundle'})

    def test_remaining_variants_are_deterministic(self):
        rows = [names.ResourceName('b', 'Style'), names.ResourceName('a', 'Style')]
        expected = {'a': 'Style_Variant01.png', 'b': 'Style_Variant02.png'}
        self.assertEqual(names.resource_names(rows, '.png'), expected)
        self.assertEqual(names.resource_names(reversed(rows), '.png'), expected)

    def test_variants_do_not_take_a_declared_singleton(self):
        rows = [names.ResourceName('a', 'Style'), names.ResourceName('b', 'Style'),
                names.ResourceName('c', 'Style_Variant01')]
        result = names.resource_names(rows, '.png')
        self.assertEqual(len({value.casefold() for value in result.values()}), 3)
        self.assertEqual(result['c'], 'Style_Variant01.png')

    def test_duplicate_keys_and_invalid_labels_reject(self):
        with self.assertRaises(ValueError):
            names.resource_names([names.ResourceName('one', 'A'), names.ResourceName('one', 'B')])
        for value in ('', '..', 'CON', 'LPT1'):
            with self.subTest(value=value), self.assertRaises(ValueError):
                names.source_label(value)

    def test_component_limit_includes_extension(self):
        self.assertEqual(len(names.resource_names([names.ResourceName('a', 'x' * 251)], '.png')['a']), 255)
        with self.assertRaises(ValueError):
            names.resource_names([names.ResourceName('a', 'x' * 252)], '.png')

    def test_path_budget_counts_meta_and_utf16(self):
        root = str(Path(__file__).resolve().parent)
        value = root + '/' + 'x' * (254 - len(root) - 1)
        self.assertEqual(names.assert_path_budget(value, 259, '.meta'), value)
        with self.assertRaises(ValueError):
            names.assert_path_budget(value + 'x', 259, '.meta')
        with self.assertRaises(ValueError):
            names.assert_path_budget(value + '\U0001F600', 260, '.meta')
        self.assertFalse(Path(value).exists())

    def test_invalid_extension_or_relative_budget_reject(self):
        with self.assertRaises(ValueError):
            names.resource_names([names.ResourceName('a', 'Style')], '/../file')
        with self.assertRaises(ValueError):
            names.assert_path_budget('relative/file.png', 259)


if __name__ == '__main__':
    unittest.main()
