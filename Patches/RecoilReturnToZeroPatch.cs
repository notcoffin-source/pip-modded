using System.Reflection;
using EFT.Animations;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace PiPDisabler.Patches
{
    internal sealed class RecoilReturnToZeroPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
            => AccessTools.Method(typeof(NewRotationRecoilProcess), nameof(NewRotationRecoilProcess.method_3));

        [PatchPostfix]
        private static void Postfix(NewRotationRecoilProcess __instance)
        {
            if (__instance == null ||
                !Settings.ModEnabled.Value ||
                !Settings.ForceRecoilReturnToZero.Value)
            {
                return;
            }

            __instance.AfterRecoilDefaultPosition = Vector2.zero;
        }
    }
}
