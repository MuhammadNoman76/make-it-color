"""
Converts the MakeItColor (DDColor, ConvNeXt-L) PyTorch checkpoint to ONNX for the app and checks the result.

  python convert.py <pytorch_model.pt> <out_dir> [images...]

Writes to <out_dir>:
  makeitcolor-fp32.onnx   for the CPU (float32)
  makeitcolor-fp16.onnx   for NPUs and GPUs (float16 inside, float32 input and output)
  reference/<name>.png    each image colorized by the original ModelScope pipeline (PyTorch), for comparing the app
  report.json             differences between PyTorch and ONNX (ab channels and final images)

Model input:  "gray"  float32 [1, 3, 512, 512]  RGB in 0..1, the image's L channel turned back into gray RGB
Model output: "ab"    float32 [1, 2, 512, 512]  Lab a and b (OpenCV float Lab scale)
"""
import json
import math
import sys
from copy import deepcopy
from pathlib import Path

import cv2
import numpy as np
import onnx
import onnxruntime as ort
import torch
import torch.nn.functional as F

sys.path.insert(0, str(Path(__file__).parent))
from ddcolor.ddcolor import DDColor  # noqa: E402

SIZE = 512


def channels_first_layer_norm(self, x):
    """ConvNeXt's channels-first LayerNorm as one LayerNormalization op (same maths as the original mean / pow /
    mean / sqrt). Written out by hand, the squares reach ~2e7, past float16's 65504, and overflow on GPUs (and on NPUs
    whenever the pattern isn't fused); LayerNormalization is computed safely by every backend."""
    if self.data_format == 'channels_last':
        return F.layer_norm(x, self.normalized_shape, self.weight, self.bias, self.eps)
    x = F.layer_norm(x.permute(0, 2, 3, 1), self.normalized_shape, self.weight, self.bias, self.eps)
    return x.permute(0, 3, 1, 2)


def load_model(path):
    from ddcolor.utils import convnext
    convnext.LayerNorm.forward = channels_first_layer_norm
    net = DDColor('convnext-l', (SIZE, SIZE), 100)
    state = torch.load(path, map_location='cpu', weights_only=False)
    state = state.get('params', state)
    for k, v in deepcopy(state).items():
        if k.startswith('module.'):
            state[k[7:]] = v
            state.pop(k)
        elif k.startswith('model.'):
            state[k[6:]] = v
            state.pop(k)
    net.load_state_dict(state, strict=True)
    net.eval()
    return net


def bake_spectral_norm(net):
    """Spectral norm computes weight = weight_orig / sigma on every call; bake it into a plain weight for export."""
    count = 0
    for module in net.modules():
        if hasattr(module, 'weight_orig'):
            torch.nn.utils.remove_spectral_norm(module)
            count += 1
    return count


def preprocess(bgr):
    """Exactly ModelScope's DDColorImageColorizationPipeline.preprocess."""
    img = (bgr / 255.0).astype(np.float32)
    orig_l = cv2.cvtColor(img, cv2.COLOR_BGR2Lab)[:, :, :1]
    small = cv2.resize(img, (SIZE, SIZE))
    img_l = cv2.cvtColor(small, cv2.COLOR_BGR2Lab)[:, :, :1]
    gray_lab = np.concatenate((img_l, np.zeros_like(img_l), np.zeros_like(img_l)), axis=-1)
    gray_rgb = cv2.cvtColor(gray_lab, cv2.COLOR_LAB2RGB)
    return orig_l, gray_rgb.transpose((2, 0, 1))[None].astype(np.float32)


def postprocess(orig_l, ab, height, width, mode='nearest'):
    """ModelScope's forward() after the model: resize ab (nearest, as F.interpolate defaults), add the original L."""
    t = torch.from_numpy(ab)
    t = F.interpolate(t, size=(height, width)) if mode == 'nearest' else F.interpolate(t, size=(height, width), mode=mode, align_corners=False)
    ab_full = t[0].float().numpy().transpose(1, 2, 0)
    out_lab = np.concatenate((orig_l, ab_full), axis=-1)
    out_bgr = cv2.cvtColor(out_lab, cv2.COLOR_LAB2BGR)
    return (out_bgr * 255.0).round().astype(np.uint8)


