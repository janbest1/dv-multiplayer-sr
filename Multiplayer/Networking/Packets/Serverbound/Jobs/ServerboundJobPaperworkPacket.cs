using Multiplayer.Networking.Data.Jobs;

namespace Multiplayer.Networking.Packets.Serverbound.Jobs;

/// <summary>
/// Sent when a rejoining player turns out to still be carrying a job's paperwork. The copy the host
/// knew about left with them, so it needs to be told which item took its place.
/// </summary>
public class ServerboundJobPaperworkPacket
{
    public ushort JobNetId { get; set; }
    public ushort ItemNetId { get; set; }
    public ValidationType Kind { get; set; }
}
