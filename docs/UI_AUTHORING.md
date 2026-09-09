# Hatifect UI authoring

`Hatifect.UI.Tooling` provides editor analysis over the same Language parser and Semantics compiler used by the framework. `Hatifect.UI.Tooling.Server` runs it as an editor-owned LSP 3.17 stdio process. It needs no game or SMAPI process. The frozen `IUiSemanticSurfaceApi` v1 and binding metadata schema v1 remain compatible.

## Build and launch

The repository toolchain is .NET 8 SDK plus .NET 6 runtime. The launcher uses the canonical resolver, including `HATIFECT_TEST_DOTNET` and `HATIFECT_DOTNET` overrides.

```sh
# Build once; EOF immediately finishes this initial server invocation.
./tools/hatifect-ui-language-server --build </dev/null

# Configure the editor to start this command with stdin/stdout pipes.
./tools/hatifect-ui-language-server
```

Release is the default; `--configuration Debug` selects an existing Debug build. Without `--build`, a missing assembly produces an actionable stderr error. Explicit build output goes to stderr, and failed builds never launch an older assembly. The command replaces its launcher process with the server; EOF, LSP shutdown/exit, and console cancellation retain the server's existing lifecycle behavior. Ordinary builds disable deployment.

Register `.hatifect` files with your editor's LSP client and supply `initializationOptions` below. The server command and initialization options are ready for a client adapter; this repository does not install or bundle a VS Code/JetBrains extension. Use an absolute command path when the editor starts outside the checkout. Standard output contains only `Content-Length` JSON-RPC frames.

## Export actual consumer bindings

The consumer creates its Experience, then exports its semantic context. Tooling never discovers declarations by scanning arbitrary C# files.

```csharp
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;

byte[] utf8 = UiBindingContextJson.Export(experience.CreateBindingContext());
File.WriteAllBytes("binding-metadata.json", utf8);
UiBindingContext context = UiBindingContextJson.Import(utf8);
```

Reference the exact `Hatifect.UI.Tooling` package from the repository's current UI feed. Its dependency graph remains Language + Semantics. Public export preserves IDs, capabilities, strictness flags and deterministic ordering; subsequent changes to the original context do not modify exported bytes. `Import` rejects unsupported schemas, duplicate declarations and conflicting identities.

With alpha.32, explicit identity, localized labels and typed relations use binding metadata schema v2. Canonical legacy contexts still export the same schema-v1 bytes; both versions import strictly. The server advertises `[1, 2]` in `capabilities.experimental.hatifectUi.bindingMetadataVersions` during initialization. A client that only understands v1 must report unsupported v2 metadata instead of regenerating IDs from names. V1 and v2 contexts can coexist in one session.

Schema v2 contains `graph.nodes`, `graph.relations`, `graph.presentedNodes` and `graph.roles`. Nodes preserve their independent `id`, authoring `alias`, display `label`, nullable data descriptor, capabilities and typed input slots. Relations preserve endpoint IDs, the target input ID, explicit submission mapping and provenance. `presentedNodes` is the ordered subset available to presentation DSL; auxiliary data/action nodes are retained in metadata but cannot be placed as widgets. Export the actual context with the API above rather than constructing this graph from displayed labels. Failed imports and binding updates retain the previous complete document/binding snapshots and revision.

The existing Examples assembly also exercises export without friend access:

```sh
./tools/hatifect-build ui
dotnet "Hatifect UI/examples/Hatifect.UI.Examples/bin/Release/net6.0/Hatifect.UI.Examples.dll" \
  --binding-metadata > binding-metadata.json
```

Use your configured .NET executable in place of `dotnet` if it is not on PATH. Pass the parsed JSON object as `initializationOptions`, not an encoded JSON string:

```json
{
  "bindingMetadata": {
    "schemaVersion": 1,
    "ownerId": "Author.Example/catalog",
    "requireDeclaredElements": true,
    "requireDeclaredRoles": true,
    "elements": [],
    "roles": [
      { "id": "Author.Example/catalog/role/Item", "name": "Item" }
    ]
  },
  "bindingRevision": 0,
  "documentBindings": [],
  "declarations": []
}
```

`bindingMetadata` is the default for open documents. Optional `documentBindings` entries have `{ "uri": "file:///path/Asset.hatifect", "bindingMetadata": <exported object> }` and override that exact URI. Different Experience owners can share a server; names resolve through stable IDs in each context.

Optional `declarations` entries contain `symbolId`, an absolute `uri`, and an LSP `range` with zero-based UTF-16 `start`/`end`. The ID must identify an element or role in one of the supplied contexts, or a v2 graph node, input slot or relation. These coordinates come from the consumer's generator or editor adapter. Go to definition uses them exactly. Built-in catalog symbols and consumer symbols without provenance return an empty definition result; the server does not invent C# locations.

