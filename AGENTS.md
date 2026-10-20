# MakeItColor: notes for coding agents

Shared project memory for coding assistants. `CLAUDE.md` and `GEMINI.md` load this file. Keep it up to date when
something important changes.

## What this is
MakeItColor is a free Windows app (C#/WPF, .NET 10) that colours black-and-white photos and videos on the PC with the
MakeItColor model (DDColor, ConvNeXt-L, Apache 2.0; https://huggingface.co/muhammadnoman76/makeitcolor), on the NPU,
then the GPU, then the CPU. It also restores them: brightness/contrast fix (`Core/Tone.cs`), an enhancer model
(Real-ESRGAN compact, BSD-3) for photos and video frames, and a light CPU clean-up for videos.
- Owner: Muhammad Noman (https://github.com/MuhammadNoman76/make-it-color).
- Website: https://www.nomanshafiq.com/makeitcolor/ (download page) and https://www.nomanshafiq.com/blog/makeitcolor/
  (field note). The site lives in a separate repo: `D:\Work\nomanshafiq-portfolio`.

## Layout
- `src/MakeItColor/`: the app.
  - `Core/ColorMath.cs`, `Colorizer.cs`: Lab maths, 512 input, bilinear a/b upscale, guided filter. They must match
    `tools/model/pipeline.py` (the reference); `Colorizer` reuses buffers, so one instance per thread.
  - `Core/Ai/`: `Hardware.cs` (Windows ML providers, device order, session options), `ModelStore.cs` (download +
    SHA-256), `ColorModel.cs` (session + correctness check), `Engine.cs` (device choice, background NPU compile, swap).
  - `Core/Video/`: `FFmpeg.cs` (probe, decoder, encoder), `SteadyColors.cs` (a/b temporal smoothing),
    `SteadyDetail.cs` (enhanced detail temporal smoothing), `VideoJob.cs` (5 stages: decode; tone + model input +
    light clean-up; enhance; colour; finish + encode).
  - `Core/Ai/EnhanceModel.cs` (enhancer session, check, `EnhanceEngine`: Auto = GPU, NPU, CPU), `Core/Enhancer.cs`
    (tiles 256 with 24 px overlap, each 4x tile resampled straight to the output size; must match
    `tools/model/enhance.py` Tiles()), `Core/PhotoPrep.cs` (tone + enhance + output size; prepared photos kept in
    `%TEMP%\MakeItColor\<pid>`), `Core/Tone.cs` (levels, `VideoTone` flicker, `LightCleanup`).
  - `Core/Headless.cs` (`--colorize`, `--prepare`), `Views/Capture.cs` (`--capture` screenshots, off-screen),
    `Core/Updater.cs` + `UpdateService.cs`, `Views/` (main window, settings), `Controls/CompareView.cs`.
- `tools/model/`: `convert.py` (PyTorch checkpoint → ONNX fp32/fp16 + reference outputs), `pipeline.py`, `ddcolor/`
  (ModelScope's DDColor code, Apache 2.0), `enhance.py` (Real-ESRGAN checkpoints → enhancer ONNX + check files).
- `src/MakeItColor/Models/`: the enhancer ONNX files (2.4 MB fp16, 4.9 MB fp32), shipped in the app's `models` folder.
- `installer/MakeItColor.iss`, `build.ps1`, `tools/get-ffmpeg.ps1`, `vendor/ffmpeg/` (binaries git-ignored).
- Installers are not in the repo: they are assets of the GitHub release `v<version>` (the x64 one is over GitHub's
  100 MB limit for repo files; release assets may be up to 2 GB). Download links and the update feed point there.
- `.work/` (git-ignored): Python venvs, checkpoint, test images and videos, scratch scripts.

## Model files
- GitHub release `models-1` on this repo: `makeitcolor-fp16.onnx` (NPU/GPU, 456,392,880 bytes) and
  `makeitcolor-fp32.onnx` (CPU, 912,062,353 bytes). Hashes in `ModelStore.cs`. Downloaded to
  `%LOCALAPPDATA%\MakeItColor\models`. `MAKEITCOLOR_MODELS=<folder>` uses local files.
- Release `ffmpeg-9.0`: the two pinned BtbN LGPL zips (`vendor/ffmpeg/SOURCE.txt`).
- Export rules found the hard way (don't undo them):
  - ConvNeXt's channels-first LayerNorm must be exported as a LayerNormalization op (`convert.py` patches it). The
    hand-written version squares values up to 2e7, past float16's 65504: GPUs then give wrong colours.
  - Pure fp16 (`keep_io_types=True`, no block list). A mixed fp16/fp32 file broke the Intel NPU.
  - DirectML: disable ORT's `MatMulAddFusion` (`Hardware.Options`); it gives wrong fp32 results on DML.
  - The ORT CPU provider can't run the fp16 file well; the CPU gets fp32.
- Every device is checked on open against `Assets/check-input.bin` / `check-expected.bin` (8×8 block means of a/b,
  rms ≤ 1.5); a device that fails is skipped. Regenerate both if the model changes.

## Enhancer rules (found by testing, see docs/QUALITY.md)
- The colour model must never see the enhanced picture (it changed the colours). It sees the tone-fixed picture;
  the enhanced picture only gives the L channel (detail) to `Colorizer.Finish`.
- Real-ESRGAN default blend: 50 % realesr-general-x4v3 + 50 % wdn weights, baked into one file.
- Video Full mode works on a copy of at most 800 px (`VideoJob.EnhanceWorkSize`), then back to the original size.
- Photo output at most 24 MP; enhancer input at most 16 MP.

## Engine behaviour
- Auto: NPU, then the best GPU (discrete first, vendor provider before DirectML), then the CPU.
- The first NPU compile takes 4–5 minutes (OpenVINO cache in `%LOCALAPPDATA%\MakeItColor\openvino-cache`). In Auto the
  app runs on the next device meanwhile and swaps when the NPU is ready. `prepared.txt` remembers "ok"/"failed".

## Build and release
1. Bump `<Version>` in `src/MakeItColor/MakeItColor.csproj`.
2. `powershell -ExecutionPolicy Bypass -File build.ps1 -Version X.Y.Z` (x64 and arm64). .NET SDK:
   `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`; Inno Setup 6: `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.
3. Commit and push, then create the GitHub release `vX.Y.Z` with `dist\*.exe` and `dist\SHA256SUMS.txt` as assets
   (REST API; there is no `gh` on the owner's PC). Do this first, so the download links exist.
5. In the website repo: update `src/data/makeitcolor.ts` and `public/updates/makeitcolor.json` (version, released,
   notes, per-edition url/sha256/size), run `npm run verify`, commit and push.

## Automatic updates
- Feed `https://www.nomanshafiq.com/updates/makeitcolor.json` (app id `MakeItColor`), read about 10 s after start,
  then at most once every 24 h. `MAKEITCOLOR_UPDATE_URL` overrides it.
- Update now downloads the edition's installer to `%TEMP%\MakeItColor-Update`, checks size and SHA-256, runs it with
  `/VERYSILENT /RELAUNCH=1` (+ `/CURRENTUSER` or `/ALLUSERS`, `/DIR`) and exits.

## Testing on the owner's PC (be careful)
- Back up `%APPDATA%\MakeItColor` and `%LOCALAPPDATA%\MakeItColor` (models and the NPU cache, hundreds of MB) before
  tests that could change them, and restore them afterwards.
- Never open windows over a full-screen app. `--capture` renders off-screen; `--colorize` has no window.
- Test installers per-user in a scratch folder (`/CURRENTUSER /DIR=… /TASKS=""`), then uninstall.

## Git and writing rules
- Commit as `Muhammad Noman <muhammadnomanshafiq76@gmail.com>`.
- Never add AI attribution: no `Co-Authored-By` trailer and no "Generated with …" line. Don't mention AI coding
  tools in commit messages, README or release notes (the app's own AI model is fine to describe).
- Ask before pushing unless the owner asked for it in the current task.
- Read and write text files as UTF-8.
- Public claims (README, website, videos) must match the code and the measurements in `docs/QUALITY.md`. Say what is
  untested: ARM64 hardware, AMD/Qualcomm NPUs, NVIDIA/AMD GPUs.
