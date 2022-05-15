// Sets the Rigidbody's velocity in Start().
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class DefaultVelocity : MonoBehaviour
{
    public Rigidbody2D rigidBody;
    public Vector2 velocity;

    void Start()
    {
        rigidBody.velocity = velocity;
    }
}
