using System;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace MediaSessionWSProvider;

public class MediaBroadcast : WebSocketBehavior
{
    protected override void OnOpen()
    {
        Console.WriteLine($"total client connected {Sessions.Count}");
        Console.WriteLine($"Client connected: {ID}");
    }

    protected override void OnClose(CloseEventArgs e)
    {
        Console.WriteLine($"total client connected {Sessions.Count}");
        Console.WriteLine($"Client disconnected: {ID}");
    }
}
