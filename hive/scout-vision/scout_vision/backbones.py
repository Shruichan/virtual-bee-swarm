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

    def embed(self, images: list[Image.Image]) -> np.ndarray:  # (N, D) float32, unit rows
        raise NotImplementedError


class ClipBackbone(Backbone):
    def __init__(self, device: str, arch: str = "ViT-B-32", pretrained: str = "laion2b_s34b_b79k") -> None:
        import open_clip

        self.name = f"clip-{arch}-{pretrained}"
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


class DinoBackbone(Backbone):
    def __init__(self, device: str, model_id: str = "facebook/dinov2-small") -> None:
        from transformers import AutoImageProcessor, AutoModel

        self.name = model_id.split("/")[-1]
        self.device = device
        self.processor = AutoImageProcessor.from_pretrained(model_id)
        self.model = AutoModel.from_pretrained(model_id).eval().to(device)

    @torch.inference_mode()
    def embed(self, images: list[Image.Image]) -> np.ndarray:
        x = self.processor(images=[im.convert("RGB") for im in images], return_tensors="pt")["pixel_values"].to(self.device)
        h = self.model(pixel_values=x).last_hidden_state
        # CLS token + mean of patch tokens (the usual DINOv2 linear-probe feature)
        f = torch.cat([h[:, 0], h[:, 1:].mean(dim=1)], dim=-1)
        f = torch.nn.functional.normalize(f.float(), dim=-1)
        return f.cpu().numpy().astype(np.float32)


def load_backbone(kind: str, device: str | None = None) -> Backbone:
    device = device or pick_device()
    if kind == "clip":
        return ClipBackbone(device)
    if kind == "dino":
        return DinoBackbone(device)
    raise ValueError(f"unknown backbone {kind!r} (clip | dino)")
