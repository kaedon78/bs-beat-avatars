# Beat Avatars

First-person body presence in Beat Saber using **the game's own Beat Avatar** — the character you
already made in the avatar editor and that other players see in multiplayer — instead of a custom
model.

Targets **Beat Saber 1.45.0**.

![built for 1.45.0](https://img.shields.io/badge/Beat%20Saber-1.45.0-blue)

## What it does

* Shows your Beat Avatar's body and hands from the inside, in the menus and in gameplay.
* Hides your own head from your own view, while keeping it for mirrors and third-person cameras.
* Follows your **controller grip settings**, so the hands sit where the sabers do.
* A settings panel with a live mirror, so you can size and position everything while watching it.
* Updates immediately when you edit your avatar in the game's own editor.

It deliberately does **not** load custom avatar models. If that is what you want, use
[CustomAvatars](https://github.com/nicoco007/BeatSaberCustomAvatars) — this is the small,
dependency-light alternative for people who just want to see the body they already have.

## Requirements

* Beat Saber 1.45.0 with BSIPA
* [BeatSaberMarkupLanguage](https://github.com/monkeymanboy/BeatSaberMarkupLanguage) (BSML) — for
  the settings panel

No FinalIK, no DynamicBone, no asset bundles. The Beat Avatar has four bones and no IK, so none of
that is needed.

## Installing

Drop `BeatAvatars.dll` into your `Plugins` folder.

## Settings

A **Beat Avatars** button on the main menu opens the panel. A mirrored copy of your avatar
appears in front of you while it is open, so every change is visible as you make it.

| Setting | What it does |
|---|---|
| Hand / head / body size | Scales each part. `1.0` is the game's own size; the Beat Avatar is drawn for a multiplayer lobby, so it tends to read as oversized up close. |
| Head / body height | Raises or lowers each independently. |
| Grip position | Slides your hands along the controller to where you actually hold it. |
| Hand height / sideways | Nudges the hands relative to the controller. |
| Hand tilt / turn / twist | Rotates the hands, on top of your own grip settings. |
| Match my grip | Follow the saber anchor, honouring your controller position and rotation settings. Off uses the controller's raw pose. |

Each slider has an **undo** button that puts that one setting back to what it was when you opened
the panel — greyed out until you change it. **Undo all** does the lot; **Defaults** starts over from
the shipped values.

Settings are saved when you close the panel, into `UserData/BeatAvatars.json`, which you can
also edit by hand.

## Building

**No game binary is committed to this repository.** Beat Saber's assemblies are not
redistributable, so the build reads them out of an install you already have. Point it at one, in
order of precedence:

1. `dotnet build -c Release -p:BeatSaberDir="C:\Path\To\Beat Saber" BeatAvatars.csproj`
2. a `BEAT_SABER_DIR` environment variable
3. a `beatsaberdir.txt` file at the repository root containing just the path (gitignored)

The install needs BSIPA applied (for `IPA.Loader.dll` in `Beat Saber_Data\Managed\`) and BSML
present (`Plugins\BSML.dll`). The build fails with a message saying which is missing rather than
emitting a DLL against whatever happened to be lying around.

## Notes

[NOTES.md](NOTES.md) records what had to be measured to make this work, including several things
that are not obvious — the URP renderer's second layer filter, an interface
event the game never raises, and why a mirror image cannot be a rotation. Worth reading before
changing anything.

## Releasing

Tag and branch naming is documented in [RELEASING.md](RELEASING.md), and is shared with the other
Beat Saber mods alongside this one.

## Licence

MIT — see [LICENSE](LICENSE).
