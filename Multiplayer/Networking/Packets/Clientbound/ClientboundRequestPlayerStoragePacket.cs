namespace Multiplayer.Networking.Packets.Clientbound;

/// <summary>
/// The host asking a client what it is carrying, so it has something recent to write down the next
/// time it saves. Leaving is the moment that matters and the client reports then by itself; this is
/// for everything that ends a session without one - a crash, a pulled cable, a closed laptop.
/// </summary>
public class ClientboundRequestPlayerStoragePacket
{
}
