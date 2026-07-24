# ITHACA 4D Gaussian Splatting

Real-time 4D Gaussian Splatting rendering and asset generation for Unity (URP), with full
3DGS/4DGS compatibility. A fork of
[aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting), extended
with temporal playback support.

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
- `Editor/GaussianSplatAssetCreator.cs` — asset creation menu, extended to read temporal PLY
  attributes and emit a companion `*_tmp.bytes` file

## License

MIT. See [LICENSE.md](LICENSE.md). This package includes and extends code originally authored
by Aras Pranckevičius (© and contributors, MIT license).
