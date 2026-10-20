"""The photo enhancer: Real-ESRGAN's compact general model (realesr-general-x4v3, SRVGGNetCompact, BSD-3-Clause,
https://github.com/xinntao/Real-ESRGAN) converted to ONNX for MakeItColor.

    python enhance.py export <checkpoints folder> <out folder>   # makeitcolor-enhance-fp32/fp16.onnx + check files
    python enhance.py tiles <image> [pad...]                    # tiled vs whole-picture difference

Real-ESRGAN's default for this model blends the weights of the normal and the "weak denoise" (wdn) checkpoints 50/50
(--denoise_strength 0.5); the blend is baked into one file. Input: RGB 0..1, [1, 3, TILE, TILE]; output 4x larger.
The app runs it tile by tile (fixed size, which NPUs need) and stitches the tiles: see Tiles() below and
src/MakeItColor/Core/Enhancer.cs, which must match.
"""
import sys, json, hashlib
from pathlib import Path
import numpy as np
import torch
from torch import nn
from torch.nn import functional as F

TILE = 256   # model input (pixels of the original); output 1024
PAD = 24     # overlap on each side (24: tiled = whole picture to 87 dB, max 2/255 on example1)
SCALE = 4
DENOISE = 0.5


class SRVGGNetCompact(nn.Module):
    """From BasicSR, basicsr/archs/srvgg_arch.py (Apache-2.0, https://github.com/XPixelGroup/BasicSR):
    64 features, 32 convolutions, PReLU, x4."""

    def __init__(self, num_feat=64, num_conv=32, upscale=4):
        super().__init__()
        self.upscale = upscale
        self.body = nn.ModuleList([nn.Conv2d(3, num_feat, 3, 1, 1), nn.PReLU(num_feat)])
        for _ in range(num_conv):
            self.body.append(nn.Conv2d(num_feat, num_feat, 3, 1, 1))
            self.body.append(nn.PReLU(num_feat))
        self.body.append(nn.Conv2d(num_feat, 3 * upscale * upscale, 3, 1, 1))
        self.upsampler = nn.PixelShuffle(upscale)

    def forward(self, x):
        out = x
        for layer in self.body:
            out = layer(out)
        return self.upsampler(out) + F.interpolate(x, scale_factor=self.upscale, mode='nearest')


def load(folder, denoise=DENOISE):
    def params(name):
        d = torch.load(Path(folder) / name, map_location='cpu', weights_only=True)
        return d['params_ema'] if 'params_ema' in d else d['params']
    a, b = params('realesr-general-x4v3.pth'), params('realesr-general-wdn-x4v3.pth')
    net = SRVGGNetCompact()
    net.load_state_dict({k: denoise * a[k] + (1 - denoise) * b[k] for k in a})
    return net.eval()


def whole(net, rgb):
    """Reference: the whole picture at once (float32 RGB 0..1, HxWx3) -> 4x, clipped to 0..1."""
    with torch.no_grad():
        x = torch.from_numpy(np.ascontiguousarray(rgb.transpose(2, 0, 1)))[None]
        return net(x)[0].clamp(0, 1).numpy().transpose(1, 2, 0)


def tile_starts(n, tile=TILE, pad=PAD):
    """Tile origins along one axis, overlapping by at least 2 * pad. The last tile is moved inside the picture (so it
    sees the picture's real border, like the whole-picture run) instead of being padded."""
    if n <= tile:
        return [0]
    starts, s = [], 0
    while s + tile < n:
        starts.append(s)
        s += tile - 2 * pad
    starts.append(n - tile)
    return starts


