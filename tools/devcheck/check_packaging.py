#!/usr/bin/env python3
"""Checks for the files that only a Windows box with Inno Setup and PowerShell could really build.

Without those tools the most valuable thing to verify is that the installer script and the build scripts are
internally consistent: every file they name exists, the per-user/no-admin install rules are still in place, and the
PowerShell syntax is balanced. Workflow YAML is parsed when PyYAML is available.
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

REQUIRED_ISS_DIRECTIVES = {
    'PrivilegesRequired': 'lowest',
    'ArchitecturesAllowed': 'x64compatible',
    'ArchitecturesInstallIn64BitMode': 'x64compatible',
    'MinVersion': '10.0.17763',
    'DefaultDirName': '{localappdata}\\Programs\\{#AppName}',
}

KNOWN_SECTIONS = {
    'Setup', 'Types', 'Components', 'Tasks', 'Dirs', 'Files', 'Icons', 'INI', 'InstallDelete',
    'Languages', 'Messages', 'CustomMessages', 'Registry', 'Run', 'UninstallRun', 'UninstallDelete',
    'Code',
}


def check_installer():
    errors = []
    path = os.path.join(ROOT, 'installer', 'NimbusX.iss')
    if not os.path.exists(path):
        return ['installer/NimbusX.iss is missing']

    text = open(path, encoding='utf-8').read()
    lines = text.splitlines()

    current = None
    for number, line in enumerate(lines, start=1):
        stripped = line.strip()
        if not stripped or stripped.startswith(';') or stripped.startswith('#'):
            # Inno Setup preprocessor lines (#define, #ifndef, ...) may appear before any section.
            continue
        if stripped.startswith('[') and stripped.endswith(']'):
            current = stripped[1:-1]
            if current not in KNOWN_SECTIONS:
                errors.append(f'installer/NimbusX.iss:{number}: unknown section [{current}]')
            continue
        if current is None:
            errors.append(f'installer/NimbusX.iss:{number}: content outside any section')
            continue
        if current == 'Files' and stripped.lower().startswith('source:'):
            match = re.search(r'Source:\s*"([^"]+)"', stripped)
            if match:
                source = match.group(1).replace('\\\\', '\\')
                if '{#PayloadDir}' not in source:
                    resolved = os.path.normpath(os.path.join(ROOT, 'installer', source.replace('\\', '/')))
                    if not os.path.exists(resolved):
                        errors.append(
                            f'installer/NimbusX.iss:{number}: Source "{source}" does not exist at packaging time')

    for directive, expected in REQUIRED_ISS_DIRECTIVES.items():
        match = re.search(r'^' + re.escape(directive) + r'=(.*)$', text, re.M)
        if not match:
            errors.append(f'installer/NimbusX.iss: required directive {directive}= is missing')
        elif match.group(1).strip() != expected:
            errors.append(
                f'installer/NimbusX.iss: {directive}={match.group(1).strip()} but NimbusX requires {expected}')

    if re.search(r'^PrivilegesRequired\s*=\s*admin', text, re.M | re.I):
        errors.append('installer/NimbusX.iss: the installer must not require administrator rights')

    for directive in ('LicenseFile', 'InfoBeforeFile'):
        match = re.search(r'^' + directive + r'=(.*)$', text, re.M)
        if not match:
            errors.append(f'installer/NimbusX.iss: {directive} is missing')
            continue
        relative = match.group(1).strip().replace('\\', '/')
        if not os.path.exists(os.path.normpath(os.path.join(ROOT, 'installer', relative))):
            errors.append(f'installer/NimbusX.iss: {directive} "{relative}" does not exist')

    return errors


def _strip_powershell(text):
    """Remove comments and string literals so bracket counting is meaningful."""
    text = re.sub(r'<#.*?#>', '', text, flags=re.S)
    text = re.sub(r'(?m)(?<!`)#[^\r\n]*', '', text)
    text = re.sub(r'"(?:`.|[^"`])*"', '""', text)
    text = re.sub(r"'(?:''|[^'])*'", "''", text)
    return text


def check_powershell():
    errors = []
    scripts = []
    for dirpath, _, filenames in os.walk(os.path.join(ROOT, 'scripts')):
        for name in filenames:
            if name.endswith('.ps1'):
                scripts.append(os.path.join(dirpath, name))

    if not scripts:
        return ['scripts/ contains no PowerShell scripts']

    for path in sorted(scripts):
        rel = os.path.relpath(path, ROOT)
        raw = open(path, encoding='utf-8').read()

        if raw.count('<#') != raw.count('#>'):
            errors.append(f'{rel}: unbalanced <# #> comment block')

        stripped = _strip_powershell(raw)
        for opening, closing, label in (('{', '}', 'curly braces'), ('(', ')', 'parentheses'),
                                        ('[', ']', 'brackets')):
            if stripped.count(opening) != stripped.count(closing):
                errors.append(
                    f'{rel}: unbalanced {label} ({stripped.count(opening)} {opening} vs '
                    f'{stripped.count(closing)} {closing})')

        # Every script this one calls must exist.
        for referenced in re.findall(r"Join-Path\s+\$PSScriptRoot\s+'([^']+\.ps1)'", raw):
            if not os.path.exists(os.path.join(os.path.dirname(path), referenced)):
                errors.append(f'{rel}: calls {referenced}, which does not exist')

        if not re.search(r'Set-StrictMode|ErrorActionPreference', raw):
            errors.append(f'{rel}: does not set strict mode or $ErrorActionPreference')

    return errors


def check_workflows():
    errors = []
    directory = os.path.join(ROOT, '.github', 'workflows')
    files = sorted(
        os.path.join(directory, name) for name in os.listdir(directory) if name.endswith(('.yml', '.yaml'))
    ) if os.path.isdir(directory) else []

    if not files:
        return ['.github/workflows contains no workflow']

    try:
        import yaml  # noqa: PLC0415 - optional dependency
    except ImportError:
        yaml = None

    if yaml is None:
        print('note   PyYAML is not installed, workflow YAML is only searched textually')

    for path in files:
        rel = os.path.relpath(path, ROOT)
        text = open(path, encoding='utf-8').read()

        if yaml is not None:
            try:
                document = yaml.safe_load(text)
            except yaml.YAMLError as exc:
                errors.append(f'{rel}: invalid YAML: {exc}')
                continue
            if not isinstance(document, dict) or 'jobs' not in document:
                errors.append(f'{rel}: no jobs block')
                continue
            for job_name, job in (document.get('jobs') or {}).items():
                if not isinstance(job, dict):
                    errors.append(f'{rel}: job {job_name} is not a mapping')
                    continue
                if 'runs-on' not in job:
                    errors.append(f'{rel}: job {job_name} has no runs-on')
                steps = job.get('steps') or []
                if not steps:
                    errors.append(f'{rel}: job {job_name} has no steps')
                for index, step in enumerate(steps):
                    if not isinstance(step, dict):
                        continue
                    if 'uses' not in step and 'run' not in step:
                        errors.append(f'{rel}: job {job_name} step {index} has neither uses nor run')
                    if 'uses' in step and '@' not in str(step['uses']):
                        errors.append(f"{rel}: job {job_name} step {index} pins no version: {step['uses']}")

    return errors


def check():
    return check_installer() + check_powershell() + check_workflows()


if __name__ == '__main__':
    problems = check()
    for problem in problems:
        print('ERROR ' + problem)
    print(f'{len(problems)} packaging error(s)')
    sys.exit(1 if problems else 0)
