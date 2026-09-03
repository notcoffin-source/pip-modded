using System.IO;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace PiPDisabler
{
    /// <summary>
    /// Renders scope vignette, shadow, and outside-scope blur after scene
    /// rendering on the main FPS camera.
    ///
    /// Effects render into SSAA's active scene target when render scaling is
    /// enabled, matching Tarkov's native post-process command buffers.
    ///
    /// ── VIGNETTE ───────────────────────────────────────────────────────────────
    /// Screen-space quad centred in view with fixed on-screen size.
    /// Circular feather: transparent centre → smooth darkening at aperture,
    /// then softly fades back out so the overlay never reads as a rectangle.
    ///
    /// ── SHADOW ─────────────────────────────────────────────────────────────────
    /// Screen-filling black quad, clipped by stencil so it draws everywhere except
    /// the visible lens mask.
    ///
    /// Textures are generated at runtime and only rebuilt when config changes.
    /// </summary>
    internal static class ScopeEffectsRenderer
    {
        // ── Vignette ────────────────────────────────────────────────────────
        private static Mesh       _vigMesh;
        private static Material   _vigMat;
        private static Texture2D  _vigTex;
        private static float      _lastVigSoftness  = -1f;
        private static float      _lastVigOpacity   = -1f;
        private static float      _lastVigRadius    = -1f;
        private static Matrix4x4  _vigMatrix = Matrix4x4.identity;
        private static bool       _vigHasLensBounds;
        private static bool       _vigActive;

        // ── Shadow ──────────────────────────────────────────────────────────
        private static Mesh       _shadowMesh;
        private static Material   _shadowMat;
        private static Texture2D  _shadowTex;
        private static float      _lastShadowOpacity  = -1f;
        private static Matrix4x4  _shadowMatrix = Matrix4x4.identity;
        private static bool       _shadowActive;
        private static bool       _effectsVisible;
        private static bool       _persistShadowUntilFovRestore;

        // ── Outside-scope dual Kawase blur ────────────────────────────────
        private static Material   _outsideBlurMat;
        private static AssetBundle _effectsShaderBundle;
        private static bool       _outsideBlurActive;
        private static bool       _outsideBlurShaderMissingLogged;
        private const string EffectsShaderBundleName = "pipdisabler_effect_shaders.bundle";
        private const string OutsideBlurShaderName = "Hidden/PiPDisabler/OutsideScopeKawaseBlur";
        private static readonly int BlurTexelSizeId = Shader.PropertyToID("_TexelSize");
        private static readonly int BlurOffsetId = Shader.PropertyToID("_Offset");
        private static readonly int BlurOpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int BlurDarkeningId = Shader.PropertyToID("_Darkening");
        private static readonly int BlurFlipYId = Shader.PropertyToID("_FlipY");
        private static readonly int BlurTextureId = Shader.PropertyToID("_BlurTex");
        private static readonly int BlurRadialGateEnabledId = Shader.PropertyToID("_RadialGateEnabled");
        private static readonly int BlurLensCenterId = Shader.PropertyToID("_LensCenter");
        private static readonly int BlurLensSizeId = Shader.PropertyToID("_LensSize");
        private static readonly int BlurViewportAspectId = Shader.PropertyToID("_ViewportAspect");
        private static readonly int BlurRadialGateStartId = Shader.PropertyToID("_RadialGateStart");
        private static readonly int BlurRadialGateSoftnessId = Shader.PropertyToID("_RadialGateSoftness");
        private static readonly int NightVisionOnId = Shader.PropertyToID("_NightVisionOn");
        private static readonly int[] KawaseChainIds =
        {
            Shader.PropertyToID("_PiPDisablerOutsideBlur0"),
            Shader.PropertyToID("_PiPDisablerOutsideBlur1"),
            Shader.PropertyToID("_PiPDisablerOutsideBlur2"),
            Shader.PropertyToID("_PiPDisablerOutsideBlur3"),
            Shader.PropertyToID("_PiPDisablerOutsideBlur4"),
            Shader.PropertyToID("_PiPDisablerOutsideBlur5"),
            Shader.PropertyToID("_PiPDisablerOutsideBlur6")
        };
        private static readonly int[] KawaseWidths = new int[KawaseChainIds.Length];
        private static readonly int[] KawaseHeights = new int[KawaseChainIds.Length];

        // ── Shared stencil debug ───────────────────────────────────────────
        private static Material   _stencilDebugMat;
        private static bool       _hasStencilSupport;

        // ── CommandBuffer ───────────────────────────────────────────────────
        private static CommandBuffer _cmdBuffer;
        private static Camera        _attachedCamera;
        private static CameraEvent   _attachedEvent = EffectsCameraEvent;
        private const CameraEvent EffectsCameraEvent = CameraEvent.AfterForwardAlpha;
        private static bool          _preCullRegistered;

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        public static void Show()
        {
            EnsureStencilDebugMaterial();

            if (Settings.VignetteEnabled.Value)
            {
                EnsureVignetteMeshAndMat();
                RefreshVignetteTexture();
                _vigActive = true;
            }
            else
            {
                _vigActive = false;
            }

            if (Settings.ScopeShadowEnabled.Value)
            {
                EnsureShadowMeshAndMat();
                RefreshShadowTexture();
                _shadowActive = true;
            }
            else
            {
                _shadowActive = false;
            }

            if (Settings.OutsideScopeBlurEnabled.Value || Settings.NvgLensFocalBlurEnabled.Value)
            {
                EnsureOutsideBlurMaterial();
                _outsideBlurActive = _outsideBlurMat != null;
            }
            else
            {
                _outsideBlurActive = false;
            }

            _effectsVisible = true;
            _persistShadowUntilFovRestore = false;

            // Force re-attach to guarantee this CommandBuffer is ordered AFTER
            // ReticleRenderer's CB. If we just call AttachToCamera() it returns
            // early when already attached to the same camera, leaving the CB at
            // its stale position — which ends up before ReticleRenderer's CB
            // whenever Reticle detaches/re-attaches (e.g. after persist-on-unscope
            // + optic mode switch, or after a magnification mode switch while
            // scoped). The shadow reads stencil written by ReticleRenderer, so
            // it must execute AFTER that pass.
            ReattachToSceneEvent();

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeEffects] Showing: vignette={_vigActive} shadow={_shadowActive} blur={_outsideBlurActive} (CommandBuffer)");
        }

        /// <summary>Per-frame update — call from ScopeLifecycle.Tick().</summary>
        public static void UpdateTransform()
        {
            EnsureCorrectCameraEvent();

            // Refresh textures if config changed
            if (_vigActive)  RefreshVignetteTexture();
            if (_shadowActive) RefreshShadowTexture();

            // Re-attach if camera changed
            var mainCam = Helpers.GetMainCamera();
            if (mainCam != null && mainCam != _attachedCamera)
                AttachToCamera();
        }

        public static void RefreshSettings()
        {
            if (!_effectsVisible)
                return;

            EnsureStencilDebugMaterial();

            if (Settings.VignetteEnabled.Value)
            {
                EnsureVignetteMeshAndMat();
                RefreshVignetteTexture();
                _vigActive = true;
            }
            else
            {
                _vigActive = false;
            }

            if (Settings.ScopeShadowEnabled.Value)
            {
                EnsureShadowMeshAndMat();
                RefreshShadowTexture();
                _shadowActive = true;
            }
            else
            {
                _shadowActive = false;
            }

            if (Settings.OutsideScopeBlurEnabled.Value || Settings.NvgLensFocalBlurEnabled.Value)
            {
                EnsureOutsideBlurMaterial();
                _outsideBlurActive = _outsideBlurMat != null;
            }
            else
            {
                _outsideBlurActive = false;
            }

            AttachToCamera();
        }

        public static void Hide()
        {
            _effectsVisible = false;
            _persistShadowUntilFovRestore = false;

            // Keep allocated resources and camera hook alive so returning to ADS
            // does not force expensive re-attachment/rebuild work in the same frame.
            _cmdBuffer?.Clear();
        }

        public static bool OnScopeExit(bool allowShadowPersist)
        {
            _vigActive = false;
            _outsideBlurActive = false;

            bool keepShadow =
                allowShadowPersist &&
                Settings.ModEnabled.Value &&
                Settings.ScopeShadowEnabled.Value &&
                Settings.ScopeShadowPersistOnUnscope.Value &&
                _shadowActive;

            if (keepShadow)
            {
                _effectsVisible = true;
                _persistShadowUntilFovRestore = true;
                AttachToCamera();
                return true;
            }

            Cleanup();
            return false;
        }

        public static void Cleanup()
        {
            _effectsVisible = false;
            _vigActive = false;
            _shadowActive = false;
            _outsideBlurActive = false;
            _persistShadowUntilFovRestore = false;
            DetachFromCamera();
        }

        // ─────────────────────────────────────────────────────────────────────
        // CommandBuffer management
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Remove and re-add the CommandBuffer so it sits AFTER ReticleRenderer's CB
        /// in the camera's execution list.  Must be called after ReticleRenderer.Show()
        /// has (re-)attached its own CB.
        /// </summary>
        private static void ReattachToSceneEvent()
        {
            var mainCam = Helpers.GetMainCamera();
            if (mainCam == null) return;

            ReorderPreCullForSceneEvent();

            if (_attachedCamera != null && _attachedCamera != mainCam)
                DetachFromCamera();

            CameraEvent targetEvent = EffectsCameraEvent;

            if (_attachedCamera == mainCam && _cmdBuffer != null)
            {
                // Already on the correct camera — remove and re-add to push this CB
                // to the end of the list (i.e. after ReticleRenderer's CB).
                try { mainCam.RemoveCommandBuffer(_attachedEvent, _cmdBuffer); }
                catch (System.Exception) { }
                mainCam.AddCommandBuffer(targetEvent, _cmdBuffer);
                _attachedEvent = targetEvent;

                PiPDisablerPlugin.DebugLogInfo(
                    $"[ScopeEffects] CommandBuffer reordered on '{mainCam.name}' at {_attachedEvent}");
                return;
            }

            // First-time attachment (same path as before).
            if (_cmdBuffer == null)
                _cmdBuffer = new CommandBuffer { name = "ScopeEffectsOverlay" };

            mainCam.AddCommandBuffer(targetEvent, _cmdBuffer);
            _attachedCamera = mainCam;
            _attachedEvent = targetEvent;

            if (!_preCullRegistered)
            {
                Camera.onPreCull += OnPreCullCallback;
                _preCullRegistered = true;
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeEffects] CommandBuffer attached to '{mainCam.name}' at {_attachedEvent}");
        }

        private static void ReorderPreCullForSceneEvent()
        {
            if (_preCullRegistered)
            {
                Camera.onPreCull -= OnPreCullCallback;
                _preCullRegistered = false;
            }

            Camera.onPreCull += OnPreCullCallback;
            _preCullRegistered = true;
        }

        private static void AttachToCamera()
        {
            var mainCam = Helpers.GetMainCamera();
            if (mainCam == null) return;

            if (_attachedCamera != null && _attachedCamera != mainCam)
                DetachFromCamera();

            if (_attachedCamera == mainCam) return;

            if (_cmdBuffer == null)
                _cmdBuffer = new CommandBuffer { name = "ScopeEffectsOverlay" };

            CameraEvent targetEvent = EffectsCameraEvent;
            mainCam.AddCommandBuffer(targetEvent, _cmdBuffer);
            _attachedCamera = mainCam;
            _attachedEvent = targetEvent;

            if (!_preCullRegistered)
            {
                Camera.onPreCull += OnPreCullCallback;
                _preCullRegistered = true;
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeEffects] CommandBuffer attached to '{mainCam.name}' at {_attachedEvent}");
        }

        private static void DetachFromCamera()
        {
            if (_preCullRegistered)
            {
                Camera.onPreCull -= OnPreCullCallback;
                _preCullRegistered = false;
            }

            if (_attachedCamera != null && _cmdBuffer != null)
            {
                try { _attachedCamera.RemoveCommandBuffer(_attachedEvent, _cmdBuffer); }
                catch (System.Exception) { }
            }

            if (_cmdBuffer != null)
            {
                _cmdBuffer.Clear();
                _cmdBuffer.Release();
                _cmdBuffer = null;
            }

            _attachedCamera = null;
        }

        private static void EnsureCorrectCameraEvent()
        {
            if (_attachedCamera == null || _cmdBuffer == null) return;

            CameraEvent desiredEvent = EffectsCameraEvent;
            if (desiredEvent == _attachedEvent) return;

            try { _attachedCamera.RemoveCommandBuffer(_attachedEvent, _cmdBuffer); }
            catch (System.Exception) { }

            _attachedCamera.AddCommandBuffer(desiredEvent, _cmdBuffer);
            _attachedEvent = desiredEvent;
        }

        // ─────────────────────────────────────────────────────────────────────
        // onPreCull — rebuild CommandBuffer with final-pose data
        // ─────────────────────────────────────────────────────────────────────

        private static void OnPreCullCallback(Camera cam)
        {
            if (cam != _attachedCamera) return;
            if (_cmdBuffer == null) return;

            if (!_effectsVisible)
            {
                _cmdBuffer.Clear();
                return;
            }

            if (_persistShadowUntilFovRestore && !ShouldKeepPersistedShadowVisible())
            {
                Cleanup();
                ReticleRenderer.StopStencilOnlyPersistence();
                return;
            }

            if (!_vigActive && !_shadowActive && !_outsideBlurActive)
            {
                _cmdBuffer.Clear();
                return;
            }

            // Rebuild matrices in pure screen-space
            if (_vigActive)
                RebuildVignetteMatrix(cam);
            if (_shadowActive || _outsideBlurActive)
                RebuildShadowMatrix(cam);

            RebuildCommandBuffer(cam);
        }

        private static void RebuildVignetteMatrix(Camera cam)
        {
            _vigHasLensBounds = false;
            if (cam == null) return;

            if (!ReticleRenderer.TryGetLensMaskClipBounds(cam, out Vector2 center, out Vector2 size))
                return;

            _vigMatrix = Matrix4x4.TRS(
                new Vector3(center.x, center.y, 0.6f),
                Quaternion.identity,
                new Vector3(size.x, size.y, 1f));
            _vigHasLensBounds = true;
        }

        private static void RebuildShadowMatrix(Camera cam)
        {
            if (cam == null) return;

            _shadowMatrix = Matrix4x4.TRS(
                new Vector3(0f, 0f, 0.7f),
                Quaternion.identity,
                new Vector3(2f, 2f, 1f));
        }

        private static void RebuildCommandBuffer(Camera cam)
        {
            _cmdBuffer.Clear();
            Rect viewport = GetEffectsViewport(cam);
            RenderTargetIdentifier effectsTarget = GetEffectsTarget(cam);
            _cmdBuffer.SetRenderTarget(effectsTarget);
            SetEffectsViewport(viewport);

            Mesh stencilMesh = GetStencilMesh();
            bool useStencil = _hasStencilSupport &&
                              ReticleRenderer.AppendLensStencilMask(_cmdBuffer, stencilMesh, cam);

            // Pure screen-space draw (clip-space matrices).
            _cmdBuffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);

            if (useStencil)
            {
                var fullScreenMatrix = Matrix4x4.TRS(
                    Vector3.zero, Quaternion.identity, new Vector3(2f, 2f, 1f));

                if (Settings.DebugShowScopeShadowMask.Value && _stencilDebugMat != null)
                    _cmdBuffer.DrawMesh(stencilMesh, fullScreenMatrix, _stencilDebugMat, 0, -1);
            }

            if (useStencil && IsNvgLensFocalBlurActive)
                ReticleRenderer.AppendReticleForNvgLensBlur(_cmdBuffer, viewport);

            if (_outsideBlurActive && useStencil && _outsideBlurMat != null)
                AppendOutsideScopeBlur(viewport, stencilMesh, cam, effectsTarget);

            _cmdBuffer.SetRenderTarget(effectsTarget);
            SetEffectsViewport(viewport);
            _cmdBuffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);

            // Draw shadow first (behind vignette in render order)
            if (_shadowActive && useStencil && _shadowMat != null && _shadowMesh != null)
                _cmdBuffer.DrawMesh(_shadowMesh, _shadowMatrix, _shadowMat, 0, -1);

            // Draw vignette only in lens mask area
            if (_vigActive && _vigHasLensBounds && useStencil && _vigMat != null && _vigMesh != null)
                _cmdBuffer.DrawMesh(_vigMesh, _vigMatrix, _vigMat, 0, -1);

            // Restore original matrices
            _cmdBuffer.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Vignette texture + mesh/material
        // ─────────────────────────────────────────────────────────────────────

        private static void EnsureVignetteMeshAndMat()
        {
            if (_vigMesh == null)
                _vigMesh = BuildQuadMesh("VignetteQuad");

            if (_vigMat == null)
            {
                Shader shader = _hasStencilSupport
                    ? Shader.Find("UI/Default")
                    : FindAlphaShader();

                _vigMat = new Material(shader)
                {
                    color       = Color.white,
                    renderQueue = 3099
                };
                _vigMat.SetInt("_ZTest", (int)CompareFunction.Always);
                _vigMat.SetInt("_ZWrite", 0);
                if (_hasStencilSupport)
                {
                    _vigMat.SetFloat("_Stencil", 1f);
                    _vigMat.SetFloat("_StencilComp", (float)CompareFunction.Equal);
                    _vigMat.SetFloat("_StencilOp", (float)StencilOp.Keep);
                    _vigMat.SetFloat("_StencilReadMask", 255f);
                    _vigMat.SetFloat("_StencilWriteMask", 0f);
                }
            }
        }

        /// <summary>
        /// Circular dual-feather with aspect-ratio correction: transparent centre →
        /// darkened aperture edge → soft fade-out before texture borders.
        /// Now screen-filling (like shadow), so X distances are stretched by
        /// the aspect ratio to keep the circle round.
        /// </summary>
        private static void RefreshVignetteTexture()
        {
            float soft = PerScopeMeshSurgerySettings.GetVignetteSoftness();
            float opac = PerScopeMeshSurgerySettings.GetVignetteOpacity();
            float radius = PerScopeMeshSurgerySettings.GetVignetteRadius();

            if (Mathf.Abs(soft - _lastVigSoftness) < 0.001f &&
                Mathf.Abs(opac - _lastVigOpacity)  < 0.005f &&
                Mathf.Abs(radius - _lastVigRadius) < 0.001f) return;
            _lastVigSoftness = soft;
            _lastVigOpacity  = opac;
            _lastVigRadius   = radius;

            const int S = 256;
            if (_vigTex == null)
            {
                _vigTex            = new Texture2D(S, S, TextureFormat.RGBA32, false);
                _vigTex.name       = "ScopeLensVignetteTex";
                _vigTex.wrapMode   = TextureWrapMode.Clamp;
                _vigTex.filterMode = FilterMode.Bilinear;
            }

            // mult=1 → edge near a circle inscribed in height.
            // mult<1 → edge moves inward (more visible darkening).
            // mult>1 → edge moves outward (less visible darkening).
            float innerR = Mathf.Clamp01(radius);
            float outerR = Mathf.Lerp(innerR, 1f, Mathf.Clamp01(soft));


            var pixels = new Color32[S * S];
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                // Aspect-corrected distance from center (normalized -1..1)
                float nx   = ((float)x / S - 0.5f) * 2f;
                float ny   = ((float)y / S - 0.5f) * 2f;
                float dist = Mathf.Sqrt(nx * nx + ny * ny);

                float t = Mathf.Clamp01((dist - innerR) / Mathf.Max(0.001f, outerR - innerR));
                float feather = Mathf.SmoothStep(0f, 1f, t);

                byte a = (byte)(feather * opac * 255f);
                pixels[y * S + x] = new Color32(0, 0, 0, a);
            }

            _vigTex.SetPixels32(pixels);
            _vigTex.Apply();
            if (_vigMat != null) _vigMat.mainTexture = _vigTex;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Shadow texture + mesh/material
        // ─────────────────────────────────────────────────────────────────────

        private static void EnsureShadowMeshAndMat()
        {
            if (_shadowMesh == null)
                _shadowMesh = BuildQuadMesh("ShadowQuad");

            if (_shadowMat == null)
            {
                Shader shader = _hasStencilSupport
                    ? Shader.Find("UI/Default")
                    : FindAlphaShader();

                _shadowMat = new Material(shader)
                {
                    color       = Color.white,
                    renderQueue = 3050
                };
                _shadowMat.SetInt("_ZTest", (int)CompareFunction.Always);
                _shadowMat.SetInt("_ZWrite", 0);
                if (_hasStencilSupport)
                {
                    _shadowMat.SetFloat("_Stencil", 1f);
                    _shadowMat.SetFloat("_StencilComp", (float)CompareFunction.NotEqual);
                    _shadowMat.SetFloat("_StencilOp", (float)StencilOp.Keep);
                    _shadowMat.SetFloat("_StencilReadMask", 255f);
                    _shadowMat.SetFloat("_StencilWriteMask", 0f);
                }
            }
        }

        private static void RefreshShadowTexture()
        {
            float opac = Settings.ScopeShadowOpacity.Value;

            if (_shadowTex == null)
            {
                _shadowTex            = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _shadowTex.name       = "ScopeShadowTex";
                _shadowTex.wrapMode   = TextureWrapMode.Clamp;
                _shadowTex.filterMode = FilterMode.Point;
            }

            if (Mathf.Abs(opac - _lastShadowOpacity) < 0.005f) return;
            _lastShadowOpacity = opac;

            byte a = (byte)(Mathf.Clamp01(opac) * 255f);
            _shadowTex.SetPixel(0, 0, new Color32(0, 0, 0, a));
            _shadowTex.Apply();
            if (_shadowMat != null) _shadowMat.mainTexture = _shadowTex;

            PiPDisablerPlugin.DebugLogInfo(
                $"[ScopeEffects] Shadow texture rebuilt: opacity={opac:F2}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Outside-scope Kawase blur
        // ─────────────────────────────────────────────────────────────────────

        private static void EnsureOutsideBlurMaterial()
        {
            if (_outsideBlurMat != null) return;

            Shader shader = FindOutsideBlurShader();
            if (shader == null)
            {
                if (!_outsideBlurShaderMissingLogged)
                {
                    _outsideBlurShaderMissingLogged = true;
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[ScopeEffects] Missing shader '{OutsideBlurShaderName}'. Outside scope blur disabled.");
                }
                return;
            }

            _outsideBlurMat = new Material(shader)
            {
                renderQueue = 3030
            };
        }

        private static Shader FindOutsideBlurShader()
        {
            Shader shader = Shader.Find(OutsideBlurShaderName);
            if (shader != null)
                return shader;

            if (_effectsShaderBundle == null)
            {
                string pluginDir = Path.GetDirectoryName(typeof(PiPDisablerPlugin).Assembly.Location);
                string bundlePath = Path.Combine(pluginDir ?? string.Empty, EffectsShaderBundleName);
                if (File.Exists(bundlePath))
                    _effectsShaderBundle = AssetBundle.LoadFromFile(bundlePath);
            }

            if (_effectsShaderBundle == null)
                return null;

            foreach (Shader bundledShader in _effectsShaderBundle.LoadAllAssets<Shader>())
            {
                if (bundledShader != null && bundledShader.name == OutsideBlurShaderName)
                    return bundledShader;
            }

            return null;
        }

        private static void AppendOutsideScopeBlur(
            Rect viewport,
            Mesh fullScreenMesh,
            Camera cam,
            RenderTargetIdentifier effectsTarget)
        {
            if (fullScreenMesh == null)
                return;

            int downsample = Mathf.Clamp(Settings.OutsideScopeBlurDownsample.Value, 1, 4);
            int iterations = Mathf.Clamp(Settings.OutsideScopeBlurIterations.Value, 1, 6);
            float radius = Mathf.Clamp(Settings.OutsideScopeBlurRadius.Value, 0.25f, 6f);
            float opacity = Mathf.Clamp01(Settings.OutsideScopeBlurOpacity.Value);
            float darkening = Mathf.Clamp01(Settings.OutsideScopeBlurDarkening.Value);
            if (opacity <= 0f)
                return;

            bool nvgLensFocalBlur = ShouldApplyNvgLensFocalBlur();
            bool standardOutsideBlur = Settings.OutsideScopeBlurEnabled.Value;
            if (!standardOutsideBlur && !nvgLensFocalBlur)
                return;

            int levels = Mathf.Min(iterations, KawaseChainIds.Length - 1);

            KawaseWidths[0] = Mathf.Max(1, Mathf.RoundToInt(viewport.width / downsample));
            KawaseHeights[0] = Mathf.Max(1, Mathf.RoundToInt(viewport.height / downsample));

            _cmdBuffer.GetTemporaryRT(
                KawaseChainIds[0], KawaseWidths[0], KawaseHeights[0],
                0, FilterMode.Bilinear, RenderTextureFormat.Default);
            _cmdBuffer.Blit(effectsTarget, KawaseChainIds[0]);

            for (int i = 1; i <= levels; i++)
            {
                KawaseWidths[i] = Mathf.Max(1, KawaseWidths[i - 1] / 2);
                KawaseHeights[i] = Mathf.Max(1, KawaseHeights[i - 1] / 2);

                _cmdBuffer.GetTemporaryRT(
                    KawaseChainIds[i], KawaseWidths[i], KawaseHeights[i],
                    0, FilterMode.Bilinear, RenderTextureFormat.Default);
                _outsideBlurMat.SetVector(BlurTexelSizeId, new Vector4(
                    1f / KawaseWidths[i - 1],
                    1f / KawaseHeights[i - 1],
                    KawaseWidths[i - 1],
                    KawaseHeights[i - 1]));
                _outsideBlurMat.SetFloat(BlurOffsetId, radius);
                _cmdBuffer.Blit(KawaseChainIds[i - 1], KawaseChainIds[i], _outsideBlurMat, 0);
            }

            for (int i = levels - 1; i >= 0; i--)
            {
                _outsideBlurMat.SetVector(BlurTexelSizeId, new Vector4(
                    1f / KawaseWidths[i + 1],
                    1f / KawaseHeights[i + 1],
                    KawaseWidths[i + 1],
                    KawaseHeights[i + 1]));
                _outsideBlurMat.SetFloat(BlurOffsetId, radius);
                _cmdBuffer.Blit(KawaseChainIds[i + 1], KawaseChainIds[i], _outsideBlurMat, 1);
            }

            _outsideBlurMat.SetFloat(BlurOpacityId, opacity);
            _outsideBlurMat.SetFloat(BlurDarkeningId, darkening);
            _outsideBlurMat.SetFloat(BlurFlipYId, 0f);
            ApplyOutsideBlurRadialGate(viewport, cam);
            _cmdBuffer.SetGlobalTexture(BlurTextureId, KawaseChainIds[0]);
            _cmdBuffer.SetRenderTarget(effectsTarget);
            SetEffectsViewport(viewport);
            _cmdBuffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            if (standardOutsideBlur)
            {
                _cmdBuffer.DrawMesh(
                    fullScreenMesh,
                    Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(2f, 2f, 1f)),
                    _outsideBlurMat,
                    0,
                    2);
            }
            _cmdBuffer.DrawMesh(
                fullScreenMesh,
                Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(2f, 2f, 1f)),
                _outsideBlurMat,
                0,
                3);

            if (nvgLensFocalBlur)
            {
                float lensRadius = radius * Settings.NvgLensFocalBlurRadiusMultiplier.Value;
                for (int i = 1; i <= levels; i++)
                {
                    _outsideBlurMat.SetVector(BlurTexelSizeId, new Vector4(
                        1f / KawaseWidths[i - 1],
                        1f / KawaseHeights[i - 1],
                        KawaseWidths[i - 1],
                        KawaseHeights[i - 1]));
                    _outsideBlurMat.SetFloat(BlurOffsetId, lensRadius);
                    _cmdBuffer.Blit(KawaseChainIds[i - 1], KawaseChainIds[i], _outsideBlurMat, 0);
                }

                for (int i = levels - 1; i >= 0; i--)
                {
                    _outsideBlurMat.SetVector(BlurTexelSizeId, new Vector4(
                        1f / KawaseWidths[i + 1],
                        1f / KawaseHeights[i + 1],
                        KawaseWidths[i + 1],
                        KawaseHeights[i + 1]));
                    _outsideBlurMat.SetFloat(BlurOffsetId, lensRadius);
                    _cmdBuffer.Blit(KawaseChainIds[i + 1], KawaseChainIds[i], _outsideBlurMat, 1);
                }

                _cmdBuffer.SetGlobalTexture(BlurTextureId, KawaseChainIds[0]);
                _cmdBuffer.SetRenderTarget(effectsTarget);
                SetEffectsViewport(viewport);
                _cmdBuffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                _cmdBuffer.DrawMesh(
                    fullScreenMesh,
                    Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(2f, 2f, 1f)),
                    _outsideBlurMat,
                    0,
                    4);
            }

            for (int i = 0; i <= levels; i++)
                _cmdBuffer.ReleaseTemporaryRT(KawaseChainIds[i]);
        }

        internal static bool IsNvgLensFocalBlurActive
        {
            get
            {
                return Settings.NvgLensFocalBlurEnabled.Value &&
                       Shader.GetGlobalFloat(NightVisionOnId) > 0.5f;
            }
        }

        private static bool ShouldApplyNvgLensFocalBlur()
        {
            return IsNvgLensFocalBlurActive;
        }

        private static void ApplyOutsideBlurRadialGate(Rect viewport, Camera cam)
        {
            if (!Settings.OutsideScopeBlurRadialGateEnabled.Value ||
                cam == null ||
                !ReticleRenderer.TryGetLensMaskClipBounds(cam, out Vector2 center, out Vector2 size))
            {
                _outsideBlurMat.SetFloat(BlurRadialGateEnabledId, 0f);
                return;
            }

            // PORT NOTE (4.1.3): was viewport.width/viewport.height. That Rect is real pixel
            // dimensions (correct for the SetViewport/blur-buffer-sizing calls elsewhere in
            // this file) but never reflects EFT's in-game "Aspect Ratio" override, which skews
            // cam.aspect/the projection matrix without changing the actual pixel buffer size.
            // The radial gate needs to match the same aspect the reticle renders at (see
            // ReticleRenderer.GetActiveAspect), so it uses cam.aspect directly here instead.
            float aspect = Mathf.Max(0.01f, cam.aspect);
            _outsideBlurMat.SetFloat(BlurRadialGateEnabledId, 1f);
            _outsideBlurMat.SetVector(BlurLensCenterId, new Vector4(center.x, center.y, 0f, 0f));
            _outsideBlurMat.SetVector(BlurLensSizeId, new Vector4(size.x, size.y, 0f, 0f));
            _outsideBlurMat.SetFloat(BlurViewportAspectId, aspect);
            _outsideBlurMat.SetFloat(BlurRadialGateStartId, Mathf.Max(0f, Settings.OutsideScopeBlurRadialGateStart.Value));
            _outsideBlurMat.SetFloat(BlurRadialGateSoftnessId, Mathf.Max(0.001f, Settings.OutsideScopeBlurRadialGateSoftness.Value));
        }

        private static Rect GetSceneViewport(Camera cam)
        {
            var ssaa = cam != null ? cam.GetComponent<SSAA>() : null;
            if (ssaa != null)
            {
                int inputWidth = ssaa.GetInputWidth();
                int inputHeight = ssaa.GetInputHeight();
                if (inputWidth > 0 && inputHeight > 0)
                    return new Rect(0f, 0f, inputWidth, inputHeight);
            }

            return new Rect(0f, 0f,
                Mathf.Max(1f, cam.pixelWidth),
                Mathf.Max(1f, cam.pixelHeight));
        }

        private static Rect GetDisplayViewport(Camera cam)
        {
            var ssaa = cam != null ? cam.GetComponent<SSAA>() : null;
            if (ssaa != null)
            {
                int outputWidth = ssaa.GetOutputWidth();
                int outputHeight = ssaa.GetOutputHeight();
                if (outputWidth > 0 && outputHeight > 0)
                    return new Rect(0f, 0f, outputWidth, outputHeight);
            }

            return Helpers.GetDisplayViewport(cam);
        }

        private static Rect GetEffectsViewport(Camera cam)
        {
            return _attachedEvent == CameraEvent.AfterEverything
                ? GetDisplayViewport(cam)
                : GetSceneViewport(cam);
        }

        private static RenderTargetIdentifier GetEffectsTarget(Camera cam)
        {
            var ssaa = cam != null ? cam.GetComponent<SSAA>() : null;
            return ssaa != null
                ? ssaa.GetRTIdentifier()
                : new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
        }

        private static void SetEffectsViewport(Rect viewport)
        {
            if (_attachedEvent != CameraEvent.AfterEverything)
                _cmdBuffer.SetViewport(viewport);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Shared helpers
        // ─────────────────────────────────────────────────────────────────────

        private static Mesh GetStencilMesh()
        {
            if (_shadowMesh != null) return _shadowMesh;
            if (_vigMesh != null) return _vigMesh;
            _shadowMesh = BuildQuadMesh("ScopeEffectsStencilQuad");
            return _shadowMesh;
        }

        private static Shader FindAlphaShader() =>
            Shader.Find("Sprites/Default")         ??
            Shader.Find("UI/Default")              ??
            Shader.Find("Unlit/Transparent")       ??
            Shader.Find("Particles/Alpha Blended") ??
            Shader.Find("Legacy Shaders/Transparent/Diffuse");

        private static bool ShouldKeepPersistedShadowVisible()
        {
            if (!_persistShadowUntilFovRestore) return false;
            if (!Settings.ModEnabled.Value) return false;
            if (!Settings.ScopeShadowEnabled.Value) return false;

            bool hasActiveOptic = false;
            float currentFov = FovController.MagnificationBaselineFov;

            if (CameraClass.Exist && CameraClass.Instance != null)
            {
                hasActiveOptic = CameraClass.Instance.OpticCameraManager != null &&
                                 CameraClass.Instance.OpticCameraManager.CurrentOpticSight != null;
                currentFov = CameraClass.Instance.Camera != null
                    ? CameraClass.Instance.Camera.fieldOfView
                    : CameraClass.Instance.Fov;
            }

            return hasActiveOptic || currentFov < FovController.MagnificationBaselineFov;
        }

        private static void EnsureStencilDebugMaterial()
        {
            if (_stencilDebugMat != null) return;

            Shader stencilShader = Shader.Find("UI/Default");
            _hasStencilSupport = stencilShader != null;
            if (!_hasStencilSupport) return;

            _stencilDebugMat = new Material(stencilShader)
            {
                color = new Color(0.1f, 1f, 0.1f, 0.45f),
                renderQueue = 5000
            };
            _stencilDebugMat.SetFloat("_Stencil", 0f);
            _stencilDebugMat.SetFloat("_StencilComp", (float)CompareFunction.NotEqual);
            _stencilDebugMat.SetFloat("_StencilOp", (float)StencilOp.Keep);
            _stencilDebugMat.SetFloat("_StencilReadMask", 255f);
            _stencilDebugMat.SetInt("_ZTest", (int)CompareFunction.Always);
            _stencilDebugMat.SetInt("_ZWrite", 0);
        }

        private static Mesh BuildQuadMesh(string meshName)
        {
            var mesh = new Mesh { name = meshName };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f), new Vector3(-0.5f,  0.5f, 0f)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f)
            };
            mesh.triangles = new[] { 0, 2, 1,  0, 3, 2 };
            mesh.normals = new[]
            {
                -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward
            };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
