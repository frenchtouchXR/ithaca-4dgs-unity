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
- `Runtime/Utils/FtgszFileReader.cs` — `.ftgsz` reader (ITHACA compressed 4D format v2): decoded
  once into an equivalent PLY, cached in `Library/Ftgsz/` in the editor, then imported like any PLY
  (in a player, the decoded PLY is deleted as soon as it has been read)
- `Runtime/GaussianSplatAssetBuilder.cs` — the asset data (Morton order, chunks, positions, rotations
  and scales, colors, SH, temporal data), shared by the editor creator and runtime loading
- `Runtime/GaussianSplatRuntimeLoader.cs` — loads a `.ftgsz`, `.ply` or `.spz` while the application runs
- `Runtime/Gaussian4DSequencePlayer.cs` — streams an ITHACA render cut into sequences, with sound
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

The splats of each view are drawn into an intermediate image with the orientation of a texture, with the view's
own matrices and image size (`EyeView`), then composited into the camera target, flipped when the camera draws
straight into the final image (URP Intermediate Texture « Auto »). In single-pass stereo, the splats get their own
depth buffer, cleared for each eye: the scene's depth slice of an eye could not be used as an attachment (the
right eye was tested against the left eye's depth, cutting the model in one eye). The composite accumulates
alpha (opacity, not a²), so that the splats show correctly over a transparent background (Quest passthrough).
For Quest passthrough with Unity OpenXR: Meta, enable the Session, Camera and Composition Layers OpenXR
features, disable HDR and keep the Intermediate Texture at « Always ». A shader global,
`_Gaussian4DEyeDebug = 1`, tints the splats red in the left eye and blue in the right eye (stereo check), and
`Gaussian4DSplatRenderer.lastPassInfo` describes the last 4D pass (views, sizes, target, flip).

A FreeTimeGS file holding a single sequence of a take (as the ITHACA platform delivers them for headsets)
plays and loops over that sequence only.

### Loading models while the application runs

`GaussianSplatRuntimeLoader` turns a `.ftgsz`, `.ply` or `.spz` file (3DGS, 4C4D or FreeTimeGS) into a
`GaussianSplatAsset` without the editor. It goes through the same steps as the creator window, with the
same code (`GaussianSplatAssetBuilder`), for the VeryHigh, High and Medium qualities (SH clustering and BC7
colors stay editor-only):

```csharp
var prepared = await GaussianSplatRuntimeLoader.PrepareAsync(path, GaussianSplatRuntimeLoader.Quality.VeryHigh); // background thread
var asset = GaussianSplatRuntimeLoader.CreateAsset(prepared);                                                       // main thread
```

Prepared on the main thread, the data is byte-identical to the editor's. On a background thread, Unity's job
system is not available, so the same jobs run directly in C#; floats then differ from the Burst-compiled editor
in the last bit at most (rotations: at most one 10-bit step), which is invisible. A sequence of about 200,000
splats is prepared in 0.3 s on a desktop PC and 0.5 s on a Quest 3.

### Streaming an ITHACA render

`Gaussian4DSequencePlayer` plays an ITHACA render cut into `.ftgsz` sequences, as described by the share JSON
of the ITHACA platform (`total`, `fps`, `sequences` with `url`, `premiere`, `images`, `sons`, `decalage_son`,
`cadrage`). URLs may be `http(s)` links or local paths. Each sequence is downloaded into the application's
temporary cache, prepared on a background thread while the previous one plays, and shown in one of two slots
(the current sequence and the next one, uploaded to the GPU shortly before its first frame). The sound leads:
all tracks start together on the audio clock, offset by `decalage_son`, and the splats follow; if the next
sequence is not ready in time, picture and sound wait. Unity reads mp3, ogg and wav (not m4a on Android): the
platform provides an Ogg Vorbis copy of every track (`sons_ogg`).

Set `template` to a disabled scene object with `Gaussian4DSplatRenderer` and `Gaussian4DPlayer` already set up
(shaders, blending); it is cloned into the two slots. The render's `cadrage` (position, rotation `Rz Ry Rx`,
scale, around the robust center of the first sequence) is applied as on the platform's viewer, with its box:
`interieur` keeps only the splats inside it, `exterieur` only those outside (a `GaussianCutout` box, in the
scene axes, which does not follow the cadrage's scale, as on the platform). Turn the player's object by 180°
around X (Y down in ITHACA takes, Y up in Unity). With a cadrage, the floor is the plane y = 0 of the framed
scene, where its owner put the feet, and the origin of the framed scene is the player's object (the placement
point), as on the platform's viewer and AR. Without a cadrage, `feetOnFloor` and `centerOnOrigin` estimate the
feet and the center from the model's robust bounds. Scaling or turning the object (e.g. with the controllers)
keeps the model on the floor; the box moves with it. The scene effects of the platform (`effets`) are read
too: their `trajectoire` (displacement of the character in the model's frame, interpolated between key frames,
before the cadrage) keeps for instance an AI-generated character on the floor. `modelRadius` gives the
horizontal half-size of the framed model (floor shadow, placement ring).

The share JSON of the platform also gives `sons_ogg`, Ogg Vorbis copies of the tracks that Unity reads on
Android: they are used when present. `Open(json)` ignores a UTF-8 byte order mark in front of the JSON. Downloaded files are deleted when the render is closed, unless
`keepDownloads` is set. Some sites refuse Unity's default user agent: `userAgent` is sent with every request.
On a Quest 3, the Thriller Cat (14 sequences of about 200,000 splats, 2 s each) plays with its sound in sync,
each sequence being prepared in 0.5 s.

## License

MIT. See [LICENSE.md](LICENSE.md). This package includes and extends code originally authored
by Aras Pranckevičius (© and contributors, MIT license).
