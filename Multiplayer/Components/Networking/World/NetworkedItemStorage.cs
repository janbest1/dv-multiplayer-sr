using DV.CabControls;
using DV.Utils;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Packets.Common;
using System;
using System.Linq;

namespace Multiplayer.Components.Networking.World;

/// <summary>
/// Keeps a lost and found to the player who actually lost something. Every machine runs its own
/// StorageController, and each one stowed its own copy of every item that went out of reach, so all
/// of them filled up with everyone else's belongings. Only the host decides that an item is lost,
/// and only its owner ends up holding it.
/// </summary>
public static class NetworkedItemStorage
{
    /// <summary>
    /// Set while a stow received from the network is being carried out, so the patch that watches
    /// the game's own call lets it through instead of taking it for a local decision.
    /// </summary>
    public static bool IsApplyingRemoteChange { get; private set; }

    /// <summary>
    /// Decides what happens to an item the game wants to move into the lost and found.
    /// </summary>
    /// <returns>True to let the game store it here, false to leave it out of this machine's lost and found.</returns>
    public static bool ShouldStoreLocally(ItemBase item)
    {
        if (IsApplyingRemoteChange)
            return true;

        if (NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsClientRunning)
            return true;

        //Not a world item we track, so nobody else has an opinion about it
        if (!NetworkedItem.TryGetNetId(item, out ushort netId) || netId == 0)
            return true;

        //Only the host decides that something is lost. A client reaching this on its own is looking
        //at its copy of an item that belongs to someone else's world.
        if (!NetworkLifecycle.Instance.IsHost())
            return false;

        byte owner = FindOwner(netId);

        NetworkLifecycle.Instance.Server?.LogDebug(() => $"NetworkedItemStorage.ShouldStoreLocally() item {netId} ({item?.name}) lost by player {owner}");

        NetworkLifecycle.Instance.Server?.SendItemStored(netId, owner, true);

        //The host keeps it only when the host is the one who lost it
        return owner != 0 && owner == NetworkLifecycle.Instance.Client?.PlayerId;
    }

    /// <summary>
    /// Reports that a player took something back out of their lost and found.
    /// </summary>
    public static void ItemRetrieved(ItemBase item)
    {
        if (IsApplyingRemoteChange || NetworkLifecycle.Instance == null || !NetworkLifecycle.Instance.IsClientRunning)
            return;

        if (!NetworkedItem.TryGetNetId(item, out ushort netId) || netId == 0)
            return;

        byte playerId = NetworkLifecycle.Instance.Client?.PlayerId ?? 0;

        NetworkLifecycle.Instance.Client?.SendItemRetrieved(netId, playerId);
    }

    private static byte FindOwner(ushort netId)
    {
        ServerPlayer owner = NetworkLifecycle.Instance.Server?.ServerPlayers?.FirstOrDefault(p => p.OwnsItem(netId));

        return owner?.PlayerId ?? 0;
    }

    public static void Apply(CommonItemStorePacket packet)
    {
        if (packet == null)
            return;

        if (!NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem netItem) || netItem.Item == null)
        {
            Multiplayer.LogWarning($"NetworkedItemStorage.Apply() item {packet.ItemNetId} not found");
            return;
        }

        bool isMine = packet.PlayerId != 0 && packet.PlayerId == (NetworkLifecycle.Instance.Client?.PlayerId ?? 0);

        IsApplyingRemoteChange = true;

        try
        {
            if (packet.Stored)
            {
                if (isMine)
                    SingletonBehaviour<StorageController>.Instance.AddItemToLostAndFound(netItem.Item);
                else
                    netItem.Item.gameObject.SetActive(false);

                Multiplayer.LogDebug(() => $"NetworkedItemStorage.Apply() item {packet.ItemNetId} stored for player {packet.PlayerId}, mine: {isMine}");
            }
            else
            {
                if (isMine && SingletonBehaviour<StorageController>.Instance.StorageLostAndFound.ContainsItem(netItem.Item))
                    SingletonBehaviour<StorageController>.Instance.RemoveItemFromLostAndFound(netItem.Item);

                netItem.Item.gameObject.SetActive(true);

                Multiplayer.LogDebug(() => $"NetworkedItemStorage.Apply() item {packet.ItemNetId} retrieved by player {packet.PlayerId}, mine: {isMine}");
            }
        }
        catch (Exception e)
        {
            Multiplayer.LogError($"NetworkedItemStorage.Apply() item {packet.ItemNetId}: {e.Message}\r\n{e.StackTrace}");
        }
        finally
        {
            IsApplyingRemoteChange = false;
        }
    }

    /// <summary>
    /// True while the item is put away in somebody's lost and found, where it is not a world item
    /// and must not be synced as one.
    /// </summary>
    public static bool IsStowed(ItemBase item)
    {
        if (item == null)
            return false;

        StorageController storage = SingletonBehaviour<StorageController>.Instance;

        return storage != null && storage.StorageLostAndFound != null && storage.StorageLostAndFound.ContainsItem(item);
    }
}
