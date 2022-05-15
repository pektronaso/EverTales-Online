using UnityEngine;
using Mirror;

public abstract class NetworkBehaviourNonAlloc : NetworkBehaviour
{
    // .name allocates and we call it a lot. let's cache it to avoid GC.
    // (the more players/monsters, the more .name calls. this matters.)
    string cachedName;
    public new string name
    {
        get
        {
            if (string.IsNullOrWhiteSpace(cachedName))
                cachedName = base.name;
            return cachedName;
        }
        set
        {
            cachedName = base.name = value;
        }
    }
}
