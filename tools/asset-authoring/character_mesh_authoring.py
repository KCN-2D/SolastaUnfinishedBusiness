"""Carry Blender-authored character surfaces through the Unity mesh worker."""
import argparse
from collections import Counter, defaultdict
from functools import lru_cache
import gzip
import json
import math
from pathlib import Path
import re
import shutil
import sys

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'blender-models/CharacterFaces'))
import build_models as models
import character_resources as resources
from mesh_recipe_codec import encode_recipe, source_surface_binding
from resource_storage import resource_work


def pin(path):
    path = models.checked_source_path(path)
    return dict(path=str(path), sha256=models.sha(path))


def checked(reference):
    path = Path(reference['path'])
    if pin(path) != reference:
        raise ValueError(f'An authoring input changed: {path}')
    return path


def update_face(packet, positions, triangles, surface, sculpt_normals=None):
    """Transfer the authored neutral surface while protecting original contact vertices."""
    original = np.asarray(packet['vertices'], dtype='<f4').reshape(-1, 3)
    native_triangles = np.concatenate(packet['subMeshes']).reshape(-1, 3)
    if original.shape != positions.shape or not np.array_equal(native_triangles, triangles):
        raise ValueError('A Blender face must retain the source vertex and submesh correspondence.')
    moved = np.any(positions != original, axis=1)
    if not np.any(moved) and sculpt_normals is None:
        return packet
    # Original and authored coordinates use the same native renderer frame.
    _, orientation = surface.preserve_surface_orientation(original, positions, triangles, np.eye(4))
    if orientation['surfaceScaleFactor'] != 1:
        raise ValueError('The Blender face reverses a native triangle.')
    normals = np.asarray(packet['normals'], dtype='<f4').reshape(-1, 3)
    tangents = np.asarray(packet['tangents'], dtype='<f4').reshape(-1, 4)
    if sculpt_normals is None:
        authored_normals, authored_tangents = surface.surface_basis(original, positions, normals, tangents,
                                                                   triangles, np.eye(4), moved, True)
        authored_normals[~moved] = normals[~moved]
        authored_tangents[~moved] = tangents[~moved]
    else:
        authored_normals = sculpt_normals
        if (authored_normals.shape != normals.shape or not np.isfinite(authored_normals).all()
                or not np.allclose(np.linalg.norm(authored_normals, axis=1), 1., atol=2e-4)):
            raise ValueError('Blender must supply a unit shading normal per vertex.')
        changed_basis = np.linalg.norm(authored_normals - normals, axis=1) > 1e-6
        before, after = surface.normalise(normals[changed_basis]), authored_normals[changed_basis]
        authored_tangents = tangents.copy()
        authored_tangents[changed_basis, :3] = surface.rotate_between(tangents[changed_basis, :3], before, after)
        shapes = []
        for shape in packet['shapes']:
            frames = []
            for frame in shape['frames']:
                offsets = {}
                for key in ('normals', 'tangents'):
                    values = np.asarray(frame[key]).reshape(-1, 3)
                    values[changed_basis] = surface.rotate_between(values[changed_basis], before, after)
                    offsets[key] = values.ravel().tolist()
                frames.append(dict(frame, **offsets))
            shapes.append(dict(shape, frames=frames))
        packet = dict(packet, shapes=shapes)
    result = dict(packet, vertices=positions.ravel().tolist(), normals=authored_normals.ravel().tolist(),
                  tangents=authored_tangents.ravel().tolist())
    result['bounds'] = surface.physical_mesh_bounds(result)
    return result


