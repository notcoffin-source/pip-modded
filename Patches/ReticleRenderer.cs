using System.Collections.Generic;
using System.IO;
using BSG.CameraEffects;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace PiPDisabler
{
    /// <summary>
    /// Renders the scope reticle via a CommandBuffer injected at
    /// CameraEvent.AfterEverything on the main FPS camera.
    ///
    /// ── CAMERA ALIGNMENT APPROACH ───────────────────────────────────────
    /// The root cause of reticle jitter is the mismatch between where the
    /// camera looks and where the scope tube points.  In vanilla PiP, this
    /// doesn't matter — the optic camera is aligned to the scope by design.
    /// In no-PiP mode, the main camera and scope have slightly different
    /// orientations, and any reticle placement (world-space, angular, etc.)
    /// amplifies that difference at tighter FOV values.
    ///
    /// The fix: in onPreCull (after all game systems have run), override the
    /// main camera's rotation to match the scope's forward direction.  This
    /// makes the rendered frame look exactly where the scope points.  The
    /// reticle becomes a simple fixed quad at screen center — zero jitter
    /// by definition.
    ///
    /// Weapon sway is preserved: the scope transform sways each frame from
    /// ProceduralWeaponAnimation, and the camera follows.  The player sees
    /// the world shift (exactly like looking through a real scope), not a
    /// dancing crosshair.
    ///
    /// ── NVG INTEGRATION ─────────────────────────────────────────────────
    /// The CommandBuffer is attached at CameraEvent.AfterEverything, after
    /// Tarkov's NightVision post effect has already run. The reticle shader
    /// remaps non-black reticle pixels toward an NVG off-white.
    ///
    /// Dark reticle pixels stay black while colored pixels become whiteish.
    /// </summary>
    internal static class ReticleRenderer
    {
        private enum ReticleSource
        {
            None,
            Texture,
            Mesh
        }

        private static Material     _reticleMat;
        private static Mesh         _reticleMesh;
        private static Texture      _savedMarkTex;
        private static Texture      _savedMaskTex;
        private static ScopeReticle _savedScopeReticle;
        private static Material     _meshReticleMat;
        private static ReticleSource _reticleSource = ReticleSource.None;
        private static float        _meshReticleBoundsScale = 1f;

        // Stencil masking — lens visibility
        // UI/Default exposes _Stencil, _StencilComp, _StencilOp, _ColorMask which let us
        // write to the stencil buffer and test against it without a custom shader.
        private static Material            _stencilClearMat; // full-screen quad: write 0 to stencil
        private static Material            _lensStencilMat;  // lens pass: write 1 to stencil, no colour
        private static Material            _occluderStencilMat; // housing/weapon pass: write 2 to stencil
        private static Material            _stencilDebugMat; // debug overlay: red tint where lens writes
        private static readonly List<LensTransparency.LensMaskEntry> _lensMaskEntries = new List<LensTransparency.LensMaskEntry>();
        private static readonly List<Renderer> _occluderMaskRenderers = new List<Renderer>();
        private static readonly List<Vector3> _lensBoundsVertices = new List<Vector3>(256);
        private static readonly Dictionary<Mesh, List<Vector3>> _lensVertexCache =
            new Dictionary<Mesh, List<Vector3>>();
        private static readonly HashSet<Mesh> _lensVertexCacheFailures = new HashSet<Mesh>();
        private static bool                _hasStencilSupport; // true when UI/Default was found

        // Debug frame counter — logs stencil state for first N frames after scope enter
        private static int  _debugFrameCount;
        private const  int  DebugLogFrames = 10;

        // Scale tracking
        private static float _baseScale;
        private static float _lastMag = 1f;
        private static float _lastZoomPosition;
        private static Vector2 _reticlePixelSize = Vector2.zero;

        // Cached transforms
        private static Transform _opticTransform;   // OpticSight   — for forward (downrange)

        // CommandBuffer state
        private static CommandBuffer _cmdBuffer;
        private static Camera        _attachedCamera;
        private static CameraEvent   _attachedEvent = CameraEvent.AfterEverything;
        private static bool          _preCullRegistered;

        private const string AfterNvgReticleShaderName = "Hidden/PiPDisabler/AfterNvgReticle";
        private const string AfterNvgReticleBundleName = "pipdisabler_reticle_shaders.bundle";
        private static readonly int AfterNvgOnId = Shader.PropertyToID("_AfterNvgOn");
        private static readonly int AfterNvgColorId = Shader.PropertyToID("_AfterNvgColor");
        private static readonly int BlackPointId = Shader.PropertyToID("_BlackPoint");
        private static readonly int WhitePointId = Shader.PropertyToID("_WhitePoint");
        private static readonly int ClipToVignetteId = Shader.PropertyToID("_ClipToVignette");
        private static readonly int VignetteClipCenterId = Shader.PropertyToID("_VignetteClipCenter");
        private static readonly int VignetteClipSizeId = Shader.PropertyToID("_VignetteClipSize");
        private static readonly int VignetteClipRadiusId = Shader.PropertyToID("_VignetteClipRadius");
        private static readonly int VignetteClipSoftnessId = Shader.PropertyToID("_VignetteClipSoftness");
        private static AssetBundle _afterNvgShaderBundle;

        // World-space TRS for the reticle quad (rebuilt in onPreCull)
        private static Matrix4x4 _reticleMatrix = Matrix4x4.identity;

        // Rendering state
        private static bool _settled;
        private static bool _stencilOnlyPersistence;

        // Camera alignment state
        private static bool _alignmentActive;
        private static bool _fireReloadRotationSuppressed;
        private static bool _fireReloadRotationEntering;
        private static bool _fireReloadRotationRecovering;
        private static float _fireReloadRotationEnterStartTime;
        private static float _fireReloadRotationRecoverStartTime;
        private static Quaternion _fireReloadRotationEnterStart = Quaternion.identity;
        private static Quaternion _fireReloadRotationRecoverStart = Quaternion.identity;

        private const string FireReloadStateName = "Hands.FIRE RELOAD";
        private const int HandsAnimatorLayer = 1;
        private const float FireReloadRotationEnterDuration = 0.5f;
        private const float FireReloadRotationRecoverDuration = 0.12f;

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Extract reticle data from ScopeData first, then lens mark textures.
        /// MUST be called BEFORE LensTransparency replaces the mesh.
        /// </summary>
        public static void ExtractReticle(OpticSight os)
        {
            if (os == null) return;

            _savedMarkTex = null;
            _savedMaskTex = null;
            _savedScopeReticle = null;
            _reticleSource = ReticleSource.None;

            try
            {
                ScopeReticle scopeReticle = os.ScopeData != null ? os.ScopeData.Reticle : null;
                if (scopeReticle != null && scopeReticle.Mesh != null && scopeReticle.Material != null)
                {
                    _savedScopeReticle = scopeReticle;
                    _reticleSource = ReticleSource.Mesh;
                    _meshReticleBoundsScale = GetMeshReticleBoundsScale(scopeReticle.Mesh);
                }

                Renderer lensRenderer = os.LensRenderer;
                if (lensRenderer != null)
                {
                    Material mat = null;
                    foreach (var m in lensRenderer.sharedMaterials)
                    {
                        if (m != null && m.shader != null && m.shader.name.Contains("OpticSight"))
                        { mat = m; break; }
                    }
                    if (mat == null) mat = lensRenderer.sharedMaterial;

                    if (mat != null)
                    {
                        if (mat.HasProperty("_MarkTex"))
                            _savedMarkTex = mat.GetTexture("_MarkTex");
                        if (mat.HasProperty("_MaskTex"))
                            _savedMaskTex = mat.GetTexture("_MaskTex");
                    }
                }

                if (_savedMarkTex != null)
                {
                    _savedMarkTex.filterMode = FilterMode.Trilinear;
                    _savedMarkTex.anisoLevel = 16;
                    if (_reticleSource == ReticleSource.None)
                        _reticleSource = ReticleSource.Texture;
                }

                PiPDisablerPlugin.DebugLogInfo(
                    $"[Reticle] Extracted: source={_reticleSource} " +
                    $"ScopeReticle={(_savedScopeReticle != null ? _savedScopeReticle.Mesh.name : "null")} " +
                    $"_MarkTex={(_savedMarkTex != null ? _savedMarkTex.name : "null")} " +
                    $"({(_savedMarkTex != null ? $"{_savedMarkTex.width}x{_savedMarkTex.height}" : "?")}) " +
                    $"_MaskTex={(_savedMaskTex != null ? _savedMaskTex.name : "null")} " +
                    "filter=Trilinear aniso=16");
            }
            catch (System.Exception e)
            {
                PiPDisablerPlugin.DebugLogInfo($"[Reticle] Extract failed: {e.Message}");
            }
        }

        /// <summary>
        /// Show the reticle.  Creates the mesh/material, attaches the CommandBuffer
        /// to the main camera, and registers the onPreCull hook.
        /// </summary>
        public static void Show(OpticSight os, float magnification = 1f)
        {
            if (os == null || _reticleSource == ReticleSource.None) return;

            try
            {
                _opticTransform = os.transform;

                EnsureMeshAndMaterial();

                if (_reticleSource == ReticleSource.Texture)
                {
                    if (_savedMarkTex == null) return;
                    _reticleMat.mainTexture = _savedMarkTex;
                    ApplyHorizontalFlip();
                }
                else if (_reticleSource == ReticleSource.Mesh)
                {
                    EnsureMeshReticleMaterial();
                    if (_meshReticleMat == null || _savedScopeReticle == null) return;
                }

                // Scale
                _baseScale = GetBaseReticleScale();
                if (_baseScale < 0.001f) _baseScale = 0.030f;

                if (magnification < 1f) magnification = 1f;
                _lastMag = magnification;
                _lastZoomPosition = FovController.GetVisualZoomPosition();

                // Attach CommandBuffer + onPreCull
                AttachToCamera();

                _stencilOnlyPersistence = false;
                _settled = true;
                _alignmentActive = true;

                PiPDisablerPlugin.DebugLogInfo(
                    $"[Reticle] Showing: source={_reticleSource} base={_baseScale:F4} " +
                    $"mag={magnification:F1}x zoomT={_lastZoomPosition:F3} " +
                    $"(camera-aligned centered rendering)");
            }
            catch (System.Exception e)
            {
                PiPDisablerPlugin.DebugLogInfo($"[Reticle] Show failed: {e.Message}");
            }
        }

        /// <summary>
        /// Per-frame update from ScopeLifecycle.Tick():
        /// handles scale changes and ensures the CommandBuffer is attached.
        /// </summary>
        public static void UpdateTransform(float magnification)
        {
            if (_cmdBuffer == null) return;

            EnsureCorrectCameraEvent();

            if (magnification < 1f) magnification = 1f;
            if (Mathf.Abs(magnification - _lastMag) >= 0.01f)
                _lastMag = magnification;
            _lastZoomPosition = FovController.GetVisualZoomPosition();
            _baseScale = GetBaseReticleScale();
            if (_baseScale < 0.001f) _baseScale = 0.030f;

            var mainCam = Helpers.GetMainCamera();
            if (mainCam != null && mainCam != _attachedCamera)
                AttachToCamera();
        }

        public static void Hide()
        {
            _alignmentActive = false;
            ResetFireReloadRotationState();
            _settled = false;
            _stencilOnlyPersistence = false;
            DetachFromCamera();
        }

        public static void OnScopeExit(bool keepStencil)
        {
            _alignmentActive = false;
            ResetFireReloadRotationState();
            _settled = false;

            if (keepStencil && _cmdBuffer != null)
            {
                _stencilOnlyPersistence = true;
                return;
            }

            Cleanup();
        }

        public static void StopStencilOnlyPersistence()
        {
            if (!_stencilOnlyPersistence) return;
            Cleanup();
        }

        public static void Cleanup()
        {
            Hide();
            _lensMaskEntries.Clear();
            _occluderMaskRenderers.Clear();
            _debugFrameCount   = 0;
            _savedMarkTex      = null;
            _savedMaskTex      = null;
            _savedScopeReticle  = null;
            _meshReticleMat     = null;
            _reticleSource      = ReticleSource.None;
            _meshReticleBoundsScale = 1f;
            _opticTransform    = null;
            _lastMag           = 1f;
            _lastZoomPosition   = 0f;
            _reticlePixelSize   = Vector2.zero;
            _baseScale         = 0f;
            _settled           = false;
        }

        /// <summary>
        /// Returns true if camera alignment is currently active while scoped.
        /// Used by ScopeEffectsRenderer to know that vignette/shadow can also
        /// render centered rather than tracking lens position.
        /// </summary>
        public static bool IsAlignmentActive => _alignmentActive && _settled;

        /// <summary>
        /// Returns the current optic transform (for ScopeEffectsRenderer to
        /// share camera alignment).
        /// </summary>
        public static Transform OpticTransform => _opticTransform;

        public static CameraEvent CurrentCameraEvent => _attachedEvent;

        public static bool HasLensStencilMask => _hasStencilSupport && _lensMaskEntries.Count > 0;

        public static bool AppendLensStencilMask(CommandBuffer cmd, Mesh fullScreenMesh, Camera cam)
        {
            if (cmd == null || fullScreenMesh == null || cam == null) return false;
            if (!_hasStencilSupport || _lensMaskEntries.Count == 0) return false;
            if (_stencilClearMat == null || _lensStencilMat == null) return false;

            var fullScreenMatrix = Matrix4x4.TRS(
                Vector3.zero, Quaternion.identity, new Vector3(2f, 2f, 1f));

            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.DrawMesh(fullScreenMesh, fullScreenMatrix, _stencilClearMat, 0, -1);

            cmd.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);
            for (int i = 0; i < _occluderMaskRenderers.Count; i++)
                DrawOccluderMaskRenderer(cmd, _occluderMaskRenderers[i]);
            for (int i = 0; i < _lensMaskEntries.Count; i++)
                DrawLensMaskEntry(cmd, _lensMaskEntries[i]);

            return true;
        }

        public static bool TryGetLensMaskClipBounds(Camera cam, out Vector2 center, out Vector2 size)
        {
            center = Vector2.zero;
            size = Vector2.zero;
            if (cam == null || _lensMaskEntries.Count == 0) return false;

            bool any = false;
            Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

            for (int i = 0; i < _lensMaskEntries.Count; i++)
            {
                var entry = _lensMaskEntries[i];
                if (entry.Renderer == null || entry.Mesh == null) continue;
                if (!entry.Renderer.gameObject.activeInHierarchy) continue;

                Matrix4x4 matrix = entry.Renderer.localToWorldMatrix;
                if (!TryGetLensMeshVertices(entry.Mesh, _lensBoundsVertices))
                    continue;

                for (int v = 0; v < _lensBoundsVertices.Count; v++)
                {
                    Vector3 local = _lensBoundsVertices[v];
                    Vector3 view = cam.WorldToViewportPoint(matrix.MultiplyPoint3x4(local));
                    if (view.z <= cam.nearClipPlane) continue;

                    min.x = Mathf.Min(min.x, view.x);
                    min.y = Mathf.Min(min.y, view.y);
                    max.x = Mathf.Max(max.x, view.x);
                    max.y = Mathf.Max(max.y, view.y);
                    any = true;
                }
            }

            if (!any) return false;

            center = new Vector2((min.x + max.x) - 1f, (min.y + max.y) - 1f);
            size = new Vector2(
                Mathf.Max(0.001f, (max.x - min.x) * 2f),
                Mathf.Max(0.001f, (max.y - min.y) * 2f));
            return true;
        }

        private static bool TryGetLensMeshVertices(Mesh mesh, List<Vector3> vertices)
        {
            vertices.Clear();
            if (mesh == null) return false;

            if (_lensVertexCache.TryGetValue(mesh, out var cached))
            {
                vertices.AddRange(cached);
                return vertices.Count > 0;
            }

            var extracted = new List<Vector3>(Mathf.Max(16, mesh.vertexCount));

            try
            {
                if (mesh.isReadable)
                {
                    mesh.GetVertices(extracted);
                }
                else
                {
                    Mesh readableCopy = MeshPlaneCutter.MakeReadableMeshCopy(mesh);
                    if (readableCopy != null)
                    {
                        try
                        {
                            readableCopy.GetVertices(extracted);
                        }
                        finally
                        {
                            UnityEngine.Object.Destroy(readableCopy);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                if (!_lensVertexCacheFailures.Contains(mesh))
                {
                    _lensVertexCacheFailures.Add(mesh);
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[Reticle] Failed to extract lens vertices from '{mesh.name}': {ex.Message}");
                }
                return false;
            }

            if (extracted.Count == 0)
                return false;

            _lensVertexCache[mesh] = extracted;
            vertices.AddRange(extracted);
            return true;
        }

        /// <summary>
        /// Provide cached lens mesh entries for the stencil pass.
        /// Pass null or an empty list to disable lens masking.
        /// </summary>
        public static void SetLensMaskEntries(List<LensTransparency.LensMaskEntry> entries)
        {
            _lensMaskEntries.Clear();
            _debugFrameCount = 0;
            if (entries != null)
                _lensMaskEntries.AddRange(entries);

            PiPDisablerPlugin.DebugLogInfo(
                $"[Reticle] Lens mask: {_lensMaskEntries.Count} entry(s) registered" +
                $" stencilSupport={_hasStencilSupport}");
        }

        public static void SetOccluderMaskRenderers(List<Renderer> renderers)
        {
            _occluderMaskRenderers.Clear();
            if (renderers != null)
                _occluderMaskRenderers.AddRange(renderers);
        }

        // ── CommandBuffer management ────────────────────────────────────────

        private static void AttachToCamera()
        {
            var mainCam = Helpers.GetMainCamera();
            if (mainCam == null) return;

            if (_attachedCamera != null && _attachedCamera != mainCam)
                DetachFromCamera();

            if (_attachedCamera == mainCam) return;

            if (_cmdBuffer == null)
                _cmdBuffer = new CommandBuffer { name = "ScopeReticleOverlay" };

            CameraEvent targetEvent = GetReticleCameraEvent();
            mainCam.AddCommandBuffer(targetEvent, _cmdBuffer);
            _attachedCamera = mainCam;
            _attachedEvent = targetEvent;

            if (!_preCullRegistered)
            {
                Camera.onPreCull += OnPreCullCallback;
                _preCullRegistered = true;
            }

            PiPDisablerPlugin.DebugLogInfo(
                $"[Reticle] CommandBuffer attached to '{mainCam.name}' at {_attachedEvent}");
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


        private static CameraEvent GetReticleCameraEvent()
        {
            return CameraEvent.AfterEverything;
        }

        private static void EnsureCorrectCameraEvent()
        {
            if (_attachedCamera == null || _cmdBuffer == null) return;

            CameraEvent desiredEvent = GetReticleCameraEvent();
            if (desiredEvent == _attachedEvent) return;

            try { _attachedCamera.RemoveCommandBuffer(_attachedEvent, _cmdBuffer); }
            catch (System.Exception) { }

            _attachedCamera.AddCommandBuffer(desiredEvent, _cmdBuffer);
            _attachedEvent = desiredEvent;

            PiPDisablerPlugin.DebugLogInfo($"[Reticle] CommandBuffer moved to {_attachedEvent} (debug toggle)");
        }

        // ── onPreCull — camera alignment + rebuild CommandBuffer ─────────────

        private static void OnPreCullCallback(Camera cam)
        {
            if (cam != _attachedCamera) return;
            if (_cmdBuffer == null || !_settled) return;
            if (GetActiveReticleMesh() == null || GetActiveReticleMaterial() == null) return;

            // ── Camera alignment ─────────────────────────────────────────
            // Override the camera's rotation to look exactly where the scope
            // points.  This happens in onPreCull — after all game systems
            // (PWA, animation, IK) and OpticComponentUpdater.LateUpdate()
            // have updated transforms, but before Unity starts rendering.
            //
            // We use the optic camera's transform cached by PiPDisabler.
            // OpticComponentUpdater.LateUpdate() syncs this transform to the
            // scope's look direction every frame.  We let LateUpdate run
            // (v4.5.2 fix), so the transform is always up to date even though
            // the optic camera itself can't render.
            if (_alignmentActive && !FreelookTracker.IsFreelooking)
            {
                // Primary source: optic camera transform kept in sync by EFT updater.
                // Fallback: optic transform itself, so sway-follow remains active even
                // if optic camera cache is temporarily unavailable.
                //
                // Skipped during freelook: the player is looking around independently
                // of the scope direction, so the camera must NOT be locked to the optic.
                Transform swaySource = PiPDisabler.OpticCameraTransform ?? _opticTransform;
                if (swaySource != null)
                {
                    bool suppressForFireReload = IsFireReloadStateActive();
                    if (suppressForFireReload)
                    {
                        if (!_fireReloadRotationSuppressed && !_fireReloadRotationEntering)
                        {
                            _fireReloadRotationEntering = true;
                            _fireReloadRotationEnterStartTime = Time.realtimeSinceStartup;
                            _fireReloadRotationEnterStart = swaySource.rotation;
                        }

                        _fireReloadRotationRecovering = false;

                        if (_fireReloadRotationEntering)
                        {
                            float elapsed = Time.realtimeSinceStartup - _fireReloadRotationEnterStartTime;
                            float t = Mathf.Clamp01(elapsed / FireReloadRotationEnterDuration);
                            t = Mathf.SmoothStep(0f, 1f, t);
                            cam.transform.rotation = Quaternion.Slerp(
                                _fireReloadRotationEnterStart,
                                cam.transform.rotation,
                                t);

                            if (t >= 1f)
                            {
                                _fireReloadRotationEntering = false;
                                _fireReloadRotationSuppressed = true;
                            }
                        }
                        else
                        {
                            _fireReloadRotationSuppressed = true;
                        }
                    }
                    else
                    {
                        if (_fireReloadRotationSuppressed || _fireReloadRotationEntering)
                        {
                            _fireReloadRotationSuppressed = false;
                            _fireReloadRotationEntering = false;
                            _fireReloadRotationRecovering = true;
                            _fireReloadRotationRecoverStartTime = Time.realtimeSinceStartup;
                            _fireReloadRotationRecoverStart = cam.transform.rotation;
                        }

                        if (_fireReloadRotationRecovering)
                        {
                            float elapsed = Time.realtimeSinceStartup - _fireReloadRotationRecoverStartTime;
                            float t = Mathf.Clamp01(elapsed / FireReloadRotationRecoverDuration);
                            t = Mathf.SmoothStep(0f, 1f, t);
                            cam.transform.rotation = Quaternion.Slerp(
                                _fireReloadRotationRecoverStart,
                                swaySource.rotation,
                                t);

                            if (t >= 1f)
                                _fireReloadRotationRecovering = false;
                        }
                        else
                        {
                            cam.transform.rotation = swaySource.rotation;
                        }
                    }
                }
            }

            RebuildMatrix(cam);
            RebuildCommandBuffer(cam);
        }

        private static bool IsFireReloadStateActive()
        {
            try
            {
                var player = Helpers.GetLocalPlayer();
                var firearmsAnimator = player?.HandsController?.FirearmsAnimator;
                return firearmsAnimator != null &&
                       firearmsAnimator.CurrentStateNameIs(HandsAnimatorLayer, FireReloadStateName);
            }
            catch
            {
                return false;
            }
        }

        private static void ResetFireReloadRotationState()
        {
            _fireReloadRotationSuppressed = false;
            _fireReloadRotationEntering = false;
            _fireReloadRotationRecovering = false;
            _fireReloadRotationEnterStartTime = 0f;
            _fireReloadRotationRecoverStartTime = 0f;
            _fireReloadRotationEnterStart = Quaternion.identity;
            _fireReloadRotationRecoverStart = Quaternion.identity;
        }

        // ── Centered quad matrix ─────────────────────────────────────────────

        /// <summary>
        /// Place the reticle quad at screen center: a fixed distance along
        /// the camera's (now scope-aligned) forward.  Since the camera is
        /// aligned with the scope, this is always dead center.
        ///
        /// Size is fixed in screen-space and does not react to current FOV or magnification.
        /// </summary>
        private static void RebuildMatrix(Camera cam)
        {
            if (cam == null) return;

            if (_reticleSource == ReticleSource.Mesh && _savedScopeReticle != null)
            {
                float meshAspect = GetActiveAspect(cam);
                float zoomPosition = FovController.GetVisualZoomPosition();
                float currentMag = FovController.GetVisualMagnificationUncached();
                if (currentMag < 1f) currentMag = _lastMag;
                float meshZoomScale = Mathf.Lerp(1f, Mathf.Max(1f, currentMag), zoomPosition);
                float normalizedScale = Settings.MeshReticleNormalizedScale.Value;
                float meshScale = _baseScale * meshZoomScale * _meshReticleBoundsScale * normalizedScale * Settings.GlobalReticleScalingMultiplier.Value;
                float minMeshScale = PerScopeMeshSurgerySettings.GetMeshReticleMinScale() * Settings.GlobalReticleScalingMultiplier.Value;
                float maxMeshScale = PerScopeMeshSurgerySettings.GetMeshReticleMaxScale() * Settings.GlobalReticleScalingMultiplier.Value;
                if (minMeshScale > 0f && maxMeshScale > 0f)
                {
                    if (maxMeshScale < minMeshScale)
                    {
                        float swap = minMeshScale;
                        minMeshScale = maxMeshScale;
                        maxMeshScale = swap;
                    }

                    if (FovController.TryGetCurrentVariableFovRange(
                        out float currentFov,
                        out float wideFov,
                        out float narrowFov,
                        out float variableZoomPosition))
                    {
                        float fovHigh = Mathf.Max(wideFov, narrowFov);
                        float fovLow = Mathf.Min(wideFov, narrowFov);
                        if (!Mathf.Approximately(fovHigh, fovLow))
                        {
                            float clampedFov = Mathf.Clamp(currentFov, fovLow, fovHigh);
                            float t = Mathf.Clamp01(Mathf.InverseLerp(fovHigh, fovLow, clampedFov));
                            if (variableZoomPosition >= 0f)
                                t = variableZoomPosition;

                            meshScale = Mathf.Lerp(minMeshScale, maxMeshScale, t);
                        }
                    }
                    else
                    {
                        meshScale = Mathf.Lerp(
                            minMeshScale,
                            maxMeshScale,
                            FovController.GetVisualZoomPosition());
                    }
                }

                Vector3 position = _savedScopeReticle.Position;
                position.z = 0.5f;
                float zRotation = Mathf.Repeat(_savedScopeReticle.Rotation.z, 360f);
                bool quarterTurnReticle = Mathf.Abs(zRotation - 90f) < 0.5f || Mathf.Abs(zRotation - 270f) < 0.5f;
                Vector3 meshReticleScale = quarterTurnReticle
                    ? new Vector3(meshScale, meshScale / Mathf.Max(0.01f, meshAspect), meshScale)
                    : new Vector3(meshScale / Mathf.Max(0.01f, meshAspect), meshScale, meshScale);

                _reticleMatrix = Matrix4x4.TRS(
                    position,
                    Quaternion.Euler(_savedScopeReticle.Rotation),
                    meshReticleScale);
                return;
            }

            // Clip-space centered quad: independent of world/lens transforms.
            // Convert configured physical size using fixed references so the
            // reticle stays constant on screen regardless of runtime FOV/mag.
            const float referenceFovDeg = 35f;
            const float referenceLensDistance = 0.075f;
            float referenceTanHalfFov = Mathf.Max(0.01f, Mathf.Tan(referenceFovDeg * Mathf.Deg2Rad * 0.5f));

        float angularSize = _baseScale / referenceLensDistance;
        float ndcSize = angularSize / referenceTanHalfFov;
        ndcSize *= Settings.GlobalReticleScalingMultiplier.Value;
        ndcSize = Mathf.Clamp(ndcSize, 0.01f, 2f);

            Vector3 pos = new Vector3(0f, 0f, 0.5f);
            float aspect = GetActiveAspect(cam);
            Vector3 scale = new Vector3(ndcSize / Mathf.Max(0.01f, aspect), ndcSize, 1f);
            _reticleMatrix = Matrix4x4.TRS(pos, Quaternion.identity, scale);
        }

        /// <summary>
        /// Rebuild the CommandBuffer.
        ///
        /// When cached lens meshes are available and UI/Default was found (stencil-capable):
        ///   1. Clear stencil to 0 with a full-screen clip-space quad.
        ///   2. Draw the cached lens meshes in world-space, writing 1 to stencil where
        ///      the lens passes the depth test.
        ///   3. Draw the reticle with stencil test Equal-1, so it only appears inside the
        ///      visible lens.
        ///
        /// Falls back to the original single-draw path when stencil is unavailable or no
        /// lens renderers have been registered.
        ///
        /// The reticle is attached at AfterEverything, so CameraTarget and the
        /// display viewport are explicitly bound for the final upscaled frame.
        /// </summary>
        private static void RebuildCommandBuffer(Camera cam)
        {
            _cmdBuffer.Clear();

            bool isAfterEverything = _attachedEvent == CameraEvent.AfterEverything;

            if (isAfterEverything)
            {
                // Late-overlay path: draw in display space after upscaling/postfx.
                Rect viewport = GetDisplayViewport(cam);
                _cmdBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                _cmdBuffer.SetViewport(viewport);
                _reticlePixelSize = GetClipPixelSize(viewport);
            }
            else
            {
                // Scene-overlay path: use render-resolution viewport for DLSS/FSR correctness.
                Rect viewport = GetSceneViewport(cam);
                _cmdBuffer.SetViewport(viewport);
                _reticlePixelSize = GetClipPixelSize(viewport);
            }

            bool useStencil = _hasStencilSupport && _lensMaskEntries.Count > 0
                              && _stencilClearMat != null && _lensStencilMat != null;

            // ── Per-frame debug logging (first N frames after scope enter) ────────────
            if (_debugFrameCount < DebugLogFrames)
            {
                int activeCount = 0;
                for (int i = 0; i < _lensMaskEntries.Count; i++)
                {
                    var entry = _lensMaskEntries[i];
                    if (entry.Renderer != null && entry.Renderer.gameObject.activeInHierarchy) activeCount++;
                }

                PiPDisablerPlugin.DebugLogInfo(
                    $"[Reticle] Frame {_debugFrameCount + 1}/{DebugLogFrames}: " +
                    $"useStencil={useStencil} lensTotal={_lensMaskEntries.Count} " +
                    $"lensActive={activeCount} stencilSupport={_hasStencilSupport}");
                _debugFrameCount++;
            }

            // Scale the unit quad (-0.5..0.5) to cover the full NDC range (-1..1).
            var fullScreenMatrix = Matrix4x4.TRS(
                Vector3.zero, Quaternion.identity, new Vector3(2f, 2f, 1f));

            if (useStencil)
            {
                AppendLensStencilMask(_cmdBuffer, _reticleMesh, cam);

                if (!_stencilOnlyPersistence && !ScopeEffectsRenderer.IsNvgLensFocalBlurActive)
                {
                    // ── Step 3: draw reticle only inside the visible lens (clip-space) ──
                    _cmdBuffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    DrawActiveReticle();
                }

                // ── Step 4: optional debug overlay — red tint where lens writes ─────
                // Renders anywhere stencil == 1, i.e. every visible lens pixel.
                // Enable via DebugShowHousingMask in BepInEx config.
                if (Settings.DebugShowHousingMask.Value && _stencilDebugMat != null)
                {
                    _cmdBuffer.DrawMesh(_reticleMesh, fullScreenMatrix, _stencilDebugMat, 0, -1);
                }
            }
            else
            {
                // Original path — no stencil.
                _cmdBuffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                if (!ScopeEffectsRenderer.IsNvgLensFocalBlurActive)
                    DrawActiveReticle();
            }

            _cmdBuffer.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);
        }

        public static bool AppendReticleForNvgLensBlur(CommandBuffer cmd, Rect viewport)
        {
            if (!ScopeEffectsRenderer.IsNvgLensFocalBlurActive) return false;
            if (cmd == null || !HasLensStencilMask) return false;
            if (GetActiveReticleMesh() == null || GetActiveReticleMaterial() == null) return false;

            _reticlePixelSize = GetClipPixelSize(viewport);
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            DrawActiveReticle(cmd);
            return true;
        }

        private static void DrawActiveReticle()
        {
            DrawActiveReticle(_cmdBuffer);
        }

        private static void DrawActiveReticle(CommandBuffer cmd)
        {
            if (!HasLensStencilMask) return;

            Mesh mesh = GetActiveReticleMesh();
            Material material = GetActiveReticleMaterial();
            if (cmd == null || mesh == null || material == null) return;

            ApplyAfterNvgProperties(material);

            if (_reticleSource == ReticleSource.Mesh && Settings.MeshReticleMinimumStrokeEnabled.Value)
                DrawMeshReticleWithMinimumStroke(cmd, mesh, material);
            else
                DrawReticleMesh(cmd, mesh, material, _reticleMatrix);
        }

        private static void DrawMeshReticleWithMinimumStroke(CommandBuffer cmd, Mesh mesh, Material material)
        {
            float minimumPixels = Settings.MeshReticleMinimumStrokePixels.Value;
            if (minimumPixels <= 0f || _reticlePixelSize.x <= 0f || _reticlePixelSize.y <= 0f)
            {
                DrawReticleMesh(cmd, mesh, material, _reticleMatrix);
                return;
            }

            float pixelRadius = Mathf.Clamp((minimumPixels - 1f) * 0.5f, 0f, 1.5f);
            if (pixelRadius > 0f)
            {
                DrawReticleMesh(cmd, mesh, material, OffsetReticleMatrix(-_reticlePixelSize.x * pixelRadius, 0f));
                DrawReticleMesh(cmd, mesh, material, OffsetReticleMatrix( _reticlePixelSize.x * pixelRadius, 0f));
                DrawReticleMesh(cmd, mesh, material, OffsetReticleMatrix(0f, -_reticlePixelSize.y * pixelRadius));
                DrawReticleMesh(cmd, mesh, material, OffsetReticleMatrix(0f,  _reticlePixelSize.y * pixelRadius));
            }

            DrawReticleMesh(cmd, mesh, material, _reticleMatrix);
        }

        private static void DrawReticleMesh(CommandBuffer cmd, Mesh mesh, Material material, Matrix4x4 matrix)
        {
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                cmd.DrawMesh(mesh, matrix, material, subMesh, -1);
        }

        private static Matrix4x4 OffsetReticleMatrix(float clipX, float clipY)
        {
            return Matrix4x4.Translate(new Vector3(clipX, clipY, 0f)) * _reticleMatrix;
        }

        private static Mesh GetActiveReticleMesh()
        {
            return _reticleSource == ReticleSource.Mesh && _savedScopeReticle != null
                ? _savedScopeReticle.Mesh
                : _reticleMesh;
        }

        private static Material GetActiveReticleMaterial()
        {
            return _reticleSource == ReticleSource.Mesh
                ? _meshReticleMat
                : _reticleMat;
        }

        private static void ApplyAfterNvgProperties(Material material)
        {
            if (material == null || !material.HasProperty(AfterNvgOnId)) return;

            bool nvgOn = Shader.GetGlobalFloat("_NightVisionOn") > 0.5f;
            material.SetFloat(AfterNvgOnId, nvgOn ? 1f : 0f);
            material.SetFloat(BlackPointId, 0.04f);
            material.SetFloat(WhitePointId, 0.22f);

            Color afterNvgColor = new Color(0.86f, 0.95f, 0.82f, 1f);
            Camera cam = _attachedCamera != null ? _attachedCamera : Helpers.GetMainCamera();
            NightVision nightVision = cam != null ? cam.GetComponent<NightVision>() : null;
            if (nightVision != null)
            {
                Color sourceColor = nightVision.Color;
                float maxChannel = Mathf.Max(sourceColor.r, Mathf.Max(sourceColor.g, sourceColor.b));
                if (maxChannel > 0.001f)
                {
                    Color normalized = new Color(
                        sourceColor.r / maxChannel,
                        sourceColor.g / maxChannel,
                        sourceColor.b / maxChannel,
                        1f);
                    afterNvgColor = Color.Lerp(Color.white, normalized, 0.35f);
                    afterNvgColor.a = 1f;
                }
            }

            material.SetColor(AfterNvgColorId, afterNvgColor);
            ApplyVignetteClipProperties(material, cam);
        }

        private static void ApplyVignetteClipProperties(Material material, Camera cam)
        {
            if (material == null || !material.HasProperty(ClipToVignetteId)) return;

            if (!Settings.VignetteEnabled.Value ||
                cam == null ||
                !TryGetLensMaskClipBounds(cam, out Vector2 center, out Vector2 size))
            {
                material.SetFloat(ClipToVignetteId, 0f);
                return;
            }

            material.SetFloat(ClipToVignetteId, 1f);
            material.SetVector(VignetteClipCenterId, new Vector4(center.x, center.y, 0f, 0f));
            material.SetVector(VignetteClipSizeId, new Vector4(size.x, size.y, 0f, 0f));
            material.SetFloat(VignetteClipRadiusId, PerScopeMeshSurgerySettings.GetVignetteRadius());
            material.SetFloat(VignetteClipSoftnessId, PerScopeMeshSurgerySettings.GetVignetteSoftness());
        }

        private static float GetBaseReticleScale()
        {
            float configBase = PerScopeMeshSurgerySettings.GetReticleBaseSize();

            if (_reticleSource == ReticleSource.Mesh)
            {
                if (configBase > 0f)
                    return configBase;
                if (_savedScopeReticle != null && _savedScopeReticle.Scale > 0f)
                    return _savedScopeReticle.Scale;
            }

            return configBase > 0f
                ? configBase
                : PerScopeMeshSurgerySettings.GetPlane1Radius() * 2f;
        }

        private static float GetMeshReticleBoundsScale(Mesh mesh)
        {
            if (mesh == null)
                return 1f;

            Bounds bounds = mesh.bounds;
            Vector3 size = bounds.size;
            float maxDimension = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            return maxDimension > 0.0001f ? 1f / maxDimension : 1f;
        }

        private static void DrawLensMaskEntry(CommandBuffer cmd, LensTransparency.LensMaskEntry entry)
        {
            if (entry.Renderer == null || entry.Mesh == null) return;
            if (!entry.Renderer.gameObject.activeInHierarchy) return;

            int subMeshCount = Mathf.Max(1, entry.Mesh.subMeshCount);
            Matrix4x4 matrix = entry.Renderer.localToWorldMatrix;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                cmd.DrawMesh(entry.Mesh, matrix, _lensStencilMat, subMesh, -1);
        }

        private static void DrawOccluderMaskRenderer(CommandBuffer cmd, Renderer renderer)
        {
            if (renderer == null) return;
            if (!renderer.gameObject.activeInHierarchy) return;

            var mf = renderer.GetComponent<MeshFilter>();
            var smr = renderer as SkinnedMeshRenderer;
            Mesh mesh = mf?.sharedMesh ?? smr?.sharedMesh;
            if (mesh == null) return;

            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            Matrix4x4 matrix = renderer.localToWorldMatrix;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                cmd.DrawMesh(mesh, matrix, _occluderStencilMat, subMesh, -1);
        }

        // ── Private helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Returns the viewport rect in render-resolution pixel coordinates.
        /// cam.pixelWidth/pixelHeight always reflect the actual RT size, even
        /// under DLSS/FSR where Screen.width/height report display resolution.
        /// </summary>
        private static Rect GetSceneViewport(Camera cam)
        {
            var ssaa = cam != null ? cam.GetComponent<SSAA>() : null;
            if (ssaa != null)
            {
                int outputWidth = ssaa.GetOutputWidth();
                int outputHeight = ssaa.GetOutputHeight();
                if (outputWidth > 0 && outputHeight > 0)
                    return new Rect(0f, 0f, outputWidth, outputHeight);
            }

            return new Rect(0f, 0f,
                Mathf.Max(1f, cam.pixelWidth),
                Mathf.Max(1f, cam.pixelHeight));
        }

        private static Rect GetDisplayViewport(Camera cam)
            => Helpers.GetDisplayViewport(cam);

        private static Vector2 GetClipPixelSize(Rect viewport)
        {
            float width = Mathf.Max(1f, viewport.width);
            float height = Mathf.Max(1f, viewport.height);
            return new Vector2(2f / width, 2f / height);
        }

        /// <summary>
        /// Returns the aspect ratio for the currently attached command-buffer event.
        ///
        /// PORT NOTE (4.1.3): switched from pixelWidth/pixelHeight and Screen.width/height
        /// to Camera.aspect. EFT's in-game "Aspect Ratio" setting (distinct from Resolution)
        /// works by overriding cam.aspect directly to skew the projection matrix — the actual
        /// render target/backbuffer stays at the real display resolution the whole time, so
        /// neither pixelWidth/pixelHeight nor Screen.width/height ever reflect the override.
        /// cam.aspect is the one property that's guaranteed to match whatever's actually
        /// driving the projection matrix, so it's correct both when EFT's aspect override is
        /// active and when it isn't (Unity auto-syncs .aspect to the real viewport otherwise).
        /// </summary>
        private static float GetActiveAspect(Camera cam)
        {
            return Mathf.Max(0.01f, cam.aspect);
        }

        private static void ApplyHorizontalFlip()
        {
            if (_reticleMesh == null) return;
            _reticleMesh.uv = new[]
            {
                new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1)
            };
        }

        private static Shader FindAfterNvgReticleShader()
        {
            Shader shader = Shader.Find(AfterNvgReticleShaderName);
            if (shader != null)
                return shader;

            if (_afterNvgShaderBundle == null)
            {
                string pluginDir = Path.GetDirectoryName(typeof(PiPDisablerPlugin).Assembly.Location);
                string bundlePath = Path.Combine(pluginDir ?? string.Empty, AfterNvgReticleBundleName);
                if (File.Exists(bundlePath))
                    _afterNvgShaderBundle = AssetBundle.LoadFromFile(bundlePath);
            }

            if (_afterNvgShaderBundle == null)
                return null;

            foreach (Shader bundledShader in _afterNvgShaderBundle.LoadAllAssets<Shader>())
            {
                if (bundledShader != null && bundledShader.name == AfterNvgReticleShaderName)
                    return bundledShader;
            }

            return null;
        }

        private static void EnsureMeshReticleMaterial()
        {
            if (_meshReticleMat != null || _savedScopeReticle == null) return;

            Shader reticleShader = FindAfterNvgReticleShader();
            if (reticleShader == null)
            {
                PiPDisablerPlugin.DebugLogInfo(
                    $"[Reticle] Missing shader '{AfterNvgReticleShaderName}'.");
                return;
            }

            Shader stencilShader = Shader.Find("UI/Default");
            if (stencilShader != null)
                _hasStencilSupport = true;

            _meshReticleMat = new Material(reticleShader)
            {
                color = Color.white,
                renderQueue = 3100
            };

            Material source = _savedScopeReticle.Material;
            if (source != null)
            {
                if (source.HasProperty("_MainTex") && source.mainTexture != null)
                    _meshReticleMat.mainTexture = source.mainTexture;
                if (source.HasProperty("_Color"))
                    _meshReticleMat.color = source.color;
            }

            _meshReticleMat.SetInt("_ZTest", (int)CompareFunction.Always);
            _meshReticleMat.SetInt("_ZWrite", 0);
            _meshReticleMat.SetFloat("_Stencil", 1f);
            _meshReticleMat.SetFloat("_StencilComp", (float)CompareFunction.Equal);
            _meshReticleMat.SetFloat("_StencilOp", (float)StencilOp.Keep);
            _meshReticleMat.SetFloat("_StencilReadMask", 255f);
            _meshReticleMat.SetFloat("_StencilWriteMask", 0f);
        }

        private static void EnsureMeshAndMaterial()
        {
            if (_reticleMesh != null && _reticleMat != null) return;

            if (_reticleMesh == null)
            {
                _reticleMesh = new Mesh { name = "ReticleQuad" };
                _reticleMesh.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0),
                    new Vector3(0.5f,  0.5f,  0), new Vector3(-0.5f, 0.5f, 0)
                };
                _reticleMesh.uv = new[]
                {
                    new Vector2(0, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0, 1)
                };
                _reticleMesh.triangles = new[] { 0,2,1, 0,3,2, 0,1,2, 0,2,3 };
                _reticleMesh.normals = new[]
                {
                    -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward
                };
                _reticleMesh.RecalculateBounds();
            }

            if (_reticleMat == null)
            {
                // UI/Default exposes _Stencil* and _ColorMask, so it must be found first
                // for stencil-based lens masking.  Sprites/Default does not have these.
                Shader stencilShader = Shader.Find("UI/Default");
                _hasStencilSupport   = stencilShader != null;

                Shader alphaShader = FindAfterNvgReticleShader();
                if (alphaShader == null)
                {
                    PiPDisablerPlugin.DebugLogInfo(
                        $"[Reticle] Missing shader '{AfterNvgReticleShaderName}'.");
                    return;
                }

                _reticleMat = new Material(alphaShader)
                {
                    color       = Color.white,
                    renderQueue = 3100
                };
                _reticleMat.SetInt("_ZTest",  (int)CompareFunction.Always);
                _reticleMat.SetInt("_ZWrite", 0);

                // ── Stencil test: only draw reticle where the lens DID write ─────────
                if (_hasStencilSupport)
                {
                    _reticleMat.SetFloat("_Stencil",          1f);
                    _reticleMat.SetFloat("_StencilComp",      (float)CompareFunction.Equal);
                    _reticleMat.SetFloat("_StencilOp",        (float)StencilOp.Keep);
                    _reticleMat.SetFloat("_StencilReadMask",  255f);
                    _reticleMat.SetFloat("_StencilWriteMask", 0f);   // don't write
                }

                PiPDisablerPlugin.DebugLogInfo(
                    $"[Reticle] Created material (shader='{(alphaShader != null ? alphaShader.name : "null")}'" +
                    $" stencilSupport={_hasStencilSupport})");

                // ── Stencil helper materials (both need UI/Default) ──────────────────
                if (_hasStencilSupport)
                {
                    // Clear material: full-screen pass, writes stencil=0, no colour output.
                    _stencilClearMat = new Material(stencilShader) { renderQueue = 4998 };
                    _stencilClearMat.SetFloat("_Stencil",          0f);
                    _stencilClearMat.SetFloat("_StencilComp",      (float)CompareFunction.Always);
                    _stencilClearMat.SetFloat("_StencilOp",        (float)StencilOp.Replace);
                    _stencilClearMat.SetFloat("_StencilWriteMask", 255f);
                    _stencilClearMat.SetFloat("_ColorMask",        0f); // write no colour
                    _stencilClearMat.SetInt("_ZTest",  (int)CompareFunction.Always);
                    _stencilClearMat.SetInt("_ZWrite", 0);

                    // Lens material: world-space pass, writes stencil=1 where the lens is visible.
                    _lensStencilMat = new Material(stencilShader) { renderQueue = 4999 };
                    _lensStencilMat.SetFloat("_Stencil",          1f);
                    _lensStencilMat.SetFloat("_StencilComp",      (float)CompareFunction.Always);
                    _lensStencilMat.SetFloat("_StencilOp",        (float)StencilOp.Replace);
                    _lensStencilMat.SetFloat("_StencilWriteMask", 255f);
                    _lensStencilMat.SetFloat("_ColorMask",        0f);
                    _lensStencilMat.SetInt("_ZTest",  (int)CompareFunction.LessEqual);
                    _lensStencilMat.SetInt("_ZWrite", 0);

                    _occluderStencilMat = new Material(stencilShader) { renderQueue = 4998 };
                    _occluderStencilMat.SetFloat("_Stencil",          2f);
                    _occluderStencilMat.SetFloat("_StencilComp",      (float)CompareFunction.Always);
                    _occluderStencilMat.SetFloat("_StencilOp",        (float)StencilOp.Replace);
                    _occluderStencilMat.SetFloat("_StencilWriteMask", 255f);
                    _occluderStencilMat.SetFloat("_ColorMask",        0f);
                    _occluderStencilMat.SetInt("_ZTest",  (int)CompareFunction.LessEqual);
                    _occluderStencilMat.SetInt("_ZWrite", 0);

                    // Debug overlay: renders a semi-transparent red tint wherever stencil == 1.
                    // Reveals which screen regions are inside the visible lens mask.
                    _stencilDebugMat = new Material(stencilShader)
                    {
                        color       = new Color(1f, 0.1f, 0.1f, 0.55f),
                        renderQueue = 5000
                    };
                    _stencilDebugMat.SetFloat("_Stencil",         1f);
                    _stencilDebugMat.SetFloat("_StencilComp",     (float)CompareFunction.Equal); // only where the lens is visible
                    _stencilDebugMat.SetFloat("_StencilOp",       (float)StencilOp.Keep);
                    _stencilDebugMat.SetFloat("_StencilReadMask", 255f);
                    _stencilDebugMat.SetInt("_ZTest",  (int)CompareFunction.Always);
                    _stencilDebugMat.SetInt("_ZWrite", 0);
                }
            }

            ApplyHorizontalFlip();
        }
    }
}
