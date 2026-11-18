"""
手机包装专用检测模型 —— 训练 + 导出 ONNX 一键脚本

依赖（在训练机上安装，本机仅需生成配置）:
    pip install ultralytics

用法:
    python scripts/train_sop_detector.py \
        --data configs/sop/sop_phone_detector_data.yaml \
        --model yolo26s.pt --epochs 200 --imgsz 640 --batch 16

说明:
    1) 训练产物在 artifacts/training/sop-det/exp/
    2) 自动导出 best.onnx (opset=18) 并复制到 yolo_models/sop_phone_yolo26s.onnx
       —— YOLOv26 必须用 opset=18 导出（YOLOv5u~v12 才用 opset=17），否则 YoloDotNet 兼容性差
    3) 部署时把 sop_*.yaml 的 model.path 指向该 onnx，classes 改为 6 个英文名
    4) YOLOv26 为 Ultralytics 官方 2026-01 发布模型，原生端到端无 NMS，CPU 推理比 v8 快约 43%
"""

import argparse
import shutil
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent


def run(cmd):
    print(f"[RUN] {' '.join(cmd)}")
    subprocess.run(cmd, check=True, cwd=ROOT)


def main():
    ap = argparse.ArgumentParser(description="手机包装专用检测模型训练+导出")
    ap.add_argument("--data", default="configs/sop/sop_phone_detector_data.yaml")
    ap.add_argument("--model", default="yolo26s.pt", help="起始权重，可用 yolo26n/s/m（推荐 s：精度/速度均衡）")
    ap.add_argument("--epochs", type=int, default=200)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--batch", type=int, default=16)
    ap.add_argument("--project", default="artifacts/training/sop-det")
    ap.add_argument("--name", default="exp")
    ap.add_argument("--out", default="yolo_models/sop_phone_yolo26s.onnx")
    args = ap.parse_args()

    # 输入与输出的相对路径统一基于仓库根目录，不依赖调用时的工作目录。
    project = Path(args.project)
    if not project.is_absolute():
        project = ROOT / project
    output = Path(args.out)
    if not output.is_absolute():
        output = ROOT / output

    # 1. 训练
    run([
        "yolo", "detect", "train",
        f"data={args.data}",
        f"model={args.model}",
        f"epochs={args.epochs}",
        f"imgsz={args.imgsz}",
        f"batch={args.batch}",
        f"project={project}",
        f"name={args.name}",
        "exist_ok=True",
    ])

    best_pt = project / args.name / "weights" / "best.pt"

    # 2. 导出 ONNX（YOLOv26 必须用 opset=18，YoloDotNet 兼容性最佳）
    run(["yolo", "export", f"model={best_pt}", "format=onnx", f"imgsz={args.imgsz}", "opset=18"])

    # 3. 复制到 yolo_models
    src_onnx = project / args.name / "weights" / "best.onnx"
    output.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy(src_onnx, output)
    print(f"[OK] 模型已导出并复制到: {output}")


if __name__ == "__main__":
    sys.exit(main())
