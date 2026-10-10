using System.Collections;
using UnityEngine;

// Spec 5, Fix 1: enforce spawn position, facing, and floor-aligned height.
//
// *** DISABLED by default (see the `active` flag below). ***
// The Floor-origin + recenter approach placed the head at floor level on-device,
// so Fix 1 was reverted: the scene is back on EyeLevel tracking with the rig root
// at its original localPosition (0, 1.7, 6.793), yaw -180. This component is kept
// (not deleted) but inert — `active = false` means it never sets the tracking
// origin and never recenters, so it can't override the original height/facing.
// Re-enable only once the height handling is redesigned.
//
// The rig is Meta's OVRCameraRigInteraction. When active, this component:
//   1. Forces the tracking origin to Floor at runtime (belt-and-suspenders on
//      top of the OVRManager Inspector setting) so the virtual floor sits on
//      the real floor and the user's real standing height is preserved.
//   2. Recenters the rig so the headset ends up at SpawnPoint's XZ position and
//      yaw. Floor mode owns Y/height, so this NEVER touches Y — there is no
//      hardcoded camera height here (the old 1.7 offset was removed from the
//      rig root in the scene).
//   3. Re-runs on scene start AND whenever the headset is re-mounted or input
//      focus is regained (take headset off -> put back on recenters cleanly).
//
// Attach to the OVRCameraRigInteraction root (rigRoot then defaults to this
// transform). Assign centerEye = CenterEyeAnchor and spawnPoint = the SpawnPoint
// transform. On Quest the OVRManager mount events fire; OnApplicationFocus is a
// general fallback.
public class RigRecenter : MonoBehaviour
{
    [Tooltip("Master switch. DISABLED (false) per the Spec 5 Fix 1 revert: while " +
             "false this component never changes the tracking origin and never " +
             "recenters, so the scene's original EyeLevel + rig height/facing are " +
             "left untouched. Turn on only when reintroducing a redesigned spawn fix.")]
    [SerializeField] private bool active = false;

    [Tooltip("The transform moved to recenter the player. Leave empty to use " +
             "this GameObject's transform (attach this component to the " +
             "OVRCameraRigInteraction root).")]
    [SerializeField] private Transform rigRoot;

    [Tooltip("The CenterEyeAnchor (headset) transform. Same object PromptEnabler.OVRCamera points to.")]
    [SerializeField] private Transform centerEye;

    [Tooltip("Empty transform placed at the intended standing spot, rotated to " +
             "face the intended direction (toward the audience/intro board). " +
             "Y should sit on the floor (typically 0); Floor tracking owns height.")]
    [SerializeField] private Transform spawnPoint;

    private void Awake()
    {
        if (rigRoot == null) rigRoot = transform;

        // Best-effort auto-resolve of the head if it wasn't wired in the Inspector.
        if (centerEye == null)
        {
            OVRCameraRig rig = GetComponentInChildren<OVRCameraRig>();
            if (rig == null) rig = FindObjectOfType<OVRCameraRig>();
            if (rig != null) centerEye = rig.centerEyeAnchor;
        }
    }

    private void OnEnable()
    {
        OVRManager.HMDMounted += OnHmdMounted;
        OVRManager.InputFocusAcquired += OnInputFocusAcquired;
    }

    private void OnDisable()
    {
        OVRManager.HMDMounted -= OnHmdMounted;
        OVRManager.InputFocusAcquired -= OnInputFocusAcquired;
    }

    private IEnumerator Start()
    {
        // Disabled per the Spec 5 Fix 1 revert: do nothing so the scene's original
        // EyeLevel tracking and rig height/facing stay in effect.
        if (!active) yield break;

        SetFloorTrackingOrigin();

        // The head pose isn't valid on the very first frame after load; wait one
        // frame so the recenter reads a real CenterEyeAnchor pose.
        yield return null;
        Recenter();
    }

    private void OnHmdMounted() { if (!active) return; SetFloorTrackingOrigin(); Recenter(); }

    private void OnInputFocusAcquired() { if (!active) return; SetFloorTrackingOrigin(); Recenter(); }

    // General fallback for editor/Link and platforms where the OVR mount events
    // don't fire; harmless duplicate of the mount-event recenter on Quest.
    private void OnApplicationFocus(bool hasFocus)
    {
        if (active && hasFocus) Recenter();
    }

    private void SetFloorTrackingOrigin()
    {
        // Idempotent: only WRITE when the origin isn't already Floor. Re-assigning
        // trackingOriginType (even to the same value) makes OVR destroy and
        // recreate its reference space — the source of the repeated
        // "xrDestroySpace: XR_ERROR_HANDLE_INVALID" churn and a possible transient
        // height glitch. Setting it once (the scene also serializes FloorLevel)
        // and letting the mount/focus handlers no-op keeps height stable.
        if (OVRManager.instance != null &&
            OVRManager.instance.trackingOriginType != OVRManager.TrackingOrigin.FloorLevel)
        {
            OVRManager.instance.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
        }
    }

    // Aligns the headset to SpawnPoint using ONLY horizontal position (XZ) and
    // yaw. With Floor tracking origin, height is owned entirely by the tracking
    // system: this routine never reads, sets, or offsets any Y. The rig root
    // stays at world Y = 0 and Floor tracking places the head at the user's real
    // physical height. (SpawnPoint's own Y is irrelevant — only its XZ and yaw
    // are used.)
    public void Recenter()
    {
        if (!active) return;
        if (rigRoot == null || centerEye == null || spawnPoint == null) return;

        // 1) Yaw only. Rotate the rig so the head's yaw matches the spawn yaw.
        //    The pivot is flattened to the rig root's own height and the axis is
        //    Vector3.up, so this rotation cannot introduce or change any Y. The
        //    pivot line passes through the head's XZ, so the head stays put while
        //    only its facing changes (XZ step below is unaffected).
        float yawDelta = Mathf.DeltaAngle(centerEye.eulerAngles.y, spawnPoint.eulerAngles.y);
        Vector3 pivot = new Vector3(centerEye.position.x, rigRoot.position.y, centerEye.position.z);
        rigRoot.RotateAround(pivot, Vector3.up, yawDelta);

        // 2) Horizontal position only. Slide the rig so the head lands on the
        //    spawn point in X/Z. Y is deliberately left alone: we read the rig
        //    root's current Y and write it back unchanged, so Floor tracking's
        //    height is never disturbed.
        Vector3 rigPos = rigRoot.position;
        rigPos.x += spawnPoint.position.x - centerEye.position.x;
        rigPos.z += spawnPoint.position.z - centerEye.position.z;
        rigRoot.position = rigPos;
    }
}
