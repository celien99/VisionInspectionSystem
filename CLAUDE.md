# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Windows-only WPF (.NET 8) **SOP compliance inspection** application. It runs YOLO object detection (YoloDotNet) plus MediaPipe/YOLO-pose hand landmarks over up to 4 cameras, executes a YAML-described workflow through a state machine, and reports violations (UI banner + `ng.wav` + optional Modbus output). The `rk3588/` tree is a physically isolated C++17 port for the RK3588 board; it does not share code with `src/`.

## Commands

### Windows / .NET (the main application)

```bat
dotnet restore
dotnet build                       :: CPU build (default)
dotnet run --project src/UI/VisionInspection.UI
```

Build output: `src/UI/VisionInspection.UI/bin/Debug/net8.0-windows/VisionInspection.UI.exe`.

GPU is a **build-time** choice. Only one ONNX Runtime execution provider can be referenced:

```bat
dotnet build -p:YoloEp=Cuda                       :: needs CUDA 12.x + cuDNN 9.x on PATH
scripts\build_cuda.bat                            :: recommended: checks deps, cleans, builds, verifies artifacts
scripts\build_cuda.bat --release                  :: Release; also supports --force, --no-clean
```

A plain `dotnet build` or F5 in Visual Studio **overwrites a CUDA build with the CPU build**. `Directory.Build.props` turns `-p:YoloEp=Cuda` into the `YOLO_EP_CUDA` compile constant; `YoloExecutionProviders.IsCudaBuild` is the single place that branches on it.

There is **no .NET test project, lint config, or formatter config** in this repo — do not invent test commands.

### RK3588 C++ port

```bash
cd rk3588
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
ctest --test-dir build --output-on-failure
```

The PC "native" mode builds `engine` + replay tooling without RKNN/Qt. Note that `CMakeLists.txt` lists several files (e.g. `engine/tests/test_main.cpp`, `tools/replay/replay_compare.cpp`) that are not present yet; `scripts/build_replay.sh` (Git Bash) compiles the existing engine sources plus `tools/replay/replay_main.cpp`. Board build: pass `-DCMAKE_TOOLCHAIN_FILE=scripts/aarch64-linux-gnu.toolchain.cmake -DENABLE_RKNN=ON -DBUILD_UI=ON`.

### Optional Python utilities (`scripts/`)

- `train_sop_detector.py` — train + export an Ultralytics ONNX (needs `ultralytics`); YOLOv26 must export with `opset=18`.
- `build_sop_manual.py` — compiles `docs/SOP*.md` into a Word manual (needs `python-docx`).
- `gen_modern_icon.py`, `convert_wav_to_16bit.py` — one-off asset utilities.

## Solution layout and dependency direction

```
Core (interfaces/models/services, no WPF)
  ↑
Modules.Detection (YoloDotNet object detection + OpenCV video decoding)
  ↑
Modules.SOP (YAML → workflow/state machine, hand services)
  ↑
UI (WPF + MVVM)
```

`VisionInspection.Converters` is a small WPF converter library. `src/Modules/VisionInspection.Modules.Cascade` and `.Defect` contain only `obj/` leftovers — the projects themselves were removed.

The intended boundary is that SOP/UI consume domain types (`DetectedObject`, `HandPoseEstimationResult`), not YoloDotNet types. Current exceptions: `YoloPoseHandEstimationService` in Modules.SOP directly creates a `Yolo` instance for pose and an `Microsoft.ML.OnnxRuntime.InferenceSession` for the landmark model. Do not spread this pattern further; extend `YoloModel` instead.

## Development constraints

- **Minimal-scope verification only.** No full-suite / "test everything" passes. Verify only the smallest scope the change touches (one runnable check for non-trivial logic), and do not invent test projects or test commands — this repo has none.
- **Fix root causes, never patch over symptoms.** Read the real flow and every caller of the function you touch before editing. The fix goes once in the shared path — no per-caller guards, fallbacks, or "just in case" workarounds for a reported symptom.
- **Keep it modular.** Reuse the existing helper/type for the job (`AppPaths`, `YoloModel`, existing evaluators) instead of re-implementing it; keep the dependency direction above; do not let UI/SOP absorb detection or YoloDotNet concepts; no duplicated logic and no new cross-module coupling.

## Runtime architecture

**Startup:** `App.xaml.cs` installs crash handlers, writes `logs/startup.log` (`AppPaths.Describe()`), opens `LoginWindow` (SQLite users in `data/users.db`), then `MainWindow`/`MainViewModel`, then starts the Modbus singleton. There is no DI container; `MainViewModel` constructs services, shares one in-flight SOP startup task and waits for it during background disposal, and `CameraManager.Instance` / `ModbusService.Instance` / `RegionConfigManager.Instance` are singletons.

**Frame pipeline (the non-obvious part):**

