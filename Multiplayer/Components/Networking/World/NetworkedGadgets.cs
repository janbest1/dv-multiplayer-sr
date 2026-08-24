using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data.Gadgets;
using Multiplayer.Networking.Packets.Common;
using Multiplayer.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

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

    /// <summary>
    /// Mints a UID no other machine in this session can produce, or 0 when there is no session to
    /// collide with and the game's own numbering can stand.
    /// </summary>
    public static int NextLocalUid()
    {
        byte playerId = NetworkLifecycle.Instance?.Client?.PlayerId ?? 0;

        if (playerId == 0)
            return 0;

        localUidCounter = (localUidCounter + 1) & UID_COUNTER_MASK;

        if (localUidCounter == 0)
            localUidCounter = 1;

        return (playerId << UID_PLAYER_SHIFT) | localUidCounter;
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

        IsApplyingRemoteChange = true;

        try
        {
            switch (packet.Action)
            {
                case GadgetAction.Attached:
                    ApplyAttached(packet);
                    break;

                case GadgetAction.Detached:
                    ApplyDetached(packet);
                    break;

                case GadgetAction.MountPointState:
                    ApplyMountPointState(packet);
                    break;
            }
        }
        catch (Exception e)
        {
            Multiplayer.LogError($"NetworkedGadgets.Apply({packet.Action}) car: {packet.CarNetId}, uid: {packet.Uid}: {e.Message}\r\n{e.StackTrace}");
        }
        finally
        {
            IsApplyingRemoteChange = false;
        }
    }

    private static void ApplyAttached(CommonGadgetPacket packet)
    {
        if (!TryGetCustomization(packet.CarNetId, out TrainCarCustomization customization))
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() no customization for car {packet.CarNetId}");
            return;
        }

        if (!NetworkedItem.TryGet(packet.ItemNetId, out NetworkedItem netItem) || netItem.Item == null)
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() item {packet.ItemNetId} not found for car {packet.CarNetId}");
            return;
        }

        GadgetItem gadgetItem = netItem.Item.GetComponent<GadgetItem>();

        if (gadgetItem == null)
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() item {packet.ItemNetId} ({netItem.Item.name}) is not a gadget");
            return;
        }

        if (gadgetItem.Gadget == null)
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() item {packet.ItemNetId} ({netItem.Item.name}) has no gadget to place");
            return;
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

                Multiplayer.LogDebug(() => $"NetworkedGadgets.ApplyAttached() {netItem.Item.name} was already on car {packet.CarNetId}, adopted uid {packet.Uid}");

                return;
            }

            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() item {packet.ItemNetId} ({netItem.Item.name}) is attached elsewhere, taking it down first");

            try
            {
                gadgetItem.Gadget.ForceRemove(false);
            }
            catch (Exception e)
            {
                Multiplayer.LogError($"NetworkedGadgets.ApplyAttached() could not take down item {packet.ItemNetId}: {e.Message}");
                return;
            }
        }

        //Link() mints a fresh UID from the local counter whenever it finds none, which would both
        //burn a number here and leave the gadget under the wrong id until the state lands
        AssignNetworkUid(gadgetItem.Gadget, packet.Uid);

        GadgetBase gadget = GadgetItem.Place(customization, packet.LocalPosition, packet.LocalRotation, gadgetItem);

        if (gadget == null)
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyAttached() placing {netItem.Item.name} on car {packet.CarNetId} failed");
            return;
        }

        //The state carries the placing instance's UID, which everyone has to agree on for wiring to resolve
        ApplyState(gadget, packet.State);

        Multiplayer.LogDebug(() => $"NetworkedGadgets.ApplyAttached() {netItem.Item.name} on car {packet.CarNetId}, uid: {gadget.UID}");
    }

    private static void ApplyDetached(CommonGadgetPacket packet)
    {
        if (!TryGetGadget(packet.CarNetId, packet.Uid, out GadgetBase gadget))
        {
            Multiplayer.LogWarning($"NetworkedGadgets.ApplyDetached() gadget uid {packet.Uid} not found on car {packet.CarNetId}");
            return;
        }

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
