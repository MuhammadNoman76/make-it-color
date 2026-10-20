# MakeItColor: how it was checked

Every number here was measured on one laptop: Intel Core Ultra 5 135U (Intel AI Boost NPU, Intel Graphics),
Windows 11, Windows ML 2.4.89 with the Intel OpenVINO execution provider Windows downloaded. Scripts are in
`tools/model/`; scratch scripts and test files lived in `.work/` (not in the repository).

## 1. The colour model: export
Source: the MakeItColor checkpoint (`pytorch_model.pt`, 911,950,059 bytes, the same file as in the Hugging Face model
and Space), DDColor ConvNeXt-L, run by ModelScope's DDColor code (`tools/model/ddcolor/`, Apache 2.0).

`tools/model/convert.py` exports ONNX (opset 17, input 1 × 3 × 512 × 512):
- Spectral norm baked into 8 layers (max difference 0.0).
- **ConvNeXt's channels-first LayerNorm exported as one LayerNormalization operator.** The original hand-written
  version squares values up to about 2 × 10^7; float16 tops out at 65,504, so on GPUs the float16 file gave wrong
  colours. Same maths, no overflow.
- float16 file made with `keep_io_types=True` and no block list. A mixed float16/float32 file (the first fix for the
  overflow) gave correct colours on GPUs but wrong ones on the Intel NPU (check difference 15), so it was dropped.

| Check (vs ModelScope's PyTorch pipeline) | example1 1500 × 1000 | kisswar 2912 × 3000 | classroom 640 × 480 |
|---|---|---|---|
| float32 ONNX, a/b max difference | 0.0006 | 0.0009 | 0.0009 |
| float32 ONNX, PSNR of the colour picture | 88.4 dB | 90.7 dB | 87.1 dB |
| float16 ONNX on the CPU, PSNR | 65.3 dB | 63.9 dB | 61.9 dB |

## 2. The colour pipeline around the model
`tools/model/pipeline.py` is the reference the app (`Core/ColorMath.cs`, `Core/Colorizer.cs`) must match. Changes
from ModelScope's pipeline, kept because they measured better on the 24 Kodak images (gray versions coloured and
compared with the real colours):
- antialiased (PIL-style triangle) shrink to 512 instead of OpenCV's plain bilinear;
- bilinear a/b enlargement with half-pixel centres instead of nearest neighbour (no 2-pixel blocks of colour);
- a guided filter steered by L (radius 2 × round(longest side / 512), eps 0.001) that lines the colour up with the
  edges.

| Kodak 24 | PSNR | ΔE (CIE76) |
|---|---|---|
| ModelScope pipeline | 21.23 dB | 18.71 |
| MakeItColor pipeline | 21.92 dB | 17.61 |

