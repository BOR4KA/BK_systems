using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine.Events;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Multiplayer
{
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMovement : NetworkBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float walkSpeed = 5f;
        [SerializeField] private float sprintSpeed = 8f;

        [Header("Jump")]
        [SerializeField] private float jumpHeight = 1.2f;
        [SerializeField] private float gravity = -9.81f;

        [Header("Animation")]
        [SerializeField] private Animator animator;

        public CharacterController characterController { get; private set; }

        // MotorcycleController motora bindirirken/indirirken bunu doğrudan true/false yapar.
        // Gerçek Unity parent'ı kullanmıyoruz (bkz. MotorcycleController), o yüzden bu ayrı bir bayrak.
        public bool IsRiding { get; set; }
        public NetworkSeat ActiveSeat { get; private set; }
        public bool IsSeated => ActiveSeat != null;
        public bool IsMovementLocked => IsRiding || IsSeated;

        [Header("Sitting events")]
        [Tooltip("Invoked on every peer when the player sits or stands.")]
        [SerializeField] private UnityEvent<bool> onSeatedChanged = new UnityEvent<bool>();
        private bool seatPreviousControllerEnabled;
        private Rigidbody seatBody;
        private bool seatPreviousKinematic;

        public bool BeginSeat(NetworkSeat seat)
        {
            if (ActiveSeat == seat) return true;
            if (!seat || IsMovementLocked) return false;
            ActiveSeat = seat;
            seatPreviousControllerEnabled = characterController.enabled;
            characterController.enabled = false;
            seatBody = GetComponent<Rigidbody>();
            if (seatBody)
            {
                seatPreviousKinematic = seatBody.isKinematic;
                if (!seatBody.isKinematic)
                {
                    seatBody.linearVelocity = Vector3.zero;
                    seatBody.angularVelocity = Vector3.zero;
                }
                seatBody.isKinematic = true;
            }
            ResetSeatMotion();
            TeleportOwned(seat.seatPoint.position, seat.seatPoint.rotation);
            if (animator != null) animator.SetBool(IsSittingHash, true);
            onSeatedChanged.Invoke(true);
            return true;
        }

        public void EndSeat(NetworkSeat seat, Vector3 exitPosition, Quaternion exitRotation)
        {
            if (ActiveSeat != seat) return;
            TeleportOwned(exitPosition, exitRotation);
            ActiveSeat = null;
            ResetSeatMotion();
            if (seatBody)
            {
                seatBody.isKinematic = seatPreviousKinematic;
                if (!seatBody.isKinematic)
                {
                    seatBody.linearVelocity = Vector3.zero;
                    seatBody.angularVelocity = Vector3.zero;
                }
            }
            characterController.enabled = seatPreviousControllerEnabled;
            if (animator != null) animator.SetBool(IsSittingHash, false);
            onSeatedChanged.Invoke(false);
        }

        private void ResetSeatMotion()
        {
            verticalVelocity = 0f;
            if (IsSpawned && IsOwner) isMovingNetworked.Value = false;
        }

        private void TeleportOwned(Vector3 position, Quaternion rotation)
        {
            if (!IsOwner) return;
            transform.SetPositionAndRotation(position, rotation);
            var networkTransform = GetComponent<NetworkTransform>();
            if (IsSpawned && networkTransform)
                networkTransform.Teleport(position, rotation, transform.localScale);
        }

        private float verticalVelocity;
        private static readonly int IsMovingHash = Animator.StringToHash("IsMoving");
        private static readonly int IsSittingHash = Animator.StringToHash("IsSitting");

        private readonly NetworkVariable<bool> isMovingNetworked = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private void Awake()
        {
            characterController = GetComponent<CharacterController>();
            if (animator != null) animator.SetBool(IsSittingHash, false);
        }

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                transform.position += new Vector3(OwnerClientId * 2f, 0f, 0f);
            }
            isMovingNetworked.OnValueChanged += OnIsMovingChanged;
            OnIsMovingChanged(false, isMovingNetworked.Value);
        }

        public override void OnNetworkDespawn()
        {
            isMovingNetworked.OnValueChanged -= OnIsMovingChanged;
        }

        private void OnIsMovingChanged(bool previousValue, bool newValue)
        {
            if (animator != null) animator.SetBool(IsMovingHash, newValue);
        }

        private void Update()
        {
            // Eğer motordaysak veya kontrol bizde değilse karakteri yürütme!
            if (!IsOwner || Keyboard.current == null || IsMovementLocked || !characterController.enabled)
                return;

            Vector2 input = Vector2.zero;
            if (Keyboard.current.wKey.isPressed) input.y += 1f;
            if (Keyboard.current.sKey.isPressed) input.y -= 1f;
            if (Keyboard.current.dKey.isPressed) input.x += 1f;
            if (Keyboard.current.aKey.isPressed) input.x -= 1f;

            Vector3 direction = (transform.right * input.x + transform.forward * input.y);
            if (direction.sqrMagnitude > 1f) direction.Normalize();

            float speed = Keyboard.current.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;
            Vector3 horizontalMotion = direction * speed;

            if (characterController.isGrounded)
            {
                verticalVelocity = -2f;
                if (Keyboard.current.spaceKey.wasPressedThisFrame)
                {
                    verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                }
            }
            else
            {
                verticalVelocity += gravity * Time.deltaTime;
            }

            Vector3 motion = (horizontalMotion + Vector3.up * verticalVelocity) * Time.deltaTime;
            characterController.Move(motion);

            isMovingNetworked.Value = input.sqrMagnitude > 0f;
        }
    }
}
