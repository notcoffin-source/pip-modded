using System.Reflection;
using EFT;
using EFT.Animations;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace PiPDisabler.Patches
{
    internal sealed class FireModeSwitchMovementPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.SetFireMode),
                new[] { typeof(Weapon.EFireMode), typeof(bool) });

        [PatchPrefix]
        private static void Prefix(FirearmsAnimator __instance, ref bool skipAnimation)
        {
            if (!WeaponMotionSuppressionState.ShouldApply(Settings.SuppressFireModeSwitchMovement.Value))
                return;

            skipAnimation = true;
            WeaponMotionSuppressionState.PlayWeaponSound(__instance, "Selector");
        }
    }

    internal static class WeaponMotionSuppressionState
    {
        internal static bool ShouldApply(bool settingEnabled)
            => Settings.ModEnabled.Value &&
               settingEnabled &&
               ScopeLifecycle.IsScoped &&
               ScopeLifecycle.ActiveOptic != null &&
               !ScopeLifecycle.IsModBypassedForCurrentScope;

        internal static void PlayWeaponSound(FirearmsAnimator firearmsAnimator, string soundName)
        {
            if (firearmsAnimator == null)
                return;

            WeaponSoundPlayer soundPlayer = null;
            foreach (IActorEvents eventsConsumer in firearmsAnimator.EventsConsumers)
            {
                soundPlayer = eventsConsumer as WeaponSoundPlayer;
                if (soundPlayer != null)
                    break;
            }

            if (soundPlayer == null)
                return;

            soundPlayer.SoundEventHandler(soundName);
        }
    }

    internal sealed class MagnificationSwitchMovementContextPatch : ModulePatch
    {
        [System.ThreadStatic]
        private static int _scopeModeDepth;

        internal static bool ShouldSuppressModToggle =>
            _scopeModeDepth > 0 &&
            WeaponMotionSuppressionState.ShouldApply(Settings.SuppressMagnificationSwitchMovement.Value);

        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(Player.FirearmController), "SetScopeMode", new[] { typeof(FirearmScopeStateStruct[]) });

        [PatchPrefix]
        private static void Prefix()
        {
            if (!WeaponMotionSuppressionState.ShouldApply(Settings.SuppressMagnificationSwitchMovement.Value))
                return;

            _scopeModeDepth++;
        }

        [PatchFinalizer]
        private static void Finalizer()
        {
            if (_scopeModeDepth > 0)
                _scopeModeDepth--;
        }
    }

    internal sealed class ModToggleTriggerMovementPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.ModToggleTrigger));

        [PatchPrefix]
        private static bool Prefix(FirearmsAnimator __instance)
        {
            if (!MagnificationSwitchMovementContextPatch.ShouldSuppressModToggle)
                return true;

            WeaponMotionSuppressionState.PlayWeaponSound(__instance, "ModSwitch");
            return false;
        }
    }
}
