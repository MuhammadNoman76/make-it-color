"""
MakeItColor's colorization pipeline in plain numpy: the reference the app (src/MakeItColor/Core/Colorizer.cs) must match.

Differences from ModelScope's DDColor pipeline (chosen after side-by-side tests, see docs/QUALITY.md):
- the 512x512 model input is made with an antialiased resize (PIL-style triangle filter), not cv2.INTER_LINEAR, so large
  pictures and film grain don't alias into the model's input;
- the predicted a/b channels are upscaled bilinearly and then refined with a guided filter that follows the full-size L
  channel, instead of nearest-neighbour: no colour blocks, specks or fringes at edges.
Lab conversions follow OpenCV's float formulas (sRGB, D65), without its lookup tables.

  python pipeline.py <model.onnx> <out_dir> images...
"""
import sys
from pathlib import Path

import numpy as np

SIZE = 512

# OpenCV's sRGB <-> XYZ matrices and D65 white point (color_lab.cpp).
RGB2XYZ = np.array([[0.412453, 0.357580, 0.180423],
                    [0.212671, 0.715160, 0.072169],
                    [0.019334, 0.119193, 0.950227]], np.float64)
XYZ2RGB = np.array([[3.240479, -1.53715, -0.498535],
                    [-0.969256, 1.875991, 0.041556],
                    [0.055648, -0.204043, 1.057311]], np.float64)
WHITE = np.array([0.950456, 1.0, 1.088754])


