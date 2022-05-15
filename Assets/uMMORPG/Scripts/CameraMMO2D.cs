// Simple MMO camera that always follows the player.
using UnityEngine;

public class CameraMMO2D : MonoBehaviour
{
    [Header("Snap to Pixel Grid")]
    public PixelDensityCamera pixelDensity;
    public bool snapToGrid = true;

    [Header("Target Follow")]
    public Transform target;
    // the target position can be adjusted by an offset in order to foucs on a
    // target's head for example
    public Vector2 offset = Vector2.zero;

    // smooth the camera movement
    [Header("Dampening")]
    public float damp = 5;

    void LateUpdate()
    {
        if (!target) return;

        // calculate goal position
        Vector2 goal = (Vector2)target.position + offset;

        // interpolate
        Vector2 position = Vector2.Lerp(transform.position, goal, Time.deltaTime * damp);

        // snap to grid, so it's always in multiples of 1/16 for pixel perfect looks
        // and to prevent shaking effects of moving objects etc.
        if (snapToGrid)
        {
            float gridSize = pixelDensity.pixelsToUnits * pixelDensity.zoom;
            position.x = Mathf.Round(position.x * gridSize) / gridSize;
            position.y = Mathf.Round(position.y * gridSize) / gridSize;
        }

        // convert to 3D but keep Z to stay in front of 2D plane
        transform.position = new Vector3(position.x, position.y, transform.position.z);
    }
}
