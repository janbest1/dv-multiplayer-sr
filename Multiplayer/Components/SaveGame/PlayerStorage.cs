using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.SaveGame;

/// <summary>
/// Reads what the player is carrying and what is waiting in their lost and found, in the same shape
/// the game itself writes into a save - and turns it back again.
/// </summary>
public static class PlayerStorage
{
    /// <summary>
    /// Writes down what one player is carrying and what is waiting in their keeping.
    ///
    /// The host has a copy of every one of their things and knows which of them are in a hand, in a
    /// bag or in a box, so it can say this for a player without asking them - which matters, because
    /// the moment worth writing it down is the moment they leave, and by then nobody can be asked
    /// anything.
    /// </summary>
    public static void CollectForPlayer(ServerPlayer player, out List<StorageItemData> inventory, out List<StorageItemData> lostAndFound)
    {
        inventory = new List<StorageItemData>();
        lostAndFound = new List<StorageItemData>();

        if (player == null)
            return;

        foreach (NetworkedItem item in NetworkedItem.GetAll())
        {
            if (item == null || item.Item == null || item.NetId == 0)
                continue;

            if (!player.OwnsItem(item.NetId))
                continue;

            string prefabName = item.Item.InventorySpecs?.ItemPrefabName;

            if (string.IsNullOrEmpty(prefabName))
                continue;

            //Anything in a hand comes back in the bag: a pair of hands is not somewhere to leave
            //something overnight.
            if (item.LastState == ItemState.InInventory || item.LastState == ItemState.InHand)
                inventory.Add(Describe(prefabName));
            else if (item.LastState == ItemState.InStorage && item.StoredFor == player.PlayerId)
                lostAndFound.Add(Describe(prefabName));
        }

        Multiplayer.LogDebug(() => $"PlayerStorage.CollectForPlayer({player.Username}) carrying: {inventory.Count}, in keeping: {lostAndFound.Count}");
    }

    /// <summary>
    /// No place and no slot: the game finds somewhere for it. What matters is that it is theirs and
    /// which box it belongs in.
    /// </summary>
    private static StorageItemData Describe(string prefabName)
    {
        return new StorageItemData(prefabName, Vector3.zero, Quaternion.identity, true);
    }

    public static PlayerItemSaveData[] ToPacket(List<StorageItemData> items)
    {
        if (items == null)
            return new PlayerItemSaveData[0];

        List<PlayerItemSaveData> result = new List<PlayerItemSaveData>(items.Count);

        foreach (StorageItemData item in items)
        {
            if (item == null || string.IsNullOrEmpty(item.itemPrefabName))
                continue;

            result.Add(new PlayerItemSaveData
            {
                ItemPrefabName = item.itemPrefabName,
                ItemPositionX = item.itemPositionX,
                ItemPositionY = item.itemPositionY,
                ItemPositionZ = item.itemPositionZ,
                ItemRotationX = item.itemRotationX,
                ItemRotationY = item.itemRotationY,
                ItemRotationZ = item.itemRotationZ,
                ItemRotationW = item.itemRotationW,
                BelongsToPlayer = item.belongsToPlayer,
                IsGrabbed = item.isGrabbed,
                CarGuid = item.carGuid,
                ContainerId = item.containerId,
                State = item.state,
                InventorySlotIndex = item.inventorySlotIndex,
                ContainerSlotIndex = item.containerSlotIndex,
                InLockedSlot = item.inLockedSlot,
                IsDropped = item.isDropped
            });
        }

        return result.ToArray();
    }

    public static List<StorageItemData> ToStorage(PlayerItemSaveData[] items)
    {
        List<StorageItemData> result = new List<StorageItemData>();

        if (items == null)
            return result;

        foreach (PlayerItemSaveData item in items)
        {
            if (string.IsNullOrEmpty(item.ItemPrefabName))
                continue;

            result.Add(new StorageItemData(
                item.ItemPrefabName,
                new Vector3(item.ItemPositionX, item.ItemPositionY, item.ItemPositionZ),
                new Quaternion(item.ItemRotationX, item.ItemRotationY, item.ItemRotationZ, item.ItemRotationW),
                item.BelongsToPlayer,
                item.IsGrabbed,
                item.CarGuid,
                item.State,
                item.InventorySlotIndex,
                item.ContainerSlotIndex,
                item.InLockedSlot,
                item.IsDropped,
                item.ContainerId));
        }

        return result;
    }
}
