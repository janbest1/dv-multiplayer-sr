namespace Multiplayer.Networking.Packets.Clientbound;

/// <summary>
/// The net id the host handed out for a client's item.
/// </summary>
public class ClientboundItemRegisteredPacket
{
    public uint RequestId { get; set; }

    /// <summary>Zero when the host could not make the item, and the request has to be given up on.</summary>
    public ushort ItemNetId { get; set; }
}