def update_face_uv(packet, art, surface, source_slot):
    """Split an authored skin chart while retaining its physical topology and rig."""
    source_count = packet['vertexCount']
    alias = art['source_vertices']
    positions = np.asarray(packet['vertices'], dtype='<f4').reshape(-1, 3)
    if (not np.array_equal(alias[:source_count], np.arange(source_count)) or
            np.any((alias < 0) | (alias >= source_count)) or not np.array_equal(positions[alias], art['positions'])):
        raise ValueError('A Blender UV chart moved or renumbered the source surface.')
    triangles, slots = art['triangles'], art['material_slots']
    native_slots = packet['subMeshes']
    added_slot = len(native_slots)
    if not 0 <= source_slot < added_slot or not np.any(slots == added_slot) or np.any((slots < 0) | (slots > added_slot)):
        raise ValueError('A skin chart needs one appended material slot from its declared source.')
    for index, original in enumerate(native_slots):
        selected = (slots == index) | ((index == source_slot) & (slots == added_slot))
        if Counter(map(tuple, np.asarray(original).reshape(-1, 3))) != Counter(map(tuple, alias[triangles[selected]])):
            raise ValueError('A Blender UV chart changed physical triangles or material ownership.')
    result = dict(packet, vertexCount=len(alias),
                  subMeshes=[triangles[slots == slot].ravel().tolist() for slot in range(added_slot + 1)])
    for key, width in (('vertices', 3), ('normals', 3), ('tangents', 4), ('weights', 4), ('boneIndices', 4), ('colors', 4)):
        if packet[key]:
            result[key] = np.asarray(packet[key]).reshape(-1, width)[alias].ravel().tolist()
    result['uv'] = []
    for channel in packet['uv']:
        values = np.asarray(channel['values'], dtype='<f4').reshape(-1, 4)[alias]
        if channel['channel'] == 0:
            values[source_count:, :2] = art['uv0'][source_count:]
        result['uv'].append(dict(channel, values=values.ravel().tolist()))
    normals = np.asarray(result['normals']).reshape(-1, 3)
    authored = art.get('skin_normals', normals)
    tangents = np.asarray(result['tangents']).reshape(-1, 4)
    moved = np.any(authored != normals, axis=1)
    tangents[moved, :3] = surface.rotate_between(tangents[moved, :3], normals[moved], authored[moved])
    tangents[source_count:] = art['tangents'][source_count:]
    result['normals'] = authored.ravel().tolist()
    result['tangents'] = tangents.ravel().tolist()
    result['shapes'] = []
    for shape in packet['shapes']:
        frames = []
        for frame in shape['frames']:
            offsets = {key: np.asarray(frame[key]).reshape(-1, 3)[alias] for key in ('vertices', 'normals', 'tangents')}
            delta = offsets['normals']
            delta[moved] = surface.rotate_between(normals[moved] + delta[moved], normals[moved], authored[moved]) - authored[moved]
            posed = surface.normalise(authored + delta)
            offsets['tangents'][source_count:] = surface.rotate_between(tangents[source_count:, :3],
                authored[source_count:], posed[source_count:]) - tangents[source_count:, :3]
            frames.append(dict(frame, **{key: values.ravel().tolist() for key, values in offsets.items()}))
        result['shapes'].append(dict(shape, frames=frames))
    return result


def source_edge_stencils(packet, inserted):
    original_edges = {}
    for slot in packet['subMeshes']:
        for triangle in np.asarray(slot, dtype=int).reshape(-1, 3).tolist():
            for a, b in zip(triangle, triangle[1:] + triangle[:1]):
                original_edges.setdefault(tuple(sorted((a, b))), triangle)
    samples = []
    for vertex in inserted:
        edge = tuple(sorted(vertex['edge']))
        triangle = original_edges.get(edge)
        if triangle is None:
            raise ValueError('A refined vertex lost its original skin triangle.')
        samples.append(dict(vertex=vertex['index'], surface=dict(triangle=triangle,
                            barycentric=[.5 if anchor in edge else 0. for anchor in triangle])))
    return samples


def refine_authored_surface(packet, refinement, surface):
    refined, inserted, _ = surface.refine_skin_packet(packet, refinement['rendererToPrefab'],
        refinement['lod'], profile=refinement['profile'], vertex_ownership=refinement['ownership'])
    return refined, source_edge_stencils(packet, inserted)


def stage_material_maps(recipe, maps):
    staged = []
    for texture in maps:
        original = models.checked_source_path(texture['path'])
        name = texture['name']
        if not name or Path(name).name != name or name in ('.', '..'):
            raise ValueError('A material image needs a texture asset name without a directory.')
        path = recipe.parent / 'Textures' / (name + original.suffix.lower())
        path.parent.mkdir(exist_ok=True, parents=True)
        shutil.copyfile(original, path)
        digest = models.sha(path)
        if 'sha256' in texture and digest != texture['sha256']:
            raise ValueError(f'A source material image changed: {original}')
        staged.append(dict(name=name, path=path.relative_to(recipe.parent).as_posix(), sha256=digest,
                           normalMap=texture.get('normalMap', False), sRGB=texture['sRGB'], mipmaps=True))
    return staged


