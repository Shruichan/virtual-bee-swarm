"""Image -> L2-normalized embedding. Two backbones: open_clip ViT-B-32 (laion2b) and DINOv2-small."""

from __future__ import annotations

import numpy as np
import torch
from PIL import Image


def pick_device() -> str:
    if torch.backends.mps.is_available():
        return "mps"
    if torch.cuda.is_available():
        return "cuda"
    return "cpu"


class Backbone:
    name: str
    kind: str

    def embed(self, images: list[Image.Image]) -> np.ndarray:  # (N, D) float32, unit rows
        raise NotImplementedError


class ClipBackbone(Backbone):
    def __init__(self, device: str, arch: str = "ViT-B-32", pretrained: str = "laion2b_s34b_b79k") -> None:
        import open_clip

        self.name = f"clip-{arch}-{pretrained}"
        self.kind = "clip"
        self.device = device
        model, _, preprocess = open_clip.create_model_and_transforms(arch, pretrained=pretrained)
        self.model = model.eval().to(device)
        self.preprocess = preprocess

    @torch.inference_mode()
    def embed(self, images: list[Image.Image]) -> np.ndarray:
        x = torch.stack([self.preprocess(im.convert("RGB")) for im in images]).to(self.device)
        f = self.model.encode_image(x)
        f = torch.nn.functional.normalize(f.float(), dim=-1)
        return f.cpu().numpy().astype(np.float32)


MEAN = (0.485, 0.456, 0.406)
STD = (0.229, 0.224, 0.225)


class DinoBackbone(Backbone):
    def __init__(self, device: str, model_id: str = "facebook/dinov2-small") -> None:
        from transformers import AutoImageProcessor, AutoModel

        self.name = model_id.split("/")[-1]
        self.kind = "dino"
        self.device = device
        self.processor = AutoImageProcessor.from_pretrained(model_id)
        # fp16 on the GPU: ~3x faster on MPS for multi-crop batches, same accuracy on eval.py
        self.dtype = torch.float16 if device in ("mps", "cuda") else torch.float32
        self.model = AutoModel.from_pretrained(model_id).eval().to(device=device, dtype=self.dtype)

    @torch.inference_mode()
    def embed(self, images: list[Image.Image]) -> np.ndarray:
        x = self.processor(images=[im.convert("RGB") for im in images], return_tensors="pt")["pixel_values"].to(self.device)
        return self._forward(x)

    def _forward(self, x: torch.Tensor) -> np.ndarray:
        h = self.model(pixel_values=x.to(self.dtype)).last_hidden_state
        # CLS token + mean of patch tokens (the usual DINOv2 linear-probe feature)
        f = torch.cat([h[:, 0], h[:, 1:].mean(dim=1)], dim=-1)
        f = torch.nn.functional.normalize(f.float(), dim=-1)
        return f.cpu().numpy().astype(np.float32)

    @torch.inference_mode()
    def embed_crops(self, rgb: np.ndarray, boxes: list[tuple[int, int, int, int]]) -> np.ndarray:
        """One frame (HxWx3 uint8), several boxes -> (len(boxes), D). All crops are cut and resized to 224 in
        one roi_align call on the GPU in fp16 (~10 ms for 15 crops on MPS vs ~40 ms per-crop interpolate)."""
        import torchvision

        t = torch.from_numpy(rgb if rgb.flags.writeable and rgb.flags.c_contiguous else rgb.copy()).to(self.device)
        t = t.permute(2, 0, 1).unsqueeze(0).to(self.dtype)
        b = torch.tensor([[0, *bx] for bx in boxes], dtype=self.dtype, device=self.device)
        x = torchvision.ops.roi_align(t, b, output_size=(224, 224), spatial_scale=1.0, sampling_ratio=2, aligned=True)
        mean = torch.tensor(MEAN, device=self.device, dtype=self.dtype).view(1, 3, 1, 1) * 255
        std = torch.tensor(STD, device=self.device, dtype=self.dtype).view(1, 3, 1, 1) * 255
        return self._forward((x - mean) / std)


def load_backbone(kind: str, device: str | None = None) -> Backbone:
    device = device or pick_device()
    if kind == "clip":
        return ClipBackbone(device)
    if kind == "dino":
        return DinoBackbone(device)
    raise ValueError(f"unknown backbone {kind!r} (clip | dino)")
