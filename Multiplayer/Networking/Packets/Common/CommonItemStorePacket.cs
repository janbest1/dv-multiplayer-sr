namespace Multiplayer.Networking.Packets.Common;

/// <summary>
/// An item went into, or came back out of, one player's lost and found. Everyone else only has to
/// take it out of the world: a lost and found belongs to the player who lost the item, so nobody
/// else should find it in theirs.
/// </summary>
public class CommonItemStorePacket
{
    public ushort ItemNetId { get; set; }

    /// <summary>The player whose lost and found the item belongs to.</summary>
    public byte PlayerId { get; set; }

    /// <summary>True when the item was stored, false when it was taken back out.</summary>
    public bool Stored { get; set; }
}