def native_groom_mesh(current, identifier, row, surface, load_installed_bundle):
    """Carry a groom surface through its installed UVs, skin and cloth domain."""
    origin = row['nativeGroom']
    reference = {key: origin[key] for key in ('path', 'sha256')}
    original = load_installed_bundle(**reference)
    mesh = original.objects[origin['pathId']]
    if mesh.type.name != 'Mesh' or row['firstVertex'] != 0:
        raise ValueError('A native groom export must own the complete installed mesh.')
    renderer = origin['rendererPathId']
    renderer_reference = origin.get('rendererBundle', reference)
    renderer_file = load_installed_bundle(**renderer_reference)
    before, after = renderer_file.objects[renderer], current.objects[renderer]
    old_skin, new_skin = before.read_typetree(), after.read_typetree()
    original_pointer = old_skin['m_Mesh']
    external = original_pointer['m_FileID']
    original_owner = (renderer_file.name if external == 0 else
                      Path(renderer_file.externals[external - 1].path).name)
    if (before.type.name != 'SkinnedMeshRenderer' or after.type.name != before.type.name or
            original_owner != original.name or original_pointer['m_PathID'] != mesh.path_id or
            new_skin['m_Mesh'] != dict(m_FileID=0, m_PathID=identifier) or
            any(old_skin[field] != new_skin[field] for field in ('m_Bones', 'm_RootBone'))):
        raise ValueError('The native groom requires its original renderer and skin palette.')
    native = models.export_current_mesh(mesh)
    retained = models.export_current_mesh(current.objects[identifier])
    refinement = row.get('refinement')
    basis, stencils = refine_authored_surface(native, refinement, surface) if refinement else (native, [])
    with np.load(checked(row['geometry']), allow_pickle=False) as art:
        authored = update_face(basis, art['positions'], art['triangles'], surface, art.get('skin_normals'))
    if len(authored['subMeshes']) != len(old_skin['m_Materials']) or len(new_skin['m_Materials']) < len(authored['subMeshes']):
        raise ValueError('The native groom material slots do not agree with its renderer.')
    # Extra material slots from earlier groom layers must have empty submeshes.
    # Unity otherwise draws the final visible submesh again for each extra material.
    triangles = list(authored['subMeshes']) + [[] for _ in range(len(new_skin['m_Materials']) - len(authored['subMeshes']))]
    uv = {item['channel']: item for item in authored['uv']}
    main = np.asarray(uv[0]['values'], dtype='<f4').reshape(-1, 4).copy()
    main[:, 2:] = 0.
    flow = main.copy()
    flow[:, 3] = np.asarray(authored['tangents'], dtype='<f4').reshape(-1, 4)[:, 3]
    uv[0] = dict(channel=0, dimension=4, values=main.ravel().tolist())
    uv[2] = dict(channel=2, dimension=4, values=flow.ravel().tolist())
    authored = dict(authored, uv=[uv[key] for key in sorted(uv)], subMeshes=triangles)
    binding = dict(sourceFingerprint=retained['sourceFingerprint'], sourcePathIds=[identifier], binding=None)
    for component in current.objects.values():
        if component.type.name != 'MonoBehaviour':
            continue
        deformer = component.read_typetree().get('deformer')
        if not isinstance(deformer, dict) or deformer.get('sharedMesh') != dict(m_FileID=0, m_PathID=identifier):
            continue
        binding['nativeClothSource'] = dict(reference, pathId=mesh.path_id)
        if authored['vertices'] != native['vertices'] or authored['normals'] != native['normals']:
            frame = np.linalg.inv(np.asarray(row['worldToUnity'])).T.ravel().tolist()
            binding['binding'] = source_surface_binding(native, authored, frame, stencils)
        break
    return models.recipe_mesh(dict(authored, name=row['name'])), binding, (reference, renderer_reference)


