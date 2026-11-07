"""Readable resource names and explicit native path budgets for asset producers.

Opaque keys identify returned entries and deterministic variants; they are never
appended to a resource name. Labels and optional details belong to the producer.
"""
from collections import defaultdict
from dataclasses import dataclass
import os
from pathlib import PurePosixPath
import re

_RESERVED = {'CON', 'PRN', 'AUX', 'NUL'} | {
    prefix + str(index) for prefix in ('COM', 'LPT') for index in range(1, 10)
}


def source_label(value):
    """Use the native asset's basename, retaining readable words and spelling."""
    if not isinstance(value, str) or not value.strip():
        raise ValueError('A nonempty source asset label is required.')
    label = PurePosixPath(value.replace('\\', '/')).stem
    label = re.sub(r'[^\w.-]+', '_', label, flags=re.UNICODE).strip('._-')
    if not label or label.split('.', 1)[0].upper() in _RESERVED:
        raise ValueError('The source label cannot form a Windows resource name.')
    return label


@dataclass(frozen=True)
class ResourceName:
    key: str
    label: str
    detail: str = ''
    suffix: str = ''


def _distinct_detail(label, detail):
    base, words = label.split('_'), detail.split('_')
    index = 0
    while index < min(len(base), len(words)) and base[index].casefold() == words[index].casefold():
        index += 1
    return '_'.join(words[index:])


def resource_names(resources, extension='', lowercase=False, strict=False, maximum=255):
    """Allocate names across one output scope, using source details on collision.

    The same declared resource set yields the same result in any input order.
    A producer should provide a distinct mesh/material detail when labels repeat.
    Remaining duplicates receive numbered variants in opaque-key order unless
    strict publication naming is requested. Public names must fail on a real
    semantic collision; opaque keys cannot become names or numbered fallbacks.
    """
    if not isinstance(extension, str) or re.fullmatch(r'(?:\.[A-Za-z0-9]+)*', extension) is None:
        raise ValueError('A resource extension must contain only dotted file suffixes.')
    if type(strict) is not bool or type(maximum) is not int or not 1 <= maximum <= 255:
        raise ValueError('A resource needs a boolean collision policy and a 1..255 component budget.')
    entries = list(resources)
    if any(not isinstance(row, ResourceName) or not isinstance(row.key, str) or not row.key for row in entries):
        raise ValueError('Every resource requires an opaque nonempty key and source label.')
    if len({row.key for row in entries}) != len(entries):
        raise ValueError('Resource keys must be unique in their output scope.')
    labels = {row.key: source_label(row.label) for row in entries}
    suffixes = {row.key: source_label(row.suffix) if row.suffix else '' for row in entries}

    def compose(row, detail=''):
        parts = [labels[row.key], detail, suffixes[row.key]]
        value = '_'.join(part for part in parts if part) + extension
        return value.lower() if lowercase else value

    candidates = {row.key: compose(row) for row in entries}
    groups = defaultdict(list)
    for row in entries:
        groups[candidates[row.key].casefold()].append(row)
    for group in groups.values():
        if len(group) > 1:
            for row in group:
                if row.detail:
                    detail = _distinct_detail(labels[row.key], source_label(row.detail))
                    if detail:
                        candidates[row.key] = compose(row, detail)
    groups = defaultdict(list)
    for row in entries:
        groups[candidates[row.key].casefold()].append(row)
    used = {name for name, group in groups.items() if len(group) == 1}
    for _, group in sorted(groups.items()):
        if len(group) < 2:
            continue
        if strict:
            raise ValueError('Semantic resource collision: ' + repr([(row.label, row.detail, row.suffix) for row in group]))
        for row in sorted(group, key=lambda item: item.key):
            index = 1
            while True:
                candidate = compose(row, 'Variant' + str(index).zfill(2))
                if candidate.casefold() not in used:
                    break
                index += 1
            candidates[row.key] = candidate
            used.add(candidate.casefold())
    if len({name.casefold() for name in candidates.values()}) != len(entries):
        raise ValueError('Source labels did not produce unique resource names.')
    if any(len(name.encode('utf-16-le')) // 2 > maximum for name in candidates.values()):
        raise ValueError('A resource name exceeds its declared component budget; shorten its source label.')
    return candidates


def assert_path_budget(path, maximum, suffix=''):
    """Reject a projected native path before creation; no alias or OS setting changes."""
    value = os.fspath(path)
    if not os.path.isabs(value) or not isinstance(maximum, int) or isinstance(maximum, bool) or maximum < 1:
        raise ValueError('A full projected path and explicit native character budget are required.')
    length = len((value + suffix).encode('utf-16-le')) // 2
    if length > maximum:
        raise ValueError(f'Native path needs {length} UTF-16 characters, budget {maximum}; '
                         f'shorten the output root or source label: {value}{suffix}')
    return value


def body_atlas_filename(native_texture_name, parts_lods, purpose='main', maximum=128):
    """Stable native model + actual parts/LOD sets; omit repeated atlas syntax."""
    label=source_label(native_texture_name)
    tokens=label.lower().split('_')
    if 'basecolor'not in tokens:
        raise ValueError('A Body Main atlas has no declared native BaseColor label.')
    # Native sex words have fixed semantic abbreviations, independent of the
    # output directory/budget. Origin and other source variants stay intact.
    model=[{'female':'f','male':'m'}.get(t,t)for t in tokens if t not in ('shape','basecolor','bodyrgb')]
    if not model or any(not re.fullmatch('[a-z0-9]+',t)for t in model):
        raise ValueError('A Body atlas has no semantic native model/variant.')
    order=('body','forearms','hands','feet')
    if not parts_lods or any(p not in order for p in parts_lods):
        raise ValueError('A Body atlas has an unknown actual anatomical part.')
    groups={}
    for part in order:
        if part not in parts_lods:continue
        levels=tuple(sorted(set(parts_lods[part])))
        if not levels or any(type(v)is not int or not 0<=v<=2 for v in levels):
            raise ValueError('A Body atlas has an invalid actual LOD set.')
        groups.setdefault(levels,[]).append(part)
    def lod_label(levels):
        return str(levels[0])+'-'+str(levels[-1]) if len(levels)>1 and levels==tuple(range(levels[0],levels[-1]+1))else'-'.join(map(str,levels))
    component='_'.join('_'.join(parts)+'_lod'+lod_label(levels)for levels,parts in groups.items())
    purpose=source_label(purpose).lower()
    if not re.fullmatch('[a-z][a-z0-9_]*',purpose):raise ValueError('A Body atlas purpose is not semantic.')
    result='_'.join(model)+'_'+component+'_'+purpose+'.bundle'
    if len(result)>maximum:raise ValueError('The semantic Body atlas name exceeds its path budget.')
    return result
