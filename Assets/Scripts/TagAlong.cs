using UnityEngine;

// Spec 12 Part S — keeps a small world-space canvas within reach of the head
// without locking it to the view (used by the Back to Hub canvas).
//
// The canvas sits `distance` metres from the head, `yawOffset` degrees to the
// side and `pitch` degrees down, facing the head. It follows head POSITION
// every frame but head YAW only lazily: while the student looks around within
// `followAngle` degrees it stays put (so aiming at it is steady), and once
// they turn further it swings round to the new facing. Head pitch and roll
// are ignored, so looking down at it does not push it away.
public class TagAlong : MonoBehaviour
{
    public Transform Head;

    [SerializeField] private float distance = 0.9f;
    [SerializeField] private float yawOffset = -35f;
    [SerializeField] private float pitch = 22f;
    [SerializeField] private float followAngle = 40f;
    [SerializeField] private float smoothTime = 0.35f;

    private float anchorYaw;
    private float targetYaw;
    private float yawVelocity;

    // Places it immediately at the head's current facing (no swing).
    public void Snap()
    {
        if (Head == null) return;
        anchorYaw = targetYaw = HeadYaw();
        yawVelocity = 0f;
        Place();
    }

    private void LateUpdate()
    {
        if (Head == null) return;

        float headYaw = HeadYaw();
        if (Mathf.Abs(Mathf.DeltaAngle(targetYaw, headYaw)) > followAngle) targetYaw = headYaw;
        anchorYaw = Mathf.SmoothDampAngle(anchorYaw, targetYaw, ref yawVelocity, smoothTime);
        Place();
    }

    private float HeadYaw()
    {
        Vector3 f = Head.forward;
        f.y = 0f;
        // Looking straight up or down: keep the current yaw.
        return f.sqrMagnitude < 1e-4f ? anchorYaw : Quaternion.LookRotation(f).eulerAngles.y;
    }

    private void Place()
    {
        Vector3 dir = Quaternion.Euler(pitch, anchorYaw + yawOffset, 0f) * Vector3.forward;
        transform.position = Head.position + dir * distance;
        // A UI canvas is read from behind its forward axis: face away from the head.
        transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
    }
}
