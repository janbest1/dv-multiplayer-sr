using LiteNetLib.Utils;

namespace Multiplayer.Networking.Packets.Serverbound;

/// <summary>
/// A client asking the host to name something it brought into the world itself - bought in a shop,
/// loaded from its own save, taken back out of the item cache. Ids belong to the host, so without
/// this such an item stays at net id zero and every update about it is thrown away.
/// </summary>
public class ServerboundItemRegisterPacket
{
    /// <summary>The client's own number for this request, so it can match up the answer.</summary>
    public uint RequestId { get; set; }

    public string PrefabName { get; set; }
}
