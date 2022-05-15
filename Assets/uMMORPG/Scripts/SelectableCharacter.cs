// small helper script that is added to character selection previews at runtime
using UnityEngine;
using Mirror;

public class SelectableCharacter : MonoBehaviour
{
    // index will be set by networkmanager when creating this script
    public int index = -1;

    void OnMouseDown()
    {
        // set selection index
        ((NetworkManagerMMO)NetworkManager.singleton).selection = index;
    }

    void Update()
    {
        // selected?
        bool selected = ((NetworkManagerMMO)NetworkManager.singleton).selection != index;

        // set name overlay font style as indicator
        Player player = GetComponent<Player>();
        player.nameOverlay.fontStyle = selected ? FontStyle.Normal : FontStyle.Bold;
    }
}
