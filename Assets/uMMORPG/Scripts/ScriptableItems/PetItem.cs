﻿using System.Text;
using UnityEngine;
using Mirror;

[CreateAssetMenu(menuName="uMMORPG Item/Pet", order=999)]
public class PetItem : SummonableItem
{
    // usage
    public override bool CanUse(Player player, int inventoryIndex)
    {
        // summonable checks if we can summon it already,
        // we just need to check if we have no active pet summoned yet
        return base.CanUse(player, inventoryIndex) && player.activePet == null;
    }

    public override void Use(Player player, int inventoryIndex)
    {
        // always call base function too
        base.Use(player, inventoryIndex);

        // summon right next to the player
        ItemSlot slot = player.inventory[inventoryIndex];
        GameObject go = Instantiate(summonPrefab.gameObject, player.petDestination, Quaternion.identity);
        Pet pet = go.GetComponent<Pet>();
        pet.name = summonPrefab.name; // avoid "(Clone)"
        pet.owner = player;
        pet.level = slot.item.summonedLevel;
        pet.experience = slot.item.summonedExperience;
        // set health AFTER level, otherwise health is always level 1 max health
        pet.health = slot.item.summonedHealth;

        NetworkServer.Spawn(go);
        player.activePet = go.GetComponent<Pet>(); // set syncvar to go after spawning

        // set item summoned pet reference so we know it can't be sold etc.
        slot.item.summoned = go;
        player.inventory[inventoryIndex] = slot;
    }
}
