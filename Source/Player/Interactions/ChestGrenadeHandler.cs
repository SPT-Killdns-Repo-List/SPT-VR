using System;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Settings;
using Systems.Effects;
using TarkovVR.Source.Controls;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.Interactions
{
    /// <summary>
    /// Quick-grenade-from-chest:
    ///
    ///   GRAB   : secondary grip pressed in chest zone + physical yank of the controller
    ///            → grenade ejected from inventory, held as a physics object in the hand.
    ///            Pin-pull sound plays immediately. RDG-2B (ignition smoke) is excluded.
    ///
    ///   THROW  : release grip outside chest zone
    ///            → live Grenade spawned via GrenadeFactory.
    ///            Lever sound plays for grenades with PlayFuzeSound == true.
    ///
    ///   CANCEL : release grip inside chest zone → grenade back to inventory.
    ///
    ///   HAPTIC : buzz on chest-zone entry when throwable grenades are available.
    /// </summary>
    internal class ChestGrenadeHandler : MonoBehaviour
    {
        // ── tuning ─────────────────────────────────────────────────────────────────
        internal const float CHEST_DETECT_RADIUS = 0.25f;
        private const float PULL_SPEED_THRESH = 0.8f;
        private const float THROW_SPEED_MULTIPLIER = 1.5f;
        private const float HAPTIC_LENGTH = 0.12f;
        private const float HAPTIC_AMOUNT = 0.6f;

        // ── state ──────────────────────────────────────────────────────────────────
        private enum GrenadeState
        {
            Idle,
            WaitingSecondGrip, // first grip clicked, waiting for second hold
            HoldingGrenade
        }

        private GrenadeState grenadeState = GrenadeState.Idle;

        private LootItem heldGrenade;
        private ThrowWeapItemClass currentGrenadeItem;
        private ItemAddress sourceAddress; // where the grenade was taken from — used for CancelGrab

        private bool isTransactionInProgress; // guard against overlapping inventory transactions

        // input
        private bool wasInChest;
        private float hapticCooldown;
        private float firstGripTime = -1f;
        private bool firstGripReleased; // must release grip between first and second press
        private const float DOUBLE_GRIP_WINDOW = 0.6f; // seconds to do second grip

        // holding physics
        private Rigidbody heldRb;
        private Transform secondaryHand;
        private bool isRbInitialized;

        // Velocity sampled each frame while grip is held — used on release.
        // We read it one frame before gripUp to avoid the near-zero value
        // that SteamVR reports at the exact moment of release.
        private const int VEL_WINDOW = 5;
        private readonly Vector3[] velWindow = new Vector3[VEL_WINDOW];
        private int velIdx;
        private const float THROW_SPEED_MIN = 1.2f; // m/s — below this, release in zone = cancel

        private static readonly Vector3 HeldItemOffset = new(-0.1f, -0.05f, 0f);

        // Audio clips — read from original prefab asset (not a clone), so they stay valid
        private AudioClip pinPullClip; // SndPin    — played on grab
        private AudioClip fuzePopClip; // SndLever  — played on throw (PlayFuzeSound only)
        private AudioClip safetyClip; // SndSafety — played on grab (PlayFuzeSound only)
        private AudioClip fuzeHissClip; // SndFuse   — played on throw (PlayFuzeSound only) — the UZRGM fuze pop
        private AudioClip holsterClip; // SndHolster — played on cancel (ring reinserted)

        // ─────────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!VRGlobals.inGame || VRGlobals.menuOpen ||
                VRGlobals.player == null || VRGlobals.vrPlayer == null)
            {
                return;
            }

            if (WeaponPatches.grenadeEquipped && grenadeState == GrenadeState.Idle)
            {
                return;
            }

            bool leftHanded = VRSettings.GetLeftHandedMode();

            SteamVR_Action_Boolean secondaryGrip = leftHanded
                ? SteamVR_Actions._default.RightGrip
                : SteamVR_Actions._default.LeftGrip;

            SteamVR_Input_Sources secondarySource = leftHanded
                ? SteamVR_Input_Sources.RightHand
                : SteamVR_Input_Sources.LeftHand;

            secondaryHand = leftHanded
                ? VRGlobals.vrPlayer.RightHand.transform
                : VRGlobals.vrPlayer.LeftHand.transform;

            bool gripDown = secondaryGrip.GetStateDown(SteamVR_Input_Sources.Any);
            bool gripHeld = secondaryGrip.state;
            bool gripUp = secondaryGrip.GetStateUp(SteamVR_Input_Sources.Any);
            bool inChest = IsHandInChestZone(secondaryHand);

            HandleChestZoneEntry(inChest, secondarySource);

            switch (grenadeState)
            {
                case GrenadeState.Idle:
                case GrenadeState.WaitingSecondGrip:
                    HandleIdle(gripDown, gripHeld, inChest, secondarySource);
                    break;
                case GrenadeState.HoldingGrenade:
                    HandleHolding(gripHeld, gripUp, inChest, secondarySource);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(grenadeState), grenadeState, null);
            }

            wasInChest = inChest;
        }

        // ── Haptic on chest zone entry ─────────────────────────────────────────────

        private void HandleChestZoneEntry(bool inChest, SteamVR_Input_Sources src)
        {
            if (!inChest || wasInChest)
            {
                return;
            }

            // Buzz only when holding a grenade and re-entering the zone (to put it back)
            if (grenadeState == GrenadeState.HoldingGrenade)
            {
                StartCoroutine(DoubleHaptic(src));
            }
        }

        private IEnumerator DoubleHaptic(SteamVR_Input_Sources src)
        {
            Haptic(src, cooldown: 0f, frequency: 1f, amplitude: HAPTIC_AMOUNT * 0.7f);
            yield return new WaitForSeconds(0.12f);
            Haptic(src, cooldown: 0f, frequency: 1f, amplitude: HAPTIC_AMOUNT * 0.7f);
        }

        // ── Idle ──────────────────────────────────────────────────────────────────

        private void HandleIdle(bool gripDown, bool gripHeld, bool inChest, SteamVR_Input_Sources src)
        {
            if (grenadeState == GrenadeState.Idle)
            {
                // First grip click in chest zone — wait for release then second grip
                if (gripDown && inChest)
                {
                    firstGripTime = Time.time;
                    firstGripReleased = false;
                    grenadeState = GrenadeState.WaitingSecondGrip;
                }

                return;
            }

            if (grenadeState == GrenadeState.WaitingSecondGrip)
            {
                // Timeout — cancel wait
                if (Time.time - firstGripTime > DOUBLE_GRIP_WINDOW)
                {
                    grenadeState = GrenadeState.Idle;
                    firstGripTime = -1f;
                    firstGripReleased = false;
                    return;
                }

                // Track release of first grip
                if (!gripHeld)
                {
                    firstGripReleased = true;
                }

                // Second grip held AFTER first was released + yank in chest zone — grab
                if (firstGripReleased && gripHeld && inChest)
                {
                    Vector3 rawVel = ControllerVelocity.GetSteamVRVelocity(src);
                    Vector3 worldVel = VRGlobals.vrOffsetter != null
                        ? VRGlobals.vrOffsetter.transform.TransformDirection(rawVel)
                        : rawVel;

                    if (worldVel.magnitude >= PULL_SPEED_THRESH)
                    {
                        grenadeState = GrenadeState.Idle;
                        firstGripTime = -1f;
                        firstGripReleased = false;
                        TryGrabGrenade(src);
                    }
                }
            }
        }

        // ── Holding ───────────────────────────────────────────────────────────────

        private void HandleHolding(bool gripHeld, bool gripUp, bool inChest, SteamVR_Input_Sources src)
        {
            if (gripHeld && heldGrenade != null)
            {
                if (!isRbInitialized)
                {
                    InitRigidbody();
                }

                if (heldRb != null)
                {
                    // Re-enforce kinematic every frame — EFT coroutines can change
                    // rigidbody state while the item is held
                    heldRb.isKinematic = true;
                    heldRb.detectCollisions = false;
                    heldRb.useGravity = false;

                    // Direct transform assignment — smooth, no physics jitter
                    Vector3 target = secondaryHand.position
                                     + secondaryHand.right * HeldItemOffset.x
                                     + secondaryHand.up * HeldItemOffset.y
                                     + secondaryHand.forward * HeldItemOffset.z;

                    heldGrenade.transform.position = target;
                    heldGrenade.transform.rotation = secondaryHand.rotation;

                    heldRb.velocity = Vector3.zero;
                    heldRb.angularVelocity = Vector3.zero;
                }

                // Track rolling velocity window — use recent frames only,
                // so an old swing doesn't contaminate the throw decision
                Vector3 rawV = ControllerVelocity.GetSteamVRVelocity(src);
                Vector3 worldV = VRGlobals.vrOffsetter != null
                    ? VRGlobals.vrOffsetter.transform.TransformDirection(rawV)
                    : rawV;
                velWindow[velIdx % VEL_WINDOW] = worldV;
                velIdx++;
            }

            if (!gripUp)
            {
                return;
            }

            // Average the window for the cancel-vs-throw decision.
            // This smooths out noise but doesn't affect actual throw force.
            Vector3 avgVel = velWindow.Aggregate(Vector3.zero, (current, v) => current + v);
            avgVel /= VEL_WINDOW;

            // For throw force use the last frame — most representative of release motion.
            // _velIdx points to the NEXT write slot, so (_velIdx - 1) is the last written.
            int lastIdx = (velIdx - 1 + VEL_WINDOW) % VEL_WINDOW;
            Vector3 releaseVel = velWindow[lastIdx];

            // Cancel only if hand is in chest zone AND arm was calm (low average speed)
            if (inChest && avgVel.magnitude < THROW_SPEED_MIN)
            {
                CancelGrab(src);
            }
            else
            {
                ThrowGrenade(src, releaseVel);
            }
        }

        // ── Grab ─────────────────────────────────────────────────────────────────

        private void TryGrabGrenade(SteamVR_Input_Sources src)
        {
            if (isTransactionInProgress)
            {
                return;
            }

            ThrowWeapItemClass grenade = FindFirstGrenadeInInventory();
            if (grenade == null)
            {
                return;
            }

            isTransactionInProgress = true;
            Haptic(src, cooldown: 0f, frequency: 150f, amplitude: HAPTIC_AMOUNT);
            currentGrenadeItem = grenade;
            sourceAddress = grenade.Parent;

            VRGlobals.player.InventoryController.ThrowItem(grenade, false, result =>
            {
                isTransactionInProgress = false;
                if (!result.Succeed)
                {
                    currentGrenadeItem = null;
                    ResetState();
                    return;
                }

                StartCoroutine(GrabSpawnedLootItem(src));
            });
        }

        private IEnumerator GrabSpawnedLootItem(SteamVR_Input_Sources src)
        {
            yield return null;
            yield return null;

            if (currentGrenadeItem == null)
            {
                yield break;
            }

            LootItem found = FindWorldLootItem(currentGrenadeItem);
            if (found == null)
            {
                Plugin.MyLog.LogWarning($"[ChestGrenadeHandler] LootItem not found – aborting.");
                currentGrenadeItem = null;
                ResetState();
                yield break;
            }

            heldGrenade = found;
            isRbInitialized = false;
            grenadeState = GrenadeState.HoldingGrenade;
            ClearVelWindow();
            velIdx = 0;

            pinPullClip = null;
            fuzePopClip = null;
            try
            {
                GameObject prefabAsset =
                    Singleton<IEasyAssets>.Instance.GetAsset<GameObject>(currentGrenadeItem.Prefab);
                if (prefabAsset != null)
                {
                    BaseSoundPlayer sp = prefabAsset.GetComponentInChildren<BaseSoundPlayer>(true);
                    if (sp != null)
                    {
                        foreach (var elem in sp.AdditionalSounds)
                        {
                            switch (elem.EventName)
                            {
                                case "SndPin" when pinPullClip == null:
                                    pinPullClip = elem.RandomSoundClip;
                                    break;
                                case "SndLever" when fuzePopClip == null:
                                    fuzePopClip = elem.RandomSoundClip;
                                    break;
                                case "SndSafety" when safetyClip == null:
                                    safetyClip = elem.RandomSoundClip;
                                    break;
                                case "SndFuse" when fuzeHissClip == null:
                                    fuzeHissClip = elem.RandomSoundClip;
                                    break;
                                case "SndHolster" when holsterClip == null:
                                    holsterClip = elem.RandomSoundClip;
                                    break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogWarning($"[ChestGrenadeHandler] Audio extraction failed: {ex.Message}");
            }

            // Play pin-pull sound on grab + safety sound for fuze grenades
            PlayCachedClip(pinPullClip, found.transform.position);

            if (currentGrenadeItem.PlayFuzeSound)
            {
                PlayCachedClip(safetyClip, found.transform.position);
            }

            Haptic(src, cooldown: 0f, frequency: 80f, amplitude: HAPTIC_AMOUNT * 0.8f);
        }

        // ── Rigidbody ─────────────────────────────────────────────────────────────

        private void InitRigidbody()
        {
            heldRb = heldGrenade.GetComponent<Rigidbody>()
                     ?? heldGrenade.gameObject.AddComponent<Rigidbody>();

            heldGrenade._rigidBody = heldRb;
            heldRb.interpolation = RigidbodyInterpolation.Interpolate;
            // isKinematic=true while held — direct transform assignment, no physics sim
            heldRb.isKinematic = true;
            heldRb.detectCollisions = false;
            heldRb.useGravity = false;
            heldRb.mass = heldGrenade.item_0.TotalWeight;
            isRbInitialized = true;
        }

        // ── Cancel ────────────────────────────────────────────────────────────────

        private void CancelGrab(SteamVR_Input_Sources src)
        {
            if (heldGrenade == null)
            {
                ResetState();
                return;
            }

            bool returned = false;

            // Try to return to the exact original slot first
            if (sourceAddress != null)
            {
                GStruct154<GClass3411> moveBack = InteractionsHandlerClass.Move(
                    heldGrenade.Item,
                    sourceAddress,
                    VRGlobals.player.InventoryController,
                    simulate: true
                );

                if (moveBack.Succeeded && heldGrenade.ItemOwner.CanExecute(moveBack.Value))
                {
                    if (heldRb != null)
                    {
                        heldRb.useGravity = true;
                        heldRb.detectCollisions = true;
                    }

                    isTransactionInProgress = true;
                    VRGlobals.player.InventoryController.RunNetworkTransaction(moveBack.Value, res =>
                    {
                        isTransactionInProgress = false;
                        if (res.Succeed)
                        {
                            VRGlobals.player.UpdateInteractionCast();
                        }
                    });
                    returned = true;
                }
            }

            // Fallback — find any free slot
            if (!returned)
            {
                GStruct154<GInterface424> pickUp = InteractionsHandlerClass.QuickFindAppropriatePlace(
                    heldGrenade.Item,
                    VRGlobals.player.InventoryController,
                    VRGlobals.player.InventoryController.Inventory.Equipment.ToEnumerable(),
                    InteractionsHandlerClass.EMoveItemOrder.PickUp,
                    simulate: true
                );

                if (pickUp.Succeeded && heldGrenade.ItemOwner.CanExecute(pickUp.Value))
                {
                    if (heldRb != null)
                    {
                        heldRb.useGravity = true;
                        heldRb.detectCollisions = true;
                    }

                    isTransactionInProgress = true;
                    VRGlobals.player.InventoryController.RunNetworkTransaction(pickUp.Value, res =>
                    {
                        isTransactionInProgress = false;
                        if (res.Succeed) VRGlobals.player.UpdateInteractionCast();
                    });
                }
                else
                {
                    if (heldRb != null)
                    {
                        heldRb.useGravity = true;
                        heldRb.isKinematic = false;
                    }
                }
            }

            Haptic(src, cooldown: 0f, frequency: 30f, amplitude: HAPTIC_AMOUNT * 0.4f);
            PlayCachedClip(holsterClip, heldGrenade?.transform.position ?? secondaryHand.position);
            ResetState();
        }

        // ── Throw ─────────────────────────────────────────────────────────────────

        private void ThrowGrenade(SteamVR_Input_Sources src, Vector3 releaseVel)
        {
            if (heldGrenade == null)
            {
                ResetState();
                return;
            }

            LootItem lootItem = heldGrenade;
            ThrowWeapItemClass grenadeItem = currentGrenadeItem;

            Haptic(src, cooldown: 0f, frequency: 200f, amplitude: HAPTIC_AMOUNT);

            // Scale release velocity — same multiplier and cap as VRGrenadeController.RepositionGrenadeThrow
            Vector3 worldVel = releaseVel * THROW_SPEED_MULTIPLIER;
            if (worldVel.magnitude > 15f)
            {
                worldVel = worldVel.normalized * 15f;
            }

            // Consume hands stamina — formula from feature/fix-grab-items ConsumeThrowStamina:
            // cost = 1.340 * w^0.90 * Lerp(0.20, 1, speedFactor), threshold 1.5 m/s
            try
            {
                if (VRGlobals.player?.Physical is PlayerPhysicalClass physical)
                {
                    float speed = releaseVel.magnitude;
                    const float throwVelocityThreshold = 1.5f;
                    if (speed >= throwVelocityThreshold)
                    {
                        float w2 = grenadeItem.TotalWeight;
                        float sf = Mathf.Clamp01((speed - throwVelocityThreshold) / 3f);
                        float cost = 1.340f * Mathf.Pow(w2, 0.90f) * Mathf.Lerp(0.20f, 1f, sf);
                        if (cost > 0f)
                        {
                            physical.ConsumeAsMelee(cost);
                        }
                    }
                }
            }
            catch
            {
                /* ignored */
            }

            // Release kinematic so the grenade can fly freely
            if (heldRb != null)
            {
                heldRb.isKinematic = false;
                heldRb.detectCollisions = true;
                heldRb.useGravity = true;
            }

            Vector3 throwPos = lootItem.transform.position;
            Quaternion throwRot = lootItem.transform.rotation;

            // Capture clips before ResetState nulls them
            AudioClip leverClip = fuzePopClip;
            AudioClip fuzeClip = fuzeHissClip;
            bool playFuze = grenadeItem.PlayFuzeSound;

            ResetState();

            StartCoroutine(SpawnLiveGrenade(lootItem, grenadeItem, throwPos, throwRot, worldVel, leverClip, fuzeClip,
                playFuze));
        }

        private static IEnumerator SpawnLiveGrenade(
            LootItem lootItem, ThrowWeapItemClass grenadeItem,
            Vector3 throwPos, Quaternion throwRot, Vector3 throwForce,
            AudioClip leverClip, AudioClip fuzeClip, bool playFuze)
        {
            yield return new WaitForFixedUpdate();

            if (VRGlobals.player == null)
            {
                yield break;
            }

            // SndLever = spoon click — for all grenades with PlayFuzeSound
            // SndFuse  = UZRGM fuze pop — frag grenades only, excluding V40 (no UZRGM)
            PlayCachedClip(leverClip, throwPos);

            if (playFuze && grenadeItem.ThrowType == ThrowWeapType.frag_grenade
                         && !IsV40(grenadeItem)) // V40 has no UZRGM
            {
                PlayCachedClip(fuzeClip, throwPos);
            }

            try
            {
                if (Singleton<GameWorld>.Instantiated)
                {
                    Singleton<GameWorld>.Instance.DestroyLoot(lootItem);
                }
                
                lootItem.Kill();
            }
            catch
            {
                // ignored
            }

            try
            {
                GameObject prefabGo = Singleton<PoolManagerClass>.Instance.CreateItem(grenadeItem, false);
                if (prefabGo == null)
                {
                    Plugin.MyLog.LogError("[ChestGrenadeHandler] CreateItem returned null.");
                    yield break;
                }

                GrenadePrefab grenadePrefab = prefabGo.GetComponent<GrenadePrefab>();
                if (grenadePrefab == null)
                {
                    Plugin.MyLog.LogError("[ChestGrenadeHandler] GrenadePrefab not found.");
                    yield break;
                }

                GrenadeSettings grenadeSettings = Instantiate(grenadePrefab.GrenadeItself);

                Vector3 finalForce = throwForce + VRGlobals.player.Velocity;

                Grenade grenade = new GrenadeFactoryClass().Create(
                    grenadeSettings,
                    throwPos,
                    throwRot,
                    finalForce,
                    grenadeItem,
                    VRGlobals.player.ProfileId,
                    prewarm: 0f);

                // Start smoke emission for smoke grenades (e.g. M18)
                // Mirrors vmethod_2 in BaseGrenadeHandsController
                if (grenade is SmokeGrenade smokeGrenade)
                {
                    try
                    {
                        GrenadeEmission emission = Singleton<Effects>.Instance.GetEmissionEffect(grenadeSettings.EmmisionEffect);
                        if (emission != null)
                        {
                            emission.AttachTo(smokeGrenade.transform, Vector3.zero);
                            emission.SetFillParams(0f, grenadeItem.EmitTime);
                            emission.StartEmission(0f);
                            smokeGrenade.EmissionEnd += emission.StopEmission;
                            smokeGrenade.VelocityBelowThreshold += emission.Stall;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.MyLog.LogWarning($"[ChestGrenadeHandler] Smoke emission setup failed: {ex.Message}");
                    }
                }

                Singleton<GInterface169>.Instance.RegisterGrenade(grenade);
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[ChestGrenadeHandler] SpawnLiveGrenade failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── Sound helpers ─────────────────────────────────────────────────────────

        private static void PlayCachedClip(AudioClip clip, Vector3 pos)
        {
            if (clip == null || !MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                return;
            }

            try
            {
                float distance = CameraClass.Instance.Distance(pos);
                MonoBehaviourSingleton<BetterAudio>.Instance.PlayAtPoint(
                    pos, clip, distance,
                    BetterAudio.AudioSourceGroupType.Weaponry,
                    35, 1.0f,
                    EOcclusionTest.None, null, false);
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogWarning($"[ChestGrenadeHandler] PlayCachedClip failed: {ex.Message}");
                AudioSource.PlayClipAtPoint(clip, pos, 1.0f);
            }
        }

        // ── Inventory helpers ─────────────────────────────────────────────────────

        private static ThrowWeapItemClass FindFirstGrenadeInInventory()
        {
            if (VRGlobals.player?.Equipment == null)
            {
                return null;
            }

            List<ThrowWeapItemClass> candidates = [];
            foreach (EquipmentSlot slotId in new[] { EquipmentSlot.TacticalVest })
            {
                Slot slot = VRGlobals.player.Equipment.GetSlot(slotId);
                if (slot?.ContainedItem == null)
                {
                    continue;
                }

                switch (slot.ContainedItem)
                {
                    case CompoundItem compound:
                        candidates.AddRange(compound.GetAllItems().OfType<ThrowWeapItemClass>());
                        break;
                    case ThrowWeapItemClass direct:
                        candidates.Add(direct);
                        break;
                }
            }

            candidates.RemoveAll(IsRdg2B);
            return candidates.Count == 0 ? null : candidates[0];
        }

        private static bool IsRdg2B(ThrowWeapItemClass grenade)
        {
            const string templateId = "5a2a57cfc4a2826c6e06d44a";
            return grenade?.TemplateId.ToString() == templateId;
        }

        private static bool IsV40(ThrowWeapItemClass grenade)
        {
            const string templateId = "66dae7cbeb28f0f96809f325";
            return grenade?.TemplateId.ToString() == templateId;
        }

        private static LootItem FindWorldLootItem(ThrowWeapItemClass grenadeItem)
        {
            Vector3 origin = InitVRPatches.chestGrenadeZone != null
                ? InitVRPatches.chestGrenadeZone.position
                : (VRGlobals.player?.Transform.position ?? Vector3.zero);

            foreach (Collider col in Physics.OverlapSphere(origin, 2.5f))
            {
                LootItem li = col.GetComponent<LootItem>() ?? col.GetComponentInParent<LootItem>();
                if (li != null && li.Item == grenadeItem)
                {
                    return li;
                }
            }

            return FindObjectsOfType<LootItem>().FirstOrDefault(li => li.Item == grenadeItem);
        }

        private static bool IsHandInChestZone(Transform hand)
        {
            if (hand == null)
            {
                return false;
            }

            Transform anchor = InitVRPatches.chestGrenadeZone;
            if (anchor == null)
            {
                return false;
            }

            return Vector3.Distance(hand.position, anchor.position) <= CHEST_DETECT_RADIUS;
        }

        private void Haptic(SteamVR_Input_Sources src,
            float cooldown = 0f,
            float frequency = 80f,
            float amplitude = HAPTIC_AMOUNT)
        {
            if (Time.time < hapticCooldown)
            {
                return;
            }

            SteamVR_Actions._default.Haptic.Execute(0f, HAPTIC_LENGTH, frequency, amplitude, src);
            if (cooldown > 0f)
            {
                hapticCooldown = Time.time + cooldown;
            }
        }

        private void ResetState()
        {
            grenadeState = GrenadeState.Idle;
            heldGrenade = null;
            currentGrenadeItem = null;
            sourceAddress = null;
            heldRb = null;
            isRbInitialized = false;
            isTransactionInProgress = false;
            velIdx = 0;
            firstGripTime = -1f;
            firstGripReleased = false;
            pinPullClip = null;
            fuzePopClip = null;
            safetyClip = null;
            fuzeHissClip = null;
            holsterClip = null;
            ClearVelWindow();
        }

        private void ClearVelWindow()
        {
            Array.Clear(velWindow, 0, VEL_WINDOW);
        }
    }
}
