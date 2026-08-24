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
    /// Drilling, taping and freeing a screw point all end up here. The call is a no-op when the
    /// point is already in the requested state, so remember what it was and only report a change.
    /// </summary>
    [HarmonyPatch(nameof(Drillable.SetMountPointState))]
    [HarmonyPrefix]
    static void SetMountPointStatePrefix(Drillable __instance, int index, out MountPoint.States __state)
    {
        __state = index >= 0 && index < __instance.MountPointCount
            ? __instance.GetMountPointState(index)
            : MountPoint.States.None;
    }

    [HarmonyPatch(nameof(Drillable.SetMountPointState))]
    [HarmonyPostfix]
    static void SetMountPointStatePostfix(Drillable __instance, int index, MountPoint.States newState, MountPoint.States __state)
    {
        if (NetworkedGadgets.IsApplyingRemoteChange || __state == newState)
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
