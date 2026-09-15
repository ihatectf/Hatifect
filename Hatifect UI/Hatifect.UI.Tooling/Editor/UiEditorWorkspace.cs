using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Hatifect.UI.Language.Diagnostics;
using Hatifect.UI.Language.Syntax;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Formatting;
using Hatifect.UI.Tooling.Inspection;
using Hatifect.UI.Tooling.Metadata;

namespace Hatifect.UI.Tooling.Editor;

/// <summary>
/// Immutable result for one editor document version. Syntax recovery diagnostics remain available
/// for invalid input, while typed source reveal is exposed only when semantic IR is valid.
/// </summary>
public sealed class UiEditorDocumentSnapshot
{
    internal UiEditorDocumentSnapshot(
        string sourceName,
        long version,
        string source,
        UiDefinitionKind? expectedKind,
        UiBoundDefinition? definition,
        UiDiagnostic[] diagnostics,
        UiFormatResult format,
        UiSourceInspectionIndex? inspection,
        UiDocumentSyntax syntax,
        UiBindingContextMetadata bindings,
        UiLanguageMetadata language,
        long revision)
    {
        SourceName = sourceName;
        Version = version;
        Source = source;
        Text = new UiEditorText(source);
        ExpectedKind = expectedKind;
        Definition = definition;
        Diagnostics = Array.AsReadOnly((UiDiagnostic[])diagnostics.Clone());
        Format = format;
        Inspection = inspection;
        Analysis = new UiEditorAnalysis(Text, syntax, bindings, language);
        ResultId = revision.ToString(CultureInfo.InvariantCulture);
    }

    public string SourceName { get; }
    public long Version { get; }
    public string Source { get; }
    internal UiEditorText Text { get; }
    internal UiEditorAnalysis Analysis { get; }
    internal string ResultId { get; }
    public UiDefinitionKind? ExpectedKind { get; }
    public UiBoundDefinition? Definition { get; }
    public IReadOnlyList<UiDiagnostic> Diagnostics { get; }
    public UiFormatResult Format { get; }
    public UiSourceInspectionIndex? Inspection { get; }
    public bool IsValid => Definition != null &&
        Diagnostics.All(diagnostic => diagnostic.Severity != UiDiagnosticSeverity.Error);
}

/// <summary>
/// Transport-neutral editor/LSP document owner over the shared compiler, formatter, and typed source
/// index. Retained source/IR state is versioned, failure-atomic, count-bounded, and LRU-evicted. A
/// host advances the version when either source text or its semantic binding context changes.
/// </summary>
public sealed class UiEditorWorkspace
{
    public const int DefaultCapacity = 64;
    public const int DefaultMaximumSourceLength = 1024 * 1024;

    private readonly object _sync = new();
    private readonly UiCompiler _compiler;
    private readonly UiLanguageMetadata _language;
    private long _revision;
    private readonly UiSourceFormatter _formatter = new();
    private readonly Dictionary<string, LinkedListNode<UiEditorDocumentSnapshot>> _documents =
        new(StringComparer.Ordinal);
    private readonly LinkedList<UiEditorDocumentSnapshot> _recency = new();

