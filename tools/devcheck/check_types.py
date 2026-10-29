#!/usr/bin/env python3
"""Type-level checks that stand in for a compiler: interface implementation and override validity.

This is deliberately conservative. It only reports a problem when both sides of the relationship are declared in
this solution, so a framework base class (`Window`, `IDisposable`, ...) never produces a false positive.
"""
import os
import re

TYPE_KEYWORDS = ('class', 'struct', 'interface', 'record', 'enum')


class TypeDecl:
    def __init__(self, path, namespace, keyword, name, bases, body, is_abstract, is_static, body_start=0):
        self.path = path
        self.namespace = namespace
        self.keyword = keyword
        self.name = name
        self.bases = bases
        self.body = body
        self.is_abstract = is_abstract
        self.is_static = is_static
        # Offset of the body's first character inside the comment-stripped file text.
        self.body_start = body_start

    @property
    def qualified(self):
        return (self.namespace + '.' if self.namespace else '') + self.name


def _scan(text):
    """Walk C# source once, copying code through and handling comments, strings and chars.

    A naive `re.sub(r'//.*', ...)` breaks on regex patterns that contain `//` (SecretRedactor has several), which
    silently truncated a class body and made the member checks blind. This scanner is the fix.
    """
    out = []
    index = 0
    length = len(text)
    while index < length:
        character = text[index]

        if character == '/' and index + 1 < length and text[index + 1] == '/':
            while index < length and text[index] != '\n':
                index += 1
            continue

        if character == '/' and index + 1 < length and text[index + 1] == '*':
            index += 2
            while index + 1 < length and not (text[index] == '*' and text[index + 1] == '/'):
                index += 1
            index = min(index + 2, length)
            continue

        if text.startswith('"""', index):
            # C# 11 raw string literal: ends at the next run of three or more quotes.
            end = text.find('"""', index + 3)
            if end < 0:
                end = length - 3
            index = end + 3
            out.append('""')
            continue

        if character == '"':
            verbatim = index > 0 and text[index - 1] == '@'
            start = index
            index += 1
            while index < length:
                if text[index] == '"':
                    if verbatim and index + 1 < length and text[index + 1] == '"':
                        index += 2
                        continue
                    break
                if not verbatim and text[index] == '\\':
                    index += 2
                    continue
                index += 1
            index += 1
            out.append('""')
            continue

        if character == "'":
            start = index
            index += 1
            while index < length and text[index] != "'":
                index += 2 if text[index] == '\\' else 1
            index += 1
            out.append("''")
            continue

        out.append(character)
        index += 1

    return ''.join(out)


def _strip_comments(text):
    return _scan(text)


def _match_brace(text, start):
    """Index of the brace that closes the one at `start` (string literals already neutralised by _scan)."""
    depth = 0
    for index in range(start, len(text)):
        if text[index] == '{':
            depth += 1
        elif text[index] == '}':
            depth -= 1
            if depth == 0:
                return index
    return -1


def _namespace_at(text, position):
    namespaces = re.findall(r'\bnamespace\s+([A-Za-z0-9_.]+)', text[:position])
    return namespaces[-1] if namespaces else ''


def parse_types(path, text):
    """Every class/struct/interface declared in one file, with its body and base list."""
    text = _strip_comments(text)
    found = []
    pattern = re.compile(
        r'(?P<mods>(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|new)\s+)*)'
        r'(?P<kind>class|struct|interface|record)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)'
        r'\s*(?:<[^>{]*>)?\s*'
        r'(?::\s*(?P<bases>[^{\r\n]+))?')

    for match in pattern.finditer(text):
        # `foreach (MountRecord record in ...)` also reads as "record" + a name; a declaration has a body,
        # a base list, a parameter list or a constraint after the name.
        following = text[match.end():match.end() + 40].lstrip()
        if not following or following[0] not in '{(:<;':
            continue
        brace = text.find('{', match.end() - 1 if match.group('bases') else match.end())
        if brace < 0:
            continue
        close = _match_brace(text, brace)
        if close < 0:
            continue
        mods = match.group('mods') or ''
        bases = []
        if match.group('bases'):
            for part in match.group('bases').split(','):
                cleaned = re.sub(r'<.*', '', part).strip()
                cleaned = cleaned.split('(')[0].strip()
                if cleaned:
                    bases.append(cleaned.split('.')[-1])
        found.append(TypeDecl(
            path=path,
            namespace=_namespace_at(text, match.start()),
            keyword=match.group('kind'),
            name=match.group('name'),
            bases=bases,
            body=text[brace + 1:close],
            is_abstract='abstract' in mods,
            is_static='static' in mods,
            body_start=brace + 1))
    return found


