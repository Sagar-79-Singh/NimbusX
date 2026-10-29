#!/usr/bin/env python3
"""Checks member access on explicitly typed local variables.

`MountSnapshot snapshot = ...; snapshot.DriveLetterX` cannot be seen by the type-name passes, because the left side of
the dot is a variable. This pass reads the declarations in each file (`Type name`, parameters, `foreach` variables,
fields) and then verifies `name.Member` and `name.Member(...)` against the types that variable can have.

When one name is declared with several types in the same file - or a type lives outside this solution - the name is
skipped instead of guessed, so the pass never reports something it cannot prove.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_calls
import check_members
import check_types

# Builtin types are included so that `string target` and `StreamTarget target` in the same file count as ambiguous.
DECLARATION = re.compile(
    r'(?<![\w.])([A-Za-z_][A-Za-z0-9_]*)(?:\s*<[^<>]*>)?\??\s+([a-z_][A-Za-z0-9_]*)\s*(?=[=;,)]|\bin\b)')
FOR_EACH = re.compile(r'foreach\s*\(\s*([A-Z][A-Za-z0-9_]*)(?:\s*<[^<>]*>)?\s+\??([a-z_][A-Za-z0-9_]*)\s+in\b')

SKIP_MEMBERS = {'Equals', 'ToString', 'GetHashCode', 'GetType', 'ReferenceEquals'}


def _argument_count(text, open_paren):
    depth = 0
    end = open_paren
    while end < len(text):
        if text[end] == '(':
            depth += 1
        elif text[end] == ')':
            depth -= 1
            if depth == 0:
                break
        end += 1
    parameters = check_calls._split_top_level(text[open_paren + 1:end])
    if len(parameters) == 1 and not parameters[0].strip():
        return 0
    if any(not parameter.strip() for parameter in parameters):
        return None
    return len(parameters)


def check(paths):
    members, bases = check_members.build_index(paths)
    declared = set(members)
    inherited = {name: check_members._with_bases(name, members, bases) for name in members}
    framework_derived = {name for name, parents in bases.items() if any(p not in declared for p in parents)}
    methods, _ = check_calls.build_method_index(paths)
    member_types = check_members.build_member_types(paths)

    errors = []
    for path in paths:
        text = check_types._strip_comments(open(path, encoding='utf-8').read())
        rel = os.path.relpath(path, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

        # Every declared type of every variable, external types included: a name declared with more than one type
        # is ambiguous and is skipped rather than guessed.
        candidates = {}
        for match in list(DECLARATION.finditer(text)) + list(FOR_EACH.finditer(text)):
            type_name, variable = match.group(1), match.group(2)
            candidates.setdefault(variable, set()).add(type_name)
        candidates = {variable: types for variable, types in candidates.items()
                      if len(types) == 1 and next(iter(types)) in declared}

        reported = set()
        for variable, types in candidates.items():
            if any(name in framework_derived for name in types):
                continue
            for match in re.finditer(r'(?<![\w.])' + re.escape(variable) + r'\.([A-Za-z_]\w*)', text):
                member = match.group(1)
                if member in SKIP_MEMBERS:
                    continue
                resolvable = [name for name in types if member in inherited[name]]
                if not resolvable:
                    key = (variable, member)
                    if key not in reported:
                        reported.add(key)
                        line = text.count('\n', 0, match.start()) + 1
                        errors.append(
                            f"{rel}:{line}: '{variable}' ({'/'.join(sorted(types))}) has no member '{member}'")
                    continue

                # One more hop: `name.Member.Next`, where Member's declared type is known.
                following = re.match(r'\s*\.\s*([A-Za-z_]\w*)', text[match.end():])
                if following:
                    hop = following.group(1)
                    declared_types = {
                        member_types.get(name, {}).get(member) for name in resolvable}
                    # `DriveLetter? letter` legitimately exposes HasValue/Value; only a non-nullable struct hides them.
                    nullable = any((entry or '').strip().endswith('?') for entry in declared_types)
                    declared_types = {
                        re.sub(r'<.*', '', entry or '').strip().rstrip('?').replace('[]', '')
                        for entry in declared_types}
                    if nullable and hop in ('HasValue', 'Value', 'GetValueOrDefault'):
                        targets = set()
                    else:
                        targets = {entry.split('.')[-1] for entry in declared_types if entry in declared}
                    targets = {name for name in targets if hop not in inherited[name]}
                    if targets and following.group(1) not in SKIP_MEMBERS:
                        key = (variable, member, hop)
                        if key not in reported:
                            reported.add(key)
                            line = text.count('\n', 0, match.start()) + 1
                            errors.append(
                                f"{rel}:{line}: '{variable}.{member}' is "
                                f"{'/'.join(sorted(targets))} which has no member '{hop}'")

                position = match.end()
                while position < len(text) and text[position] in ' \t\r\n':
                    position += 1
                if position >= len(text) or text[position] != '(':
                    continue
                # Only methods can be called; a property that returns a delegate is left alone.
                ranges = []
                for name in resolvable:
                    ranges.extend(methods.get(name, {}).get(member, []))
                if not ranges:
                    continue
                count = _argument_count(text, position)
                if count is None:
                    continue
                if any(minimum <= count <= maximum for minimum, maximum, _ in ranges):
                    continue
                key = (variable, member, count)
                if key in reported:
                    continue
                reported.add(key)
                line = text.count('\n', 0, match.start()) + 1
                errors.append(
                    f"{rel}:{line}: '{variable}.{member}' takes {check_calls._describe(ranges)} "
                    f"argument(s) but got {count}")
    return errors


if __name__ == '__main__':
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    files = []
    for base in ('src', 'tests', 'tools'):
        for dirpath, dirnames, filenames in os.walk(os.path.join(root, base)):
            dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
            files.extend(os.path.join(dirpath, name) for name in filenames if name.endswith('.cs'))
    problems = check(sorted(files))
    for problem in problems:
        print('ERROR ' + problem)
    print(f'{len(problems)} local member error(s)')
