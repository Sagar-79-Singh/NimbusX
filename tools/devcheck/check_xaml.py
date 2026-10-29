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
ROOT_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

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


def _collect_member_types(cs_files):
    """class name -> {member name: declared type text} for properties, fields and generated properties."""
    table = {}
    for path in cs_files:
        try:
            text = open(path, encoding='utf-8').read()
        except OSError:
            continue
        body = re.sub(r'//.*', '', text)
        body = re.sub(r'/\*.*?\*/', '', body, flags=re.S)

        members = {}
        for match in re.finditer(
                r'(?:public|internal|private|protected)\s+(?:static\s+|readonly\s+|override\s+|virtual\s+|required\s+)*'
                r'([A-Za-z0-9_<>?.,\[\]\s]+?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:\{|=>|;|\()', body):
            members.setdefault(match.group(2), match.group(1).strip())
        for match in re.finditer(
                r'\[ObservableProperty\][^;{]*?(?:private|protected|internal)\s+([A-Za-z0-9_<>?.,\[\]]+)\s+_([A-Za-z0-9_]+)',
                body, flags=re.S):
            members[match.group(2)[0].upper() + match.group(2)[1:]] = match.group(1)

        for match in re.finditer(
                r'^\s*(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+)*'
                r'(?:class|struct|interface|enum|record)\s+([A-Za-z0-9_]+)', body, re.M):
            table.setdefault(match.group(1), {}).update(members)
    return table


def _element_type(declared):
    """Element type of a collection property such as ObservableCollection<GameEntry>, or None."""
    if not declared:
        return None
    match = re.search(r'<\s*([A-Za-z_][\w.]*)\s*>', declared)
    if match:
        return match.group(1).split('.')[-1]
    return None


def _check_template_bindings(path, vm_name, member_types, type_members):
    """Bindings inside a DataTemplate must exist on the item type of the ItemsSource that uses it."""
    rel = os.path.relpath(path, ROOT_DIR)
    raw = _strip_comments(open(path, encoding='utf-8').read())
    try:
        root = ET.fromstring(raw)
    except ET.ParseError:
        return []

    parents = {}
    for parent in root.iter():
        for child in parent:
            parents[child] = parent

    errors = []
    for element in root.iter():
        tag = element.tag.split('}')[-1]
        if tag not in ('DataTemplate', 'HierarchicalDataTemplate'):
            continue

        owner = parents.get(element)
        items_source = None
        while owner is not None:
            items_source = owner.get('ItemsSource')
            if items_source:
                break
            owner = parents.get(owner)
        if not items_source:
            continue

        match = re.fullmatch(r'\{Binding\s+(?:Path=)?([A-Za-z_][\w.]*)\}', items_source.strip())
        if not match:
            continue
        property_name = match.group(1).split('.')[0]
        declared = member_types.get(vm_name, {}).get(property_name)
        if declared is None:
            declared = member_types.get(vm_name, {}).get(property_name[0].upper() + property_name[1:])
        item_type = _element_type(declared)
        if item_type is None:
            continue
        item_members = type_members.get(item_type)
        if not item_members:
            continue

        text = ET.tostring(element, encoding='unicode')
        for binding in re.findall(r'\{Binding\s+([^}]*)\}', text):
            body = binding.strip()
            if not body or 'RelativeSource' in body or 'ElementName' in body or 'Source=' in body:
                continue
            head = body.split(',')[0].strip()
            if '=' in head:
                head = head.split('=', 1)[1].strip()
            head = head.strip("'\"")
            if not head or head.startswith('('):
                continue
            segment = re.split(r'[.\[]', head)[0]
            if not segment or not segment[0].isalpha():
                continue
            if segment in item_members or segment in FRAMEWORK_MEMBERS:
                continue
            errors.append(
                f"{rel}: binding '{{Binding {head}}}' in a {tag} over '{property_name}' has no member "
                f"'{segment}' on {item_type}")
    return errors


def _collect_resource_keys(xaml_files):
    keys = set()
    for path in xaml_files:
        try:
            text = open(path, encoding='utf-8').read()
        except OSError:
            continue
        keys.update(re.findall(r'x:Key="([^"]+)"', text))
    return keys



