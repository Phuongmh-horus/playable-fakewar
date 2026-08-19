using System.Collections.Generic;
using GamePlay.CombatSystems;
using GamePlay.ComponentSystems;
using GamePlay.Entities;
using UnityEngine;

namespace GamePlay.Weapons
{
    public class WeaponUnit : PoolEntity
    {
        // [Header("Components References (MonoBehaviours implementing IComponent)")]
        // [SerializeField] private List<MonoBehaviour> components = new List<MonoBehaviour>();
        [SerializeField] private Transform childrenRoot;

        [Header("Models")]
        [SerializeField] private GameObject[] visualModels;
        private int _appliedVisualLevel = -1;

        [Header("childrenRoot Transform Cache")]
        [SerializeField] private Vector3 renderPosition;
        [SerializeField] private Vector3 renderEulerAngle;

        protected override void Awake()
        {
            base.Awake();

            if (_entityType == EntityType.None)
            {
                _entityType = EntityType.EnemyWeapon;
            }

            BuildCapabilityPack();
        }


        public void Initialize()
        {
            if (_entityType == EntityType.None)
            {
                _entityType = EntityType.EnemyWeapon;
            }

            // Runtime safety: ensure pack is valid even if inspector list was empty.
            if (Pack.Mover == null || Pack.Attacker == null)
            {
                BuildCapabilityPack();
            }

            if ((ActiveFlags & CapabilityFlags.Attack) != 0 && Pack.Attacker != null)
            {
                Pack.Attacker.Initialize();

                // [FIX] Force Weapon to target Enemies defaults (Character + Wheel)
                // This ensures it hits the Wheel even if serialization is wrong
                if (Pack.Attacker is AttackComponent attackComp)
                {
                    attackComp.SetTargetPreset(AttackComponent.AttackTargetPreset.Enemy);
                }
            }
        }


        public void SetFly()
        {
            if (childrenRoot == null)
            {
                return;
            }

            childrenRoot.localPosition = renderPosition;
            childrenRoot.localRotation = Quaternion.Euler(renderEulerAngle);
        }

        [ContextMenu("Set Default")]
        public void SetDefault()
        {
            if (childrenRoot == null)
            {
                return;
            }

            childrenRoot.localPosition = Vector3.zero;
            childrenRoot.localRotation = Quaternion.identity;
        }

        public bool Launch(Vector3 startPoint, Vector3 direction, float distance, float duration, float arcHeight, float rotationSpeed, int damage, EnemyProjectileSystem.ProjectileSpinAxis spinAxis = EnemyProjectileSystem.ProjectileSpinAxis.X, EnemyProjectileSystem.ProjectileMotionMode motionMode = EnemyProjectileSystem.ProjectileMotionMode.Arc, IAttacker thrower = null, bool alignRotationToDirection = true)
        {
            Initialize();

            if (Pack.Attacker == null)
            {
                return false;
            }

            Pack.Attacker.Setup(damage);
            if (Pack.Attacker is AttackComponent attackComp)
            {
                attackComp.SetTargetPreset(AttackComponent.AttackTargetPreset.PlayerProjectile);
            }

            Vector3 launchDirection = direction;
            if (motionMode == EnemyProjectileSystem.ProjectileMotionMode.Arc)
            {
                launchDirection.y = 0f;
            }

            if (launchDirection.sqrMagnitude < 0.0001f)
            {
                launchDirection = Vector3.forward;
            }

            launchDirection.Normalize();
            if (alignRotationToDirection)
            {
                transform.SetPositionAndRotation(startPoint, Quaternion.LookRotation(launchDirection));
            }
            else
            {
                transform.position = startPoint;
            }

            EnemyProjectileSystem.RegisterProjectile(
                transform,
                startPoint,
                startPoint.y,
                launchDirection,
                Mathf.Max(0.1f, distance),
                Mathf.Max(0.01f, duration),
                Mathf.Max(0f, arcHeight),
                rotationSpeed,
                Pack.Attacker,
                Pack.Mover,
                thrower,
                spinAxis,
                motionMode,
                alignRotationToDirection);

            return true;
        }

        public void Dispose()
        {
            DespawnInterval();
        }

        private void DespawnInterval()
        {
            Despawn();
        }

        public void ApplyVisualLevel(int levelIndex)
        {
            if (visualModels == null || visualModels.Length == 0) return;

            // Limit level index to avoid out of bounds
            int safeIndex = Mathf.Clamp(levelIndex, 0, visualModels.Length - 1);
            if (_appliedVisualLevel == safeIndex)
            {
                return;
            }

            _appliedVisualLevel = safeIndex;

            for (int i = 0; i < visualModels.Length; i++)
            {
                if (visualModels[i] != null)
                {
                    visualModels[i].SetActive(i == safeIndex);
                }
            }
        }
    }
}


