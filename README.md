# worldgen-test

An LLM-driven Unity voxel world generator. A text prompt describing a place (a *biome*) is sent to
a chat-completion LLM, which returns terrain-generation parameters and a list of objects to
populate it with; those objects are then generated on the fly by an image-generation model and a
3D (image-to-voxel) model, and streamed into the world as the player walks. Biomes blend into one
another as the player crosses a threshold distance, so new prompts can be issued at any time
without a loading screen.

The Unity project itself lives in [`worldgen-test/`](worldgen-test/) (open that folder in Unity
Hub, not the repo root).

## Requirements

- **Unity 6000.4.1f1** (Unity 6) — install via Unity Hub; a different 6000.4.x patch will likely
  work but hasn't been tested.
- Universal Render Pipeline (URP), XR Interaction Toolkit + OpenXR (for the VR scenes), Newtonsoft
  Json, and `com.whisper.unity` (voice input) — all resolved automatically from
  `worldgen-test/Packages/manifest.json` when the project is opened.
- **Three servers reachable on the LAN**, hosting the actual generation models. None of the
  generation logic runs locally in Unity — it's all HTTP calls to these:

  | Purpose | Default endpoint | Called from |
  |---|---|---|
  | Chat completions (LM Studio, OpenAI-compatible) | `http://<host>:1234` | `LLMWorldDirector.cs` |
  | Image generation (`/create_image`, `/extract_normal_map`) | `http://<host>:5000` | `GenerateTexture.cs`, `Objects/GenerateAsset.cs`, `Objects/LLMObjectGeneration.cs` |
  | 3D / voxel generation (`/generate_vox_only`) | `http://<host>:7960` (+ optional second instance on `7961`) | `Objects/GenerateAsset.cs`, `Objects/LLM3DGeneration.cs` |

  Without these reachable, world generation will time out — the game (and the benchmark harness)
  degrade to failed/timed-out prompts rather than crashing.

## Configuration