def prepare(args, source, output):
    manifest = models.read(source / 'Catalog' / models.RESOURCE_FILES['manifest'])
    available = models.catalog_payloads(manifest)
    surface, _ = models.source_owner()

    @lru_cache(maxsize=8)
    def load_installed_bundle(path, sha256):
        return resources.load_serialized_bundle(checked(dict(path=path, sha256=sha256)), in_memory=True)[1]

    reports = []
    for path in args.blender_export:
        files = sorted(path.glob('*/BlenderExport.json')) if path.is_dir() else [path]
        if path.is_dir() and (path / 'BlenderExport.json').is_file():
            files.append(path / 'BlenderExport.json')
        if not files:
            raise ValueError(f'No Blender character mesh exports in {path}.')
        reports.extend((pin(file), models.read(file)) for file in files)
    charts = {}
    for path in args.skin_chart:
        reference = pin(path)
        chart = models.read(path)
        if chart['bundle'] in charts:
            raise ValueError('Each character bundle can have one skin chart plan.')
        charts[chart['bundle']] = reference, chart
    if charts.keys() - {report['bundle'] for _, report in reports}:
        raise ValueError('Each skin chart needs a matching Blender export.')
    native_finishes = {}
    if args.native_groom_finishes:
        native_finish_pin = pin(args.native_groom_finishes)
        document = models.read(checked(native_finish_pin))
        if document.get('schemaVersion') != 1 or not isinstance(document.get('materials'), dict):
            raise ValueError('A native groom finish requires a versioned material map.')
        native_finishes = document['materials']
        native_exports = {report['bundle'] for _, report in reports
                          if any('nativeGroom' in row for row in report['meshes'])}
        if native_finishes.keys() - native_exports:
            raise ValueError('Each finish needs a matching native groom export.')
    plan = {}
    for report_ref, report in reports:
        name = report['bundle']
        if name not in available or name in plan or report['status'] != 'PASS_BLENDER_CHARACTER_MESH_EXPORT':
            raise ValueError(f'The Blender export does not own a unique character bundle: {name}')
        native_path = models.checked_catalog_path(source / 'Catalog', name)
        if models.sha(native_path) != available[name]:
            raise ValueError(f'The source bundle differs from its catalog: {name}')
        _, native = resources.load_serialized_bundle(native_path, in_memory=True)
        rows = {row['pathId']: row for row in report['meshes']}
        face_ids = resources.face_mesh_identifiers(native)
        native_groom = any('nativeGroom' in row for row in rows.values())
        groom_ids = resources.groom_mesh_identifiers(native, include_native_lods=native_groom)
        faces = face_ids & rows.keys()
        hair = groom_ids & rows.keys()
        if (not rows or len(rows) != len(report['meshes']) or set(rows) != faces | hair or
                (faces and faces != face_ids) or (hair and hair != groom_ids)):
            raise ValueError(f'The Blender export requires every LOD of the selected surfaces: {name}')
        recipe = output / 'UnityInput' / Path(name).stem / 'MeshRecipe.json'
        meshes, textures, skin_slots, face_stencils = [], [], [], {}
        chart_ref, chart = charts.get(name, (None, None))
        layouts = {row['pathId']: row for row in chart['meshes'] if row['collar']} if chart else {}
        if chart and (not layouts or layouts.keys() - faces):
            raise ValueError('A skin chart requires an authored face mesh and its declared material source.')
        for identifier in sorted(faces):
            row = rows[identifier]
            if row['firstVertex'] != 0:
                raise ValueError('A face export must contain the complete surface.')
            packet = models.export_current_mesh(native.objects[identifier])
            with np.load(checked(row['geometry']), allow_pickle=False) as art:
                refinement = row.get('refinement')
                if refinement:
                    if identifier in layouts:
                        raise ValueError('Finish the refined face surface before adding its collar chart.')
                    packet, face_stencils[str(identifier)] = refine_authored_surface(packet, refinement, surface)
                if identifier in layouts:
                    layout = layouts[identifier]['collar']
                    face = update_face_uv(packet, art, surface, layout['sourceSlot'])
                    skin_slots.append(dict(meshPathId=identifier, renderer=layouts[identifier]['name'], sourceSlot=layout['sourceSlot'],
                                           targetSlot=len(packet['subMeshes']), maps=layout['maps']))
                    textures.extend(stage_material_maps(recipe, layout['maps']))
                else:
                    face = update_face(packet, art['positions'], art['triangles'], surface, art.get('skin_normals'))
            meshes.append(models.recipe_mesh(dict(face, name=row['name'])))
        job = dict(blenderReport=report_ref, source=pin(native_path))
        if skin_slots:
            job.update(skinChart=chart_ref, skinSlots=skin_slots)
        if face_stencils:
            job['faceStencils'] = face_stencils
        if hair:
            if any('nativeGroom' not in rows[identifier] for identifier in hair):
                raise ValueError('A native groom build requires the installed surface of every groom renderer.')
            bindings, source_inputs = [], []
            for identifier in sorted(hair):
                recipe_mesh, binding, original_sources = native_groom_mesh(
                    native, identifier, rows[identifier], surface, load_installed_bundle)
                meshes.append(recipe_mesh)
                bindings.append(binding)
                for reference in original_sources:
                    if reference not in source_inputs:
                        source_inputs.append(reference)
            material_updates = []
            # Soften the broad highlights and recover the feathered edges
            # already represented by the source coverage map.
            finish = dict(_Cutoff=0.44, _CoreResponse=0.68, _CoreFineStrength=0.17, _CoreGroomPigmentStrength=0.,
                          _CoreReliefStrength=0., _FiberColorStrength=0., _BakedColorDetail=1.,
                          _BakedColorMipBias=0., _NativePigmentMean=0., _MacroNormalStrength=0.94,
                          _NativeGrooveStrength=1.45, _FiberSmoothness=0.44, _CoreSmoothnessScale=0.56,
                          _FlowInVertexFrame=0., _SupportInVertexFrame=0., _UBSurfaceProjection=0.)
            if name in native_finishes:
                by_bundle = native_finishes[name]
                fixed = {'_FlowInVertexFrame', '_SupportInVertexFrame', '_UBSurfaceProjection'}
                if (not isinstance(by_bundle, dict) or not by_bundle or by_bundle.keys() - (finish.keys() - fixed)
                        or any(type(value) not in (int, float) or not math.isfinite(value) for value in by_bundle.values())):
                    raise ValueError(f'Unsupported native groom finish properties: {name}')
                finish.update(by_bundle)
                job['nativeGroomFinish'] = native_finish_pin
            for identifier in resources.groom_material_identifiers(native):
                floats = dict(native.objects[identifier].read_typetree()['m_SavedProperties']['m_Floats'])
                material_updates.append(dict(pathId=identifier, values={key: dict(before=floats.get(key), after=value)
                                                                         for key, value in finish.items()}))
            attachment = output / 'Bindings' / (name + '.json.gz')
            attachment.parent.mkdir(parents=True, exist_ok=True)
            with gzip.open(attachment, 'wt', encoding='utf-8') as stream:
                json.dump(dict(meshes=bindings, materialUpdates=material_updates), stream, separators=(',', ':'))
            job.update(groomBinding=pin(attachment), nativeGroomSources=source_inputs)
        recipe_contents = dict(schemaVersion=1, bundleName=name, meshes=meshes)
        if textures:
            recipe_contents['textures'] = textures
        encode_recipe(recipe_contents, recipe)
        plan[name] = dict(job, meshRecipe=pin(recipe))
    models.write(output / 'BuildPlan.json', plan)


