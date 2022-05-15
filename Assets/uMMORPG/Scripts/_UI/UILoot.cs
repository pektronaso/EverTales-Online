﻿// Note: this script has to be on an always-active UI parent, so that we can
// always find it from other code. (GameObject.Find doesn't find inactive ones)
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public partial class UILoot : MonoBehaviour
{
    public static UILoot singleton;
    public GameObject panel;
    public GameObject goldSlot;
    public Text goldText;
    public UILootSlot itemSlotPrefab;
    public Transform content;

    public UILoot()
    {
        // assign singleton only once (to work with DontDestroyOnLoad when
        // using Zones / switching scenes)
        if (singleton == null) singleton = this;
    }

    void Update()
    {
        Player player = Player.localPlayer;

        // use collider point(s) to also work with big entities
        if (player != null &&
            panel.activeSelf &&
            player.target != null &&
            player.target.health == 0 &&
            Utils.ClosestDistance(player.collider, player.target.collider) <= player.interactionRange &&
            player.target is Monster &&
            ((Monster)player.target).HasLoot())
        {
            // gold slot
            if (player.target.gold > 0)
            {
                goldSlot.SetActive(true);
                goldSlot.GetComponentInChildren<Button>().onClick.SetListener(() => {
                    player.CmdTakeLootGold();
                });
                goldText.text = player.target.gold.ToString();
            }
            else goldSlot.SetActive(false);

            // instantiate/destroy enough slots
            // (we only want to show the non-empty slots)
            List<ItemSlot> items = player.target.inventory.Where(slot => slot.amount > 0).ToList();
            UIUtils.BalancePrefabs(itemSlotPrefab.gameObject, items.Count, content);

            // refresh all valid items
            for (int i = 0; i < items.Count; ++i)
            {
                UILootSlot slot = content.GetChild(i).GetComponent<UILootSlot>();
                slot.dragAndDropable.name = i.ToString(); // drag and drop index
                int itemIndex = player.target.inventory.FindIndex(
                    // note: .Equals because name AND dynamic variables matter (petLevel etc.)
                    itemSlot => itemSlot.amount > 0 && itemSlot.item.Equals(items[i].item)
                );

                // refresh
                slot.button.interactable = player.InventoryCanAdd(items[i].item, items[i].amount);
                slot.button.onClick.SetListener(() => {
                    player.CmdTakeLootItem(itemIndex);
                });
                // only build tooltip while it's actually shown. this
                // avoids MASSIVE amounts of StringBuilder allocations.
                slot.tooltip.enabled = true;
                if (slot.tooltip.IsVisible())
                    slot.tooltip.text = items[i].ToolTip();
                slot.image.color = Color.white;
                slot.image.sprite = items[i].item.image;
                slot.nameText.text = items[i].item.name;
                slot.amountOverlay.SetActive(items[i].amount > 1);
                slot.amountText.text = items[i].amount.ToString();
            }
        }
        else panel.SetActive(false);
    }

    public void Show() { panel.SetActive(true); }
}
