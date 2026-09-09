# SpeedrunGym

Contains utilities for speedrun practice. Feature ideas welcome.

Can be configured using BepInEx configuration, e.g. at runtime
using [ConfigurationManager](https://thunderstore.io/c/hollow-knight-silksong/p/jakobhellermann/BepInExConfigurationManager/).

## RNG Normalization

### Craw pogo

Normalize how early the first craw dives.

- `Early` - dive as soon as possible
- `Late1` - dive after one more flap cycle
- `Late2` - dive after two more flap cycles

## Pogo Endlag Detection

When enabled, will display why a pogo endlag cancel failed, or how close it was to being optimal.

![pogo endlag ui](https://raw.githubusercontent.com/jakobhellermann/silksongmod-SpeedrunGym/main/docs/pogo-endlag.png)

Possible popup messages:

- `repress +Xms` - success, re-pressed direction X ms after the neutral landing.
- `jump +Xms` - jumped X ms after the earliest possible jump, shown on a second line.
- `dir repress Xms early` - failed, you pressed a direction X ms too early.
- `neutral Xms late` - you went neutral X ms too late.
- `no neutral` - you never went neutral.

## Jump Timing

Two independently toggleable air-jump readouts, shown as popups next to Hornet.

![jump timing ui](https://raw.githubusercontent.com/jakobhellermann/silksongmod-SpeedrunGym/main/docs/jump-timing.png)

- `Sprintjump Release` - out of a sprintjump, shows the vertical velocity at the moment you released jump. `yvel ≈ 0`
  means you let go right at the peak, `-` = still gaining height, `+` = falling.
- `Repress` - if you repress jump in order to get float, show how long the repress took
