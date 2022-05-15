// Quick slash in front of us that hits anything standing there.
//
// => Useful for hack & slash / action based combat skills/games without target.
//    (set one of the skillbar slots to SPACEBAR key for the ultimate effect)
using System.Text;
using UnityEngine;
using Mirror;

[CreateAssetMenu(menuName="uMMORPG Skill/Slash Damage", order=999)]
public class SlashDamageSkill : DamageSkill
{
    public override bool CheckTarget(Entity caster)
    {
        // no target necessary, but still set to self so that LookAt(target)
        // doesn't cause the player to look at a target that doesn't even matter
        caster.target = caster;
        return true;
    }

    public override bool CheckDistance(Entity caster, int skillLevel, out Vector2 destination)
    {
        // can cast anywhere
        destination = (Vector2)caster.transform.position + caster.lookDirection;
        return true;
    }

    public override void Apply(Entity caster, int skillLevel)
    {
        // cast a box or circle into look direction and try to attack anything
        // that is attackable
        float range = castRange.Get(skillLevel);
        Vector2 center = (Vector2)caster.transform.position + caster.lookDirection * range / 2;
        Vector2 size = new Vector2(range, range);
        Collider2D[] colliders = Physics2D.OverlapBoxAll(center, size, 0);
        foreach (Collider2D co in colliders)
        {
            Entity candidate = co.GetComponentInParent<Entity>();
            if (candidate != null && caster.CanAttack(candidate))
            {
                // deal damage directly with base damage + skill damage
                caster.DealDamageAt(candidate, caster.damage + damage.Get(skillLevel));
            }
        }
    }
}
