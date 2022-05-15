using UnityEngine;
using Mirror;

public class OneTimeTargetSkillEffect : SkillEffect
{
    public float time = 1;
    float endTime;

    void Start() { endTime = Time.time + time; }

    void Update()
    {
        // follow the target's position (because we can't make a NetworkIdentity
        // a child of another NetworkIdentity)
        if (target != null)
            transform.position = target.collider.bounds.center;

        // destroy self if target disappeared or time elapsed
        if (isServer)
            if (target == null || Time.time > endTime)
                NetworkServer.Destroy(gameObject);
    }
}
