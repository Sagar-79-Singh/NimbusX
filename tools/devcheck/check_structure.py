#!/usr/bin/env python3
"""Structural checks that a compiler would catch before it compiles any code.

No .NET SDK is available in the authoring environment, so these checks stand in for the parts of a build that do
not need a compiler: the solution and project files, project references, package versions, target framework
compatibility, namespace reachability across project references, and XAML type references.
"""
import os
import re
import xml.etree.ElementTree as ET

MSBUILD_NS = '{http://schemas.microsoft.com/developer/msbuild/2003}'


def _cs_files(root, project_dir):
    for dirpath, dirnames, filenames in os.walk(project_dir):
        dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
        for name in filenames:
            if name.endswith('.cs'):
                yield os.path.join(dirpath, name)


def _declared_namespaces(path):
    text = open(path, encoding='utf-8').read()
    text = re.sub(r'//.*', '', text)
    found = set()
    for match in re.finditer(r'^\s*namespace\s+([A-Za-z0-9_.]+)\s*(;|\{)', text, re.M):
        found.add(match.group(1))
    return found


def _project_info(root, csproj):
    tree = ET.parse(csproj)
    node = tree.getroot()
    entry = {
        'path': csproj,
        'name': os.path.splitext(os.path.basename(csproj))[0],
        'tfm': '',
        'references': [],
        'packages': [],
    }

    for element in node.iter():
        tag = element.tag.replace(MSBUILD_NS, '')
        if tag == 'TargetFramework':
            entry['tfm'] = (element.text or '').strip()
        elif tag == 'ProjectReference':
            include = element.get('Include') or ''
            entry['references'].append(os.path.normpath(os.path.join(os.path.dirname(csproj), include.replace('\\', '/'))))
        elif tag == 'PackageReference':
            entry['packages'].append(element.get('Include') or '')

    entry['namespaces'] = set()
    for path in _cs_files(root, os.path.dirname(csproj)):
        entry['namespaces'] |= _declared_namespaces(path)
    return entry


def _declared_packages(root):
    props = os.path.join(root, 'Directory.Packages.props')
    names = set()
    if os.path.exists(props):
        for element in ET.parse(props).getroot().iter():
            if element.tag.replace(MSBUILD_NS, '') == 'PackageVersion':
                names.add(element.get('Include') or '')
    return names


def _solution_projects(root):
    sln = os.path.join(root, 'NimbusX.sln')
    entries = []
    with open(sln, encoding='utf-8-sig') as handle:
        for line in handle:
            match = re.match(r'\s*Project\("\{([^}]+)\}"\)\s*=\s*"([^"]+)",\s*"([^"]+)"', line)
            if match:
                entries.append({'type': match.group(1), 'name': match.group(2),
                                'rel': match.group(3).replace('\\', '/')})
    return entries


