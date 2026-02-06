# AudioService (New Audio System)

This project uses the new audio system under `Assets/Audio/` in the `Game.Audio2` namespace.

The goal is **low-friction tuning**: each sound is configured in a `SoundCue` asset (variants + distance + priority + limits), with bus volumes controlled via `AudioMixer` exposed parameters.

See `Assets/Audio/README_Audio_Categories.md` for the project’s **bus/category definitions**, **current cue IDs**, and **LUFS targets**.

## Quick start
1. Create an `AudioConfig` asset (Create > AudioService > Audio Config)
2. Assign:
   - `AudioMixer`
   - an `AudioSource` prefab
   - bus output groups + exposed volume param names
3. Create a `SoundCatalog` asset (Create > AudioService > Sound Catalog)
4. Create `SoundCue` assets (Create > AudioService > Sound Cue)
5. Add cue entries to the catalog (id → cue)
6. Add an `AudioService` to a bootstrap scene, or rely on its runtime auto-create
7. (Optional) Add `AudioServiceEventBusBridge` if you want to trigger audio via EventBus string-id events

## Validation tooling
Use the menu item:

`Tools > AudioService > Validate Selected SoundCatalog`

to catch missing/duplicate IDs and cues with no valid clips.

