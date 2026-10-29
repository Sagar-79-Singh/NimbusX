#!/usr/bin/env python3
"""Checks that a file imports the namespace of every framework type it names.

`ImplicitUsings` is disabled in `Directory.Build.props`, so a file that uses `Task`, `Path` or `StringBuilder`
without the matching `using` line does not compile (CS0246). That mistake is invisible to every other pass.
Only well-known BCL types are listed, and a type is reported only when it appears in a clear type position.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_types

SYMBOLS = {
    'System': ['Math', 'Console', 'Convert', 'Environment', 'Random', 'Guid', 'DateTime', 'DateTimeOffset',
               'TimeSpan', 'StringComparer', 'StringComparison', 'ArgumentException', 'ArgumentNullException',
               'ArgumentOutOfRangeException', 'InvalidOperationException', 'InvalidCastException',
               'NotSupportedException', 'NotImplementedException', 'ObjectDisposedException',
               'OperationCanceledException', 'PlatformNotSupportedException', 'TimeoutException', 'EventHandler',
               'EventArgs', 'IDisposable', 'IAsyncDisposable', 'IEquatable', 'IComparable', 'Func', 'Action',
               'Version', 'AppContext', 'OperatingSystem', 'Uri', 'Lazy', 'Progress'],
    'System.Threading.Tasks': ['Task', 'ValueTask', 'TaskCompletionSource'],
    'System.Threading': ['CancellationToken', 'CancellationTokenSource', 'SemaphoreSlim', 'Interlocked', 'Monitor',
                         'Mutex', 'Thread', 'ThreadPool', 'WaitHandle', 'ManualResetEventSlim', 'AutoResetEvent'],
    'System.Text': ['StringBuilder', 'Encoding', 'UTF8Encoding'],
    'System.Text.RegularExpressions': ['Regex', 'Match', 'MatchCollection'],
    'System.IO': ['Path', 'File', 'Directory', 'FileStream', 'FileInfo', 'DirectoryInfo', 'Stream', 'MemoryStream',
                  'StreamReader', 'StreamWriter', 'FileMode', 'FileAccess', 'FileShare', 'FileOptions', 'SeekOrigin',
                  'DriveInfo', 'IOException', 'FileNotFoundException', 'DirectoryNotFoundException',
                  'PathTooLongException', 'FileSystemWatcher', 'TextWriter', 'TextReader', 'StringWriter',
                  'StringReader', 'BinaryReader', 'BinaryWriter'],
    'System.Globalization': ['CultureInfo', 'NumberStyles', 'DateTimeStyles'],
    'System.Diagnostics': ['Process', 'ProcessStartInfo', 'Stopwatch', 'Debug'],
    'System.Collections.Generic': ['List', 'Dictionary', 'IReadOnlyList', 'IReadOnlyDictionary', 'HashSet', 'Queue',
                                   'Stack', 'KeyValuePair', 'SortedDictionary', 'IReadOnlyCollection', 'IList',
                                   'ICollection', 'ISet', 'IEnumerable', 'IDictionary'],
    'System.Collections.ObjectModel': ['ObservableCollection', 'ReadOnlyCollection'],
    'System.Security.Cryptography': ['SHA256', 'RandomNumberGenerator', 'Aes', 'MD5', 'HMACSHA256'],
    'System.Runtime.InteropServices': ['DllImport', 'Marshal', 'LibraryImport', 'StructLayout', 'LayoutKind',
                                       'RuntimeInformation', 'SafeHandle'],
    'System.Net.Http': ['HttpClient', 'HttpRequestMessage', 'HttpResponseMessage', 'StringContent', 'HttpContent'],
    'System.Net.Sockets': ['TcpListener', 'TcpClient', 'NetworkStream', 'Socket'],
    'System.Net': ['IPAddress', 'IPEndPoint', 'HttpStatusCode', 'WebUtility', 'Dns'],
    'System.Text.Json': ['JsonSerializer', 'JsonDocument', 'JsonElement', 'JsonSerializerOptions',
                         'JsonNamingPolicy'],
    'System.Text.Json.Nodes': ['JsonObject', 'JsonArray', 'JsonNode', 'JsonValue'],
    'System.Buffers.Binary': ['BinaryPrimitives'],
    'System.Buffers': ['ArrayPool', 'MemoryPool'],
    'System.Windows.Threading': ['Dispatcher', 'DispatcherTimer', 'DispatcherPriority'],
    'System.Xml.Linq': ['XDocument', 'XElement'],
    'System.ComponentModel': ['INotifyPropertyChanged', 'PropertyChangedEventArgs', 'Win32Exception'],
    'System.Runtime.CompilerServices': ['CallerMemberName', 'InternalsVisibleTo', 'ModuleInitializer',
                                        'RuntimeHelpers'],
    'System.Diagnostics.CodeAnalysis': ['NotNullWhen', 'MemberNotNull', 'SuppressMessage'],
    'System.Collections.Concurrent': ['ConcurrentDictionary', 'ConcurrentBag', 'ConcurrentQueue'],
    'Microsoft.Win32': ['Registry', 'RegistryKey'],
}

# Names that exist in more than one of the namespaces above; only reported when none of them is imported.
ALTERNATIVES = {
    'IEnumerable': {'System.Collections.Generic', 'System.Collections'},
    'IDictionary': {'System.Collections.Generic', 'System.Collections'},
    'IList': {'System.Collections.Generic', 'System.Collections'},
    'ICollection': {'System.Collections.Generic', 'System.Collections'},
    'Progress': {'System'},
}

TYPE_POSITIONS = (
    r'(?<![\w.]){sym}(?:\s*<[^<>]*>)?\s*\.',        # static member access
    r'\bnew\s+{sym}\b',                              # construction
    r'(?<![\w.]){sym}\s*<',                          # generic type argument
    r'(?<![\w.]){sym}\s+[a-z_]\w*\s*(?=[=;,)])',      # declaration
    r'(?<![\w.]){sym}\s*\[',                         # array
    r'(?:\)|,)\s*{sym}\s+[a-z_]\w*',                 # parameter list
    r':\s*{sym}\b',                                  # base type
    r'\bcatch\s*\(\s*{sym}\b',
    r'\b(?:typeof|is|as|default)\s*\(?\s*{sym}\b',
)


COMPILED = {}


def _patterns(symbol):
    compiled = COMPILED.get(symbol)
    if compiled is None:
        compiled = [re.compile(pattern.replace('{sym}', re.escape(symbol))) for pattern in TYPE_POSITIONS]
        COMPILED[symbol] = compiled
    return compiled


def check(paths):
    errors = []

    sources = []
    declared = set()
    for path in paths:
        text = check_types._strip_comments(open(path, encoding='utf-8').read())
        declarations = check_types.parse_types(path, open(path, encoding='utf-8').read())
        declared.update(declaration.name for declaration in declarations)
        sources.append((path, text, declarations))

    for path, text, declarations in sources:
        imported = set(re.findall(r'(?m)^\s*using\s+([A-Za-z_][\w.]*)\s*;', text))
        alias_names = set(re.findall(r'(?m)^\s*using\s+([A-Za-z_]\w*)\s*=', text))

        # A member or local with the same name would make the report a guess, so those symbols are left alone.
        shadowed = set(declared) | alias_names
        for declaration in declarations:
            shadowed |= check_types.member_names(declaration.body)

        rel = os.path.relpath(path, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
        for namespace, symbols in SYMBOLS.items():
            if namespace in imported:
                continue
            for symbol in symbols:
                if symbol in shadowed or symbol not in text:
                    continue
                if not ALTERNATIVES.get(symbol, {namespace}) - imported:
                    continue
                for pattern in _patterns(symbol):
                    match = pattern.search(text)
                    if not match:
                        continue
                    line = text.count('\n', 0, match.start()) + 1
                    errors.append(f"{rel}:{line}: uses {symbol} but does not import {namespace}")
                    break
    return errors


if __name__ == '__main__':
    base = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    files = []
    for folder in ('src', 'tests', 'tools'):
        for dirpath, dirnames, filenames in os.walk(os.path.join(base, folder)):
            dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
            files.extend(os.path.join(dirpath, name) for name in filenames if name.endswith('.cs'))
    problems = check(sorted(files))
    for problem in problems:
        print('ERROR ' + problem)
    print(f'{len(problems)} using error(s)')
