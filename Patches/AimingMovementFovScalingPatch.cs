using System.Reflection;
using EFT.Animations;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler.Patches
{
    internal sealed class SpringVectorAccelerationFovScalingPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(Spring), nameof(Spring.AddAcceleration),
                new[] { typeof(Vector3) });

        [PatchPrefix]
        private static void Prefix(ref Vector3 acceleration)
        {
            if (!SwayFovScaling.TryGetScale(out float scale)) return;
            acceleration *= scale;
        }
    }

    internal sealed class SpringComponentAccelerationFovScalingPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(Spring), nameof(Spring.AddAcceleration),
                new[] { typeof(int), typeof(float) });

        [PatchPrefix]
        private static void Prefix(ref float val)
        {
            if (!SwayFovScaling.TryGetScale(out float scale)) return;
            val *= scale;
        }
    }
}
