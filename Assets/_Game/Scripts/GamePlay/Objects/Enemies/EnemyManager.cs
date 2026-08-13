using System.Collections.Generic;
using GamePlay.AnimationSystems;
using GamePlay.CombatSystems;
using GamePlay.ComponentSystems;
using GamePlay.Weapons;
using Pools;
using UnityEngine;

namespace GamePlay.Enemies
{
    // Lightweight runtime state per enemy.
    public class EnemyData
    {
        public EnemyUnit Causer;
        public bool IsActive;
    }

    public class EnemyManager : MonoSingleton<EnemyManager>
    {
        private readonly List<EnemyData> _enemies = new List<EnemyData>(64);
        public int EnemyCount => _enemies.Count;

        private EnemyData _currentEnemy;
        private bool _isGameplayPaused = true;
        private bool _needsCleanup;
        private readonly List<AttackComponent> _attackComponentsBuffer = new List<AttackComponent>(8);

        protected override void Awake()
        {
            base.Awake();
            enabled = false;
        }

        public void RegisterEnemy(EnemyUnit causer)
        {
            if (causer == null) return;

            for (int i = 0; i < _enemies.Count; i++)
            {
                if (_enemies[i] == null || _enemies[i].Causer != causer) continue;
                _enemies[i].IsActive = true;
                causer.PlayAnimation(_isGameplayPaused ? AnimationType.Idle : AnimationType.Move);
                return;
            }

            var data = new EnemyData
            {
                Causer = causer,
                IsActive = true
            };

            _enemies.Add(data);
            causer.PlayAnimation(_isGameplayPaused ? AnimationType.Idle : AnimationType.Move);
        }

        public void UnregisterEnemy(EnemyUnit causer)
        {
            for (int i = 0; i < _enemies.Count; i++)
            {
                var enemy = _enemies[i];
                if (enemy == null || enemy.Causer != causer) continue;

                enemy.IsActive = false;
                _needsCleanup = true;
                break;
            }

            if (!enabled) enabled = true;
        }

        public void SetAllEnemiesIdle()
        {
            if (_enemies == null || _enemies.Count == 0) return;
            _isGameplayPaused = true;

            for (int i = 0; i < _enemies.Count; i++)
            {
                var enemyData = _enemies[i];
                if (enemyData == null || !enemyData.IsActive) continue;

                var enemy = enemyData.Causer;
                if (enemy == null || !enemy.isActiveAndEnabled) continue;

                enemy.PlayAnimation(AnimationType.Idle);
            }
        }

        public void SyncGameplayState(bool gameplayStarted)
        {
            bool shouldPause = !gameplayStarted;
            if (_isGameplayPaused == shouldPause)
            {
                return;
            }

            _isGameplayPaused = shouldPause;
            var targetAnimation = _isGameplayPaused ? AnimationType.Idle : AnimationType.Move;

            for (int i = 0; i < _enemies.Count; i++)
            {
                var enemy = _enemies[i];
                if (enemy == null || !enemy.IsActive || enemy.Causer == null) continue;
                enemy.Causer.PlayAnimation(targetAnimation);
            }
        }

        private void Update()
        {
            if (!_needsCleanup)
            {
                enabled = false;
                return;
            }

            CompactInactiveEnemies();
            _needsCleanup = false;
            enabled = false;
        }

        public void UnregisterAllEnemies()
        {
            for (int i = 0; i < _enemies.Count; i++)
            {
                var enemy = _enemies[i];
                if (enemy == null) continue;
                enemy.IsActive = false;
            }

            _enemies.Clear();
            _needsCleanup = false;
            enabled = false;
        }

        private void CompactInactiveEnemies()
        {
            for (int i = _enemies.Count - 1; i >= 0; i--)
            {
                var enemy = _enemies[i];
                if (enemy != null && enemy.IsActive && enemy.Causer != null) continue;

                _enemies.RemoveAt(i);
            }
        }
    }
}
