using Comfort.Common;
using EFT;
using HarmonyLib;

namespace RaidArmorRepair
{
    /// <summary>You cannot shoot while repairing. Swallowing only the "pressed" edge means
    /// releasing the trigger still goes through, so the weapon cannot be left stuck firing
    /// if a repair starts mid-burst.</summary>
    /// <remarks>
    /// Bots fire through this same method (ShootData -> Player.FirearmController.SetTriggerPressed;
    /// AIFirearmController does not override it), so the block has to be limited to the main
    /// player's own controller. Before 1.4.0 it was not: while the player held the repair key,
    /// every bot on the map had its trigger presses swallowed too.
    /// </remarks>
    [HarmonyPatch(typeof(Player.FirearmController), nameof(Player.FirearmController.SetTriggerPressed))]
    internal static class BlockTriggerWhileRepairingPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Player.FirearmController __instance, bool pressed)
        {
            if (!RepairService.IsRepairing || !pressed)
            {
                return true;
            }

            return !IsMainPlayerController(__instance);
        }

        private static bool IsMainPlayerController(Player.FirearmController controller)
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                return false;
            }

            Player mainPlayer = Singleton<GameWorld>.Instance.MainPlayer;
            return mainPlayer != null && ReferenceEquals(mainPlayer.HandsController, controller);
        }
    }
}
