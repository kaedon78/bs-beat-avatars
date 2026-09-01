# Notes

What a maintainer needs to know about the game's avatar system, and the rules this mod follows
because of it. Every entry here is something the code depends on and that reading the code alone
will not tell you.

Source in [src/](src/). Targets 1.40.5; see `BS_1.45.0` for the newer game.

## The rig

Three shipped assemblies do the work: `BeatSaber.AvatarCore`, `BeatSaber.BeatAvatarAdapter`,
`BeatSaber.BeatAvatarSDK`.

```csharp
Task<Avatar> IAvatarSystem.InstantiateAvatar(AvatarDisplayContext ctx, int levelOfDetail, DiContainer c)
void Avatar.SetPoseDataProvider(IAvatarPoseDataProvider)      // we supply this
void Avatar.SetVisualDataProvider(IAvatarVisualDataProvider)  // we supply this
struct AvatarPoseData { Pose headPose, leftHandPose, rightHandPose; }
```

`BeatAvatar.UpdateAvatarFromPose` forwards to `BeatAvatarPoseController.UpdateTransforms`, which
writes **local** positions and rotations onto four bones — head, left hand, right hand, body — and
derives the body from the head with a fixed neck offset. That is the entire rig: no IK, no
skeleton, no skinning. It is why this mod needs neither FinalIK nor DynamicBone.

## Never cache a Zenject container

The most expensive lesson here, and it caused three separate bugs.

`AvatarsAsyncInstaller` binds `AvatarSystemCollection` onto **`AppCoreSceneContext`**, a child of
`ProjectContext` — so resolving from `ProjectContext.Instance.Container` returns null forever. Scan
`Zenject.Context` components and take whichever container answers; a child resolves everything its
parents bind.

Having found it, **do not keep it**. Applying anything in the game's own Settings rebuilds that
context, and a dead container *still resolves*. It does not throw, return null, or complain: it
hands back an `AvatarPartsModel` built from destroyed ScriptableObjects, whose arrays keep their
lengths so every count looks right, while every `id` reads null and every mesh lands null.

The symptom is an avatar that spawns, tracks perfectly, sits on the correct layers, is active and
unscaled — and wears nothing. Only the head remains, because that mesh is baked into the prefab
while head-top, clothes and hands come from `AvatarData`. From inside you see almost nothing, since
the head is culled from your view; in a mirror you see a bare head.

Two more failures share the shape — something captured once from a container and never revisited:

* the **menu button**, because BSML binds `MenuButtons` into the *menu* container, which is rebuilt
  whenever settings are applied, leaving a fresh instance with an empty button list;
* the **flow coordinator**, because `BeatSaberUI.CreateFlowCoordinator` puts it on a plain
  GameObject in the current scene, so it dies with that scene.

Each is patched separately. Taking a SiraUtil dependency — `Zenjector.Install(Location.App, …)` —
would make the whole class impossible and delete the context scan, the retry burst and the
`Instance` static with it.

## URP filters layers twice

A camera's culling mask is not the only filter. The `ScriptableRenderer` ANDs its own
`opaqueLayerMask` and `transparentLayerMask` on top, so a layer missing from those renders on **no
camera**, however that camera is configured — and no camera-level check can see it.

**1.40.5 does not run URP at all** -- it is the built-in pipeline, ships no
`Unity.RenderPipelines.*` assembly, and `GraphicsSettings.currentRenderPipeline` is null. The fix
below is inert here and is reached only by reflection, so this branch keeps it without the
reference. It matters from Unity 6 onward.

1.45.0 omits layer 3 from both, which is the layer CustomAvatars and Camera2 use for
"third person only". Full write-up in [migrations/urp-layer-masks.md](migrations/urp-layer-masks.md).

Body and hands go on layer 10 ("Avatar"), the head bone's subtree on layer 3. Touch the HMD
camera's mask and nothing else: Camera2 owns its cameras and implements the same convention, and
its `Cam2_WindowOwner` carries an empty mask deliberately.

## The avatar prefabs

* Six `AvatarDisplayContext` values reach **three** prefabs — `BeatAvatar`, `BeatAvatarResults`,
  `BeatAvatarHologram` — with **identical hierarchies**. Any difference is materials or scale.
  Choosing a different display context buys nothing.
* They ship on **layer 0**, not layer 10. The layer assignment is entirely this mod's doing.
* The head mesh is **not** one of the five named head-part fields (head top, glasses, facial hair,
  eyes, mouth). Classify head geometry by the head **bone's subtree**, not field by field, or a
  bare face is left floating in the player's view.

