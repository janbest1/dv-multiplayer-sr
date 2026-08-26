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

        //Take the UID the game just handed out and replace it with one carrying this player's slice,
        //so the number cannot collide with one minted on another machine at the same moment
        NetworkedGadgets.AssignNetworkUid(__result, NetworkedGadgets.NextLocalUid(destination));

        NetworkedGadgets.Track(__result);

        GadgetBase gadget = __result;
        string prefabName = gadgetItem.Item?.InventorySpecs?.ItemPrefabName;

        //The attach names the item by its net id, and anything a client brought into the world
        //itself has none. Ask the host for one and report the attach once it comes back.
        if (!NetworkedItem.TryGetNetId(gadgetItem.Item, out ushort itemNetId) || itemNetId == 0)
        {
            if (!NetworkedItem.TryGetNetworkedItem(gadgetItem.Item, out NetworkedItem netItem))
            {
                Multiplayer.LogWarning($"GadgetItemPatch.Place() {prefabName} placed on car {carNetId} is not a networked item at all");
                return;
            }

            Multiplayer.LogDebug(() => $"GadgetItemPatch.Place() {prefabName} has no net id yet, asking the host for one");

            NetworkedItemManager.Instance.RequestNetId(netItem, assignedId => Send(gadget, carNetId, assignedId, prefabName, localPos, localRot));

            return;
        }

        Send(gadget, carNetId, itemNetId, prefabName, localPos, localRot);
    }

    private static void Send(GadgetBase gadget, ushort carNetId, ushort itemNetId, string prefabName, Vector3 localPos, Quaternion localRot)
    {
        if (gadget == null || itemNetId == 0)
            return;

        //The id may have taken a round trip to the host and back, and in that time the player can
        //easily have taken the gadget off again. Announcing it now would attach it everywhere else
        //for good, since the detach went out before this and found nothing to remove.
        if (!gadget.IsLinked || !NetworkedGadgets.TryGetCarNetId(gadget.Custom, out ushort currentCar) || currentCar != carNetId)
        {
            Multiplayer.LogDebug(() => $"GadgetItemPatch.Send() {prefabName} is no longer on car {carNetId}, dropping the attach");
            return;
        }

        NetworkedGadgets.MarkAnnounced(gadget);

        NetworkLifecycle.Instance.Client?.SendGadgetChange(new CommonGadgetPacket
        {
            Action = GadgetAction.Attached,
            CarNetId = carNetId,
            Uid = gadget.UID,
            ItemNetId = itemNetId,
            PrefabName = prefabName,
            LocalPosition = localPos,
            LocalRotation = localRot,
            State = NetworkedGadgets.SerialiseState(gadget)
        });
    }
}