def bind_part_textures(manifest, name, staged, mesh_receipt, slots, compiled, target, parameter_set):
    """Bind Unity-compiled textures to their added material slots in the prefab."""
    owners = {row['asset']: row for row in manifest['resources'] if row['type'] == 'GameObject'
              and row['bundles'][0]['file'] == name}
    environment, file = resources.load_serialized_bundle(staged, in_memory=True)
    paths = {}
    for asset in owners:
        prefab = resources.typed_bundle_asset(environment, asset, 'GameObject').deref()
        for path, renderer in resources.prefab_skin_renderers(prefab,
                lambda container, pointer: (container, container.objects[pointer['m_PathID']])):
            paths.setdefault(renderer.path_id, set()).add((asset, path))
    expected = {}
    for mesh, slot in slots.items():
        declared = slot.get('rendererOwners')
        if declared is None:
            if len(owners) != 1:
                raise ValueError('An authored material shared across prefabs must declare its renderer owners.')
            declared = [dict(prefab=next(iter(owners)), renderer=slot['renderer'])]
        expected[mesh] = {(row['prefab'], row['renderer']) for row in declared}
    observed = {mesh: set() for mesh in slots}
    updates, bindings = {}, {asset: [] for asset in owners}
    bundle = pin(compiled)
    for renderer in mesh_receipt['rendererMaterialUpdates']:
        declared = slots[renderer['mesh']]
        selected = paths.get(renderer['renderer'], set())
        added, = renderer['addedSlots']
        if not selected or selected - expected[renderer['mesh']] or added['targetSlot'] != declared['targetSlot']:
            raise ValueError('The added material belongs to another prefab renderer.')
        observed[renderer['mesh']].update(selected)
        material = added['materialPathId']
        properties = dict(file.objects[material].read_typetree()['m_SavedProperties']['m_TexEnvs'])
        for texture in declared['maps']:
            property_name = texture['property']
            updates[material, property_name] = dict(pathId=material, property=property_name,
                before=properties[property_name]['m_Texture'], texture=dict(bundle, name=texture['name']), identityUv=True)
        for asset, path in sorted(selected):
            bindings[asset].append(dict(renderer=path, slot=added['targetSlot'], set=parameter_set))
    if observed != expected:
        raise ValueError('The authored material omitted a prefab renderer.')
    receipt = resources.replace_owned_material_textures(staged, mesh_receipt['target']['sha256'], target, list(updates.values()))
    for asset, parameters in bindings.items():
        owners[asset]['parameters'].extend(parameters)
    return receipt


def bind_mesh_bundle(name, job, compiled_reference, recipe_sha, output, identity):
    """Compile one independent prefab from immutable Unity and Blender inputs."""
    original = checked(job['source'])
    compiled = checked(compiled_reference)
    if job['meshRecipe']['sha256'] != recipe_sha:
        raise ValueError(f'The Unity mesh build describes another recipe: {name}')
    slots = job.get('skinSlots', job.get('groomSlots', ()))
    mesh_target = models.checked_catalog_path(output / ('Mesh' if slots else 'Catalog'), name)
    receipt_path = models.checked_catalog_path(output / 'Receipts', name + '.json')
    report_path = checked(job['blenderReport'])
    groom_path = checked(job['groomBinding']) if 'groomBinding' in job else None
    evidence = dict(job=job, compiled=compiled_reference, producer=identity)
    if receipt_path.is_file() and mesh_target.is_file():
        previous = models.read(receipt_path)
        if previous.get('bindInputs') == evidence and previous['source'] == job['source'] and checked(previous['target']) == mesh_target:
            return receipt_path

    saved = resources.load_serialized_bundle(compiled, in_memory=True)[1]
    unity_meshes = {obj.peek_name(): obj for obj in saved.objects.values() if obj.type.name == 'Mesh'}
    native = resources.load_serialized_bundle(original, in_memory=True)[1]
    report = models.read(report_path)
    attached = {}
    material_updates = []
    if groom_path:
        with gzip.open(groom_path, 'rt', encoding='utf-8') as stream:
            binding = json.load(stream)
        attached = {row['sourcePathIds'][0]: row for row in binding['meshes']}
        material_updates = binding['materialUpdates']
    cloth_meshes = set()
    if not attached:
        for obj in native.objects.values():
            if obj.type.name != 'MonoBehaviour':
                continue
            deformer = obj.read_typetree().get('deformer')
            pointer = deformer.get('sharedMesh') if isinstance(deformer, dict) else None
            if pointer and pointer['m_FileID'] == 0:
                cloth_meshes.add(pointer['m_PathID'])
    replacements = []
    for row in report['meshes']:
        identifier = row['pathId']
        mesh = unity_meshes[row['name']]
        new = models.export_current_mesh(mesh)
        if identifier in attached:
            retained = attached[identifier]
        else:
            old = models.export_current_mesh(native.objects[identifier])
            retained = dict(sourceFingerprint=old['sourceFingerprint'], sourcePathIds=[identifier], binding=None)
            if (identifier in cloth_meshes and (old['vertexCount'] == new['vertexCount'] or 'refinement' in row)
                    and any(old[field] != new[field] for field in ('vertices', 'normals', 'tangents'))):
                transform = np.linalg.inv(np.asarray(row['worldToUnity'])).T.ravel().tolist()
                stencils = job.get('faceStencils', {}).get(str(identifier), [])
                retained['binding'] = source_surface_binding(old, new, transform, stencils)
        replacements.append(dict(retained, compiledTree=mesh.read_typetree(), packet=new))
    slot_updates = [dict(meshPathId=row['meshPathId'], slots=[dict(sourceSlot=row['sourceSlot'],
                    targetSlot=row['targetSlot'])]) for row in slots]
    receipt = resources.prepare_standalone_part(original, job['source']['sha256'], mesh_target,
                                                replacements, export_mesh=models.export_current_mesh,
                                                material_updates=material_updates, material_slot_updates=slot_updates)
    models.write(receipt_path, dict(receipt, file=name, bindInputs=evidence))
    return receipt_path