def kept(starts, n, tile=TILE):
    """For each tile, the pixels [lo, hi) whose result comes from it: the overlaps are split in the middle."""
    bounds = [0] + [(starts[i] + starts[i - 1] + tile) // 2 for i in range(1, len(starts))] + [n]
    return list(zip(bounds[:-1], bounds[1:]))


def Tiles(run, rgb, tile=TILE, pad=PAD):
    """Tiled run: run(x [3, tile, tile]) -> [3, 4 tile, 4 tile]. Pictures smaller than a tile are edge-padded."""
    h, w, _ = rgb.shape
    ph, pw = max(h, tile), max(w, tile)
    src = np.pad(rgb, ((0, ph - h), (0, pw - w), (0, 0)), mode='edge') if (ph, pw) != (h, w) else rgb
    out = np.zeros((ph * SCALE, pw * SCALE, 3), np.float32)
    ys, xs = tile_starts(ph, tile, pad), tile_starts(pw, tile, pad)
    ky, kx = kept(ys, ph, tile), kept(xs, pw, tile)
    for y, (y0, y1) in zip(ys, ky):
        for x, (x0, x1) in zip(xs, kx):
            r = run(np.ascontiguousarray(src[y:y + tile, x:x + tile].transpose(2, 0, 1)))
            r = np.clip(r, 0, 1).transpose(1, 2, 0)
            out[y0 * SCALE:y1 * SCALE, x0 * SCALE:x1 * SCALE] = r[(y0 - y) * SCALE:(y1 - y) * SCALE, (x0 - x) * SCALE:(x1 - x) * SCALE]
    return out[:h * SCALE, :w * SCALE]


def psnr(a, b):
    mse = float(np.mean((a.astype(np.float64) - b) ** 2))
    return 99.0 if mse == 0 else 10 * np.log10(1 / mse)


def export(folder, out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    net = load(folder)
    fp32 = out / 'makeitcolor-enhance-fp32.onnx'
    torch.onnx.export(net, torch.zeros(1, 3, TILE, TILE), str(fp32), input_names=['input'], output_names=['output'],
                      opset_version=17, dynamo=False)
    import onnx
    from onnxconverter_common import float16
    fp16 = out / 'makeitcolor-enhance-fp16.onnx'
    onnx.save(float16.convert_float_to_float16(onnx.load(str(fp32)), keep_io_types=True), str(fp16))

    # Check data for the app: a fixed 256x256 gray test tile and the expected output, as 8x8 block means (128x128x3).
    import cv2
    img = cv2.imread(str(Path(__file__).resolve().parents[2] / '.work' / 'model' / 'example1.jpg'), cv2.IMREAD_GRAYSCALE)
    tile = img[300:300 + TILE, 600:600 + TILE]
    (out / 'enhance-check-input.bin').write_bytes(tile.tobytes())
    x = np.repeat((tile.astype(np.float32) / 255)[None], 3, 0)
    with torch.no_grad():
        y = net(torch.from_numpy(x)[None])[0].clamp(0, 1).numpy()
    blocks = y.reshape(3, TILE * SCALE // 8, 8, TILE * SCALE // 8, 8).mean(axis=(2, 4)).astype(np.float32)
    (out / 'enhance-check-expected.bin').write_bytes(blocks.tobytes())

    import onnxruntime as ort
    report = {}
    for f in (fp32, fp16):
        s = ort.InferenceSession(str(f), providers=['CPUExecutionProvider'])
        r = s.run(None, {'input': x[None]})[0][0].clip(0, 1)
        report[f.name] = {'bytes': f.stat().st_size, 'sha256': hashlib.sha256(f.read_bytes()).hexdigest(),
                          'psnr_vs_torch': round(psnr(r, y), 2), 'max_diff': float(np.abs(r - y).max())}
    (out / 'enhance-report.json').write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))


def tiles(image, pads):
    import cv2
    net = load(Path(__file__).resolve().parents[2] / '.work' / 'esrgan' / 'dl')
    img = cv2.imread(image, cv2.IMREAD_COLOR)[:, :, ::-1].astype(np.float32) / 255
    ref = whole(net, img)

    def run(x):
        with torch.no_grad():
            return net(torch.from_numpy(x)[None])[0].numpy()
    for p in pads:
        t = Tiles(run, img, TILE, p)
        print(f'pad {p}: PSNR {psnr(t, ref):.2f} dB, max diff {np.abs(t - ref).max() * 255:.2f}/255')


if __name__ == '__main__':
    if sys.argv[1] == 'export':
        export(sys.argv[2], sys.argv[3])
    elif sys.argv[1] == 'tiles':
        tiles(sys.argv[2], [int(p) for p in sys.argv[3:]] or [PAD])
