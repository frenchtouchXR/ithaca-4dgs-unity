# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
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