    public UiEditorWorkspace(
        UiSemanticCatalog? catalog = null,
        int capacity = DefaultCapacity,
        int maximumSourceLength = DefaultMaximumSourceLength)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumSourceLength <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSourceLength));
        catalog ??= UiSemanticCatalog.CreateFoundation();
        _compiler = new UiCompiler(catalog);
        _language = UiLanguageMetadataExporter.Export(catalog);
        Capacity = capacity;
        MaximumSourceLength = maximumSourceLength;
    }

    public int Capacity { get; }
    public int MaximumSourceLength { get; }

    public int Count
    {
        get
        {
            lock (_sync) return _documents.Count;
        }
    }

    public UiEditorDocumentSnapshot Update(
        string sourceName,
        long version,
        string source,
        UiBindingContext bindingContext,
        UiDefinitionKind? expectedKind = null)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new ArgumentException("A source name is required.", nameof(sourceName));
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bindingContext);
        if (source.Length > MaximumSourceLength)
            throw new ArgumentException(
                $"Editor source exceeds the {MaximumSourceLength} character workspace limit.",
                nameof(source));

        lock (_sync)
        {
            if (TryResolveExisting(sourceName, version, source, expectedKind, out UiEditorDocumentSnapshot existing))
                return existing;
        }

        UiEditorDocumentSnapshot candidate = CreateSnapshot(
            sourceName,
            version,
            source,
            bindingContext,
            expectedKind);

        lock (_sync)
        {
            if (TryResolveExisting(sourceName, version, source, expectedKind, out UiEditorDocumentSnapshot existing))
                return existing;

            return InsertEvictingOldest(sourceName, candidate);
        }
    }

    // Callers must already hold _sync: this only mutates the recency list and document map.
    private UiEditorDocumentSnapshot InsertEvictingOldest(string sourceName, UiEditorDocumentSnapshot candidate)
    {
        if (_documents.TryGetValue(sourceName, out LinkedListNode<UiEditorDocumentSnapshot>? previous))
        {
            _documents.Remove(sourceName);
            _recency.Remove(previous);
        }
        else if (_documents.Count == Capacity)
        {
            LinkedListNode<UiEditorDocumentSnapshot> oldest = _recency.First
                ?? throw new InvalidOperationException("A full editor workspace has no eviction candidate.");
            _recency.RemoveFirst();
            _documents.Remove(oldest.Value.SourceName);
        }

        var node = new LinkedListNode<UiEditorDocumentSnapshot>(candidate);
        _recency.AddLast(node);
        _documents.Add(sourceName, node);
        return candidate;
    }

    public bool TryGet(string sourceName, out UiEditorDocumentSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new ArgumentException("A source name is required.", nameof(sourceName));
        lock (_sync)
        {
            if (!_documents.TryGetValue(sourceName, out LinkedListNode<UiEditorDocumentSnapshot>? node))
            {
                snapshot = null;
                return false;
            }
            Touch(node);
            snapshot = node.Value;
            return true;
        }
    }

    public bool Remove(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new ArgumentException("A source name is required.", nameof(sourceName));
        lock (_sync)
        {
            if (!_documents.Remove(sourceName, out LinkedListNode<UiEditorDocumentSnapshot>? node)) return false;
            _recency.Remove(node);
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _documents.Clear();
            _recency.Clear();
        }
    }

    internal IReadOnlyList<UiEditorDocumentSnapshot> Snapshots()
    {
        lock (_sync) return _recency.OrderBy(snapshot => snapshot.SourceName, StringComparer.Ordinal).ToArray();
    }

    internal int Rebind(Func<string, UiBindingContext> resolveContext)
    {
        ArgumentNullException.ThrowIfNull(resolveContext);
        IReadOnlyList<UiEditorDocumentSnapshot> originals = Snapshots();
        UiEditorDocumentSnapshot[] candidates = originals.Select(original => CreateSnapshot(original.SourceName,
            original.Version, original.Source, resolveContext(original.SourceName), original.ExpectedKind)).ToArray();
        lock (_sync)
        {
            if (_documents.Count != originals.Count || originals.Any(original =>
                !_documents.TryGetValue(original.SourceName, out var node) || !ReferenceEquals(node.Value, original)))
                throw new InvalidOperationException("Documents changed while rebinding the workspace.");
            foreach (UiEditorDocumentSnapshot candidate in candidates)
                _documents[candidate.SourceName].Value = candidate;
        }
        return candidates.Length;
    }

    private UiEditorDocumentSnapshot CreateSnapshot(
        string sourceName,
        long version,
        string source,
        UiBindingContext bindingContext,
        UiDefinitionKind? expectedKind)
    {
        UiBindingContextMetadata bindings = UiBindingContextMetadataExporter.Export(bindingContext);
        UiCompilationResult compilation = _compiler.Compile(source,
            UiBindingContextMetadataWire.CreateBindingContext(bindings), sourceName);
        UiBoundDefinition? definition = compilation.Definition;
        UiDiagnostic[] diagnostics = compilation.Diagnostics.ToArray();
        if (definition != null && expectedKind is { } expected && definition.Kind != expected)
        {
            diagnostics = diagnostics.Append(new UiDiagnostic(
                    "LUI3001",
                    UiDiagnosticSeverity.Error,
                    $"Expected a {expected} definition but source declares {definition.Kind}.",
                    compilation.Syntax.KindToken.Span,
                    sourceName))
                .ToArray();
            definition = null;
        }

        if (diagnostics.Any(diagnostic => diagnostic.Severity == UiDiagnosticSeverity.Error))
            definition = null;

        UiFormatResult format = _formatter.Format(source, sourceName);
        UiSourceInspectionIndex? inspection = definition != null
            ? new UiSourceInspectionIndex(new UiBoundDefinition[] { definition })
            : null;
        return new UiEditorDocumentSnapshot(
            sourceName,
            version,
            source,
            expectedKind,
            definition,
            diagnostics,
            format,
            inspection,
            compilation.Syntax,
            bindings,
            _language,
            Interlocked.Increment(ref _revision));
    }

    private bool TryResolveExisting(
        string sourceName,
        long version,
        string source,
        UiDefinitionKind? expectedKind,
        out UiEditorDocumentSnapshot snapshot)
    {
        if (!_documents.TryGetValue(sourceName, out LinkedListNode<UiEditorDocumentSnapshot>? node))
        {
            snapshot = null!;
            return false;
        }

        UiEditorDocumentSnapshot current = node.Value;
        if (version < current.Version)
            throw new InvalidOperationException(
                $"Editor document '{sourceName}' rejected stale version {version}; current version is {current.Version}.");
        if (version > current.Version)
        {
            snapshot = null!;
            return false;
        }
        if (!string.Equals(source, current.Source, StringComparison.Ordinal) || expectedKind != current.ExpectedKind)
            throw new InvalidOperationException(
                $"Editor document '{sourceName}' version {version} was reused with different content or kind.");

        Touch(node);
        snapshot = current;
        return true;
    }

    private void Touch(LinkedListNode<UiEditorDocumentSnapshot> node)
    {
        _recency.Remove(node);
        _recency.AddLast(node);
    }
}
