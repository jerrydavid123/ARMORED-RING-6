# Tarnished in a Mech (0.1.0, unfinished, made to be improved)

**Elden Ring as the host, Armored Core VI as the source.** You play a Tarnished piloting an AC6 mech (Ayre's IA-C01 EPHEMERA)
with AC6 movement numbers and weapons. Solo, offline only. Nothing here touches anti-cheat; run Elden Ring through
ModEngine2 without Easy Anti-Cheat.

**This is a half-finished prototype.** It works well enough to run around, boost, fly and shoot, and it has several
known rough edges (listed below). It is released so others can take it further.

## What is in the box

| Folder | What it is |
|---|---|
| `mod/` | Rust code add-on (`tarnished_mech.dll`, built against ER 2.7.1.0): AC6 ground boost / quick boost / vertical boost / air model, EN and overheat, auto descent and landing, no-fall-damage layers, the weapon system (rifle, back weapons, blade lunge), under-the-map rescue |
| `converter/Ac6Convert` | C# tool that reads **your own** AC6 and Elden Ring installs and converts armor kits and weapon models, dumps params, extracts archives, renders parts (`render`), lists skeleton nodes (`nodes`) |
| `converter/HkxTool` | C# tool that ports AC6 Havok animations onto the Elden Ring skeleton and patches them into Elden Ring animation files (`port`, `stats`, `sheet`, `bones`) |
| `tools/` | Python and PowerShell glue: AC6 number extraction (`ac6_numbers.py`, `ac6_weapons.py`), regulation builder (`build_regulation.py`), animation batch (`batch_port_anims.py` + `anim_map.json`), `deploy.ps1` |
| `sheets/` | The data the build reads (frames, weapons, systems, derived AC6 stats) |
| `bin/tarnished_mech.dll` | A prebuilt code add-on |
| `MODLOG.md` | A very detailed engineering log of everything tried, what was verified in game and what was not |
| `docs/` | Reference renders of Ayre assembled from the AC6 files, and of the converted body |

## What it does NOT contain

**No FromSoftware or Bandai Namco assets.** No models, textures, animations, params or game files are shipped. Everything is
converted on your PC from the games you own, and the outputs stay on your PC. You need both games installed:
Elden Ring 2.7.1.0 and Armored Core VI.

## Requirements

- Elden Ring 2.7.1.0 (English). The code add-on uses `vswarte/fromsoftware-rs`, which keys offsets to the game version.
- Armored Core VI: Fires of Rubicon (read only, for the conversion).
- ModEngine2 2.1.0 (soulsmods/ModEngine2), launched without EAC.
- To build: Rust (stable, `x86_64-pc-windows-gnullvm` was used), .NET 9 SDK, Python 3, WitchyBND (to unpack/repack
  regulation.bin), `texconv.exe` (Microsoft DirectXTex), and for animation ports `CompressAnim.exe` from
  SoulsAssetPipeline / DSAnimStudio (**not included**: it is Havok tooling; get it from those projects).
- **Paths:** the scripts and project files were written on one PC and use `C:\Users\ddean\modtools\...`, the game folders and
  the OneDrive project folder directly. Search and replace those paths (or recreate the layout) before building. Cleaning
  that up into one config is the first good contribution.

## How it was put together (short)

1. `Ac6Convert extract` pulls named files out of the game archives with the games' own indexes.
2. `tools/ac6_numbers.py` / `ac6_weapons.py` read AC6 params (`MovementAcTypeParam`, `EquipParam*`, `BehaviorParam_PC`,
   `Bullet`, `AtkParam_Pc`) into the sheets; `tools/gen_frames_rs.py` generates the Rust constants from them.
3. `Ac6Convert kit ...` / `weapons ...` build the armor pieces and weapon models.
4. `tools/build_regulation.py` writes `regulation.bin` rows (class, weapons, projectile rows).
5. `tools/batch_port_anims.py` ports AC6 clips onto Elden Ring clip slots (same-id clips plus `tools/anim_map.json`).
6. `tools/deploy.ps1` assembles everything into a ModEngine2 mod folder.

## Known problems (good places to start)

- Hard-coded paths (above).
- **The blade slash animation is janky**: the ported AC6 lunge stance makes the arms cross in front of the chest, and
  AC6's real blade sweep is the weapon's own animation, which is not reproduced. The mod also adds a slash bolt (projectile
  row `99910100`) and a ground-following dash; the bolt was never confirmed visually.
- The shoulder weapon pods (Aurora) are fixed rigid to the torso at AC6's mount points; their pose in game was reported as
  wrong by the author and not yet resolved (see `docs/` renders for the AC6 reference).
- Only the **light frame (Ayre)** is a real AC6 build; the medium and heavy frames still use the Tarnished's own animations
  and generic parts.
- Animations: movement clips are ported (idle, walk, run, sprint, jump, fall, landing, quick boost); there is no layer for AC6
  weapon animations (the `a2xx`/`a3xx` archives are catalogued in `MODLOG.md`), rifle and shoulder-weapon poses are not ported,
  knock-down / get-up clips are not mapped.
- The rifle sits in Elden Ring's dagger slot (always drawn in hand); R1/R2/L2 are disabled and the mod fires the weapons.
- No sounds, no AC6 effects (stock Elden Ring effects are used for the energy shots).
- Damage numbers and enemy hits are not verified in game.

## Install (outline)

Because the converted assets cannot be shipped, installing means building: convert the kit and weapons, port animations,
build `regulation.bin`, then run `tools/deploy.ps1` to get a mod folder, point ModEngine2's `external_dlls` at
`tarnished_mech.dll` and start Elden Ring with that config. `launch-test.ps1` shows one working launch (paths are the author's).
The mod uses its own save file (`ER0000.mec`), so your normal save is untouched.

## Credits

- **ModEngine2**: the soulsmods team. **fromsoftware-rs**: vswarte and contributors (game structures and hooks).
- **SoulsFormats** (JKAnderson, TKGP and forks), **Havoc / SoulsAssetPipeline / DSAnimStudio**: Meowmaritus (GPL-3.0),
  **WitchyBND**, **Paramdex**: the Souls modding community. Archive key facts are the same public ones used by UXM and
  BinderTool.
- Built with **Claude Code (Anthropic), model Claude Sonnet 5.5** driving the work in a long autonomous session with the
  author testing in game. The engineering log in `MODLOG.md` is honest about what is and is not verified.

## Legal

Fan project, no affiliation with FromSoftware or Bandai Namco. Armored Core VI and Elden Ring are theirs. Offline, solo use only.
The tools are licensed **GPL-3.0** (see `LICENSE`), matching the GPL-3.0 projects they build on.
