using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using Multiplayer.Components.Networking.Player;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data.Gadgets;
using Multiplayer.Networking.Packets.Common;
using Multiplayer.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

/// <summary>
/// Translates between the game's gadget objects and the wire. A gadget's own state is carried as the
/// JSON the game writes into a save, so this never has to know what any individual gadget stores.
/// </summary>
public static class NetworkedGadgets
{
    /// <summary>
    /// True while a change received from the network is being applied, so the patches that watch the
    /// game's gadget calls know not to send it straight back out again.
    /// </summary>
    public static bool IsApplyingRemoteChange { get; private set; }

    //A gadget's UID comes from a counter that runs per process, so two machines placing at the same
    //time hand out the same number for different gadgets and TryGetCustomizerByUID then finds the
    //wrong one. Give every player their own slice of the number space instead.
    private const int UID_PLAYER_SHIFT = 24;
    private const int UID_COUNTER_MASK = 0xFFFFFF;

    private static int localUidCounter;

    //An item built this very frame is not yet whole enough to be placed, so the packet that needs
    //it waits, and everything arriving afterwards waits behind it to keep the order intact.
    private const int SETTLE_FRAMES = 5;

    private static readonly Queue<CommonGadgetPacket> waiting = new Queue<CommonGadgetPacket>();
    private static Coroutine drain;

    /// <summary>
    /// Mints a UID no other machine in this session can produce, or 0 when there is no session to
    /// collide with and the game's own numbering can stand.
    /// </summary>
    public static int NextLocalUid(Customization destination)
    {
        byte playerId = NetworkLifecycle.Instance?.Client?.PlayerId ?? 0;

        if (playerId == 0)
            return 0;

        TrainCarCustomization carCustomization = destination as TrainCarCustomization;

        //The counter starts over every session, but gadgets this player placed in an earlier one are
        //still on the car under the numbers it handed out back then. Step past those.
        for (int attempt = 0; attempt <= UID_COUNTER_MASK; attempt++)
        {
            localUidCounter = (localUidCounter + 1) & UID_COUNTER_MASK;

            if (localUidCounter == 0)
                localUidCounter = 1;

            int uid = (playerId << UID_PLAYER_SHIFT) | localUidCounter;

            if (carCustomization == null || !carCustomization.TryGetCustomizerByUID(uid, out _))
                return uid;
        }

        return 0;
    }

    /// <summary>
    /// Gives a gadget a UID without disturbing the game's own counter. AssignUID drags that counter
    /// up to whatever it is handed, which would push the game's next number into another player's
    /// slice, so put it back afterwards.
    /// </summary>
    public static void AssignNetworkUid(GadgetBase gadget, int uid)
    {
        if (gadget == null || uid == 0)
            return;

        int gameCounter = TrainCarCustomization.TrainCarCustomizerBase.uidCounter;

        gadget.AssignUID(uid);

        TrainCarCustomization.TrainCarCustomizerBase.uidCounter = gameCounter;
    }

    /// <summary>
    /// True while this item is bolted onto something as a gadget. The game hides the item away in
    /// installed gadgets for as long as that lasts, so it is not a world item and must not be synced
    /// as one, or it reappears at whoever placed it.
    /// </summary>
    public static bool IsInstalledGadget(ItemBase item)
    {
        if (item == null)
            return false;

        GadgetItem gadgetItem = item.GetComponent<GadgetItem>();

        return gadgetItem != null && gadgetItem.Gadget != null && gadgetItem.Gadget.IsLinked;
    }

    //Everything a gadget knows about itself, from a lamp's power switch to which gadget sits on a
    //mount, lives in the data it writes into a save. That data only travelled when the gadget was
    //placed, so anything changed afterwards never reached the other side. Watch it for changes
    //instead: one path covers every gadget type, including ones added to the game later.
    private const int STATE_POLL_TICKS = 10;

    private static readonly Dictionary<GadgetBase, string> trackedState = new Dictionary<GadgetBase, string>();
    private static readonly List<GadgetBase> pollBuffer = new List<GadgetBase>();

