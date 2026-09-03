using System;
using System.Reflection;
using HarmonyLib;
using EFT.Animations;

namespace PiPDisabler.Patches
{
    internal static class FikaCompat
    {
        private static Harmony _harmony;
        private static bool _patched;
        public static void Enable()
        {
            if (_patched) return;

            try
            {
                // Resolve Fika type by reflection — no compile-time dependency
                var worldToScreenType = AccessTools.TypeByName("Fika.Core.Main.Utils.WorldToScreen");
                if (worldToScreenType == null)
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        "[FikaCompat] Fika.Core.Main.Utils.WorldToScreen not found — Fika not installed, skipping compat patch.");
                    return;
                }

                var targetMethod = AccessTools.Method(worldToScreenType, "IsZoomedOpticAiming",
                    new[] { typeof(ProceduralWeaponAnimation) });

                var prefix = new HarmonyMethod(
                    typeof(FikaCompat).GetMethod(nameof(IsZoomedOpticAimingPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static));

                _harmony = new Harmony("com.fiodor.pipdisabler.fikacompat");
                _harmony.Patch(targetMethod, prefix: prefix);
                _patched = true;

                PiPDisablerPlugin.DebugLogInfo(
                    "[FikaCompat] Patched WorldToScreen.IsZoomedOpticAiming — pings/healthbars will use main camera when PiP is disabled.");
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogError(
                    $"[FikaCompat] Failed to patch Fika compat: {ex.Message}");
            }
        }

        private static bool IsZoomedOpticAimingPrefix(ref bool __result)
        {
            if (!Settings.ModEnabled.Value)
                return true;

            if (!ScopeLifecycle.IsScoped)
                return true;

            if (ScopeLifecycle.IsModBypassedForCurrentScope)
                return true;

            __result = false;
            return false;
        }

        public static void Disable()
        {
            if (!_patched || _harmony == null) return;

            try
            {
                _harmony.UnpatchSelf();
                _patched = false;
                PiPDisablerPlugin.DebugLogInfo("[FikaCompat] Unpatched Fika compat.");
            }
            catch (Exception ex)
            {
                PiPDisablerPlugin.DebugLogError(
                    $"[FikaCompat] Failed to unpatch: {ex.Message}");
            }
        }
    }
}
