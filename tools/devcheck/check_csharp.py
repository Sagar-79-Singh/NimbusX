#!/usr/bin/env python3
"""Static sanity checks for the NimbusX C# tree.

This is NOT a compiler. It catches the mechanical failures that are easy to make when
writing a large solution without an SDK on the build machine:

  * unbalanced braces / parentheses / brackets
  * namespaces that do not match the folder layout
  * missing usings for types used in the file
  * duplicate type declarations inside a namespace
  * files that declare more than one top level type with the same name
  * TODO/placeholder markers left in shipping code
  * XAML: binding names that have no matching property on the view model
  * project/solution structure: missing projects, references, package versions, TFM compatibility
  * namespace reachability across project references
  * interface implementation and override validity for types declared in this solution
  * installer, build scripts and workflows (see check_packaging.py)

Exit code 1 when an error is found.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_packaging
import check_structure
import check_types
import check_xaml

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# type -> required using, only applied when the using is not already present
USING_RULES = {
    r'\bList<': 'System.Collections.Generic',
    r'\bDictionary<': 'System.Collections.Generic',
    r'\bHashSet<': 'System.Collections.Generic',
    r'\bIReadOnlyList<': 'System.Collections.Generic',
    r'\bIReadOnlyDictionary<': 'System.Collections.Generic',
    r'\bIReadOnlySet<': 'System.Collections.Generic',
    r'\bIEnumerable<': 'System.Collections.Generic',
    r'\bIList<': 'System.Collections.Generic',
    r'\bQueue<': 'System.Collections.Generic',
    r'\bStack<': 'System.Collections.Generic',
    r'\bTask\b': 'System.Threading.Tasks',
    r'\bCancellationTokenSource\b': 'System.Threading',
    r'\bCancellationToken\b': 'System.Threading',
    r'\bInterlocked\b': 'System.Threading',
    r'\bStopwatch\b': 'System.Diagnostics',
    r'\bnew Process\(|(?<!\.)\bProcess\.': 'System.Diagnostics',
    r'\bRegex\b': 'System.Text.RegularExpressions',
    r'\bJsonSerializer\b': 'System.Text.Json',
    r'\bJsonSerializerOptions\b': 'System.Text.Json',
    r'\bStringBuilder\b': 'System.Text',
    r'\bFile\.': 'System.IO',
    r'\bDirectory\.': 'System.IO',
    r'\bPath\.': 'System.IO',
    r'\bFileStream\b': 'System.IO',
    r'\bStreamReader\b': 'System.IO',
    r'\bStreamWriter\b': 'System.IO',
    r'\bFileInfo\b': 'System.IO',
    r'\bDirectoryInfo\b': 'System.IO',
    r'\bDriveInfo\b': 'System.IO',
    r'\bEncoding\b': 'System.Text',
    r'\bIPAddress\b': 'System.Net',
    r'\bIPEndPoint\b': 'System.Net',
    r'\bTcpListener\b': 'System.Net.Sockets',
    r'\bTcpClient\b': 'System.Net.Sockets',
    r'\bHttpClient\b': 'System.Net.Http',
    r'\bHttpRequestMessage\b': 'System.Net.Http',
    r'\bHttpResponseMessage\b': 'System.Net.Http',
    r'\bDateTimeOffset\b': 'System',
    r'\bDateTime\b': 'System',
    r'\bTimeSpan\b': 'System',
    r'\bGuid\b': 'System',
    r'\bException\b': 'System',
    r'\bMath\.': 'System',
    r'\bConvert\.': 'System',
    r'\bDllImport\b': 'System.Runtime.InteropServices',
    r'\bMarshal\.': 'System.Runtime.InteropServices',
    r'\bStructLayout\b': 'System.Runtime.InteropServices',
    r'\bIntPtr\b': 'System',
    r'\bObservableCollection<': 'System.Collections.ObjectModel',
    r'\bCultureInfo\b': 'System.Globalization',
    r'\bZipArchive\b': 'System.IO.Compression',
    r'\bZipFile\b': 'System.IO.Compression',
    r'\bJsonNode\b': 'System.Text.Json.Nodes',
    r'\bJsonObject\b': 'System.Text.Json.Nodes',
    r'\bJsonArray\b': 'System.Text.Json.Nodes',
    r'\bAuthenticationHeaderValue\b': 'System.Net.Http.Headers',
    r'\bHttpStatusCode\b': 'System.Net',
    r'\bThread\b': 'System.Threading',
    r'\bMutex\b': 'System.Threading',
    r'\bSemaphoreSlim\b': 'System.Threading',
    r'\bConcurrentDictionary<': 'System.Collections.Concurrent',
    r'\bConcurrentQueue<': 'System.Collections.Concurrent',
    r'\bX509Certificate2\b': 'System.Security.Cryptography.X509Certificates',
    r'\bSHA256\b': 'System.Security.Cryptography',
    r'\bRandomNumberGenerator\b': 'System.Security.Cryptography',
    r'\bRegistryKey\b': 'Microsoft.Win32',
}

ERRORS = []
WARNINGS = []


def rel(path):
    return os.path.relpath(path, ROOT).replace('\\', '/')


def check_balance(path, text):
    pairs = {'{': '}', '(': ')', '[': ']'}
    closing = {v: k for k, v in pairs.items()}
    stack = []
    in_str = False
    in_char = False
    in_line_comment = False
    in_block_comment = False
    in_verbatim = False
    i = 0
    line = 1
    while i < len(text):
        c = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ''
        if c == '\n':
            line += 1
            in_line_comment = False
            i += 1
            continue
        if in_line_comment:
            i += 1
            continue
        if in_block_comment:
            if c == '*' and nxt == '/':
                in_block_comment = False
                i += 2
                continue
            i += 1
            continue
        if in_verbatim:
            if c == '"' and nxt == '"':
                i += 2
                continue
            if c == '"':
                in_verbatim = False
            i += 1
            continue
        if in_str:
            if c == '\\':
                i += 2
                continue
            if c == '"':
                in_str = False
            i += 1
            continue
        if in_char:
            if c == '\\':
                i += 2
                continue
            if c == "'":
                in_char = False
            i += 1
            continue
        if c == '/' and nxt == '/':
            in_line_comment = True
            i += 2
            continue
        if c == '/' and nxt == '*':
            in_block_comment = True
            i += 2
            continue
        if c == '@' and nxt == '"':
            in_verbatim = True
            i += 2
            continue
        if c == '"':
            in_str = True
            i += 1
            continue
        if c == "'":
            in_char = True
            i += 1
            continue
        if c in pairs:
            stack.append((c, line))
        elif c in closing:
            if not stack:
                ERRORS.append(f"{rel(path)}:{line}: unexpected '{c}'")
                return
            opener, opened_at = stack.pop()
            if opener != closing[c]:
                ERRORS.append(f"{rel(path)}:{line}: '{c}' closes '{opener}' opened at line {opened_at}")
                return
        i += 1
    if stack:
        opener, opened_at = stack[-1]
        ERRORS.append(f"{rel(path)}:{opened_at}: unclosed '{opener}'")


def check_usings(path, text):
    usings = set(re.findall(r'^using\s+([A-Za-z0-9_.]+)\s*;', text, re.M))
    # strip comments and string literals crudely for the usage scan
    body = re.sub(r'//.*', '', text)
    body = re.sub(r'/\*.*?\*/', '', body, flags=re.S)
    for pattern, required in USING_RULES.items():
        if re.search(pattern, body) and required not in usings:
            ERRORS.append(f"{rel(path)}: uses {pattern} but is missing 'using {required};'")


def check_namespace(path, text):
    match = re.search(r'^namespace\s+([A-Za-z0-9_.]+)\s*;', text, re.M)
    if not match:
        ERRORS.append(f"{rel(path)}: no file-scoped namespace declaration")
        return None
    namespace = match.group(1)
    folder = os.path.basename(os.path.dirname(path))
    parts = namespace.split('.')
    if folder not in ('NimbusX.Core',) and folder not in parts and not any(p == folder for p in parts):
        project = path.split(os.sep + 'src' + os.sep)[1].split(os.sep)[0] if os.sep + 'src' + os.sep in path else ''
        if project and project.replace('.', '') not in namespace.replace('.', ''):
            ERRORS.append(f"{rel(path)}: namespace '{namespace}' does not match project '{project}'")
    return namespace


def check_cross_namespace(files):
    """Flag our own types used without a using for their namespace."""
    declarations = {}
    for path in files:
        text = open(path, encoding='utf-8').read()
        ns = re.search(r'^namespace\s+([A-Za-z0-9_.]+)\s*;', text, re.M)
        if not ns:
            continue
        for match in re.finditer(r'^\s*(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+)*(?:class|struct|interface|enum|record)\s+([A-Za-z0-9_]+)', text, re.M):
            declarations.setdefault(match.group(1), set()).add(ns.group(1))

    for path in files:
        text = open(path, encoding='utf-8').read()
        ns_match = re.search(r'^namespace\s+([A-Za-z0-9_.]+)\s*;', text, re.M)
        if not ns_match:
            continue
        own = ns_match.group(1)
        usings = set(re.findall(r'^using\s+([A-Za-z0-9_.]+)\s*;', text, re.M))
        visible = set(usings)
        # enclosing namespaces of `own` are searched implicitly
        parts = own.split('.')
        for i in range(len(parts), 0, -1):
            visible.add('.'.join(parts[:i]))
        body = re.sub(r'//.*', '', text)
        body = re.sub(r'/\*.*?\*/', '', body, flags=re.S)
        members = set(re.findall(r'(?:public|internal|private|protected)\s+(?:static\s+|readonly\s+|override\s+|virtual\s+|async\s+)*[A-Za-z0-9_<>?.,\[\]\s]+?\s+([A-Z][A-Za-z0-9_]*)\s*(?:\{|=>|;|\()', body))
        # CommunityToolkit.Mvvm source generator: [ObservableProperty] private string _foo; declares Foo
        for field in re.findall(r'\[ObservableProperty\][^;]*?private\s+[A-Za-z0-9_<>?.,\[\]]+\s+_([A-Za-z0-9_]+)\s*(?:=[^;]*)?;', body, flags=re.S):
            members.add(field[0].upper() + field[1:])
        for field in re.findall(r'\[RelayCommand\][^;{]*?\b([A-Za-z0-9_]+)Async\s*\(', body):
            members.add(field[0].upper() + field[1:] + "Command")
        for field in re.findall(r'\[RelayCommand\][^;{]*?\b([A-Za-z0-9_]+)\s*\(', body):
            members.add(field[0].upper() + field[1:] + "Command")
        for type_name, namespaces in declarations.items():
            if type_name in members:
                continue
            if len(namespaces) > 1:
                continue
            type_ns = next(iter(namespaces))
            if type_ns in visible:
                continue
            if type_ns == own:
                continue
            # A few short type names also occur inside ordinary strings ("Program Files", "Process is
            # running"), so require member access (Type.Member) before reporting them.
            if type_name in ('Program', 'Process', 'Task', 'Path', 'App', 'MainWindow'):
                pattern = r'(?<![\w.])' + re.escape(type_name) + r'\s*\.'
            else:
                pattern = r'(?<![\w.])' + re.escape(type_name) + r'(?!\w)'
            if re.search(pattern, body):
                ERRORS.append(f"{rel(path)}: uses type '{type_name}' from namespace '{type_ns}' without a using")


def main():
    cs_files = []
    for base in ('src', 'tests', 'tools'):
        for dirpath, _, filenames in os.walk(os.path.join(ROOT, base)):
            if 'obj' in dirpath or 'bin' in dirpath:
                continue
            for name in filenames:
                if name.endswith('.cs'):
                    cs_files.append(os.path.join(dirpath, name))

    declared = {}
    for path in sorted(cs_files):
        text = open(path, encoding='utf-8').read()
        check_balance(path, text)
        check_usings(path, text)
        check_namespace(path, text)
        for mistake in ('TODO:', 'FIXME:', 'NotImplementedException', 'throw new NotSupportedException("todo'):
            if mistake in text:
                ERRORS.append(f"{rel(path)}: contains '{mistake}'")
        for match in re.finditer(r'^\s*(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+)*(?:class|struct|interface|enum|record)\s+([A-Za-z0-9_]+)', text, re.M):
            declared.setdefault(match.group(1), []).append(rel(path))

    for name, places in sorted(declared.items()):
        if len(places) > 1:
            WARNINGS.append(f"type '{name}' declared in {len(places)} files: {', '.join(places)}")

    check_cross_namespace(sorted(cs_files))

    xaml_errors, _ = check_xaml.check(ROOT)
    xaml_count = 0
    for base in ('src', 'tests', 'tools'):
        for dirpath, _, filenames in os.walk(os.path.join(ROOT, base)):
            if 'obj' in dirpath or 'bin' in dirpath:
                continue
            xaml_count += sum(1 for name in filenames if name.endswith('.xaml'))
    ERRORS.extend(xaml_errors)

    structure_errors, _ = check_structure.check(ROOT)
    ERRORS.extend(structure_errors)

    ERRORS.extend(check_types.check(sorted(cs_files)))
    ERRORS.extend(check_packaging.check())

    print(f"checked {len(cs_files)} C# files and {xaml_count} XAML files")
    for warning in WARNINGS:
        print("WARN  " + warning)
    for error in ERRORS:
        print("ERROR " + error)
    print(f"{len(ERRORS)} error(s), {len(WARNINGS)} warning(s)")
    return 1 if ERRORS else 0


if __name__ == '__main__':
    sys.exit(main())
