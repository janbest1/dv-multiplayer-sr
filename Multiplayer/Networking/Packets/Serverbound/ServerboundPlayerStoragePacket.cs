using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Networking.Packets.Serverbound;

/// <summary>
/// A client saying what it is carrying and what is waiting in its lost and found.
///
/// A client's game is never written to disk - it would put the host's world into their own career -
/// so nothing they pick up, buy or leave behind would outlive the session. Only they know the whole
/// of it: which belt slot each thing sits in, what is inside what, how much is left in the lamp. So
/// they say, and the host is the one that writes it down.
/// </summary>
public class ServerboundPlayerStoragePacket
{
    public PlayerItemSaveData[] Inventory { get; set; }
    public PlayerItemSaveData[] LostAndFound { get; set; }
}
