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
    /// What the gadget was attached to, read before the removal because Unlink() clears both.
    /// </summary>
    public struct RemovalState
    {
        public ushort CarNetId;
        public int Uid;
        public GadgetBase Gadget;
    }

    /// <summary>
    /// ForceRemove funnels into Remove, so this covers being taken off by hand, shaken off in a
    /// collision and dropped when glass breaks. The car and the UID have to be read here: by the
    /// time the call returns, Unlink() has cleared Custom and reset the UID to zero.
    /// </summary>
    [HarmonyPatch(nameof(GadgetBase.Remove))]
    [HarmonyPrefix]
    static void RemovePrefix(GadgetBase __instance, out RemovalState __state)
    {
        __state = default;

        if (NetworkedGadgets.IsApplyingRemoteChange || !__instance.IsLinked)
            return;

        if (!NetworkedGadgets.TryGetCarNetId(__instance.Custom, out ushort carNetId))
            return;

        //Nobody else has been told this gadget is here, so there is nothing for them to take off.
        //Happens when it goes back on and off again while its item is still waiting for a net id.
        if (!NetworkedGadgets.WasAnnounced(__instance))
        {
            Multiplayer.LogDebug(() => $"GadgetBasePatch.Remove() uid {__instance.UID} was never announced, nothing to report");
            return;
        }

        __state.CarNetId = carNetId;
        __state.Uid = __instance.UID;
        __state.Gadget = __instance;
    }

    /// <summary>
    /// Remove refuses the job and returns null whenever the gadget is already off, is buried under
    /// another gadget, or has lost its item, and the removal tool keeps asking every frame while the
    /// player holds it. Only a call that actually handed back the item removed anything.
    /// </summary>
    [HarmonyPatch(nameof(GadgetBase.Remove))]
    [HarmonyPostfix]
    static void RemovePostfix(GadgetItem __result, bool reparentToTrainCar, RemovalState __state)
    {
        if (__result == null || __state.CarNetId == 0)
            return;

        NetworkedGadgets.ClearAnnounced(__state.Gadget);

        NetworkLifecycle.Instance.Client?.SendGadgetChange(new CommonGadgetPacket
        {
            Action = GadgetAction.Detached,
            CarNetId = __state.CarNetId,
            Uid = __state.Uid,
            ReparentToCar = reparentToTrainCar
        });
    }
}