After rebuilding consumer metadata, send `hatifect/updateBindings` with the same complete options shape and an increasing `bindingRevision`. This replaces all bindings and provenance atomically, recompiles open documents, and returns `{ "bindingRevision": n, "documentsReanalyzed": count }`. Omitted overrides/declarations are removed. Text versions stay unchanged; diagnostic result IDs change. Pull diagnostics again after a successful update.

## Supported editor operations

| Operation | Behavior |
|---|---|
| Incremental synchronization | Sequential range edits and full replacements; one version committed after the batch validates. UTF-16 positions count a tab as one unit and a supplementary character as two. CRLF, CR and LF work; long columns clamp to the line end. Invalid lines, reversed ranges and split surrogate pairs are rejected. |
| Completion | Document kinds, declared elements/roles, properties, typed tokens/enums, regions, profiles and states. Presentation choices respect element capabilities. A text edit replaces the full qualified name, including its suffix. |
| Hover | Exact symbol range, property type/effects/animation support, enum choices and target metadata. Quoted and unquoted String values remain literals. |
| Document symbols/folding | Recovery syntax supplies profile, role, matrix and assignment structure while values are incomplete. Hierarchical output is negotiated; old clients and depth 24+ receive a flat outline with all nodes. |
| Semantic tokens | Full relative UTF-16 token data with a declared legend, no overlapping or multiline tokens. Results are cached per immutable document snapshot. Delta requests are not advertised. |
| References/highlights | Stable identity across open documents; highlights stay in the requested file. `includeDeclaration` controls local and explicitly supplied declarations. No disk-wide indexing is implied. |
| Definition | Exact explicit declaration locations, or the actual local DSL document declaration. |
| Quick fixes | Catalog/binding spelling corrections within two character edits. The compiler must remove the selected error without adding another diagnostic. Independent errors may remain. Minimal versioned edits are returned to the client for application. |
| Pull diagnostics | Document/workspace `full` and `unchanged` reports with opaque result IDs. Closing a document clears its previous workspace report; reopening does not reuse its old ID. Workspace reports include document versions. |
| Formatting | Existing canonical formatting only when syntax is safe to format. |

For versioned code-action edits, advertise both `capabilities.workspace.workspaceEdit.documentChanges = true` and `capabilities.textDocument.codeAction.codeActionLiteralSupport`. Otherwise quick fixes are disabled. The editor must reject edits whose document version is stale. `context.only` is honored; client diagnostics do not replace the server's current compiler evidence.

## Bounds and ownership

The default session retains at most 64 open documents and 1,048,576 UTF-16 units per document. Overflowing the open-document limit is rejected without evicting synchronized text. A change notification contains 1–128 edits. A context has at most 4,096 symbols and 64 capabilities per element; the session allows 16,384 symbols across contexts, 4,096 source declarations and 64 document overrides. For v2, symbol budgets count every node, input slot, relation and role, including auxiliary declarations. Binding names are limited to 512 characters, configured URIs to 8,192. Descriptor nesting is limited to 16 edges; graph validation and descriptor equality/hash reuse shared branches. Wire export rejects more than 65,536 expanded non-null descriptor entries, preventing a small shared descriptor graph from expanding into unbounded JSON. Import retains the existing JSON depth limit of 32.

Snapshots retain recovery syntax, frozen bindings, line offsets and symbol indexes. Queries reuse them. Qualified-name analysis is linear in token count. Quick-fix processing examines at most 32 relevant diagnostics and compiles at most 16 candidates; the diagnostic multiset is built once. Large results stop at a serialization budget derived from the actual transport payload limit and escaped request ID. A rejected result returns an error and the session can serve the next request; no partial response frame is written. The framing/dispatcher layer still fails closed on malformed or oversized transport data.

The normal payload limit is 4 MiB. Request IDs are 32-bit integers or strings whose escaped JSON encoding fits 4,096 bytes and one quarter of the transport payload limit. Oversized IDs receive `Invalid Request` with a null ID before the method runs. Error messages are bounded too. Workspace diagnostic requests accept at most twice the document capacity in `previousResultIds`; unknown closed URIs receive an empty full report. The client owns cancellation, process lifetime, metadata refresh and applying edits. A background file watcher, game deployment and runtime visual acceptance are outside these authoring slices.

Protocol shapes follow [LSP 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/). Focused evidence lives in `Editor*Tests`, `PublicBindingMetadataTests`, `AuthoringServerTests` and `tools/tests/test_ui_language_server.py`. Run `./tools/hatifect-check` for the static/build/test gate and `./tools/hatifect-isolated-ui-ca` for current-package consumer compatibility. Runtime/SMAPI and visual verification are `NOT_APPLICABLE` for this tooling change.
