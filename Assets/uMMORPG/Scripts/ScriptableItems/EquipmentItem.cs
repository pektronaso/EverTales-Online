﻿using System.Text;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName="uMMORPG Item/Equipment", order=999)]
public class EquipmentItem : UsableItem
{
    [Header("Equipment")]
    public string category;
    public int healthBonus;
    public int manaBonus;
    public int damageBonus;
    public int defenseBonus;
    [Range(0, 1)] public float blockChanceBonus;
    [Range(0, 1)] public float criticalChanceBonus;

    // animated equipment sprites. list instead of array so that we can pass it
    // around without copying the whole thing each time
    public List<Sprite> sprites = new List<Sprite>();

    // usage
    // -> can we equip this into any slot?
    public override bool CanUse(Player player, int inventoryIndex)
    {
        return FindEquipableSlotFor(player, inventoryIndex) != -1;
    }

    // can we equip this item into this specific equipment slot?
    public bool CanEquip(Player player, int inventoryIndex, int equipmentIndex)
    {
        string requiredCategory = player.equipmentInfo[equipmentIndex].requiredCategory;
        return base.CanUse(player, inventoryIndex) &&
               requiredCategory != "" &&
               category.StartsWith(requiredCategory);
    }

    int FindEquipableSlotFor(Player player, int inventoryIndex)
    {
        for (int i = 0; i < player.equipment.Count; ++i)
            if (CanEquip(player, inventoryIndex, i))
                return i;
        return -1;
    }

    public override void Use(Player player, int inventoryIndex)
    {
        // always call base function too
        base.Use(player, inventoryIndex);

        // find a slot that accepts this category, then equip it
        int slot = FindEquipableSlotFor(player, inventoryIndex);
        if (slot != -1)
        {
            // reuse Player.SwapInventoryEquip function for validation etc.
            player.SwapInventoryEquip(inventoryIndex, slot);
        }
    }

    // tooltip
    public override string ToolTip()
    {
        StringBuilder tip = new StringBuilder(base.ToolTip());
        tip.Replace("{CATEGORY}", category);
        tip.Replace("{DAMAGEBONUS}", damageBonus.ToString());
        tip.Replace("{DEFENSEBONUS}", defenseBonus.ToString());
        tip.Replace("{HEALTHBONUS}", healthBonus.ToString());
        tip.Replace("{MANABONUS}", manaBonus.ToString());
        tip.Replace("{BLOCKCHANCEBONUS}", Mathf.RoundToInt(blockChanceBonus * 100).ToString());
        tip.Replace("{CRITICALCHANCEBONUS}", Mathf.RoundToInt(criticalChanceBonus * 100).ToString());
        return tip.ToString();
    }
}
