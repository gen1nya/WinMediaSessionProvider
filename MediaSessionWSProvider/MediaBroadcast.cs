using System;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace MediaSessionWSProvider;

public class MediaBroadcast : WebSocketBehavior
{
    private readonly MetadataCache _cache;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
        WriteIndented = false
    };

    public MediaBroadcast(MetadataCache cache)
    {
        _cache = cache;
    }

    protected override void OnOpen()
    {
        Console.WriteLine($"total client connected {Sessions.Count}");
        Console.WriteLine($"Client connected: {ID}");

        var state = _cache.Last;
        if (state != null)
        {
            var envelope = new { type = "metadata", data = state };
            var json = JsonSerializer.Serialize(envelope, _jsonOptions);
            Send(json);
        }
    }

    protected override void OnClose(CloseEventArgs e)
    {
        Console.WriteLine($"total client connected {Sessions.Count}");
        Console.WriteLine($"Client disconnected: {ID}");
    }
}
