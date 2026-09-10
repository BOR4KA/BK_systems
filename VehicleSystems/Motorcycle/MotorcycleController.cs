using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Multiplayer
{
    [RequireComponent(typeof(Rigidbody))]
    public class MotorcycleController : NetworkBehaviour, IPlayerInteractable
    {
        [Header("Wheels and vehicle frame")]
        public WheelCollider frontCollider;
        public WheelCollider rearCollider;
        public Transform frontMesh;
        public Transform rearMesh;
        public Transform steeringVisual;
        public Transform centerOfMass;

        [Header("Drive (metres, seconds)")]
        public float moveSpeed = 22f;
        public float reverseSpeed = 3f;
        public float acceleration = 5f;
        public float brakeDeceleration = 9f;
        public float throttleResponse = 2.5f;
        public float brakeResponse = 5f;
        public float reverseDelay = 0.4f;

        [Header("Progressive steering")]
        public float maxSteerAngle = 26f;
        public float minSpeedToTurn = 0.15f;
        public float fullSteeringSpeed = 2f;
        public float steeringResponse = 2f;
        public float steerAngleRate = 55f;
        public float maxLateralAcceleration = 4.5f;
        public float maxYawRate = 0.85f;

        [Header("Balance (physical torque, no rotation locks)")]
        public float maxLeanAngle = 22f;
        public float leanResponse = 35f;
        public float balanceStrength = 220f;
        public float balanceDamping = 30f;
        public float pitchStrength = 18f;
        public float pitchDamping = 7f;
        public float yawDamping = 3f;

        [Header("Interaction")]
        public Transform ridePoint;
        public float interactionRadius = 3f;
        public NetworkVariable<ulong> driverId = new NetworkVariable<ulong>(
            ulong.MaxValue, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<float> networkSteer = new NetworkVariable<float>();
        private readonly NetworkVariable<float> networkSpeed = new NetworkVariable<float>();
        private Rigidbody rb;
        private Vector2 localInput, serverInput;
        private bool localBrake, serverBrake;
        private float lastInputTime, throttle, brake, steering, steerAngle, lean, directionWait;
        private int driveDirection = 1;
        private float wheelbase, frontSpin, rearSpin;
        private Quaternion steeringRest, frontMeshOffset, rearMeshOffset;
        private bool ready;

        private Vector3 Forward => rb.rotation * Vector3.forward;
        private Vector3 Right => rb.rotation * Vector3.right;
        private bool Simulates => !IsSpawned || IsServer;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (!frontCollider || !rearCollider || !ridePoint)
            {
                Debug.LogError("MotorcycleController needs both WheelColliders and a ride point.", this);
                enabled = false;
                return;
            }
            // WheelCollider physics uses the Rigidbody frame: +Z forward, +Y up.
            wheelbase = Mathf.Max(0.2f, Mathf.Abs(Vector3.Dot(
                frontCollider.transform.position - rearCollider.transform.position, transform.forward)));
            if (centerOfMass) rb.centerOfMass = transform.InverseTransformPoint(centerOfMass.position);
            rb.maxAngularVelocity = 3f;
            rb.solverIterations = 12;
            rb.solverVelocityIterations = 6;
            frontCollider.ConfigureVehicleSubsteps(5f, 12, 15);
            if (steeringVisual) steeringRest = steeringVisual.localRotation;
            // Keep imported mesh axes instead of overwriting them with raw collider axes.
            if (frontMesh) frontMeshOffset = Quaternion.Inverse(frontCollider.transform.rotation) * frontMesh.rotation;
            if (rearMesh) rearMeshOffset = Quaternion.Inverse(rearCollider.transform.rotation) * rearMesh.rotation;
            ready = true;
        }

        public override void OnNetworkSpawn()
        {
            // NetworkTransform is server authoritative; only that same instance runs physics.
            rb.isKinematic = !IsServer;
            rb.interpolation = IsServer ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            frontCollider.enabled = IsServer;
            rearCollider.enabled = IsServer;
            PlayerInteraction.Register(this);
            driverId.OnValueChanged += OnDriverChanged;
            if (IsServer) NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            OnDriverChanged(ulong.MaxValue, driverId.Value);
        }

        public override void OnNetworkDespawn()
        {
            PlayerInteraction.Unregister(this);
            driverId.OnValueChanged -= OnDriverChanged;
            if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            if (driverId.Value != ulong.MaxValue) SetRider(driverId.Value, false);
            ClearInput();
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (!IsServer || driverId.Value == ulong.MaxValue) return;
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(driverId.Value, out var rider) ||
                rider.OwnerClientId == clientId)
                driverId.Value = ulong.MaxValue;
        }

        private void OnDriverChanged(ulong oldId, ulong newId)
        {
            ClearInput();
            if (oldId != ulong.MaxValue) SetRider(oldId, false);
            if (newId != ulong.MaxValue) SetRider(newId, true);
        }

        private void ClearInput()
        {
            localInput = serverInput = Vector2.zero;
            localBrake = serverBrake = false;
            throttle = 0f;
            directionWait = 0f;
            driveDirection = 1;
            lastInputTime = float.NegativeInfinity;
        }

        private void SetRider(ulong id, bool riding)
        {
            if (!NetworkManager || !NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(id, out var rider)) return;
            var movement = rider.GetComponent<PlayerMovement>();
            var cc = rider.GetComponent<CharacterController>();
            if (cc) cc.enabled = false;
            if (movement) movement.IsRiding = riding;
            // Only the player owner writes its owner-authoritative transform.
            if (rider.IsOwner && ridePoint)
            {
                if (riding) rider.transform.SetPositionAndRotation(ridePoint.position, ridePoint.rotation);
                else
                {
                    Vector3 forward = Vector3.ProjectOnPlane(Forward, Vector3.up).normalized;
                    rider.transform.SetPositionAndRotation(ridePoint.position + Right * 1.5f,
                        Quaternion.LookRotation(forward, Vector3.up));
                }
            }
            if (cc) cc.enabled = !riding;
        }

        private void Update()
        {
            localInput = Vector2.zero;
            localBrake = false;
            var nm = NetworkManager.Singleton;
            if (!ready || !IsSpawned || !nm || !nm.IsConnectedClient || nm.LocalClient.PlayerObject == null)
                return;
            var player = nm.LocalClient.PlayerObject;
            var keyboard = Keyboard.current;
            bool driving = driverId.Value == player.NetworkObjectId;
            if (keyboard == null) return;
            if (!driving) return;
            localInput.y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
            localInput.x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
            localBrake = keyboard.spaceKey.isPressed;
        }

        private void FixedUpdate()
        {
            if (!ready) return;
            if (IsSpawned && NetworkManager.IsConnectedClient && NetworkManager.LocalClient.PlayerObject != null &&
                driverId.Value == NetworkManager.LocalClient.PlayerObject.NetworkObjectId)
                SubmitInputServerRpc(localInput, localBrake);

            if (!Simulates) return;
            bool occupied = IsSpawned && driverId.Value != ulong.MaxValue;
            bool fresh = occupied && Time.unscaledTime - lastInputTime < 0.35f;
            Vector2 input = fresh ? serverInput : Vector2.zero;
            bool stop = !fresh || serverBrake;
            float dt = Time.fixedDeltaTime;
            float speed = Vector3.Dot(rb.linearVelocity, Forward);
            float absSpeed = Mathf.Abs(speed);
            bool frontGrounded = frontCollider.GetGroundHit(out WheelHit frontHit);
            bool rearGrounded = rearCollider.GetGroundHit(out WheelHit rearHit);
            bool grounded = frontGrounded || rearGrounded;
            Vector3 groundUp = Vector3.up;
            if (grounded)
            {
                groundUp = ((frontGrounded ? frontHit.normal : Vector3.zero) +
                            (rearGrounded ? rearHit.normal : Vector3.zero)).normalized;
            }

            // Opposite throttle brakes to rest, waits, then engages the requested direction.
            int requestedDirection = input.y > 0.01f ? 1 : input.y < -0.01f ? -1 : 0;
            bool changingDirection = requestedDirection != 0 &&
                (requestedDirection != driveDirection || speed * requestedDirection < -0.2f);
            if (changingDirection)
            {
                directionWait = absSpeed < 0.25f ? directionWait + dt : 0f;
                if (directionWait >= reverseDelay)
                {
                    driveDirection = requestedDirection;
                    directionWait = 0f;
                    changingDirection = false;
                }
            }
            else directionWait = 0f;
            float targetThrottle = !stop && !changingDirection ? input.y : 0f;
            float targetBrake = stop || changingDirection ? 1f : 0f;
            if (requestedDirection == 0 && absSpeed < 0.2f) targetBrake = 1f;
            throttle = Mathf.MoveTowards(throttle, targetThrottle, throttleResponse * dt);
            brake = Mathf.MoveTowards(brake, targetBrake, brakeResponse * dt);
            float limit = throttle >= 0f ? moveSpeed : reverseSpeed;
            float speedLimiter = Mathf.Clamp01((limit - speed * Mathf.Sign(throttle)) / 2f);
            float motorTorque = throttle * acceleration * rb.mass * rearCollider.radius * speedLimiter * (1f - brake);
            frontCollider.motorTorque = 0f;
            rearCollider.motorTorque = rearGrounded ? motorTorque : 0f;
            frontCollider.brakeTorque = brake * brakeDeceleration * rb.mass * frontCollider.radius * 0.6f;
            rearCollider.brakeTorque = brake * brakeDeceleration * rb.mass * rearCollider.radius * 0.4f;

            steering = Mathf.MoveTowards(steering, input.x, steeringResponse * dt);
            float moving = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(minSpeedToTurn, fullSteeringSpeed, absSpeed));
            float safeAngle = Mathf.Atan(maxLateralAcceleration * wheelbase / Mathf.Max(1f, speed * speed)) * Mathf.Rad2Deg;
            float yawLimitedAngle = Mathf.Atan(maxYawRate * wheelbase / Mathf.Max(0.1f, absSpeed)) * Mathf.Rad2Deg;
            float targetSteer = steering * moving * Mathf.Min(maxSteerAngle, Mathf.Min(safeAngle, yawLimitedAngle));
            steerAngle = Mathf.MoveTowards(steerAngle, targetSteer, steerAngleRate * dt);
            frontCollider.steerAngle = steerAngle;
            rearCollider.steerAngle = 0f;
            float expectedYaw = speed / wheelbase * Mathf.Tan(steerAngle * Mathf.Deg2Rad);
            float targetLean = Mathf.Clamp(Mathf.Atan(speed * expectedYaw / Physics.gravity.magnitude) *
                Mathf.Rad2Deg, -maxLeanAngle, maxLeanAngle);
            lean = Mathf.MoveTowards(lean, grounded ? targetLean : 0f, leanResponse * dt);

            if (grounded)
            {
                Vector3 forward = Vector3.ProjectOnPlane(Forward, groundUp).normalized;
                Vector3 right = Vector3.Cross(groundUp, forward).normalized;
                Vector3 desiredUp = Quaternion.AngleAxis(-lean, forward) * groundUp;
                Vector3 error = Vector3.Cross(transform.up, desiredUp);
                Vector3 angular = rb.angularVelocity;
                float rollTorque = Vector3.Dot(error, forward) * balanceStrength -
                    Vector3.Dot(angular, forward) * balanceDamping;
                float pitchTorque = Vector3.Dot(error, right) * pitchStrength -
                    Vector3.Dot(angular, right) * pitchDamping;
                // Dampen excess yaw only; tyres supply the actual steering force.
                float yaw = Vector3.Dot(angular, groundUp);
                float excessYaw = yaw - Mathf.Clamp(yaw, Mathf.Min(0f, expectedYaw), Mathf.Max(0f, expectedYaw));
                rb.AddTorque(forward * Mathf.Clamp(rollTorque, -120f, 120f) +
                    right * Mathf.Clamp(pitchTorque, -20f, 20f) -
                    groundUp * excessYaw * yawDamping, ForceMode.Acceleration);
                if (requestedDirection == 0 && !stop && absSpeed > 0.2f)
                    rb.AddForce(-Vector3.ProjectOnPlane(rb.linearVelocity, groundUp) * 0.15f, ForceMode.Acceleration);
            }
            if (IsSpawned && IsServer)
            {
                networkSteer.Value = steerAngle;
                networkSpeed.Value = speed;
            }
        }

        private void LateUpdate()
        {
            if (!ready) return;
            float visualSteer = Simulates ? steerAngle : networkSteer.Value;
            if (steeringVisual)
            {
                Vector3 parentUp = steeringVisual.parent.InverseTransformDirection(transform.up);
                steeringVisual.localRotation = Quaternion.AngleAxis(visualSteer, parentUp) * steeringRest;
            }
            UpdateWheelVisual(frontCollider, frontMesh, frontMeshOffset, visualSteer, ref frontSpin);
            UpdateWheelVisual(rearCollider, rearMesh, rearMeshOffset, 0f, ref rearSpin);
            if (IsSpawned && driverId.Value != ulong.MaxValue &&
                NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(driverId.Value, out var rider) && rider.IsOwner)
                rider.transform.SetPositionAndRotation(ridePoint.position, ridePoint.rotation);
        }

        private void UpdateWheelVisual(WheelCollider wheel, Transform mesh, Quaternion offset, float angle, ref float spin)
        {
            if (!mesh) return;
            if (Simulates)
            {
                wheel.GetWorldPose(out var position, out var rotation);
                mesh.SetPositionAndRotation(position, rotation * offset);
            }
            else
            {
                spin = (spin + networkSpeed.Value / Mathf.Max(0.05f, wheel.radius) * Mathf.Rad2Deg * Time.deltaTime) % 360f;
                Vector3 position = wheel.transform.TransformPoint(wheel.center - Vector3.up * wheel.suspensionDistance * 0.5f);
                Quaternion rotation = wheel.transform.rotation * Quaternion.Euler(0f, angle, 0f) *
                    Quaternion.Euler(spin, 0f, 0f);
                mesh.SetPositionAndRotation(position, rotation * offset);
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone, Delivery = RpcDelivery.Unreliable)]
        private void SubmitInputServerRpc(Vector2 input, bool braking, RpcParams rpcParams = default)
        {
            if (driverId.Value == ulong.MaxValue ||
                !NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(driverId.Value, out var rider) ||
                rider.OwnerClientId != rpcParams.Receive.SenderClientId) return;
            serverInput = Vector2.ClampMagnitude(input, 1.414214f);
            serverInput.x = Mathf.Clamp(serverInput.x, -1f, 1f);
            serverInput.y = Mathf.Clamp(serverInput.y, -1f, 1f);
            serverBrake = braking;
            lastInputTime = Time.unscaledTime;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestMountServerRpc(ulong playerObjectId, RpcParams rpcParams = default)
        {
            if (driverId.Value != ulong.MaxValue ||
                !NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(playerObjectId, out var player) ||
                player.OwnerClientId != rpcParams.Receive.SenderClientId ||
                Vector3.Distance(ridePoint.position, player.transform.position) > interactionRadius) return;
            var movement = player.GetComponent<PlayerMovement>();
            if (!movement || movement.IsMovementLocked) return;
            driverId.Value = playerObjectId;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestDismountServerRpc(ulong playerObjectId, RpcParams rpcParams = default)
        {
            if (driverId.Value != playerObjectId ||
                !NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(playerObjectId, out var player) ||
                player.OwnerClientId != rpcParams.Receive.SenderClientId) return;
            driverId.Value = ulong.MaxValue;
        }

        public bool CanInteract(PlayerMovement player)
        {
            if (!isActiveAndEnabled || !IsSpawned || !ridePoint || !player) return false;
            if (driverId.Value == player.NetworkObjectId) return true;
            return driverId.Value == ulong.MaxValue && !player.IsMovementLocked &&
                InteractionDistance(player) <= interactionRadius;
        }

        public float InteractionDistance(PlayerMovement player) =>
            driverId.Value == player.NetworkObjectId ? 0f : Vector3.Distance(ridePoint.position, player.transform.position);

        public string InteractionPrompt(PlayerMovement player) =>
            driverId.Value == player.NetworkObjectId ? "E to Dismount" : "E to Ride";

        public void Interact(PlayerMovement player)
        {
            if (!player.IsOwner) return;
            if (driverId.Value == player.NetworkObjectId) RequestDismountServerRpc(player.NetworkObjectId);
            else RequestMountServerRpc(player.NetworkObjectId);
        }
    }
}
