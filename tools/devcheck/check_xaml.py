#!/usr/bin/env python3
"""Static checks for the WPF XAML tree.

There is no WPF compiler in the build sandbox, so these checks replace the two things the compiler
would otherwise tell us about:

  * malformed XAML (not well formed XML, unbalanced tags)
  * {Binding X} paths that do not exist on the view model (or on any known type)
  * {StaticResource Key} references that are never defined
  * x:Class handlers/classes referenced from XAML that have no code-behind type
"""
import os
import re
import xml.etree.ElementTree as ET

XAML_NS = '{http://schemas.microsoft.com/winfx/2006/xaml}'

# Members every view model gets from PageViewModel / ObservableObject.
BASE_MEMBERS = {
    'IsBusy', 'Error', 'HasError', 'Title', 'Subtitle', 'Glyph', 'Services', 'Logger',
    'RefreshAsync', 'RefreshNowCommand', 'Dispose',
}

# Windows whose DataContext is set in code-behind rather than by naming convention.
WINDOW_VIEW_MODELS = {
    'MainWindow': 'MainViewModel',
    'AddAccountWindow': 'ConnectViewModel',
    'WizardWindow': 'WizardViewModel',
}

# WPF members that are always valid on a binding target.
FRAMEWORK_MEMBERS = {
    'DataContext', 'Content', 'Text', 'Tag', 'ActualWidth', 'ActualHeight',
}


def _strip_comments(text):
    return re.sub(r'<!--.*?-->', '', text, flags=re.S)


def _collect_csharp_types(cs_files):
    """class name -> set of member names (properties, commands, fields exposed by the toolkit)."""
    types = {}
    for path in cs_files:
        try:
            text = open(path, encoding='utf-8').read()
        except OSError:
            continue
        body = re.sub(r'//.*', '', text)
        body = re.sub(r'/\*.*?\*/', '', body, flags=re.S)

        # Members declared anywhere in the file.
        members = set()
        for match in re.finditer(
                r'(?:public|internal|private|protected)\s+(?:static\s+|readonly\s+|override\s+|virtual\s+|async\s+|new\s+|const\s+)*'
                r'[A-Za-z0-9_<>?.,\[\]\s]+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\{|=>|;|\()', body):
            members.add(match.group(1))
        for match in re.finditer(r'\b([A-Za-z_][A-Za-z0-9_]*)\s*\{\s*get;', body):
            members.add(match.group(1))
        for field in re.findall(r'\[ObservableProperty\][^;]*?private\s+[A-Za-z0-9_<>?.,\[\]]+\s+_([A-Za-z0-9_]+)', body, flags=re.S):
            members.add(field[0].upper() + field[1:])
        for name in re.findall(r'\[RelayCommand\][^;{]*?\b([A-Za-z0-9_]+)Async\s*\(', body):
            members.add(name[0].upper() + name[1:] + 'Command')
        for name in re.findall(r'\[RelayCommand\][^;{]*?\b([A-Za-z0-9_]+)\s*\(', body):
            members.add(name[0].upper() + name[1:] + 'Command')

        for match in re.finditer(
                r'^\s*(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+)*'
                r'(?:class|struct|interface|enum|record)\s+([A-Za-z0-9_]+)', body, re.M):
            types.setdefault(match.group(1), set()).update(members)
    return types


def _collect_resource_keys(xaml_files):
    keys = set()
    for path in xaml_files:
        try:
            text = open(path, encoding='utf-8').read()
        except OSError:
            continue
        keys.update(re.findall(r'x:Key="([^"]+)"', text))
    return keys


def _binding_paths(text):
    """Every Binding path used in the document, with the first path segment."""
    paths = []
    for match in re.finditer(r'\{Binding\s+([^}]*)\}', text):
        body = match.group(1).strip()
        if not body:
            continue
        # Trim markup arguments: Path=..., Mode=...
        parts = [part.strip() for part in re.split(r'(?<!\\),', body)]
        head = parts[0]
        if '=' in head:
            named = dict(
                (kv.split('=', 1)[0].strip(), kv.split('=', 1)[1].strip())
                for kv in parts if '=' in kv)
            head = named.get('Path', '')
        head = head.strip().strip("'\"")
        if not head or head.startswith('(') or head.startswith('/'):
            continue
        head = head.replace('DataContext.', '')
        segment = re.split(r'[.\[]', head)[0]
        if not segment or not segment[0].isalpha():
            continue
        paths.append((segment, head))
    return paths


