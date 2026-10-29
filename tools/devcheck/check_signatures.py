#!/usr/bin/env python3
"""Checks that an implementing member matches the interface it implements.

check_types.py proves the *name* exists. This pass compares the shape as well: same kind (method or property),
same return type and the same parameter types, ignoring whitespace, nullable annotations and default values.
A changed return type or an extra parameter is a compile error (CS0738 / CS0535) that a name-only check misses.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_types

MEMBER = re.compile(
    r'(?m)^[ \t]*(?:\[[^\]]*\]\s*)*'
    r'(?:(?:public|internal|private|protected|static|readonly|virtual|override|abstract|sealed|extern|new|async|'
    r'partial|required)\s+)*'
    r'(?P<ret>[A-Za-z_][\w<>\[\],\.\?\s]*?)\s+(?:(?P<owner>[A-Za-z_]\w*)\.)?(?P<name>[A-Za-z_]\w*)\s*'
    r'(?:<[^<>()]*>)?\s*(?P<tail>\([^;{]*?\)|(?=\{|=>)[^;{\n]*)')

NOT_MEMBERS = {'get', 'set', 'init', 'return', 'new', 'where', 'if', 'for', 'foreach', 'while', 'switch',
               'catch', 'using', 'lock', 'yield', 'throw', 'else'}


def _normalise(type_text):
    return re.sub(r'\s+', '', type_text or '').replace('?', '')


def _parameters(text):
    parameters = []
    for raw in text.split(','):
        part = raw.strip().split('=')[0].strip()
        if not part:
            continue
        parameters.append(_normalise(part.rsplit(' ', 1)[0] if ' ' in part else part))
    return tuple(parameters)


def _members(decl):
    """member name -> list of (kind, return type, parameter types)."""
    members = {}
    for match in MEMBER.finditer(decl.body):
        name = match.group('name')
        if name in NOT_MEMBERS:
            continue
        tail = (match.group('tail') or '').strip()
        if tail.startswith('('):
            signature = ('method', _normalise(match.group('ret')), _parameters(tail[1:-1]))
        else:
            signature = ('property', _normalise(match.group('ret')), ())
        members.setdefault(name, []).append(signature)
    return members


def _describe(signature):
    kind, returns, parameters = signature
    if kind == 'property':
        return f'{returns} {{ get; }}'
    return f'{returns} ({", ".join(parameters)})'


def check(paths):
    declarations = []
    for path in paths:
        declarations.extend(check_types.parse_types(path, open(path, encoding='utf-8').read()))

    by_name = {}
    for decl in declarations:
        by_name.setdefault(decl.name, []).append(decl)

    def requirements(interface, seen=None):
        seen = seen or set()
        if interface.name in seen:
            return {}
        seen.add(interface.name)
        required = dict(_members(interface))
        for base in interface.bases:
            for candidate in by_name.get(base, []):
                if candidate.keyword == 'interface':
                    required.update(requirements(candidate, seen))
        return required

    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    errors = []
    for decl in declarations:
        if decl.keyword not in ('class', 'struct'):
            continue
        required = {}
        for base in decl.bases:
            for candidate in by_name.get(base, []):
                if candidate.keyword == 'interface':
                    required.update(requirements(candidate))
        if not required:
            continue

        implemented = _members(decl)
        rel = os.path.relpath(decl.path, root)
        for name, wanted in required.items():
            if name not in implemented:
                errors.append(f"{rel}: '{decl.name}' does not implement '{name}' ({_describe(wanted[0])})")
                continue
            if any(signature == wanted[0] for signature in implemented[name]):
                continue
            found = ', '.join(sorted({_describe(signature) for signature in implemented[name]}))
            errors.append(
                f"{rel}: '{decl.name}.{name}' is {found} but the interface requires {_describe(wanted[0])}")
    return errors


if __name__ == '__main__':
    base = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    files = []
    for folder in ('src', 'tests', 'tools'):
        for dirpath, dirnames, filenames in os.walk(os.path.join(base, folder)):
            dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
            files.extend(os.path.join(dirpath, name) for name in filenames if name.endswith('.cs'))
    problems = check(sorted(files))
    for problem in problems:
        print('ERROR ' + problem)
    print(f'{len(problems)} signature error(s)')
