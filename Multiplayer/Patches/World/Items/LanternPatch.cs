using DV.CabControls;
using HarmonyLib;
using Multiplayer.Components.Networking.World;
using Multiplayer.Utils;
using System;

namespace Multiplayer.Patches.World.Items;

/// <summary>
/// The lantern and, through it, the end-of-train lamp. What a lantern is carries in two numbers -
/// the game's own save writes exactly these, under exactly these names, in exactly this order:
///
///     if (this.flame.IsLit) data.SetBool("On", true);
///     if (this.wickSize &lt; 0.99f) data.SetFloat("Wick_state", this.wickSize);
///
/// The wick comes first because a flame cannot be lit on a wick that is still wound down.
/// </summary>
[HarmonyPatch(typeof(Lantern))]
public static class LanternPatch
{
    [HarmonyPatch(nameof(Lantern.Awake))]
    [HarmonyPostfix]
    static void Awake(Lantern __instance)
    {
        NetworkedItem networkedItem = __instance?.gameObject?.GetOrAddComponent<NetworkedItem>();

        if (networkedItem == null)
        {
            Multiplayer.LogError("LanternPatch.Awake() networkedItem returned null!");
            return;
        }

        networkedItem.Initialize(__instance);
    }

    /// <summary>
    /// Runs a frame after Awake, once the knob and the flame are there to be read. The end-of-train
    /// lamp overrides this and calls back into it, so one hook covers both.
    /// </summary>
    [HarmonyPatch(nameof(Lantern.Initialize))]
    [HarmonyPostfix]
    static void Initialize(Lantern __instance)
    {
        NetworkedItem networkedItem = __instance?.gameObject?.GetOrAddComponent<NetworkedItem>();

        if (networkedItem == null)
        {
            Multiplayer.LogError("LanternPatch.Initialize() networkedItem not found!");
            return;
        }

        try
        {
            networkedItem.RegisterTrackedValue(
                "Wick_state",
                () => __instance.wickSize,
                value =>
                {
                    //SetWickLocalPosition is what actually moves wickSize, so reading it back
                    //afterwards gives what was just set and the value settles instead of repeating
                    __instance.SetWickLocalPosition(value);

                    //Unity's null is not C#'s, and ?. asks the wrong one
                    if (__instance.knob != null)
                        __instance.knob.SetValue(__instance.wickSize, ControlImplBase.SetValueSource.Default);

                    __instance.UpdateWickRelatedLogic(__instance.wickSize);
                },
                //Winding a wick sends a value every frame; a hundredth of a turn is not worth a packet
                (current, last) => Math.Abs(current - last) >= 0.01f
                );

            networkedItem.RegisterTrackedValue(
                "On",
                () => __instance.flame != null && __instance.flame.IsLit,
                value =>
                {
                    if (__instance.flame == null)
                        return;

                    //Forced, because the game skips the work when the flame already agrees - and on
                    //an item switched off in someone else's hands it never agrees on its own.
                    //Intensity zero is what puts it out: it raises the extinguish event the lantern
                    //listens to, which calling OnFlameExtinguished by hand would not.
                    __instance.flame.UpdateFlameIntensity(value ? __instance.wickSize : 0f, true);
                }
                );

            networkedItem.FinaliseTrackedValues();
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"LanternPatch.Initialize() {ex.Message}\r\n{ex.StackTrace}");
        }
    }
}