def psnr(a, b):
    mse = np.mean((a.astype(np.float64) - b.astype(np.float64)) ** 2)
    return float('inf') if mse == 0 else 10 * math.log10(255.0 ** 2 / mse)


def main():
    ckpt, out_dir = Path(sys.argv[1]), Path(sys.argv[2])
    images = [Path(p) for p in sys.argv[3:]]
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / 'reference').mkdir(exist_ok=True)
    report = {'images': {}}

    net = load_model(ckpt)
    torch.manual_seed(0)
    probe = torch.rand(1, 3, SIZE, SIZE)
    with torch.no_grad():
        before = net(probe)
        report['spectral_norm_layers'] = bake_spectral_norm(net)
        after = net(probe)
    report['spectral_norm_bake_max_diff'] = float((before - after).abs().max())
    print('spectral norm baked:', report['spectral_norm_layers'], 'max diff', report['spectral_norm_bake_max_diff'])

    fp32 = out_dir / 'makeitcolor-fp32.onnx'
    torch.onnx.export(net, probe, str(fp32), input_names=['gray'], output_names=['ab'], opset_version=17,
                      do_constant_folding=True, dynamo=False)
    onnx.checker.check_model(str(fp32))
    print('exported', fp32, fp32.stat().st_size)

    from onnxconverter_common import float16
    model16 = float16.convert_float_to_float16(onnx.load(str(fp32)), keep_io_types=True)
    fp16 = out_dir / 'makeitcolor-fp16.onnx'
    onnx.save(model16, str(fp16))
    print('fp16', fp16, fp16.stat().st_size)

    sess32 = ort.InferenceSession(str(fp32), providers=['CPUExecutionProvider'])
    try:
        sess16 = ort.InferenceSession(str(fp16), providers=['CPUExecutionProvider'])
    except Exception as e:  # the CPU provider may lack float16 kernels; checked on DirectML separately
        sess16 = None
        report['fp16_cpu_error'] = str(e)[:300]

    for path in images:
        bgr = cv2.imread(str(path), cv2.IMREAD_COLOR)
        h, w = bgr.shape[:2]
        orig_l, x = preprocess(bgr)
        with torch.no_grad():
            ab_torch = net(torch.from_numpy(x)).numpy()
        ref = postprocess(orig_l, ab_torch, h, w)
        cv2.imwrite(str(out_dir / 'reference' / f'{path.stem}.png'), ref)
        np.save(out_dir / 'reference' / f'{path.stem}.ab.npy', ab_torch)
        np.save(out_dir / 'reference' / f'{path.stem}.gray.npy', x)

        ab32 = sess32.run(['ab'], {'gray': x})[0]
        entry = {
            'size': [w, h],
            'ab_range': [float(ab_torch.min()), float(ab_torch.max())],
            'fp32_ab_max_diff': float(np.abs(ab32 - ab_torch).max()),
            'fp32_psnr_vs_reference': psnr(postprocess(orig_l, ab32, h, w), ref),
            'bilinear_psnr_vs_nearest': psnr(postprocess(orig_l, ab_torch, h, w, 'bilinear'), ref),
        }
        if sess16 is not None:
            ab16 = sess16.run(['ab'], {'gray': x})[0]
            entry['fp16_cpu_ab_max_diff'] = float(np.abs(ab16 - ab_torch).max())
            entry['fp16_cpu_psnr_vs_reference'] = psnr(postprocess(orig_l, ab16, h, w), ref)
        report['images'][path.name] = entry
        print(path.name, json.dumps(entry))

    (out_dir / 'report.json').write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