    //Placing can no longer report itself straight away: an item a client brought along has to be
    //given a net id by the host first, and the player may well have taken the gadget off again
    //before that comes back. Only a gadget whose attach actually went out has a detach worth
    //sending, and only one that is still attached is worth announcing at all.
    private static readonly HashSet<GadgetBase> announced = new HashSet<GadgetBase>();

    public static void MarkAnnounced(GadgetBase gadget)
    {
        if (gadget != null)
            announced.Add(gadget);
    }

    public static bool WasAnnounced(GadgetBase gadget)
    {
        return gadget != null && announced.Contains(gadget);
    }

    public static void ClearAnnounced(GadgetBase gadget)
    {
        if (gadget != null)
            announced.Remove(gadget);
    }

    /// <summary>
    /// Starts watching a gadget's data, taking what it holds now as the agreed starting point.
    /// </summary>
    public static void Track(GadgetBase gadget)
    {
        if (gadget != null)
            trackedState[gadget] = SerialiseState(gadget);
    }

    /// <summary>
    /// Reports any watched gadget whose data no longer matches what everyone last agreed on.
    /// </summary>
    public static void PollStates(uint tick)
    {
        if (tick % STATE_POLL_TICKS != 0 || trackedState.Count == 0)
            return;

        pollBuffer.Clear();
        pollBuffer.AddRange(trackedState.Keys);

        foreach (GadgetBase gadget in pollBuffer)
        {
            //Taken off or destroyed: its removal has been reported through its own path
            if (gadget == null || !gadget.IsLinked)
            {
                trackedState.Remove(gadget);
                continue;
            }

            string current = SerialiseState(gadget);

            if (current == trackedState[gadget])
                continue;

            trackedState[gadget] = current;

            if (!TryGetCarNetId(gadget.Custom, out ushort carNetId))
                continue;

            Multiplayer.LogDebug(() => $"NetworkedGadgets.PollStates() uid {gadget.UID} on car {carNetId} changed");

            NetworkLifecycle.Instance.Client?.SendGadgetChange(new CommonGadgetPacket
            {
                Action = GadgetAction.State,
                CarNetId = carNetId,
                Uid = gadget.UID,
                State = current
            });
        }
    }

    /// <summary>
    /// Describes every gadget currently bolted onto the cars, so a joining player can be told what
    /// the world already looks like. Without this both sides keep whatever their own save had, each
    /// under its own UIDs, and nothing either of them does to a gadget reaches the other.
    /// </summary>
    public static List<CommonGadgetPacket> DescribeAll()
    {
        List<CommonGadgetPacket> described = new List<CommonGadgetPacket>();

        foreach (Trainset set in Trainset.allSets)
        {
            foreach (TrainCar car in set.cars)
            {
                if (car == null || !(car.Customization is TrainCarCustomization customization))
                    continue;

                if (!TryGetCarNetId(customization, out ushort carNetId))
                    continue;

                foreach (Customization.CustomizerBase customizer in customization.Customizers)
                {
                    if (!(customizer is GadgetBase gadget) || gadget.GadgetItem == null)
                        continue;

                    if (!NetworkedItem.TryGetNetId(gadget.GadgetItem.Item, out ushort itemNetId) || itemNetId == 0)
                    {
                        Multiplayer.LogWarning($"NetworkedGadgets.DescribeAll() gadget uid {gadget.UID} on car {carNetId} has no networked item, skipping");
                        continue;
                    }

                    described.Add(new CommonGadgetPacket
                    {
                        Action = GadgetAction.Attached,
                        CarNetId = carNetId,
                        Uid = gadget.UID,
                        ItemNetId = itemNetId,
                        PrefabName = gadget.GadgetItem.Item?.InventorySpecs?.ItemPrefabName,
                        LocalPosition = gadget.transform.localPosition,
                        LocalRotation = gadget.transform.localRotation,
                        State = SerialiseState(gadget)
                    });
                }
            }
        }

        return described;
    }