def bind(args, source, output):
    plan = models.read(output / 'BuildPlan.json')
    artifacts = {}
    for path in args.unity_report:
        report_pin = pin(path)
        unity = models.read(path)
        if not unity.get('success'):
            raise ValueError(f'The Unity character mesh build failed: {path}')
        for row in unity.get('bundles') or [unity]:
            digest = row['inputSha256']
            if not row.get('success'):
                raise ValueError(f'The Unity character mesh recipe did not pass readback: {digest}')
            if digest in artifacts:
                raise ValueError(f'A Unity character mesh recipe has more than one build report: {digest}')
            artifacts[digest] = row, report_pin
    missing = sorted(name for name, job in plan.items() if job['meshRecipe']['sha256'] not in artifacts)
    if missing:
        raise ValueError(f'Unity has not built these character bundles: {", ".join(missing)}')
    assemblies = source / 'ResourceAssembly.json'
    assembly = models.read(assemblies)
    manifest_key = Path('Catalog') / models.RESOURCE_FILES['manifest']
    manifest = models.read(source / manifest_key)
    proofs = {row['file']: row for row in assembly['proofs']}
    authoring = assembly.setdefault('characterMeshAuthoring', {})
    catalog = output / 'Catalog'
    catalog.mkdir(exist_ok=True)
    by_name = {entry['file']: entry for entry in manifest['bundles']}
    if plan.keys() - by_name.keys():
        raise ValueError('A Blender mesh has no resource in its source catalog.')
    identity = [pin(Path(path)) for path in
                (__file__, resources.__file__, models.__file__, sys.modules[source_surface_binding.__module__].__file__)]
    pending = {}
    for name, job in plan.items():
        original = models.checked_catalog_path(source / 'Catalog', name)
        if Path(job['source']['path']) != original or job['source']['sha256'] != by_name[name]['sha256']:
            raise ValueError(f'The Blender mesh candidate was prepared for another source: {name}')
        check, _ = artifacts[job['meshRecipe']['sha256']]
        pending[name] = (name, job, dict(path=check['bundlePath'], sha256=check['bundleSha256']),
                         check['inputSha256'], output, identity)

    receipts = {}
    failures = {}
    if args.workers == 1:
        receipts = {name: bind_mesh_bundle(*values) for name, values in pending.items()}
    else:
        from concurrent.futures import ProcessPoolExecutor, as_completed
        with ProcessPoolExecutor(max_workers=args.workers) as pool:
            tasks = {pool.submit(bind_mesh_bundle, *values): name for name, values in pending.items()}
            for future in as_completed(tasks):
                name = tasks[future]
                try:
                    receipts[name] = future.result()
                except Exception as error:
                    failures[name] = str(error)
                    print(f'Mesh binding failed for {name}: {error}', flush=True)
                if len(receipts) and len(receipts) % 16 == 0 and name in receipts:
                    print(f'Bound {len(receipts)}/{len(pending)} character meshes.', flush=True)
        if failures:
            models.write(output / 'BindingFailures.json', failures)
            raise ValueError('Character mesh binding failed. Finished bundles are available for retry.')
    if (failure_report := output / 'BindingFailures.json').is_file():
        failure_report.unlink()

    for entry in manifest['bundles']:
        name = entry['file']
        target = models.checked_catalog_path(catalog, name)
        original = models.checked_catalog_path(source / 'Catalog', name)
        if name not in plan:
            if not target.exists():
                target.hardlink_to(original)
            elif not target.samefile(original):
                raise ValueError(f'The candidate already contains a different resource: {name}')
            continue
        job = plan[name]
        check, report_pin = artifacts[job['meshRecipe']['sha256']]
        report = models.read(checked(job['blenderReport']))
        part_slots = {row['meshPathId']: row for row in job.get('skinSlots', job.get('groomSlots', ()))}
        receipt_path = receipts[name]
        receipt = models.read(receipt_path)
        declaration = dict(blenderReport=job['blenderReport'], meshRecipe=job['meshRecipe'], unityReport=report_pin)
        previous = authoring.get(name, [])
        if isinstance(previous, dict):
            previous = [previous]
        # One prefab can carry a face and separate groom renderers with their
        # own authoring scenes. A groom update must retain the face's proof.
        authored_meshes = {row['pathId'] for row in report['meshes']}
        inherited = [item for item in previous if any(row['pathId'] not in authored_meshes
            for row in models.read(checked(item['blenderReport']))['meshes'])]
        authoring[name] = [*inherited, declaration] if inherited else declaration
        proof = proofs[name]
        inputs = list(declaration.values())
        if 'groomBinding' in job:
            inputs.append(job['groomBinding'])
        if 'nativeGroomFinish' in job:
            checked(job['nativeGroomFinish'])
            inputs.append(job['nativeGroomFinish'])
        inputs.extend(job.get('nativeGroomSources', ()))
        if part_slots:
            material = job.get('skinChart', job.get('groomMaterial'))
            declaration = models.read(checked(material))
            inputs.extend([material, *declaration.get('sources', ())])
            for reference in declaration.get('sources', ()):
                checked(reference)
        proof.setdefault('appearanceFinishing', []).append(dict(kind='blender-character-mesh',
            beforeSha256=entry['sha256'], sha256=receipt['target']['sha256'], proof=pin(receipt_path),
            authority=dict(inputs=inputs)))
        entry['sha256'] = receipt['target']['sha256']
        added_objects = len(receipt['objectsAdded'])
        if part_slots:
            groom = 'groomSlots' in job
            compiled = checked(dict(path=check['bundlePath'], sha256=check['bundleSha256']))
            textures = bind_part_textures(manifest, name, Path(receipt['target']['path']), receipt, part_slots, compiled, target,
                                          'Groom' if groom else 'Skin')
            texture_receipt = output / 'Receipts' / (name + '.material-maps.json')
            models.write(texture_receipt, textures)
            kind = 'source-groom-material-bindings' if groom else 'source-skin-interface-materials'
            proof['appearanceFinishing'].append(dict(kind=kind, beforeSha256=entry['sha256'],
                sha256=textures['sha256'], proof=pin(texture_receipt)))
            entry['sha256'] = textures['sha256']
            added_objects += len(textures['objectsAdded'])
        proof.update(sha256=entry['sha256'], bytes=target.stat().st_size,
                     objects=proof['objects'] + added_objects,
                     allRetainedObjectFieldsExactAfterReferenceRebasing=False)
    manifest_path = output / manifest_key
    models.verify_character_mesh_authoring(manifest_path, manifest, assembly, source / 'Catalog', edited_bundles=plan.keys())
    models.write(manifest_path, manifest)
    assembly.update(catalogSha256=models.sha(manifest_path), bytes=sum(row['bytes'] for row in proofs.values()),
                    preparedSource=dict(assembly=pin(assemblies), catalog=pin(source / manifest_key)), artAccepted=False)
    assembly.pop('artAcceptance', None)
    models.write(output / 'ResourceAssembly.json', assembly)


