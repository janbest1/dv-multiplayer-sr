using DV.CabControls;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

public enum ItemState : byte
{
    Dropped,        //belongs to the world
    Thrown,         //was thrown by player
    InHand,         //held by player
    InInventory,    //in player's inventory
    Attached,       //attached to another object (e.g. EOT Lanterns)
    OnCar           //resting on/in a train car (e.g. an item left in a loco cab)
}

public class NetworkedItem : IdMonoBehaviour<ushort, NetworkedItem>
{
    #region Lookup Cache
    private static readonly Dictionary<ItemBase, NetworkedItem> itemBaseToNetworkedItem = new(4096);

    public static Dictionary<ItemBase, NetworkedItem>.ValueCollection GetAll() => itemBaseToNetworkedItem.Values;
    
    public static bool Get(ushort netId, out NetworkedItem obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedItem> rawObj);
        obj = (NetworkedItem)rawObj;
        return b;
    }

    public static bool TryGet(ushort netId, out NetworkedItem obj)
    {
        bool b = TryGet(netId, out IdMonoBehaviour<ushort, NetworkedItem> rawObj);
        obj = (NetworkedItem)rawObj;
        return b;
    }

    public static bool GetItem(ushort netId, out ItemBase obj)
    {
        bool b = Get(netId, out NetworkedItem networkedItem);
        obj = b ? networkedItem.Item : null;
        return b;
    }

    public static bool TryGetNetworkedItem(ItemBase item, out NetworkedItem networkedItem)
    {
        return itemBaseToNetworkedItem.TryGetValue(item, out networkedItem);
    }

    public static bool TryGetNetId(ItemBase item, out ushort netID)
    {
        if (itemBaseToNetworkedItem.TryGetValue(item, out var networkedItem))
        {
            netID = networkedItem.NetId;
            return true;
        }

        netID = 0;
        return false;
    }
    #endregion

    private const float PositionThreshold = 0.1f;
    private const float RotationThreshold = 0.1f;

    public ItemBase Item { get; private set; }

    /// <summary>
    /// The item's lasting name. NetId is an address on the wire and starts over with every server,
    /// so anything that has to still mean the same item after a restart - a player's inventory, a
    /// lost and found - is kept under this instead. Handed out by the host, never by a client.
    /// </summary>
    public Guid Guid { get; private set; }
    private GrabHandlerItem grabHandler;
    private SnappableItem snappableItem;
    private Component trackedItem;
    private List<object> trackedValues = new List<object>();
    public bool UsefulItem { get; private set; } = false;
    public Type TrackedItemType { get; private set; }
    public uint LastDirtyTick { get; private set; }
    private bool initialised;
    private bool registrationComplete = false;
    private Queue<ItemUpdateData> pendingSnapshots = new Queue<ItemUpdateData>();

    //Track dirty states
    private bool createdDirty = true;   //if set, we created this item dirty and have not sent an update
    private ItemState lastState;
    private bool stateDirty;
    private bool wasThrown;

    private Vector3 thrownPosition;
    private Quaternion thrownRotation;
    private Vector3 throwDirection;

    //Track the car an item belongs to, so it can be synced relative to that car
    private const float SETTLED_VELOCITY = 0.05f;
    private const float CAR_WAIT_TIMEOUT = 30f;
    private const float CAR_WAIT_INTERVAL = 0.5f;
    private TrainCar parentCar;
    private Transform lastKnownParent;
    private bool parentLookupDone;
    private ushort lastCarNetId;
    private Coroutine deferredOnCar;

    //Set while we are only mirroring this item in another player's hand
    private NetworkedPlayer remoteHolder;

    //The player we last heard is carrying this, 0 if that is us or nobody
    private byte heldByRemote;

    //What that player is doing with it, so we don't read it back off our own copy
    private ItemState? mirroredState;

    //Handle ownership
    public sbyte OwnerId { get; private set; } = -1; // 0 means no owner

    /// <summary>
    /// Host only: the player whose word this item's state last came from. They already know what
    /// they told us, so anything the host would introduce back to them is at best redundant and at
    /// worst a correction of the very thing they are doing.
    /// </summary>
    public byte LastReportedBy { get; set; }

    //public void SetOwner(ushort playerId)
    //{
    //    if (OwnerId != playerId)
    //    {
    //        if (OwnerId != 0)
    //        {
    //            NetworkedItemManager.Instance.RemoveItemFromPlayerInventory(this);
    //        }
    //        OwnerId = playerId;
    //        if (playerId != 0)
    //        {
    //            NetworkedItemManager.Instance.AddItemToPlayerInventory(playerId, this);
    //        }
    //    }
    //}

    protected override bool IsIdServerAuthoritative => true;

    protected override void Awake()
    {
        base.Awake();
        //Multiplayer.LogDebug(() => $"NetworkedItem.Awake() {name}");
        NetworkedItemManager.Instance.CheckInstance(); //Ensure the NetworkedItemManager is initialised

        Register();
    }

    protected void Start()
    {
        if (!initialised)
            Register();

        // Mark registration as complete for items that don't need tracked values
        if (!registrationComplete && !UsefulItem)
            registrationComplete = true;
    }

    public T GetTrackedItem<T>() where T : Component
    {
        return UsefulItem ? trackedItem as T : null;
    }

    public void Initialize<T>(T item, ushort netId = 0, bool createDirty = true) where T : Component
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.Initialize<{typeof(T)}>(netId: {netId}, name: {name}, createDirty: {createdDirty})");

        if (netId != 0)
            NetId = netId;

        trackedItem = item;
        TrackedItemType = typeof(T);
        UsefulItem = true;

        createdDirty = createDirty;

        if (Item == null)
            Register();

    }

    private bool Register()
    {
        if (initialised)
            return false;

        try
        {

            if (!TryGetComponent(out ItemBase itemBase))
            {
                Multiplayer.LogError($"NetworkedItem.Register() Unable to find ItemBase for {name}");
                return false;
            }

            Item = itemBase;
            itemBaseToNetworkedItem[Item] = this;

            Item.Grabbed += OnGrabbed;
            Item.Ungrabbed += OnUngrabbed;

            //Find special interaction components
            TryGetComponent<GrabHandlerItem>(out grabHandler);
            TryGetComponent<SnappableItem>(out snappableItem);

            lastState = GetItemState();
            stateDirty = false;

            //Only the host names items. A client's copy is told the name along with the item.
            if (Guid == Guid.Empty && NetworkLifecycle.Instance.IsHost())
                Guid = Guid.NewGuid();

            initialised = true;
            return true;
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"NetworkedItem.Register() Unable to find ItemBase for {name}\r\n{ex.Message}");
            return false;
        }
    }

    private void OnUngrabbed(ControlImplBase obj)
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.OnUngrabbed() NetID: {NetId}, {name}");
        stateDirty = true;
    }

    private void OnGrabbed(ControlImplBase obj)
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.OnGrabbed() NetID: {NetId}, {name}");

        //An item riding on a car has its physics switched off, grabbing it puts them back
        SetRidingPhysics(false);

        //It is in our hands now, whoever we thought was carrying it no longer is
        heldByRemote = 0;
        mirroredState = null;

        stateDirty = true;
    }

    public void OnThrow(Vector3 direction)
    {
        //block a received throw from 
        if (wasThrown)
        {
            wasThrown = false;
            return;
        }

        throwDirection = direction;
        thrownPosition = Item.transform.position - WorldMover.currentMove;

        //Both machines fold their own origin shift into every position they send. If the two have
        //drifted apart, the same item is two places at once - and the numbers below say by how much.
        Multiplayer.LogDebug(() => $"NetworkedItem.OnThrow() ItemNetId: {NetId}, {name}, world: {Item.transform.position}, wire: {thrownPosition}, currentMove: {WorldMover.currentMove}");
        thrownRotation = Item.transform.rotation;

        //Multiplayer.LogDebug(() => $"NetworkedItem.OnThrow() netId: {NetId}, Name: {name}, Raw Position: {Item.transform.position}, Position: {thrownPosition}, Rotation: {thrownRotation}, Direction: {throwDirection}");

        wasThrown = true;
        stateDirty = true;
    }


    #region Item Value Tracking
    public void RegisterTrackedValue<T>(string key, Func<T> valueGetter, Action<T> valueSetter, Func<T, T, bool> thresholdComparer = null, bool serverAuthoritative = false)
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.RegisterTrackedValue(\"{key}\", {valueGetter != null}, {valueSetter != null}, {thresholdComparer != null}, {serverAuthoritative}) itemNetId {NetId}, item name: {name}");
        trackedValues.Add(new TrackedValue<T>(key, valueGetter, valueSetter, thresholdComparer, serverAuthoritative));
    }

    public void FinaliseTrackedValues()
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.FinaliseTrackedValues() itemNetId: {NetId}, item name: {name}");

        while (pendingSnapshots.Count > 0)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.FinaliseTrackedValues() itemNetId: {NetId}, item name: {name}. Dequeuing");
            ApplySnapshot(pendingSnapshots.Dequeue());
        }

        registrationComplete = true;

    }

    private bool HasDirtyValues()
    {
        //clients should only send values that are not server authoritative
        if (!NetworkLifecycle.Instance.IsHost())
            return trackedValues.Any(tv => ((dynamic)tv).IsDirty && !((dynamic)tv).ServerAuthoritative);
        else
            return trackedValues.Any(tv => ((dynamic)tv).IsDirty);
    }

    private Dictionary<string, object> GetDirtyStateData()
    {
        var dirtyData = new Dictionary<string, object>();
        foreach (var trackedValue in trackedValues)
        {
            if (((dynamic)trackedValue).IsDirty)
            {
                dirtyData[((dynamic)trackedValue).Key] = ((dynamic)trackedValue).GetValueAsObject();
            }
        }
        return dirtyData;
    }
    private Dictionary<string, object> GetAllStateData()
    {
        var data = new Dictionary<string, object>();
        foreach (var trackedValue in trackedValues)
        {
            data[((dynamic)trackedValue).Key] = ((dynamic)trackedValue).GetValueAsObject();
        }
        return data;
    }

    private void MarkValuesClean()
    {
        foreach (var trackedValue in trackedValues)
        {
            ((dynamic)trackedValue).MarkClean();
        }
    }

    #endregion

    public ItemUpdateData GetSnapshot()
    {
        ItemUpdateData snapshot;
        ItemUpdateData.ItemUpdateType updateType = ItemUpdateData.ItemUpdateType.None;

        bool hasDirtyVals = HasDirtyValues();

        if (Item == null && Register() == false)
            return null;

        if (heldByRemote != 0)
            ReviewRemoteHolder();

        //An item we only show in another player's hand is theirs to report on, not ours. It lives
        //under their player object, so everything we could measure about it here would contradict
        //what they are telling us.
        if (remoteHolder != null)
        {
            MarkValuesClean();
            return null;
        }

        //The game re-parents items without raising an event (e.g. an item put down in a loco cab),
        //so a change of parent is treated as a possible state change.
        bool parentChanged = RefreshParentCar();

        //Putting an item down counts as a throw, so a thrown item is still in flight. Keep
        //evaluating it until it comes to rest, otherwise it never leaves the Thrown state.
        bool settling = !wasThrown && lastState == ItemState.Thrown;

        //An item nobody has been told about yet has something to say even when nothing about it has
        //changed. A client buys a beacon, puts it down and leaves it alone: it holds still, so
        //nothing is dirty, and the introduction that would make it real for everyone else never
        //goes out. It only appeared once the player happened to pick it up again.
        if (!createdDirty && !stateDirty && !hasDirtyVals && !parentChanged && !settling)
            return null;

        ItemState currentState = GetItemState();

        //Items stowed in another player's inventory are deactivated for us, we must not report
        //state for those. Items in another player's hand are caught by the remoteHolder check above.
        if (gameObject.activeInHierarchy &&
            (currentState != lastState || (currentState == ItemState.OnCar && parentCar.GetNetId() != lastCarNetId)))
        {
            //Only report where a thrown item ended up once it has actually stopped moving
            if (!settling || HasSettled())
                stateDirty = true;
        }

        if (!createdDirty && !stateDirty && !hasDirtyVals)
            return null;

        if (!createdDirty)
        {
            if (lastState != currentState)
                updateType |= ItemUpdateData.ItemUpdateType.ItemState;

            if (hasDirtyVals)
            {
                Multiplayer.LogDebug(GetDirtyValuesDebugString);
                updateType |= ItemUpdateData.ItemUpdateType.ObjectState;
            }
        }
        else
        {
            updateType = ItemUpdateData.ItemUpdateType.Create;
        }

        //no changes this snapshot
        if (updateType == ItemUpdateData.ItemUpdateType.None)
            return null;

        lastState = currentState;
        lastCarNetId = currentState == ItemState.OnCar ? parentCar.GetNetId() : (ushort)0;
        LastDirtyTick = NetworkLifecycle.Instance.Tick;
        snapshot = CreateUpdateData(updateType);

        createdDirty = false;
        stateDirty = false;
        wasThrown = false;

        MarkValuesClean();

        return snapshot;
    }

    /// <summary>
    /// Marks this item as one the other side already knows about. An item built from someone
    /// else's snapshot starts out looking like something nobody has been told about yet, and would
    /// otherwise be introduced straight back to whoever just introduced it.
    /// </summary>
    public void MarkAsKnownElsewhere()
    {
        createdDirty = false;
    }

    /// <summary>
    /// Takes a name that was decided elsewhere. The host uses this for an item it named in advance
    /// for a client, so both sides end up calling the same thing by the same name.
    /// </summary>
    public void AssignGuid(Guid guid)
    {
        if (guid != Guid.Empty)
            Guid = guid;
    }

    /// <summary>
    /// Takes the name carried along with an item update. Ignored on the host, which does its own
    /// naming and must not let a client rename anything.
    /// </summary>
    public void SetGuid(Guid guid)
    {
        if (NetworkLifecycle.Instance.IsHost())
            return;

        AssignGuid(guid);
    }

    public void ReceiveSnapshot(ItemUpdateData snapshot)
    {
        if (snapshot == null || snapshot.UpdateType == ItemUpdateData.ItemUpdateType.None)
            return;

        SetGuid(snapshot.Guid);

        if (!registrationComplete)
        {
            Multiplayer.Log($"NetworkedItem.ReceiveSnapshot() netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}. Queuing");
            pendingSnapshots.Enqueue(snapshot);
            return;
        }

        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(ItemUpdateData snapshot)
    {
        CancelDeferredOnCar();

        if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ItemState) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.FullSync) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create))
        {
            Multiplayer.Log($"NetworkedItem.ApplySnapshot() netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}, ItemState: {snapshot?.ItemState}, Active state: {gameObject.activeInHierarchy}");

            switch (snapshot.ItemState)
            {
                case ItemState.Dropped:
                case ItemState.Thrown:
                    HandleDroppedOrThrownState(snapshot);
                    break;

                case ItemState.InHand:
                case ItemState.InInventory:
                    HandleInventoryOrHandState(snapshot);
                    break;

                case ItemState.Attached:
                    HandleAttachedState(snapshot);
                    break;

                case ItemState.OnCar:
                    HandleOnCarState(snapshot);
                    break;

                default:
                    throw new Exception($"NetworkedItem.ApplySnapshot() Item state not implemented: {snapshot?.ItemState}");

            }
        }

        Multiplayer.Log($"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, ItemUpdateType {snapshot?.UpdateType} About to process states");

        if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ObjectState))
        {
            Multiplayer.Log($"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, States: {snapshot?.States?.Count}");

            if (trackedItem != null && snapshot.States != null)
            {
                ApplyTrackedValues(snapshot.States);
            }
        }

        Multiplayer.Log($"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, ItemUpdateType {snapshot?.UpdateType} states processed");

        //mark values as clean
        createdDirty = false;
        stateDirty = false;

        SyncStateTracking();

        MarkValuesClean();
        return;
    }

    /// <summary>
    /// Aligns the locally tracked state with reality after applying a received snapshot,
    /// so the change isn't detected as dirty and sent straight back out again.
    /// </summary>
    private void SyncStateTracking()
    {
        RefreshParentCar(true);
        lastState = GetItemState();
        lastCarNetId = lastState == ItemState.OnCar ? parentCar.GetNetId() : (ushort)0;
    }

    public ItemUpdateData CreateUpdateData(ItemUpdateData.ItemUpdateType updateType)
    {
        if (transform == null || Item == null || Item?.InventorySpecs == null || Item?.InventorySpecs?.ItemPrefabName == null)
        {
            Multiplayer.LogDebug(()=>$"NetworkedItem.CreateUpdateData({updateType}) NetId: {NetId}, name: {name}. Transform is null: {transform == null}, Item is null: {Item == null}, Inventory Specs: {Item?.InventorySpecs == null}, ItemPrefabName is null: {Item?.InventorySpecs?.ItemPrefabName == null}");
            return null;
        }

        Vector3 position;
        Quaternion rotation;
        Dictionary<string, object> states;
        ushort carId = 0;
        bool frontCoupler = true;

        if (wasThrown)
        {
            position = thrownPosition;
            rotation = thrownRotation;
        }
        else
        {
            position = transform.position - WorldMover.currentMove;
            rotation = transform.rotation;
        }

        if (updateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || updateType.HasFlag(ItemUpdateData.ItemUpdateType.FullSync))
        {
            states = GetAllStateData();
        }
        else
        {
            states = GetDirtyStateData();
        }

        ItemState itemState = lastState;

        if (itemState == ItemState.Attached)
        {
            ItemSnapPointCoupler itemSnapPointCoupler = snappableItem.SnappedTo as ItemSnapPointCoupler;

            if (itemSnapPointCoupler != null)
            {
                carId = itemSnapPointCoupler.Car.GetNetId();
                frontCoupler = itemSnapPointCoupler.IsFront;
            }
        }
        else if (itemState == ItemState.OnCar)
        {
            RefreshParentCar();
            carId = parentCar.GetNetId();

            if (carId != 0)
            {
                //Send the pose relative to the car, the item moves with the car on the receiving side
                position = parentCar.transform.InverseTransformPoint(transform.position);
                rotation = Quaternion.Inverse(parentCar.transform.rotation) * transform.rotation;
            }
            else
            {
                //The car isn't networked (yet), fall back to a world position so the item isn't lost
                Multiplayer.LogWarning($"NetworkedItem.CreateUpdateData({updateType}) NetId: {NetId}, name: {name}. Item is on a car without a netId, sending world position");
                itemState = ItemState.Dropped;
            }
        }

        //An item in a hand or a bag has no position of its own to send, the player carrying it is
        //what the other side needs in order to place it. Usually that is us; when we are only
        //passing on what someone else told us, it is still them.
        byte holder = 0;
        if (itemState == ItemState.InHand || itemState == ItemState.InInventory)
            holder = heldByRemote != 0 ? heldByRemote : (NetworkLifecycle.Instance.Client?.PlayerId ?? 0);

        var updateData = new ItemUpdateData
        {
            UpdateType = updateType,
            ItemNetId = NetId,
            PrefabName = Item.InventorySpecs.ItemPrefabName,
            ItemState = itemState,
            Player = holder,
            ItemPosition = position,
            ItemRotation = rotation,
            ThrowDirection = throwDirection,
            CarNetId = carId,
            AttachedFront = frontCoupler,
            States = states,
            Guid = Guid,
        };

        return updateData;
    }

    private ItemState GetItemState()
    {
        //An item another player is carrying is in their hand or their bag, whatever our own copy of
        //it is parented to. Reading it off the transform would call it dropped on the spot.
        if (mirroredState.HasValue)
            return mirroredState.Value;

        RefreshParentCar();

        //Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}, isGrabbed: {Item.IsGrabbed()} Inventory.Contains(): {Inventory.Instance.Contains(this.gameObject, false)} Storage.Contains: {StorageController.Instance.StorageInventory.ContainsItem(Item)}");


        if (Item.transform.parent == WorldMover.OriginShiftParent && !wasThrown)
        {
            Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}");
            return ItemState.Dropped;
        }

        if (wasThrown)
        {
            Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}");
            return ItemState.Thrown;
        }

        if (Item.IsGrabbed())
            return ItemState.InHand;

        if (Inventory.Instance.Contains(this.gameObject, false))
            return ItemState.InInventory;

        if (snappableItem != null && snappableItem.IsSnapped)
        {
            Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, snapped! {this.transform.parent}");
            return ItemState.Attached;
        }

        if (parentCar != null)
        {
            Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, on car {parentCar?.ID}");
            return ItemState.OnCar;
        }

        //do we need a condition to check if it's attached to something else (last attach vs current attach)?
        Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, no car found for parent {transform.parent}, falling back to Dropped");
        return ItemState.Dropped;

    }

    /// <summary>
    /// Whether the item has stopped moving. Used to find where a thrown item came to rest.
    /// </summary>
    private bool HasSettled()
    {
        Rigidbody itemRigidbody = Item?.ItemRigidbody;

        if (itemRigidbody == null)
            return true;

        return itemRigidbody.IsSleeping() || itemRigidbody.velocity.sqrMagnitude <= SETTLED_VELOCITY * SETTLED_VELOCITY;
    }

    /// <summary>
    /// Resolves the TrainCar an item is parented to. The hierarchy is only walked when the item's
    /// parent actually changed, so this is cheap enough to call every tick.
    /// </summary>
    /// <param name="force">Re-resolve even if the parent didn't change.</param>
    /// <returns>True if the item's parent changed since the last call.</returns>
    private bool RefreshParentCar(bool force = false)
    {
        Transform currentParent = transform.parent;

        if (parentLookupDone && currentParent == lastKnownParent)
        {
            if (!force)
                return false;
        }

        bool changed = !parentLookupDone || currentParent != lastKnownParent;

        lastKnownParent = currentParent;
        parentLookupDone = true;
        parentCar = null;

        if (currentParent == null || currentParent == WorldMover.OriginShiftParent)
            return changed;

        //GetComponentInParent() skips inactive objects on this Unity version, so walk the hierarchy ourselves
        for (Transform parent = currentParent; parent != null; parent = parent.parent)
        {
            if (parent.TryGetComponent(out TrainCar trainCar))
            {
                parentCar = trainCar;
                break;
            }
        }

        //A loco's loaded interior is not a child of its TrainCar, so items put down in a cab have to be matched against it
        if (parentCar == null)
            parentCar = FindCarByInterior(currentParent);

        return changed;
    }

    /// <summary>
    /// Finds the car the given transform belongs to when it isn't a child of the car itself,
    /// which is the case for anything put down inside a loco interior.
    /// </summary>
    private static TrainCar FindCarByInterior(Transform child)
    {
        if (child == null)
            return null;

        //The game resolves a car from anything belonging to it, interiors included
        TrainCar resolved = TrainCar.Resolve(child);

        if (resolved != null)
            return resolved;

        TrainCarRegistry registry = TrainCarRegistry.Instance;

        if (registry == null || registry.logicCarToTrainCar == null)
            return null;

        //A car keeps two interiors, the streamed one only exists while a player is inside
        foreach (TrainCar trainCar in registry.logicCarToTrainCar.Values)
        {
            if (trainCar == null)
                continue;

            Transform loaded = trainCar.loadedInterior?.transform;
            Transform interior = trainCar.interior?.transform;

            if (IsBelow(child, loaded) || IsBelow(child, interior))
                return trainCar;
        }

        Multiplayer.LogDebug(() => $"FindCarByInterior() no car owns {GetPath(child)}");

        return null;
    }

    /// <summary>
    /// Builds the hierarchy path of a transform, so we can see where an object actually sits.
    /// </summary>
    private static string GetPath(Transform transform)
    {
        if (transform == null)
            return "null";

        StringBuilder sb = new StringBuilder(transform.name);

        for (Transform parent = transform.parent; parent != null; parent = parent.parent)
            sb.Insert(0, parent.name + "/");

        return sb.ToString();
    }

    /// <summary>
    /// Checks whether a transform is the given interior or sits somewhere below it.
    /// </summary>
    private static bool IsBelow(Transform child, Transform interior)
    {
        return interior != null && child.IsChildOf(interior);
    }

    private void ApplyTrackedValues(Dictionary<string, object> newValues)
    {
        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Null checks");

        if (newValues == null || newValues.Count == 0)
            return;


        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Registration complete: {registrationComplete}");

        foreach (var newValue in newValues)
        {
            var trackedValue = trackedValues.Find(tv => ((dynamic)tv).Key == newValue.Key);
            if (trackedValue != null)
            {
                if (!NetworkLifecycle.Instance.IsHost() || !((dynamic)trackedValue).ServerAuthoritative)
                {
                    try
                    {
                        ((dynamic)trackedValue).SetValueFromObject(newValue.Value);
                        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}, Updated tracked value: {newValue.Key}, value: {newValue.Value} ");
                    }
                    catch (Exception ex)
                    {
                        Multiplayer.LogError($"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Error updating tracked value {newValue.Key}: {ex.Message}");
                    }
                }
                else
                {
                    Multiplayer.LogWarning($"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Skipped server-authoritative value update from client: {newValue.Key}");
                }
            }
            else
            {
                Multiplayer.LogWarning($"Tracked value not found: {newValue.Key}\r\n {String.Join(", ", trackedValues.Select(val => ((dynamic)val).Key))}");
            }
        }
    }

    #region Item State Update Handlers

    private void HandleDroppedOrThrownState(ItemUpdateData snapshot)
    {
        //it may have been in another player's hand
        heldByRemote = 0;
        mirroredState = null;
        ReleaseFromRemoteHand();

        //resolve attachment
        if (Item.IsSnapped)
        {
            Item.SnappableItem.SnappedTo.UnsnapItem(false);
        }

        //resolve ownership
        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && player.OwnsItem(NetId))
                player.RemoveOwnedItem(NetId);

        //release the item from any car it was resting on
        if (transform.parent != WorldMover.OriginShiftParent)
            transform.SetParent(WorldMover.OriginShiftParent, true);

        //It may have been parked while it rode along on a car
        SetRidingPhysics(false);

        //activate and relocate item
        gameObject.SetActive(true);
        transform.position = snapshot.ItemPosition + WorldMover.currentMove;
        transform.rotation = snapshot.ItemRotation;
        OwnerId = 0;

        //handle throwing of the item
        if (snapshot.ItemState == ItemState.Thrown)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState() ItemNetId: {snapshot?.ItemNetId} Thrown. Position: {transform.position}, Direction: {snapshot?.ThrowDirection}, wire: {snapshot?.ItemPosition}, currentMove: {WorldMover.currentMove}");

            wasThrown = true;
            grabHandler?.Throw(snapshot.ThrowDirection);
        }
        else
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState() ItemNetId: {snapshot?.ItemNetId} Dropped. Position: {transform.position}, wire: {snapshot?.ItemPosition}, currentMove: {WorldMover.currentMove}");
        }
    }

    private void HandleAttachedState(ItemUpdateData snapshot)
    {
        //it may have been in another player's hand
        heldByRemote = 0;
        mirroredState = null;
        ReleaseFromRemoteHand();

        //resovle ownership
        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && player.OwnsItem(NetId))
                player.RemoveOwnedItem(NetId);

        //handle attaching the item
        gameObject.SetActive(true);
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleAttachedState() ItemNetId: {snapshot?.ItemNetId} attempting attachment to car {snapshot.CarNetId}, at the front {snapshot.AttachedFront}");

        if (!NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar trainCar))
        {
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() CarNetId: {snapshot?.CarNetId} not found for ItemNetId: {snapshot?.ItemNetId}");
            return;
        }

        //Try to find the coupler snap point for the car and correct end to snap to
        var snapPoint = trainCar?.physicsLod?.GetCouplerSnapPoints()
            .FirstOrDefault(sp => sp.IsFront == snapshot.AttachedFront);

        if (snapPoint == null)
        {
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() ItemNetId: {snapshot?.ItemNetId}. No valid snap point found for car {snapshot.CarNetId}");
            return;
        }

        //Attempt attachment to car
        SetRidingPhysics(false);
        if (!snapPoint.SnapItem(Item, false))
        {
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() Attachment failed for item {snapshot?.ItemNetId} to car {snapshot.CarNetId}");
        }
    }

    private void HandleOnCarState(ItemUpdateData snapshot)
    {
        //it may have been in another player's hand
        heldByRemote = 0;
        mirroredState = null;
        ReleaseFromRemoteHand();

        //resolve attachment
        if (Item.IsSnapped)
        {
            Item.SnappableItem.SnappedTo.UnsnapItem(false);
        }

        //resolve ownership
        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && player.OwnsItem(NetId))
                player.RemoveOwnedItem(NetId);

        OwnerId = 0;

        if (!NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar trainCar) || trainCar == null)
        {
            //The car may not have spawned yet (e.g. joining, or the trainset is still streaming in)
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleOnCarState() ItemNetId: {snapshot?.ItemNetId}. Car {snapshot?.CarNetId} not found, deferring");
            deferredOnCar = CoroutineManager.Instance.StartCoroutine(WaitForCar(snapshot));
            return;
        }

        ApplyOnCarState(snapshot, trainCar);
    }

    private void ApplyOnCarState(ItemUpdateData snapshot, TrainCar trainCar)
    {
        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyOnCarState() ItemNetId: {snapshot?.ItemNetId}, car: [{trainCar?.ID}, {snapshot?.CarNetId}], local position: {snapshot?.ItemPosition}");

        gameObject.SetActive(true);

        //parent to the car so the item moves with it, the pose is relative to the car
        transform.SetParent(trainCar.transform, false);
        transform.localPosition = snapshot.ItemPosition;
        transform.localRotation = snapshot.ItemRotation;

        //Has to come after the pose, a simulated rigidbody would immediately overwrite it
        SetRidingPhysics(true);
    }

    /// <summary>
    /// Parks or wakes the item's rigidbody. A simulated rigidbody is moved by the physics engine
    /// in world space and ignores its parent, so an item resting on a car would be left behind the
    /// moment the car starts moving. Items we only mirror for another player are held in place by
    /// the parent instead.
    /// </summary>
    private void SetRidingPhysics(bool riding)
    {
        Rigidbody itemRigidbody = Item?.ItemRigidbody;

        if (itemRigidbody == null || itemRigidbody.isKinematic == riding)
            return;

        if (riding)
        {
            itemRigidbody.velocity = Vector3.zero;
            itemRigidbody.angularVelocity = Vector3.zero;
        }

        itemRigidbody.isKinematic = riding;

        Multiplayer.LogDebug(() => $"NetworkedItem.SetRidingPhysics({riding}) NetId: {NetId}, name: {name}");
    }

    private IEnumerator WaitForCar(ItemUpdateData snapshot)
    {
        float startTime = Time.time;

        while (Time.time - startTime < CAR_WAIT_TIMEOUT)
        {
            yield return new WaitForSeconds(CAR_WAIT_INTERVAL);

            if (this == null)
                yield break;

            if (NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar trainCar) && trainCar != null)
            {
                deferredOnCar = null;
                ApplyOnCarState(snapshot, trainCar);
                SyncStateTracking();
                yield break;
            }
        }

        deferredOnCar = null;
        Multiplayer.LogWarning($"NetworkedItem.WaitForCar() ItemNetId: {NetId}, name: {name}. Car {snapshot.CarNetId} did not appear, item left where it is");
    }

    private void CancelDeferredOnCar()
    {
        if (deferredOnCar == null)
            return;

        CoroutineManager.Instance?.Stop(deferredOnCar);
        deferredOnCar = null;
    }

    private void HandleInventoryOrHandState(ItemUpdateData snapshot)
    {
        //Remember who has it, unless the snapshot is talking about us
        if (snapshot.Player != 0 && snapshot.Player != (NetworkLifecycle.Instance.Client?.PlayerId ?? 0))
        {
            heldByRemote = snapshot.Player;
            mirroredState = snapshot.ItemState;
        }
        else
        {
            heldByRemote = 0;
            mirroredState = null;
        }

        if (Item.IsSnapped)
        {
            Item.SnappableItem.SnappedTo.UnsnapItem(false);
        }

        //It may have been parked while it rode along on a car
        SetRidingPhysics(false);

        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && !player.OwnsItem(NetId))
                player.AddOwnedItem(NetId);

        //Show it in the other player's hand if we have a model to hang it off
        if (snapshot.ItemState == ItemState.InHand && TryShowInRemoteHand(snapshot.Player))
            return;

        //Stowed, or held by someone we can't see - out of sight either way
        ReleaseFromRemoteHand();
        this.gameObject.SetActive(false);
    }

    /// <summary>
    /// Puts the item into its carrier's hand once we know who that is. Their model can arrive after
    /// the item does: on joining, the host introduces everything standing nearby, and an item can
    /// land before the player whose hand it is in - leaving it switched off for good, because
    /// nothing about it changes again for the host to tell us about.
    /// </summary>
    private void ReviewRemoteHolder()
    {
        if (mirroredState != ItemState.InHand || remoteHolder != null)
            return;

        if (!NetworkLifecycle.Instance.IsClientRunning)
            return;

        if (!NetworkLifecycle.Instance.Client.ClientPlayerManager.TryGetPlayer(heldByRemote, out NetworkedPlayer holder) || holder == null)
            return;

        Multiplayer.LogDebug(() => $"NetworkedItem.ReviewRemoteHolder() NetId: {NetId}, name: {name}. Player {holder.Username} is here now");
        TryShowInRemoteHand(heldByRemote);
    }

    /// <summary>
    /// Hands the item back to the world when the player carrying it has left. Nobody is going to
    /// say where it went otherwise, so whatever they had in hand is left where they stood.
    /// </summary>
    public void ReleaseHeldBy(byte playerId)
    {
        if (heldByRemote != playerId)
            return;

        bool wasInHand = mirroredState == ItemState.InHand;

        Multiplayer.LogDebug(() => $"NetworkedItem.ReleaseHeldBy({playerId}) NetId: {NetId}, name: {name}, was in hand: {wasInHand}");

        heldByRemote = 0;
        mirroredState = null;
        ReleaseFromRemoteHand();

        if (!wasInHand)
            return;

        gameObject.SetActive(true);
        stateDirty = true;
    }

    /// <summary>
    /// Hangs the item off another player's model so we can see what they are carrying.
    /// </summary>
    /// <returns>True if the item is now in that player's hand.</returns>
    private bool TryShowInRemoteHand(byte playerId)
    {
        if (playerId == 0 || !NetworkLifecycle.Instance.IsClientRunning)
            return false;

        //Our own hands are the real thing, we never mirror those. The player map only holds
        //other players, so this fails for us by itself.
        if (!NetworkLifecycle.Instance.Client.ClientPlayerManager.TryGetPlayer(playerId, out NetworkedPlayer holder) || holder == null)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.TryShowInRemoteHand() NetId: {NetId}, name: {name}. No player model for {playerId}");
            return false;
        }

        //Already in that player's hand, nothing to do but make sure it can be seen
        if (remoteHolder == holder && holder.RightHandItemGO == gameObject)
        {
            gameObject.SetActive(true);
            return true;
        }

        ReleaseFromRemoteHand();

        //Only one item fits in the hand we model, whatever was there goes out of sight
        if (holder.RightHandItemGO != null && holder.RightHandItemGO != gameObject)
        {
            NetworkedItem previous = holder.RightHandItemGO.GetComponent<NetworkedItem>();
            holder.DropItem();

            if (previous != null)
            {
                previous.remoteHolder = null;
                previous.gameObject.SetActive(false);
            }
        }

        if (transform.parent != WorldMover.OriginShiftParent)
            transform.SetParent(WorldMover.OriginShiftParent, true);

        gameObject.SetActive(true);

        GetHoldAnchor(out Vector3? localPos, out Quaternion? localRot);
        holder.HoldItem(gameObject, localPos, localRot);
        remoteHolder = holder;

        Multiplayer.LogDebug(() => $"NetworkedItem.TryShowInRemoteHand() NetId: {NetId}, name: {name}, player: {holder.Username}, localPos: {localPos}, localRot: {localRot}");
        return true;
    }

    /// <summary>
    /// Takes the item back out of another player's hand. Safe to call when it isn't in one.
    /// </summary>
    private void ReleaseFromRemoteHand()
    {
        NetworkedPlayer holder = remoteHolder;
        remoteHolder = null;

        if (holder == null)
            return;

        if (holder.RightHandItemGO == gameObject)
            holder.DropItem();

        Multiplayer.LogDebug(() => $"NetworkedItem.ReleaseFromRemoteHand() NetId: {NetId}, name: {name}");
    }

    /// <summary>
    /// Reads the item's own idea of where it sits in a non-VR hand, so a mirrored item is held the
    /// same way the owner sees it.
    /// </summary>
    private void GetHoldAnchor(out Vector3? localPos, out Quaternion? localRot)
    {
        localPos = null;
        localRot = null;

        FovBasedNonVRGrabAnchor fovAnchor = GetComponentInChildren<FovBasedNonVRGrabAnchor>(true);
        if (fovAnchor != null)
        {
            var anchor = fovAnchor.GetGrabAnchor();
            localPos = anchor.localPos;
            localRot = anchor.localRot;
            return;
        }

        CustomNonVrGrabAnchor customAnchor = GetComponentInChildren<CustomNonVrGrabAnchor>(true);
        if (customAnchor != null)
        {
            var anchor = customAnchor.GetGrabAnchor();
            localPos = anchor.localPos;
            localRot = anchor.localRot;
        }
    }
    #endregion

    protected override void OnDestroy()
    {
        if (UnloadWatcher.isQuitting)
            return;

        CancelDeferredOnCar();
        ReleaseFromRemoteHand();

        if (UnloadWatcher.isUnloading)
        {
            itemBaseToNetworkedItem.Clear();
            base.OnDestroy();
            return;
        }

        if (NetworkLifecycle.Instance.IsHost())
        {
            var updateData = CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
            if (updateData != null)
                NetworkedItemManager.Instance.AddDirtyItemSnapshot(this, updateData);
        }

        if (Item != null)
        {
            Item.Grabbed -= OnGrabbed;
            Item.Ungrabbed -= OnUngrabbed;
            itemBaseToNetworkedItem.Remove(Item);
        }
        else
        {
            Multiplayer.LogWarning($"NetworkedItem.OnDestroy({name}, {NetId}) Item is null!");
        }

        base.OnDestroy();

    }

    public string GetDirtyValuesDebugString()
    {
        var dirtyValues = trackedValues.Where(tv => ((dynamic)tv).IsDirty).ToList();
        if (dirtyValues.Count == 0)
        {
            return "No dirty values";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Dirty values for NetworkedItem: {name}, NetId: {NetId}:");
        foreach (var value in dirtyValues)
        {
            sb.AppendLine(((dynamic)value).GetDebugString());
        }
        return sb.ToString();
    }
}