    /// <summary>
    /// Takes every gadget off the cars. A joining client's own save put them there under UIDs nobody
    /// else knows, so they are replaced wholesale by what the host describes.
    /// </summary>
    public static void ClearAllFromCars()
    {
        ClearWaiting();

        IsApplyingRemoteChange = true;

        try
        {
            foreach (Trainset set in Trainset.allSets)
            {
                foreach (TrainCar car in set.cars)
                {
                    if (car == null || !(car.Customization is TrainCarCustomization customization))
                        continue;

                    //Link() adds to this list and Unlink() takes away, so walk it backwards
                    for (int i = customization.Customizers.Count - 1; i >= 0; i--)
                    {
                        if (!(customization.Customizers[i] is GadgetBase gadget))
                            continue;

                        ClearAnnounced(gadget);
                        trackedState.Remove(gadget);

                        try
                        {
                            gadget.ForceRemove(false);
                        }
                        catch (Exception e)
                        {
                            Multiplayer.LogWarning($"NetworkedGadgets.ClearAllFromCars() could not take down uid {gadget.UID}: {e.Message}");
                        }
                    }
                }
            }
        }
        finally
        {
            IsApplyingRemoteChange = false;
        }

        Multiplayer.LogDebug(() => "NetworkedGadgets.ClearAllFromCars() cleared the cars for the host's own set");
    }

    public static bool TryGetCustomization(ushort carNetId, out TrainCarCustomization customization)
    {
        customization = null;

        if (!NetworkedTrainCar.TryGet(carNetId, out NetworkedTrainCar netCar) || netCar.TrainCar == null)
            return false;

        customization = netCar.TrainCar.Customization as TrainCarCustomization;

        return customization != null;
    }

    /// <summary>
    /// Resolves the car a gadget is attached to. Returns false for anything not on a train car,
    /// such as the storage shed or the player house, which this does not sync.
    /// </summary>
    public static bool TryGetCarNetId(Customization customization, out ushort carNetId)
    {
        carNetId = 0;

        TrainCarCustomization trainCarCustomization = customization as TrainCarCustomization;

        if (trainCarCustomization == null || trainCarCustomization.TrainCar == null)
            return false;

        carNetId = trainCarCustomization.TrainCar.GetNetId();

        return carNetId != 0;
    }

    public static bool TryGetGadget(ushort carNetId, int uid, out GadgetBase gadget)
    {
        gadget = null;

        if (!TryGetCustomization(carNetId, out TrainCarCustomization customization))
            return false;

        if (!customization.TryGetCustomizerByUID(uid, out TrainCarCustomization.TrainCarCustomizerBase customizer))
            return false;

        gadget = customizer as GadgetBase;

        return gadget != null;
    }

    /// <summary>
    /// Asks the gadget for the same data it would write into a save.
    /// </summary>
    public static string SerialiseState(GadgetBase gadget)
    {
        try
        {
            JObject data = new JObject();
            gadget.SaveDataRequested(data);

            return data.ToString(Formatting.None);
        }
        catch (Exception e)
        {
            Multiplayer.LogError($"NetworkedGadgets.SerialiseState() {gadget?.name}: {e.Message}");

            return string.Empty;
        }
    }

    /// <summary>
    /// Feeds a gadget the state from another instance, along the same path a save load takes.
    /// </summary>
    public static void ApplyState(GadgetBase gadget, string state)
    {
        if (gadget == null || string.IsNullOrEmpty(state))
            return;

        try
        {
            JObject data = JObject.Parse(state);

            //SaveDataLoaded assigns the UID from the data, dragging the game's counter along with it
            int gameCounter = TrainCarCustomization.TrainCarCustomizerBase.uidCounter;

            gadget.SaveDataLoaded(data);

            TrainCarCustomization.TrainCarCustomizerBase.uidCounter = gameCounter;

            gadget.AfterSaveDataLoaded(data);
        }
        catch (Exception e)
        {
            Multiplayer.LogError($"NetworkedGadgets.ApplyState() {gadget?.name}: {e.Message}");
        }
    }

    public static void Apply(CommonGadgetPacket packet)
    {
        if (packet == null)
            return;

        //Anything behind a packet that is waiting on a frame has to wait with it, or a detach
        //arrives at a car the matching attach has not reached yet and finds nothing to take down.
        if (waiting.Count > 0)
        {
            waiting.Enqueue(packet.Copy());
            EnsureDrain();

            return;
        }

        if (!ApplyImmediate(packet, true))
            Defer(packet);
    }

