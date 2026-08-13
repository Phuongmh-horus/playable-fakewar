using UnityEngine;
using System;

namespace GamePlay.ComponentSystems
{
    public class MovementComponent : BaseComponent, IMover
    {
        private static readonly Action NoMovementComplete = () => { };

        public event Action OnMovementComplete = NoMovementComplete;

        [Header("Config")]
        [SerializeField] private float maxDistance = 30f;
        [SerializeField] private float duration = 1.2f;

        // --- IMoveable Implementation ---
        public Vector3 MoveDirection => CacheTransform.forward;
        public float MaxDistance => maxDistance;
        public float Duration => duration;

        public override void Initialize()
        {
            base.Initialize();
            OnMovementComplete = NoMovementComplete;
        }

        public void OnMovementFinished()
        {
            OnMovementComplete?.Invoke();
        }

    }
}
