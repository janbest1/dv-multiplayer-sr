using System.Collections.Generic;
using System.Linq;
using System.Text;
using DV.Utils;
using UnityEngine;
using JetBrains.Annotations;
using Multiplayer.Networking.Data;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Components.Networking.World;
using System;
using Multiplayer.Utils;
using DV;
using DV.Interaction;
using Multiplayer.Networking.Data.Items;

namespace Multiplayer.Components.Networking.World;

public class NetworkedItemManager : SingletonBehaviour<NetworkedItemManager>
{
    /*
     * Server 
     */

    //Culling distance for items, fallen back on when the setting is missing or nonsense
    public const float MAX_DISTANCE_TO_ITEM = 250f;
    private const float MIN_DISTANCE_TO_ITEM = 50f;

    /// <summary>
    /// How far from a player an item is told about, squared. A flag planted half a kilometre down
    /// the line is nobody else's business until they come near it - but a hundred metres, which is
    /// what this was, is shorter than the stretch of track anyone would mark out with them, so most
    /// of a row of flags was never mentioned to the other player at all.
    /// </summary>
    private static float MaxDistanceToItemSqr
    {
        get
        {
            float range = Multiplayer.Settings?.ItemRange ?? MAX_DISTANCE_TO_ITEM;

            if (range < MIN_DISTANCE_TO_ITEM)
                range = MAX_DISTANCE_TO_ITEM;

            return range * range;
        }
    }
    public const float NEARBY_REMOVAL_DELAY = 3f; // 3 seconds delay
    public const float REACH_DISTANCE_BUFFER = 0.5f;
    public float MAX_REACH_DISTANCE = 4f + REACH_DISTANCE_BUFFER;         //from the game, but we should try to look up the value

    //caches for item snapshots
    private List<ItemUpdateData> DestroyedItems = new(64);

    //Item ownership
    //private Dictionary<ushort, PlayerInventory> playerInventories = new Dictionary<ushort, PlayerInventory>();
    //private Dictionary<NetworkedItem, ushort> itemToPlayerMap = new Dictionary<NetworkedItem, ushort>();


    /*
     * Client
     */

    //cache for client-sided items & spawns
    private Dictionary<string, List<NetworkedItem>> CachedItems = new(1024); //Client cached items
    private Dictionary<string, InventoryItemSpec> ItemPrefabs = new(1024);   //Item prefabs

    //How much of the game's item catalogue the lookup above was built from, so a miss can tell
    //"nothing new to find" from "the catalogue has grown since"
    private int prefabLookupSourceCount = -1;

    //Items we were asked to build and could not, so the same complaint isn't made every tick
    private readonly HashSet<ushort> uncreatable = new HashSet<ushort>();
    private bool ClientInitialised = false;


    /* 
     * Common
     */
    private Queue<Tuple<ItemUpdateData, ServerPlayer>> ReceivedSnapshots = new(64);

    protected override void Awake()
    {
        base.Awake();
        if (!NetworkLifecycle.Instance.IsHost())
            return;

        //B99 temporary patch NetworkLifecycle.Instance.Server.PlayerDisconnected += PlayerDisconnected;

        try
        {
            MAX_REACH_DISTANCE = GrabberRaycasterDV.RAYCAST_MAX_DIST + REACH_DISTANCE_BUFFER;
        }
        catch (Exception ex)
        {
            NetworkLifecycle.Instance.Server.LogWarning($"NatworkedItemManager.Awake() Failed to find GrabberRaycasterDV\r\n{ex.Message}");
        }
    }

    private void PlayerDisconnected(uint netID)
    {
        throw new NotImplementedException();
    }

    protected void Start()
    {
        NetworkLifecycle.Instance.OnTick += Common_OnTick;

        BuildPrefabLookup();

        if (Multiplayer.Settings.DumpItemInfo)
            DumpItemPrefabs();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (UnloadWatcher.isQuitting)
            return;

        NetworkLifecycle.Instance.OnTick -= Common_OnTick;

        awaitingRegistration.Clear();
        requested.Clear();
    }

    public void AddDirtyItemSnapshot(NetworkedItem netItem, ItemUpdateData snapshot)
    {
        DestroyedItems.Add(snapshot);

        foreach(var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if(player.KnownItems.ContainsKey(netItem))
                player.KnownItems.Remove(netItem);

            if(player.NearbyItems.ContainsKey(netItem))
                player.NearbyItems.Remove(netItem);
        }
    }