    /// <summary>
    /// Runs a packet against the world. Returns false when it asked to be held over to a later
    /// frame, which only ever happens on the first attempt.
    /// </summary>
    private static bool ApplyImmediate(CommonGadgetPacket packet, bool mayDefer)
    {
        IsApplyingRemoteChange = true;

        try
        {
            switch (packet.Action)
            {
                case GadgetAction.Attached:
                    return ApplyAttached(packet, mayDefer);

                case GadgetAction.Detached:
                    ApplyDetached(packet);
                    break;

                case GadgetAction.MountPointState:
                    ApplyMountPointState(packet);
                    break;

                case GadgetAction.State:
                    ApplyStatePacket(packet);
                    break;
            }

            return true;
        }
        catch (Exception e)
        {
            Multiplayer.LogError($"NetworkedGadgets.Apply({packet.Action}) car: {packet.CarNetId}, uid: {packet.Uid}: {e.Message}\r\n{e.StackTrace}");

            return true;
        }
        finally
        {
            IsApplyingRemoteChange = false;
        }
    }

    /// <summary>
    /// Holds a packet over until the item it names has had a frame to finish building itself, then
    /// replays it along with everything that queued up behind it.
    /// </summary>
    private static void Defer(CommonGadgetPacket packet)
    {
        if (NetworkedItemManager.Instance == null)
        {
            //Nothing here to hang a wait on, so take the chance now rather than queue for a frame
            //that will never come round
            ApplyImmediate(packet, false);

            return;
        }

        Multiplayer.LogDebug(() => $"NetworkedGadgets.Defer() {packet.Action} car: {packet.CarNetId}, uid: {packet.Uid} is waiting for its item to finish building");

        waiting.Enqueue(packet.Copy());
        EnsureDrain();
    }

    private static void EnsureDrain()
    {
        if (drain != null || waiting.Count == 0)
            return;

        if (NetworkedItemManager.Instance == null)
        {
            //Without somewhere to run the wait, holding these back would strand every gadget packet
            //behind them for the rest of the session
            while (waiting.Count > 0)
                ApplyImmediate(waiting.Dequeue(), false);

            return;
        }

        drain = NetworkedItemManager.Instance.StartCoroutine(Drain());
    }

    private static IEnumerator Drain()
    {
        //A freshly built item only finishes wiring itself up on the frames after it appeared, and
        //Place reaches straight into that wiring.
        for (int i = 0; i < SETTLE_FRAMES; i++)
            yield return null;

        while (waiting.Count > 0)
        {
            CommonGadgetPacket packet = waiting.Peek();

            //Second time round it goes through whatever the outcome, so this cannot circle forever
            ApplyImmediate(packet, false);

            waiting.Dequeue();
        }

        drain = null;
    }

    /// <summary>
    /// Drops anything still waiting, for a session that is going away.
    /// </summary>
    public static void ClearWaiting()
    {
        waiting.Clear();

        if (drain != null && NetworkedItemManager.Instance != null)
            NetworkedItemManager.Instance.StopCoroutine(drain);

        drain = null;
    }

    private static bool ApplyAttached(CommonGadgetPacket packet, bool mayDefer)
    {
        if (!TryGetCustomization(packet.CarNetId, out TrainCarCustomization customization))
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() no customization for car {packet.CarNetId}");
            return true;
        }