MEMBER_PATTERNS = (
    # Interface members often carry no access modifier at all: `IReadOnlyList<MountSnapshot> Snapshots { get; }`.
    r'(?m)^[ \t]*(?:[A-Za-z_][\w<>\[\],\.\?\s]*?)\s+([A-Za-z_]\w*)\s*\{\s*(?:get|set|init)\b',
    r'(?m)^[ \t]*(?:[A-Za-z_][\w<>\[\],\.\?\s]*?)\s+([A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\(',
    r'(?:public|internal|private|protected|static|readonly|virtual|override|abstract|sealed|async|extern|new|partial|required|const|event|\s)+'
    r'(?:\([^()]*(?:\([^()]*\)[^()]*)*\)|[A-Za-z_][\w<>\[\],\.\?\s]*?)\s+([A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*\(',
    r'(?:public|internal|private|protected|static|readonly|virtual|override|abstract|sealed|new|required)\s+'
    r'[A-Za-z_][\w<>\[\],\.\?\s]*?\s+([A-Za-z_]\w*)\s*(?:\{|=>)',
    r'\bevent\s+[A-Za-z_][\w<>\[\],\.\?]*\s+([A-Za-z_]\w*)',
)


def member_names(body):
    names = set()
    for pattern in MEMBER_PATTERNS:
        for match in re.finditer(pattern, body):
            names.add(match.group(1))
    return names


def interface_requirements(decl):
    """Members an interface requires: everything without a default implementation."""
    body = decl.body
    required = {}

    # Events first: "event EventHandler<LogEntry>? EntryWritten;" - the member name is the last identifier.
    for match in re.finditer(r'\bevent\s+[\w.<>\[\],\? ]+?\s+([A-Za-z_]\w*)\s*;', body):
        required[match.group(1)] = True
    body = re.sub(r'\bevent\s+[\w.<>\[\],\? ]+?\s+[A-Za-z_]\w*\s*;', '', body)

    for match in re.finditer(
            r'[A-Za-z_][\w<>\[\],\.\?\s]*?\s+([A-Za-z_]\w*)\s*(\([^;{]*\)|[^;{}\r\n]*(?=\s*[\{;]))\s*(=>|\{)?',
            body):
        name = match.group(1)
        if name in ('get', 'set', 'init', 'return', 'new'):
            continue
        has_default = match.group(3) == '=>'
        required[name] = required.get(name, False) or not has_default
    return {name for name, needs in required.items() if needs}


def check(files):
    errors = []
    types = []
    for path in files:
        text = open(path, encoding='utf-8').read()
        types.extend(parse_types(path, text))

    by_name = {}
    for decl in types:
        by_name.setdefault(decl.name, []).append(decl)

    def members_with_inheritance(decl, seen=None):
        """Own members plus everything inherited from class bases (interfaces are not implementation)."""
        seen = seen or set()
        if decl.name in seen:
            return set()
        seen.add(decl.name)
        merged = member_names(decl.body)
        for base in decl.bases:
            for candidate in by_name.get(base, []):
                if candidate.keyword == 'interface':
                    continue
                merged |= members_with_inheritance(candidate, seen)
        return merged

    # 1. Interfaces declared here must be fully implemented by the classes that claim them.
    interfaces = [decl for decl in types if decl.keyword == 'interface']
    for decl in types:
        if decl.keyword not in ('class', 'struct', 'record') or decl.is_abstract or decl.is_static:
            continue
        for base in decl.bases:
            for interface in interfaces:
                if interface.name != base:
                    continue
                required = interface_requirements(interface)
                available = members_with_inheritance(decl)
                explicit = set(re.findall(re.escape(interface.name) + r'\.([A-Za-z_]\w*)', decl.body))
                missing = sorted(required - available - explicit)
                for member in missing:
                    errors.append(
                        f"{decl.path}: {decl.name} implements {interface.name} but has no member '{member}'")

    # 2. An override must have something to override inside this solution.
    for decl in types:
        for match in re.finditer(
                r'\boverride\s+[A-Za-z_][\w<>\[\],\.\?\s]*?\s+([A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*[\(\{]', decl.body):
            name = match.group(1)
            inherited = set()
            for base in decl.bases:
                for candidate in by_name.get(base, []):
                    inherited |= members_with_inheritance(candidate)
            if not decl.bases:
                continue
            if any(base in by_name for base in decl.bases) and name not in inherited:
                errors.append(f"{decl.path}: {decl.name} overrides '{name}', but no base type declares it")

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
    print(f"{len(problems)} type error(s)")
