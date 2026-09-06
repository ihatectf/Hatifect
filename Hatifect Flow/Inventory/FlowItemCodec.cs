using System;
using System.IO;
using System.Text;
using System.Xml;
using StardewValley;
using StardewValley.SaveSerialization;
using SObject = StardewValley.Object;

namespace Hatifect.Flow.Inventory;

internal static class FlowItemCodec
{
    internal const int MaxPayloadLength = 65536;

    internal static string Encode(Item item)
    {
        RequireSupported(item);
        using var text = new BoundedTextWriter();
        using (XmlWriter writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
            SaveSerializer.Serialize<Item>(writer, item);
        return text.ToString();
    }

    internal static Item Decode(string payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxPayloadLength)
            throw new InvalidDataException("Invalid cargo payload length.");
        using var text = new StringReader(payload);
        using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxPayloadLength });
        Item item = SaveSerializer.Deserialize<Item>(reader);
        RequireSupported(item);
        return item;
    }

    internal static void RequireSupported(Item item)
    {
        if (item is null || item.GetType() != typeof(SObject) || item.Stack <= 0
            || ((SObject)item).bigCraftable.Value || ((SObject)item).heldObject.Value is not null)
            throw new InvalidOperationException("Flowline currently supports ordinary object stacks only.");
    }

    private sealed class BoundedTextWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.Unicode;
        public override void Write(char value) { RequireRoom(1); _text.Append(value); }
        public override void Write(string? value) { if (value is not null) { RequireRoom(value.Length); _text.Append(value); } }
        public override void Write(char[] buffer, int index, int count) { RequireRoom(count); _text.Append(buffer, index, count); }
        public override void Write(ReadOnlySpan<char> buffer) { RequireRoom(buffer.Length); _text.Append(buffer); }
        public override string ToString() => _text.ToString();
        private void RequireRoom(int count)
        {
            if (count > MaxPayloadLength - _text.Length)
                throw new InvalidOperationException("Cargo metadata exceeds the supported payload size.");
        }
    }
}
