# ExtractionConfirm

A confirmation-button mod for [R.E.P.O.](https://store.steampowered.com/app/2132060/R_E_P_O_/).

When your haul reaches the extraction target, the game normally starts the countdown
immediately. This mod freezes the extraction right before the **3-2-1 countdown** and only
lets it start once a player presses the **physical button on the extraction point** —
it is not a UI button.

## How the game decides to extract

| State | What happens |
| --- | --- |
| `Idle` | The point waits. Pressing the physical button calls `ExtractionPoint.OnClick()` → `ButtonPress()` → `Active`. |
| `Active` | The extraction area is live; valuables inside count towards `haulGoal`. When `haulGoal` is reached, `HaulChecker()` waits 1.5s and calls `StateSet(Success)`. |
| `Success` | The "!!!!!!!" / check-mark validation, 2 seconds. |
| `Surplus` | Tax-return count-up (only if you went over the goal). |
| `Warning` | **The 3-2-1 countdown with the alarm.** |
| `Extracting` | The tube slams down and the haul is collected. |

The mod blocks exactly one transition — `Success`/`Surplus` → `Warning` — and only until the
button is pressed.

`Warning` is reached by two different routes, and the difference matters: a haul that lands exactly
on the goal goes `Success → Warning`, while an over-target haul goes `Success → Surplus → Warning`.
A confirmation therefore has to survive the `Surplus` step to be useful. Clearing it on any
non-`Warning` transition eats it on `Surplus`, `Warning` is blocked again, and the mod reports
"countdown starting" while nothing happens — so only the states that genuinely end the extraction
(`Extracting`, `Complete`, `Cancel`, `Idle`, `TaxReturn`) drop it.

## The confirm button

The button is **the shop's own button**, spawned onto the extraction point.

That sounds obvious, but it is worth saying why, because the assets make it easy to get backwards.
Dumping every `Extraction Point` prefab out of `resources.assets` shows the shop's button and the
extraction point's button are the **same prefab object**:

| Object | Components | Mesh |
| --- | --- | --- |
| `Extraction Tube / Button Deny Animation / Button` | `Transform, MeshFilter, BoxCollider` + 3 MonoBehaviours | `Button` |
| `Shop Station / Shop Button` | `Transform, MeshFilter, BoxCollider` + 3 MonoBehaviours | `Button` |

Identical component set, identical mesh, identical scale. The shop does not have a special kind of
button; it has the same button **standing on a counter**:

```
Shop Station                                  (0, 0, 0)
  Meshtownusa                                 (2.741, 0.484, 1.169)   the counter
  Cube (1)                                    (2.741, 0.951, 1.424)
  Shop Button                                 (2.742, 1.173, 1.483)   the button, on the counter
  Extraction Point Side Button                (2.750, 0.000, 1.039)   the pedestal beside it
```

So there is nothing to import: the button is already in every extraction point prefab that has a
shop station, and the game throws it away outside the shop:

```csharp
// ExtractionPoint.Start
if (!isShop)
{
    Object.Destroy(shopStation.gameObject);
    return;
}
```

### What the mod does with it

1. **Clone it before it is thrown away.** The clone happens in a prefix on `ExtractionPoint.Start`,
   because vanilla destroys the station in the same method. No AssetBundle, no companion library,
   no authoring step.
2. **Cache the first stand as a template.** Not every extraction point prefab carries a shop
   station, so the first one the mod sees is kept (inactive, parked out of sight, `DontDestroyOnLoad`)
   and every later extraction point is built from it. Without that, a level whose extraction points
   have no station of their own would silently get nothing — and `No confirm button: this extraction
   point has no Shop Station and none was cached yet` says so if it ever happens.
3. **Stand it at the south-west corner of the extraction square, parented to the extraction point
   itself.** Two traps here, both of which look like "it spawned in the wrong place":

   *The station is authored at the extraction point's origin, but its button head sits 2.74 m to
   the side of that origin*, on top of the counter. Parenting the clone to the extraction point and
   stopping there leaves the whole shop station standing 2.7 m away. Only the station root is moved,
   by the negative of the head's offset inside it, so the counter and pedestal keep their authored
   relationship to the button.

   *And the extraction point's own `Button` is not a child of the extraction point.* It is nested
   under the tube:

   ```
   Extraction Point
     Scale
       Extraction Tube          (0, 5.590, 0)   ← vanilla Start drops this to y = 0
         Button Deny Animation
           Button               (0, 1.037, 2.214)
   ```

   so parenting the stand to that button makes it ride the tube, which drops on load and rises again
   when the point opens. The stand is parented to `ep.transform`, which never moves.

   The corner itself, in the extraction point's local frame:

   | Wall | Position |
   | --- | --- |
   | `Main Collider Back` | `(0, 23.09, -1.989)` |
   | `Main Collider Front` — south | `(0, 23.09, +2.026)` |
   | `Main Collider Right` | `(-2.108, 23.09, 0)` |
   | `Main Collider Left` | `(+2.080, 23.09, 0)` |

   The regular button at `z = +2.214` is the centre of the south border. The stand goes to the
   south-west corner at `(2.08, 1.03, 2.03)`, the `StandPosition` constant in `ConfirmGate.cs`.
4. **Move the game's own button to the stand rather than replacing it.** The stand is purely a
   picture; the press is handled by the extraction point's own `buttonGrabObject`, which is relocated
   to the corner. A *cloned* grab object cannot do this job:

   - `StaticGrabObject.Start` does `GetComponent<PhotonView>()` and then
     `photonView.TransferOwnership(...)` in multiplayer — a null reference on a clone Photon never
     registered.
   - The grabbing zone beside the button, `PhysGrabObjectGrabArea`, collects its colliders in **its
     own `Start`** and is only usable while enabled. Disabling it to stop it buying therefore
     leaves the button with no grabbing zone at all, which is exactly the "spawned but not
     pressable" failure.

   Moving the real button keeps its collider, its grab object, its networking and its press
   animation, and works in singleplayer and multiplayer with nothing added.

   It cannot simply be parked, because the game rewrites its local position every frame:

   ```csharp
   // ExtractionPoint.Update
   buttonDenyTransform.localPosition = new Vector3(0f, 0f, -0.06f * buttonDenyCurve.Evaluate(buttonDenyLerp));
   ```

   So a static `ExtractionConfirmAnchor` is inserted between the extraction point and the deny
   transform. The anchor never moves; the game's own nudge still happens locally inside it.
5. **Re-arm that button while the extraction is held.** The game switches it off the moment the point
   leaves `Idle`:

   ```csharp
   // ExtractionPoint.ButtonToggle
   if (currentState != State.Idle)
   {
       buttonGrabObject.enabled = false;
   }
   ```

   and `PhysGrabber` refuses to grab a disabled `StaticGrabObject`. So out of the box the only button
   in the level is un-grabbable at exactly the moment it is needed. The mod turns the grab object,
   light and material back on for the whole window between "haul goal reached" and "extracting".

## What the mod does

| Piece | Role |
| --- | --- |
| `StateSet` (prefix) | Holds the `Warning` transition while unconfirmed, and drives the tube text. |
| `OnClick` (prefix) | A press of the button confirms the extraction. |
| `Start` (prefix) | Clones the shop stand **before** vanilla destroys it. |
| `Update` (postfix) | Re-arms the button and watches for the press, once per frame. |
| `ChatManager.Update` (postfix) | The heartbeat: re-scans the level every half second and re-applies the hold. |
| `SceneManager.sceneLoaded` | A plain C# event, not a GameObject — nothing a scene change can destroy. |

None of the patches is load-bearing on its own: the heartbeat does the same work whatever the
others do, so a patch that fails to attach costs speed, never function. That is deliberate — this
mod spent several sessions doing nothing at all, and the only symptom was a log that stayed quiet,
which is impossible to tell apart from "the level never finished loading".

The heartbeat was originally a `GameObject` with a `MonoBehaviour`, created while BepInEx loaded.
It produced not one line in a full session: a GameObject created before any scene exists does not
survive the first scene load, and Unity says nothing when it goes. Hence the event-and-hook version,
which has no object to lose.

### Multiplayer

State changes are host-authoritative in REPO, so:

- The hold is enforced on every client (each runs the same gate), and the countdown is only
  released by the host.
- When a **non-host** player presses the button, the client raises a small Photon event
  (custom event code `177`) carrying the extraction point's `PhotonView` id; the host applies
  the confirmation and the normal `StateSetRPC` starts the countdown for everyone.
- When the **host** presses it, the confirmation applies locally.
- Presses are also read directly from `PhysGrabber.grabbedStaticGrabObject` — the same signal the
  game uses when a player holds a button — so the mod still works if the game's click wiring ever
  changes. Both paths are debounced so one press cannot confirm twice.

Note: the host needs the mod installed for the hold to work at all, and every player who should be
able to press the button needs it installed.

## Requirements

- R.E.P.O. with [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (Bleeding Edge package)

No other mods, libraries or asset files are required.

## Install

1. Drop `ExtractionConfirm.dll` into `REPO/BepInEx/plugins/`.
2. Start the game.

Keep exactly **one** copy of the DLL in the whole BepInEx tree: two DLLs with the same plugin GUID
means BepInEx silently skips one of them, and you end up debugging a version you did not mean to run.

## Reading the log

The mod logs at startup, and then only when something happens: a denied press, a confirmation, a
host setting, or a failure. There is no per-frame or per-scan output.

Startup:

```
[Info: ExtractionConfirm] ExtractionConfirm 1.0.0 loaded
[Info: ExtractionConfirm] Patched ExtractionPoint.StateSet, ExtractionPoint.OnClick, ExtractionPoint.Start, ExtractionPoint.Update, ChatManager.Update
```

If `Patched` is missing a hook, a warning names what was lost, e.g.
`ExtractionPoint.OnClick not found - a press of the extraction point's own button may not register.`
A patch that fails to attach costs speed, never function: the heartbeat re-scans the level every
half second and does the same work regardless.

Then, in play:

```
[Info: ExtractionConfirm] Denied: haul 320 of 1000 required.
[Info: ExtractionConfirm] Extraction confirmed - countdown starting
```

In multiplayer a guest's press is answered differently, and the host does the actual confirming:

```
[Info: ExtractionConfirm] Confirmation press - requested the countdown from the host
[Info: ExtractionConfirm] Confirmation received from a player - countdown starting
[Info: ExtractionConfirm] Using the host's validation setting: Auto
```

How to read it:

| What you see | What it means |
| --- | --- |
| **No `loaded` line at all** | The plugin was not loaded: wrong DLL location, or two copies of it in the tree. |
| **`ExtractionPoint members missing`** | The game changed. `Resolve` names the members it could not find and nothing is patched. |
| **Only `loaded`, never `Patched`** | Harmony refused to attach, or `ExtractionPoint` itself is gone. |
| **`No confirm button: this extraction point has no Shop Station and none was cached yet`** | This level's extraction points carry no shop station to clone from. The regular button is simply re-armed instead of replaced, so the hold still works. |
| **`The button did not move to the corner`** | `buttonDenyTransform` is no longer the button's parent, so the confirm stand and the press target are in different places. |
| **`Patch error in <where>`** | A mod-side exception. Each distinct one is logged once, and every patch body fails open, so the extraction itself still behaves as vanilla. |
| **`Denied: haul X of Y required`** | The button was pressed before the requirement was met. Set `Validation/Amount` to test without a full haul. |

## Configuration

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| Validation | `Auto` | `true` | `true` validates against the game's own haul goal. `false` uses `Amount`, so the hold is reachable without a full haul. **The host's setting always wins over a guest's.** |
| Validation | `Amount` | `0` | Fixed validation target, 0–100000. **Changing this turns `Auto` off**, because a non-zero amount is the whole point of setting it. Leave it at `0` to keep using the game's target. |

### Validating without a full haul

Setting `Amount` to anything other than `0` turns `Auto` off automatically, which makes the confirm button usable immediately, so the grabbing zone
and the whole flow can be tested without hauling anything.

Pressing before the requirement is met answers the way the shop answers a purchase you cannot
afford: the red deny flash, on the game's own button-deny animation, with the cancel sound. It says
"not yet" and nothing more — vanilla still runs, because in REPO that button also opens the
extraction area and a player may do that before reaching the goal.

In multiplayer the setting is the host's alone. The host publishes it with Photon event `178` and
clients read *that* instead of their own config — a guest validating at a different threshold than
the host would be a rule nobody can see.

The file lives at `BepInEx/config/com.repo.extractionconfirm.cfg`.

## Building

```
build.bat
```

Or directly:

```
dotnet build -c Release
```

Output: `bin\Release\ExtractionConfirm.dll`. Override the game location with
`-p:RepoDir="D:\SteamLibrary\steamapps\common\REPO"`.

## Layout of the code

| File | Role |
| --- | --- |
| [ExtractionConfirmMod.cs](ExtractionConfirmMod.cs) | Plugin entry point, configuration, and the Harmony patch declarations. |
| [ConfirmGate.cs](ConfirmGate.cs) | The hold itself: blocking `StateSet(Warning)`, cloning and placing the shop stand, arming the button, confirmation handling and the multiplayer Photon event. |
| [ConfirmButton.cs](ConfirmButton.cs) | The spawned button: glow, press squash on the game's own curve, and the press read from the button underneath. |
| [ModRunner.cs](ModRunner.cs) | The heartbeat: a scene event and a throttled per-frame tick that discover extraction points and keep the hold applied. |

## Status

Built and type-checked against the game's assemblies (`dotnet build -c Release`, 0 warnings, 0
errors), and verified in game: reach the target, the extraction holds, the confirm button releases
the countdown. The state-machine analysis and the button geometry come from decompiling the shipped
`Assembly-CSharp` and dumping the prefabs out of `resources.assets`.

Version history:

- `1.0.0` — the shop's purchase stand is cloned onto the extraction point and the game's own button
  is moved onto it, so the stand's button is the one that extracts. The extraction is held before the
  `Warning` countdown until that button is pressed, the button the game switches off is re-armed for
  the whole hold, and a fixed validation amount makes the flow reachable without a full haul.
  Everything that only existed to diagnose the build — the liveness probe, the per-scene scene
  inventory, the per-hook "is alive" lines and the per-frame heartbeat logging — is gone; the only
  remaining configuration is `Validation/Auto` and `Validation/Amount`.
