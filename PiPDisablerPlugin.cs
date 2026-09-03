using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PiPDisabler
{
    [BepInPlugin("com.fiodor.pipdisabler", "PiP-Disabler", "1.5.0-4.1.3port")]
    [BepInDependency("com.fontaine.fovfix", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.Shibatsu.DynamicExternalResolution", BepInDependency.DependencyFlags.SoftDependency)]

    public sealed class PiPDisablerPlugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;
        internal static PiPDisablerPlugin Instance;

        public static void DebugLogInfo(object data)
        {
            if (Settings.DebugLogging.Value)
            {
                LogSource.LogInfo(data);
            }
        }

        public static void DebugLogError(object data)
        {
            if (Settings.DebugLogging.Value)
            {
                LogSource.LogError(data);
            }
        }

        private void Awake()
        {
            Instance = this;
            LogSource = Logger;
            LogSource.LogInfo("PiP-Disabler 1.5.0 loaded.");
            Settings.Init(Config);
            Patches.Patcher.Enable();
            ScopeLifecycle.Init();
            FreelookTracker.Init();
            Settings.ModEnabled.SettingChanged += OnModEnabledChanged;
            Settings.ScopeBlacklistNames.SettingChanged += OnScopeListSettingsChanged;
            Settings.ScopeWhitelistNames.SettingChanged += OnWhitelistSettingsChanged;
        }

              
        private void OnDestroy()
        {
            // Plugin unload or game exit — restore everything
            ScopeLifecycle.ForceExit();
            CameraSettingsManager.ForceRestore();
            LensTransparency.FullRestoreAll();
            MeshSurgeryManager.CleanupForShutdown();
            Patches.VisualRecoilCompensationPatch.Disable();
            PiPDisabler.RestoreAllCameras();

            Settings.ModEnabled.SettingChanged -= OnModEnabledChanged;
            Settings.ScopeBlacklistNames.SettingChanged -= OnScopeListSettingsChanged;
            Settings.ScopeWhitelistNames.SettingChanged -= OnWhitelistSettingsChanged;
        }

        private static void OnModEnabledChanged(object sender, EventArgs e)
        {
            if (!Settings.ModEnabled.Value)
            {
                ScopeLifecycle.ForceExit();
                CameraSettingsManager.ForceRestore();
                LensTransparency.FullRestoreAll();
                PiPDisabler.RestoreAllCameras();
            }
            else
            {
                ScopeLifecycle.SyncState();
            }
        }

        private static void OnWhitelistSettingsChanged(object sender, EventArgs e)
        {
            OnScopeListSettingsChanged(sender, e);
        }

        private static void OnScopeListSettingsChanged(object sender, EventArgs e)
        {
            if (!Settings.ModEnabled.Value) return;
            if (ScopeLifecycle.IsScoped)
            {
                ScopeLifecycle.ForceExit();
                ScopeLifecycle.SyncState();
            }
        }


        private void Update()
        {
            if (Settings.ModToggleKey.Value != KeyCode.None && InputProxy.GetKeyDown(Settings.ModToggleKey.Value))
            {
                Settings.ModEnabled.Value = !Settings.ModEnabled.Value;
                DebugLogInfo($"[Global] Mod {(Settings.ModEnabled.Value ? "ENABLED" : "DISABLED")}");
            }
            if (!Settings.ModEnabled.Value) return;

            if (Settings.ScopeWhitelistToggleEntryKey.Value != KeyCode.None && InputProxy.GetKeyDown(Settings.ScopeWhitelistToggleEntryKey.Value))
            {
                ScopeLifecycle.ToggleActiveScopeWhitelistEntry();
            }

            if (Settings.ScopeBlacklistToggleEntryKey.Value != KeyCode.None && InputProxy.GetKeyDown(Settings.ScopeBlacklistToggleEntryKey.Value))
            {
                ScopeLifecycle.ToggleActiveScopeBlacklistEntry();
            }

            if (Settings.SaveCustomMeshSurgerySettingsKey.Value != KeyCode.None && InputProxy.GetKeyDown(Settings.SaveCustomMeshSurgerySettingsKey.Value))
            {
                string scopeKey = ScopeLifecycle.GetActiveScopeWhitelistKey();
                if (string.IsNullOrWhiteSpace(scopeKey))
                {
                    DebugLogInfo("[CustomMeshSettings] Save ignored: no active scope key");
                }
                else
                {
                    bool saved = PerScopeMeshSurgerySettings.SaveCustomSettingsForScope(scopeKey);
                    DebugLogInfo(saved
                        ? $"[CustomMeshSettings] Saved custom settings for scope key '{scopeKey}'"
                        : "[CustomMeshSettings] Save failed");
                }
            }

            if (Settings.DeleteCustomMeshSurgerySettingsKey.Value != KeyCode.None && InputProxy.GetKeyDown(Settings.DeleteCustomMeshSurgerySettingsKey.Value))
            {
                string scopeKey = ScopeLifecycle.GetActiveScopeWhitelistKey();
                if (string.IsNullOrWhiteSpace(scopeKey))
                {
                    DebugLogInfo("[CustomMeshSettings] Delete ignored: no active scope key");
                }
                else
                {
                    bool removed = PerScopeMeshSurgerySettings.DeleteCustomSettingsForScope(scopeKey);
                    DebugLogInfo(removed
                        ? $"[CustomMeshSettings] Deleted custom settings for scope key '{scopeKey}'"
                        : $"[CustomMeshSettings] No custom settings existed for scope key '{scopeKey}'");
                }
            }

            if (!ScopeLifecycle.ShouldRunUpdateLoop())
                return;

            PiPDisabler.TickBaseOpticCamera();
            ScopeLifecycle.CheckAndUpdate("Update");
            ScopeLifecycle.Tick();
        }
    }
}
