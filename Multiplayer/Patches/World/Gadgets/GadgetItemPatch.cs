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

        //Without a net id nobody else can tell which item this is, and the attach would arrive as
        //"item 0 not found". Most items a client brings along have no id at all yet.
        if (!NetworkedItem.TryGetNetId(gadgetItem.Item, out ushort itemNetId) || itemNetId == 0)
        {
            Multiplayer.LogWarning($"GadgetItemPatch.Place() {gadgetItem.Item?.name} placed on car {carNetId}, but the item has no network id, so the attach cannot be shared");
            return;
        }

        //Take the UID the game just handed out and replace it with one carrying this player's slice,
        //so the number cannot collide with one minted on another machine at the same moment
        NetworkedGadgets.AssignNetworkUid(__result, NetworkedGadgets.NextLocalUid());

        NetworkedGadgets.Track(__result);

        NetworkLifecycle.Instance.Client?.SendGadgetChange(new CommonGadgetPacket
        {
            Action = GadgetAction.Attached,
            CarNetId = carNetId,
            Uid = __result.UID,
            ItemNetId = itemNetId,
            PrefabName = gadgetItem.Item?.name,
            LocalPosition = localPos,
            LocalRotation = localRot,
            State = NetworkedGadgets.SerialiseState(__result)
        });
    }
}
