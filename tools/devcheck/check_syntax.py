#!/usr/bin/env python3
"""Full syntax validation of every C# file with tree-sitter.

The other passes are regex based and only look for the mistakes they were told about. A real C# grammar catches
everything else: a stray token, a missing semicolon, an unbalanced construct. Tree-sitter is a build-time
convenience for contributors, so when it is not installed this pass says so and returns no errors instead of
failing the gate.
"""
import os
import sys

try:
    import tree_sitter_c_sharp
    from tree_sitter import Language, Parser
except ImportError:  # pragma: no cover - optional tooling
    tree_sitter_c_sharp = None


def _walk(node, problems, source):
    if node.is_error or node.is_missing:
        text = source[node.start_byte:node.end_byte].decode('utf-8', 'replace').strip()
        text = ' '.join(text.split())
        if len(text) > 60:
            text = text[:57] + '...'
        kind = 'missing ' + node.type if node.is_missing else 'unexpected ' + (text or 'token')
        problems.append((node.start_point[0] + 1, kind))
        return
    for child in node.children:
        _walk(child, problems, source)


def check(paths):
    if tree_sitter_c_sharp is None:
        print('note   tree-sitter is not installed, skipping the grammar pass (pip install tree-sitter tree-sitter-c-sharp)')
        return []

    language = Language(tree_sitter_c_sharp.language())
    parser = Parser(language)
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    errors = []
    for path in paths:
        source = open(path, 'rb').read()
        tree = parser.parse(source)
        problems = []
        _walk(tree.root_node, problems, source)
        if not problems:
            continue
        rel = os.path.relpath(path, root)
        for line, kind in problems:
            errors.append(f'{rel}:{line}: {kind}')
    return errors


if __name__ == '__main__':
    base = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    files = []
    for folder in ('src', 'tests', 'tools'):
        for dirpath, dirnames, filenames in os.walk(os.path.join(base, folder)):
            dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
            files.extend(os.path.join(dirpath, name) for name in filenames if name.endswith('.cs'))
    found = check(sorted(files))
    for problem in found:
        print('ERROR ' + problem)
    print(f'{len(found)} syntax error(s)')
