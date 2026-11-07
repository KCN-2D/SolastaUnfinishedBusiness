import sys
import json
from pathlib import Path
import bpy
import numpy as np
sys.path.insert(0, str(Path(__file__).parent))
from production import write_image

destination = Path(__file__).resolve().parents[2] / 'asset-authoring/workspace/FaceRefinement/PackedImageControl'
destination.mkdir(parents=True, exist_ok=True)
rows = []
for srgb in (False, True):
    for alpha in (.03, .35, 1.):
        data = np.tile([.18, .42, .81, alpha], (4, 4, 1)).astype(np.float32)
        path = destination / ('data-' + str(int(srgb)) + '-' + str(alpha) + '.png')
        image = write_image(path, data, srgb)
        bpy.data.images.remove(image)
        reloaded = bpy.data.images.load(str(path), check_existing=False)
        reloaded.colorspace_settings.name = 'Non-Color'; reloaded.alpha_mode = 'CHANNEL_PACKED'
        values = np.empty(64, dtype=np.float32); reloaded.pixels.foreach_get(values)
        expected = data.copy()
        if srgb:
            rgb = expected[:, :, :3]
            expected[:, :, :3] = np.where(rgb <= .0031308, rgb * 12.92, 1.055 * np.power(rgb, 1 / 2.4) - .055)
        error = float(np.max(np.abs(values.reshape(4, 4, 4) - expected)))
        rows.append(dict(srgb=srgb, alpha=alpha, maximumError=error, passed=error <= 1 / 255))
        if error > 1 / 255:
            raise ValueError('Packed RGB/alpha were changed during PNG save/reload: ' + str(rows[-1]))
(destination / 'Result.json').write_text(json.dumps(dict(status='PASS', controls=rows), indent=2), encoding='utf-8')
print(json.dumps(dict(status='PASS', controls=len(rows), maximumError=max(r['maximumError'] for r in rows))))
