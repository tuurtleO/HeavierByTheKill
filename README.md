# Heavier by the Kill — Dark Souls Remastered

Every enemy slain adds mass to the equipped weapon. Fresh weapons are fast but deal only 62% of normal damage; kills buy uncapped damage, impact, stagger, and knockback at the cost of attack speed, stamina, and recovery.

This build targets **DSR app 1.03.1 / regulation 1.04** and is for offline play only.

## Mechanics

- Ordinary kills add temporary weight to the weapon family responsible for the lethal soul reward.
- Bosses add 12 weight, permanently lock 35% of it, and grant a Predator, Titan, or Arcane legacy trait. The acquired type appears in the event notification and controller log. Every legacy benefit also adds an attack-speed, stamina, or recovery penalty.
- The bonfire main menu offers an explicit R1/F8 action that removes 40% of temporary weight once per seated session. Opening Level Up, Kindle, Reverse Hollowing, warping, or their dialogs cannot trigger reductions.
- Fresh weapons deal `0.62×` damage and attack at `1.15×` speed. Damage then grows by `0.045×` per weight with no plateau.
- Weight slows attacks down to a `0.40×` floor, lengthens recovery, increases stamina costs up to `4.00×`, and raises poise and guard impact.
- Knockback begins at 18 weight and keeps growing for every weapon category. R2 heavy attacks above 35 weight also gain an expanding collision shock radius that can stagger or knock down nearby enemies, whether the weapon is a dagger, light, standard, heavy, or colossal weapon.
- Heavy and running attacks receive more momentum than quick attacks. Overspending stamina empties the bar and causes an additional exhaustion recovery.
- R2 heavy attacks at 20+ weight produce a zero-centered camera shake for every weapon category. Critical animations keep authored speed and knockback so backstabs and ripostes remain aligned.
- Attack speed bottoms out at `0.40×` and stamina cost tops out at `4.00×`, including recovery and boss-legacy calculations. Damage, impact, radial radius, and knockback remain uncapped.
- Standard weapons retain the global speed curve but receive 15% less weight-derived stamina growth by default. `standard_stamina_penalty_multiplier` makes that adjustment configurable.
- Daggers gain weight quickly but convert less into impact; great and colossal weapons gain it more slowly and hit harder.
- Death places 50% of temporary weight into the bloodstain. Recovering it restores that weight.
- The controller reports Light, Tempered, Burdened, Crushing, Devastating, Worldbreaker, and Cataclysmic tiers. Every boundary is configurable.
- A click-through DSR-styled HUD shows the weight tier, smaller weapon-type metadata, separate total/temporary/permanent rows, a temporary-versus-permanent composition bar, and damage/speed/stamina stat cells. Contextual messages report kills, rests, bloodstains, boss legacies, and reforging; the HUD appears only while DSR is focused.

Progress follows the base weapon across reinforcement and infusion changes, so upgrading a +4 weapon to +5 does not reset it. 
!! Duplicate copies of the same weapon family share progression.

### Weapon reforging

At any blacksmith, open **Reinforce Weapon** and press **R1** once or press **F8**. 
Each use converts up to 20 temporary weight on the currently equipped weapon into permanent weight for exactly 1,000 souls per weight, for a maximum displayed price of 20,000 souls. 
Normal reinforcement materials are not required.

Conversely, you can use any Bonfire or base menu to reduce `40%` of your current temporary weight.

## Install and play

Download the Windows ZIP from the GitHub Releases page and extract the entire folder. No separate .NET or Rust installation is required.

1. Double-click `INSTALL MOD.cmd`. It detects the Steam library automatically and prompts for the game folder only when detection fails.
2. Start Dark Souls Remastered, choose **Offline**, and load a character.
3. Double-click `START MOD.cmd` from the extracted release folder and leave its window open while playing.

Running `INSTALL MOD.cmd` again updates the binaries while preserving customized settings and all per-character progression. The installer verifies the supported executable, creates only the `HeavierByTheKill` subfolder, and does not replace the game executable, regulation file, or proxy DLLs, avoiding conflicts with ReShade and Lordran by Sound.

Advanced users can run `install.ps1 -GameDir 'E:\SteamLibrary\steamapps\common\DARK SOULS REMASTERED'` directly to override detection.

The installer verifies the supported executable, creates only the `HeavierByTheKill` subfolder, updates mod binaries, preserves progression and customized settings, and appends newly introduced settings. Existing `HeavierByKill` installations are migrated automatically. It does not replace the game executable, regulation file, or proxy DLLs, avoiding conflicts with ReShade and Lordran by Sound.

The controller enforces a single running instance. Launching it again reports that the mod is already active instead of creating a second overlay or applying combat changes twice. No original game DLL is replaced: uninstalling removes the controller, overlay, core, and input bridge together.

## Record the HUD in OBS

OBS Game Capture cannot see the desktop HUD because it captures only DSR's rendered framebuffer. The controller therefore provides the same HUD as a transparent, loopback-only Browser Source:

1. Keep the DSR **Game Capture** source in the scene.
2. Add a **Browser** source above it.
3. Use `http://127.0.0.1:27361/` as the URL.
4. Set the Browser source to **430 × 492** pixels.

The source mirrors weight, weapon type, modifiers, temporary/permanent composition, notifications, and contextual bonfire/reforge prompts. It can be resized and positioned independently in the OBS preview. The server accepts connections only from the local computer and exposes display values, not game-memory access or mod actions.

`obs_overlay_enabled` and `obs_overlay_port` are available in `heavier_by_the_kill.ini`; restart the controller after changing either. The installed `OBS-SETUP.txt` contains the same setup steps.

## Remove

Double-click `UNINSTALL MOD.cmd`, or run the installed `HeavierByTheKill\uninstall-mod.ps1`. The uninstaller validates its marker, backs up the legacy global save and the complete per-character profile directory beside the mod folder, and removes only the mod subfolder. No original game files need restoration.

## Build and verify

```powershell
cargo fmt --check
cargo test --all-targets
cargo build --release
dotnet build controller\HeavierByTheKill.Controller.csproj -c Release
```

To produce the exact self-contained ZIP used by GitHub Releases:

```powershell
.\build-release.ps1
```

The ZIP and its SHA-256 checksum are written to `artifacts`. Pushing a tag such as `v0.1.0` runs the included GitHub Actions workflow, repeats all tests, builds the ZIP, and creates the release automatically.

The adapter uses executable signatures for world, menu, class, and event-flag globals plus the verified DSR 1.03.1 damage-entry layout. Technical details are in `docs/runtime-adapter.md`.

### Configuration 

Primary balance values and all HUD tier boundaries are editable in `heavier_by_the_kill.ini`. Damage uses a configurable linear-plus-quadratic curve, so each later point of weight grants more damage; set `damage_acceleration_per_weight_squared=0` to restore linear growth. Double-click `edit-heavier-by-the-kill-config.cmd` in the installed mod folder for convenient access, save the file, then restart the controller. The INI documents every setting and uses `1.00` for an unmodified game multiplier. Progress is stored per character name under `HeavierByTheKill\profiles`, so switching DSR saves switches weight, kills, reforging, legacies, and bloodstain state instead of combining them. Character names are matched case-insensitively; two characters with the same name intentionally share one profile.
