# Dino NES

The Chrome offline dino game as an NES ROM.

**Play it in the browser: https://antonakesson.github.io/dino-nes-game/**

## Controls

| Key | NES | What it does |
|---|---|---|
| Enter | Start | Start a game |
| Space / X / ↑ | A | Jump. Hold for a higher jump, let go early for a short hop. Restarts after a crash. |
| Z / Shift | B | Jetpack, in the air, while the fuel lasts. Fuel refills on the ground. |

Snakes bite from the side. Land on one to bounce off it for +50. A gamepad works too.

## Build

```
dotnet fsi Dino.fsx      # writes output/dino.nes
```

Every push to `main` builds the ROM the same way and publishes it with `docs/` to GitHub Pages.