    public void ReceiveSnapshots(List<ItemUpdateData> snapshots, ServerPlayer sender)
    {
        if (snapshots == null)
            return;

        foreach (var snapshot in snapshots)
        {
            ReceivedSnapshots.Enqueue(new (snapshot, sender));
        }

        Multiplayer.LogDebug(() => $"NetworkItemManager.ReceiveSnapshots() count: {ReceivedSnapshots.Count}, from: ");
    }

    #region Common

    private void Common_OnTick(uint tick)
    {
        ProcessReceived();

        if (NetworkLifecycle.Instance.IsHost())
        {
            UpdatePlayerItemLists();
            ProcessChanged(tick);
        }
        else
        {
            ProcessClientChanges(tick);
        }
    }

    private void ProcessReceived()
    {
        while (ReceivedSnapshots.Count > 0)
        {
            var snapshotInfo = ReceivedSnapshots.Dequeue();
            ItemUpdateData snapshot = snapshotInfo.Item1;
            try
            {
                Multiplayer.LogDebug(() => $"ProcessReceived: {snapshot.UpdateType}");

                if (snapshot == null || snapshot.UpdateType == ItemUpdateData.ItemUpdateType.None)
                {
                    Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Invalid Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
                    continue;
                }

                if (NetworkLifecycle.Instance.IsHost())
                {
                    ProcessReceivedAsHost(snapshot, snapshotInfo.Item2);
                }
                else
                {
                    ProcessReceivedAsClient(snapshot);
                }
            }
            catch (Exception ex)
            {
                Multiplayer.LogError($"NetworkedItemManager.ProcessReceived() Error! {ex.Message}\r\n{ex.StackTrace}");
            }
        }
    }

    #endregion

    #region Server