def check(root):
    errors = []
    warnings = []

    csproj_files = []
    for base in ('src', 'tests', 'tools'):
        for dirpath, dirnames, filenames in os.walk(os.path.join(root, base)):
            dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
            for name in filenames:
                if name.endswith('.csproj'):
                    csproj_files.append(os.path.join(dirpath, name))

    packages = _declared_packages(root)
    projects = {}
    for csproj in sorted(csproj_files):
        try:
            info = _project_info(root, csproj)
        except ET.ParseError as exc:
            errors.append(f"{os.path.relpath(csproj, root)}: not well formed XML: {exc}")
            continue
        projects[os.path.normpath(csproj)] = info

        rel = os.path.relpath(csproj, root)
        if not info['tfm']:
            errors.append(f"{rel}: no TargetFramework")
        if not info['namespaces']:
            errors.append(f"{rel}: declares no namespace (are the sources missing?)")

        # Every ProjectReference must exist and be TFM compatible.
        for reference in info['references']:
            if not os.path.exists(reference):
                errors.append(f"{rel}: ProjectReference '{os.path.relpath(reference, root)}' does not exist")
                continue
            target = projects.get(reference)
            if target is None:
                continue
            if _is_windows_only(target['tfm']) and not _is_windows_only(info['tfm']):
                errors.append(
                    f"{rel}: '{info['tfm']}' project cannot reference '{target['tfm']}' project {target['name']}")

        # Every PackageReference must have a central version.
        for package in info['packages']:
            if packages and package not in packages:
                errors.append(f"{rel}: package '{package}' has no PackageVersion in Directory.Packages.props")

    # Unreferenced project files are a warning: the build would silently skip them.
    sln_projects = _solution_projects(root)
    sln_paths = set()
    for entry in sln_projects:
        path = os.path.normpath(os.path.join(root, entry['rel']))
        sln_paths.add(path)
        if not os.path.exists(path):
            errors.append(f"NimbusX.sln: project '{entry['rel']}' does not exist on disk")
    for csproj in projects:
        if csproj not in sln_paths:
            errors.append(f"{os.path.relpath(csproj, root)}: project is not in NimbusX.sln")

    guids = re.findall(r'Project\("\{[^}]+\}"\)\s*=\s*"[^"]+",\s*"[^"]+",\s*"(\{[^}]+\})"', open(
        os.path.join(root, 'NimbusX.sln'), encoding='utf-8-sig').read())
    if len(guids) != len(set(guids)):
        errors.append('NimbusX.sln: duplicate project GUIDs')

    # Namespace reachability: a using directive must name a namespace that exists and is reachable through the
    # project's own references (including transitive ones).
    def reachable(project_path, seen=None):
        seen = seen or set()
        if project_path in seen:
            return set()
        seen.add(project_path)
        info = projects.get(project_path)
        if info is None:
            return set()
        namespaces = set(info['namespaces'])
        for reference in info['references']:
            namespaces |= reachable(reference, seen)
        return namespaces

    for project_path, info in projects.items():
        available = reachable(project_path)
        rel = os.path.relpath(project_path, root)
        for path in _cs_files(root, os.path.dirname(project_path)):
            text = open(path, encoding='utf-8').read()
            text = re.sub(r'//.*', '', text)
            for match in re.finditer(r'^\s*using\s+(NimbusX(?:\.[A-Za-z0-9_]+)+)\s*;', text, re.M):
                namespace = match.group(1)
                if namespace in available:
                    continue
                if namespace in all_namespaces(projects):
                    errors.append(
                        f"{os.path.relpath(path, root)}: uses namespace '{namespace}' but {rel} does not reference "
                        f"the project that declares it")
                else:
                    errors.append(f"{os.path.relpath(path, root)}: uses unknown namespace '{namespace}'")

    errors.extend(_check_xaml_types(root, projects))

    return errors, warnings


def all_namespaces(projects):
    namespaces = set()
    for info in projects.values():
        namespaces |= info['namespaces']
    return namespaces


def _is_windows_only(tfm):
    return '-windows' in (tfm or '')


def _check_xaml_types(root, projects):
    errors = []
    for base in ('src', 'tests', 'tools'):
        for dirpath, dirnames, filenames in os.walk(os.path.join(root, base)):
            dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
            for name in filenames:
                if not name.endswith('.xaml'):
                    continue
                path = os.path.join(dirpath, name)
                rel = os.path.relpath(path, root)
                text = open(path, encoding='utf-8').read()

                prefixes = dict(re.findall(r'xmlns:(\w+)="clr-namespace:([^";]+)(?:;assembly=[^"]*)?"', text))
                for prefix, namespace in prefixes.items():
                    if namespace.startswith('NimbusX') and namespace not in all_namespaces(projects):
                        errors.append(f"{rel}: xmlns:{prefix} names unknown namespace '{namespace}'")

                for prefix, type_name in re.findall(r'x:Type\s+(\w+):(\w+)', text):
                    namespace = prefixes.get(prefix)
                    if namespace and namespace.startswith('NimbusX'):
                        _require_type(errors, root, projects, rel, namespace, type_name)

                for prefix, type_name in re.findall(r'<(\w+):(\w+)[\s/>]', text):
                    namespace = prefixes.get(prefix)
                    if namespace and namespace.startswith('NimbusX'):
                        _require_type(errors, root, projects, rel, namespace, type_name)

    return errors


def _require_type(errors, root, projects, rel, namespace, type_name):
    for project_path, info in projects.items():
        if namespace not in info['namespaces']:
            continue
        for path in _cs_files(root, os.path.dirname(project_path)):
            text = open(path, encoding='utf-8').read()
            if not re.search(r'namespace\s+' + re.escape(namespace) + r'\b', text):
                continue
            if re.search(r'\b(?:class|struct|interface|enum|record)\s+' + re.escape(type_name) + r'\b', text):
                return True
    errors.append(f"{rel}: type '{namespace}.{type_name}' is not defined in the solution")
    return False


if __name__ == '__main__':
    problems, _ = check(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
    for problem in problems:
        print('ERROR ' + problem)
    print(f"{len(problems)} structural error(s)")
