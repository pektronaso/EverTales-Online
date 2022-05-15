// Applies another renderer's animation to another sprite sheet.
// => The sprite names have to be exactly the same!
using System;
using System.Collections.Generic;
using UnityEngine;

public class SubAnimation : MonoBehaviour
{
    public SpriteRenderer sourceAnimation;
#pragma warning disable CS0109 // member does not hide accessible member
    public new SpriteRenderer renderer;
#pragma warning restore CS0109 // member does not hide accessible member
    public List<Sprite> spritesToAnimate;

    void LateUpdate()
    {
        renderer.sprite = spritesToAnimate != null
                          ? spritesToAnimate.Find(s => s.name == sourceAnimation.sprite.name)
                          : null;
    }
}
