// Simple script that inherits from NetworkStartPosition to make class based
// spawns.
using UnityEngine;
using Mirror;

public class NetworkStartPositionForClass : NetworkStartPosition
{
    public Player playerPrefab;
}
