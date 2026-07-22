# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
