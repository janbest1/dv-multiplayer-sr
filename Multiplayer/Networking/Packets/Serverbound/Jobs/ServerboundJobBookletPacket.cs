namespace Multiplayer.Networking.Packets.Serverbound.Jobs;

/// <summary>
/// Sent when a rejoining player turns out to still be carrying the booklet for one of their jobs.
/// The copy the host knew about left with them, so it needs to be told which item took its place.
/// </summary>
public class ServerboundJobBookletPacket
{
    public ushort JobNetId { get; set; }
    public ushort ItemNetId { get; set; }
}
