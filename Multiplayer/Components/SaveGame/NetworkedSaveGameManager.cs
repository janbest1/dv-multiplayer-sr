using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.ThingTypes;
using DV.Utils;
using JetBrains.Annotations;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace Multiplayer.Components.SaveGame;

public class NetworkedSaveGameManager : SingletonBehaviour<NetworkedSaveGameManager>
{
    private const string ROOT_KEY = "Multiplayer";
    private const string PLAYERS_KEY = "Players";
    private const string INVENTORY_KEY = "Inventory";
    private const string LOST_AND_FOUND_KEY = "LostAndFound";

    protected override void Awake()
    {
        base.Awake();
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        Inventory.Instance.MoneyChanged += Server_OnMoneyChanged;
        LicenseManager.Instance.LicenseAcquired += Server_OnLicenseAcquired;
        LicenseManager.Instance.JobLicenseAcquired += Server_OnJobLicenseAcquired;
        LicenseManager.Instance.GarageUnlocked += Server_OnGarageUnlocked;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (UnloadWatcher.isUnloading)
            return;
        if (!NetworkLifecycle.Instance.IsHost())
            return;
        Inventory.Instance.MoneyChanged -= Server_OnMoneyChanged;
        LicenseManager.Instance.LicenseAcquired -= Server_OnLicenseAcquired;
        LicenseManager.Instance.JobLicenseAcquired -= Server_OnJobLicenseAcquired;
        LicenseManager.Instance.GarageUnlocked -= Server_OnGarageUnlocked;
    }

    #region Server

    private static void Server_OnMoneyChanged(double oldAmount, double newAmount)
    {
        NetworkLifecycle.Instance.Server.SendMoney((float)newAmount);
    }

    private static void Server_OnLicenseAcquired(GeneralLicenseType_v2 license)
    {
        NetworkLifecycle.Instance.Server.SendLicense(license.id, false);
    }

    private static void Server_OnJobLicenseAcquired(JobLicenseType_v2 license)
    {
        NetworkLifecycle.Instance.Server.SendLicense(license.id, true);
    }

    private static void Server_OnGarageUnlocked(GarageType_v2 garage)
    {
        NetworkLifecycle.Instance.Server.SendGarage(garage.id);
    }

    public void Server_UpdateInternalData(SaveGameData data)
    {
        JObject root = data.GetJObject(ROOT_KEY) ?? [];
        JObject players = root.GetJObject(PLAYERS_KEY) ?? [];

        foreach (ServerPlayer player in NetworkLifecycle.Instance.Server.ServerPlayers)
        {
            if (player.Peer == NetworkLifecycle.Instance.Server.SelfPeer || player.LoadingState != PlayerLoadingState.Complete)
                continue;

            JObject playerData = [];
            playerData.SetVector3(SaveGameKeys.Player_position, player.AbsoluteWorldPosition);
            playerData.SetFloat(SaveGameKeys.Player_rotation, player.WorldRotationY);

            //A client's game is never written to disk - it would put this world into their own
            //career - so what they are carrying and what is waiting in their keeping is kept here,
            //against their name, and handed back when they next arrive.
            WriteItems(playerData, player);

            players.SetJObject(player.Guid.ToString(), playerData);
        }

        root.SetJObject(PLAYERS_KEY, players);
        data.SetJObject(ROOT_KEY, root);
    }

    /// <summary>
    /// Puts one player's belongings into the save data straight away, without waiting for the next
    /// pass over everybody. Somebody leaving says what they have on their way out, and by the time
    /// it arrives the host may already have written down everything else about them.
    /// </summary>
    public void Server_RememberPlayerItems(ServerPlayer player)
    {
        SaveGameData data = SaveGameManager.Instance?.data;

        if (data == null || player == null)
            return;

        JObject root = data.GetJObject(ROOT_KEY) ?? [];
        JObject players = root.GetJObject(PLAYERS_KEY) ?? [];
        JObject playerData = players.GetJObject(player.Guid.ToString()) ?? [];

        WriteItems(playerData, player);

        players.SetJObject(player.Guid.ToString(), playerData);
        root.SetJObject(PLAYERS_KEY, players);
        data.SetJObject(ROOT_KEY, root);
    }

    /// <summary>
    /// What the player themselves said, if they have said anything - only they know which belt slot
    /// a thing sits in or what is inside what. Failing that, what the host can see for itself: it
    /// has a copy of all their things and knows which are in a hand, a bag or a box.
    /// </summary>
    private static void WriteItems(JObject playerData, ServerPlayer player)
    {
        if (player.ReportedInventory != null || player.ReportedLostAndFound != null)
        {
            playerData[INVENTORY_KEY] = JArray.FromObject(PlayerStorage.ToStorage(player.ReportedInventory));
            playerData[LOST_AND_FOUND_KEY] = JArray.FromObject(PlayerStorage.ToStorage(player.ReportedLostAndFound));
            return;
        }

        PlayerStorage.CollectForPlayer(player, out List<StorageItemData> inventory, out List<StorageItemData> lostAndFound);

        playerData[INVENTORY_KEY] = JArray.FromObject(inventory);
        playerData[LOST_AND_FOUND_KEY] = JArray.FromObject(lostAndFound);
    }

    public JObject Server_GetPlayerData(SaveGameData data, Guid guid)
    {
        return data?.GetJObject(ROOT_KEY)?.GetJObject(PLAYERS_KEY)?.GetJObject(guid.ToString());
    }

    /// <summary>
    /// What we wrote down for this player last time they were here. Nothing at all for somebody
    /// arriving for the first time, who gets what the game gives anybody starting out.
    /// </summary>
    public static List<StorageItemData> Server_GetPlayerItems(JObject playerData, bool lostAndFound)
    {
        JToken items = playerData?[lostAndFound ? LOST_AND_FOUND_KEY : INVENTORY_KEY];

        if (items == null)
            return new List<StorageItemData>();

        try
        {
            return items.ToObject<List<StorageItemData>>() ?? new List<StorageItemData>();
        }
        catch (Exception ex)
        {
            Multiplayer.LogWarning($"NetworkedSaveGameManager.Server_GetPlayerItems() {ex.Message}");
            return new List<StorageItemData>();
        }
    }

    #endregion

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        return $"[{nameof(NetworkedSaveGameManager)}]";
    }
}
