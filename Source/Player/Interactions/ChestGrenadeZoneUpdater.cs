using TarkovVR.Patches.Core.VR;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    /// <summary>
    /// Attached to the chestGrenadeZone GameObject.
    /// Re-applies localPosition every LateUpdate to prevent the animation system
    /// from overriding it. Also updates the debug sphere size.
    /// Position comes from InitVRPatches.ChestGrenadeZoneLocalPos.
    /// Radius comes from ChestGrenadeHandler.CHEST_DETECT_RADIUS.
    /// </summary>
    internal class ChestGrenadeZoneUpdater : MonoBehaviour
    {
        private Transform sphere;

        private void Start()
        {
            sphere = transform.Find("chestGrenadeDebugSphere");
            Apply();
        }

        private void LateUpdate()
        {
            // Always reapply every frame — prevents animation system overriding it
            transform.localPosition = InitVRPatches.ChestGrenadeZoneLocalPos;
        }

        private void Apply()
        {
            transform.localPosition = InitVRPatches.ChestGrenadeZoneLocalPos;

            if (sphere == null)
            {
                return;
            }
            float d = ChestGrenadeHandler.CHEST_DETECT_RADIUS * 2f;
            sphere.localScale = new Vector3(d, d, d);
        }
    }
}
