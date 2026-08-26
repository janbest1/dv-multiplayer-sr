using HarmonyLib;
using Multiplayer.Components.Networking.World;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Patches.World.Items;

[HarmonyPatch(typeof(FlashlightItem))]
public static class FlashlightItemPatch
{
    [HarmonyPatch(nameof(FlashlightItem.Start))] 
    static void Postfix(FlashlightItem __instance)
    {
        var networkedItem = __instance.gameObject.GetOrAddComponent<NetworkedItem>();
        networkedItem.Initialize(__instance);

        //Only whether the lamp is on is followed. The beam colour and intensity are read off the
        //prefab in Start(), so they are the same everywhere without being sent, and the battery is
        //deliberately left alone: it is server authoritative, so a client's charge never reaches
        //anyone, and both machines run their own copy down. One player's lamp went flat in the
        //other player's hands while their own still had charge.
        networkedItem.RegisterTrackedValue(
            "buttonState",
            () => (__instance.button.Value > 0f),
            value =>
                {
                    //Our copy of the battery is not kept in step, so it may read flat while the
                    //player carrying the lamp still has charge. Theirs burns, ours would not.
                    if (value && __instance.battery != null && __instance.battery.Depleted)
                    {
                        __instance.battery.currentPower = 100f;
                        __instance.battery.UpdatePower(0f);
                    }

                    if (value)
                        __instance.button.SetValue(1f);
                    else
                        __instance.button.SetValue(0f);

                    __instance.ToggleFlashlight(value);

                    //Switching it on starts the drain. On somebody else's lamp that would run our
                    //own copy down until it went dark in their hands.
                    if (__instance.batteryConsumer != null)
                        __instance.batteryConsumer.TogglePowerConsumption(false);
                }
            );

        networkedItem.FinaliseTrackedValues();
    }
}