def native_groom_lod_bindings(file, rows, open_native):
    """Match each occupied slot to the prepared material of its original LOD family."""
    prepared = resources.groom_material_identifiers(file)
    donors, targets = defaultdict(list), []
    installed = {}
    for row in rows:
        if 'nativeGroom' not in row:
            continue
        original = row['nativeGroom']
        renderer = original['rendererPathId']
        shape = re.fullmatch(r'(.+)_LOD(\d+)', row['name'], re.I)
        if shape is None:
            continue
        reference = original.get('rendererBundle', {key: original[key] for key in ('path', 'sha256')})
        native = open_native(reference['path'], reference['sha256'])
        installed[reference['path']] = reference
        original_slots = native.objects[renderer].read_typetree()['m_Materials']
        current_slots = file.objects[renderer].read_typetree()['m_Materials']
        current_mesh = file.objects[row['pathId']].read_typetree()
        for slot, pointer in enumerate(original_slots):
            if not current_mesh['m_SubMeshes'][slot]['indexCount'] or not pointer['m_PathID']:
                continue
            cab = native.name if not pointer['m_FileID'] else native.externals[pointer['m_FileID'] - 1].path
            key = (shape[1].casefold(), slot, cab, pointer['m_PathID'])
            current = current_slots[slot]
            if not current['m_FileID'] and current['m_PathID'] in prepared:
                donors[key].append((int(shape[2]), current['m_PathID'], current))
            else:
                targets.append((key, renderer, slot, current))
    result = []
    for key, renderer, slot, current in targets:
        if key not in donors:
            continue
        candidates = sorted(donors[key], key=lambda donor: donor[:2])
        material = file.objects[candidates[0][1]].read_typetree()
        if any(file.objects[identifier].read_typetree() != material for _, identifier, _ in candidates[1:]):
            raise ValueError('The source groom material has incompatible prepared LOD owners.')
        result.append(dict(renderer=renderer, slot=slot, before=current, after=candidates[0][2]))
    return result, list(installed.values())


