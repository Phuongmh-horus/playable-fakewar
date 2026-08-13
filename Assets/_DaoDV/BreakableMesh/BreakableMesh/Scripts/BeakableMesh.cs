using System;
using UnityEngine;
using UnityEngine.Events;

public class BeakableMesh : MonoBehaviour
{
    [Header("Phạm Vi Vụ nổ")] 
    public float radius = 25f;

    [Header("Sức Mạnh vụ nổ (Càng to bay càng xa)")]
    public float power = 500.0F;

    [Header("Trọng lực (Càng lớn vật rơi càng nhanh)")]
    [Range(-50f,-5f)]
    public float gravityforce = -20;

    [Header("Thời gian tối đa sử dụng vật lý")]
    public float physicTime = 6;

    [Tooltip("Tắt Obj khi hết thời gian hiệu lực"), SerializeField]
    public bool disableObjAffterStopPhysic =true;
    public bool isAirplane = false;
    public bool isDisableCollider = false;
    [Tooltip("Thời gian tắt Box khi là máy bay")]
    public float timedisableBoxesAirplane = 0.2f;
    public bool isCheckCollision = true;
    public Transform centerOfExplosion;
    public BoxCollider[] key;
    public Rigidbody[] physic;
    
    [Tooltip("Tắt gravity Rb khi tắt collider"), SerializeField]
    private bool _isDisableGravityWhenDisableCollider = false;
    
    [Tooltip("Không sleep physic khi velocity nhỏ"), SerializeField]
    public bool _isNotSleepWhenLowVelocity = false;

    public UnityEvent StartEvent;
    public UnityEvent DisableEvent;
    [HideInInspector] public Transform[] physicstrans;
    [HideInInspector] public Vector3[] previousLocation ;
    [HideInInspector] public Quaternion[] previousRotation ;
    [HideInInspector] public Transform parentTrans;
    protected float time;
    protected Transform myTrans;
    protected Action ExplosiveAction;

    private void Awake()
    {
        parentTrans = transform.parent;
        myTrans = transform;

    }

    protected virtual void OnEnable()
    {
        ResetPosition();

        time = 0;

        if (isAirplane)
        {
            transform.parent = null;
        }
        if (centerOfExplosion==null)
        {
            GameObject gameObject = new GameObject(name = "CenterOfExplosion");
            gameObject.transform.SetParent(myTrans);
            gameObject.transform.position = myTrans.position;
        }
        for (int i = 0; i < physic.Length; i++)
        {
            if (key[i])
            {
                key[i].gameObject.SetActive(true);
                key[i].enabled = true;
            }
            if (physic[i])
            {
                physic[i].isKinematic = false;
                physic[i].detectCollisions = isCheckCollision;
                physic[i].useGravity = true;
            }
        }

        foreach (Rigidbody rb in physic)
        {
            if (rb != null)
                rb.AddExplosionForce(power, centerOfExplosion.position, radius);
        }

        if (isAirplane)
        {
            Invoke(nameof(DisableBoxes), timedisableBoxesAirplane);
        }
        StartEvent?.Invoke();
        ExplosiveAction += Explosive;
    }

    protected void DisableBoxes()
    {
        for (int i = 0; i < key.Length; i++)
        {
            if(key[i])  key[i].enabled = false;
            if (physic[i]) 
            {
                physic[i].detectCollisions = false;
            }

            if (_isDisableGravityWhenDisableCollider)
            {
                physic[i].isKinematic = true;
            }
        }
    }

    protected virtual void FixedUpdate()
    {
        ExplosiveAction?.Invoke();
    }

    protected void Explosive()
    {
        time += Time.fixedDeltaTime;
        if (time >= physicTime)
        {
            time = -10000;

            for (int i = 0; i < physic.Length; i++)
            {
                if (key[i] == null || physic[i] == null)
                {
                    continue;
                }
                if (disableObjAffterStopPhysic)
                {
                    physic[i].gameObject.SetActive(false);
                }
                physic[i].useGravity = false;
                physic[i].detectCollisions = false;
                physic[i].Sleep();
                key[i].enabled = false;
            }

            if (isAirplane)
            {
                transform.parent = parentTrans;
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
            }
            ExplosiveAction -= Explosive;
        }
        else
        {
            if (time > 0.1f)
            {
                for (int i = 0; i < physic.Length; i++)
                {
                    if (key[i] == null || physic[i] == null)
                        continue;
                    
                    if (physic[i].IsSleeping())
                        continue;

                    if (isAirplane || isDisableCollider)
                    {
                        physic[i].AddForce(new Vector3(0, gravityforce, 0) * physic[i].mass);
                    }
                    else
                    {
                        var yOffset = physic[i].transform.position.y - myTrans.position.y;
                        if (yOffset > 0)
                        {
                            physic[i].AddForce(new Vector3(0, gravityforce, 0) * physic[i].mass);
                        }
                        Debug.LogWarning(physic[i].angularVelocity.sqrMagnitude);

                        if(_isNotSleepWhenLowVelocity) continue;
                        
                        if (physic[i].angularVelocity.sqrMagnitude < 0.025f && yOffset < 0.5f || yOffset < -0.15f || physic[i].angularVelocity.sqrMagnitude < 0.005f)
                        {
                            physic[i].velocity = Vector3.zero;
                            physic[i].Sleep();
                        }
                    }
                    
                }
            }
        }
    }

    private void OnDisable()
    {
        ResetPosition();
        ResetPhysic();
        CancelInvoke();
        DisableEvent?.Invoke();
        ExplosiveAction = null;
    }

    public virtual void ResetPosition()
    {
        for (int i = 0; i < physicstrans.Length; i++)
        {
            if (physicstrans[i])
            {
                physicstrans[i].localPosition = previousLocation[i];
                physicstrans[i].localRotation = previousRotation[i];
            }
        }
    }

    public void ResetPhysic()
    {
        for (int i = 0; i < physic.Length; i++)
        {
            if (physic[i])
            {
                physic[i].Sleep();
            }
        }
    }

    public virtual void SaveRandomValue()
    {
    }

    public virtual void LoadRandomValue()
    {
    }

#if UNITY_EDIOR
    private void OnDrawGizmos()
    {
        if(centerOfExplosion != null)
        {
            Gizmos.DrawWireSphere(centerOfExplosion.position, radius);
        }
    }
#endif

}
