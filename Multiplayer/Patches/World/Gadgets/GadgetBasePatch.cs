using DV.Customization.Gadgets;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Gadgets;
using Multiplayer.Networking.Packets.Common;

namespace Multiplayer.Patches.World.Gadgets;

[HarmonyPatch(typeof(GadgetBase))]
public static class GadgetBasePatch
{
    /// <summary>
    /// ForceRemove funnels into Remove, so this one hook covers being taken off by hand, shaken off
    /// in a collision and dropped when glass breaks. It has to run before the call, because
    /// afterwards the gadget is unlinked and no longer knows which car it was on.
    /// </summary>
    [HarmonyPatch(nameof(GadgetBase.Remove))]
    [HarmonyPrefix]
    static void Remove(GadgetBase __instance, bool reparentToTrainCar)
    {
        if (NetworkedGadgets.IsApplyingRemoteChange || !__instance.IsLinked)
            return;

        if (!NetworkedGadgets.TryGetCarNetId(__instance.Custom, out ushort carNetId))
            return;

        NetworkLifecycle.Instance.Client?.SendGadgetChange(new CommonGadgetPacket
        {
            Action = GadgetAction.Detached,
            CarNetId = carNetId,
            Uid = __instance.UID,
            ReparentToCar = reparentToTrainCar
        });
    }
}