1. `CameraFramePipeline` owns the camera callback subscription, capacity-1 preview channel and preview worker (25 fps cap).
2. `MainViewModel.RenderPreview` converts to `SKBitmap`, submits a copy for inference, and renders the newest frame with cached overlays.
3. The pipeline keeps one pending bitmap per camera; replacement disposes the previous bitmap immediately. A single inference worker updates the latest per-camera frames and calls `MainViewModel.ProcessCameraFramesAsync` for SOP or regular detection. Stop waits for the worker before disposing its frames.
4. `DetectionOverlayRenderer` owns SOP overlay caches and draws them onto the **next newest camera frame**. `DetectionTrackSmoother` handles regular detection tracking and EMA smoothing. `SopAudioFeedback` owns cached NAudio players and releases them at shutdown.
5. `SopPanelViewModel` owns step-card state, elapsed time and production counters; `SOPModuleView` binds cards through an `ItemsControl` template. Workflow-completion events hold snapshots so automatic reset cannot erase queued UI results.

Do not block the camera callback thread, and remember that YoloDotNet reuses its result `List` — copy detection results immediately (`ToList()`) before caching.

**SOP engine:**

`SOPYamlConverter.LoadFromYaml` → `SOPWorkflow` → `SOPStateMachine.Start`. Each frame: `ViolationDetector.DetectViolations` (rules + global timeout + skip-step probe) → `StepConditionEvaluator.EvaluateConditions` → advance step or emit failure diagnostics. Events (`StepChanged`, `StepCompleted`, `ViolationDetected`, `WorkflowCompleted`) are forwarded by `SOPModule` to `MainViewModel`. All steps passing raises `WorkflowCompleted`; `settings.auto_reset_on_complete` loops the workflow after `reset_delay_sec`.

Important engine semantics:

- The starting step is the **minimum `StepId`**, not hard-coded 1; transitions also use the next-higher `StepId`.
- `StepConditionEvaluator` owns a generic stable-frame gate keyed by `(stepId, conditionIndex)`. It is deliberately skipped for `HandStable`, `ObjectStable`, `HandMoveFromTo`, `TimeElapsed`, and `SequenceComplete`. Changing this list was the source of past "step stuck" and "skip not detected" regressions.
- Skip-step detection evaluates the **next** step with `ignoreFromRegion: true`; it intentionally relaxes the `from_region` history requirement while still requiring the core action.
- The state machine evaluates against the **current step's designated camera** (`step.camera`, default `main_camera`) with **no cross-camera fallback**.
- `hand_action` in YAML maps onto `HandMoveFromTo`; `pickup` means "the hand holds the target object while the hand or the object is currently in `from_region`", `putdown` means "the object (or the hand carrying it) reached `to_region` after visiting `from_region`".

**Hand detection:** `HandDetectionBackend` is `MediaPipe` (default, two-stage palm→landmark), `YoloPose` (YOLOv8-pose wrist + MediaPipe landmark), or `Auto`. The service is lazily initialized on first use, with a 10-second retry backoff after failure. Hand defaults live in `configs/sop_config.json`. In-app edits pass the same `HandPoseEstimationConfig` through the view and `MainViewModel.ApplySopConfigurationAsync`; `UpdateHandPoseConfigAsync` waits for the old service to shut down before rebuilding it. The UI retains a complete config snapshot for the current session; only the main GPU toggle writes `sop_config.json` back. `SOPModule` clones the full config when rebuilding the service — partial clones previously caused UI-tuned parameters to silently revert to defaults.

**Multi-camera:** slots are `main_camera`, `cam_2`…`cam_4` (max 4). Each step may specify `camera` and `model`, and `SOPModule` lazily caches per-model `YoloModel` instances. `cross_camera` rules in YAML are evaluated by `CrossCameraEvaluator` against `PerCameraDetections`; `monitoring` (helmet) rules are evaluated by `RegionMonitoringEvaluator`. `SOPModule.ProcessAsync` owns both evaluations and returns alerts for UI presentation; monitoring alerts do not change step violations.

**Modbus:** `ModbusService` is a singleton. Rising edge on input X1 (register 0) starts the loaded workflow when idle. Step-level `modbus_trigger` writes FC06 to Y1–Y8 with pulse/latch semantics; latched outputs are released via `ReleaseLatch`/`ReleaseAllLatches`.

## Configuration, paths, and data

