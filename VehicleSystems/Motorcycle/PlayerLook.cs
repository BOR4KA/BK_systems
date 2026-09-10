using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Multiplayer
{
    public class PlayerLook : NetworkBehaviour
    {
        [SerializeField] private Transform cameraTransform;
        [SerializeField] private float mouseSensitivity = 2f;
        [SerializeField] private float minPitch = -80f;
        [SerializeField] private float maxPitch = 80f;
        [SerializeField] private bool lockCursor = true;

        private PlayerMovement playerMovement;
        private float pitch;
        private float seatedYaw;
        [SerializeField, Range(30f, 180f)] private float seatedYawLimit = 120f;

        private void Awake()
        {
            playerMovement = GetComponent<PlayerMovement>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner)
            {
                if (cameraTransform != null) cameraTransform.gameObject.SetActive(false);
                return;
            }

            if (lockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void Update()
        {
            if (!IsOwner || Mouse.current == null) return;

            if (lockCursor && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                bool isLocked = Cursor.lockState == CursorLockMode.Locked;
                Cursor.lockState = isLocked ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = isLocked;
            }

            Vector2 mouseDelta = Mouse.current.delta.ReadValue() * (mouseSensitivity * Time.deltaTime);

            
            bool seated = playerMovement != null && playerMovement.IsSeated;
            if (seated)
                seatedYaw = Mathf.Clamp(seatedYaw + mouseDelta.x, -seatedYawLimit, seatedYawLimit);
            else
            {
                seatedYaw = 0f;
                if (playerMovement != null && !playerMovement.IsMovementLocked)
                    transform.Rotate(Vector3.up * mouseDelta.x);
            }

            
            if (cameraTransform != null)
            {
                pitch = Mathf.Clamp(pitch - mouseDelta.y, minPitch, maxPitch);
                cameraTransform.localEulerAngles = new Vector3(pitch, seated ? seatedYaw : 0f, 0f);
            }
        }
    }
}
