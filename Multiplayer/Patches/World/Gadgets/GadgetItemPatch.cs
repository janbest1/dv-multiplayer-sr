using DV.Customization;
using DV.Customization.Gadgets;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Gadgets;
using Multiplayer.Networking.Packets.Common;
using UnityEngine;

namespace Multiplayer.Patches.World.Gadgets;

/// <summary>
/// Every placement of a gadget onto a car goes through GadgetItem.Place, whichever tool or hand
/// started it, so one hook here covers them all.
/// </summary>
[HarmonyPatch(typeof(GadgetItem))]
public static class GadgetItemPatch
{
    [HarmonyPatch(nameof(GadgetItem.Place))]
    [HarmonyPostfix]
    static void Place(GadgetBase __result, Customization destination, Vector3 localPos, Quaternion localRot, GadgetItem gadgetItem)
    {
        if (NetworkedGadgets.IsApplyingRemoteChange || __result == null || gadgetItem == null)
            return;

        if (!NetworkedGadgets.TryGetCarNetId(destination, out ushort carNetId))
            return;

        if (!NetworkedItem.TryGetNetId(gadgetItem.Item, out ushort itemNetId))
        {
            Multiplayer.LogWarning($"GadgetItemPatch.Place() {gadgetItem.name} placed on car {carNetId}, but the item is not networked");
            return;
        }

        NetworkLifecycle.Instance.Client?.SendGadgetChange(new CommonGadgetPacket
        {
            Action = GadgetAction.Attached,
            CarNetId = carNetId,
            Uid = __result.UID,
            ItemNetId = itemNetId,
            LocalPosition = localPos,
            LocalRotation = localRot,
            State = NetworkedGadgets.SerialiseState(__result)
        });
    }
}
