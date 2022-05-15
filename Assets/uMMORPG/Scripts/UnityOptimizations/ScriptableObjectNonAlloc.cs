using UnityEngine;

public abstract class ScriptableObjectNonAlloc : ScriptableObject
{
    // .name allocates and we call it a lot. let's cache it to avoid GC.
    // (4.1KB/frame for skillbar items before, 0KB now)
    string cachedName;
    public new string name
    {
        get
        {
            if (string.IsNullOrWhiteSpace(cachedName))
                cachedName = base.name;
            return cachedName;
        }
        // set: not needed, we don't change ScriptableObject names at runtime.
    }
}
