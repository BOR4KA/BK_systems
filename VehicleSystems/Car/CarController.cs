using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class VehicleController : MonoBehaviour
{
    [Header("Wheel Colliders (Physics Wheels)")]
    public WheelCollider wheelFL;
    public WheelCollider wheelFR;
    public WheelCollider wheelRL;
    public WheelCollider wheelRR;

    [Header("Wheel Meshes (Visual Wheels)")]
    public Transform meshFL;
    public Transform meshFR;
    public Transform meshRL;
    public Transform meshRR;

    [Header("Vehicle Settings")]
    public float motorTorque = 1500f;
    public float brakeForce = 3000f;
    public float maxSteerAngle = 25f;
    public float downForce = 100f;

    public bool isDriving = false;

    private float motorInput;
    private float steerInput;
    private float brakeInput;
    private Rigidbody rb;

    [Header("Steering Wheel Visual")]
    public Transform steeringWheel; 
    public float steeringWheelMaxAngle = 200f;  
    private float steeringWheelCurrentAngle = 0f;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = 1200f;
        rb.centerOfMass = new Vector3(0, -0.5f, 0);
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
    }

    void Update()
    {
        if (!isDriving)
        {
            StopVehicle();
            return;
        }

        motorInput = Input.GetAxis("Vertical");      // W ileri, S geri
        steerInput = Input.GetAxis("Horizontal");    // A sol, D sağ
        brakeInput = Input.GetKey(KeyCode.Space) ? 1f : 0f;

        UpdateSteeringWheel();
    }

    void FixedUpdate()
    {
        if (!isDriving) return;

        ApplyMotor();
        ApplySteering();
        ApplyBraking();
        ApplyDownForce();
        UpdateWheelVisuals();
    }

    void ApplyMotor()
    {
        float torque = -motorInput * motorTorque;  // W ileri, S geri
        wheelRL.motorTorque = torque;
        wheelRR.motorTorque = torque;
    }

    void ApplySteering()
    {
        float steer = steerInput * maxSteerAngle;
        wheelFL.steerAngle = steer;
        wheelFR.steerAngle = steer;
    }

    void ApplyBraking()
    {
        float brake = brakeInput * brakeForce;
        wheelFL.brakeTorque = brake;
        wheelFR.brakeTorque = brake;
        wheelRL.brakeTorque = brake;
        wheelRR.brakeTorque = brake;
    }

    void ApplyDownForce()
    {
        rb.AddForce(-transform.up * downForce * rb.linearVelocity.magnitude);
    }

    void UpdateWheelVisuals()
    {
        UpdateWheelPose(wheelFL, meshFL);
        UpdateWheelPose(wheelFR, meshFR);
        UpdateWheelPose(wheelRL, meshRL);
        UpdateWheelPose(wheelRR, meshRR);
    }

    void UpdateWheelPose(WheelCollider col, Transform mesh)
    {
        Vector3 pos;
        Quaternion rot;
        col.GetWorldPose(out pos, out rot);
        mesh.position = pos;
        mesh.rotation = rot;
    }

    
    void UpdateSteeringWheel()
    {
        if (steeringWheel == null) return;

        float targetAngle = steerInput * steeringWheelMaxAngle;
        steeringWheelCurrentAngle = Mathf.Lerp(steeringWheelCurrentAngle, targetAngle, Time.deltaTime * 10f);

        steeringWheel.localRotation = Quaternion.Euler(0f, steeringWheelCurrentAngle, 0f);
    }

    void StopVehicle()
    {
        wheelFL.motorTorque = 0;
        wheelFR.motorTorque = 0;
        wheelRL.motorTorque = 0;
        wheelRR.motorTorque = 0;
    }
}
