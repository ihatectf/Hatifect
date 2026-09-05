using System.Text.Json;
using Hatifect.UI.Semantics;
using Hatifect.UI.Tooling.Metadata;
using Hatifect.UI.Tooling.Protocol;
using Hatifect.UI.Tooling.Server;
using Xunit;

namespace Hatifect.UI.Tooling.Server.Tests;

public sealed class AuthoringServerTests
{
    [Fact]
    public async Task PublicMetadataDrivesCompleteAuthoringTranscriptThroughFramedStdio()
    {
        const string uri = "file:///Storage.hatifect";
        var binding = new UiBindingContext(new UiSymbolId("External.Author", "storage")).DeclareRole("Item");
        using var metadata = JsonDocument.Parse(UiBindingContextJson.Export(binding));
        using var input = new MemoryStream();
        using (var frames = new UiLspMessageStream(Stream.Null, input))
        {
            await Write("initialize", 1, new
            {
                capabilities = new
                {
                    workspace = new { workspaceEdit = new { documentChanges = true } },
                    textDocument = new { documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
                        codeAction = new { codeActionLiteralSupport = new { codeActionKind = new { valueSet = new[] { "quickfix" } } } } }
                },
                initializationOptions = new { bindingMetadata = metadata.RootElement.Clone(), declarations = new[]
                {
                    new { symbolId = binding.OwnerId.Child("role/Item").ToString(), uri = "file:///StorageExperience.cs",
                        range = new { start = new { line = 12, character = 4 }, end = new { line = 12, character = 8 } } }
                } }
            });
            await Write("initialized", null, new { });
            await Write("textDocument/didOpen", null, new
            {
                textDocument = new { uri, version = 1, text = "visual Storage\nItem\n    surface = Surface.Raisd\n" }
            });
            await Write("textDocument/completion", 2, At(2, 24));
            await Write("textDocument/hover", 3, At(2, 5));
            await Write("textDocument/documentSymbol", 4, Document());
            await Write("textDocument/foldingRange", 5, Document());
            await Write("textDocument/semanticTokens/full", 6, Document());
            await Write("textDocument/references", 7, new
            {
                textDocument = new { uri }, position = new { line = 1, character = 2 }, context = new { includeDeclaration = false }
            });
            await Write("textDocument/definition", 8, At(1, 2));
            await Write("textDocument/codeAction", 9, new
            {
                textDocument = new { uri },
                range = new { start = new { line = 2, character = 0 }, end = new { line = 2, character = 27 } },
                context = new { diagnostics = Array.Empty<object>() }
            });
            await Write("textDocument/diagnostic", 10, Document());
            await Write("textDocument/didChange", null, new
            {
                textDocument = new { uri, version = 2 }, contentChanges = new[]
                {
                    new { range = new { start = new { line = 2, character = 14 }, end = new { line = 2, character = 27 } }, text = "Surface.Raised" }
                }
            });
            await Write("textDocument/diagnostic", 11, Document());
            await Write("workspace/diagnostic", 12, new { previousResultIds = Array.Empty<object>() });
            await Write("shutdown", 13, new { });
            await Write("exit", null, new { });

            async ValueTask Write(string method, int? id, object parameters)
            {
                object message = id is { } requestId
                    ? new { jsonrpc = "2.0", id = requestId, method, @params = parameters }
                    : new { jsonrpc = "2.0", method, @params = parameters };
                await frames.WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(message));
            }
        }
        input.Position = 0;
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        Assert.Equal(0, await UiToolingServer.RunAsync(input, output, errors));
        Assert.Empty(errors.ToString());
        output.Position = 0;
        var results = new Dictionary<int, JsonElement>();
        using (var frames = new UiLspMessageStream(output, Stream.Null))
            while (await frames.ReadFrameAsync() is { } bytes)
            {
                using var response = JsonDocument.Parse(bytes);
                Assert.False(response.RootElement.TryGetProperty("error", out _));
                results.Add(response.RootElement.GetProperty("id").GetInt32(), response.RootElement.GetProperty("result").Clone());
            }
        Assert.Equal(13, results.Count);
        Assert.Equal(2, results[1].GetProperty("capabilities").GetProperty("textDocumentSync").GetProperty("change").GetInt32());
        Assert.Equal("Surface.Raised", Assert.Single(results[2].GetProperty("items").EnumerateArray()).GetProperty("label").GetString());
        Assert.Contains("SurfaceToken", results[3].GetProperty("contents").GetProperty("value").GetString());
        Assert.Equal("Storage", Assert.Single(results[4].EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(2, Assert.Single(results[5].EnumerateArray()).GetProperty("endLine").GetInt32());
        Assert.True(results[6].GetProperty("data").GetArrayLength() > 0);
        Assert.Equal(uri, Assert.Single(results[7].EnumerateArray()).GetProperty("uri").GetString());
        Assert.Equal("file:///StorageExperience.cs", Assert.Single(results[8].EnumerateArray()).GetProperty("uri").GetString());
        Assert.Equal(1, Assert.Single(results[9].EnumerateArray()).GetProperty("edit").GetProperty("documentChanges")[0]
            .GetProperty("textDocument").GetProperty("version").GetInt32());
        Assert.Equal("LUI2013", Assert.Single(results[10].GetProperty("items").EnumerateArray()).GetProperty("code").GetString());
        Assert.Empty(results[11].GetProperty("items").EnumerateArray());
        Assert.Equal(2, Assert.Single(results[12].GetProperty("items").EnumerateArray()).GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, results[13].ValueKind);

        object Document() => new { textDocument = new { uri } };
        object At(int line, int character) => new { textDocument = new { uri }, position = new { line, character } };
    }
}
