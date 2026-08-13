using UnityEngine;

public sealed class GameplayEnvironmentObject : MonoBehaviour
{
    #region Fields

    [SerializeField] private string id;

    public string Id => id;

    #endregion
}