        if (!NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem netItem) || netItem.Item == null)
        {
            //The id was set aside for an item only the sender has. Build it here now that it is
            //needed, rather than having made a spare copy back when the id was handed out.
            netItem = NetworkedItemManager.Instance.CreateItemFromPrefab(packet.PrefabName, packet.ItemNetId);

            if (netItem == null || netItem.Item == null)
            {
                Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() item {packet.ItemNetId} ({packet.PrefabName}) not found for car {packet.CarNetId}");
                return true;
            }

            //An item put together this instant is not ready to be bolted onto anything: the pieces
            //Place goes looking for are only put in place once the item has seen a frame go by.
            //Placing it now throws inside the game's own code and the gadget is lost.
            if (mayDefer)
                return false;
        }

        GadgetItem gadgetItem = netItem.Item.GetComponent<GadgetItem>();

        if (gadgetItem == null)
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() item {packet.ItemNetId} ({netItem.Item.name}) is not a gadget");
            return true;
        }

        if (gadgetItem.Gadget == null)
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() item {packet.ItemNetId} ({netItem.Item.name}) has no gadget to place");
            return true;
        }

        //Link() throws outright on a gadget that is still attached somewhere
        if (gadgetItem.Gadget.IsLinked)
        {
            //Already on the car this packet names: the world is in the requested shape and only the
            //identity and the data need to catch up. Taking it down first would be busywork, and
            //Remove() throws on an item whose reparenting component has gone.
            if (gadgetItem.Gadget.Custom == customization)
            {
                AssignNetworkUid(gadgetItem.Gadget, packet.Uid);
                ApplyState(gadgetItem.Gadget, packet.State);
                Track(gadgetItem.Gadget);
                MarkAnnounced(gadgetItem.Gadget);

                Multiplayer.LogDebug(() => $"NetworkedGadgets.ApplyAttached() {netItem.Item.name} was already on car {packet.CarNetId}, adopted uid {packet.Uid}");

                return true;
            }

            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() item {packet.ItemNetId} ({netItem.Item.name}) is attached elsewhere, taking it down first");

            try
            {
                gadgetItem.Gadget.ForceRemove(false);
            }
            catch (Exception e)
            {
                Multiplayer.LogError($"NetworkedGadgets.ApplyAttached() could not take down item {packet.ItemNetId}: {e.Message}");
                return true;
            }
        }

        //Whoever placed this was holding the item, and the last item update before the attach said so.
        //A remote player's hand is driven every frame from NetworkedPlayer.Update, so the item would
        //keep being dragged along beside the gadget for the rest of the session.
        ReleaseFromRemoteHands(netItem.Item.gameObject);

        //An item another player is carrying is switched off over here, and the game cannot place
        //something that is not present in the scene
        if (!netItem.Item.gameObject.activeSelf)
            netItem.Item.gameObject.SetActive(true);

        //Link() mints a fresh UID from the local counter whenever it finds none, which would both
        //burn a number here and leave the gadget under the wrong id until the state lands
        AssignNetworkUid(gadgetItem.Gadget, packet.Uid);

        Multiplayer.LogDebug(() => $"NetworkedGadgets.ApplyAttached() about to place {packet.PrefabName} on car {packet.CarNetId}: {Describe(customization, netItem, gadgetItem)}");

        GadgetBase gadget = null;

        try
        {
            gadget = GadgetItem.Place(customization, packet.LocalPosition, packet.LocalRotation, gadgetItem);
        }
        catch (Exception e)
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() the game threw while placing {packet.PrefabName} on car {packet.CarNetId}: {e.Message}\r\n{Describe(customization, netItem, gadgetItem)}\r\n{e.StackTrace}");
        }

        if (gadget == null)
        {
            //Place links the gadget onto the car before it finishes the rest of its work, so a throw
            //part way through still leaves it hanging there. Put right what did not happen rather
            //than walk away and leave the gadget nowhere.
            gadget = gadgetItem.Gadget;

            if (gadget == null || !gadget.IsLinked || gadget.Custom != customization)
            {
                Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() placing {packet.PrefabName} on car {packet.CarNetId} failed");
                return true;
            }

            gadget.transform.localPosition = packet.LocalPosition;
            gadget.transform.localRotation = packet.LocalRotation;

            //Putting the gadget on the car is the last thing that happens to the item it came from,
            //so a placement that stopped short leaves the item lying there as a second copy of a
            //part that is now bolted on. Taking it out of the scene is what the rest of the run
            //would have done anyway, and dropping or picking it up switches it back on.
            if (netItem.Item != null && !gadget.transform.IsChildOf(netItem.Item.transform))
                netItem.Item.gameObject.SetActive(false);

            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() picked up the pieces of a half finished placement of {packet.PrefabName} on car {packet.CarNetId}");
        }

        //The state carries the placing instance's UID, which everyone has to agree on for wiring to resolve
        ApplyState(gadget, packet.State);
        Track(gadget);
        MarkAnnounced(gadget);

        Multiplayer.LogDebug(() => $"NetworkedGadgets.ApplyAttached() {packet.PrefabName} on car {packet.CarNetId}, uid: {gadget.UID}");

        return true;
    }

    /// <summary>
    /// What the world looks like around a placement, for working out why the game refused one.
    /// </summary>
    private static string Describe(TrainCarCustomization customization, NetworkedItem netItem, GadgetItem gadgetItem)
    {
        try
        {
            TrainCar car = customization != null ? customization.TrainCar : null;
            Transform interior = car != null ? car.interior?.transform : null;
            GameObject itemGo = netItem != null && netItem.Item != null ? netItem.Item.gameObject : null;
            GameObject gadgetGo = gadgetItem != null && gadgetItem.Gadget != null ? gadgetItem.Gadget.gameObject : null;

            return $"interior: {(interior == null ? "not loaded" : interior.name)}, " +
                   $"item: {(itemGo == null ? "gone" : $"{itemGo.activeSelf}/{itemGo.activeInHierarchy} under {itemGo.transform.parent?.name ?? "nothing"}")}, " +
                   $"gadget: {(gadgetGo == null ? "gone" : $"{gadgetGo.activeSelf}/{gadgetGo.activeInHierarchy} under {gadgetGo.transform.parent?.name ?? "nothing"}")}";
        }
        catch (Exception e)
        {
            return $"could not be described: {e.Message}";
        }
    }

    /// <summary>
    /// Makes any player shown holding this item let go of it, so nothing keeps moving it about once
    /// it has become a gadget on a car.
    /// </summary>
    private static void ReleaseFromRemoteHands(GameObject itemGo)
    {
        if (itemGo == null)
            return;

        foreach (NetworkedPlayer player in UnityEngine.Object.FindObjectsOfType<NetworkedPlayer>())
        {
            if (player == null || player.RightHandItemGO != itemGo)
                continue;

            Multiplayer.LogDebug(() => $"NetworkedGadgets.ReleaseFromRemoteHands() {player.name} was shown holding {itemGo.name}");

            player.DropItem();
        }
    }

    private static void ApplyStatePacket(CommonGadgetPacket packet)
    {
        if (!TryGetGadget(packet.CarNetId, packet.Uid, out GadgetBase gadget))
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyStatePacket() gadget uid {packet.Uid} not found on car {packet.CarNetId}");
            return;
        }

        ApplyState(gadget, packet.State);

        //Agree on what was just applied, or the watcher would read it back as a local change and
        //send it straight out again
        trackedState[gadget] = SerialiseState(gadget);

        Multiplayer.LogDebug(() => $"NetworkedGadgets.ApplyStatePacket() uid: {packet.Uid} on car {packet.CarNetId}");
    }

    private static void ApplyDetached(CommonGadgetPacket packet)
    {
        if (!TryGetGadget(packet.CarNetId, packet.Uid, out GadgetBase gadget))
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyDetached() gadget uid {packet.Uid} not found on car {packet.CarNetId}");
            return;
        }

        ClearAnnounced(gadget);

        gadget.ForceRemove(packet.ReparentToCar);

        Multiplayer.LogDebug(() => $"NetworkedGadgets.ApplyDetached() uid: {packet.Uid} from car {packet.CarNetId}");
    }

    private static void ApplyMountPointState(CommonGadgetPacket packet)
    {
        if (!TryGetGadget(packet.CarNetId, packet.Uid, out GadgetBase gadget))
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyMountPointState() gadget uid {packet.Uid} not found on car {packet.CarNetId}");
            return;
        }

        Drillable drillable = gadget.GetComponent<Drillable>();

        if (drillable == null || packet.MountPointIndex >= drillable.MountPointCount)
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyMountPointState() gadget uid {packet.Uid} has no mount point {packet.MountPointIndex}");
            return;
        }

        MountPoint.States state = (MountPoint.States)packet.MountPointState;

        if (!Enum.IsDefined(typeof(MountPoint.States), state))
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyMountPointState() unknown state {packet.MountPointState} for gadget uid {packet.Uid}");
            return;
        }

        drillable.SetMountPointState(packet.MountPointIndex, state);

        Multiplayer.LogDebug(() => $"NetworkedGadgets.ApplyMountPointState() uid: {packet.Uid}, point: {packet.MountPointIndex}, state: {state}");
    }
}
