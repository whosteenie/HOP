# Audio Categories (AudioService)

This project uses `Game.Audio2` with **AudioMixer bus routing**. Each `SoundCue` routes to a `SoundBus` (category) which maps to an `AudioMixerGroup` via `AudioConfig`.

## Why categories exist
- Give you **separate volume sliders** and mixing control (via AudioMixer exposed parameters).
- Keep authoring sane: you tune a cue (distance/priority/limits) once, and the bus handles broad mixing.

## Bus list (SoundBus)
- **Master**
- **Sfx**
- **Ui**
- **Weapons**
- **Foley**
- **Ambience**
- **Music**
- **Gameplay**

## Recommended loudness targets (offline batch)
These are **starting points** for batch processing new assets. Final balance should be done with AudioMixer + per-cue `volumeDb`.

Use **LUFS-I** targets and a **true-peak limit of -1 dBTP** (recommended).

- **Music**: **-20 LUFS-I**
- **Ambience**: **-26 LUFS-I**
- **UI**: **-18 LUFS-I**
- **Weapons**: **-20 LUFS-I**
- **Foley**: **-24 LUFS-I**
- **Gameplay** (world interaction stingers like JumpPad/Grapple/Hopball): **-22 LUFS-I**
- **Sfx** (misc one-shots that don’t fit elsewhere): **-22 LUFS-I**

## Current cue IDs by category (SoundCatalog)
These are the current `SoundCatalog` ids you can reference in code (and what they generally represent).

### UI (2D)
- `ui.button.forward`
- `ui.button.back`
- `ui.button.hover`
- `ui.timer`
- `ui.sniper.zoom`
- `ui.weapon.switch`
- `ui.hit.hitmarker.hit`
- `ui.hit.hitmarker.kill`
- `ui.hit.hurt`
- `ui.tag.tagged`
- `ui.tag.tagger`

### Weapons (mostly 3D, authored per cue)
- `weapons.pistol.shoot`
- `weapons.pistol.reload`
- `weapons.deagle.shoot`
- `weapons.deagle.reload`
- `weapons.smg.shoot`
- `weapons.smg.reload`
- `weapons.rifle.shoot`
- `weapons.rifle.reload`
- `weapons.shotgun.shoot`
- `weapons.shotgun.reload`
- `weapons.sniper.shoot`
- `weapons.sniper.reload`
- `weapons.bullet.dry`
- `weapons.bullet.trail`
- `weapons.bullet.impact`
- `weapons.bullet.hurt`

### Foley (movement)
- `foley.tile.walk`
- `foley.tile.run`
- `foley.tile.jump.start`
- `foley.tile.jump.land`
- `foley.slide`

### Gameplay (world interaction stingers)
- `gameplay.jumppad`
- `gameplay.grapple`
- `gameplay.hopball.spawn`

### Music
- `music.main`

### Sfx (misc)
- Any new one-shots that don’t clearly belong in UI/Weapons/Foley/Ambience/Music/Gameplay.

## Inspector changes required
After adding new buses:
- Update your `AudioConfig` asset to include **Music** and **Gameplay** bus configs (output group, exposed mixer param, pool sizes).
- Ensure your AudioMixer (`Assets/MainMixer.mixer`) has matching groups/params if you want sliders for these buses.

