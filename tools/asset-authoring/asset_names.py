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


def resource_names(resources, extension='', lowercase=False):
    """Allocate names across one output scope, using source details on collision.

    The same declared resource set yields the same result in any input order.
    A producer should provide a distinct mesh/material detail when labels repeat.
    Remaining duplicates receive numbered variants in opaque-key order.
    """
    if not isinstance(extension, str) or re.fullmatch(r'(?:\.[A-Za-z0-9]+)*', extension) is None:
        raise ValueError('A resource extension must contain only dotted file suffixes.')
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
    if any(len(name.encode('utf-16-le')) // 2 > 255 for name in candidates.values()):
        raise ValueError('A resource name exceeds the Windows component length; shorten its source label.')
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
