using LiteNetLib.Utils;

namespace Multiplayer.Networking.Packets.Clientbound;

/// <summary>
/// The host's answer to <see cref="Multiplayer.Networking.Packets.Serverbound.ServerboundItemRegisterPacket"/>:
/// the address on the wire, and the lasting name that outlives it.
/// </summary>
public class ClientboundItemRegisteredPacket
{
    public uint RequestId { get; set; }

    public ushort ItemNetId { get; set; }

    public byte[] Guid { get; set; }
}
