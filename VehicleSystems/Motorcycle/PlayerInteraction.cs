using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Multiplayer
{
    public interface IPlayerInteractable
    {
        bool CanInteract(PlayerMovement player);
        float InteractionDistance(PlayerMovement player);
        string InteractionPrompt(PlayerMovement player);
        void Interact(PlayerMovement player);
    }

    [RequireComponent(typeof(PlayerMovement))]
    public class PlayerInteraction : NetworkBehaviour
    {
        private static readonly HashSet<IPlayerInteractable> Targets = new HashSet<IPlayerInteractable>();
        private PlayerMovement movement;
        public IPlayerInteractable CurrentTarget { get; private set; }
        public string CurrentPrompt { get; private set; } = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetTargets() => Targets.Clear();
        public static void Register(IPlayerInteractable target) => Targets.Add(target);
        public static void Unregister(IPlayerInteractable target) => Targets.Remove(target);
        private void Awake() => movement = GetComponent<PlayerMovement>();

        private void Update()
        {
            CurrentTarget = null;
            CurrentPrompt = "";
            if (!IsSpawned || !IsOwner) return;
            float nearest = float.PositiveInfinity;
            foreach (var target in Targets)
            {
                if ((target as Object) == null || !target.CanInteract(movement)) continue;
                float distance = target.InteractionDistance(movement);
                if (distance >= nearest) continue;
                nearest = distance;
                CurrentTarget = target;
            }
            if (CurrentTarget == null) return;
            CurrentPrompt = CurrentTarget.InteractionPrompt(movement);
            if (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame)
                CurrentTarget.Interact(movement); 
        }

        private void OnGUI()
        {
            if (!IsSpawned || !IsOwner || string.IsNullOrEmpty(CurrentPrompt)) return;
            Rect promptRect = new Rect(Screen.width / 2f - 100f, Screen.height / 2f + 50f, 240f, 30f);
            if (movement.IsSeated && CurrentTarget == movement.ActiveSeat)
            {
                const float margin = 20f;
                Vector2 size = GUI.skin.label.CalcSize(new GUIContent(CurrentPrompt));
                promptRect = new Rect(
                    Mathf.Max(0f, Screen.width - size.x - margin),
                    Mathf.Max(0f, Screen.height - size.y - margin),
                    size.x, size.y);
            }
            GUI.Label(promptRect, CurrentPrompt);
        }
    }
}
