# BeatAvatars

First-person body presence using the **base game's own Beat Avatar** instead of a custom model.
Source in [src/BeatAvatars](../src/BeatAvatars). Targets 1.45.0.

Written from scratch rather than by porting CustomAvatars, which targets 1.41.1 and depends on
FinalIK and DynamicBone. The Beat Avatar has no IK, no bones beyond four, no DynamicBone and no
custom shaders, so almost all of that machinery is irrelevant here.

## What the game gives you

Three shipped assemblies do the work: `BeatSaber.AvatarCore`, `BeatSaber.BeatAvatarAdapter`,
`BeatSaber.BeatAvatarSDK`. The public API is close to purpose-built for this:

```csharp
Task<Avatar> IAvatarSystem.InstantiateAvatar(AvatarDisplayContext ctx, int levelOfDetail, DiContainer c)
void Avatar.SetPoseDataProvider(IAvatarPoseDataProvider)      // we supply this
void Avatar.SetVisualDataProvider(IAvatarVisualDataProvider)  // we supply this
struct AvatarPoseData { Pose headPose, leftHandPose, rightHandPose; }
```

`BeatAvatar.UpdateAvatarFromPose` forwards straight to
`BeatAvatarPoseController.UpdateTransforms`, which writes **local** positions and rotations onto
four bones — head, left hand, right hand, body — and derives the body from the head with a fixed
neck offset. That is the entire rig.

## Facts established by measurement

Each of these cost a run, and several contradicted a reasonable-looking assumption.

### The bindings are on a scene context, not the project context

`AvatarsAsyncInstaller` binds `AvatarSystemCollection` onto **`AppCoreSceneContext`**
(`BGLib.AppFlow.Initialization.AsyncSceneContext`), a child of `ProjectContext`. Resolving from
`ProjectContext.Instance.Container` returns null forever. The first probe run spun silently for its
whole duration because of this — hence the "never wait in silence" logging in the controller.

Scan `Zenject.Context` components instead and take whichever container answers; a child container
resolves everything its parents bind.

### The prefabs ship on layer 0, and there are only three of them

Six `AvatarDisplayContext` values reach three distinct prefabs — `BeatAvatar` (Unknown, UI,
MultiplayerLobby, MultiplayerGameplay), `BeatAvatarResults`, `BeatAvatarHologram` — and all three
have **identical hierarchies**, on **layer 0 (Default)**, not layer 10. Any difference between them
is materials or scale, not structure. Picking a different display context buys nothing.

### The head mesh is not one of the named part fields

`BeatAvatarVisualController` names five head parts (head top, glasses, facial hair, eyes, mouth).
The head itself is not among them. Classifying head geometry field-by-field leaves a bare face
floating in your view; take the head **bone's whole subtree** instead, which is head by
construction.

### Mouth, glasses and facial hair are shipped switched off

Inactive in the prefab, before any visual data. `UpdateAvatarVisual` assigns their mesh or sprite
anyway and never activates them, and there is **no `SetActive` call anywhere in
`BeatSaber.BeatAvatarSDK`** — so nothing the game does could make them render.

`BeatAvatarPartReveal` activates them when the visual update actually put something in them, and
that was verified against a synthetic `AvatarData` carrying `Glasses01` and `Beard01`: both objects
go from populated-and-inactive to active with their meshes.

**But no player can reach it, because the game's avatar editor cannot select those parts.**
`BeatAvatarEditorViewController` sets up exactly four value pickers — head top, hands, clothes,
eyes — and glasses and facial hair appear nowhere else in the whole adapter except the network
serialiser. The `AvatarPart` enum tells the same story from the other side: it has `HeadTopModel`,
`HandsModel` and `ClothesModel`, and for glasses and facial hair only `GlassesColor` and
`FacialHairColor` — the *model* entries were removed while the colour ones were left behind.

So glasses and facial hair are a retired feature: meshes still shipped (2 glasses, 3 facial hair),
data still serialised, editor gone, prefab objects disabled. The reveal only has an effect for a
save that already carries a non-`None` id — an older save, a hand-edited one, or a mod that sets it.

**Pickers for both were built, tried, and removed.** They worked: the settings panel offered the
retired parts, writing them through `AvatarDataModel.avatarData` reached the body and the mirror
live, and the operator confirmed in VR that glasses and facial hair render correctly on the head.
They were dropped because the *models themselves* are not good enough to want — which is a fair
guess at why the game retired them. Removed in a single commit and easy to restore from history if
a later version improves the art.

Two things that had to be right, and are worth knowing if it is ever revived:

* **Clone, modify, assign — never mutate in place.** `AvatarDataModel.avatarData`'s setter is what
  calls `ReportAvatarChanged`, and it only fires when the object it is given differs from the one
  it holds. Editing the held object changes the avatar and tells nobody.
* The **preview** has its own `BeatAvatarPartReveal`, so refreshing only the body's leaves the part
  missing from the mirror — the very mirror being used to look at it.

`BeatAvatarPartReveal` itself is kept. It costs nothing when the ids are `None`, it is what makes a
save that already carries one render correctly, and it would start working on its own if a later
version restored the editor UI.

Mouth is further gone still: **all twelve entries have a null sprite**, so there is no art to
reveal even if something did select them.

