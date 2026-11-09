# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Loading while the application runs: `GaussianSplatRuntimeLoader` builds a `GaussianSplatAsset` from a
  `.ftgsz`, `.ply` or `.spz` file (VeryHigh, High and Medium qualities), on a background thread
  (`PrepareAsync`) then on the main thread (`CreateAsset`).
- `Gaussian4DSequencePlayer`: streams an ITHACA render cut into sequences (share JSON of the ITHACA
  platform), with sound leading the playback, the render's `cadrage`, feet on the floor, and the downloaded
  files deleted on close.
- `Gaussian4DPlayer.SetTemporalData`: changes the temporal data of a player while the application runs.
- `Gaussian4DSequencePlayer`: the cadrage's box (`interieur` / `exterieur`, as a `GaussianCutout` box following
  the player's object), `centerOnOrigin` (model centered on the object, so that it turns and scales around its
  feet), `sons_ogg` tracks preferred when present, UTF-8 byte order mark ignored in front of the JSON.
- `Gaussian4DSequencePlayer`: the platform's scene effects (`effets.trajectoire`, applied in the model's frame
  before the cadrage, as the ITHACA viewer), `modelRadius`; with a cadrage, the floor is y = 0 of the framed
  scene and its origin is the placement point (feet were estimated from the robust bounds, which sank models
  whose point cloud goes under the feet).
- `Gaussian4DSplatRenderer.coverageOnlyCameras`: cameras drawn without sorting the splats, for alpha-only uses
  such as a ground shadow silhouette (same alpha, one sort per frame saved).
- Diagnostics: `_Gaussian4DEyeDebug` (left eye red, right eye blue) and `Gaussian4DSplatRenderer.lastPassInfo`.

### Changed
- The asset data creation moved from the editor creator to `Runtime/GaussianSplatAssetBuilder.cs`, and the
  file readers (`FtgszFileReader`, `PLYFileReader`, `GaussianFileReader`, `SPZFileReader`,
  `TinyJsonParser`) to `Runtime/Utils`, namespace `GaussianSplatting4D.Runtime.Utils`. The creator writes the
  same bytes as before (87 data files of 3 models × 5 qualities compared); only `dataHash` changes for the
  chunked qualities, which used to hash the address of a temporary array. The runtime assembly now
  references Burst and Collections.
- XR Single Pass Instanced / multiview (Meta Quest 3): the 4D URP pass sorts the splats once from the camera,
  then for each eye computes the view data with that eye's view and projection matrices (`EyeView`,
  `_MatrixVPEye` / `_MatrixPEye` in `Splat4DUtilities.compute`) and draws and composites them into the eye's
  slice of the camera target. Previously nothing was drawn in this mode; Multi-pass still works, and
  non-XR rendering is unchanged.
- FreeTimeGS support (`.ftgs.ply`, FreeTimeGsVanilla format v1, as produced and assembled by the
  ITHACA pipeline): automatic detection on import, `*_tmp.bytes` in a dedicated 32-byte layout
  (time, duration, velocity, sequence window), `GaussianSplatAsset.temporalFormat` /
  `temporalFrames`, FreeTimeGS branch in `Splat4DUtilities.compute` (`_TemporalMode`), and
  automatic `Duration` / `Num Frames` on `Gaussian4DPlayer`. Spatial data uses the regular 3DGS
  path, so every quality preset applies.
- Sequence window (`seg_start` / `seg_end`): splats of assembled FreeTimeGS files are only shown
  within their own sequence.
- Creator window: detected type (3DGS / 4D 4C4D / 4D FreeTimeGS) shown under the file size.
- `Gaussian4DSplatRenderer.m_SRGBBlending` (on by default): splats are composited over the scene in sRGB,
  as during training and in the ITHACA browser player. The original linear compositing (aras-p) made
  semi-transparent areas up to ~3x brighter in Linear color space projects (halos and floaters around
  silhouettes). Uncheck to get the previous look back. The 3D `GaussianSplatRenderer` is unchanged.
  The setting is per object: 4D objects are composited in consecutive groups sharing the same mode
  (farthest group first); with a single mode in the scene there is one composite pass, as before.

### Fixed
- FreeTimeGS sequence window: frame f is shown up to f + 1 excluded (ITHACA rule of 23/10/2026, same as the
  browser player and `render_freetimegs.py`). The window is now [first frame − 1 % of a frame, last frame +
  0.99 frame[ instead of half a frame on each side, and the shader test excludes the end (`>=`), so two
  assembled sequences no longer overlap nor leave a gap at their junction.
- Quest 3 (single-pass stereo): the model was cut in one eye; the splats now use their own depth buffer,
  cleared per eye. Splats are drawn with explicit per-view matrices and image size (`EyeView.screenSize`) and
  flipped at composite time when the camera draws straight into the final image (URP global matrices were
  wrong there: model upside down or stretched splats). The composite accumulates alpha, for transparent
  backgrounds (passthrough). Background preparation allocates with `Allocator.Persistent` instead of `TempJob`
  (no more « JobTempAlloc » warnings when it lasts more than 4 frames).
- Quest 3 (Android, Adreno): isolated black pixels around silhouettes, at the edge of nearly transparent
  splats. Both composite shaders (`Gaussian4DComposite`, `GaussianComposite`) now read the splat target in
  full precision and skip invalid or out-of-range values; a valid render is unchanged.
- FreeTimeGS: a file holding only some sequences of a take (e.g. one sequence downloaded on its own) was
  played over the whole take, showing nothing outside its sequence window. `Gaussian4DPlayer` now loops
  over the window of the sequences present in the file (read from `*_tmp.bytes`).
- Player builds (Windows, Android…) failed to compile: `Gaussian4DPlayer` called the editor-only
  `AutoPopulateTemporalDataAsset` from `Awake` and `OnValidate` (CS0103). The calls are now editor-only,
  and a new build step (`Editor/Gaussian4DBuildProcessor.cs`) assigns each player's `*_tmp.bytes`
  in the built scenes, so a sequence whose Temporal Data Asset was left empty still animates in the
  application (a warning is logged when none is found).
- FreeTimeGS: splats are culled when their current opacity (base x temporal) falls below 1/255, like
  the browser player, instead of the 4C4D rule (`temporalCutoff` on the temporal factor alone) that
  made them pop in at 5 % opacity on contours during playback. `temporalCutoff` still applies to 4C4D.
- PLY files with spherical harmonics of degree < 3 (e.g. degree 1) no longer mix color channels
  on import.
- `Export PLY` (3D renderer editor) used the size of the importer's splat structure (with its
  temporal fields) instead of the 62-float layout of the GPU export buffer.

### Unchanged
- Existing 3DGS and 4C4D assets: `temporalFormat` defaults to the previous layout; regenerated
  4C4D assets are byte-identical and render pixel-identical.

## [0.1.0] - 2026-07-22

### Added
- Initial public release: 4D Gaussian Splatting rendering and playback on top of
  aras-p/UnityGaussianSplatting.
- `Gaussian4DSplatRenderer`, `Gaussian4DURPFeature`, `Splat4DUtilities.compute` — temporal
  rendering path, coexisting with the unmodified 3D path.
- `Gaussian4DPlayer` — playback component with play/pause/stop/loop and editor preview
  scrubbing.
- Temporal PLY attribute reading (`t`, `scale_t`, `rot_r_0..3`) and companion `*_tmp.bytes`
  asset generation.
- Auto-population of shader/compute shader references on `Gaussian4DSplatRenderer` when the
  component is added.
- All package code namespaced under `GaussianSplatting4D.*` to avoid conflicts with the
  original aras-p plugin when both are installed side by side.
