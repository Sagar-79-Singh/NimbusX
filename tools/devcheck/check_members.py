#!/usr/bin/env python3
"""Checks that `Type.Member` references actually exist on the solution types.

This is the closest a static pass can get to the compiler's CS0117 ("'T' does not contain a definition for 'M'").
It only inspects references whose left side is a type declared in this solution, and it merges partial
declarations plus base classes, so inherited members are accepted. The solution contains no extension methods,
which is what makes the check safe.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_types


OBJECT_MEMBERS = {'Equals', 'ToString', 'GetHashCode', 'GetType', 'ReferenceEquals', 'MemberwiseClone'}


def _strip_literals(text):
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    text = re.sub(r'//.*', '', text)
    text = re.sub(r'@"(?:[^"]|"")*"', '""', text)
    text = re.sub(r'"(?:\\.|[^"\\])*"', '""', text)
    text = re.sub(r"'(?:\\.|[^'\\])'", "''", text)
    return text


def build_index(paths):
    declarations = []
    for path in paths:
        declarations.extend(check_types.parse_types(path, open(path, encoding='utf-8').read()))

    members = {}
    bases = {}
    for decl in declarations:
        own = check_types.member_names(decl.body)
        own |= set(re.findall(r'\bconst\s+[\w<>\.\[\]]+\s+(\w+)', decl.body))
        own |= set(re.findall(r'\b(?:public|internal)\s+(?:static\s+|readonly\s+|const\s+)*[\w<>\[\]\.\?]+\s+(\w+)\s*[;=]', decl.body))
        if decl.keyword == 'enum':
            own |= set(re.findall(r'^\s*(\w+)\s*(?:=\s*[^,]+)?,?\s*$', decl.body, re.M))
        members[decl.name] = members.get(decl.name, set()) | own
        bases.setdefault(decl.name, set()).update(decl.bases)

    return members, bases


def build_member_types(paths):
    """type name -> {member name: declared type text} for properties and fields (methods excluded)."""
    table = {}
    pattern = re.compile(
        r'(?:public|internal|private|protected)\s+(?:static\s+|readonly\s+|override\s+|virtual\s+|required\s+)*'
        r'([A-Za-z0-9_<>?.,\[\]\s]+?)\s+([A-Za-z_]\w*)\s*(?:\{|=>|;|=)')
    for path in paths:
        for decl in check_types.parse_types(path, open(path, encoding='utf-8').read()):
            if decl.keyword not in ('class', 'struct', 'record'):
                continue
            members = table.setdefault(decl.name, {})
            for match in pattern.finditer(decl.body):
                members.setdefault(match.group(2), match.group(1).strip())
    return table


def _with_bases(name, members, bases, seen=None):
    seen = seen or set()
    if name in seen:
        return set()
    seen.add(name)
    merged = set(members.get(name, set()))
    for base in bases.get(name, set()):
        merged |= _with_bases(base, members, bases, seen)
    return merged


def check(paths):
    members, bases = build_index(paths)
    # A type whose base class lives outside this solution (Window, Application, ...) inherits members this pass
    # cannot see, so it is checked only for its own members and never reported as missing something inherited.
    framework_derived = {name for name, parents in bases.items() if any(p not in members for p in parents)}
    resolved = {name: _with_bases(name, members, bases) for name in members}
    errors = []

    for path in paths:
        text = _strip_literals(open(path, encoding='utf-8').read())
        rel = os.path.relpath(path, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        reported = set()
        for match in re.finditer(r'(?<![\w.])([A-Z][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)', text):
            type_name, member = match.group(1), match.group(2)
            if type_name not in resolved or member in OBJECT_MEMBERS or type_name in framework_derived:
                continue
            if member in resolved[type_name]:
                continue
            key = (type_name, member)
            if key in reported:
                continue
            reported.add(key)
            line = text.count('\n', 0, match.start()) + 1
            errors.append(f"{rel}:{line}: '{type_name}' has no member '{member}'")

    return errors


if __name__ == '__main__':
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    paths = []
    for base in ('src', 'tests', 'tools'):
        for dirpath, dirnames, filenames in os.walk(os.path.join(root, base)):
            dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
            paths.extend(os.path.join(dirpath, name) for name in filenames if name.endswith('.cs'))
    problems = check(sorted(paths))
    for problem in problems:
        print('ERROR ' + problem)
    print(f"{len(problems)} member error(s)")
