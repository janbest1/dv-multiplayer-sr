using DV.Customization;
using HarmonyLib;
using Multiplayer.Components.Networking;

namespace Multiplayer.Patches.World.Gadgets;

[HarmonyPatch(typeof(TrainCarCustomization))]
public static class TrainCarCustomizationPatch
{
    /// <summary>
    /// A hard enough impact shakes loose gadgets off the car, picked with UnityEngine.Random. Left
    /// alone every instance rolls its own dice and drops different gadgets, so only the host decides
    /// and the removal reaches everyone else as a packet.
    /// </summary>
    [HarmonyPatch("OnCollisionEnter")]
    [HarmonyPrefix]
    static bool OnCollisionEnter()
    {
        return NetworkLifecycle.Instance == null || NetworkLifecycle.Instance.IsHost();
    }
}
