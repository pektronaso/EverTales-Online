// The Npc class is rather simple. It contains state Update functions that do
// nothing at the moment, because Npcs are supposed to stand around all day.
//
// Npcs first show the welcome text and then have options for item trading and
// quests.
using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

// talk-to-npc quests work by adding the same quest to two npcs, one with
// accept=true and complete=false, the other with accept=false and complete=true
[Serializable]
public class ScriptableQuestOffer
{
    public ScriptableQuest quest;
    public bool acceptHere = true;
    public bool completeHere = true;
}

[RequireComponent(typeof(NetworkNavMeshAgent2D))]
public partial class Npc : Entity
{
    [Header("Text Meshes")]
    public TextMesh questOverlay;

    [Header("Welcome Text")]
    [TextArea(1, 30)] public string welcome;

    [Header("Items for Sale")]
    public ScriptableItem[] saleItems;

    [Header("Quests")]
    public ScriptableQuestOffer[] quests;

    [Header("Teleportation")]
    public Transform teleportTo;

    [Header("Guild Management")]
    public bool offersGuildManagement = true;

    [Header("Summonables")]
    public bool offersSummonableRevive = true;

    // networkbehaviour ////////////////////////////////////////////////////////
    public override void OnStartServer()
    {
        base.OnStartServer();

        // all npcs should spawn with full health and mana
        health = healthMax;
        mana = manaMax;

        // addon system hooks
        Utils.InvokeMany(GetType(), this, "OnStartServer_");
    }

    // finite state machine states /////////////////////////////////////////////
    [Server] protected override string UpdateServer() { return state; }
    [Client] protected override void UpdateClient()
    {
        // addon system hooks
        Utils.InvokeMany(GetType(), this, "UpdateClient_");
    }

    // overlays ////////////////////////////////////////////////////////////////
    public bool CanPlayerCompleteAnyQuestHere(Player player)
    {
        // check manually. Linq.Any() is HEAVY(!) on GC and performance
        foreach (ScriptableQuestOffer entry in quests)
            if (entry.completeHere && player.CanCompleteQuest(entry.quest.name))
                return true;
        return false;
    }

    public bool CanPlayerAcceptAnyQuestHere(Player player)
    {
        // check manually. Linq.Any() is HEAVY(!) on GC and performance
        foreach (ScriptableQuestOffer entry in quests)
            if (entry.acceptHere && player.CanAcceptQuest(entry.quest))
                return true;
        return false;
    }

    protected override void UpdateOverlays()
    {
        base.UpdateOverlays();

        if (questOverlay != null)
        {
            // find local player (null while in character selection)
            if (Player.localPlayer != null)
            {
                if (CanPlayerCompleteAnyQuestHere(Player.localPlayer))
                    questOverlay.text = "!";
                else if (CanPlayerAcceptAnyQuestHere(Player.localPlayer))
                    questOverlay.text = "?";
                else
                    questOverlay.text = "";
            }
        }
    }

    // skills //////////////////////////////////////////////////////////////////
    public override bool CanAttack(Entity entity) { return false; }

    // quests //////////////////////////////////////////////////////////////////
    // helper function to filter the quests that are shown for a player
    // -> all quests that:
    //    - can be started by the player
    //    - or were already started but aren't completed yet
    public List<ScriptableQuest> QuestsVisibleFor(Player player)
    {
        // search manually. Linq is HEAVY(!) on GC and performance
        List<ScriptableQuest> visibleQuests = new List<ScriptableQuest>();
        foreach (ScriptableQuestOffer entry in quests)
            if (entry.acceptHere && player.CanAcceptQuest(entry.quest) ||
                entry.completeHere && player.HasActiveQuest(entry.quest.name))
                visibleQuests.Add(entry.quest);
        return visibleQuests;
    }
}