# Elements that carry no Style of their own in any XAML file here; used to keep the TargetType check honest.
# Properties every FrameworkElement/UIElement already has. Only the ones used in this tree are listed; the check
# exists to catch a misspelled custom property, not to re-implement WPF.
INHERITED_MEMBERS = {
    'Width', 'Height', 'MinWidth', 'MinHeight', 'MaxWidth', 'MaxHeight', 'Margin', 'Padding',
    'HorizontalAlignment', 'VerticalAlignment', 'Opacity', 'Visibility', 'Tag', 'Name', 'Cursor',
    'ClipToBounds', 'SnapsToDevicePixels', 'UseLayoutRounding', 'IsEnabled', 'ToolTip', 'Style',
    'LayoutTransform', 'RenderTransform', 'FlowDirection', 'Focusable', 'IsHitTestVisible',
}

_SKIP_ATTRS = {'x:Name', 'Name', 'x:Key', 'x:Class', 'x:Type', 'xmlns', 'mc:Ignorable', 'd:DesignHeight',
               'd:DesignWidth', 'ResourceDictionary.MergedDictionaries', 'd:'}


def _style_target_types(files):
    """x:Key -> (TargetType, BasedOn) for every keyed Style in the tree."""
    styles = {}
    for path in files:
        text = _strip_comments(open(path, encoding='utf-8').read())
        for match in re.finditer(r'<Style\b([^>]*)>', text):
            attributes = match.group(1)
            key = re.search(r'x:Key="([^"]+)"', attributes)
            if not key:
                continue
            target = re.search(r'TargetType="\{?(?:[A-Za-z_]\w*:)?([A-Za-z0-9_]+)\}?"', attributes)
            based = re.search(r'BasedOn="\{StaticResource\s+([^}]+)\}"', attributes)
            styles[key.group(1)] = (
                target.group(1) if target else None,
                based.group(1).strip() if based else None)
    return styles


def _check_styles(files):
    """TargetType compatibility for Styles and the elements that use them."""
    errors = []
    styles = _style_target_types(files)

    for key, (target, based) in styles.items():
        if not based or based not in styles:
            continue
        inherited = styles[based][0]
        if target and inherited and target != inherited:
            errors.append(
                f"Theme: style '{key}' is based on '{based}' whose TargetType is {inherited}, but its own "
                f"TargetType is {target}")

    for path in files:
        rel = os.path.relpath(path, ROOT_DIR)
        text = _strip_comments(open(path, encoding='utf-8').read())
        for match in re.finditer(r'<(?:(\w+):)?(\w+)\b([^>]*)>', text):
            prefix, element, attributes = match.group(1), match.group(2), match.group(3)
            used = re.search(r'Style="\{StaticResource\s+([^}]+)\}"', attributes)
            if not used:
                continue
            name = used.group(1).strip()
            if name not in styles:
                continue
            target = styles[name][0]
            if target and target != element:
                errors.append(
                    f"{rel}: <{element}> uses style '{name}' whose TargetType is {target}")
    return errors


def _check_names(path):
    rel = os.path.relpath(path, ROOT_DIR)
    text = _strip_comments(open(path, encoding='utf-8').read())
    names = re.findall(r'x:Name="([^"]+)"', text)
    duplicates = sorted({name for name in names if names.count(name) > 1})
    return [f"{rel}: x:Name '{name}' is used more than once" for name in duplicates]


def _check_custom_attributes(path, types):
    """Attributes on a custom (clr-namespace) element must be a property that class declares."""
    rel = os.path.relpath(path, ROOT_DIR)
    text = _strip_comments(open(path, encoding='utf-8').read())
    prefixes = dict(re.findall(r'xmlns:(\w+)="clr-namespace:([^";]+)(?:;assembly=[^"]*)?"', text))
    errors = []

    for match in re.finditer(r'<(\w+):(\w+)\b([^>]*?)/?>', text):
        prefix, element, attributes = match.group(1), match.group(2), match.group(3)
        namespace = prefixes.get(prefix)
        if not namespace or not namespace.startswith('NimbusX'):
            continue
        members = types.get(element)
        if members is None:
            continue
        for attribute, _ in re.findall(r'([A-Za-z_][\w.]*)="([^"]*)"', attributes):
            if attribute.startswith('x:') or attribute in ('Name',):
                continue
            if '.' in attribute:
                continue
            if attribute not in members and attribute not in INHERITED_MEMBERS:
                errors.append(f"{rel}: <{element}> has no property '{attribute}'")
    return errors


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
    member_types = _collect_member_types(cs_files)
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

        if vm_name is not None:
            errors.extend(_check_template_bindings(path, vm_name, member_types, types))

        errors.extend(_check_names(path))
        errors.extend(_check_custom_attributes(path, types))

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

    errors.extend(_check_styles(sorted(xaml_files)))

    return errors, warnings


if __name__ == '__main__':
    problems, _ = check(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
    for problem in problems:
        print('ERROR ' + problem)
    print(f"{len(problems)} XAML error(s)")
