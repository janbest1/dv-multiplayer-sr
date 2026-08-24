using DV.Customization.Gadgets;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Gadgets;
using Multiplayer.Networking.Packets.Common;

namespace Multiplayer.Patches.World.Gadgets;

[HarmonyPatch(typeof(Drillable))]
public static class DrillablePatch
{
    /// <summary>
    /// Drilling, taping and freeing a screw point all end up here.
    /// </summary>
    [HarmonyPatch(nameof(Drillable.SetMountPointState))]
    [HarmonyPostfix]
    static void SetMountPointState(Drillable __instance, int index, MountPoint.States newState)
    {
        if (NetworkedGadgets.IsApplyingRemoteChange)
            return;

        GadgetBase gadget = __instance.ThisGadget;

        if (gadget == null || !gadget.IsLinked)
            return;

        if (!NetworkedGadgets.TryGetCarNetId(gadget.Custom, out ushort carNetId))
            return;

        NetworkLifecycle.Instance.Client?.SendGadgetChange(new CommonGadgetPacket
        {
            Action = GadgetAction.MountPointState,
            CarNetId = carNetId,
            Uid = gadget.UID,
            MountPointIndex = (byte)index,
            MountPointState = (byte)newState
        });
    }
}
