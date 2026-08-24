using LiteNetLib.Utils;
using Multiplayer.Networking.Data.Gadgets;
using Multiplayer.Networking.Serialization;
using UnityEngine;

namespace Multiplayer.Networking.Packets.Common;

/// <summary>
/// A change to the gadgets bolted onto a train car. The gadget's own state travels as the JSON the
/// game itself writes into a save, so every gadget type is covered without knowing any of them.
/// </summary>
public class CommonGadgetPacket : INetSerializable
{
    public GadgetAction Action { get; set; }

    /// <summary>Car the gadget sits on.</summary>
    public ushort CarNetId { get; set; }

    /// <summary>The gadget's UID, assigned by whoever placed it and adopted by everyone else.</summary>
    public int Uid { get; set; }

    /// <summary>Item the gadget was placed from. Only meaningful for <see cref="GadgetAction.Attached"/>.</summary>
    public ushort ItemNetId { get; set; }

    public Vector3 LocalPosition { get; set; }
    public Quaternion LocalRotation { get; set; }

    /// <summary>The gadget's save data, as written by SaveDataRequested.</summary>
    public string State { get; set; }

    public byte MountPointIndex { get; set; }
    public byte MountPointState { get; set; }

    /// <summary>Whether a detached gadget should be left on the car rather than in the world.</summary>
    public bool ReparentToCar { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)Action);
        writer.Put(CarNetId);
        writer.Put(Uid);

        switch (Action)
        {
            case GadgetAction.Attached:
                writer.Put(ItemNetId);
                Vector3Serializer.Serialize(writer, LocalPosition);
                QuaternionSerializer.Serialize(writer, LocalRotation);
                writer.Put(State ?? string.Empty);
                break;

            case GadgetAction.Detached:
                writer.Put(ReparentToCar);
                break;

            case GadgetAction.MountPointState:
                writer.Put(MountPointIndex);
                writer.Put(MountPointState);
                break;
        }
    }

    public void Deserialize(NetDataReader reader)
    {
        Action = (GadgetAction)reader.GetByte();
        CarNetId = reader.GetUShort();
        Uid = reader.GetInt();

        switch (Action)
        {
            case GadgetAction.Attached:
                ItemNetId = reader.GetUShort();
                LocalPosition = Vector3Serializer.Deserialize(reader);
                LocalRotation = QuaternionSerializer.Deserialize(reader);
                State = reader.GetString();
                break;

            case GadgetAction.Detached:
                ReparentToCar = reader.GetBool();
                break;

            case GadgetAction.MountPointState:
                MountPointIndex = reader.GetByte();
                MountPointState = reader.GetByte();
                break;
        }
    }
}
