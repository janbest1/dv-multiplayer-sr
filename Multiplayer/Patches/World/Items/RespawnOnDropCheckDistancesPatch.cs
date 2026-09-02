using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using System;
using UnityEngine;

namespace Multiplayer.Patches.World.Items;

/// <summary>
/// Decides whether an item has been left behind.
///
/// The game asks how far the item is from "the player", and on every machine that means the one
/// sitting at it. Alone that is the same question; together it is a different question on each
/// side, and both sides answer it for themselves - so a player who walks away banks an item in
/// their own lost and found while it still lies in the world for everyone else, and coming back
/// finds it in the bag rather than where they left it.
///
/// The host answers for everyone now, and asks about the nearest player rather than its own.
///
/// It also asks a second question. Anywhere on this map is two hundred metres from anywhere else,
/// so a single use of the map's teleport had everything anyone had put down taken in four seconds
/// later, on both sides. Something set down on purpose stays where it was set down; the game only
/// takes an item in when it has also ended up well below where it was left, which is what happens
/// when it falls through the world and is the one case nobody can walk back to.
/// </summary>
[HarmonyPatch(typeof(RespawnOnDrop), "CheckDistances")]
public static class RespawnOnDrop_CheckDistances_Patch
{
    //Fallen back on when the setting is missing or nonsense
    private const float DEFAULT_RANGE = 200f;
    private const float MIN_RANGE = 20f;

    //How far below where it was left an item has to end up before it counts as gone for good
    private const float FALL_DEPTH = 20f;

    private static void Postfix(RespawnOnDrop __instance, ref ValueTuple<bool, bool, bool, bool> __result)
    {
        //(farFromSpawn, farFromPlayer, farFromActiveCamera, boundToPlayer). Only the second decides
        //whether the item is taken away; the third only parks its physics.
        if (!NetworkLifecycle.Instance.IsClientRunning && !NetworkLifecycle.Instance.IsServerRunning)
            return;

        //Something a player is carrying is never lost, whoever is asking
        if (__result.Item4)
            return;

        if (!NetworkLifecycle.Instance.IsHost())
        {
            //A client keeps its hands off. Whatever the host decides will reach it as a change of
            //state like any other.
            __result.Item2 = false;
            return;
        }

        __result.Item2 = !AnyPlayerNear(__instance.transform.position, SqrRange(__instance))
                      && HasFallenOutOfReach(__instance);
    }

    /// <summary>
    /// Whether the item is somewhere nobody could walk back to, rather than simply somewhere
    /// nobody is standing right now.
    /// </summary>
    private static bool HasFallenOutOfReach(RespawnOnDrop respawnOnDrop)
    {
        NetworkedItem netItem = respawnOnDrop.GetComponent<NetworkedItem>();

        //Nothing to compare it against - leave it be, the world is a better place for it than
        //somebody's bag it never got announced to.
        if (netItem == null)
            return false;

        return netItem.HasFallenBelowReported(FALL_DEPTH);
    }

    /// <summary>
    /// How close a player has to be for the item to stay where it is.
    ///
    /// Every item carries its own idea of that, and they differ by a lot: two hundred metres for
    /// something of the player's own, a kilometre for anything else. Asking about all of them at
    /// the shorter distance took a crate away five times sooner than the game ever would, so an
    /// item's own answer is the one used, and the setting only ever widens it.
    /// </summary>
    private static float SqrRange(RespawnOnDrop respawnOnDrop)
    {
        float range = DEFAULT_RANGE;

        if (Multiplayer.Settings != null && Multiplayer.Settings.LostItemRange >= MIN_RANGE)
            range = Multiplayer.Settings.LostItemRange;

        return Mathf.Max(respawnOnDrop.maxDistanceSquared, range * range);
    }

    /// <summary>
    /// Whether anyone at all is close enough to the item for it to stay where it is.
    /// </summary>
    private static bool AnyPlayerNear(Vector3 position, float sqrRange)
    {
        //On a listen server the host is a player like any other, but on a dedicated one there is
        //nobody here at all, so both have to be asked
        Transform localPlayer = PlayerManager.PlayerTransform;

        if (localPlayer != null && (localPlayer.position - position).sqrMagnitude <= sqrRange)
            return true;

        if (NetworkLifecycle.Instance.Server == null)
            return false;

        foreach (ServerPlayer player in NetworkLifecycle.Instance.Server.ServerPlayers)
            if ((player.WorldPosition - position).sqrMagnitude <= sqrRange)
                return true;

        return false;
    }
}
