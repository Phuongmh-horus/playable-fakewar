using System;
using UnityEngine;
using PlayerArmy;

public class ArmyUpgradeManager : MonoBehaviour
{
    public static ArmyUpgradeManager Instance { get; private set; }

    [SerializeField] private CharacterVisualConfigSO characterVisualConfig;

    [SerializeField] private int startLevelIndex = 0;

    private int _currentLevelIndex;
    public int CurrentLevel => _currentLevelIndex;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        _currentLevelIndex = startLevelIndex;
    }

    public void UpgradeLevel()
    {
        SetLevel(_currentLevelIndex + 1);
    }

    public void SetLevel(int levelIndex)
    {
        int targetLevel = Mathf.Max(_currentLevelIndex, levelIndex);
        if (targetLevel == _currentLevelIndex)
        {
            return;
        }

        _currentLevelIndex = targetLevel;

        var armySystem = FindObjectOfType<PlayerArmySystem>();
        if (armySystem != null)
        {
            armySystem.ApplyLevelUpgrade(_currentLevelIndex);
        }
    }
}