(Absolute numbers are low for every colouriser: the true colour of a shirt or a car can't be known from gray.)

## 3. The app against the reference, per chip
App output (`--colorize ... --tone off --enhance off`) against `pipeline.py` with the float32 model:

| Chip | Model file | PSNR | Max difference |
|---|---|---|---|
| CPU | float32 | 88–100 dB | 1 / 255 |
| NPU (OpenVINO) | float16 | 51–57 dB | |
| GPU (OpenVINO) | float16 | 54–58 dB | |

**Bug found: DirectML.** On DirectML the float32 model gave wrong colours (check difference 15). Bisecting ONNX
Runtime's graph optimisations showed the MatMul + Add → Gemm fusion was the cause; the app turns that one fusion off for
DirectML (`Hardware.Options`).

**The check.** Every chip runs a known picture (`Assets/check-input.bin`) when it is loaded; the result, averaged
over 8 × 8 blocks, must be within 1.5 (RMS, a/b units) of the float32 CPU result (`check-expected.bin`). A chip or
driver that computes something differently is skipped. The enhancer has the same check (`enhance-check-*.bin`,
limit 0.01 in 0..1 units).

## 4. Speed of the colour model (one 512 × 512 run)
| Chip | Path | Time |
|---|---|---|
| NPU | OpenVINO, float16 | 414–417 ms |
| GPU | OpenVINO, float16 | 513–541 ms |
| GPU | DirectML, float16 | 773 ms |
| GPU | DirectML, float32 | 1,676 ms |
| CPU | ONNX Runtime, float32 | 1.8–2.35 s |

The first NPU compile took 230–320 s; OpenVINO keeps the result, later loads take about 3 s. In Auto the app works on
the next chip meanwhile and switches when the NPU is ready.

## 5. Steady colours in videos
`Core/Video/SteadyColors.cs` keeps a/b from the previous frame where the (blurred) L is unchanged, after removing a
global brightness change; a scene cut (mean change above 10 L units) starts fresh.

Measured with `.work/flicker.py` on a 20 s, 600-frame clip: mean |Δa| + |Δb| between frames over pixels whose L
didn't change.

| | Mean | 90th percentile |
|---|---|---|
| Steady colours off | 5.40 | 8.65 |
| Steady colours on | 2.76 | 4.53 |

## 6. The enhancer
Real-ESRGAN's compact general model (`realesr-general-x4v3`, 1.2 M parameters), blended 50/50 with its weak-denoise
twin as Real-ESRGAN does by default, exported by `tools/model/enhance.py` with a fixed 256 × 256 input (4× output).

| Check | Result |
|---|---|
| float32 ONNX vs PyTorch | 128 dB (max 0.00007) |
| float16 ONNX vs PyTorch (CPU) | 75.5 dB (max 0.009) |
| Tiled vs whole picture, overlap 8 / 16 / 24 px | 59.2 / 68.8 / **87.0 dB** (max 23.9 / 12.3 / 2.2 of 255) |
| App on the CPU vs the Python tiled reference (4×, classroom) | 101 dB, max 1 / 255 |
| App on the NPU vs the reference | 60.8 dB, max 6 / 255 |
| App on the GPU vs the reference | 53.5 dB, max 9 / 255 |

Speed per tile: GPU 66–75 ms, NPU 131 ms (measured while the NPU was also colouring a video), CPU 1.0 s. First NPU
compile about 14 s. A 1500 × 1000 photo (35 tiles) enhanced to 3000 × 2000 and coloured: about 3 s with the enhancer
on the graphics card, 49 s on the CPU alone.

Choices, each from a test:
- **The colour model never sees the enhanced picture.** In a first version it did, and the colours changed: a bluish
  patch on the rocks of a Great Wall photo, a red patch on a film title card. Now the colour model sees the photo
  with only the brightness fixed (what it was tested on), and the enhanced picture gives the L channel (the detail).
- **Auto runs the enhancer on the graphics card first** (75 ms a tile vs 131 ms on the busy NPU), so in videos the
  enhancer (GPU) and the colour model (NPU) work at the same time.
- **Videos: the enhancer works on a copy of at most 800 pixels.** On a 1280 × 720 VHS-era film transfer, 960 pixels
  was clean but soft, 640 sharpest but painted-looking on faces; 800 was the balance (8 tiles a frame). The result is
  resampled back to the original size.
- **Steady detail** (`Core/Video/SteadyDetail.cs`): the enhancer works on each frame alone and fine detail shimmered
  a little. Where the source didn't change (3 × 3 mean of luma), the enhanced pixel is blended with the previous result.

Frame-to-frame change over pixels that are static in the original (`.work/flicker2.py`, 100 frames of the film):

| Version | L change | a/b change |
|---|---|---|
| The original (gray) | 0.64 | |
| Coloured, no clean-up, no brightness fix | 0.70 | 5.11 |
| Light clean-up + brightness fix | 0.77 | 5.32 |
| Full clean-up, no steady detail | 0.89 | 5.38 |
| Full clean-up with steady detail | 0.81 | 5.30 |

Video speed on that film (1280 × 720, 25 fps): Full about 1.5 frames a second, Light about 2.2.

## 7. Brightness and contrast
`Core/Tone.cs`: the levels at 0.5 % and 99.5 % are stretched toward 4 and 250 (8-bit), at most 1.8×, never less
contrast. A first version also pulled the middle gray toward 118; it darkened a well-exposed elephant photo, so it was
removed: a picture that already uses the whole range is now left alone. In videos the levels follow each shot slowly
(4 % a frame), and each frame's brightness is matched to the mean of the 3 frames before and after it in the same shot
(gain 0.85–1.18), which takes out film flicker without dimming fades.

## 8. Video clean-up options that were tried
On 100 frames of 720p (CPU time per frame): FFmpeg `atadenoise` 1 ms, `fftdnoiz` 74 ms, `nlmeans` 180 ms (`hqdn3d` is
GPL and not in the LGPL build). On the film they changed almost nothing: its problem is blur and compression blocks,
not grain. The enhancer at reduced size fixed both, so it became Full; Light (grain filter + halo-free sharpening in
`Tone.cs`) stays for PCs without an NPU or graphics card fast enough.

## Not tested
- ARM64 hardware (Qualcomm Snapdragon X and its NPU).
- AMD and Qualcomm NPUs; NVIDIA and AMD graphics cards (DirectML or their own providers).
- Windows 10 machines (built for 1903+, run only on Windows 11).
