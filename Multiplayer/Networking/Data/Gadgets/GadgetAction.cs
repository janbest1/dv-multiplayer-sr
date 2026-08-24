namespace Multiplayer.Networking.Data.Gadgets;

/// <summary>
/// What happened to a gadget bolted onto a train car.
/// </summary>
public enum GadgetAction : byte
{
    Attached,           //placed onto a car, carries the position and the gadget's full state
    Detached,           //taken off again
    MountPointState     //a screw point was drilled, taped or freed
}