def srgb_to_linear(v):
    return np.where(v <= 0.04045, v / 12.92, ((v + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(v):
    return np.where(v <= 0.0031308, v * 12.92, 1.055 * np.power(np.maximum(v, 0), 1 / 2.4) - 0.055)


def bgr_to_lab(bgr):
    """float BGR 0..1 -> L 0..100, a, b (OpenCV float scale)."""
    rgb = srgb_to_linear(bgr[..., ::-1].astype(np.float64))
    xyz = rgb @ RGB2XYZ.T / WHITE
    f = np.where(xyz > 0.008856, np.cbrt(xyz), 7.787 * xyz + 16 / 116)
    y = xyz[..., 1]
    L = np.where(y > 0.008856, 116 * f[..., 1] - 16, 903.3 * y)
    a = 500 * (f[..., 0] - f[..., 1])
    b = 200 * (f[..., 1] - f[..., 2])
    return np.stack([L, a, b], -1)


def lab_to_bgr(lab):
    L, a, b = lab[..., 0], lab[..., 1], lab[..., 2]
    low = L <= 7.9996248
    y = np.where(low, L / 903.3, ((L + 16) / 116) ** 3)
    fy = np.where(low, 7.787 * y + 16 / 116, (L + 16) / 116)
    fx = fy + a / 500
    fz = fy - b / 200
    inv = lambda t: np.where(t <= 0.20689657, (t - 16 / 116) / 7.787, t ** 3)
    xyz = np.stack([inv(fx), y, inv(fz)], -1) * WHITE
    rgb = np.clip(xyz @ XYZ2RGB.T, 0, 1)
    return linear_to_srgb(rgb)[..., ::-1]


def resize_weights(src, dst):
    """PIL-style antialiased triangle filter: a [dst, src] weight matrix."""
    scale = src / dst
    support = max(scale, 1.0)
    w = np.zeros((dst, src), np.float64)
    for i in range(dst):
        center = (i + 0.5) * scale
        lo = max(int(np.floor(center - support)), 0)
        hi = min(int(np.ceil(center + support)), src)
        j = np.arange(lo, hi)
        t = np.maximum(0.0, 1.0 - np.abs((j + 0.5 - center) / support))
        w[i, lo:hi] = t / t.sum()
    return w


def resize(img, width, height):
    """Separable: rows first, then columns (two matrix products)."""
    h, w, c = img.shape
    rows = (resize_weights(h, height) @ img.reshape(h, w * c)).reshape(height, w, c)
    return (rows.transpose(0, 2, 1) @ resize_weights(w, width).T).transpose(0, 2, 1)


def upscale_bilinear(ab, width, height):
    """Bilinear, half-pixel centres (like F.interpolate(..., align_corners=False)) from [S, S, 2] to [H, W, 2]."""
    s = ab.shape[0]

    def axis(n_out):
        pos = np.clip((np.arange(n_out) + 0.5) * s / n_out - 0.5, 0, s - 1)
        i0 = np.floor(pos).astype(int)
        i1 = np.minimum(i0 + 1, s - 1)
        return i0, i1, pos - i0
    y0, y1, fy = axis(height)
    x0, x1, fx = axis(width)
    top = ab[y0][:, x0] * (1 - fx)[None, :, None] + ab[y0][:, x1] * fx[None, :, None]
    bot = ab[y1][:, x0] * (1 - fx)[None, :, None] + ab[y1][:, x1] * fx[None, :, None]
    return top * (1 - fy)[:, None, None] + bot * fy[:, None, None]


def box(x, r):
    """Mean over a (2r+1)^2 window with the window clipped at the borders (count-normalised)."""
    def along(v, axis):
        c = np.cumsum(np.pad(v, [(1, 0) if k == axis else (0, 0) for k in range(v.ndim)]), axis=axis)
        n = v.shape[axis]
        hi = np.minimum(np.arange(n) + r + 1, n)
        lo = np.maximum(np.arange(n) - r, 0)
        s = np.take(c, hi, axis=axis) - np.take(c, lo, axis=axis)
        shape = [1] * v.ndim
        shape[axis] = n
        return s / (hi - lo).reshape(shape)
    return along(along(x, 0), 1)


def guided(I, p, r, eps):
    mI, mp = box(I, r), box(p, r)
    a = (box(I * p, r) - mI * mp) / (box(I * I, r) - mI * mI + eps)
    b = mp - a * mI
    return box(a, r) * I + box(b, r)


def guide_radius(width, height):
    return 2 * max(1, int(round(max(width, height) / SIZE)))


EPS = 1e-3


def model_input(bgr01):
    """[1, 3, 512, 512] gray RGB from the L channel of the antialiased 512x512 picture."""
    small = resize(bgr01, SIZE, SIZE)
    L = bgr_to_lab(small)[..., 0]
    gray = lab_to_bgr(np.stack([L, np.zeros_like(L), np.zeros_like(L)], -1))[..., 0]  # a = b = 0: R = G = B
    return np.repeat(gray[None, None], 3, axis=1).astype(np.float32)


def finish(bgr01, ab):
    """Full-size L + refined ab -> BGR uint8."""
    h, w = bgr01.shape[:2]
    L = bgr_to_lab(bgr01)[..., 0]
    up = upscale_bilinear(ab[0].transpose(1, 2, 0).astype(np.float64), w, h)
    r = guide_radius(w, h)
    I = L / 100.0
    ref = np.stack([guided(I, up[..., c], r, EPS) for c in range(2)], -1)
    out = lab_to_bgr(np.concatenate([L[..., None], ref], -1))
    return np.clip(np.round(out * 255.0), 0, 255).astype(np.uint8)


def main():
    import cv2
    import onnxruntime as ort
    sess = ort.InferenceSession(sys.argv[1], providers=['CPUExecutionProvider'])
    out = Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    for p in sys.argv[3:]:
        bgr01 = cv2.imread(p, cv2.IMREAD_COLOR).astype(np.float64) / 255.0
        x = model_input(bgr01)
        ab = sess.run(['ab'], {'gray': x})[0]
        np.save(out / f'{Path(p).stem}.input.npy', x)
        np.save(out / f'{Path(p).stem}.ab.npy', ab)
        cv2.imwrite(str(out / f'{Path(p).stem}.png'), finish(bgr01, ab))
        print(p)


if __name__ == '__main__':
    main()