    private void UpdatePlayerItemLists()
    {
        float currentTime = Time.time;
        float maxDistanceSqr = MaxDistanceToItemSqr;

        var allItems = NetworkedItem.GetAll();

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            foreach (var item in allItems)
            {
                if (item == null)
                {
                    NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Null item found in allItems!");
                    continue;
                }

                float sqrDistance = (player.WorldPosition - item.transform.position).sqrMagnitude;

                if (sqrDistance <= maxDistanceSqr)
                {
                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Adding for player: {player?.Username}, Nearby Item: {item?.NetId}, {item?.name}");
                    player.NearbyItems[item] = currentTime;
                }
            }

            // Remove items that are no longer nearby
            for (int i = 0; i < player.NearbyItems.Count; i++)
            {
                var kvp = player.NearbyItems.ElementAt(i);

                if (currentTime - kvp.Value > NEARBY_REMOVAL_DELAY)
                {
                    NetworkLifecycle.Instance.Server.LogDebug(() => $"UpdatePlayerItemLists() Removing for player: {player?.Username}, Nearby Item: {kvp.Key?.NetId}, {kvp.Key?.name}");
                    player.NearbyItems.Remove(kvp.Key);
                }
            }
        }
    }

    private void ProcessChanged(uint tick)
    {
        List<ItemUpdateData> dirtyItems = new List<ItemUpdateData>();
        float timeStamp = Time.time;

        foreach (var item in NetworkedItem.GetAll())
        {
            //One item that cannot answer for itself must not silence every other item. A throw here
            //used to abort the whole tick, and with it all item sync for as long as it kept coming.
            try
            {
                ItemUpdateData snapshot = item.GetSnapshot();
                if (snapshot != null)
                    dirtyItems.Add(snapshot);
            }
            catch (Exception ex)
            {
                NetworkLifecycle.Instance.Server.LogError($"ProcessChanged({tick}) Item {item?.NetId} ({(item == null ? "gone" : item.name)}) failed: {ex.Message}\r\n{ex.StackTrace}");
            }
        }

        if (dirtyItems.Count > 0)
            NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) DirtyItems: {dirtyItems.Count}");

        foreach (var player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.LoadingState < PlayerLoadingState.ReadyForItems)
                continue;

            List<ItemUpdateData> playerUpdates = new List<ItemUpdateData>();

            // Process nearby items
            foreach (var nearbyItem in player.NearbyItems.Keys)
            {
                if (!player.KnownItems.ContainsKey(nearbyItem))
                {
                    player.KnownItems[nearbyItem] = tick;

                    //An item only becomes "nearby" for the player who threw it at the moment they
                    //throw it - until then it was in their hands or their bag, and not in the world
                    //at all. Introducing it back to them puts it wherever the host had it a moment
                    //ago, which for something still in the air means back at the throwing hand.
                    if (nearbyItem.LastReportedBy == player.PlayerId)
                    {
                        NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Not introducing item {nearbyItem.NetId} back to {player.Username}, it came from them");
                        continue;
                    }

                    //Job paperwork is introduced by the station that prints it, with the id that
                    //goes with the job. Sending it a second time from here builds a plain copy
                    //over the top of that one. The check asked the wrapper what type it was,
                    //which is always NetworkedItem, so it never once matched.
                    if (DoNotCreateItem(nearbyItem.TrackedItemType))
                        continue;

                    // This is a new item for the player
                    NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) New item for: {player.Username}, itemNetID{nearbyItem.NetId}");

                    ItemUpdateData snapshot = nearbyItem.CreateUpdateData(ItemUpdateData.ItemUpdateType.Create);

                    if (snapshot != null)
                        playerUpdates.Add(snapshot);
                }
                else
                {
                    // Check if this item is in the dirty items list
                    var dirtyUpdate = dirtyItems.FirstOrDefault(di => di.ItemNetId == nearbyItem.NetId);

                    //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Item exists for: {player.Username}, {dirtyUpdate != null}");

                    if (dirtyUpdate == null)
                    {
                        //NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Item exists for: {player.Username}, LastDirtyTick: {player.KnownItems[nearbyItem] < nearbyItem.LastDirtyTick}");
                        if (player.KnownItems[nearbyItem] < nearbyItem.LastDirtyTick)
                        {
                            dirtyUpdate = nearbyItem.CreateUpdateData(ItemUpdateData.ItemUpdateType.FullSync);
                        }
                    }

                    if (dirtyUpdate != null)
                    {
                        Multiplayer.LogDebug(() => $"ProcessChanged({tick}) Sending item {dirtyUpdate.ItemNetId} to {player.Username}, Update Type: {dirtyUpdate.UpdateType}, Item State: {dirtyUpdate.ItemState}, Player: {dirtyUpdate.Player}");
                        playerUpdates.Add(dirtyUpdate);
                        player.KnownItems[nearbyItem] = tick;
                    }
                }
            }

            if (DestroyedItems.Count > 0)
                NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Adding {DestroyedItems.Count()} DestroyedItems for: {player.Username}");

            playerUpdates.AddRange(DestroyedItems);

            if (playerUpdates.Count > 0)
            {
                NetworkLifecycle.Instance.Server.LogDebug(() => $"ProcessChanged({tick}) Sending {playerUpdates.Count()} to player: {player.Username}");
                NetworkLifecycle.Instance.Server.SendItemsChangePacket(playerUpdates, player);
            }
        }

        DestroyedItems.Clear();
    }

    #region Naming Items For A Client

    //What the host has promised a client but not yet seen. An id on its own is no use: the item it
    //stands for lives on the client, and until the client introduces it the host has nothing to
    //apply updates to.
    private readonly Dictionary<ushort, byte> promisedToPlayer = new Dictionary<ushort, byte>();
    private readonly Dictionary<ushort, Guid> promisedGuid = new Dictionary<ushort, Guid>();

    /// <summary>
    /// Sets an id and a lasting name aside for something a client built itself. Nothing is created
    /// here - building a copy just to read its number leaves the host with a real item nobody asked
    /// for. The item arrives with the client's own introduction.
    /// </summary>
    public ushort PromiseItemToPlayer(byte playerId, out Guid guid)
    {
        ushort netId = NetworkedItem.ReserveId();

        guid = Guid.NewGuid();

        promisedToPlayer[netId] = playerId;
        promisedGuid[netId] = guid;

        return netId;
    }

    /// <summary>
    /// Whether this player may introduce this item, and if so builds the host's copy of it.
    /// </summary>
    private bool AcceptPromisedItem(ItemUpdateData snapshot, ServerPlayer player)
    {
        if (!promisedToPlayer.TryGetValue(snapshot.ItemNetId, out byte promisedTo) || promisedTo != player.PlayerId)
            return false;

        if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem existing) && existing != null)
        {
            //Already introduced once; a second introduction would replace an item that is in use
            NetworkLifecycle.Instance.Server.LogWarning($"NetworkedItemManager.AcceptPromisedItem() {player.Username} introduced item {snapshot.ItemNetId} twice");
            return false;
        }

        promisedGuid.TryGetValue(snapshot.ItemNetId, out Guid guid);

        promisedToPlayer.Remove(snapshot.ItemNetId);
        promisedGuid.Remove(snapshot.ItemNetId);

        CreateItem(snapshot);

        //The host's own record, not the client's word for it
        if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem netItem) && netItem != null)
        {
            netItem.AssignGuid(guid);
            netItem.LastReportedBy = player.PlayerId;
        }

        NetworkLifecycle.Instance.Server.LogDebug(() => $"NetworkedItemManager.AcceptPromisedItem() {player.Username} introduced {snapshot.PrefabName} as item {snapshot.ItemNetId} ({guid})");

        return true;
    }

    /// <summary>
    /// Lets go of everything that still points at a player who has left.
    /// </summary>
    public void ForgetPlayer(byte playerId)
    {
        //Ids set aside for items this player never got round to introducing
        List<ushort> stale = promisedToPlayer.Where(pair => pair.Value == playerId).Select(pair => pair.Key).ToList();

        foreach (ushort netId in stale)
        {
            promisedToPlayer.Remove(netId);
            promisedGuid.Remove(netId);

            //Nothing is ever going to answer to it now
            NetworkedItem.ReturnReservedId(netId);
        }

        //Player ids are handed out again, so a rejoining player usually gets their old one back. An
        //item still named after them is then taken for one they just reported and never introduced
        //to them - which is why the lantern they walked out with was missing when they came back.
        foreach (var item in NetworkedItem.GetAll())
        {
            if (item == null)
                continue;

            if (item.LastReportedBy == playerId)
                item.LastReportedBy = 0;

            //Whatever they were carrying is nobody's now
            item.ReleaseHeldBy(playerId);
        }
    }

    #endregion

    private void ProcessReceivedAsHost(ItemUpdateData snapshot, ServerPlayer player)
    {
        if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Create)
        {
            //A client introducing something the host set an id aside for is the one Create the host
            //accepts. Anything else is a client trying to invent an item.
            if (!AcceptPromisedItem(snapshot, player))
                NetworkLifecycle.Instance.Server.LogError($"NetworkedItemManager.ProcessReceivedAsHost() Host received Create snapshot! ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");

            return;
        }

        if (NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem netItem))
        {
            if (ValidatePlayerAction(snapshot, player)) //Ensure the player can do this
            {
                NetworkLifecycle.Instance.Server.LogDebug(() => $"NetworkedItemManager.ProcessReceivedAsHost() ItemNetId: {snapshot.ItemNetId}, snapshot type: {snapshot.UpdateType}");

                netItem.LastReportedBy = player.PlayerId;
                netItem.ReceiveSnapshot(snapshot);
            }
            else
            {
                NetworkLifecycle.Instance.Server.LogWarning($"NetworkedItemManager.ProcessReceivedAsHost() Player action validation failed for ItemNetId: {snapshot.ItemNetId}");
            }
        }
        else if (uncreatable.Contains(snapshot.ItemNetId))
        {
            //We already said once that we cannot build this one. Its owner has no way of knowing
            //and will keep telling us about it for as long as they hold on to it.
            NetworkLifecycle.Instance.Server.LogDebug(() => $"NetworkedItemManager.ProcessReceivedAsHost() Still nothing to apply {snapshot.UpdateType} to for ItemNetId: {snapshot.ItemNetId}");
        }
        else
        {
            NetworkLifecycle.Instance.Server.LogError($"NetworkedItemManager.ProcessReceivedAsHost() NetworkedItem not found! Update Type: {snapshot.UpdateType}, ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
        }
    }

    private bool ValidatePlayerAction(ItemUpdateData snapshot, ServerPlayer player)
    {
        return true;
        // Must have valid item
        if (!NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem networkedItem))
            return false;

        Multiplayer.LogDebug(() => $"ValidatePlayerAction() ItemId: {snapshot.ItemNetId}, name: {networkedItem.name} Update Type: {snapshot.UpdateType}, Item State: {snapshot.ItemState}, Player: {player.Username}");

        switch (snapshot.ItemState)
        {
            case ItemState.InHand:
            case ItemState.InInventory:
                // Check if someone else owns it
                GetItemOwner(snapshot.ItemNetId, out ServerPlayer currentOwner);
                Multiplayer.LogDebug(() => $"ValidatePlayerAction() ItemId: {snapshot.ItemNetId}, name: {networkedItem.name} Update Type: {snapshot.UpdateType}, Item State: {snapshot.ItemState}, Player: {player?.Username}, Current Owner: {currentOwner?.Username}");

                if (currentOwner != null && currentOwner != player)
                    return false;

                // Check pickup distance
                float distance = Vector3.Distance(player.WorldPosition, networkedItem.transform.position);
                if (distance > MAX_REACH_DISTANCE)
                    return false;

                Multiplayer.LogDebug(() => $"ValidatePlayerAction() ItemId: {snapshot.ItemNetId}, name: {networkedItem.name} Update Type: {snapshot.UpdateType}, Item State: {snapshot.ItemState}, Player: {player.Username}, Distance check: {distance}");
                break;

            case ItemState.Dropped:
            case ItemState.Thrown:
            case ItemState.OnCar:
            case ItemState.Attached: //needs additional checks for distance to coupler
                // Only owner can drop/throw
                if (!player.OwnsItem(snapshot.ItemNetId))
                    return false;
                break;
        }

        return true;
    }

    private bool GetItemOwner(ushort itemNetId, out ServerPlayer owner)
    {
        owner = NetworkLifecycle.Instance.Server.ServerPlayers.FirstOrDefault(p => p.OwnsItem(itemNetId));
        return owner != null;
    }
    #endregion

    #region Client

    #region Naming Items A Client Built Itself

    /// <summary>
    /// Whether this client is done joining. Before that the world it loaded for itself is still
    /// being replaced by the host's, and nothing local can be taken at face value.
    /// </summary>
    private static bool HasJoined =>
        NetworkLifecycle.Instance.Client != null &&
        NetworkLifecycle.Instance.Client.LoadingState == PlayerLoadingState.Complete;

    private uint nextRequestId = 1;
    private readonly Dictionary<uint, NetworkedItem> awaitingRegistration = new Dictionary<uint, NetworkedItem>();
    private readonly HashSet<NetworkedItem> requested = new HashSet<NetworkedItem>();

    /// <summary>
    /// Asks the host to name an item, once. Repeating the question every tick would hand out a
    /// fresh id per tick for the same item.
    /// </summary>
    private void RequestRegistration(NetworkedItem netItem)
    {
        if (netItem == null || netItem.Item == null || requested.Contains(netItem))
            return;

        string prefabName = netItem.Item.InventorySpecs?.ItemPrefabName;

        if (string.IsNullOrEmpty(prefabName))
            return;

        uint requestId = nextRequestId++;

        requested.Add(netItem);
        awaitingRegistration[requestId] = netItem;

        Multiplayer.LogDebug(() => $"NetworkedItemManager.RequestRegistration() asking for {prefabName}, request {requestId}");

        NetworkLifecycle.Instance.Client?.SendItemRegisterRequest(requestId, prefabName);
    }

    /// <summary>
    /// The host's answer: from here on this item has an address and a lasting name.
    /// </summary>
    public void ItemRegistered(uint requestId, ushort netId, Guid guid)
    {
        if (!awaitingRegistration.TryGetValue(requestId, out NetworkedItem netItem))
        {
            Multiplayer.LogWarning($"NetworkedItemManager.ItemRegistered() no request {requestId} outstanding for item {netId}");
            return;
        }

        awaitingRegistration.Remove(requestId);

        if (netItem == null)
        {
            //The item went away while the question was in flight; the id simply goes unused
            Multiplayer.LogDebug(() => $"NetworkedItemManager.ItemRegistered() item {netId} is already gone");
            return;
        }

        requested.Remove(netItem);

        if (IsCached(netItem))
        {
            //It went into the cache while the question was in flight. A spare part must stay
            //nameless, or the host is left waiting for an introduction that never comes.
            Multiplayer.LogDebug(() => $"NetworkedItemManager.ItemRegistered() item {netId} went into the cache, dropping the name");
            return;
        }

        netItem.NetId = netId;
        netItem.AssignGuid(guid);

        Multiplayer.LogDebug(() => $"NetworkedItemManager.ItemRegistered() {netItem.name} is now item {netId} ({guid})");
    }

    #endregion

    private void ProcessClientChanges(uint tick)
    {
        List<ItemUpdateData> changedItems = new List<ItemUpdateData>();

        if(!ClientInitialised)
            return;

        foreach (var item in NetworkedItem.GetAll())
        {
            if (item == null)
                continue;

            //Spare parts, waiting to stand in for something the host names later
            if (IsCached(item))
                continue;

            if (item.NetId == 0)
            {
                //Until this client has finished joining, anything without a name is a leftover of
                //the world it loaded for itself - the host is about to send its own set of the very
                //same things. CacheWorldItems sweeps those up, but it runs the moment loading
                //finishes and the world spawns its items a fraction of a second later, so it walks
                //an almost empty list and the leftovers live on. Put them aside here instead.
                if (!HasJoined && IsOwnWorldItem(item))
                {
                    SendToCache(item);
                    continue;
                }

                //Past that point a nameless item is something this player really did bring into the
                //world - bought, taken out of the lost and found, carried in from their own save.
                //Ids belong to the host, and the host throws away every word said about item zero,
                //so ask for a name and stay quiet until it arrives.
                RequestRegistration(item);
                continue;
            }

            try
            {
                ItemUpdateData snapshot = item.GetSnapshot();
                if (snapshot != null)
                    changedItems.Add(snapshot);
            }
            catch (Exception ex)
            {
                NetworkLifecycle.Instance.Client.LogError($"ProcessClientChanges({tick}) Item {item?.NetId} ({(item == null ? "gone" : item.name)}) failed: {ex.Message}\r\n{ex.StackTrace}");
            }
        }

        if (changedItems.Count > 0)
        {
            NetworkLifecycle.Instance.Client.SendItemsChangePacket(changedItems);
        }
    }

    private void ProcessReceivedAsClient(ItemUpdateData snapshot)
    {
        NetworkedItem.TryGet(snapshot.ItemNetId, out NetworkedItem netItem);

        NetworkLifecycle.Instance.Client.LogDebug(() => $"NetworkedItemManager.ProcessReceivedAsClient() Update Type: {snapshot?.UpdateType}, ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
        if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Create)
        {
            //The host introduces back to us what we introduced to it, and the item is already here,
            //in this player's hand or in mid-flight. Building a replacement snatches it away: a
            //thrown map vanished on the first throw and only behaved on the second.
            if (netItem != null && netItem.Guid != Guid.Empty && netItem.Guid == snapshot.Guid)
            {
                netItem.ReceiveSnapshot(snapshot);
                return;
            }

            //if the item already exists we need to remove it
            if (netItem != null)
                SendToCache(netItem);

            CreateItem(snapshot);
        }
        else if (snapshot.UpdateType == ItemUpdateData.ItemUpdateType.Destroy)
        {
            SendToCache(netItem);
        }
        else if (netItem != null)
        {
            netItem.ReceiveSnapshot(snapshot);
        }
        else
        {
            NetworkLifecycle.Instance.Client.LogError($"NetworkedItemManager.ProcessReceivedAsClient() NetworkedItem not found on client! Update Type: {snapshot.UpdateType}, ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");
        }
    }
    #endregion

    #region Item Cache And Management
    private void CreateItem(ItemUpdateData snapshot)
    {
        if(snapshot == null || snapshot.ItemNetId == 0)
        {
            Multiplayer.LogError($"NetworkedItemManager.CreateItem() Invalid snapshot! ItemNetId: {snapshot?.ItemNetId}, prefabName: {snapshot?.PrefabName}");
            return;
        }

        NetworkedItem newItem = GetFromCache(snapshot.PrefabName);

        if(newItem == null)
        {
            //GameObject prefabObj = Resources.Load(snapshot.PrefabName) as GameObject;
            
            if (!TryGetPrefab(snapshot.PrefabName, out InventoryItemSpec spec))
            {
                if (uncreatable.Add(snapshot.ItemNetId))
                    Multiplayer.LogError($"NetworkedItemManager.CreateItem() Unable to load prefab for ItemNetId: {snapshot.ItemNetId}, prefabName: {snapshot.PrefabName}");

                return;
            }

            uncreatable.Remove(snapshot.ItemNetId);

            GetSpawnPose(snapshot, out Vector3 spawnPosition, out Quaternion spawnRotation);

            //create a new item
            GameObject gameObject = Instantiate(spec.gameObject, spawnPosition, spawnRotation);

            //Make sure we have a NetworkedItem
            newItem = gameObject.GetOrAddComponent<NetworkedItem>();
        }

        newItem.gameObject.SetActive(true);
        newItem.NetId = snapshot.ItemNetId;

        //This came from the other side's snapshot, so they plainly know about it already
        newItem.MarkAsKnownElsewhere();

        newItem.ReceiveSnapshot(snapshot);
    }

    /// <summary>
    /// Resolves the world pose to spawn an item at. Items resting on a car are stored relative to
    /// that car, so they have to be transformed back into world space before instantiating.
    /// </summary>
    private void GetSpawnPose(ItemUpdateData snapshot, out Vector3 position, out Quaternion rotation)
    {
        if (snapshot.ItemState == ItemState.OnCar &&
            NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar trainCar) && trainCar != null)
        {
            position = trainCar.transform.TransformPoint(snapshot.ItemPosition);
            rotation = trainCar.transform.rotation * snapshot.ItemRotation;
            return;
        }

        position = snapshot.ItemPosition + WorldMover.currentMove;
        rotation = snapshot.ItemRotation;
    }

    private void BuildPrefabLookup()
    {
        var items = Globals.G.Items.items;
        prefabLookupSourceCount = items.Count();

        foreach (var item in items)
        {
            if (!ItemPrefabs.ContainsKey(item.ItemPrefabName))
                ItemPrefabs[item.ItemPrefabName] = item;
        }

        Multiplayer.LogDebug(() => $"NetworkedItemManager.BuildPrefabLookup() {ItemPrefabs.Count} prefabs from a catalogue of {prefabLookupSourceCount}");
    }

    /// <summary>
    /// Finds the prefab an item was built from.
    ///
    /// The catalogue is read once, when the world loads. A mod that registers items of its own
    /// after that is not in it, and nobody can then build what one of its items - so a signal disc
    /// one player bought simply never appeared for anyone else. A miss is worth looking again for,
    /// but only while there is something new to find.
    /// </summary>
    private bool TryGetPrefab(string prefabName, out InventoryItemSpec spec)
    {
        if (ItemPrefabs.TryGetValue(prefabName, out spec))
            return true;

        if (Globals.G.Items.items.Count() == prefabLookupSourceCount)
            return false;

        Multiplayer.LogDebug(() => $"NetworkedItemManager.TryGetPrefab({prefabName}) Not known, the catalogue has grown - reading it again");
        BuildPrefabLookup();

        return ItemPrefabs.TryGetValue(prefabName, out spec);
    }

    /// <summary>
    /// Logs every item prefab together with the non-Unity components it carries. Syncing an item's
    /// internal state means hooking one of those components, so this is the starting point for
    /// working out what still needs support.
    /// </summary>
    private void DumpItemPrefabs()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("NetworkedItemManager.DumpItemPrefabs() prefab | components");

        foreach (var item in Globals.G.Items.items)
        {
            if (item == null)
                continue;

            var components = item.GetComponentsInChildren<Component>(true)
                                 .Where(component => component != null && component.GetType().Namespace?.StartsWith("UnityEngine") != true)
                                 .Select(component => component.GetType().Name)
                                 .Distinct()
                                 .OrderBy(componentName => componentName);

            sb.AppendLine($"{item.ItemPrefabName} | {string.Join(", ", components)}");
        }

        Multiplayer.Log(sb.ToString());
    }

    /// <summary>
    /// Whether this is scenery the client's own world spawned, as opposed to something the player
    /// is holding or carrying. Only the scenery is replaced wholesale by the host's set; what the
    /// player brought with them stays theirs and gets a name of its own.
    /// </summary>
    private static bool IsOwnWorldItem(NetworkedItem netItem)
    {
        //What the player is holding or carrying stays theirs. Everything else standing in the world
        //is the client's own copy of scenery the host is about to send its version of - shop
        //equipment included. Leaving those out left a second scanner on every shop counter.
        return netItem != null
            && netItem.Item != null
            && !netItem.Item.IsGrabbed()
            && !StorageController.Instance.StorageInventory.ContainsItem(netItem.Item);
    }

    public void CacheWorldItems()
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        // Remove all spawned world items and place them into a cache for later use
        foreach (var item in NetworkedItem.GetAll())
        {
            try
            {
                if (IsOwnWorldItem(item))
                {
                    SendToCache(item);
                }
                //else
                //{
                //    NetworkLifecycle.Instance.Client.LogDebug(() => $"CacheWorldItems() Not caching: {item.Item.InventorySpecs.previewPrefab} is in Inventory: {StorageController.Instance.StorageInventory.ContainsItem(item.Item)}");
                //}
            }
            catch (Exception ex)
            {
                NetworkLifecycle.Instance.Client.LogError($"Error Caching Spawned Item: {ex.Message}");
            }
        }

        NetworkLifecycle.Instance.Client.Log($"Cached {inCache.Count} of {NetworkedItem.GetAll().Count} world items");

        //Whatever is left is either in the player's hands or their bag. Anything else here is
        //something the sweep could not account for, and it will end up beside the host's copy.
        Multiplayer.LogDebug(() =>
        {
            List<string> kept = NetworkedItem.GetAll()
                .Where(item => item != null && !IsCached(item))
                .Select(item => item.Item?.InventorySpecs?.ItemPrefabName ?? item.name)
                .ToList();

            return $"CacheWorldItems() kept {kept.Count}: {string.Join(", ", kept)}";
        });

        ClientInitialised = true;
    }

    //Everything sitting in the cache, so it can be recognised in one step. Walking the lists per
    //item per tick would be work for nothing, and the answer is needed for every item every tick.
    private readonly HashSet<NetworkedItem> inCache = new HashSet<NetworkedItem>();

    /// <summary>
    /// Whether this is a spare part waiting to be reused rather than something in the world.
    /// </summary>
    public bool IsCached(NetworkedItem netItem)
    {
        return netItem != null && inCache.Contains(netItem);
    }

    private NetworkedItem GetFromCache(string prefabName)
    {
        if (CachedItems.TryGetValue(prefabName, out var items) && items.Count > 0)
        {

            var cachedItem = items[items.Count - 1];
            items.RemoveAt(items.Count - 1);

            inCache.Remove(cachedItem);

            return cachedItem;
        }

        return null;
    }

    private void SendToCache(NetworkedItem netItem)
    {
        //Unity keeps a destroyed object's reference alive but hollow, and C#'s ?. does not consult
        //Unity's own idea of null - so reading anything off one throws from native code with no
        //message at all. A Destroy that names an item already gone is exactly that case.
        if (netItem == null || netItem.Item == null)
            return;

        //A question about this item may still be in flight; the answer is no longer wanted
        requested.Remove(netItem);

        string prefabName = netItem?.Item?.InventorySpecs?.itemPrefabName;

        //NetworkLifecycle.Instance.Client.LogDebug(() => $"Caching Spawned Item: {prefabName ?? ""}");

        netItem.gameObject.SetActive(false);
        RespawnOnDrop respawn = netItem.Item.GetComponent<RespawnOnDrop>();

        Destroy(respawn);

        //NetworkLifecycle.Instance.Client.LogDebug(() => $"Caching Spawned Item: {prefabName ?? ""}: checkWhileDisabled {respawn.checkWhileDisabled}, ignoreDistanceFromSpawnPosition {respawn.ignoreDistanceFromSpawnPosition}, respawnOnDropThroughFloor {respawn.respawnOnDropThroughFloor}");

        //respawn.checkWhileDisabled = false;
        //respawn.ignoreDistanceFromSpawnPosition = true;
        //respawn.respawnOnDropThroughFloor = false;

        if (SingletonBehaviour<StorageController>.Instance.StorageWorld.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromWorldStorage(netItem.Item);
        }

        if (SingletonBehaviour<StorageController>.Instance.StorageInventory.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromStorageItemList(netItem.Item);
        }

        if (SingletonBehaviour<StorageController>.Instance.StorageLostAndFound.ContainsItem(netItem.Item))
        {
            SingletonBehaviour<StorageController>.Instance.RemoveItemFromStorageItemList(netItem.Item);
        }

        netItem.Item.InventorySpecs.BelongsToPlayer = false;
        netItem.NetId = 0;
        
        if (!CachedItems.ContainsKey(prefabName))
        {
            CachedItems[prefabName] = new List<NetworkedItem>();
        }
        CachedItems[prefabName].Add(netItem);

        inCache.Add(netItem);
    }

    #endregion

    public bool DoNotCreateItem(Type itemType)
    {
        if (
            itemType == typeof(JobOverview) ||
            itemType == typeof(JobBooklet) ||
            itemType == typeof(JobReport) ||
            itemType == typeof(JobExpiredReport) ||
            itemType == typeof(JobMissingLicenseReport)
           )
        {
            return true;
        }

            return false;
    }

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedItemManager)}]";
    }
}