`AppPaths` (`src/Core/.../Services/AppPaths.cs`) is the single source for path resolution. `Root` = `AppContext.BaseDirectory` or `VISION_HOME`; the config directory is the nearest ancestor containing `configs/sop`, resolved once at startup (so dev runs from `bin/` still read the repo's `configs/`). `configs/` is copied only on publish, not into `bin/`.

- `configs/sop_config.json` — SOP module startup defaults (model, thresholds, hand backend/params, GPU). Keep it the startup source; don't reintroduce the old `AddInMemoryCollection` overrides in `MainViewModel.BuildSopConfig`.
- `configs/sop/*.yaml` — recipes. `configs/sop/sop_template.yaml` is the annotated template. The UI remembers the last recipe in `configs/last_recipe.json` (runtime file, gitignored).
- Legacy region JSON: `configs/sop/regions/*.json` via `RegionConfigLoader` (scales coordinates when the frame size differs). New recipes should keep regions in the YAML `regions` node.
- Model search order: `models/`, `Models/`, `yolo_models/`, walking the program root and its ancestors (up to 8 levels in `AppPaths.SearchRoots`), then the working directory; a recipe path like `yolo_models/x.onnx` also matches `models/x.onnx` by filename. Missing models degrade gracefully with log/UI warnings.
- `VISION_HOME` overrides the program root; `VISION_FFMPEG_DIR` locates ffmpeg for data acquisition only (video inference uses bundled OpenCV, no external ffmpeg needed).

YAML recipes reference `configs/sop/*.yaml`. Supported `detection.method` values: `person_present`, `hand_action`, `hand_in_region`, `hand_not_in_region`, `hand_stable`, `hand_move`, `hand_near_object`, `object_present`, `object_in_zone`, `pose_stable`, `time_elapsed`. YAML also supports `settings`, `model`, `monitoring`, `cross_camera`, step-level `camera`/`model`/`modbus_trigger`/`forbidden_objects`/`must_keep`/`required_objects`.

Runtime output locations (not in git):

| Output | Location |
|---|---|
| Startup / crash / debug logs | `<root>/logs/startup.log`, `crash.log`, `sop_module_debug.log`, `sop_frame_debug.log` |
| SOP cycle records | `<app base dir>/sop_records/sop_run_YYYY-MM-DD.jsonl` and `.csv` |
| Golden dataset (for RK3588 replay) | `<app base dir>/golden/cases/<case>/` (`frames/`, `inputs.jsonl`, `decisions.jsonl`, `meta.json`, `summary.json`) |
| Violation screenshots | `<app base dir>/sop_captures/` (configurable in `configs/sop_capture.json`) |
| Audio feedback | `ok.wav` / `ng.wav` copied next to the executable |

## Where to look

| Concern | Primary file(s) |
|---|---|
| UI commands and detection control | `src/UI/VisionInspection.UI/ViewModels/MainViewModel.cs` |
| Camera workers, overlays, audio | `src/UI/VisionInspection.UI/Services/CameraFramePipeline.cs`, `DetectionOverlayRenderer.cs`, `SopAudioFeedback.cs` |
| SOP panel state and counters | `src/UI/VisionInspection.UI/ViewModels/SopPanelViewModel.cs` |
| SOP module façade: model cache, hand init, workflow start | `src/Modules/VisionInspection.Modules.SOP/SOPModule.cs` |
| State machine / conditions / violations | `src/Modules/VisionInspection.Modules.SOP/Models/SOPStateMachine.cs`, `.../Services/StepConditionEvaluator.cs`, `.../Services/ViolationDetector.cs` |
| YAML DTOs and two-way conversion | `src/Modules/VisionInspection.Modules.SOP/Models/SOPYamlConfig.cs` |
| Path, config, model resolution | `src/Core/VisionInspection.Core/Services/AppPaths.cs` |
| YOLO adapter and EP switch | `src/Modules/VisionInspection.Modules.Detection/YoloModel.cs`, `.../YoloExecutionProviders.cs` |
| Camera slots and frame events | `src/Core/VisionInspection.Core/Services/CameraManager.cs` |

## Cross-cutting conventions / gotchas

- **YAML must be edited symmetrically.** A new field needs: the `Sopyaml*` DTO, conversion into `SOPWorkflow`/`StepCondition` (`LoadFromYaml`), back-conversion (`SaveToYaml`/`ConvertConditionToDetection`), and — for engine behavior — the rk3588 `yaml_config`/`state_machine` port. The UI round-trips recipes through these models and loses anything not modeled.
- Always deserialize YAML with `IgnoreUnmatchedProperties()`; legacy recipes contain removed fields (`model.type`, `model.classes`, `settings.detectionMode`, etc.).
- Keep new inference code inside `Modules.Detection`. The object-detection boundary is `YoloModel.Detect` returning domain `DetectedObject`; UI and SOP logic must not touch YoloDotNet types. The one existing exception is `YoloPoseHandEstimationService` (pose `Yolo` + landmark `InferenceSession`).
- Never reference both `YoloDotNet.ExecutionProvider.Cpu` and `.Cuda`; native `onnxruntime.dll` copies collide.
- Do not hard-code paths (`e:\...`, `configs/...`, model locations); use `AppPaths` / `RecipeFile`.
- rk3588 `engine/` must not include RKNN/Qt/V4L2/OpenCV headers — only `inference/include/detector.h`. Its README contains hard constraints (stable_frames/thresholds must not be weakened, manual NPU core binding, recipes copied verbatim, per-run comparison logged to `docs/移植台账.md`).
- Coordinates are in camera-frame pixels. Region membership uses rectangle intersection for objects and center-point containment for hands (see evaluators for exact semantics).
- Comments and UI strings are predominantly Chinese; match that style.
- `docs/` is blanket-ignored by `.gitignore` (only the two already-tracked docs are committed). New docs won't be committed unless forced. The SOP manuals linked from `README.md` are not present in the tracked tree.
- Models (`*.onnx`, `*.pt`) are intentionally untracked. `models/` and `yolo_models/` must be populated per machine.