Part lookups themselves are fine — all seven collections report `HIT`. An eyes id of `Eyes11`
resolving to a sprite asset named `Eyes4` is just asset naming; ids and asset names do not
correspond.

### `IAvatarSystem.avatarDidChangeEvent` is dead

The obvious hook for "the player edited their avatar". Its only raiser is
`AvatarSystem.RaiseAvatarDidChangeEvent`, which is `protected` and has **no caller in any of the
255 game assemblies** — the string occurs in `BeatSaber.AvatarCore.dll`, where it is defined, and
nowhere else. Subscribing compiles, runs, and silently never fires.

The live signal is `AvatarDataModel.didChangeAvatarDataEvent`, raised by `ReportAvatarChanged` from
the `avatarData` setter, which is what the game's own avatar editor listens to. The new
`AvatarData` arrives with the event, so the body updates from an edit **before it is saved to
disk**. Verified firing in VR.

### The controller transform is not where your hands are

`VRController.Update` writes the **raw tracked node pose** onto its own transform, and
`VRController.position`/`rotation` return exactly that. The player's controller position and
rotation settings are applied by `TryGetControllerOffset` onto **`_viewAnchorTransform`**, a child —
which is what the saber is mounted on.

Follow the anchor, or the avatar's hands sit at a subtly different angle from the saber the player
is actually holding. Logged per spawn as `leftHand=ok(MenuHandle)` so a silent fallback is visible.

### Resolve the rig continuously, not once

Dismissing the health warning reloads the menu scene. The avatar respawns into a half-built scene
where `Camera.main` and the `MenuControllers` do not exist yet, and a rig resolved once at spawn
stays broken for the session — the avatar freezes with both hands at their fallback rest pose.
**In fpfc the transition is fast enough that this never happens**, so only a VR run could find it.

Guarded two ways: do not spawn while `Camera.main` is null, and re-resolve anything that goes
missing.

### A mirror image cannot be a rotation

The tuning preview is a second avatar reflected by a container with **negative Z scale**, fed the
player's poses completely unchanged (CustomAvatars' "fake mirror").

The first attempt mirrored the poses instead — reflecting each position and rebuilding each
rotation from a reflected forward and up. The body looked right and the hands did not, and that
split is the diagnosis: the body's orientation is yaw-only, derived from the head, so almost any
plausible mirror gets it right. The hands carry full 3D orientation **and chirality**, and a
reflection is orientation-reversing — a mirrored right hand is a *left* hand, which no rotation can
express. A negative scale can, because it is an actual reflection.

With the container at distance *d*, a bone at local *z* lands at *d − z*: the apparent mirror
surface is at **half** the container distance.

## Layers

See [migrations/urp-layer-masks.md](migrations/urp-layer-masks.md) — the URP renderer applies its
own layer masks after the camera's, and layer 3 was missing from them. That is why the head
rendered nowhere despite every camera mask being correct.

Body and hands go on layer 10 ("Avatar"), the head bone's subtree on layer 3 ("third person only"),
matching the CustomAvatars/Camera2 convention. We touch the HMD camera's mask and nothing else.

## Settings

A dedicated **"Beat Avatars"** menu button opening its own `FlowCoordinator`, not a Mod Settings
tab. Mod Settings is a narrow modal that fills the space in front of the player, which is where the
preview has to go. CustomAvatars reaches the same conclusion for the same reason.

`UserData/BeatAvatars.json`, also editable by hand:

| key | meaning |
|---|---|
| `handScale`, `headScale`, `bodyScale` | bone scales; 1.0 is the game's own size |
| `headVerticalOffset`, `bodyVerticalOffset` | raise/lower the head or torso **visuals**, in metres |
| `handPositionOffset` | grip offset in the anchor's frame; `z` runs along the handle |
| `handRotationOffset` | extra hand rotation in degrees, anchor frame |
| `useControllerOffsets` | follow the saber anchor (true) or the raw controller pose |
| `hideHeadInFirstPerson` | applies at spawn, so it needs a scene change |
| `previewPosition` | mirror container offset; apparent mirror at half the `z` |

Scales are set on the **bone**; vertical offsets on the bone's **visual child**. Neither can be the
other way round: the pose controller rewrites each bone's local position every frame, so a position
set there is gone by the next pose event, while the child is never touched. Using the child for the
head also decouples it from the body, which `UpdateBodyPosition` derives from the head *bone*.

## Verified in VR by the operator

No own head from inside; scale correct; Camera2 third-person view shows the head; 360° maps show
correct views; gameplay pose tracking through a full song; mirror hands correct; settings panel
functional, including both vertical sliders.

## Not yet done

* Layout of the per-slider undo column is unconfirmed — a slider and a button side by side is the
  arrangement this repo has lost attempts to before (see the BSML width trap in CLAUDE.md).
* The **gameplay** hand anchor has never been named in a spawn line; all panel runs stayed in the
  menu. If it ever logs `ok(no anchor)`, the fallback is firing.
* `hideHeadInFirstPerson` only takes effect on a scene change.
* Custom avatar parts are unexplored. Eyes and mouths are `Sprite`s — loadable from PNG at runtime,
  no asset bundle, and mouths are currently empty so adding any would restore a missing feature.
  Head-tops, glasses, facial hair and clothes are `Mesh`es and would need an asset bundle plus the
  avatar's UV conventions, since `MulticolorAvatarPartPropertyBlockSetter` tints by UV segment.
