#!/usr/bin/env python3
"""Checks the source generator contract of CommunityToolkit.Mvvm.

`[ObservableProperty] private string _name;` makes the toolkit generate a `Name` property and call the optional
partial methods `OnNameChanging(string value)` / `OnNameChanged(string value)`. If the field type changes and the
partial method is not updated, the hand-written half silently stops matching the generated half - a compile error
that no other pass in this repository can see.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_types


def _normalise(type_text):
    return re.sub(r'\s+', '', type_text or '')


def _generated_name(field):
    stripped = field.lstrip('_')
    return stripped[0].upper() + stripped[1:]


def check(paths):
    errors = []
    for path in paths:
        text = check_types._strip_comments(open(path, encoding='utf-8').read())
        rel = os.path.relpath(path, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        for decl in check_types.parse_types(path, open(path, encoding='utf-8').read()):
            fields = re.findall(
                r'\[ObservableProperty\][^;{}]*?(?:private|protected|internal)\s+'
                r'((?:readonly\s+)?[A-Za-z_][\w<>\[\],\.\?\s]*?)\s+(@?[A-Za-z_]\w*)\s*(?:=[^;]*)?;',
                decl.body)
            for field_type, field_name in fields:
                if field_name.startswith('@'):
                    continue
                if field_type.startswith('readonly'):
                    continue
                property_name = _generated_name(field_name)
                for kind in ('Changing', 'Changed'):
                    method = f'On{property_name}{kind}'
                    for match in re.finditer(r'\bpartial\s+void\s+' + method + r'\s*\(([^)]*)\)', decl.body):
                        parameters = [p.strip() for p in match.group(1).split(',') if p.strip()]
                        if not parameters:
                            continue
                        declared = _normalise(parameters[0].rsplit(' ', 1)[0])
                        expected = _normalise(field_type)
                        if declared != expected:
                            line = text.count('\n', 0, text.find(method)) + 1
                            errors.append(
                                f'{rel}:{line}: {method} takes {declared} but the generated property '
                                f'{property_name} is {expected}')
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
    print(f'{len(problems)} toolkit error(s)')
