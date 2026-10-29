#!/usr/bin/env python3
"""Checks constructor arity for types declared in this solution.

`new MountRequest(account, drive)` where the only constructor takes one parameter is a compile error, and it is the
most common mistake this codebase can make that the other passes cannot see. Optional parameters and `params`
arrays widen the accepted range, and a class with no declared constructor only accepts `new T()`.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_types

INFINITY = 10 ** 6


def _split_top_level(text):
    """Splits on commas that are not inside (), [], {}, <> or a string."""
    parts = []
    depth = 0
    current = []
    for character in text:
        if character in '([{<':
            depth += 1
        elif character in ')]}>':
            depth -= 1
        if character == ',' and depth == 0:
            parts.append(''.join(current))
            current = []
            continue
        current.append(character)
    tail = ''.join(current)
    if tail.strip() or parts:
        parts.append(tail)
    return parts


def _parameter_range(parameters):
    parameters = [p.strip() for p in parameters]
    if len(parameters) == 1 and not parameters[0]:
        return (0, 0)
    minimum = 0
    maximum = len(parameters)
    for parameter in parameters:
        if re.search(r'\bparams\s', parameter):
            maximum = INFINITY
        if '=' not in parameter:
            minimum += 1
    return (minimum, maximum)


def build_constructor_index(paths):
    """type name -> set of (min, max) argument counts, or None when it cannot be determined."""
    ranges = {}
    for path in paths:
        for decl in check_types.parse_types(path, open(path, encoding='utf-8').read()):
            if decl.keyword not in ('class', 'struct'):
                continue
            name = decl.name
            found = set()
            for match in re.finditer(
                    r'(?<![\w.])' + re.escape(name) + r'\s*\(([^()]*(?:\([^()]*\)[^()]*)*)\)', decl.body):
                prefix = decl.body[max(0, match.start() - 40):match.start()]
                if re.search(r'\bstatic\s*$', prefix):
                    continue
                found.add(_parameter_range(_split_top_level(match.group(1))))
            if not found:
                found.add((0, 0))
            ranges.setdefault(name, set()).update(found)
    return ranges


def _new_expressions(text):
    """Yields (type name, arguments text) for every `new T(...)` expression."""
    index = 0
    while True:
        index = text.find('new', index)
        if index < 0:
            return
        index += 3
        cursor = index
        while cursor < len(text) and text[cursor] in ' \t\r\n':
            cursor += 1
        if cursor >= len(text) or not (text[cursor].isalpha() or text[cursor] == '_'):
            continue
        start = cursor
        while cursor < len(text) and (text[cursor].isalnum() or text[cursor] in '_.'):
            cursor += 1
        name = text[start:cursor].split('.')[-1]
        while cursor < len(text) and text[cursor] in ' \t\r\n':
            cursor += 1
        if cursor < len(text) and text[cursor] == '<':
            depth = 0
            while cursor < len(text):
                if text[cursor] == '<':
                    depth += 1
                elif text[cursor] == '>':
                    depth -= 1
                    if depth == 0:
                        cursor += 1
                        break
                cursor += 1
            while cursor < len(text) and text[cursor] in ' \t\r\n':
                cursor += 1
        if cursor >= len(text) or text[cursor] != '(':
            continue
        depth = 0
        end = cursor
        while end < len(text):
            if text[end] == '(':
                depth += 1
            elif text[end] == ')':
                depth -= 1
                if depth == 0:
                    break
            end += 1
        yield name, text[cursor + 1:end], index
        index = cursor + 1


def check(paths):
    ranges = build_constructor_index(paths)
    errors = []
    for path in paths:
        text = check_types._strip_comments(open(path, encoding='utf-8').read())
        rel = os.path.relpath(path, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        for name, arguments, position in _new_expressions(text):
            allowed = ranges.get(name)
            if not allowed:
                continue
            parameters = _split_top_level(arguments)
            count = 0 if len(parameters) == 1 and not parameters[0].strip() else len(parameters)
            if any(not parameter.strip() for parameter in parameters):
                continue
            if any(parameter.lstrip().startswith(('{', '(')) and count > 0 for parameter in parameters):
                # Newlines in an argument list, initializers or casts confuse a comma count; skip.
                pass
            if any(minimum <= count <= maximum for minimum, maximum in allowed):
                continue
            expected = ', '.join(sorted({
                str(minimum) if minimum == maximum else (
                    f'{minimum}..' if maximum == INFINITY else f'{minimum}..{maximum}')
                for minimum, maximum in allowed}))
            line = text.count('\n', 0, position) + 1
            errors.append(f"{rel}:{line}: 'new {name}' takes {expected} argument(s) but got {count}")
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
    print(f"{len(problems)} constructor error(s)")