All server/world/graphics settings are runtime-editable from the in-game Options panel and persist
to `settings.json` (see `GameSettings.cs` / `GameSettingsData`). In the Editor that file lives at
`Assets/Json/settings.json`; in a build it's under the platform's persistent data path (see
[Where files live](#where-files-live) below). Key fields:

```jsonc
{
  "serverHost": "25.18.55.60",
  "llmPort": 1234,
  "imagePort": 5000,
  "gen3dPort": 7960,
  "gen3dPort2": 7961,          // second Trellis instance, used in parallel if reachable
  "micDeviceName": "",          // "" = OS default microphone
  "objectDensityMultiplier": 1.0,
  "renderDistanceMultiplier": 1.0,
  "transitionLengthInChunks": 8,
  "enablePath": true,
  "pathWidthMultiplier": 1.0,
  "pathBlendWidthMultiplier": 1.0,
  "shadowQuality": 3             // 0=Off 1=Low 2=Medium 3=High
}
```

Edit it by hand before first launch, or launch once and use the Options panel — either way it's
picked up on the next scene load.

## Project structure

```
worldgen-test/
  Assets/
    Scenes/                 — see Scenes below
    Scripts/
      WorldManager.cs        — chunk streaming, biome blending, object placement, "central path"
      Chunk.cs                — per-chunk terrain mesh generation (BiomeConfig → heightmap → mesh)
      LLMWorldDirector.cs      — talks to the chat-completion server, drives terrain generation
      GenerateTexture.cs       — per-material texture generation (albedo + normal map)
      TextureAtlasManager.cs   — packs generated textures into a shared atlas
      Objects/
        GenerateAsset.cs        — orchestrates the object pipeline (cache check → image → 3D)
        LLMObjectGeneration.cs  — image generation for a single object
        LLM3DGeneration.cs      — image-to-voxel generation for a single object
        VoxelRenderer.cs        — compiles a .voxjson into a runtime mesh, LOD generation
        LODManager.cs, ObjectDistanceCuller.cs, BillboardPlaceholder.cs
      FirstGeneration.cs       — Start scene: prompt input UI, kicks off the very first biome
      AutoPromptSequencer.cs   — auto-plays a list of prompts back to back (dev/demo tool)
      PregeneratedBiomeSequencer.cs — pre-bakes N biomes to disk snapshots, then plays them back
                                       with zero further LLM calls (see Pregenerated Tour below)
      Benchmark/               — see Benchmark harness below
      GameSettings.cs, PathSettings.cs — settings persistence, platform-aware file paths
      PlayerMovement.cs, MouseLook.cs, VRSprintController.cs — locomotion
    Editor/                  — Editor-only tools (Tools menu), see below
    Resources/Baseline/      — baseline asset library shipped with builds (see below)
    Json/                    — biome pool / prompt lists used by the sequencers and benchmark
  ProjectSettings/, Packages/ — standard Unity project files
```

## Scenes

| Scene | What it is | In Build Settings |
|---|---|---|
| `Start.unity` | Entry point: prompt input panel, generates the first biome, then loads `MainScene` | ✅ |
| `MainScene.unity` | The actual game — desktop keyboard/mouse | ✅ |
| `MainScene_VR.unity` | VR version (XR Interaction Toolkit + OpenXR) | ✅ |
| `MainScene_PregeneratedTour.unity` | Desktop pregenerated-tour playback (see below) | ❌ |
| `MainScene_PregeneratedTour_VR.unity` | VR pregenerated-tour playback | ✅ |
| `MainScene_Benchmark.unity` | Automated headless-ish benchmark run (see below) | ✅ |
| `MeshGenerator.unity` | Editor-only scratch scene used by the Minecraft-style prefab/material generator tools | ❌ |

If you add a new scene to the game flow, remember to add it to **File → Build Settings** — a scene
missing from that list is silently unreachable in a build even though it plays fine from the
Editor. (`MainScene_PregeneratedTour.unity` and `MeshGenerator.unity` are intentionally excluded.)

## Controls

- **Desktop**: WASD to move, mouse to look, Space to jump, mouse wheel / sprint key per
  `VRSprintController`/`PlayerMovement` (see Options panel for sensitivity).
- **VR**: standard XR Interaction Toolkit locomotion, `VRSprintController` for sprint.
- Text prompt input is either typed into the Start-scene panel, or spoken via `VoiceBiomeInput`
  (Whisper-based transcription) if a microphone is configured.

## Where files live

Generated content is never fully baked into the Unity project — `GameSettings`, textures, object
`.voxjson` data, and the object/material registries are written to disk at runtime and reloaded on
each launch (`PathSettings.cs` picks the path):

- **In the Editor**: `Assets/Json/`, `Assets/GeneratedTexture/`, `Assets/GeneratedImages/` — i.e.
  directly inside the project, visible in the Project window.
- **In a build**: the platform's persistent data path. On Windows that's
  `%userprofile%\AppData\LocalLow\<companyName>\<productName>\` (see `ProjectSettings/ProjectSettings.asset`
  for the exact company/product name) — `Json\`, `GeneratedTexture\`, `GeneratedImages\`,
  `Benchmark\` (benchmark reports) live under there.

A build's first launch seeds that folder from `Assets/Resources/Baseline/` (a pre-baked library of
~250 common objects + a baseline terrain config + atlas textures) so it isn't starting from a
completely empty world — see **Prepare Baseline Assets for Build** below.

## Building

### 1. Desktop (`MainScene`)

1. **Tools → Prepare Baseline Assets for Build** (see [Editor tools](#editor-tools)) — refreshes
   `Assets/Resources/Baseline/` from whatever is currently on disk in `Assets/Json`/
   `Assets/GeneratedTexture`, so the build ships with a working baseline object library instead of
   an empty one.
2. **File → Build Settings** → target platform Windows/Mac/Linux Standalone → make sure
   `Start`, `MainScene` are checked (and the other checked scenes don't hurt, they're just extra
   entry points) → **Build**.
3. Launch the resulting executable — it opens on `Start.unity`, type or speak a prompt, and it
   loads into `MainScene` once the first biome is ready.

### 2. VR (`MainScene_VR`)

Same as above, but:
- In **Project Settings → XR Plug-in Management**, make sure OpenXR is enabled for your target
  platform, with the interaction profile for your headset added.
- Set `MainScene_VR` (not `MainScene`) as the scene `FirstGeneration.mainSceneName` loads into, or
  make `Start` → `MainScene_VR` the flow you build (both are already in Build Settings; if you only
  want the VR path, uncheck `MainScene` in Build Settings to avoid shipping the desktop scene too).
- Build as usual; run on a headset-connected machine (or Play Mode with the headset attached, for
  testing).

### 3. Pregenerated Tour (desktop or VR)

`PregeneratedBiomeSequencer` pre-bakes a fixed sequence of biomes to disk (`Assets/Json`-style
snapshot files, one per biome — actually persistentDataPath/PregeneratedSequences at runtime) via
real LLM calls, **then** plays them back as the player walks a fixed distance per biome, with
**zero further LLM calls** during playback — useful for demos/kiosks where you want a
deterministic, network-independent walkthrough after an initial generation pass.

1. Open `MainScene_PregeneratedTour_VR.unity` (VR) — the desktop equivalent
   `MainScene_PregeneratedTour.unity` exists but isn't in Build Settings; add it there first if you
   want a desktop build of this mode.
2. On the `PregeneratedBiomeSequencer` component in the scene, set `biomePrompts` (one entry per
   biome, in traversal order) and `biomeLengths` (world units per biome before advancing to the
   next — index-aligned with `biomePrompts`; shorter than `biomePrompts` falls back to
   `defaultBiomeLength`).
3. Build normally (adding the scene to Build Settings first if not already there). On first launch
   it pre-generates all biomes (needs the LLM/image/3D servers reachable, same as any other mode)
   before gameplay starts; subsequent traversal needs no network at all.

### 4. Benchmark (`MainScene_Benchmark`)

See the dedicated section below — it's a fully automated run, not something you play. Build it like
any other scene (already in Build Settings) and just run the executable; it starts the run on
launch and writes a report when done, no interaction needed.

## Benchmark harness

`MainScene_Benchmark.unity` (`BenchmarkHarness` GameObject) automates an end-to-end generation
benchmark for measuring the pipeline's performance — built for gathering numbers for a paper, but
useful any time you want reproducible before/after numbers for a change.

**What a run does**, driven by `Benchmark/BenchmarkRunner.cs`:

1. On `Awake` (before anything else touches disk, via `[DefaultExecutionOrder(-1000)]`): wipes all
   generated assets/textures/registries so the run starts from 0, then writes a flat, empty "world
   zero" biome to `llm_generated.json` so the player spawns on a known flat plain instead of
   whatever was last generated — see `spawnOnFlatWorldZero`/`worldZeroConfigJson` fields.
2. Picks `promptsPerRun` prompts (default 10) at random, without repeats, from
   `Assets/Json/benchmark_biome_pool.json` (25 "structured" + 25 "natural" prompts). `randomSeed =
   0` → different shuffle every run; nonzero → reproducible selection.
3. Feeds them one at a time to `LLMWorldDirector`, waiting for each biome's terrain *and*
   background 3D asset generation to fully finish before the next prompt.
4. The player starts walking automatically (`Benchmark/AutoWalkerJumper.cs`) once the first biome
   is ready: always due north (world `+Z`, fixed regardless of player rotation), colliders on every
   non-terrain object are disabled for the duration (so decor never blocks the walk), and a jump
   triggers only when forward progress stalls for `stuckFrameThreshold` frames (a terrain step),
   not on a timer.
5. After the last prompt, the player keeps walking `finalBiomeWalkDistance` more world units
   (default 400) *inside* the last biome before the run actually ends — otherwise that biome's
   FPS/RAM segment would close almost immediately with near-zero samples.
6. Writes `run_<timestamp>.txt` (human-readable) and `.csv` to the `Benchmark/` folder under
   [wherever generated files live](#where-files-live) for this run mode (Editor vs. build).

**Per biome, per run**: generation time, new-vs-cached asset counts (→ reuse %), FPS
(avg/min/1%-low, via `Benchmark/BenchmarkMetricsLogger.cs`), RAM (`Profiler.GetTotalAllocatedMemoryLong()`
— avg/peak, as both a system-RAM % and absolute MB), and the latency of every individual HTTP call
to the LLM/image-gen/3D-gen servers, broken down by call type (`Benchmark/LlmCallMetrics.cs`).

**Two things worth knowing before trusting the numbers**:
- The reuse % includes hits on the pre-shipped `Resources/Baseline/objects/` library (~250
  objects) — even a from-scratch run isn't measuring "0 assets ever existed", it's measuring "0
  assets generated *this session*". If you need those two separated, that's not implemented yet.
- `WorldManager`'s "central path" (a flattened corridor along the player's walking line, controlled
  by `pathWidth`/`pathBlendWidth`/`pathLevel` on `WorldManager`) is **off by default in every
  scene** (`pathWidth = 0`) even though `enablePath` is `true` — set nonzero values on the
  `WorldManager` component in the scene if you want it (the benchmark scene currently uses
  `pathWidth=20, pathBlendWidth=16, pathLevel=24`).

If a biome ever comes out with terrain but zero placed objects, check the Console/log for a
`[PlacementDiag]` line (logged every time a biome loads, from `WorldManager.LoadLatestConfig()`) —
it prints that biome's `slopeMaxLimit`/`objectDensityThreshold`/`waterLevel`. An unusually low
`slopeMaxLimit` (outside the intended ~0.5–3 range) makes `Chunk.ComputeLayoutMap()` reject nearly
every placement cell as "too steep", which looks exactly like an empty biome despite terrain and
asset generation both succeeding normally.

## Editor tools

Under the **Tools** menu (`Assets/Editor/`):

- **Tools → Prepare Baseline Assets for Build** (`BuildAssetPacker.cs`) — copies whatever is
  currently generated on disk (`Assets/Json/*.json`, `Assets/GeneratedTexture/TextureAtlas*.png`,
  every `.voxjson` under `Assets/Json/objects/`) into `Assets/Resources/Baseline/`, renaming as
  needed for `TextAsset`/`.bytes` loading. Run this **before building** whenever you want the build
  to ship with an updated baseline object library — otherwise it ships whatever was baked in last.
- **Tools → Clear Generated Content** (`ClearGeneratedContent.cs`) — one-click reset of all
  generated content (`GeneratedImages`, `GeneratedAsset` except `VoxelVertexColor.mat`,
  `Json/objects`, the four registry JSON files, `GeneratedTexture`) with a confirmation dialog.
  Equivalent to what the benchmark does automatically on launch, but callable any time from the
  Editor for a manual clean slate.

## Known quirks

- **RAM sampling**: `Benchmark/BenchmarkMetricsLogger.cs` used to read
  `System.Diagnostics.Process.WorkingSet64`, which silently returns 0 on IL2CPP builds (a known
  .NET/IL2CPP limitation, not specific to this project) — it now uses
  `UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()`, which works identically in the
  Editor and in any build backend.
- **LM server context window**: the chat-completion server has a tight per-slot context window —
  keep prompt growth in `Prompts.cs` short; raising LM Studio's configured Context Length is the
  fix if you hit "Context size has been exceeded" errors, not something fixable from the Unity side.