def check(root):
    errors = []
    warnings = []

    xaml_files = []
    cs_files = []
    for base in ('src', 'tests', 'tools'):
        for dirpath, _, filenames in os.walk(os.path.join(root, base)):
            if 'obj' in dirpath or 'bin' in dirpath:
                continue
            for name in filenames:
                if name.endswith('.xaml'):
                    xaml_files.append(os.path.join(dirpath, name))
                elif name.endswith('.cs'):
                    cs_files.append(os.path.join(dirpath, name))

    if not xaml_files:
        return errors, warnings

    types = _collect_csharp_types(cs_files)
    all_members = set(BASE_MEMBERS) | FRAMEWORK_MEMBERS
    for members in types.values():
        all_members |= members

    resource_keys = _collect_resource_keys(xaml_files)

    for path in sorted(xaml_files):
        rel = os.path.relpath(path, root)
        raw = open(path, encoding='utf-8').read()

        try:
            ET.fromstring(raw)
        except ET.ParseError as exc:
            errors.append(f"{rel}: XAML is not well formed: {exc}")
            continue

        cleaned = _strip_comments(raw)

        # The view model this file binds against, by naming convention.
        stem = os.path.splitext(os.path.basename(path))[0]
        page_members = set(BASE_MEMBERS) | FRAMEWORK_MEMBERS
        vm_name = None
        if stem.endswith('View'):
            vm_name = stem[:-4] + 'ViewModel'
        elif stem in WINDOW_VIEW_MODELS:
            vm_name = WINDOW_VIEW_MODELS[stem]

        if vm_name is not None:
            page_members |= types.get(vm_name, set())

        for segment, full in _binding_paths(cleaned):
            if segment in page_members or segment in all_members:
                continue
            errors.append(f"{rel}: binding '{{Binding {full}}}' has no matching member on {stem} or any view model")

        # Stronger check: bindings outside a DataTemplate bind against the page view model itself. Inside a
        # DataTemplate the item type is not known statically, so those stay on the permissive pass above.
        if vm_name is None:
            continue

        # A window's DataContext is set in code-behind; LoginWindow has none (it only shows a page).
        outer = re.sub(r'<DataTemplate\b.*?</DataTemplate>', '', cleaned, flags=re.S)
        outer = re.sub(r'<HierarchicalDataTemplate\b.*?</HierarchicalDataTemplate>', '', outer, flags=re.S)
        for segment, full in _binding_paths(outer):
            if segment in page_members:
                continue
            errors.append(
                f"{rel}: binding '{{Binding {full}}}' is used outside a DataTemplate but is not a member of {vm_name}")

        for key in re.findall(r'\{StaticResource\s+([^}]+)\}', cleaned):
            name = key.strip()
            if name not in resource_keys:
                errors.append(f"{rel}: StaticResource '{name}' is not defined")

        # x:Class must have a matching partial class in a code-behind file.
        match = re.search(r'x:Class="([A-Za-z0-9_.]+)"', cleaned)
        if match:
            class_name = match.group(1).split('.')[-1]
            if class_name not in types:
                errors.append(f"{rel}: x:Class '{match.group(1)}' has no code-behind class {class_name}")

        # Event handlers must be defined in the code-behind.
        code_behind = os.path.splitext(path)[0] + '.xaml.cs'
        handlers = []
        for attr, value in re.findall(r'\b([A-Za-z]+)="([A-Za-z0-9_]+)"', cleaned):
            if attr in ('Click', 'Checked', 'Unchecked', 'SelectionChanged', 'Loaded', 'Closing', 'Closed',
                        'KeyDown', 'MouseDown', 'MouseDoubleClick', 'TextChanged', 'Drop'):
                handlers.append(value)
        if handlers:
            if not os.path.exists(code_behind):
                errors.append(f"{rel}: uses event handlers {handlers} but there is no {os.path.basename(code_behind)}")
            else:
                code = open(code_behind, encoding='utf-8').read()
                for handler in handlers:
                    if not re.search(r'\b' + re.escape(handler) + r'\s*\(', code):
                        errors.append(f"{rel}: event handler '{handler}' is not defined in {os.path.basename(code_behind)}")

    return errors, warnings


if __name__ == '__main__':
    problems, _ = check(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
    for problem in problems:
        print('ERROR ' + problem)
    print(f"{len(problems)} XAML error(s)")
