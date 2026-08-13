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
        _currentLevelIndex++;
        
        // Cập nhật Asset cho PlayerArmy
        var armySystem = FindObjectOfType<PlayerArmySystem>();
        if (armySystem != null)
        {
            // Hàm này sẽ được định nghĩa trong PlayerArmySystem
            armySystem.ApplyLevelUpgrade(_currentLevelIndex);
        }
    }
}
