#!/usr/bin/env python3
"""Prove that tools/devcheck actually catches the mistakes it claims to catch.

A static checker that silently stops checking is worse than no checker at all, so this script copies the tree to a
temporary folder, injects one realistic fault at a time, and asserts that check_csharp.py fails with the expected
message. The real tree is never modified.
"""
import os
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

FAULTS = [
    (
        'binding typo in XAML',
        'src/NimbusX.Desktop/Views/DashboardView.xaml',
        'Text="{Binding Headline}"',
        'Text="{Binding Headlien}"',
        "binding '{Binding Headlien}'",
    ),
    (
        'style used on the wrong element type',
        'src/NimbusX.Desktop/Themes/Theme.xaml',
        '<Style x:Key="ThinProgress" TargetType="ProgressBar">',
        '<Style x:Key="ThinProgress" TargetType="Button">',
        "uses style 'ThinProgress' whose TargetType is Button",
    ),
    (
        'duplicate x:Name in one file',
        'src/NimbusX.Desktop/Views/DashboardView.xaml',
        '<ScrollViewer Style="{StaticResource RoundedScrollViewer}">',
        '<ScrollViewer x:Name="Body" Style="{StaticResource RoundedScrollViewer}"><Border x:Name="Body" />',
        "x:Name 'Body' is used more than once",
    ),
    (
        'unknown property on a custom control',
        'src/NimbusX.Desktop/Views/DashboardView.xaml',
        '<controls:RingMeter Width="46" Height="46" Value="{Binding QuotaFraction}"',
        '<controls:RingMeter Width="46" Height="46" Valu="{Binding QuotaFraction}"',
        "<RingMeter> has no property 'Valu'",
    ),
    (
        'interface member not implemented',
        'tests/NimbusX.Tests/TestSupport.cs',
        'public IReadOnlyList<LogEntry> RecentEntries(int count)',
        'public IReadOnlyList<LogEntry> RecentEntriesX(int count)',
        "implements IAppLogger but has no member 'RecentEntries'",
    ),
    (
        'override of a member that does not exist',
        'src/NimbusX.Desktop/ViewModels/DashboardViewModel.cs',
        'public override async Task RefreshAsync(CancellationToken cancellationToken)',
        'public override async Task RefreshEverythingAsync(CancellationToken cancellationToken)',
        "overrides 'RefreshEverythingAsync'",
    ),
    (
        'unknown namespace',
        'src/NimbusX.Core/AppInfo.cs',
        'namespace NimbusX.Core;',
        'using NimbusX.Does.Not.Exist;\n\nnamespace NimbusX.Core;',
        "uses unknown namespace 'NimbusX.Does.Not.Exist'",
    ),
    (
        'namespace from a project that is not referenced',
        'src/NimbusX.Core/AppInfo.cs',
        'namespace NimbusX.Core;',
        'using NimbusX.Cache;\n\nnamespace NimbusX.Core;',
        "does not reference the project that declares it",
    ),
    (
        'static resource that does not exist',
        'src/NimbusX.Desktop/Views/DashboardView.xaml',
        'Style="{StaticResource SectionText}"',
        'Style="{StaticResource SectionTxet}"',
        "StaticResource 'SectionTxet' is not defined",
    ),
    (
        'call to a static member that does not exist',
        'src/NimbusX.Desktop/MainWindow.xaml.cs',
        'Title = AppInfo.WindowTitle;',
        'Title = AppInfo.WindowTitles;',
        "'AppInfo' has no member 'WindowTitles'",
    ),
    (
        'call to a formatting helper that does not exist',
        'src/NimbusX.Desktop/ViewModels/GameLibraryViewModel.cs',
        'TextFormat.Pluralize(Games.Count, "game")',
        'TextFormat.Pluralise(Games.Count, "game")',
        "'TextFormat' has no member 'Pluralise'",
    ),
    (
        'too few arguments to a constructor',
        'src/NimbusX.Mount/RcloneProvisioner.cs',
        'new RcloneBinaryInfo(flavor, path, exists: false, checksumVerified: false, version: null, sha256: null, error: null)',
        'new RcloneBinaryInfo(flavor, path)',
        "'new RcloneBinaryInfo' takes 7 argument(s) but got 2",
    ),
    (
        'static helper called with too few arguments',
        'src/NimbusX.Providers/CloudAccount.cs',
        'return ByteSize.Format(QuotaUsedBytes.Value) + " of "',
        'return ByteSize.Format() + " of "',
        "'ByteSize.Format' takes 1 argument(s) but got 0",
    ),
    (
        'instance method called through the type name',
        'tests/NimbusX.Tests/CoreTests.cs',
        'string path = folder.Combine("settings.json");\n',
        'string path = folder.Combine("settings.json");\n        AppPaths.EnsureCreated();\n',
        "'AppPaths.EnsureCreated' is an instance method",
    ),
    (
        'syntax error that no regex would notice',
        'src/NimbusX.Core/AppInfo.cs',
        'public const string Name = "NimbusX";',
        'public const string Name = "NimbusX"',
        'missing ;',
    ),
    (
        'installer wants administrator rights',
        'installer/NimbusX.iss',
        'PrivilegesRequired=lowest',
        'PrivilegesRequired=admin',
        'must not require administrator rights',
    ),
]


def main():
    failures = 0
    staging = tempfile.mkdtemp(prefix='nimbusx-selftest-')
    try:
        for name, relative, old, new, expected in FAULTS:
            work = os.path.join(staging, 'tree')
            if os.path.isdir(work):
                shutil.rmtree(work)
            shutil.copytree(ROOT, work, ignore=shutil.ignore_patterns('.git', 'bin', 'obj', '__pycache__'))

            target = os.path.join(work, relative)
            text = open(target, encoding='utf-8').read()
            if old not in text:
                print(f'STALE  {name}: pattern not found in {relative}')
                failures += 1
                continue
            open(target, 'w', encoding='utf-8').write(text.replace(old, new, 1))

            result = subprocess.run(
                [sys.executable, os.path.join('tools', 'devcheck', 'check_csharp.py')],
                cwd=work, capture_output=True, text=True)
            output = result.stdout + result.stderr

            if result.returncode != 0 and expected in output:
                print(f'OK     {name}')
            else:
                print(f'FAILED {name}: expected exit 1 containing {expected!r}')
                print(output[-2000:])
                failures += 1
    finally:
        shutil.rmtree(staging, ignore_errors=True)

    print(f'{len(FAULTS) - failures}/{len(FAULTS)} injected faults detected')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
