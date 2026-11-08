# ITHACA 4D Gaussian Splatting

Real-time 4D Gaussian Splatting rendering and asset generation for Unity (URP), with full
3DGS/4DGS compatibility. A fork of
[aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting), extended
with temporal playback support for 4D PLY files — 4D rotation models (`t`, `scale_t`, `rot_r_*`)
and velocity models (`time`, `log_duration`, `velocity_*`), detected automatically on import —
and for the compact **`.ftgsz`** compressed 4D format (about a quarter of the PLY size).

See the [full project README](https://github.com/frenchtouchXR/ithaca-4dgs-unity#readme) for
setup instructions, screenshots, and usage details.

## Installing this sample

This package can be installed directly via git URL, without cloning the repository: in
`Window > Package Manager`, click **+** → **Install package from git URL...**, and paste:

https://github.com/frenchtouchXR/ithaca-4dgs-unity.git?path=/Packages/com.frenchtouchfactory.ithaca-4dgs


Once installed, import the **Demo Scene** sample from this package's page in Package Manager
(**Samples** section, **Import** button).

The "Demo Scene" sample needs Gaussian Splat assets that are not bundled with the package —
download them here:
[Demo assets (Google Drive)](https://drive.google.com/file/d/1DrsaGNzCFkl63uh62_RSAf4FhUCFAp24/view?usp=sharing).
The archive includes `Coucou40Deg_HD.ply`, a higher-quality 4D take (replacing the previous
Coucou40deg example): create its asset like any other PLY (its temporal type is detected
automatically), then assign it to a `Gaussian4DSplatRenderer` with a `Gaussian4DPlayer`.

## Requirements

- Unity 6000.0 or later
- Universal Render Pipeline (URP)
- DirectX 12 or Vulkan on Windows (DirectX 11 is not supported — GPU sorting requires wave
  intrinsics unavailable on that API). Set this in `Project Settings > Player > Other Settings
  > Graphics APIs for Windows`.
- Both `GaussianSplatURPFeature` and `Gaussian4DURPFeature` must be added to your active URP
  Renderer asset, reachable via `Project Settings > Graphics` → Pipeline Asset → Renderer, or
  by searching `t:UniversalRendererData` in the Project window. The 3D feature must be listed
  before the 4D one.

## What's included

- `Runtime/Gaussian4DSplatRenderer.cs` — 4D renderer, GPU binding of the temporal buffer
- `Runtime/Gaussian4DURPFeature.cs` — URP Renderer Feature for the 4D rendering path
- `Runtime/Gaussian4DPlayer.cs` — playback component (play/pause/stop/loop, editor preview)
- `Runtime/GaussianSplatRenderer.cs`, `GaussianSplatURPFeature.cs` — unmodified 3D path from
  aras-p, for static environments alongside 4D actors
- `Editor/GaussianSplatAssetCreator.cs` — asset creation menu
  (`Tools > Gaussian Splats > Create GaussianSplatAsset`), extended to read temporal PLY
  attributes and emit a companion `*_tmp.bytes` file; accepts `.ply`, `.spz` and `.ftgsz`; the
  detected type (3DGS or 4D) is shown in the window, and every quality preset applies
- `Editor/Utils/FtgszFileReader.cs` — `.ftgsz` reader (ITHACA compressed 4D format v2): decoded
  once into an equivalent PLY, cached in `Library/Ftgsz/`, then imported like any PLY
- `Shaders/Splat4DUtilities.compute` — temporal evaluation (4D rotation conditional covariance, or
  velocity, temporal opacity and sequence window)

`Gaussian4DSplatRenderer` has an `sRGB Blending` option (on by default, per object) that
composites the splats in sRGB like the training renders and the ITHACA browser player; turn it
off for the original aras-p linear compositing. With a velocity-model asset, `Gaussian4DPlayer` sets
`Duration` to 1 (normalized time) and `Num Frames` to the asset's frame count.

### Building an application

Scenes with `Gaussian4DPlayer` build as standalone players like any other scene. During the build,
`Editor/Gaussian4DBuildProcessor.cs` assigns each player's Temporal Data Asset (`<asset>_tmp.bytes`
next to the splat asset) when the field was left empty, since the editor lookup is not available at
runtime; a warning lists any player for which no file was found. Use Vulkan on Android.

On Meta Quest 3 (OpenXR, Vulkan), 4D sequences render in both stereo modes of
`Project Settings > XR Plug-in Management > OpenXR > Android > Render Mode`. With Single Pass Instanced
(multiview, recommended), the 4D pass sorts the splats once from the camera, then computes the view data and
draws them for each eye with that eye's matrices, compositing into the eye's slice of the camera target; the
rest of the scene is rendered once for both eyes. Hello ITHACA (197,625 splats) runs at 72 fps (the headset's
refresh rate, 10 s average) on a Quest 3 this way, sorting every frame. With the new Input System, the camera's `TrackedPoseDriver` needs bindings to
`<XRHMD>/centerEyePosition` and `<XRHMD>/centerEyeRotation`.

A FreeTimeGS file holding a single sequence of a take (as the ITHACA platform delivers them for headsets)
plays and loops over that sequence only.

## License

MIT. See [LICENSE.md](LICENSE.md). This package includes and extends code originally authored
by Aras Pranckevičius (© and contributors, MIT license).
