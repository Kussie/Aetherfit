using System.Net.WebSockets;

namespace AetherfitSignaling;

// Two peers rendezvousing under one pairing code. We never see the bundle itself here - only the
// WebRTC offer/answer/ICE messages the peers relay through us to find each other.
internal sealed class PairSession(string code)
{
    public string Code { get; } = code;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt = DateTimeOffset.UtcNow;

    // Guards claiming the Host/Guest slots below against two connections racing on the same code.
    public readonly object Gate = new();
    public WebSocket? Host;
    public WebSocket? Guest;

    public bool IsMatched => Host != null && Guest != null;
}