def repair_native_groom_lods(source, output):
    """Restore groom material consistency through the published prefab and slot owners."""
    index = Path('Catalog') / models.RESOURCE_FILES['manifest']
    catalog_path = source / index
    assembly_path = source / 'ResourceAssembly.json'
    manifest, assembly = models.read(catalog_path), models.read(assembly_path)
    if output.exists() or assembly['catalogSha256'] != pin(catalog_path)['sha256']:
        raise ValueError('A groom material repair needs a complete source and new output.')
    proofs = {row['file']: row for row in assembly['proofs']}
    resources_by_bundle = defaultdict(list)
    for resource in manifest['resources']:
        if resource['type'] == 'GameObject':
            resources_by_bundle[resource['bundles'][0]['file']].append(resource)

    @lru_cache(maxsize=8)
    def open_native(path, sha256):
        return resources.load_serialized_bundle(checked(dict(path=path, sha256=sha256)), in_memory=True)[1]

    checked_bindings = {}
    for bundle, reports in assembly['characterMeshAuthoring'].items():
        reports = [reports] if isinstance(reports, dict) else reports
        for declaration in reports:
            report = models.read(checked(declaration['blenderReport']))
            if not any('nativeGroom' in row for row in report['meshes']):
                continue
            original = source / 'Catalog' / bundle
            environment, file = resources.load_serialized_bundle(original, in_memory=True)
            updates, installed = native_groom_lod_bindings(file, report['meshes'], open_native)
            if not updates:
                continue
            if bundle in checked_bindings:
                raise ValueError(f'A native groom LOD repair has multiple render authorities: {bundle}')
            missing = {(row['renderer'], row['slot']) for row in updates}
            covered = set()
            for resource in resources_by_bundle[bundle]:
                prefab = resources.typed_bundle_asset(environment, resource['asset'], 'GameObject').deref()
                for path, renderer in resources.prefab_skin_renderers(prefab, lambda owner, pointer:
                        (owner, owner.objects[pointer['m_PathID']])):
                    for number, slot in sorted(missing):
                        if renderer.path_id != number:
                            continue
                        parameter = dict(renderer=path, slot=slot, set='Groom')
                        match = [entry for entry in resource['parameters'] if entry['renderer'] == path and entry['slot'] == slot]
                        if match and match != [parameter]:
                            raise ValueError('A repaired groom slot already has another appearance parameter owner.')
                        if not match:
                            resource['parameters'].append(parameter)
                        covered.add((number, slot))
            if covered != missing:
                raise ValueError('A repaired groom renderer must belong to the declared prefab.')
            checked_bindings[bundle] = (updates, [declaration['blenderReport'], *installed])

    catalog = output / 'Catalog'
    catalog.mkdir(parents=True)
    for entry in manifest['bundles']:
        name = entry['file']
        original = source / 'Catalog' / name
        target = catalog / name
        if name not in checked_bindings:
            target.hardlink_to(original)
            continue
        updates, authority = checked_bindings[name]
        receipt = resources.prepare_standalone_part(original, entry['sha256'], target, [], export_mesh=None,
                                                    groom_slot_updates=updates)
        receipt_path = output / 'Receipts' / (name + '.json')
        models.write(receipt_path, dict(receipt, file=name))
        row = proofs[name]
        row.setdefault('appearanceFinishing', []).append(dict(kind='native-groom-lod-materials',
            beforeSha256=entry['sha256'], sha256=receipt['target']['sha256'], proof=pin(receipt_path),
            authority=dict(inputs=authority)))
        entry['sha256'] = receipt['target']['sha256']
        row.update(sha256=entry['sha256'], bytes=target.stat().st_size,
                   allRetainedObjectFieldsExactAfterReferenceRebasing=False)
        print(f'Restored {len(updates)} native LOD material slots: {name}', flush=True)
    models.write(output / index, manifest)
    assembly.update(catalogSha256=pin(output / index)['sha256'], artAccepted=False,
                    bytes=sum(row['bytes'] for row in proofs.values()),
                    preparedSource=dict(assembly=pin(assembly_path), catalog=pin(catalog_path)))
    assembly.pop('artAcceptance', None)
    models.write(output / 'ResourceAssembly.json', assembly)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--skin-chart', type=Path, nargs='+', default=[])
    parser.add_argument('--native-groom-finishes', type=Path,
                        help='Versioned optical settings for particular native groom exports.')
    parser.add_argument('--workers', metavar='COUNT', type=int, default=1,
                        help='Number of independent prefab bindings to build at once.')
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument('--blender-export', type=Path, nargs='+', help='Blender reports or export directories.')
    operation.add_argument('--unity-report', type=Path, nargs='+')
    operation.add_argument('--repair-native-groom-lods', action='store_true',
                           help='Restore matching native groom materials in an assembled candidate.')
    args = parser.parse_args()
    if not 1 <= args.workers <= 16 or (args.workers != 1 and not args.unity_report):
        parser.error('Choose one to sixteen workers when binding compiled prefabs.')
    if (args.skin_chart or args.native_groom_finishes) and not args.blender_export:
        parser.error('Attach materials when preparing the matching Blender mesh export.')
    source, output = map(models.checked_catalog_path, (args.source, args.output))
    if output == source or not any(output.is_relative_to(models.ROOT / path)
        for path in ('tools/asset-authoring/workspace', 'tools/game-test/artifacts')):
        parser.error('Use an independent character authoring workspace.')
    with resource_work('character-mesh-authoring', sys.argv[1:], metadata_names=('BuildPlan.json', 'ResourceAssembly.json')):
        if args.repair_native_groom_lods:
            repair_native_groom_lods(source, output)
        else:
            (prepare if args.blender_export else bind)(args, source, output)


if __name__ == '__main__':
    main()
