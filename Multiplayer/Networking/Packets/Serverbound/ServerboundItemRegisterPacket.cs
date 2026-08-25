namespace Multiplayer.Networking.Packets.Serverbound;

/// <summary>
/// A client asking the host to give one of its own items a place in the world everyone shares.
/// Net ids are the host's to hand out, so anything a client brings along itself starts without one.
/// </summary>
public class ServerboundItemRegisterPacket
{
    /// <summary>Identifies this request in the reply, since the item has no shared name yet.</summary>
    public uint RequestId { get; set; }

    public string PrefabName { get; set; }
}