## Retired avatar parts

Glasses, facial hair and mouth are inactive in every prefab. `UpdateAvatarVisual` fills their mesh
or sprite anyway and never activates them, and nothing in `BeatSaber.BeatAvatarSDK` calls
`SetActive` — so they are populated and permanently invisible.

They are also unreachable. `BeatAvatarEditorViewController` sets up exactly four value pickers —
head top, hands, clothes, eyes — and the `AvatarPart` enum keeps `GlassesColor` and
`FacialHairColor` while having lost the matching `…Model` entries. A retired feature with its art,
data and serialisation left in place.

`BeatAvatarPartReveal` activates whatever the visual update actually filled. It costs nothing while
the ids are `None`, handles a save that already carries one, and would start working by itself if a
later version restored the editor UI. Pickers for glasses and facial hair were built and removed;
the meshes render correctly but are not good enough to want.

Mouth cannot be revived at all: **all twelve entries have a null sprite**.

Part lookups themselves are sound. An eyes id of `Eyes11` resolving to a sprite named `Eyes4` is
just asset naming — ids and asset names do not correspond.

## `IAvatarSystem.avatarDidChangeEvent` is dead

The obvious hook for "the player edited their avatar". Its only raiser is `protected` and has **no
caller in any of the 255 game assemblies**. Subscribing compiles, runs, and silently never fires.

The live signal is `AvatarDataModel.didChangeAvatarDataEvent`, which the game's own editor listens
to. The new `AvatarData` arrives with the event, so the body can update before the edit is saved.
Take `didSaveAvatarDataEvent` as well: the change event comes off the `avatarData` **setter**, so an
editor that mutates its copy in place and saves would never raise it.

Writing avatar data has the same trap in reverse — **clone, modify, assign**. Mutating the held
object changes the avatar and tells nobody.

## Poses

* They must be **local to the bone's own parent**. World poses work only while that parent sits at
  the origin unrotated, and break the moment the room offset or a 360 map turns it.
* Follow the **saber anchor**, not the controller. `VRController.Update` writes the raw tracked node
  pose onto its own transform, and `position`/`rotation` return that; the player's grip settings are
  applied to `_viewAnchorTransform`, a child, which is what the saber is mounted on.
* **Re-resolve the rig continuously.** A scene can be rebuilt around the avatar, and at the instant
  it respawns `Camera.main` and the controllers may not exist. A rig resolved once stays broken, and
  a frozen avatar is still a fully drawn avatar, so it reads as a rendering fault.

## fpfc and VR disagree

Flat mode is not a substitute for one headset run. The menu's `VRController` components are
`enabled=false` with `mouseMode=true` under fpfc and enabled under VR, so an `isActiveAndEnabled`
check silently rejects both hands in flat runs only. Scene transitions are also faster in fpfc,
which hides races that VR reproduces every time.

## Scale on the bone, offsets on its child

The pose controller rewrites each bone's local position every frame, so a position set there is
gone by the next pose event. A bone's own **scale** is unaffected by that, and its **child** is
never touched.

So: scales go on the bone, vertical offsets on the bone's visual child. Using the child for the head
also keeps it independent of the body, which `UpdateBodyPosition` derives from the head *bone*.

`UserData/BeatAvatars.json`:

| key | meaning |
|---|---|
| `handScale`, `headScale`, `bodyScale` | bone scales; 1.0 is the game's own size |
| `headVerticalOffset`, `bodyVerticalOffset` | raise/lower the head or torso **visuals**, in metres |
| `handPositionOffset` | grip offset in the anchor's frame; `z` runs along the handle |
| `handRotationOffset` | extra hand rotation in degrees, anchor frame |
| `useControllerOffsets` | follow the saber anchor (true) or the raw controller pose |
| `previewPosition` | mirror container offset; apparent mirror at half the `z` |

## Open work

* **Take the SiraUtil dependency** and delete the three container workarounds. The largest cleanup
  available, and it removes a class of bug rather than another instance of it.
* The **gameplay** hand anchor has never appeared in a spawn line; every panel session so far stayed
  in the menu. `ok(no anchor)` in the log means a fallback pose is in use.
* Custom avatar parts are unexplored. Eyes and mouths are `Sprite`s, loadable from PNG at runtime
  with no asset bundle, and the mouth collection is empty so anything added would restore a missing
  feature. Meshes would need an asset bundle plus the avatar's UV conventions, since
  `MulticolorAvatarPartPropertyBlockSetter` tints by UV segment.
