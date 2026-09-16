using System.Collections.Generic;
using GamePlay.AnimationSystems;
using GamePlay.ComponentSystems;

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
        private readonly Stack<EnemyData> _enemyDataPool = new Stack<EnemyData>(64);
        private readonly Dictionary<EnemyUnit, EnemyData> _enemyLookup = new Dictionary<EnemyUnit, EnemyData>(64);
        public int EnemyCount => _enemies.Count;

        public bool AreAllActiveEnemiesDefeated
        {
            get
            {
                return _enemies.Count > 0 && _activeEnemyCount == 0;
            }
        }

        private int _activeEnemyCount;
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

            if (_enemyLookup.TryGetValue(causer, out EnemyData data))
            {
                if (!data.IsActive)
                {
                    data.IsActive = true;
                    _activeEnemyCount++;
                }
                causer.PlayAnimation(_isGameplayPaused ? AnimationType.Idle : AnimationType.Move);
                return;
            }

            data = _enemyDataPool.Count > 0 ? _enemyDataPool.Pop() : new EnemyData();
            data.Causer = causer;
            data.IsActive = true;

            _enemies.Add(data);
            _enemyLookup.Add(causer, data);
            _activeEnemyCount++;
            causer.PlayAnimation(_isGameplayPaused ? AnimationType.Idle : AnimationType.Move);
        }

        public void UnregisterEnemy(EnemyUnit causer)
        {
            if (causer != null && _enemyLookup.TryGetValue(causer, out EnemyData enemy) && enemy.IsActive)
            {
                enemy.IsActive = false;
                _activeEnemyCount = System.Math.Max(0, _activeEnemyCount - 1);
                _needsCleanup = true;
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

        public void ManualUpdate()
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
                enemy.Causer = null;
                _enemyDataPool.Push(enemy);
            }

            _enemies.Clear();
            _enemyLookup.Clear();
            _activeEnemyCount = 0;
            _needsCleanup = false;
            enabled = false;
        }

        private void CompactInactiveEnemies()
        {
            for (int i = _enemies.Count - 1; i >= 0; i--)
            {
                var enemy = _enemies[i];
                if (enemy != null && enemy.IsActive && enemy.Causer != null) continue;

                int last = _enemies.Count - 1;
                if (i != last)
                {
                    _enemies[i] = _enemies[last];
                }
                _enemies.RemoveAt(last);

                if (enemy != null)
                {
                    if (enemy.Causer != null)
                    {
                        _enemyLookup.Remove(enemy.Causer);
                    }
                    enemy.IsActive = false;
                    enemy.Causer = null;
                    _enemyDataPool.Push(enemy);
                }
            }
        }
    }
}
